using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// TASK-05B REST service adapter. It names the instance inputs a body freezes, renders
/// one item's request from its saved context, calls the existing invoker with no lock
/// held, maps a 2xx response in memory into a candidate of item-local outputs and
/// shared writes, and hands that candidate or a classified failure to the common item
/// protocol (<c>WorkflowEngineService.AutomaticMultiInstanceItems.cs</c>). The saved
/// candidate is the only durable record of a success: the job's result-ready row is
/// never used for an item. It never writes runtime variables or advances a token.
/// </summary>
public sealed partial class WorkflowEngineService
{
    /// <summary>
    /// The stable code a caught REST failure (template, transport, timeout, or non-2xx)
    /// leaves in a boundary's error variable. Mapping and output-contract failures use
    /// <see cref="AutomaticMultiInstanceValues.ResultInvalid"/>.
    /// </summary>
    internal const string AutomaticServiceFailureCode = "service_task_failed";

    /// <summary>
    /// Stage, invoke, map, save, and publish one item, in that order. The staging and
    /// saving transactions commit before the next phase starts, so no runtime or shared
    /// row lock is held during the HTTP call, and an item with a saved candidate never
    /// sends its request again. A crash between the call and the save sends it again
    /// with the same <c>sys.jobId</c>: REST effects are at least once.
    /// </summary>
    private async Task ProcessAutomaticServiceItemAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        var (context, stagingRefusal, node, definition) = await StageAutomaticItemAsync(lease, fence,
            AutomaticServiceEnvironment, cancellationToken);
        if (stagingRefusal is not null)
        {
            await RefuseAutomaticItemAsync(lease, fence, stagingRefusal, cancellationToken);
            return;
        }

        if (context is not null)
        {
            var result = await InvokeAutomaticServiceItemAsync(node, context, cancellationToken);
            var status = SelectableHttpStatus(result);
            if (!result.IsSuccess)
            {
                logger.LogWarning(
                    "Service task #{NodeId} item job {JobId} failed with HTTP {StatusCode}.",
                    node.Id, lease.Job.Id, result.StatusCode);
                var reason = result.Error ?? $"HTTP status {result.StatusCode}";
                await HandleAutomaticItemFailureAsync(lease, fence, AutomaticServiceFailureCode,
                    TaskExecutionOutcome.Fail($"Service task #{node.Id} REST call failed ({reason}).", status),
                    cancellationToken, AutomaticServiceFailureDescription(result));
                return;
            }

            AutomaticMultiInstanceCandidate candidate;
            try
            {
                candidate = MapAutomaticServiceItem(node, definition, context, result);
            }
            catch (AutomaticServiceItemException failure)
            {
                // A 2xx whose mapping fails selects its boundary by that status, as an
                // ordinary REST mapping failure does. Nothing was saved, so a retry
                // sends the request again.
                await HandleAutomaticItemFailureAsync(lease, fence, AutomaticMultiInstanceValues.ResultInvalid,
                    TaskExecutionOutcome.Fail(failure.Message, status), cancellationToken,
                    AutomaticServiceMappingFailureDescription(result));
                return;
            }

            var refusal = await SaveAutomaticItemCandidateAsync(lease, fence, candidate, cancellationToken);
            if (refusal == AutomaticMultiInstanceValues.ResultInvalid)
            {
                await HandleAutomaticItemFailureAsync(lease, fence, refusal,
                    TaskExecutionOutcome.Fail($"Service task #{node.Id} item result does not satisfy its declarations.", status),
                    cancellationToken, AutomaticServiceMappingFailureDescription(result));
                return;
            }
            if (refusal is not null)
            {
                await RefuseAutomaticItemAsync(lease, fence, refusal, cancellationToken);
                return;
            }
        }

