using Flowbit.Shared.Models;

namespace Flowbit.Tests;

/// <summary>
/// Link-event definitions and the ordered problems both the server's <see cref="LinkEventRules"/> and the
/// editor's <c>validateSaveLinkEvents</c> must report for them, with the same wording, so the two cannot drift.
/// Every case starts from <see cref="Base"/>: start #1, review #2, throw #3 and catch #4 named "Fulfilment",
/// and end #5, joined by flows 101, 201 (review to throw), and 401 (catch to end).
/// </summary>
internal static class LinkEventFixtures
{
    public const string Name = "Fulfilment";

    public static WorkflowModel Base(string throwName = Name, string catchName = Name) => new()
    {
        Id = $"link-fixture-{Guid.NewGuid():N}",
        Name = "Link fixture",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = throwName, Type = BpmnFlowNodeTypes.IntermediateLinkThrowEvent },
            new FlowNodeModel { Id = 4, Name = catchName, Type = BpmnFlowNodeTypes.IntermediateLinkCatchEvent },
            new FlowNodeModel { Id = 5, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Send", SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
        ]
    };

    /// <summary>(case, definition, problems in order). An empty list means the links are valid.</summary>
    public static IEnumerable<(string Case, WorkflowModel Model, string[] Problems)> Cases()
    {
        yield return ("one throw and its catch", Base(), []);

        var shared = Base();
        shared.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = Name, Type = BpmnFlowNodeTypes.IntermediateLinkThrowEvent });
        shared.SequenceFlows.Add(new SequenceFlowModel { Id = 202, Name = "Also send", SourceRef = 2, TargetRef = 6 });
        yield return ("two throws share one catch", shared, []);

        // .NET Trim() removes U+0085 and the Unicode spaces, and keeps the U+FEFF byte-order mark.
        yield return ("names are trimmed as .NET trims them",
            Base("\u00A0Fulfilment\u2028", "\u0085Fulfilment\t \u3000"), []);
        yield return ("a byte-order mark is not trimmed",
            Base("\uFEFFFulfilment"),
            [
                "Link throw event #3 has no link catch event named '\uFEFFFulfilment'.",
                "Link catch event #4 has no link throw event named 'Fulfilment'."
            ]);

        yield return ("a throw that matches only ignoring case",
            Base("fulfilment"),
            [
                "Link throw event #3 is named 'fulfilment', but link catch event #4 is named 'Fulfilment'; link names must match exactly, including letter case.",
                "Link catch event #4 has no link throw event named 'Fulfilment'."
            ]);
        yield return ("non-ASCII names compare by ordinal case",
            Base("Ωmega Ñandú", "ωMEGA ñANDÚ"),
            [
                "Link throw event #3 is named 'Ωmega Ñandú', but link catch event #4 is named 'ωMEGA ñANDÚ'; link names must match exactly, including letter case.",
                "Link catch event #4 has no link throw event named 'ωMEGA ñANDÚ'."
            ]);
        yield return ("a sharp s is not a case variant of SS",
            Base("Straße", "STRASSE"),
            [
                "Link throw event #3 has no link catch event named 'Straße'.",
                "Link catch event #4 has no link throw event named 'STRASSE'."
            ]);
        yield return ("identical non-ASCII names pair", Base("Übergabe → Lager", "Übergabe → Lager"), []);

        var duplicate = Base();
        duplicate.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = " FULFILMENT ", Type = BpmnFlowNodeTypes.IntermediateLinkCatchEvent });
        duplicate.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 5 });
        yield return ("two catches that differ only in case",
            duplicate,
            [
                "Link catch event #6 has the same name as link catch event #4 ('Fulfilment'); link catch names must be unique, ignoring letter case.",
                "Link catch event #6 has no link throw event named 'FULFILMENT'."
            ]);

        var exactDuplicate = Base();
        exactDuplicate.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = Name, Type = BpmnFlowNodeTypes.IntermediateLinkCatchEvent });
        exactDuplicate.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 5 });
        yield return ("two catches with one name",
            exactDuplicate,
            ["Link catch event #6 has the same name as link catch event #4 ('Fulfilment'); link catch names must be unique, ignoring letter case."]);

        yield return ("a throw without a catch",
            Base(catchName: "Archive"),
            [
                "Link throw event #3 has no link catch event named 'Fulfilment'.",
                "Link catch event #4 has no link throw event named 'Archive'."
            ]);

        yield return ("a blank link name",
            Base(throwName: " \t "),
            [
                "Flow node #3 name is required.",
                "Link catch event #4 has no link throw event named 'Fulfilment'."
            ]);

        var throwTopology = Base();
        throwTopology.SequenceFlows.RemoveAll(flow => flow.Id == 201);
        throwTopology.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 });
        yield return ("a throw without an incoming flow and with an outgoing flow",
            throwTopology,
            [
                "Link throw event #3 must have at least one incoming sequence flow.",
                "Link throw event #3 cannot have outgoing sequence flows."
            ]);

        var catchTopology = Base();
        catchTopology.SequenceFlows.RemoveAll(flow => flow.Id == 401);
        catchTopology.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 });
        yield return ("a catch with an incoming flow and without an outgoing flow",
            catchTopology,
            [
                "Link catch event #4 cannot have incoming sequence flows.",
                "Link catch event #4 must have exactly one outgoing sequence flow."
            ]);

        var twoOutgoing = Base();
        twoOutgoing.SequenceFlows.Add(new SequenceFlowModel { Id = 402, SourceRef = 4, TargetRef = 5 });
        yield return ("a catch with two outgoing flows",
            twoOutgoing,
            ["Link catch event #4 must have exactly one outgoing sequence flow."]);

        foreach (var (setting, apply) in RoutingSettings())
        {
            var routed = Base();
            apply(routed.SequenceFlows.Single(flow => flow.Id == 401));
            yield return ($"a catch flow with {setting}",
                routed,
                ["Link catch event #4 must have one unconditional outgoing sequence flow without user-action or multi-instance metadata."]);
        }

        var catchFirst = Base();
        catchFirst.FlowNodes = [.. catchFirst.FlowNodes.OrderByDescending(node => node.Type == BpmnFlowNodeTypes.IntermediateLinkCatchEvent)];
        catchFirst.FlowNodes.Single(node => node.Id == 3).Name = "Elsewhere";
        yield return ("problems follow node order",
            catchFirst,
            [
                "Link catch event #4 has no link throw event named 'Fulfilment'.",
                "Link throw event #3 has no link catch event named 'Elsewhere'."
            ]);
    }

    public static IEnumerable<(string Setting, Action<SequenceFlowModel> Apply)> RoutingSettings()
    {
        yield return ("a condition", flow => flow.Condition = "true");
        yield return ("a default marker", flow => flow.IsDefault = true);
        yield return ("a condition priority", flow => flow.ConditionPriority = 1);
        yield return ("roles", flow => flow.Roles = ["Clerk"]);
        yield return ("variables", flow => flow.Variables = [new VariableModel { Id = 1, Name = "note", DataType = WorkflowVariableTypes.String }]);
        yield return ("a claim bypass", flow => flow.CanActWithoutClaim = true);
        yield return ("isSelectable=false", flow => flow.IsSelectable = false);
        yield return ("a completion condition", flow => flow.CompletionCondition = "true");
        yield return ("a completion priority", flow => flow.CompletionPriority = 1);
        yield return ("cancelRemainingInstances", flow => flow.CancelRemainingInstances = true);
    }

    /// <summary>
    /// The same definition with each resolved link replaced by two automatic tasks joined by a new flow,
    /// which every graph analysis must treat exactly as it treats the link.
    /// </summary>
    public static WorkflowModel WithoutLinks(WorkflowModel model)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<WorkflowModel>(
            System.Text.Json.JsonSerializer.Serialize(model))!;
        var edges = LinkEventRules.ImplicitEdges(copy);
        var nextFlowId = copy.SequenceFlows.Max(flow => flow.Id) + 1;
        foreach (var node in copy.FlowNodes.Where(node => BpmnFlowNodeTypes.IsLinkEvent(node.Type)))
        {
            node.Type = BpmnFlowNodeTypes.Task;
        }
        foreach (var edge in edges)
        {
            copy.SequenceFlows.Add(new SequenceFlowModel { Id = nextFlowId++, SourceRef = edge.ThrowId, TargetRef = edge.CatchId });
        }
        return copy;
    }
}
