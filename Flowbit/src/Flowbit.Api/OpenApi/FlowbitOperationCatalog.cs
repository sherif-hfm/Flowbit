using System.Text.RegularExpressions;

namespace Flowbit.Api.OpenApi;

/// <summary>
/// Canonical purpose text for every route in the generated API document.
/// Keep these descriptions aligned with the richer contracts in docs/api-guide.md.
/// </summary>
internal static partial class FlowbitOperationCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GET /"] = "Redirect to the interactive Swagger UI available in Development.",
            ["GET /api/auth/context"] = "Get the server-resolved actor identity and normalized roles.",
            ["GET /api/workflows"] = "List the latest version of every workflow definition.",
            ["POST /api/workflows"] = "Create the next immutable version in a workflow-key family.",
            ["GET /api/workflows/{workflowKey}/versions"] = "List every version of the workflow identified by its stable workflow key.",
            ["GET /api/workflows/{id}"] = "Retrieve one exact workflow-definition version by its database identifier.",
            ["PUT /api/workflows/{id}"] = "Create a new immutable version from an existing workflow definition.",
            ["DELETE /api/workflows/{id}"] = "Delete one workflow-definition version when no retained runtime data references it.",
            ["POST /api/workflows/{id}/publish"] = "Publish a workflow-definition version so it can be selected for new instances.",
            ["POST /api/workflows/{id}/unpublish"] = "Unpublish a workflow-definition version so it cannot be selected for new instances.",
            ["POST /api/workflows/{id}/set-default"] = "Make a published workflow-definition version the default for key-based starts.",
            ["POST /api/workflows/{workflowKey}/message-start"] = "Start an instance by delivering JSON to a system-only message start event in a workflow family.",
            ["PATCH /api/instances/{id}/variables"] = "Administratively append one or more variable values to a running instance in one atomic operation.",
            ["POST /api/instances"] = "Start a workflow instance from an exact definition version or the published default of a workflow key.",
            ["GET /api/instances"] = "List visible workflow instances using scalar filters, structured sorting, and keyset pagination.",
            ["POST /api/instances/search"] = "Search visible workflow instances with bounded advanced variable predicates.",
            ["GET /api/instances/inbox"] = "List user-task work currently visible to the authenticated actor.",
            ["POST /api/instances/inbox/search"] = "Search the authenticated actor's inbox with bounded advanced variable predicates.",
            ["GET /api/instances/{id}"] = "Retrieve the full definition, variables, execution positions, work summary, and history for one instance.",
            ["GET /api/instances/{id}/reactivation"] = "Preview whether a completed or cancelled instance can be reactivated and list eligible prior user-task targets.",
            ["POST /api/instances/{id}/reactivation"] = "Reactivate an eligible completed or cancelled instance at a previously visited user task.",
            ["POST /api/instances/{id}/version-change/preview"] = "Preview compatibility between a running instance and a published version in the same workflow family without changing state.",
            ["POST /api/instances/{id}/version-change"] = "Atomically move a compatible running instance to another published workflow version.",
            ["GET /api/instances/{id}/user-tasks"] = "List retained user-task work items for an instance, optionally filtered by status.",
            ["GET /api/instances/{id}/flows"] = "List selectable sequence flows available from the instance's unambiguous resting user task.",
            ["POST /api/instances/{id}/claim"] = "Claim the instance's unambiguous active user task for the authenticated actor.",
            ["POST /api/instances/{id}/unclaim"] = "Release the claim on the instance's unambiguous active user task.",
            ["POST /api/instances/{id}/flows/{flowId}"] = "Complete the instance's unambiguous active user task by taking one available sequence flow.",
            ["POST /api/instances/{id}/cancel"] = "Cancel an active workflow instance and all of its unfinished runtime work.",
            ["POST /api/instances/{id}/message"] = "Deliver external JSON to an instance waiting at an intermediate message catch event.",
            ["GET /api/user-tasks/manage"] = "List open user tasks visible to the authenticated assignment or role manager.",
            ["POST /api/user-tasks/manage/search"] = "Search manageable user tasks with bounded advanced variable predicates.",
            ["GET /api/user-tasks/{taskId}"] = "Retrieve one personal user task together with the caller's current capabilities.",
            ["GET /api/user-tasks/{taskId}/flows"] = "List selectable sequence-flow actions currently available to the caller for one task.",
            ["POST /api/user-tasks/{taskId}/claim"] = "Claim one active pooled user task for the authenticated actor.",
            ["POST /api/user-tasks/{taskId}/unclaim"] = "Release a user-task claim as its owner or an authorized recovery actor.",
            ["POST /api/user-tasks/{taskId}/assign"] = "Assign or reassign one waiting user task to a named actor.",
            ["POST /api/user-tasks/{taskId}/unassign"] = "Remove the explicit assignee from one waiting user task.",
            ["GET /api/user-tasks/{taskId}/roles"] = "Read the immutable effective task and action role policy captured for one waiting task.",
            ["POST /api/user-tasks/{taskId}/roles"] = "Replace every editable role list for one waiting normal user task using optimistic concurrency.",
            ["POST /api/user-tasks/{taskId}/flows/{flowId}"] = "Complete one exact user-task work item by taking a currently available flow and submitting its values.",
            ["GET /api/multi-instance-executions/{executionId}/flows"] = "List parent-level interrupt actions available for an active multi-instance execution.",
            ["POST /api/multi-instance-executions/{executionId}/flows/{flowId}"] = "Take a parent-level interrupt action and atomically cancel unfinished child work.",
            ["GET /api/multi-instance-executions/{executionId}/roles"] = "Read the effective role policy shared by unfinished work in one multi-instance execution.",
            ["POST /api/multi-instance-executions/{executionId}/roles"] = "Replace roles for a waiting multi-instance execution and every unfinished child using optimistic concurrency.",
            ["GET /api/task-distribution/workflows/{workflowKey}/tasks"] = "List tasks available to the machine client authorized to distribute work for one workflow family.",
            ["POST /api/task-distribution/workflows/{workflowKey}/tasks/search"] = "Search distributable tasks with bounded advanced variable predicates.",
            ["POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/assign"] = "Assign or reassign one task within the machine client's authorized workflow family.",
            ["POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/unassign"] = "Remove the assignee from one task within the machine client's authorized workflow family.",
            ["GET /api/node-executions"] = "Search the authorized cross-workflow ledger of committed node executions.",
            ["POST /api/node-executions/search"] = "Search authorized node executions with bounded advanced current-variable predicates.",
            ["GET /api/node-executions/{id}"] = "Retrieve one authorized node execution with lifecycle, actor, failure, and attributed variable-change details.",
            ["GET /api/jobs"] = "Search durable workflow jobs using operational filters and keyset pagination.",
            ["GET /api/jobs/statistics"] = "Return durable workflow queue counts, oldest due work, and worker-health indicators.",
            ["GET /api/jobs/{jobId}"] = "Retrieve scheduling, lease, retry, payload, and ownership metadata for one durable job.",
            ["GET /api/jobs/{jobId}/attempts"] = "List the bounded immutable attempt history for one durable workflow job.",
            ["GET /api/incidents"] = "Search workflow incidents using operational filters and keyset pagination.",
            ["GET /api/incidents/{incidentId}"] = "Retrieve one workflow incident and its related durable-job metadata.",
            ["POST /api/incidents/{incidentId}/retry"] = "Resolve an open incident by fencing and queueing its durable job for a manual retry.",
            ["GET /api/instances/{id}/administrative-actions"] = "List active ordinary-task and multi-instance positions plus direct administrative actions, independently of personal inbox eligibility.",
            ["POST /api/instances/{id}/administrative-actions"] = "Execute one displayed administrative action immediately with exact-position concurrency checks and atomic audit.",
            ["GET /api/administrative-actions/workflows"] = "List exact workflow versions that contain source nodes eligible for administrative batch actions.",
            ["GET /api/workflows/{workflowId}/administrative-actions/nodes"] = "List ordinary and multi-instance user-task source nodes in one exact workflow version.",
            ["GET /api/workflows/{workflowId}/nodes/{sourceNodeId}/administrative-actions"] = "List direct flows and attached timer-boundary actions for a source node without normal task-authorization filtering.",
            ["POST /api/administrative-actions/candidates/search"] = "Search active ordinary-task and multi-instance execution positions at one exact source node.",
            ["POST /api/administrative-action-batches"] = "Freeze a candidate selection and asynchronously prepare an administrative-action batch.",
            ["GET /api/administrative-action-batches"] = "List durable administrative-action batches by workflow, actor, status, and page.",
            ["GET /api/administrative-action-batches/{batchId}"] = "Retrieve a batch's frozen request, progress, actor snapshots, and durable job references.",
            ["GET /api/administrative-action-batches/{batchId}/items"] = "List retained per-item preparation and execution results for an administrative-action batch.",
            ["POST /api/administrative-action-batches/{batchId}/confirm"] = "Idempotently confirm the prepared eligible set and queue independent administrative execution.",
            ["POST /api/administrative-action-batches/{batchId}/cancel"] = "Stop unstarted administrative items without reversing actions that already succeeded.",
            ["POST /api/instance-version-change-batches/candidates/search"] = "Search running version-change candidates on one exact source workflow version.",
            ["POST /api/instance-version-change-batches"] = "Freeze a selection and asynchronously prepare an instance version-change batch.",
            ["GET /api/instance-version-change-batches"] = "List durable instance version-change batches by workflow, actor, status, and page.",
            ["GET /api/instance-version-change-batches/{batchId}"] = "Retrieve a version-change batch's frozen request, progress, actor snapshots, and jobs.",
            ["GET /api/instance-version-change-batches/{batchId}/items"] = "List retained compatibility and execution results for a version-change batch.",
            ["POST /api/instance-version-change-batches/{batchId}/confirm"] = "Confirm the displayed compatibility result and queue independent instance version changes.",
            ["POST /api/instance-version-change-batches/{batchId}/cancel"] = "Cancel unstarted version changes without reversing items that already succeeded.",
            ["POST /api/instance-variable-update-batches/candidates/search"] = "Search running variable-update candidates in one workflow family.",
            ["POST /api/instance-variable-update-batches"] = "Freeze a selection and asynchronously prepare a variable-update batch.",
            ["GET /api/instance-variable-update-batches"] = "List durable instance variable-update batches by workflow, actor, status, and page.",
            ["GET /api/instance-variable-update-batches/{batchId}"] = "Retrieve a variable-update batch's frozen request, progress, actor snapshots, and jobs.",
            ["GET /api/instance-variable-update-batches/{batchId}/items"] = "List retained preparation and execution results for a variable-update batch.",
            ["POST /api/instance-variable-update-batches/{batchId}/confirm"] = "Confirm the prepared population and queue independent per-version variable-update jobs.",
            ["POST /api/instance-variable-update-batches/{batchId}/cancel"] = "Cancel unstarted variable updates without reversing items that already succeeded.",
            ["GET /api/user-delegations"] = "List the authenticated actor's outgoing or incoming standing delegation grants.",
            ["POST /api/user-delegations"] = "Create self-service delegation grants for the requested workflow families.",
            ["POST /api/user-delegations/{id}/accept"] = "Accept a pending delegation grant as its designated delegate.",
            ["POST /api/user-delegations/{id}/reject"] = "Reject a pending delegation grant as its designated delegate.",
            ["POST /api/user-delegations/{id}/revoke"] = "Withdraw or revoke a delegation grant as one of its participants.",
            ["GET /api/user-delegations/manage"] = "Administratively search delegation grants by delegator, delegate, workflow, and state.",
            ["POST /api/user-delegations/manage"] = "Create delegation grants on behalf of an explicitly named delegator.",
            ["POST /api/user-delegations/manage/{id}/revoke"] = "Administratively revoke one delegation grant.",
            ["GET /api/user-delegation-policies/{workflowKey}"] = "Read a workflow family's delegation-acceptance policy or its unpersisted default.",
            ["PUT /api/user-delegation-policies/{workflowKey}"] = "Create or replace the acceptance policy applied to future delegation grants for a workflow family.",
            ["GET /api/engine-settings"] = "List engine settings and their optimistic concurrency timestamps.",
            ["POST /api/engine-settings"] = "Create an engine setting with its operational description.",
            ["PUT /api/engine-settings/{id}"] = "Replace an engine setting's value and description using optimistic concurrency.",
            ["DELETE /api/engine-settings/{id}"] = "Delete an engine setting using its expected update timestamp.",
            ["GET /api/workflow-settings"] = "List workflow settings and their optimistic concurrency timestamps.",
            ["POST /api/workflow-settings"] = "Create a workflow setting with its operational description.",
            ["PUT /api/workflow-settings/{id}"] = "Replace a workflow setting's value and description using optimistic concurrency.",
            ["DELETE /api/workflow-settings/{id}"] = "Delete a workflow setting using its expected update timestamp.",
            ["GET /api/shared-variables"] = "List deployment-wide shared-variable contract metadata without returning current values.",
            ["POST /api/shared-variables"] = "Create a deployment-wide typed shared variable and optionally set its first value.",
            ["GET /api/shared-variables/{key}"] = "Retrieve shared-variable contract and lifecycle metadata without the current value.",
            ["PATCH /api/shared-variables/{key}"] = "Update only a shared variable's description using optimistic concurrency.",
            ["GET /api/shared-variables/{key}/value"] = "Retrieve the current value and value revision of one shared variable.",
            ["PUT /api/shared-variables/{key}/value"] = "Replace a shared variable's current value using an expected revision.",
            ["POST /api/shared-variables/{key}/archive"] = "Archive a shared variable when no active workflow contract blocks the lifecycle change.",
            ["POST /api/shared-variables/{key}/reactivate"] = "Reactivate an archived shared variable using optimistic concurrency.",
            ["GET /api/shared-variables/{key}/history"] = "List immutable metadata and value revisions for one shared variable.",
            ["GET /api/shared-variables/{key}/lifecycle-blockers"] = "Inspect active workflow references that currently prevent shared-variable archival.",
            ["GET /api/shared-variable-clients"] = "List managed shared-variable API clients without exposing secret material.",
            ["POST /api/shared-variable-clients"] = "Create a managed shared-variable API client and return its new secret exactly once.",
            ["GET /api/shared-variable-clients/{id}"] = "Retrieve managed shared-variable API-client metadata without secret material.",
            ["PUT /api/shared-variable-clients/{id}"] = "Update API-client metadata and scopes using optimistic concurrency.",
            ["POST /api/shared-variable-clients/{id}/rotate"] = "Rotate an API-client secret, optionally retaining the previous secret for a bounded grace period.",
            ["POST /api/shared-variable-clients/{id}/revoke"] = "Immediately revoke a shared-variable API client and all active secrets.",
            ["GET /api/retention"] = "Retrieve retention policies, cleanup progress, and effective operational status.",
            ["PUT /api/retention/policies/{category}"] = "Update one retention period using an optimistic policy revision.",
            ["POST /api/retention/preview"] = "Preview rows eligible for retention cleanup and rows protected from deletion without mutating state.",
            ["POST /api/retention/runs"] = "Queue a retention cleanup run or join the currently active run using saved policies."
        };

    public static int Count => Descriptions.Count;

    public static bool TryGet(string? method, string? relativePath, out OperationDocumentation documentation)
    {
        var path = NormalizePath(relativePath);
        var key = $"{method?.ToUpperInvariant()} {path}";
        if (Descriptions.TryGetValue(key, out var description))
        {
            documentation = new OperationDocumentation(
                key,
                BuildOperationId(method ?? "operation", path),
                ToSummary(description),
                description);
            return true;
        }

        documentation = default;
        return false;
    }

    public static string NormalizePath(string? relativePath)
    {
        var path = relativePath?.Split('?', 2)[0].Trim() ?? string.Empty;
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        path = RouteConstraintRegex().Replace(path, "{$1}");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string ToSummary(string description)
    {
        var end = description.IndexOf('.');
        return end > 0 ? description[..end] : description;
    }

    private static string BuildOperationId(string method, string path)
    {
        if (path == "/")
        {
            return "getApiDocumentation";
        }

        var words = new List<string> { method.ToLowerInvariant() };
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipWhile(s => s == "api"))
        {
            var isParameter = segment.StartsWith('{') && segment.EndsWith('}');
            var value = isParameter ? segment[1..^1] : segment;
            if (isParameter)
            {
                words.Add("By");
            }

            words.AddRange(value.Split('-', StringSplitOptions.RemoveEmptyEntries));
        }

        return words[0] + string.Concat(words.Skip(1).Select(ToPascalCase));
    }

    private static string ToPascalCase(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    [GeneratedRegex(@"\{([^}:]+):[^}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex RouteConstraintRegex();
}

internal readonly record struct OperationDocumentation(
    string Key,
    string OperationId,
    string Summary,
    string Description);
