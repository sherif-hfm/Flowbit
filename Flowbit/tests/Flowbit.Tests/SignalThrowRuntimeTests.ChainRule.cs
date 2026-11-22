using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The chain rule of EVENT-04B applies to definitions without signal events too: a
/// conditional event woken by a person, a message, or an administrative update starts a new
/// chain; one woken by an automatic step continues that step's count, so a loop through a
/// conditional event reaches <c>automatic_loop_limit</c>.
/// </summary>
public sealed partial class SignalThrowRuntimeTests
{
    [Theory]
    [InlineData("userAction", 0)]
    [InlineData("message", 0)]
    [InlineData("administrativeUpdate", 0)]
    [InlineData("automaticStep", 1)]
    public async Task ChainRule_AConditionalBoundaryContinuesTheCountOfWhatWokeIt(string waker, int expected)
    {
        var model = new WorkflowModel
        {
            Id = $"chain-rule-{waker}-{Guid.NewGuid():N}",
            Name = "Chain rule without signals",
            InitialEventId = 1,
            Variables = [new VariableModel { Id = 1, Name = "v", DataType = WorkflowVariableTypes.Number, Nullable = true }],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 4, Name = "Woken end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Hold", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 6, Name = "Positive", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 5,
                    CancelActivity = false, Conditional = new ConditionalDefinitionModel { Condition = "v > 0" }
                },
                new FlowNodeModel { Id = 7, Name = "After", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 8, Name = "After end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 9, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 5 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 9, Name = "Done" },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7 },
                new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8, Name = "Close" }
            ]
        };
        switch (waker)
        {
            case "userAction":
                model.FlowNodes.Add(new FlowNodeModel { Id = 3, Name = "Set", Type = BpmnFlowNodeTypes.UserTask });
                model.SequenceFlows.Add(new SequenceFlowModel
                {
                    Id = 301, SourceRef = 3, TargetRef = 4, Name = "Set",
                    Variables = [new VariableModel { Id = 11, Name = "v", DataType = WorkflowVariableTypes.Number, Required = true }]
                });
                break;
            case "message":
                model.FlowNodes.Add(new FlowNodeModel
                {
                    Id = 3, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "chain-client", ClientSecret = "chain-secret", HeaderName = "X-Chain", HeaderValue = "go",
                        OutputMappings =
                        [
                            new MessageOutputMappingModel
                            {
                                Variable = "v", Path = "v", DataType = WorkflowVariableTypes.Number, IsArray = false, Required = true
                            }
                        ]
                    }
                });
                model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 });
                break;
            case "administrativeUpdate":
                model.FlowNodes.Add(new FlowNodeModel { Id = 3, Name = "Idle", Type = BpmnFlowNodeTypes.UserTask });
                model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Idle" });
                break;
            default:
                model.FlowNodes.Add(new FlowNodeModel
                {
                    Id = 3, Name = "Compute", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
                    Assignments = [new AssignmentModel { Variable = "v", Expression = "1" }]
                });
                model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 });
                break;
        }

        model.SequenceFlows.Add(new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 });
        var instance = await StartAsync(await CreateAsync(model));
        switch (waker)
        {
            case "userAction":
                long taskId;
                await using (var db = fixture.CreateDbContext())
                {
                    taskId = await db.UserTasks.Where(item => item.InstanceId == instance.Id && item.NodeId == 3)
                        .Select(item => item.Id).SingleAsync();
                }

                using (var action = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/301",
                           new TakeFlowRequest(new Dictionary<string, JsonElement> { ["v"] = JsonSerializer.SerializeToElement(1) })))
                {
                    Assert.True(action.IsSuccessStatusCode, await action.Content.ReadAsStringAsync());
                }

                break;
            case "message":
                using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{instance.Id}/message")
                {
                    Content = new StringContent("""{"v":1}""", System.Text.Encoding.UTF8, "application/json")
                })
                {
                    request.Headers.Add("X-Client-Id", "chain-client");
                    request.Headers.Add("X-Client-Secret", "chain-secret");
                    request.Headers.Add("X-Chain", "go");
                    using var response = await fixture.Client.SendAsync(request);
                    Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                }

                break;
            case "administrativeUpdate":
                using (var patch = await SendAsync(HttpMethod.Patch, $"/api/instances/{instance.Id}/variables",
                           new UpdateInstanceVariablesRequest([new InstanceVariableWriteDto("v", JsonSerializer.SerializeToElement(1))], "Set", null)))
                {
                    Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());
                }

                break;
            default:
                await ProcessInstanceJobsAsync(instance.Id, WorkflowJobKinds.AsyncBefore);
                break;
        }

        await using var check = fixture.CreateDbContext();
        Assert.Equal(expected, await check.ExecutionTokens.AsNoTracking()
            .Where(item => item.InstanceId == instance.Id && item.NodeId == 7)
            .Select(item => item.AutomaticActivationCount).SingleAsync());
    }

    /// <summary>
    /// A loop of automatic steps through a conditional boundary, with no signal and no person
    /// in it, ran without a bound before EVENT-04B; it now reaches automatic_loop_limit.
    /// </summary>
    [Fact]
    public async Task ChainRule_ALoopThroughAConditionalBoundaryReachesTheLimit()
    {
        var model = new WorkflowModel
        {
            Id = $"chain-loop-{Guid.NewGuid():N}",
            Name = "Loop through a conditional boundary",
            InitialEventId = 1,
            Variables = [new VariableModel { Id = 1, Name = "v", DataType = WorkflowVariableTypes.Number, DefaultValue = JsonSerializer.SerializeToElement(0) }],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Hold", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    // Rises every time v becomes odd.
                    Id = 4, Name = "Odd", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 3,
                    CancelActivity = false, Conditional = new ConditionalDefinitionModel { Condition = "v % 2 == 1" }
                },
                new FlowNodeModel
                {
                    Id = 5, Name = "Make even", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
                    Assignments = [new AssignmentModel { Variable = "v", Expression = "v + 1" }]
                },
                new FlowNodeModel
                {
                    Id = 6, Name = "Make odd", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
                    Assignments = [new AssignmentModel { Variable = "v", Expression = "v + 1" }]
                },
                new FlowNodeModel { Id = 7, Name = "Looped", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 8, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 6 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 8, Name = "Done" },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7 }
            ]
        };
        var instance = await StartAsync(await CreateAsync(model));

        await WithChainLimitAsync(5, async () =>
        {
            for (var round = 0; round < 30; round++)
            {
                await using (var db = fixture.CreateDbContext())
                {
                    if (await db.WorkflowIncidents.AnyAsync(item => item.InstanceId == instance.Id && item.Status == WorkflowIncidentStatuses.Open))
                    {
                        break;
                    }
                }

                await ProcessInstanceJobsAsync(instance.Id, WorkflowJobKinds.AsyncBefore);
            }
        });

        await using var check = fixture.CreateDbContext();
        var incident = await check.WorkflowIncidents.AsNoTracking()
            .SingleAsync(item => item.InstanceId == instance.Id && item.Status == WorkflowIncidentStatuses.Open);
        Assert.Equal("automatic_loop_limit", incident.Type);
    }
}
