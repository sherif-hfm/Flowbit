using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// TASK-05C script adapter. It names the instance inputs a body freezes, supplies the
/// staged environment, evaluates NCalc assignments or the sandboxed JavaScript body in
/// a dedicated item context, and hands a candidate or a classified failure to the
/// common item protocol (<c>WorkflowEngineService.AutomaticMultiInstanceItems.cs</c>).
/// It never writes runtime variables or advances a token itself.
/// </summary>
public sealed partial class WorkflowEngineService
{
    /// <summary>The stable code a caught script failure leaves in a boundary's error variable.</summary>
    internal const string AutomaticScriptFailureCode = "script_task_failed";

    /// <summary>
    /// Stage, evaluate, save, and publish one item, in that order and in separate
    /// transactions. No runtime or shared row lock is held while the script runs, a
    /// saved candidate is never evaluated again, and each failure takes exactly one
    /// coordinator exit.
    /// </summary>
    private async Task ProcessAutomaticScriptItemAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        var (context, stagingRefusal, node, definition) = await StageAutomaticItemAsync(lease, fence,
            AutomaticScriptEnvironment, cancellationToken);
        if (stagingRefusal is not null)
        {
            await RefuseAutomaticItemAsync(lease, fence, stagingRefusal, cancellationToken);
            return;
        }