        await PublishAutomaticItemAsync(lease, fence, cancellationToken);
    }

    /// <summary>
    /// What a caught REST failure records on its item and node visit: the HTTP status, or
    /// that no response arrived. Never the error text, which can echo request or response
    /// data; uncaught failures keep that on the job's attempts and incident instead.
    /// </summary>
    internal static string AutomaticServiceFailureDescription(ServiceTaskResult result) =>
        result.StatusCode > 0
            ? $"The REST call failed with HTTP status {result.StatusCode}."
            : "The REST call got no HTTP response: it timed out, failed in transport, or its request could not be rendered.";

    internal static string AutomaticServiceMappingFailureDescription(ServiceTaskResult result) =>
        $"The REST call returned HTTP status {result.StatusCode}, but the response did not satisfy the output mappings.";

    /// <summary>
    /// Renders the request from the item's saved context on every attempt, exactly as an
    /// ordinary durable job re-renders from its snapshot, and calls the existing invoker.
    /// A template that cannot render, an exception, or a transport failure becomes a
    /// failed result with status 0, which only a catch-all boundary matches.
    /// </summary>
    private async Task<ServiceTaskResult> InvokeAutomaticServiceItemAsync(FlowNodeModel node,
        AutomaticMultiInstanceItemContext context, CancellationToken cancellationToken)
    {
        StagedServiceInvocation invocation;
        try
        {
            invocation = RenderAutomaticServiceItemRequest(node, context);
        }
        catch (WorkflowDomainException ex)
        {
            return new ServiceTaskResult(false, 0, null, LimitJobFailure(ex.Message));
        }

        try
        {
            return await serviceTaskInvoker.InvokeAsync(new ServiceTaskRequest(
                invocation.Method, invocation.Url, invocation.Headers, invocation.Body, invocation.TimeoutSeconds),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ServiceTaskResult(false, 0, null, LimitJobFailure(ex.Message));
        }
    }

    /// <summary>
    /// The item's request: the URL and header templates substitute strictly, so a
    /// placeholder with no value fails the item; the body substitutes permissively, as
    /// an ordinary REST task does. An item never reads its own aggregate target.
    /// </summary>
    private static StagedServiceInvocation RenderAutomaticServiceItemRequest(FlowNodeModel node,
        AutomaticMultiInstanceItemContext context) =>
        BuildStagedServiceInvocation(node, AutomaticServiceItemOverlay(node, context));

    private static Dictionary<string, JsonElement> AutomaticServiceItemOverlay(FlowNodeModel node,
        AutomaticMultiInstanceItemContext context)
    {
        var overlay = context.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        overlay.Remove(node.MultiInstance!.ResultVariable ?? string.Empty);
        return overlay;
    }

    /// <summary>
    /// Maps one 2xx response into the candidate the coordinator saves, with the same path
    /// extraction, defaults, strict types, and validation order as an ordinary REST task.
    /// Targets resolve to the body's local outputs, then to writable shared aliases; the
    /// status target receives this response's status before any validation runs. Throws
    /// <see cref="AutomaticServiceItemException"/> for every authored output failure, so
    /// no partial batch is ever saved.
    /// </summary>
    internal static AutomaticMultiInstanceCandidate MapAutomaticServiceItem(FlowNodeModel node,
        WorkflowModel definition, AutomaticMultiInstanceItemContext context, ServiceTaskResult result)
    {
        var service = node.Service!;
        var multi = node.MultiInstance!;
        var owner = $"Service task #{node.Id} item";
        var outputs = (multi.OutputVariables ?? []).Where(output => output is not null).ToList();
        var locals = outputs.Select(AutomaticMultiInstanceRules.ToVariable)
            .ToDictionary(variable => variable.Name!, StringComparer.OrdinalIgnoreCase);
        var shared = definition.Variables
            .Where(variable => variable.Scope == VariableScopes.Shared && !string.IsNullOrWhiteSpace(variable.Name))
            .GroupBy(variable => variable.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var reserved = new[] { multi.CollectionVariable, multi.ResultVariable }
            .Concat(ErrorBoundaryMatchRules.GetErrorBoundaries(definition, node.Id).Select(boundary => boundary.ErrorVariable))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Validation refuses any other target at save; this guards an older definition.
        var targets = new List<VariableModel>();
        var targetNames = service.OutputMappings.Where(mapping => mapping is not null).Select(mapping => mapping.Variable)
            .Concat(string.IsNullOrWhiteSpace(service.StatusVariable) ? [] : [service.StatusVariable]);
        foreach (var name in targetNames)
        {
            if (locals.TryGetValue(name, out var local))
            {
                targets.Add(local);
            }
            else if (shared.TryGetValue(name, out var binding) && binding.Access == SharedVariableAccessModes.ReadWrite
                     && !reserved.Contains(binding.Name!))
            {
                targets.Add(binding);
            }
            else
            {
                throw new AutomaticServiceItemException(
                    $"{owner} cannot write '{name}'; an item writes only its declared local outputs or readWrite shared aliases.");
            }
        }

        var overlay = AutomaticServiceItemOverlay(node, context);
        JsonElement? statusValue = string.IsNullOrWhiteSpace(service.StatusVariable)
            ? null
            : JsonSerializer.SerializeToElement(result.StatusCode);
        if (statusValue is { } status) overlay[service.StatusVariable!] = status;

        JsonDocument? document = null;
        try
        {
            if (service.OutputMappings.Count > 0 && !string.IsNullOrWhiteSpace(result.Body))
                document = JsonDocument.Parse(result.Body);
        }
        catch (JsonException)
        {
            throw new AutomaticServiceItemException($"Service task #{node.Id} response body was not valid JSON.");
        }

        Dictionary<string, JsonElement> values;
        using (document)
        {
            try
            {
                values = ResolveTypedOutputs(node.Id, "Service task",
                    service.OutputMappings.Select(mapping => ToTypedOutputRuntime(mapping, targets)).ToList(),
                    targets, document?.RootElement, overlay);
            }
            catch (Exception error) when (error is WorkflowDomainException or FormatException or InvalidCastException)
            {
                throw new AutomaticServiceItemException(error.Message);
            }
            values = values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        // Exactly what the mappings filled; a local output a mapping left alone keeps
        // its default, which no mapping validation accepted.
        var mappedTargets = values.Keys
            .Select(name => locals.TryGetValue(name, out var local) ? local.Name! : shared[name].Name!)
            .ToArray();
        if (statusValue is { } statusCode) values[service.StatusVariable!] = statusCode;

        var itemLocals = context.LocalVariables.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        var sharedWrites = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (locals.TryGetValue(name, out var local)) itemLocals[local.Name!] = value;
            else sharedWrites[shared[name].Name!] = value;
        }

        var validationContext = AutomaticValidationContext(
            outputs.Select(output => output.Validation)
                .Concat(service.OutputMappings.Where(mapping => mapping is not null).Select(mapping => mapping.Validation)),
            overlay, itemLocals, sharedWrites);
        var finalContext = new Dictionary<string, JsonElement>(validationContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in itemLocals.Concat(sharedWrites)) finalContext[pair.Key] = pair.Value;
        try
        {
            // The same catalog contract publication enforces, checked before saving.
            foreach (var (alias, value) in sharedWrites)
            {
                var binding = shared[alias];
                SharedVariableValueValidator.Validate(binding.SharedKey ?? binding.Name, binding.DataType,
                    binding.IsArray, binding.Nullable, binding.Validation, value);
            }
            _ = ValidateAutomaticLocalResult(outputs, itemLocals, finalContext);
        }
        catch (AutomaticMultiInstanceValueException)
        {
            throw new AutomaticServiceItemException($"{owner} left a non-nullable local output without a value.");
        }
        catch (WorkflowDomainException error)
        {
            throw new AutomaticServiceItemException(error.Message);
        }

        return new AutomaticMultiInstanceCandidate(
            itemLocals,
            sharedWrites,
            // Staging fenced every possible producer; the candidate keeps exactly the
            // aliases this item writes.
            sharedWrites.Keys.ToDictionary(alias => alias,
                alias => context.SharedOutputValueVersions.TryGetValue(alias, out var version)
                    ? version
                    : throw new AutomaticServiceItemException($"{owner} cannot write '{alias}': it has no output fence."),
                StringComparer.OrdinalIgnoreCase),
            validationContext,
            mappedTargets);
    }

    /// <summary>
    /// The instance variables a REST body freezes: the names its URL, header, and body
    /// placeholders, its mapping default templates and validations, and its local output
    /// defaults and validations reference. Names are exact, never matched by text.
    /// </summary>
    internal static IReadOnlyList<string> AutomaticServiceInstanceInputNames(FlowNodeModel node,
        WorkflowModel definition, IEnumerable<string> storedNames) =>
        AutomaticInstanceInputNames(node, AutomaticServiceReferencedNames(node));

    // Names a REST body's templates and expressions reference, context included.
    internal static IReadOnlyCollection<string> AutomaticServiceReferencedNames(FlowNodeModel node)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node.Service is { } service)
        {
            names.UnionWith(ServiceTaskTemplating.GetPlaceholderNames(
                service.Headers.Where(header => header is not null).Select(header => header.Value)
                    .Prepend(service.Body).Prepend(service.Url).ToArray()));
            foreach (var mapping in service.OutputMappings)
            {
                if (mapping is null) continue;
                names.UnionWith(SequenceFlowConditionEvaluator.ParameterNames(mapping.Validation));
                names.UnionWith(ServiceTaskTemplating.GetPlaceholderNames(DefaultTemplates(mapping.DefaultValue)));
            }
        }
        AddAutomaticLocalOutputReferences(node, names);
        return names;
    }

    /// <summary>
    /// The config, setting, and system values a REST item captures when it first stages
    /// and keeps for its retries: only the names its templates and expressions reference.
    /// A credential rotated after this item staged does not reach its retries.
    /// </summary>
    private Dictionary<string, JsonElement> AutomaticServiceEnvironment(AutomaticItemOwner owner, DateTimeOffset stagedAt) =>
        AutomaticItemEnvironment(owner, stagedAt, AutomaticServiceReferencedNames(owner.Node));

    /// <summary>An authored REST item output failure: a mapping or a result that breaks its declarations.</summary>
    internal sealed class AutomaticServiceItemException(string message) : Exception(message);
}
