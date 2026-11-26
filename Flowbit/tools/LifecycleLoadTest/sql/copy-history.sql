-- Grows a load-test database by copying its completed instances of one workflow family, so
-- queries can be measured against months of history without running the engine for months.
--
-- Run it only against a disposable load-test database, with no API or Worker connected.
-- PowerShell: Get-Content ./Flowbit/tools/LifecycleLoadTest/sql/copy-history.sql | docker exec -i flowbit-load-pg psql -U flowbit -d <database> -v copies=9 -v days=270
-- Bash:       docker exec -i flowbit-load-pg psql -U flowbit -d <database> -v copies=9 -v days=270 < Flowbit/tools/LifecycleLoadTest/sql/copy-history.sql
--
-- Variables: copies (default 9) is how many copies of each completed instance to add; days
-- (default 270) is how far back the oldest copy goes: copy k is moved back k * days / copies
-- days; workflow (default the purchase-request showcase) is the family to copy.
--
-- How a copy is made:
--   * Every row an instance owns is copied with each id moved up by k times the table's
--     largest id when the script started, so copies never collide with each other or with
--     rows written later. Each id column is classified below: the row's own id, a reference to
--     another copied table (moved by that table's offset), a reference kept as it is (workflow
--     definitions, nodes, flows), or a column that must be empty for the copy to be valid. The
--     script stops before copying anything if a table has an id column that is not classified.
--   * Business keys get the suffix -c<k>, with a claim row each. Every timestamp moves back by
--     the copy's shift. JSON payloads (history details, job payloads) are copied unchanged and
--     keep the original's ids and times.
--   * Foreign keys here are not deferrable and several are circular (tokens and node
--     executions; gateway executions and branches), so rows are inserted with
--     session_replication_role = replica, which skips constraint and user triggers. The
--     current-value projection that a trigger would build is copied from the original's.
--     Afterwards every foreign key and every classified reference of the copied rows is
--     checked, including that a reference stays inside its own instance.
--   * The script records what it did in public.lt_history_copy and refuses to run twice.
\set ON_ERROR_STOP on
\if :{?copies}
\else
\set copies 9
\endif
\if :{?days}
\else
\set days 270
\endif
\if :{?workflow}
\else
\set workflow example-showcase-purchase-request
\endif
SELECT set_config('lt.copies', :'copies', false) AS copies,
       set_config('lt.days', :'days', false) AS days,
       set_config('lt.workflow', :'workflow', false) AS workflow;