        if (context is not null)
        {
            AutomaticMultiInstanceCandidate candidate;
            try
            {
                candidate = EvaluateAutomaticScriptItem(scriptEvaluator, node, definition, context, cancellationToken);
            }
            catch (AutomaticScriptItemException failure)
            {
                await HandleAutomaticItemFailureAsync(lease, fence, failure.Code,
                    TaskExecutionOutcome.Fail(failure.Message), cancellationToken);
                return;
            }

            var refusal = await SaveAutomaticItemCandidateAsync(lease, fence, candidate, cancellationToken);
            if (refusal == AutomaticMultiInstanceValues.ResultInvalid)
            {
                await HandleAutomaticItemFailureAsync(lease, fence, refusal,
                    TaskExecutionOutcome.Fail($"Script task #{node.Id} item result does not satisfy its declarations."),
                    cancellationToken);
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
    /// The instance variables a body freezes. NCalc freezes the names its assignments,
    /// local default templates, and local validations reference. JavaScript can read any
    /// name, so it freezes every stored instance variable, listed under the instance lock.
    /// Neither freezes the body's own aggregate target, which an item never reads, nor
    /// its collection, which the snapshot already holds once and every item reads from
    /// there.
    /// </summary>
    internal static IReadOnlyList<string> AutomaticScriptInstanceInputNames(FlowNodeModel node,
        WorkflowModel definition, IEnumerable<string> storedNames) =>
        AutomaticInstanceInputNames(node,
            string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal)
                ? storedNames
                : AutomaticScriptReferencedNames(node));

    // Names an NCalc body's expressions and templates reference, context included.
    private static IReadOnlyCollection<string> AutomaticScriptReferencedNames(FlowNodeModel node)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in node.Assignments ?? [])
            names.UnionWith(SequenceFlowConditionEvaluator.ParameterNames(assignment?.Expression));
        AddAutomaticLocalOutputReferences(node, names);
        return names;
    }

    /// <summary>
    /// The config, setting, and system values an item captures when it first stages and
    /// keeps for its retries. NCalc captures only the names it references; JavaScript can
    /// read any name, so every item stores all of them within its 1 MiB input limit.
    /// </summary>
    private Dictionary<string, JsonElement> AutomaticScriptEnvironment(AutomaticItemOwner owner, DateTimeOffset stagedAt) =>
        AutomaticItemEnvironment(owner, stagedAt,
            string.Equals(owner.Node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal)
                ? null
                : AutomaticScriptReferencedNames(owner.Node));

    /// <summary>
    /// Evaluates one item over its frozen context and returns the candidate the coordinator
    /// saves. Writes resolve only to declared locals or writable shared aliases; nothing
    /// else, an ordinary instance variable included, can be written from an item. Throws
    /// <see cref="AutomaticScriptItemException"/> for every authored failure.
    /// </summary>
    internal static AutomaticMultiInstanceCandidate EvaluateAutomaticScriptItem(IScriptEvaluator evaluator,
        FlowNodeModel node, WorkflowModel definition, AutomaticMultiInstanceItemContext context,
        CancellationToken cancellationToken)
    {
        var multi = node.MultiInstance!;
        var outputs = multi.OutputVariables ?? [];
        var overlay = context.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        // An item never reads its own aggregate target, whatever its scope.
        overlay.Remove(multi.ResultVariable ?? string.Empty);
        var writer = new AutomaticScriptItemWriter(node, definition, overlay,
            context.LocalVariables.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase));
        try
        {
            if (string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(node.Script))
                    throw new WorkflowDomainException($"Script task #{node.Id} failed: the JavaScript body is missing.");
                var result = evaluator.Evaluate(node.Script,
                    new AutomaticScriptItemContext(overlay, writer, context.FlowInfo), cancellationToken);
                if (!result.Success)
                    throw new WorkflowDomainException($"Script task #{node.Id} failed: {result.Error}");
            }
            else
            {
                foreach (var assignment in node.Assignments ?? [])
                {
                    if (assignment is null || string.IsNullOrWhiteSpace(assignment.Variable))
                        throw new WorkflowDomainException($"Script task #{node.Id} has an invalid assignment target.");
                    writer.Write(assignment.Variable, SequenceFlowConditionEvaluator.EvaluateValue(
                        assignment.Expression, overlay, flowInfo: context.FlowInfo));
                }
            }
        }
        catch (Exception error) when (error is WorkflowDomainException or FormatException or OverflowException
                                          or InvalidCastException
                                          || error is InvalidOperationException and not AutomaticMultiInstanceValueException)
        {
            // The same failures an ordinary script reports, including coercion errors.
            throw new AutomaticScriptItemException(AutomaticScriptFailureCode, error.Message);
        }

        var validationContext = AutomaticValidationContext(outputs.Where(output => output is not null)
            .Select(output => output.Validation), overlay, writer.Locals, writer.SharedWrites);
        var finalContext = new Dictionary<string, JsonElement>(validationContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in writer.Locals.Concat(writer.SharedWrites)) finalContext[pair.Key] = pair.Value;
        try
        {
            // The same catalog contract publication enforces, checked before saving.
            foreach (var (alias, value) in writer.SharedWrites)
            {
                var binding = writer.Binding(alias);
                SharedVariableValueValidator.Validate(binding.SharedKey ?? binding.Name, binding.DataType,
                    binding.IsArray, binding.Nullable, binding.Validation, value);
            }
            _ = ValidateAutomaticLocalResult(outputs, writer.Locals, finalContext);
        }
        catch (AutomaticMultiInstanceValueException)
        {
            throw new AutomaticScriptItemException(AutomaticMultiInstanceValues.ResultInvalid,
                $"Script task #{node.Id} item left a non-nullable local output without a value.");
        }
        catch (WorkflowDomainException error)
        {
            throw new AutomaticScriptItemException(AutomaticMultiInstanceValues.ResultInvalid, error.Message);
        }

        return new AutomaticMultiInstanceCandidate(
            writer.Locals,
            writer.SharedWrites,
            // Staging fenced every possible producer; the candidate keeps exactly the
            // aliases this item wrote.
            writer.SharedWrites.Keys.ToDictionary(alias => alias,
                alias => context.SharedOutputValueVersions.TryGetValue(alias, out var version)
                    ? version
                    : throw new AutomaticScriptItemException(AutomaticScriptFailureCode,
                        $"Script task #{node.Id} item cannot write '{alias}': it has no output fence."),
                StringComparer.OrdinalIgnoreCase),
            validationContext);
    }

    /// <summary>
    /// Resolves an item's write targets: its declared locals, then writable shared
    /// aliases other than the body's collection, aggregate, and error targets. Every
    /// write is coerced like an ordinary script write, and later reads see it.
    /// </summary>
    private sealed class AutomaticScriptItemWriter
    {
        private readonly FlowNodeModel node;
        private readonly Dictionary<string, JsonElement> overlay;
        private readonly Dictionary<string, VariableModel> locals;
        private readonly Dictionary<string, VariableModel> process;
        private readonly HashSet<string> reservedNames;
        private readonly HashSet<string> reservedKeys;
        private readonly Dictionary<string, string> writtenKeys = new(StringComparer.Ordinal);

        public AutomaticScriptItemWriter(FlowNodeModel node, WorkflowModel definition,
            Dictionary<string, JsonElement> overlay, Dictionary<string, JsonElement> localDefaults)
        {
            this.node = node;
            this.overlay = overlay;
            Locals = localDefaults;
            locals = (node.MultiInstance!.OutputVariables ?? []).Where(output => output is not null)
                .Select(AutomaticMultiInstanceRules.ToVariable)
                .ToDictionary(variable => variable.Name!, StringComparer.OrdinalIgnoreCase);
            process = definition.Variables.Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
                .GroupBy(variable => variable.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            reservedNames = new[] { node.MultiInstance.CollectionVariable, node.MultiInstance.ResultVariable }
                .Concat(definition.FlowNodes.Where(boundary => boundary.AttachedToRef == node.Id
                        && BpmnFlowNodeTypes.IsErrorBoundary(boundary.Type))
                    .Select(boundary => boundary.ErrorVariable))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            reservedKeys = process.Values.Where(variable => reservedNames.Contains(variable.Name!)
                    && variable.Scope == VariableScopes.Shared && variable.SharedKey is not null)
                .Select(variable => variable.SharedKey!).ToHashSet(StringComparer.Ordinal);
        }

        public Dictionary<string, JsonElement> Locals { get; }

        public Dictionary<string, JsonElement> SharedWrites { get; } = new(StringComparer.OrdinalIgnoreCase);

        public VariableModel Binding(string alias) => process[alias];

        public void Write(string? name, object? raw)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new WorkflowDomainException("execution.setVariable requires a variable name.");
            if (locals.TryGetValue(name, out var local))
            {
                var value = CoerceScriptValue(raw, local);
                Locals[local.Name!] = value;
                overlay[local.Name!] = value;
                return;
            }

            var owner = $"Script task #{node.Id} item";
            if (IsAutomaticContextName(name))
                throw new WorkflowDomainException($"{owner} cannot write context value '{name}'.");
            if (!process.TryGetValue(name, out var target))
                throw new WorkflowDomainException(
                    $"{owner} writes '{name}', which is not a declared local output or readWrite shared alias.");
            if (reservedNames.Contains(target.Name!)
                || target.Scope == VariableScopes.Shared && target.SharedKey is not null && reservedKeys.Contains(target.SharedKey))
                throw new WorkflowDomainException(
                    $"{owner} cannot write '{target.Name}'; it is this body's collection, aggregate, or boundary error target.");
            if (target.Scope != VariableScopes.Shared)
                throw new WorkflowDomainException(
                    $"{owner} cannot write instance variable '{target.Name}'; an item writes only its declared local outputs or readWrite shared aliases.");
            if (target.Access != SharedVariableAccessModes.ReadWrite)
                throw new WorkflowDomainException($"{owner} cannot write read-only shared variable '{target.Name}'.");
            if (writtenKeys.TryGetValue(target.SharedKey!, out var other)
                && !string.Equals(other, target.Name, StringComparison.OrdinalIgnoreCase))
                throw new WorkflowDomainException(
                    $"{owner} writes shared key '{target.SharedKey}' through both '{other}' and '{target.Name}'.");
            var coerced = CoerceScriptValue(raw, target);
            writtenKeys[target.SharedKey!] = target.Name!;
            SharedWrites[target.Name!] = coerced;
            overlay[target.Name!] = coerced;
        }
    }

    // The item's IScriptContext. Reads see frozen inputs, staged shared and context
    // values, mi.*, and this item's own writes. JavaScript values cross the bridge as
    // copies, so mutating one changes nothing without an explicit setVariable.
    private sealed class AutomaticScriptItemContext(
        Dictionary<string, JsonElement> overlay,
        AutomaticScriptItemWriter writer,
        SequenceFlowInfoSnapshot? flowInfo) : IScriptContext
    {
        public bool TryGetVariable(string name, out JsonElement value) => overlay.TryGetValue(name, out value);

        public bool HasVariable(string name) => overlay.ContainsKey(name);

        public IReadOnlyDictionary<string, JsonElement> GetVariables() => overlay;

        public SequenceFlowRuntimeSummary GetFlowInfo(int flowId)
        {
            if (flowInfo is null)
                throw new WorkflowDomainException(
                    "execution.getFlowInfo is not available because this workflow definition does not use FlowInfo.");
            if (!flowInfo.TryGetSummary(flowId, out var summary))
                throw new WorkflowDomainException($"execution.getFlowInfo references unknown sequence flow #{flowId}.");
            return summary;
        }

        public void SetVariable(string name, JsonElement rawValue) => writer.Write(name, JsonElementToObject(rawValue));
    }

    /// <summary>An authored item failure: a script error or a result that breaks its declarations.</summary>
    internal sealed class AutomaticScriptItemException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
