using System.Text;
using Flowbit.Shared.Dtos;

namespace Flowbit.Infrastructure.Repositories;

public sealed partial class WorkflowRuntimeRepository
{
    private const string InboxVisibilityEvaluationCtes = """
        evaluation_targets AS MATERIALIZED (
            SELECT DISTINCT ON (
                       candidate."InstanceId",
                       candidate."InboxVisibilityConditionId",
                       COALESCE(lower(candidate."ActingFor"), ''))
                   candidate."InstanceId",
                   candidate."InboxVisibilityConditionId",
                   candidate."ActingFor",
                   candidate."WorkflowDefinitionId",
                   candidate."WorkflowName",
                   candidate."NodeId",
                   candidate."NodeName"
            FROM base_candidates candidate
            WHERE candidate."InboxVisibilityConditionId" IS NOT NULL
            ORDER BY candidate."InstanceId",
                     candidate."InboxVisibilityConditionId",
                     COALESCE(lower(candidate."ActingFor"), ''),
                     candidate."Id"
        ),
        visibility_results AS MATERIALIZED (
            SELECT target."InstanceId",
                   target."InboxVisibilityConditionId",
                   target."ActingFor"
            FROM evaluation_targets target
            JOIN flowbit.workflow_definition_user_task_conditions inbox_condition
              ON inbox_condition."Id" = target."InboxVisibilityConditionId"
            LEFT JOIN LATERAL (
                SELECT jsonb_object_agg(effective."VariableName", effective."ValueJson") AS "ValuesJson"
                FROM (
                    SELECT value."VariableName", value."ValueJson"
                    FROM flowbit.instance_variable_current_values value
                    WHERE value."InstanceId" = target."InstanceId"
                      AND value."VariableName" = ANY(inbox_condition."VariableNames")
                      AND NOT EXISTS (
                          SELECT 1
                          FROM flowbit.workflow_definition_shared_variable_bindings binding
                          WHERE binding."WorkflowDefinitionId" = target."WorkflowDefinitionId"
                            AND binding."Alias"::text = value."VariableName"
                      )
                    UNION ALL
                    SELECT binding."Alias"::text, current_value."ValueJson"
                    FROM flowbit.workflow_definition_shared_variable_bindings binding
                    JOIN flowbit.shared_variable_current_values current_value
                      ON current_value."SharedVariableId" = binding."SharedVariableId"
                     AND NOT current_value."IsDeleted"
                    WHERE binding."WorkflowDefinitionId" = target."WorkflowDefinitionId"
                      AND binding."Alias"::text = ANY(inbox_condition."VariableNames")
                ) effective
            ) inbox_values ON TRUE
            WHERE flowbit.evaluate_inbox_visibility_condition(
                inbox_condition."ProgramJson",
                COALESCE(inbox_values."ValuesJson", jsonb_build_object()),
                (@visibilityFixedValues)::jsonb
                || jsonb_strip_nulls(jsonb_build_object(
                    'sys.user', @user,
                    'sys.actingfor', target."ActingFor",
                    'sys.instanceid', target."InstanceId",
                    'sys.workflowid', target."WorkflowDefinitionId",
                    'sys.workflowname', target."WorkflowName",
                    'sys.nodeid', target."NodeId",
                    'sys.nodename', target."NodeName"
                ))
            ) IS TRUE
        )
        """;

    private static void AppendTaskOwnershipFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        string? owner,
        string? ownership)
    {
        if (!string.IsNullOrWhiteSpace(owner))
        {
            args.Add(("owner", owner.Trim()));
            where.Append(" AND lower(COALESCE(ut.\"Assignee\", ut.\"ClaimedBy\")) = lower(@owner)");
        }

        switch (ownership)
        {
            case UserTaskOwnershipKinds.Assigned:
                where.Append(" AND ut.\"Assignee\" IS NOT NULL");
                break;
            case UserTaskOwnershipKinds.Claimed:
                where.Append(" AND ut.\"Assignee\" IS NULL AND ut.\"ClaimedBy\" IS NOT NULL");
                break;
            case UserTaskOwnershipKinds.Unassigned:
                where.Append(" AND ut.\"Assignee\" IS NULL AND ut.\"ClaimedBy\" IS NULL");
                break;
        }
    }
}