-- The id columns of every copied table. action: self (the row's id), keep, null (must be empty
-- in the originals), empty (an id array that must be empty), or the copied table it refers to.
CREATE TEMP TABLE lt_columns (table_name text, column_name text, action text, PRIMARY KEY (table_name, column_name));
INSERT INTO lt_columns VALUES
    ('workflow_instances', 'Id', 'self'),
    ('workflow_instances', 'WorkflowDefinitionId', 'keep'),
    ('workflow_instances', 'LastReactivationTokenId', 'execution_tokens'),
    ('workflow_instances', 'CallRootInstanceId', 'null'),
    ('user_task_role_policies', 'Id', 'self'),
    ('user_task_role_policies', 'InstanceId', 'workflow_instances'),
    ('user_task_role_policies', 'WorkflowDefinitionId', 'keep'),
    ('user_task_role_policies', 'NodeId', 'keep'),
    ('workflow_job_snapshots', 'Id', 'self'),
    ('execution_tokens', 'Id', 'self'),
    ('execution_tokens', 'InstanceId', 'workflow_instances'),
    ('execution_tokens', 'NodeId', 'keep'),
    ('execution_tokens', 'ArrivedViaFlowId', 'keep'),
    ('execution_tokens', 'GatewayBranchId', 'gateway_branches'),
    ('execution_tokens', 'CurrentNodeExecutionId', 'node_executions'),
    ('execution_tokens', 'ComplexDrainStateIds', 'empty'),
    ('execution_tokens', 'ComplexGatewayStateId', 'complex_gateway_states'),
    ('execution_tokens', 'WaitingJobId', 'workflow_jobs'),
    ('execution_tokens', 'WaitingTimerSubscriptionId', 'timer_subscriptions'),
    ('execution_tokens', 'AutomaticActivationStateIds', 'empty'),
    ('execution_tokens', 'WaitingMultiInstanceExecutionId', 'multi_instance_executions'),
    ('gateway_executions', 'Id', 'self'),
    ('gateway_executions', 'InstanceId', 'workflow_instances'),
    ('gateway_executions', 'GatewayNodeId', 'keep'),
    ('gateway_executions', 'SelectedFlowIds', 'keep'),
    ('gateway_executions', 'ParentBranchId', 'gateway_branches'),
    ('gateway_executions', 'InterruptingNodeId', 'keep'),
    ('gateway_executions', 'InterruptingTokenId', 'execution_tokens'),
    ('gateway_branches', 'Id', 'self'),
    ('gateway_branches', 'ExecutionId', 'gateway_executions'),
    ('gateway_branches', 'OriginatingFlowId', 'keep'),
    ('complex_gateway_states', 'Id', 'self'),
    ('complex_gateway_states', 'InstanceId', 'workflow_instances'),
    ('complex_gateway_states', 'GatewayNodeId', 'keep'),
    ('complex_gateway_states', 'ContributingFlowIds', 'keep'),
    ('complex_gateway_states', 'RemainingFlowIds', 'keep'),
    ('complex_gateway_states', 'ActivationDrainStateIds', 'empty'),
    ('complex_gateway_states', 'DrainingTokenIds', 'empty'),
    ('complex_gateway_states', 'ActiveExecutionId', 'gateway_executions'),
    ('multi_instance_executions', 'Id', 'self'),
    ('multi_instance_executions', 'InstanceId', 'workflow_instances'),
    ('multi_instance_executions', 'TokenId', 'execution_tokens'),
    ('multi_instance_executions', 'NodeId', 'keep'),
    ('multi_instance_executions', 'WinningFlowId', 'keep'),
    ('multi_instance_executions', 'RolePolicyId', 'user_task_role_policies'),
    ('multi_instance_executions', 'InputSnapshotId', 'workflow_job_snapshots'),
    ('multi_instance_executions', 'PinnedChildDefinitionId', 'keep'),
    ('multi_instance_executions', 'PinnedChildStartEventId', 'keep'),
    ('multi_instance_executions', 'WorkflowDefinitionId', 'keep'),
    ('multi_instance_flow_counts', 'Id', 'self'),
    ('multi_instance_flow_counts', 'ExecutionId', 'multi_instance_executions'),
    ('multi_instance_flow_counts', 'FlowId', 'keep'),
    ('user_tasks', 'Id', 'self'),
    ('user_tasks', 'InstanceId', 'workflow_instances'),
    ('user_tasks', 'TokenId', 'execution_tokens'),
    ('user_tasks', 'NodeId', 'keep'),
    ('user_tasks', 'MultiInstanceExecutionId', 'multi_instance_executions'),
    ('user_tasks', 'SelectedFlowId', 'keep'),
    ('user_tasks', 'CompletionDelegationId', 'keep'),
    ('user_tasks', 'AdministrativeActionBatchId', 'null'),
    ('user_tasks', 'InboxVisibilityConditionId', 'keep'),
    ('user_tasks', 'RolePolicyId', 'user_task_role_policies'),
    ('node_executions', 'Id', 'self'),
    ('node_executions', 'InstanceId', 'workflow_instances'),
    ('node_executions', 'ExecutionTokenId', 'execution_tokens'),
    ('node_executions', 'UserTaskId', 'user_tasks'),
    ('node_executions', 'MultiInstanceExecutionId', 'multi_instance_executions'),
    ('node_executions', 'NodeId', 'keep'),
    ('node_executions', 'EntryGatewayBranchId', 'gateway_branches'),
    ('node_executions', 'ExitGatewayBranchId', 'gateway_branches'),
    ('node_executions', 'EnteredViaFlowId', 'keep'),
    ('node_executions', 'SelectedFlowId', 'keep'),
    ('node_executions', 'ExitedViaFlowId', 'keep'),
    ('node_executions', 'CompletedDelegationId', 'keep'),
    ('node_executions', 'TriggeredDelegationId', 'keep'),
    ('node_executions', 'WorkflowDefinitionId', 'keep'),
    ('node_executions', 'AutomaticMultiInstanceItemId', 'null'),
    ('timer_subscriptions', 'Id', 'self'),
    ('timer_subscriptions', 'InstanceId', 'workflow_instances'),
    ('timer_subscriptions', 'WorkflowDefinitionId', 'keep'),
    ('timer_subscriptions', 'TokenId', 'execution_tokens'),
    ('timer_subscriptions', 'TimerNodeId', 'keep'),
    ('timer_subscriptions', 'AttachedToNodeId', 'keep'),
    ('workflow_jobs', 'Id', 'self'),
    ('workflow_jobs', 'InstanceId', 'workflow_instances'),
    ('workflow_jobs', 'WorkflowDefinitionId', 'keep'),
    ('workflow_jobs', 'TokenId', 'execution_tokens'),
    ('workflow_jobs', 'MultiInstanceExecutionId', 'multi_instance_executions'),
    ('workflow_jobs', 'UserTaskId', 'user_tasks'),
    ('workflow_jobs', 'TimerSubscriptionId', 'timer_subscriptions'),
    ('workflow_jobs', 'NodeId', 'keep'),
    ('workflow_jobs', 'SnapshotId', 'workflow_job_snapshots'),
    ('workflow_jobs', 'ConditionalBoundarySubscriptionId', 'null'),
    ('workflow_jobs', 'CallInvocationId', 'null'),
    ('workflow_jobs', 'AutomaticMultiInstanceItemId', 'null'),
    ('workflow_job_attempts', 'Id', 'self'),
    ('workflow_job_attempts', 'JobId', 'workflow_jobs'),
    ('instance_history', 'Id', 'self'),
    ('instance_history', 'InstanceId', 'workflow_instances'),
    ('instance_history', 'ActionId', 'keep'),
    ('instance_history', 'FromStepId', 'keep'),
    ('instance_history', 'ToStepId', 'keep'),
    ('instance_history', 'MultiInstanceExecutionId', 'multi_instance_executions'),
    ('instance_history', 'TokenId', 'execution_tokens'),
    ('instance_history', 'UserTaskId', 'user_tasks'),
    ('instance_history', 'DelegationId', 'keep'),
    ('instance_history', 'WorkflowDefinitionId', 'keep'),
    ('instance_history', 'AdministrativeActionBatchId', 'null'),
    ('instance_variables', 'Id', 'self'),
    ('instance_variables', 'InstanceId', 'workflow_instances'),
    ('instance_variables', 'SourceActionId', 'keep'),
    ('instance_variables', 'NodeExecutionId', 'node_executions'),
    ('instance_variables', 'DelegationId', 'keep'),
    ('instance_variables', 'InstanceVariableUpdateAuditId', 'null'),
    ('sequence_flow_occurrences', 'Id', 'self'),
    ('sequence_flow_occurrences', 'InstanceId', 'workflow_instances'),
    ('sequence_flow_occurrences', 'SequenceFlowId', 'keep'),
    ('sequence_flow_occurrences', 'SourceNodeId', 'keep'),
    ('sequence_flow_occurrences', 'TargetNodeId', 'keep'),
    ('sequence_flow_occurrences', 'TokenId', 'execution_tokens'),
    ('sequence_flow_occurrences', 'UserTaskId', 'user_tasks'),
    ('sequence_flow_occurrences', 'MultiInstanceExecutionId', 'multi_instance_executions'),
    ('sequence_flow_occurrences', 'DelegationId', 'keep'),
    ('sequence_flow_occurrences', 'WorkflowDefinitionId', 'keep'),
    ('sequence_flow_summaries', 'Id', 'self'),
    ('sequence_flow_summaries', 'InstanceId', 'workflow_instances'),
    ('sequence_flow_summaries', 'SequenceFlowId', 'keep'),
    ('sequence_flow_summaries', 'LastActionDelegationId', 'keep'),
    ('sequence_flow_summaries', 'LastTraversalDelegationId', 'keep');

