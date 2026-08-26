-- Predeployment inventory for published REST service tasks that touch a shared
-- alias without asyncBefore. Run against the Flowbit PostgreSQL database before
-- enabling the production shared-service validation. The first result must be
-- empty. The second result is a conservative multi-key lock-order inventory;
-- each row must be republished through the current validator or drained.
--
-- This deliberately mirrors SharedVariableAccessPlanner. Validation-expression
-- matching is conservative because PostgreSQL does not parse the NCalc AST: an
-- alias mentioned in a validation string is reported even when it occurs in a
-- string literal. False positives must be reviewed; false negatives are unsafe.

WITH published_definitions AS (
    SELECT
        definition."Id" AS workflow_definition_id,
        definition."WorkflowKey" AS workflow_key,
        definition."Version" AS workflow_version,
        definition."Name" AS workflow_name,
        definition."Definition" AS model
    FROM flowbit.workflow_definitions AS definition
    WHERE definition."IsPublished"
),
shared_aliases AS (
    SELECT
        definition.workflow_definition_id,
        btrim(variable ->> 'name') AS alias,
        variable
    FROM published_definitions AS definition
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(definition.model -> 'variables', '[]'::jsonb)) AS variable
    WHERE variable ->> 'scope' = 'shared'
      AND NULLIF(btrim(variable ->> 'name'), '') IS NOT NULL
),
unsafe_nodes AS (
    SELECT
        definition.workflow_definition_id,
        definition.workflow_key,
        definition.workflow_version,
        definition.workflow_name,
        definition.model,
        node,
        node -> 'service' AS service
    FROM published_definitions AS definition
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(definition.model -> 'flowNodes', '[]'::jsonb)) AS node
    WHERE node ->> 'type' = 'serviceTask'
      AND COALESCE(NULLIF(node -> 'service' ->> 'type', ''), 'rest') = 'rest'
      AND COALESCE((node ->> 'asyncBefore')::boolean, false) = false
),
output_targets AS (
    SELECT
        node.workflow_definition_id,
        node.node,
        mapping ->> 'variable' AS alias
    FROM unsafe_nodes AS node
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(node.service -> 'outputMappings', '[]'::jsonb)) AS mapping
    WHERE NULLIF(btrim(mapping ->> 'variable'), '') IS NOT NULL

    UNION ALL

    SELECT
        node.workflow_definition_id,
        node.node,
        node.service ->> 'statusVariable'
    FROM unsafe_nodes AS node
    WHERE NULLIF(btrim(node.service ->> 'statusVariable'), '') IS NOT NULL
),
template_values AS (
    SELECT
        node.workflow_definition_id,
        node.node,
        value.template,
        value.touch_kind
    FROM unsafe_nodes AS node
    CROSS JOIN LATERAL (
        VALUES
            (node.service ->> 'url', 'input-template'::text),
            (node.service ->> 'body', 'input-template'::text)
    ) AS value(template, touch_kind)
    WHERE value.template IS NOT NULL

    UNION ALL

    SELECT
        node.workflow_definition_id,
        node.node,
        header ->> 'value',
        'input-template'
    FROM unsafe_nodes AS node
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(node.service -> 'headers', '[]'::jsonb)) AS header
    WHERE header ->> 'value' IS NOT NULL

    UNION ALL

    -- Runtime substitutes placeholders only in a scalar string default or in
    -- string elements of an array default.
    SELECT
        node.workflow_definition_id,
        node.node,
        mapping -> 'defaultValue' #>> '{}',
        'output-default'
    FROM unsafe_nodes AS node
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(node.service -> 'outputMappings', '[]'::jsonb)) AS mapping
    WHERE jsonb_typeof(mapping -> 'defaultValue') = 'string'

    UNION ALL

    SELECT
        node.workflow_definition_id,
        node.node,
        element #>> '{}',
        'output-default'
    FROM unsafe_nodes AS node
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(node.service -> 'outputMappings', '[]'::jsonb)) AS mapping
    CROSS JOIN LATERAL jsonb_array_elements(
        CASE
            WHEN jsonb_typeof(mapping -> 'defaultValue') = 'array'
                THEN mapping -> 'defaultValue'
            ELSE '[]'::jsonb
        END) AS element
    WHERE jsonb_typeof(element) = 'string'
),
template_touches AS (
    SELECT
        value.workflow_definition_id,
        value.node,
        btrim((match.placeholder)[1]) AS alias,
        value.touch_kind
    FROM template_values AS value
    CROSS JOIN LATERAL regexp_matches(
        value.template,
        '\$\{\s*([^}\s]+)\s*\}',
        'g') AS match(placeholder)
),
validation_values AS (
    SELECT
        node.workflow_definition_id,
        node.node,
        mapping ->> 'validation' AS expression,
        'mapping-validation'::text AS touch_kind
    FROM unsafe_nodes AS node
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(node.service -> 'outputMappings', '[]'::jsonb)) AS mapping
    WHERE NULLIF(btrim(mapping ->> 'validation'), '') IS NOT NULL

    UNION ALL

    -- A mapping/status target also runs its declared top-level process/shared
    -- variable validation against the service output overlay.
    SELECT
        target.workflow_definition_id,
        target.node,
        variable ->> 'validation',
        'target-validation'
    FROM output_targets AS target
    JOIN unsafe_nodes AS node
      ON node.workflow_definition_id = target.workflow_definition_id
     AND node.node = target.node
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(node.model -> 'variables', '[]'::jsonb)) AS variable
    WHERE lower(btrim(variable ->> 'name')) = lower(btrim(target.alias))
      AND NULLIF(btrim(variable ->> 'validation'), '') IS NOT NULL

    UNION ALL

    -- An attached boundary error target is produced by its REST host and its
    -- declared top-level validation runs in that host transaction.
    SELECT
        host.workflow_definition_id,
        host.node,
        variable ->> 'validation',
        'boundary-target-validation'
    FROM unsafe_nodes AS host
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(host.model -> 'flowNodes', '[]'::jsonb)) AS boundary
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(host.model -> 'variables', '[]'::jsonb)) AS variable
    WHERE boundary ->> 'type' = 'errorBoundaryEvent'
      AND boundary -> 'attachedToRef' = host.node -> 'id'
      AND lower(btrim(variable ->> 'name')) =
          lower(btrim(boundary ->> 'errorVariable'))
      AND NULLIF(btrim(variable ->> 'validation'), '') IS NOT NULL
),
validation_touches AS (
    SELECT
        validation.workflow_definition_id,
        validation.node,
        shared.alias,
        validation.touch_kind
    FROM validation_values AS validation
    JOIN shared_aliases AS shared
      ON shared.workflow_definition_id = validation.workflow_definition_id
     AND position(lower(shared.alias) IN lower(validation.expression)) > 0
),
producer_touches AS (
    SELECT
        node.workflow_definition_id,
        node.node,
        target.alias,
        'output'::text AS touch_kind
    FROM unsafe_nodes AS node
    JOIN output_targets AS target
      ON target.workflow_definition_id = node.workflow_definition_id
     AND target.node = node.node

    UNION ALL

    SELECT
        node.workflow_definition_id,
        node.node,
        variable ->> 'name',
        'node-variable'
    FROM unsafe_nodes AS node
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(node.node -> 'variables', '[]'::jsonb)) AS variable
    WHERE NULLIF(btrim(variable ->> 'name'), '') IS NOT NULL

    UNION ALL

    SELECT
        host.workflow_definition_id,
        host.node,
        boundary ->> 'errorVariable',
        'boundary-error'
    FROM unsafe_nodes AS host
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(host.model -> 'flowNodes', '[]'::jsonb)) AS boundary
    WHERE boundary ->> 'type' = 'errorBoundaryEvent'
      AND boundary -> 'attachedToRef' = host.node -> 'id'
      AND NULLIF(btrim(boundary ->> 'errorVariable'), '') IS NOT NULL
),
touches AS (
    SELECT * FROM producer_touches
    UNION ALL
    SELECT * FROM template_touches
    UNION ALL
    SELECT * FROM validation_touches
),
shared_touches AS (
    SELECT DISTINCT
        touch.workflow_definition_id,
        touch.node,
        shared.alias,
        touch.touch_kind
    FROM touches AS touch
    JOIN shared_aliases AS shared
      ON shared.workflow_definition_id = touch.workflow_definition_id
     AND lower(btrim(shared.alias)) = lower(btrim(touch.alias))
)
SELECT
    node.workflow_definition_id,
    node.workflow_key,
    node.workflow_version,
    node.workflow_name,
    node.node ->> 'id' AS node_id,
    node.node ->> 'name' AS node_name,
    array_agg(
        touch.touch_kind || ':' || touch.alias
        ORDER BY touch.touch_kind, touch.alias) AS shared_touches
