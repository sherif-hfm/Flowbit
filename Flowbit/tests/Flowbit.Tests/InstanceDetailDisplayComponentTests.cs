extern alias FlowbitUi;

using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GatewayState = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDetails.InstanceGatewayState;
using HistorySection = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDetails.InstanceHistory;
using MultiInstanceResults = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDetails.InstanceMultiInstanceResults;
using SharedBindings = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDetails.InstanceSharedBindings;
using VariablesSection = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDetails.InstanceVariables;
using VariableUpdateHistory = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDetails.InstanceVariableUpdateHistory;
using VersionChangeHistory = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDetails.InstanceVersionChangeHistory;
using Xunit;

namespace Flowbit.Tests;

public sealed class InstanceDetailDisplayComponentTests
{
    [Fact]
    public async Task GatewayStateRendersBothSectionsWithLabelsOrderAndReasons()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var nodes = new List<FlowNodeModel>
        {
            new() { Id = 5, Name = "Split approval" },
            new() { Id = 9, Name = "Join approval" },
        };
        var executions = new List<GatewayExecutionDto>
        {
            new(
                3, 5, "parallelGateway", "split", null, null, [1], null,
                "active", null, null, null,
                3, 2, 1, 0, 0, 0, now, now, null),
            new(
                9, 5, "parallelGateway", "merge", null, null, [2], 77,
                "completed", "joinCancellation", null, null,
                3, 0, 3, 3, 0, 0, now, now, now),
        };
        var states = new List<ComplexGatewayStateDto>
        {
            new(9, "ready", 2, [3, 4], [5], [11, 12], null, now),
        };

        var html = await RenderAsync<GatewayState>(new()
        {
            [nameof(GatewayState.GatewayExecutions)] = executions,
            [nameof(GatewayState.ComplexGatewayStates)] = states,
            [nameof(GatewayState.Nodes)] = nodes,
        });