-- Tables that an instance owns and that are not copied. A copied instance must have no rows in
-- them; the check below stops the script if a completed instance of the family does.
CREATE TEMP TABLE lt_uncopied (table_name text PRIMARY KEY, instance_column text NOT NULL);
INSERT INTO lt_uncopied VALUES
    ('workflow_incidents', 'InstanceId'),
    ('conditional_boundary_subscriptions', 'InstanceId'),
    ('signal_subscriptions', 'InstanceId'),
    ('message_delivery_receipts', 'InstanceId'),
    ('instance_variable_updates', 'InstanceId'),
    ('workflow_instance_version_changes', 'InstanceId'),
    ('call_activity_executions', 'ParentInstanceId'),
    ('call_activity_results', 'ChildInstanceId'),
    ('shared_variable_revisions', 'InstanceId'),
    ('workflow_idempotency_claims', 'InstanceId'),
    ('administrative_action_batch_items', 'InstanceId'),
    ('lifecycle_events', 'InstanceId');

DO $$
DECLARE
    copies int := current_setting('lt.copies')::int;
    days int := current_setting('lt.days')::int;
    family text := current_setting('lt.workflow');
    -- Parents before children where it reads naturally; the order does not matter for
    -- correctness because constraint triggers are off while rows are inserted.
    tables text[] := ARRAY['workflow_instances', 'user_task_role_policies', 'workflow_job_snapshots',
        'execution_tokens', 'gateway_executions', 'gateway_branches', 'complex_gateway_states',
        'multi_instance_executions', 'multi_instance_flow_counts', 'user_tasks', 'node_executions',
        'timer_subscriptions', 'workflow_jobs', 'workflow_job_attempts', 'instance_history',
        'instance_variables', 'sequence_flow_occurrences', 'sequence_flow_summaries'];
    filters jsonb := jsonb_build_object(
        'workflow_instances', 'lt_tpl_instances x ON x.id = s."Id"',
        'workflow_job_snapshots', 'lt_tpl_snapshots x ON x.id = s."Id"',
        'gateway_branches', 'lt_tpl_gateways x ON x.id = s."ExecutionId"',
        'multi_instance_flow_counts', 'lt_tpl_mi x ON x.id = s."ExecutionId"',
        'workflow_job_attempts', 'lt_tpl_jobs x ON x.id = s."JobId"');
    problem text;
    t text;
    k int;
    offsets jsonb := '{}';
    shift interval;
    columns text;
    expressions text;
    rows_copied bigint;
    started timestamptz;
    r record;