FROM unsafe_nodes AS node
JOIN shared_touches AS touch
  ON touch.workflow_definition_id = node.workflow_definition_id
 AND touch.node = node.node
GROUP BY
    node.workflow_definition_id,
    node.workflow_key,
    node.workflow_version,
    node.workflow_name,
    node.node
ORDER BY
    node.workflow_key,
    node.workflow_version,
    node.node ->> 'id';

-- PostgreSQL cannot faithfully reproduce the bounded FIFO/branch/conditional
-- AST proof performed by SharedVariableTransactionLockOrderValidator. Report
-- every published definition with more than one canonical shared key so an
-- operator can prove it was created or republished by the current service. A
-- retained legacy row must be drained before upgrade; runtime also fails it
-- closed from the immutable-definition cache before its first shared row lock.
WITH published_definitions AS (
    SELECT
        definition."Id" AS workflow_definition_id,
        definition."WorkflowKey" AS workflow_key,
        definition."Version" AS workflow_version,
        definition."Name" AS workflow_name,
        definition."Definition" AS model
    FROM flowbit.workflow_definitions AS definition
    WHERE definition."IsPublished"
),
shared_keys AS (
    SELECT DISTINCT
        definition.workflow_definition_id,
        definition.workflow_key,
        definition.workflow_version,
        definition.workflow_name,
        btrim(variable ->> 'sharedKey') AS shared_key
    FROM published_definitions AS definition
    CROSS JOIN LATERAL jsonb_array_elements(
        COALESCE(definition.model -> 'variables', '[]'::jsonb)) AS variable
    WHERE variable ->> 'scope' = 'shared'
      AND NULLIF(btrim(variable ->> 'sharedKey'), '') IS NOT NULL
)
SELECT
    workflow_definition_id,
    workflow_key,
    workflow_version,
    workflow_name,
    'shared-lock-order-manual-review' AS review_reason,
    array_agg(shared_key ORDER BY shared_key COLLATE "C") AS shared_keys
FROM shared_keys
GROUP BY
    workflow_definition_id,
    workflow_key,
    workflow_version,
    workflow_name
HAVING count(*) > 1
ORDER BY workflow_key, workflow_version;