        Assert.Contains("id=\"gateway-scopes\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"complex-gateway-states\"", html, StringComparison.Ordinal);
        Assert.Contains("Split approval (#5)", html, StringComparison.Ordinal);
        Assert.Contains("Parent #77", html, StringComparison.Ordinal);
        Assert.Contains("Cancelling join completed", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("#9", StringComparison.Ordinal) < html.IndexOf("#3", StringComparison.Ordinal),
            "Gateway executions must render in descending id order.");
        Assert.Contains("Join approval (#9)", html, StringComparison.Ordinal);
        Assert.Contains("Ready", html, StringComparison.Ordinal);
        Assert.Contains("#3, #4", html, StringComparison.Ordinal);
        Assert.Contains("#11, #12", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayStateHidesEmptySectionsAndLabelsUnknownGateways()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var executions = new List<GatewayExecutionDto>
        {
            new(
                4, 77, "inclusiveGateway", "split", "waiting", 1, [6], null,
                "active", null, null, null,
                2, 1, 0, 0, 0, 0, now, now, null),
        };

        var empty = await RenderAsync<GatewayState>(new()
        {
            [nameof(GatewayState.GatewayExecutions)] = Array.Empty<GatewayExecutionDto>(),
            [nameof(GatewayState.ComplexGatewayStates)] = Array.Empty<ComplexGatewayStateDto>(),
            [nameof(GatewayState.Nodes)] = Array.Empty<FlowNodeModel>(),
        });

        Assert.DoesNotContain("gateway-scopes", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("complex-gateway-states", empty, StringComparison.Ordinal);

        var visible = await RenderAsync<GatewayState>(new()
        {
            [nameof(GatewayState.GatewayExecutions)] = executions,
            [nameof(GatewayState.ComplexGatewayStates)] = Array.Empty<ComplexGatewayStateDto>(),
            [nameof(GatewayState.Nodes)] = Array.Empty<FlowNodeModel>(),
        });

        Assert.Contains("id=\"gateway-scopes\"", visible, StringComparison.Ordinal);
        Assert.Contains("Node #77", visible, StringComparison.Ordinal);
        Assert.Contains("waiting", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("complex-gateway-states", visible, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiInstanceResultsFollowsShowSectionAndRendersDerivedItems()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var flows = new List<SequenceFlowModel>
        {
            new() { Id = 201, Name = "Approve order" },
        };
        var items = new List<InstanceHistoryDto>
        {
            new(
                11, 3, 55, 7001, 2, 201, 7, 9, "alice",
                new Dictionary<string, JsonElement> { ["amount"] = JsonSerializer.SerializeToElement(15) },
                "multiInstanceItem", now),
            new(
                12, 4, 56, 7001, null, 999, 7, 9, null, null,
                "multiInstanceItem", now),
        };

        var hidden = await RenderAsync<MultiInstanceResults>(new()
        {
            [nameof(MultiInstanceResults.ShowSection)] = false,
            [nameof(MultiInstanceResults.CompletedItems)] = items,
            [nameof(MultiInstanceResults.SequenceFlows)] = flows,
        });

        Assert.DoesNotContain("multi-instance-results", hidden, StringComparison.Ordinal);

        var html = await RenderAsync<MultiInstanceResults>(new()
        {
            [nameof(MultiInstanceResults.ShowSection)] = true,
            [nameof(MultiInstanceResults.CompletedItems)] = items,
            [nameof(MultiInstanceResults.SequenceFlows)] = flows,
        });

        Assert.Contains("id=\"multi-instance-results\"", html, StringComparison.Ordinal);
        Assert.Contains("3</td>", html, StringComparison.Ordinal);
        Assert.Contains("Approve order (#201)", html, StringComparison.Ordinal);
        Assert.Contains("Flow #999", html, StringComparison.Ordinal);
        Assert.Contains("alice", html, StringComparison.Ordinal);
        Assert.Contains("amount", html, StringComparison.Ordinal);
        Assert.Contains("15", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("Approve order (#201)", StringComparison.Ordinal) < html.IndexOf("Flow #999", StringComparison.Ordinal),
            "Completed items must render in the supplied order.");
    }

    [Fact]
    public async Task MultiInstanceResultsShowsEmptyStateWithoutItems()
    {
        var html = await RenderAsync<MultiInstanceResults>(new()
        {
            [nameof(MultiInstanceResults.ShowSection)] = true,
            [nameof(MultiInstanceResults.CompletedItems)] = Array.Empty<InstanceHistoryDto>(),
            [nameof(MultiInstanceResults.SequenceFlows)] = Array.Empty<SequenceFlowModel>(),
        });

        Assert.Contains("id=\"multi-instance-results\"", html, StringComparison.Ordinal);
        Assert.Contains("No work items completed yet.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionChangeHistoryRendersDirectionActorsAndBatchLink()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var source = new WorkflowSummaryDto(17, "Purchase request", "purchase-request", 1, true, true, now);
        var target = new WorkflowSummaryDto(19, "Purchase request", "purchase-request", 2, true, false, now);
        var changes = new List<InstanceVersionChangeAuditDto>
        {
            new(101, 42, source, target, "upgrade", "operator-a", ["admin"], "Refresh compatibility", now, 91, 701),
        };

        var html = await RenderAsync<VersionChangeHistory>(new()
        {
            [nameof(VersionChangeHistory.InstanceId)] = 42L,
            [nameof(VersionChangeHistory.Changes)] = changes,
        });

        Assert.Contains("id=\"version-changes\"", html, StringComparison.Ordinal);
        Assert.Contains("Workflow version changes for instance #42", html, StringComparison.Ordinal);
        Assert.Contains("Upgrade", html, StringComparison.Ordinal);
        Assert.Contains("v1<span class=\"data-subtitle\">Purchase request</span>", html, StringComparison.Ordinal);
        Assert.Contains("v2", html, StringComparison.Ordinal);
        Assert.Contains("operator-a", html, StringComparison.Ordinal);
        Assert.Contains("admin", html, StringComparison.Ordinal);
        Assert.Contains("Refresh compatibility", html, StringComparison.Ordinal);
        Assert.Contains("href=\"instance-version-changes?batchId=91\"", html, StringComparison.Ordinal);
        Assert.Contains("Batch #91", html, StringComparison.Ordinal);
        Assert.Contains("Item #701", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionChangeHistoryHidesWhenEmpty()
    {
        var html = await RenderAsync<VersionChangeHistory>(new()
        {
            [nameof(VersionChangeHistory.InstanceId)] = 42L,
            [nameof(VersionChangeHistory.Changes)] = Array.Empty<InstanceVersionChangeAuditDto>(),
        });

        Assert.DoesNotContain("version-changes", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VariableUpdateHistoryRendersOutcomesEscapedJsonAndBatchLink()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var value = JsonSerializer.SerializeToElement("a<b & approved");
        var outcome = new InstanceVariableUpdateOutcomeDto("reviewContext", "updated", 501, value);
        var updates = new List<InstanceVariableUpdateAuditDto>
        {
            new(81, 42, 17, "operator", ["admin"], "Correct imported approval state", [outcome], now, "direct-retry", 91, 701),
        };

        var html = await RenderAsync<VariableUpdateHistory>(new()
        {
            [nameof(VariableUpdateHistory.InstanceId)] = 42L,
            [nameof(VariableUpdateHistory.Updates)] = updates,
        });

        Assert.Contains("id=\"variable-updates\"", html, StringComparison.Ordinal);
        Assert.Contains("Administrative variable updates for instance #42", html, StringComparison.Ordinal);
        Assert.Contains("Operation #81", html, StringComparison.Ordinal);
        Assert.Contains("operator", html, StringComparison.Ordinal);
        Assert.Contains("Correct imported approval state", html, StringComparison.Ordinal);
        Assert.Contains("reviewContext", html, StringComparison.Ordinal);
        Assert.Contains("history #501", html, StringComparison.Ordinal);
        Assert.Contains("a&lt;b &amp; approved", html, StringComparison.Ordinal);
        Assert.DoesNotContain("a<b", html, StringComparison.Ordinal);
        Assert.Contains("href=\"instance-variable-updates?batchId=91\"", html, StringComparison.Ordinal);
        Assert.Contains("Item #701", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VariableUpdateHistoryHidesWhenEmpty()
    {
        var html = await RenderAsync<VariableUpdateHistory>(new()
        {
            [nameof(VariableUpdateHistory.InstanceId)] = 42L,
            [nameof(VariableUpdateHistory.Updates)] = Array.Empty<InstanceVariableUpdateAuditDto>(),
        });

        Assert.DoesNotContain("variable-updates", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedBindingsRenderValueFreeCatalogMetadataAndHideWhenEmpty()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var bindings = new List<SharedVariableBindingMetadataDto>
        {
            new("approvalAmount", "examples.approval.amount", "readWrite", "number", false, true, null, "active", 4, true, now, now, null),
            new("regionKey", "examples.region.code", "read", "string", false, false, "Len(regionKey) > 0", "archived", 9, false, now, now, now),
        };

        var html = await RenderAsync<SharedBindings>(new()
        {
            [nameof(SharedBindings.InstanceId)] = 42L,
            [nameof(SharedBindings.Bindings)] = bindings,
        });

        Assert.Contains("id=\"shared-variables\"", html, StringComparison.Ordinal);
        Assert.Contains("Shared-variable bindings for instance #42", html, StringComparison.Ordinal);
        Assert.Contains("approvalAmount", html, StringComparison.Ordinal);
        Assert.Contains("examples.approval.amount", html, StringComparison.Ordinal);
        Assert.Contains("Read / write", html, StringComparison.Ordinal);
        Assert.Contains("Read only", html, StringComparison.Ordinal);
        Assert.Contains("nullable", html, StringComparison.Ordinal);
        Assert.Contains("No validation", html, StringComparison.Ordinal);
        Assert.Contains("Len(regionKey) &gt; 0", html, StringComparison.Ordinal);
        Assert.Contains("Revision #4", html, StringComparison.Ordinal);
        Assert.Contains("Value set", html, StringComparison.Ordinal);
        Assert.Contains("Value unset", html, StringComparison.Ordinal);
        Assert.Contains("href=\"shared-variables\"", html, StringComparison.Ordinal);

        var empty = await RenderAsync<SharedBindings>(new()
        {
            [nameof(SharedBindings.InstanceId)] = 42L,
            [nameof(SharedBindings.Bindings)] = Array.Empty<SharedVariableBindingMetadataDto>(),
        });

        Assert.DoesNotContain("shared-variables", empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VariablesRenderLatestValuesAuditLinksAndEmptyState()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var audited = new InstanceVariableDto(
            501, "reviewContext", 12, "operator",
            JsonSerializer.SerializeToElement("a<b"), now)
        {
            InstanceVariableUpdateAuditId = 81
        };
        var flowVariable = new InstanceVariableDto(
            502, "amount", 12, "system",
            JsonSerializer.SerializeToElement(15), now);
        var startVariable = new InstanceVariableDto(
            503, "requester", null, "starter",
            JsonSerializer.SerializeToElement("alice"), now);

        var html = await RenderAsync<VariablesSection>(new()
        {
            [nameof(VariablesSection.InstanceId)] = 42L,
            [nameof(VariablesSection.Variables)] = new InstanceVariableDto[] { audited, flowVariable, startVariable },
        });

        Assert.Contains("Latest variables stored on instance #42", html, StringComparison.Ordinal);
        Assert.Contains("reviewContext", html, StringComparison.Ordinal);
        Assert.Contains("a&lt;b", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#variable-updates\"", html, StringComparison.Ordinal);
        Assert.Contains(">#81</a>", html, StringComparison.Ordinal);
        Assert.Contains("start</td>", html, StringComparison.Ordinal);
        Assert.Contains("12</td>", html, StringComparison.Ordinal);
        Assert.Contains("alice", html, StringComparison.Ordinal);
        Assert.Contains("15", html, StringComparison.Ordinal);

        var empty = await RenderAsync<VariablesSection>(new()
        {
            [nameof(VariablesSection.InstanceId)] = 42L,
            [nameof(VariablesSection.Variables)] = Array.Empty<InstanceVariableDto>(),
        });

        Assert.Contains("No variables captured.", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"#variable-updates\"", empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryRendersAttributionClaimsDetailsAndBatchLink()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var claim = new InstanceHistoryDto(
            31, 9, 55, null, null, 201, 7, 9, "carol",
            new Dictionary<string, JsonElement>
            {
                ["previousClaimedBy"] = JsonSerializer.SerializeToElement("alice"),
                ["newClaimedBy"] = JsonSerializer.SerializeToElement("carol"),
                ["operation"] = JsonSerializer.SerializeToElement("claimed"),
                ["authority"] = JsonSerializer.SerializeToElement("userDelegation"),
            },
            "taskClaim", now)
        {
            ActorClaims = new Dictionary<string, string[]> { ["flowbit.role"] = ["manager", "manager"] },
            ActingFor = "dave",
            DelegationId = 5,
            AdministrativeActionBatchId = 33,
        };
        var assignment = new InstanceHistoryDto(
            32, 10, 55, null, null, null, 9, 10, "system", null,
            "taskAssignment", now)
        {
            ActorClaims = null,
        };

        var html = await RenderAsync<HistorySection>(new()
        {
            [nameof(HistorySection.InstanceId)] = 42L,
            [nameof(HistorySection.HistoryItems)] = new InstanceHistoryDto[] { claim, assignment },
        });

        Assert.Contains("Transition and action history for instance #42", html, StringComparison.Ordinal);
        Assert.Contains("Claimed by carol (previously claimed by alice) through delegation", html, StringComparison.Ordinal);
        Assert.Contains("Acting for dave", html, StringComparison.Ordinal);
        Assert.Contains("Delegated task access via grant #5", html, StringComparison.Ordinal);
        Assert.Contains("flowbit.role", html, StringComparison.Ordinal);
        Assert.Contains("manager", html, StringComparison.Ordinal);
        Assert.Contains("href=\"administrative-actions?batchId=33\"", html, StringComparison.Ordinal);
        Assert.Contains("Assignment changed from shared pool to shared pool", html, StringComparison.Ordinal);
        Assert.Contains("Not recorded", html, StringComparison.Ordinal);

        var empty = await RenderAsync<HistorySection>(new()
        {
            [nameof(HistorySection.InstanceId)] = 42L,
            [nameof(HistorySection.HistoryItems)] = Array.Empty<InstanceHistoryDto>(),
        });

        Assert.Contains("No history events yet.", empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryRendersAssignmentReasonAndActorWithoutClaims()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var assignment = new InstanceHistoryDto(
            32, 10, 55, null, null, null, 9, 10, "manager-one",
            new Dictionary<string, JsonElement>
            {
                ["operation"] = JsonSerializer.SerializeToElement(UserTaskAssignmentOperations.Reassigned),
                ["previousOwner"] = JsonSerializer.SerializeToElement("alice"),
                ["newOwner"] = JsonSerializer.SerializeToElement("bob"),
                ["reason"] = JsonSerializer.SerializeToElement("Covering absence"),
            },
            "taskAssignment", now)
        {
            ActorClaims = new Dictionary<string, string[]>(),
        };

        var html = await RenderAsync<HistorySection>(new()
        {
            [nameof(HistorySection.InstanceId)] = 42L,
            [nameof(HistorySection.HistoryItems)] = new[] { assignment },
        });

        Assert.Contains("manager-one", html, StringComparison.Ordinal);
        Assert.Contains("Reassigned from alice to bob &#x2014; Covering absence", html, StringComparison.Ordinal);
        Assert.Contains("actor-claims-empty", html, StringComparison.Ordinal);
        Assert.Contains("No selected claims present", html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters)
        where T : ComponentBase
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(
                ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }
}