BEGIN
    IF copies < 1 OR days < 1 THEN
        RAISE EXCEPTION 'copies and days must be at least 1';
    END IF;
    IF to_regclass('public.lt_history_copy') IS NOT NULL THEN
        RAISE EXCEPTION 'This database was already grown (public.lt_history_copy exists); start from a fresh copy';
    END IF;

    -- 1. Every id column of a copied table is classified, and every classification names a real column.
    SELECT string_agg(format('%s.%s', ic.table_name, ic.column_name), ', ') INTO problem
    FROM information_schema.columns ic
    WHERE ic.table_schema = 'flowbit' AND ic.table_name = ANY (tables)
      AND ic.data_type IN ('bigint', 'integer', 'ARRAY')
      AND (ic.column_name LIKE '%Id' OR ic.column_name LIKE '%Ids')
      AND NOT EXISTS (SELECT 1 FROM lt_columns l WHERE l.table_name = ic.table_name AND l.column_name = ic.column_name);
    IF problem IS NOT NULL THEN
        RAISE EXCEPTION 'Classify these id columns in lt_columns before copying: %', problem;
    END IF;
    SELECT string_agg(format('%s.%s', l.table_name, l.column_name), ', ') INTO problem
    FROM lt_columns l
    WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns ic
                      WHERE ic.table_schema = 'flowbit' AND ic.table_name = l.table_name AND ic.column_name = l.column_name);
    IF problem IS NOT NULL THEN
        RAISE EXCEPTION 'lt_columns names columns that do not exist: %', problem;
    END IF;

    -- 2. The template: completed instances of the family and the rows reached through them.
    CREATE TEMP TABLE lt_tpl_instances AS
        SELECT "Id" AS id FROM flowbit.workflow_instances WHERE "WorkflowKey" = family AND "Status" = 'completed';
    IF NOT EXISTS (SELECT 1 FROM lt_tpl_instances) THEN
        RAISE EXCEPTION 'No completed instances of %', family;
    END IF;
    CREATE TEMP TABLE lt_tpl_gateways AS
        SELECT g."Id" AS id FROM flowbit.gateway_executions g JOIN lt_tpl_instances x ON x.id = g."InstanceId";
    CREATE TEMP TABLE lt_tpl_mi AS
        SELECT m."Id" AS id FROM flowbit.multi_instance_executions m JOIN lt_tpl_instances x ON x.id = m."InstanceId";
    CREATE TEMP TABLE lt_tpl_jobs AS
        SELECT j."Id" AS id FROM flowbit.workflow_jobs j JOIN lt_tpl_instances x ON x.id = j."InstanceId";
    CREATE TEMP TABLE lt_tpl_snapshots AS
        SELECT DISTINCT id FROM (
            SELECT j."SnapshotId" AS id FROM flowbit.workflow_jobs j JOIN lt_tpl_instances x ON x.id = j."InstanceId"
            UNION ALL
            SELECT m."InputSnapshotId" FROM flowbit.multi_instance_executions m JOIN lt_tpl_instances x ON x.id = m."InstanceId") s
        WHERE id IS NOT NULL;
    CREATE UNIQUE INDEX ON lt_tpl_instances (id);
    ANALYZE lt_tpl_instances; ANALYZE lt_tpl_gateways; ANALYZE lt_tpl_mi; ANALYZE lt_tpl_jobs; ANALYZE lt_tpl_snapshots;

    -- The template's rows, read once; every copy is made from these.
    FOREACH t IN ARRAY tables LOOP
        EXECUTE format('CREATE TEMP TABLE %I AS SELECT s.* FROM flowbit.%I s JOIN %s',
            'lt_src_' || t, t, coalesce(filters ->> t, 'lt_tpl_instances x ON x.id = s."InstanceId"'));
    END LOOP;
    CREATE TEMP TABLE lt_src_current_values AS
        SELECT s.* FROM flowbit.instance_variable_current_values s JOIN lt_tpl_instances x ON x.id = s."InstanceId";
    CREATE TEMP TABLE lt_src_claims AS
        SELECT i."Id" AS instance_id, c."WorkflowKey", c."BusinessKey", c."IsPermanent", c."ActiveInstanceId"
        FROM flowbit.workflow_instances i
        JOIN lt_tpl_instances x ON x.id = i."Id"
        JOIN flowbit.workflow_business_key_claims c ON c."WorkflowKey" = i."WorkflowKey" AND c."BusinessKey" = i."BusinessKey";

    -- 3. The template has nothing the copy cannot carry.
    FOR r IN SELECT * FROM lt_uncopied LOOP
        EXECUTE format('SELECT count(*) FROM flowbit.%I s JOIN lt_tpl_instances x ON x.id = s.%I', r.table_name, r.instance_column) INTO rows_copied;
        IF rows_copied > 0 THEN
            RAISE EXCEPTION 'Completed instances own % rows in %, which this script does not copy', rows_copied, r.table_name;
        END IF;
    END LOOP;
    FOR r IN SELECT * FROM lt_columns WHERE action IN ('null', 'empty') LOOP
        EXECUTE format('SELECT count(*) FROM %I s WHERE %s', 'lt_src_' || r.table_name,
            CASE r.action WHEN 'null' THEN format('s.%I IS NOT NULL', r.column_name)
                          ELSE format('cardinality(s.%I) > 0', r.column_name) END) INTO rows_copied;
        IF rows_copied > 0 THEN
            RAISE EXCEPTION '% rows of the template have a value in %.%, which a copy would share', rows_copied, r.table_name, r.column_name;
        END IF;
    END LOOP;

    -- 4. Offsets: each table's largest id now. Copy k moves ids up by k times that.
    CREATE TABLE public.lt_history_copy (
        table_name text PRIMARY KEY,
        original_max bigint NOT NULL,
        template_rows bigint NOT NULL,
        copies int NOT NULL,
        days int NOT NULL,
        copied_at timestamptz NOT NULL DEFAULT now());
    FOREACH t IN ARRAY tables LOOP
        EXECUTE format('SELECT coalesce(max("Id"), 0) FROM flowbit.%I', t) INTO rows_copied;
        offsets := offsets || jsonb_build_object(t, rows_copied);
        EXECUTE format('INSERT INTO public.lt_history_copy (table_name, original_max, template_rows, copies, days) '
            || 'SELECT %L, %s, count(*), %s, %s FROM %I', t, rows_copied, copies, days, 'lt_src_' || t);
    END LOOP;
    COMMIT;
    RAISE NOTICE 'Template: % completed instances of %; making % copies over % days', (SELECT count(*) FROM lt_tpl_instances), family, copies, days;

    -- 5. The copies, one transaction each.
    FOR k IN 1..copies LOOP
        started := clock_timestamp();
        PERFORM set_config('session_replication_role', 'replica', true);
        shift := make_interval(secs => k * days * 86400.0 / copies);
        FOREACH t IN ARRAY tables LOOP
            SELECT string_agg(format('%I', ic.column_name), ', ' ORDER BY ic.ordinal_position),
                   string_agg(CASE
                       WHEN l.action = 'self' THEN format('s.%I + %s', ic.column_name, k * (offsets ->> t)::bigint)
                       WHEN l.action IN ('keep', 'null', 'empty') THEN format('s.%I', ic.column_name)
                       WHEN l.action IS NOT NULL THEN format('s.%I + %s', ic.column_name, k * (offsets ->> l.action)::bigint)
                       WHEN ic.data_type IN ('timestamp with time zone', 'timestamp without time zone')
                           THEN format('s.%I - %L::interval', ic.column_name, shift)
                       WHEN t = 'workflow_instances' AND ic.column_name = 'BusinessKey'
                           THEN format('s.%I || %L', ic.column_name, '-c' || k)
                       ELSE format('s.%I', ic.column_name) END, ', ' ORDER BY ic.ordinal_position)
              INTO columns, expressions
            FROM information_schema.columns ic
            LEFT JOIN lt_columns l ON l.table_name = ic.table_name AND l.column_name = ic.column_name
            WHERE ic.table_schema = 'flowbit' AND ic.table_name = t AND ic.is_generated = 'NEVER';
            EXECUTE format('INSERT INTO flowbit.%I (%s) SELECT %s FROM %I s', t, columns, expressions, 'lt_src_' || t);
        END LOOP;

        -- What the instance_variables trigger would have built: each name's latest value.
        INSERT INTO flowbit.instance_variable_current_values ("InstanceId", "VariableName", "SourceVariableId", "ValueJson", "SetAt")
        SELECT s."InstanceId" + k * (offsets ->> 'workflow_instances')::bigint, s."VariableName",
               s."SourceVariableId" + k * (offsets ->> 'instance_variables')::bigint, s."ValueJson", s."SetAt" - shift
        FROM lt_src_current_values s;

        -- A claim for each copied business key, released as the original's is.
        INSERT INTO flowbit.workflow_business_key_claims ("WorkflowKey", "BusinessKey", "IsPermanent", "ActiveInstanceId", "LastInstanceId")
        SELECT c."WorkflowKey", c."BusinessKey" || '-c' || k, c."IsPermanent",
               CASE WHEN c."ActiveInstanceId" = c.instance_id THEN c.instance_id + k * (offsets ->> 'workflow_instances')::bigint END,
               c.instance_id + k * (offsets ->> 'workflow_instances')::bigint
        FROM lt_src_claims c;

        COMMIT;
        RAISE NOTICE 'Copy % of % done in % (moved back %)', k, copies, date_trunc('second', clock_timestamp() - started), date_trunc('hour', shift);
    END LOOP;

    -- 6. Sequences continue above the copies.
    FOREACH t IN ARRAY tables LOOP
        EXECUTE format('SELECT setval(pg_get_serial_sequence(%L, ''Id''), (SELECT max("Id") FROM flowbit.%I))', 'flowbit.' || t, t);
    END LOOP;
    COMMIT;
