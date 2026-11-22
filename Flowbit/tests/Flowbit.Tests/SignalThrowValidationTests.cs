using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The server's rules for signal throw and end events: the shared <see cref="SignalThrowFixtures"/>, the strict scope
/// and payload models, the settings only the API rejects, and normalization.
/// </summary>
public sealed class SignalThrowValidationTests
{
    public static TheoryData<string> CaseNames() => new(SignalThrowFixtures.Cases().Select(item => item.Case));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Server_ReportsEachFixtureWithItsFirstMessage(string name)
    {
        var (_, json, problem) = SignalThrowFixtures.Cases().Single(item => item.Case == name);

        Assert.Equal(problem, ServerError(json));
    }

    [Fact]
    public void Fixtures_HaveUniqueNames()
    {
        var names = SignalThrowFixtures.Cases().Select(item => item.Case).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("scope", "businesKeyVariable")]
    [InlineData("scope", "key")]
    [InlineData("payload", "requird")]
    public void UnknownMembers_FailBindingWithTheirPath(string member, string unknown)
    {
        var json = SignalThrowFixtures.Json(model =>
        {
            if (member == "scope")
            {
                SignalThrowFixtures.Signal(model, 12)["scope"]![unknown] = "x";
            }
            else
            {
                SignalThrowFixtures.Payload(model, 7)[0]![unknown] = true;
            }
        });

        var error = Assert.Throws<JsonException>(() => Deserialize(json));
        Assert.Contains(unknown, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongJsonTypes_FailBinding()
    {
        Assert.Throws<JsonException>(() => Deserialize(SignalThrowFixtures.Json(model =>
            SignalThrowFixtures.Signal(model, 7)["payload"] = new JsonObject())));
        Assert.Throws<JsonException>(() => Deserialize(SignalThrowFixtures.Json(model =>
            SignalThrowFixtures.Payload(model, 7)[0]!["required"] = "yes")));
        Assert.Throws<JsonException>(() => Deserialize(SignalThrowFixtures.Json(model =>
            SignalThrowFixtures.Signal(model, 7)["scope"]!["kind"] = 1)));
    }

    [Fact]
    public void StoredCatchWithAnUnknownSignalMember_StillBinds()
    {
        var json = SignalThrowFixtures.Json(model => SignalThrowFixtures.Signal(model, 9)["legacy"] = "kept");

        var model = Deserialize(json);

        Assert.Equal("orders.cancelled", model.FlowNodes.Single(node => node.Id == 9).Signal!.Name);
    }

    [Theory]
    [InlineData(7, "Signal throw event #7")]
    [InlineData(12, "Signal end event #12")]
    public void UnsupportedSettings_AreRejectedByTheApi(int nodeId, string label)
    {
        var json = SignalThrowFixtures.Json(model => SignalThrowFixtures.Node(model, nodeId)["roles"] = new JsonArray("Clerk"));

        Assert.Equal(
            label + " cannot define task, role, variable, timer, conditional, message, async, or multi-instance settings.",
            ServerError(json));
    }

    [Fact]
    public void SignalOnAnotherType_IsRejected()
    {
        var json = SignalThrowFixtures.Json(model =>
            SignalThrowFixtures.Node(model, 8)["signal"] = new JsonObject { ["name"] = "orders.cancelled" });

        Assert.Equal("Flow node #8 defines signal but is not a signal event.", ServerError(json));
    }

    [Fact]
    public void Normalization_KeepsAThrowAsAuthoredAndAWaitWithoutThrowMembers()
    {
        var model = Deserialize(SignalThrowFixtures.Json(_ => { }));
        WorkflowModelMigrator.Normalize(model);
        var json = JsonSerializer.SerializeToNode(model, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var nodes = json["flowNodes"]!.AsArray().Select(node => node!.AsObject()).ToDictionary(node => (int)node["id"]!);

        var thrown = nodes[7]["signal"]!.AsObject();
        Assert.Equal("orders.cancelled", (string?)thrown["name"]);
        Assert.Equal("thisInstance", (string?)thrown["scope"]!["kind"]);
        Assert.False(thrown["scope"]!.AsObject().ContainsKey("workflowKey"));
        Assert.Equal(
            """[{"name":"reason","variable":"cancelReason","required":true}]""",
            thrown["payload"]!.ToJsonString());
        Assert.Equal("[]", nodes[12]["signal"]!["payload"]!.ToJsonString());

        var waited = nodes[9]["signal"]!.AsObject();
        Assert.False(waited.ContainsKey("scope"));
        Assert.False(waited.ContainsKey("payload"));
    }

    [Fact]
    public void NodeTypes_ClassifyBothEvents()
    {
        Assert.True(BpmnFlowNodeTypes.IsPassThrough(BpmnFlowNodeTypes.IntermediateSignalThrowEvent));
        Assert.False(BpmnFlowNodeTypes.IsEnd(BpmnFlowNodeTypes.IntermediateSignalThrowEvent));
        Assert.True(BpmnFlowNodeTypes.IsEnd(BpmnFlowNodeTypes.SignalEndEvent));
        Assert.False(BpmnFlowNodeTypes.IsPassThrough(BpmnFlowNodeTypes.SignalEndEvent));
        foreach (var type in new[] { BpmnFlowNodeTypes.IntermediateSignalThrowEvent, BpmnFlowNodeTypes.SignalEndEvent })
        {
            Assert.True(BpmnFlowNodeTypes.IsSupported(type));
            Assert.True(BpmnFlowNodeTypes.ThrowsSignal(type));
            Assert.True(BpmnFlowNodeTypes.IsSignalEvent(type));
            Assert.False(BpmnFlowNodeTypes.IsSignalWait(type));
        }

        var throwOnly = JsonSerializer.Deserialize<WorkflowModel>(SignalThrowFixtures.Json(model =>
        {
            SignalThrowFixtures.Nodes(model).Remove(SignalThrowFixtures.Node(model, 9));
            SignalThrowFixtures.Flows(model).Remove(SignalThrowFixtures.Flow(model, 202));
            SignalThrowFixtures.Flows(model).Remove(SignalThrowFixtures.Flow(model, 901));
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(SignalEventRules.HasSignalWaits(throwOnly));
        Assert.True(WorkflowDefinitionReadinessChecker.RequiresDurableProcessing(throwOnly));
    }

    internal static WorkflowModel Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    internal static string? ServerError(string json)
    {
        var validator = new WorkflowDefinitionValidator(
            new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
            new ServiceTaskOptions());
        try
        {
            var model = Deserialize(json);
            validator.ValidateAuthored(model);
            WorkflowModelMigrator.Normalize(model);
            validator.ValidateNormalized(model);
            return null;
        }
        catch (Exception error) when (error is WorkflowDomainException or JsonException)
        {
            return error.Message;
        }
    }
}
