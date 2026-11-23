namespace Flowbit.Shared.Models;

/// <summary>
/// The per-type facts of an entry event: the noun its messages start with, and where it
/// declares its start variables. A plain start declares them as <c>variables</c>; a message
/// start and a signal start declare them as typed output mappings, under <c>message</c> and
/// <c>signal</c>; a timer start declares none. Validation and the engine use this one place
/// instead of a branch per entry type.
/// </summary>
public static class EntryEventRules
{
    /// <summary>The noun of an entry event, without its id, such as "Message start event".</summary>
    public static string Kind(string type) =>
        BpmnFlowNodeTypes.IsMessageStart(type)
            ? "Message start event"
            : BpmnFlowNodeTypes.IsTimerStart(type)
                ? "Timer start event"
                : BpmnFlowNodeTypes.IsSignalStart(type)
                    ? "Signal start event"
                    : "Start event";

    /// <summary>The label every message about one entry event starts with.</summary>
    public static string Label(FlowNodeModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return $"{Kind(node.Type)} #{node.Id}";
    }

    /// <summary>Whether the entry declares its start variables as typed output mappings.</summary>
    public static bool DeclaresStartMappings(string type) =>
        BpmnFlowNodeTypes.IsMessageStart(type) || BpmnFlowNodeTypes.IsSignalStart(type);

    /// <summary>
    /// The typed output mappings that declare an entry's start variables, or an empty list
    /// for an entry that declares them as <c>variables</c>. Null entries are kept, so
    /// validation can report them.
    /// </summary>
    public static IReadOnlyList<MessageOutputMappingModel> StartMappings(FlowNodeModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return BpmnFlowNodeTypes.IsMessageStart(node.Type)
            ? node.Message?.OutputMappings ?? []
            : BpmnFlowNodeTypes.IsSignalStart(node.Type)
                ? node.Signal?.OutputMappings ?? []
                : [];
    }

    /// <summary>
    /// The names of the start variables an entry declares. A declaration without a name
    /// declares none; validation reports it with the entry's other variable rules.
    /// </summary>
    public static IEnumerable<string> DeclaredNames(FlowNodeModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var names = DeclaresStartMappings(node.Type)
            ? StartMappings(node).Where(mapping => mapping is not null).Select(mapping => mapping.Variable)
            : (node.Variables ?? []).Where(variable => variable is not null).Select(variable => variable.Name);
        return names.Where(name => name is not null);
    }
}