END
$$;

-- 7. Checks over the copied rows only: every foreign key, every classified reference, and that
-- a reference to another instance-owned row stays inside the same instance.
DO $$
DECLARE
    r record;
    copied_filter text;
    orphan_count bigint;
    failures int := 0;
    instance_max bigint := (SELECT original_max FROM public.lt_history_copy WHERE table_name = 'workflow_instances');
    started timestamptz := clock_timestamp();
BEGIN
    -- Foreign keys of tables that received copied rows.
    FOR r IN
        SELECT c.conname, cl.relname AS child, pl.relname AS parent,
               array_agg(ca.attname ORDER BY k.ord) AS child_cols, array_agg(pa.attname ORDER BY k.ord) AS parent_cols
        FROM pg_constraint c
        JOIN pg_class cl ON cl.oid = c.conrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
        JOIN pg_class pl ON pl.oid = c.confrelid
        CROSS JOIN LATERAL unnest(c.conkey, c.confkey) WITH ORDINALITY AS k(col, fcol, ord)
        JOIN pg_attribute ca ON ca.attrelid = c.conrelid AND ca.attnum = k.col
        JOIN pg_attribute pa ON pa.attrelid = c.confrelid AND pa.attnum = k.fcol
        WHERE c.contype = 'f' AND n.nspname = 'flowbit'
          AND (cl.relname IN (SELECT table_name FROM public.lt_history_copy)
               OR cl.relname IN ('instance_variable_current_values', 'workflow_business_key_claims'))
        GROUP BY c.conname, cl.relname, pl.relname
    LOOP
        copied_filter := CASE r.child
            WHEN 'instance_variable_current_values' THEN format('s."InstanceId" > %s', instance_max)
            WHEN 'workflow_business_key_claims' THEN format('s."LastInstanceId" > %s', instance_max)
            ELSE format('s."Id" > %s', (SELECT original_max FROM public.lt_history_copy WHERE table_name = r.child)) END;
        EXECUTE format('SELECT count(*) FROM flowbit.%I s WHERE %s AND %s AND NOT EXISTS (SELECT 1 FROM flowbit.%I p WHERE %s)',
            r.child, copied_filter,
            (SELECT string_agg(format('s.%I IS NOT NULL', col), ' AND ') FROM unnest(r.child_cols) col),
            r.parent,
            (SELECT string_agg(format('p.%I = s.%I', p, c), ' AND ') FROM unnest(r.parent_cols, r.child_cols) AS u(p, c)))
            INTO orphan_count;
        IF orphan_count > 0 THEN
            failures := failures + 1;
            RAISE WARNING 'Foreign key % (% -> %): % copied rows point at a missing row', r.conname, r.child, r.parent, orphan_count;
        END IF;
    END LOOP;

    -- Classified references, with or without a foreign key: the target exists and, when both
    -- rows belong to an instance, it is the same instance.
    FOR r IN
        SELECT l.table_name, l.column_name, l.action,
               EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'flowbit' AND table_name = l.table_name AND column_name = 'InstanceId') AS child_has_instance,
               EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'flowbit' AND table_name = l.action AND column_name = 'InstanceId') AS parent_has_instance
        FROM lt_columns l
        WHERE l.action NOT IN ('self', 'keep', 'null', 'empty')
    LOOP
        EXECUTE format('SELECT count(*) FROM flowbit.%I s LEFT JOIN flowbit.%I p ON p."Id" = s.%I '
            || 'WHERE s."Id" > %s AND s.%I IS NOT NULL AND (p."Id" IS NULL%s)',
            r.table_name, r.action, r.column_name,
            (SELECT original_max FROM public.lt_history_copy WHERE table_name = r.table_name),
            r.column_name,
            CASE WHEN r.child_has_instance AND r.parent_has_instance AND r.column_name <> 'InstanceId'
                 THEN ' OR p."InstanceId" IS DISTINCT FROM s."InstanceId"' ELSE '' END)
            INTO orphan_count;
        IF orphan_count > 0 THEN
            failures := failures + 1;
            RAISE WARNING '%.% -> %: % copied rows point at a missing row or another instance''s row', r.table_name, r.column_name, r.action, orphan_count;
        END IF;
    END LOOP;

    IF failures > 0 THEN
        RAISE EXCEPTION '% checks failed; this database is not usable for measurements', failures;
    END IF;
    RAISE NOTICE 'Every foreign key and reference of the copied rows checked in %', date_trunc('second', clock_timestamp() - started);
END
$$;

-- 8. Each copied table gained exactly its template rows once per copy.
DO $$
DECLARE
    r record;
    added bigint;
BEGIN
    FOR r IN SELECT * FROM public.lt_history_copy ORDER BY table_name LOOP
        EXECUTE format('SELECT count(*) FROM flowbit.%I WHERE "Id" > %s', r.table_name, r.original_max) INTO added;
        IF added <> r.template_rows * r.copies THEN
            RAISE EXCEPTION '% gained % rows, not % x %', r.table_name, added, r.template_rows, r.copies;
        END IF;
        RAISE NOTICE '%: % template rows, % added', rpad(r.table_name, 28), r.template_rows, added;
    END LOOP;
END
$$;

VACUUM (ANALYZE);
SELECT pg_size_pretty(pg_database_size(current_database())) AS database_size;
