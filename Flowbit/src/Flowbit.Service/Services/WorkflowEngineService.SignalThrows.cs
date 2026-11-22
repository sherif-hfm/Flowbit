using System.Globalization;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// Signal throw and end events (EVENT-04B). A throw writes the same broadcast the API
/// writes, inside the step's transaction, through <see cref="SignalBroadcastWriter"/>: it
/// locks no existing row and never waits for a recipient, and a refused throw fails its step.
/// A signal end event throws where its token enters it, through
/// <see cref="ThrowSignalAtEndAsync"/>, which each of the five places that complete a token
/// at an end event calls.
/// </summary>
public sealed partial class WorkflowEngineService
{
    /// <summary>
    /// Throws a signal end event's signal after its token's update is saved and before the
    /// instance's completion is decided, so the instance is still running when its other
    /// waits are frozen. It reads the chain count from the token as its update left it.
    /// Any other end event returns at once.
    /// </summary>
    private async Task ThrowSignalAtEndAsync(
        WorkflowInstanceRecord instance,
        long tokenId,
        FlowNodeModel endNode,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (!BpmnFlowNodeTypes.IsSignalEnd(endNode.Type))
        {
            return;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var token = await runtime.GetExecutionTokenAsync(tokenId, false, cancellationToken)
            ?? throw new WorkflowConflictException("The execution token disappeared while entering a signal end event.");
        var payload = await ThrowSignalFromNodeAsync(instance, token, endNode, definition, actor, cancellationToken);
        await runtime.AddTokenHistoryAsync(
            instance.Id,
            token.Id,
            null,
            endNode.Id,
            endNode.Id,
            actor.User,
            payload,
            InstanceHistoryNotes.SignalThrown,
            cancellationToken,
            actor.ActingFor,
            actor.DelegationId,
            actorClaims: actor.AuditClaims);
    }

    /// <summary>
    /// Throws the signal of a throw or end event and returns the history payload of the
    /// row that records it. Everything that can be checked is checked before the first
    /// write: the chain count, the scope, the payload, and the budget of unfinished
    /// broadcasts. The recipient limit is known only after the freeze, and the writer then
    /// deletes what it wrote before this refuses, so a caller that catches the refusal
    /// cannot commit part of a broadcast.
    /// </summary>
    private async Task<Dictionary<string, JsonElement>> ThrowSignalFromNodeAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (signalBroadcastWriter is null || signalBroadcasts is null)
        {
            throw new WorkflowDomainException("Signal persistence is unavailable.");
        }

        var label = SignalEventRules.Label(node);
        var signal = node.Signal;
        if (signal is null || !SignalEventRules.IsValidName(signal.Name) || signal.Scope is null)
        {
            // Validation rejects such a definition; one inserted without it stops here.
            throw new WorkflowDomainException($"{label} has no valid signal name and scope.");
        }

        // 1. Guard: a chain of automatic steps and signals with nothing external between them.
        var configuredGuard = await engineSettings.GetByKeyAsync(
            WorkflowAutomaticActivationGuard.SettingKey,
            cancellationToken);
        var decision = WorkflowAutomaticActivationGuard.EvaluateNext(
            token.AutomaticActivationCount,
            configuredGuard?.Value);
        if (decision.ShouldOpenIncident)
        {
            Refuse(
                instance, node, SignalRuntimeTelemetry.RefusalCauses.ChainLimit,
                $"{label} was not thrown: its chain of automatic steps and signals reached "
                + $"{WorkflowAutomaticActivationGuard.SettingKey} ({decision.Limit}).");
        }

        // 2. Flush, so the raw freeze statement sees the step's tracked changes, such as a
        // subscription created earlier in the same routing pass.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // 3 and 4. Scope and payload, from stored instance variables only: never the
        // routing overlay, which can hold shared values of earlier nodes.
        var entries = (signal.Payload ?? []).OfType<SignalPayloadEntryModel>().ToArray();
        Dictionary<string, JsonElement>? variables = null;
        if (entries.Any(entry => !string.IsNullOrEmpty(entry.Variable))
            || !string.IsNullOrEmpty(signal.Scope.BusinessKeyVariable))
        {
            variables = await LoadInstanceVariablesAsync(instance.Id, cancellationToken);
        }

        var scope = BuildThrowScope(instance, node, label, signal.Scope, variables);
        var payloadJson = BuildThrowPayload(instance, node, label, entries, variables);

        // 5. Budget: a soft threshold counted without a lock, as the API's is.
        var maxOpen = await ReadSignalLimitAsync(
            SignalSettings.MaxOpenWorkflowBroadcastsKey,
            SignalSettings.DefaultMaxOpenWorkflowBroadcasts,
            SignalSettings.MaxOpenWorkflowBroadcastsCap,
            cancellationToken);
        if (await signalBroadcasts.CountOpenAsync(SignalSettings.WorkflowThrowerKinds, maxOpen, cancellationToken)
            >= maxOpen)
        {
            Refuse(
                instance, node, SignalRuntimeTelemetry.RefusalCauses.OpenBroadcasts,
                $"{label} was not thrown: {maxOpen} signal broadcasts thrown by workflows are still delivering; "
                + $"see {SignalSettings.MaxOpenWorkflowBroadcastsKey}.");
        }

        // 6. Write, in this transaction.
        var maxRecipients = await ReadSignalLimitAsync(
            SignalSettings.MaxSubscriptionsPerBroadcastKey,
            SignalSettings.DefaultMaxSubscriptionsPerBroadcast,
            SignalSettings.MaxSubscriptionsPerBroadcastCap,
            cancellationToken);
        var thrower = instance.Id.ToString(CultureInfo.InvariantCulture);
        var written = await signalBroadcastWriter.WriteAsync(
            new SignalBroadcastWrite(
                new NewSignalBroadcastRecord(
                    signal.Name,
                    scope,
                    payloadJson,
                    SignalThrowerKinds.Instance,
                    thrower,
                    "[]",
                    null,
                    null,
                    null,
                    timeProvider.GetUtcNow(),
                    instance.Id,
                    node.Id,
                    node.Name,
                    decision.AttemptedCount),
                maxRecipients,
                thrower),
            cancellationToken);
        if (written.RecipientLimitExceeded)
        {
            Refuse(
                instance, node, SignalRuntimeTelemetry.RefusalCauses.Recipients,
                $"{label} would reach more than {maxRecipients} waiting subscriptions; "
                + "narrow its scope or raise Signals.MaxSubscriptionsPerBroadcast.");
        }

        // 7. Record, once the step commits: a step that rolls back or is retried threw
        // nothing. Neither the log nor the history payload holds a business key or a
        // payload value.
        unitOfWork.AfterCommit(() =>
        {
            SignalRuntimeTelemetry.RecordBroadcast(SignalThrowerKinds.Instance, written.RecipientCount);
            logger.LogInformation(
                "Instance {InstanceId} node {NodeId} threw signal broadcast {BroadcastId} to {RecipientCount} recipients.",
                instance.Id, node.Id, written.BroadcastId, written.RecipientCount);
        });
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["broadcastId"] = JsonSerializer.SerializeToElement(written.BroadcastId),
            ["signalName"] = JsonSerializer.SerializeToElement(signal.Name),
            ["eventNodeId"] = JsonSerializer.SerializeToElement(node.Id),
            ["eventKind"] = JsonSerializer.SerializeToElement(
                BpmnFlowNodeTypes.IsSignalEnd(node.Type) ? "end" : "throw"),
            ["scopeKind"] = JsonSerializer.SerializeToElement(signal.Scope.Kind),
            ["workflowKey"] = JsonSerializer.SerializeToElement(scope.WorkflowKey),
            ["recipientCount"] = JsonSerializer.SerializeToElement(written.RecipientCount)
        };
    }

    private SignalBroadcastScope BuildThrowScope(
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        string label,
        SignalThrowScopeModel scope,
        IReadOnlyDictionary<string, JsonElement>? variables)
    {
        switch (scope.Kind)
        {
            case SignalThrowScopeKinds.ThisInstance:
                return SignalBroadcastScope.ForInstance(instance.Id).WithWorkflowKey(instance.WorkflowKey);
            case SignalThrowScopeKinds.ThisWorkflow:
                return SignalBroadcastScope.Family(instance.WorkflowKey);
            case SignalThrowScopeKinds.Workflow when !string.IsNullOrWhiteSpace(scope.WorkflowKey):
                return SignalBroadcastScope.Family(scope.WorkflowKey);
            case SignalThrowScopeKinds.AllWorkflows:
                return SignalBroadcastScope.AllWorkflows;
            case SignalThrowScopeKinds.OwnBusinessKey when !string.IsNullOrWhiteSpace(scope.WorkflowKey):
                if (instance.BusinessKey is null)
                {
                    Refuse(
                        instance, node, SignalRuntimeTelemetry.RefusalCauses.BusinessKey,
                        $"{label} uses this instance's business key, but this instance has none.");
                }

                return SignalBroadcastScope.ForBusinessKey(scope.WorkflowKey, instance.BusinessKey!);
            case SignalThrowScopeKinds.BusinessKey
                when !string.IsNullOrWhiteSpace(scope.WorkflowKey)
                     && !string.IsNullOrWhiteSpace(scope.BusinessKeyVariable):
                var name = scope.BusinessKeyVariable;
                string? cause = null;
                var key = string.Empty;
                if (variables is null || !variables.TryGetValue(name, out var value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    cause = "it has no value";
                }
                else if (value.ValueKind != JsonValueKind.String)
                {
                    cause = "it is not text";
                }
                else
                {
                    switch (BusinessKeyRules.Normalize(value.GetString(), out key))
                    {
                        case BusinessKeyProblem.Blank:
                            cause = "it is blank";
                            break;
                        case BusinessKeyProblem.TooLong:
                            cause = $"it is longer than {BusinessKeyRules.MaxLength} characters";
                            break;
                    }
                }

                if (cause is not null)
                {
                    Refuse(
                        instance, node, SignalRuntimeTelemetry.RefusalCauses.BusinessKey,
                        $"{label} cannot read a business key from variable '{name}': {cause}.");
                }

                return SignalBroadcastScope.ForBusinessKey(scope.WorkflowKey, key);
            default:
                throw new WorkflowDomainException($"{label} has no valid signal scope.");
        }
    }

    private string? BuildThrowPayload(
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        string label,
        IReadOnlyList<SignalPayloadEntryModel> entries,
        IReadOnlyDictionary<string, JsonElement>? variables)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var entry in entries)
            {
                var name = entry.Name ?? string.Empty;
                if (string.Equals(entry.Source, SignalPayloadSources.InstanceId, StringComparison.Ordinal))
                {
                    writer.WriteNumber(name, instance.Id);
                    continue;
                }

                if (string.Equals(entry.Source, SignalPayloadSources.BusinessKey, StringComparison.Ordinal))
                {
                    if (instance.BusinessKey is { } businessKey)
                    {
                        writer.WriteString(name, businessKey);
                    }
                    else if (entry.Required)
                    {
                        Refuse(
                            instance, node, SignalRuntimeTelemetry.RefusalCauses.BusinessKey,
                            $"{label} uses this instance's business key, but this instance has none.");
                    }

                    continue;
                }

                var variable = entry.Variable ?? string.Empty;
                JsonElement stored = default;
                var found = variables is not null && variables.TryGetValue(variable, out stored);
                if (!found || stored.ValueKind == JsonValueKind.Undefined)
                {
                    if (entry.Required)
                    {
                        Refuse(
                            instance, node, SignalRuntimeTelemetry.RefusalCauses.Payload,
                            $"{label} cannot send payload entry '{name}': variable '{variable}' has no value.");
                    }

                    continue;
                }

                if (stored.ValueKind == JsonValueKind.Null && entry.Required)
                {
                    Refuse(
                        instance, node, SignalRuntimeTelemetry.RefusalCauses.Payload,
                        $"{label} cannot send payload entry '{name}': variable '{variable}' is null.");
                }

                writer.WritePropertyName(name);
                stored.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        var bytes = JsonbText.ByteCount(document.RootElement);
        if (bytes > SignalConstraints.MaxPayloadBytes)
        {
            Refuse(
                instance, node, SignalRuntimeTelemetry.RefusalCauses.PayloadSize,
                $"{label} payload is {bytes} bytes; the limit is {SignalConstraints.MaxPayloadBytes}.");
        }

        return document.RootElement.GetRawText();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Refuse(WorkflowInstanceRecord instance, FlowNodeModel node, string cause, string message)
    {
        SignalRuntimeTelemetry.RecordThrowRefusal(cause);
        logger.LogWarning(
            "Instance {InstanceId} node {NodeId} did not throw its signal: {Cause}.",
            instance.Id, node.Id, cause);
        throw new WorkflowDomainException(message);
    }

    private async Task<int> ReadSignalLimitAsync(string key, int fallback, int cap, CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(key, cancellationToken);
        return SignalSettings.ParseLimit(setting?.Value, fallback, cap);
    }
}
