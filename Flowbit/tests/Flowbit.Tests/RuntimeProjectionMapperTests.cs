using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Characterizes the redaction and version-summary mappings shared by the
/// engine and the instance projection service.
/// </summary>
public sealed class RuntimeProjectionMapperTests
{
    [Fact]
    public void RuntimeWorkflowDetailRedactsMessageAndTaskDistributionSecretsWithoutMutatingTheRecord()
    {
        var definition = new WorkflowModel
        {
            Id = "redaction-unit",
            Name = "Redaction unit",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Wait for webhook",
                    Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "svc-orders",
                        ClientSecret = "raw-message-secret",
                        HeaderName = "X-Webhook-Token",
                        HeaderValue = "raw-header-value"
                    }
                }
            ],
            TaskDistribution = new TaskDistributionModel
            {
                ClientId = "distributor",
                ClientSecret = "raw-distribution-secret"
            }
        };
        var workflow = new WorkflowDefinitionRecord(
            3,
            definition.Name,
            definition.Id,
            2,
            definition,
            true,
            true,
            DateTimeOffset.UnixEpoch);

        var detail = RuntimeProjectionMapper.ToRuntimeWorkflowDetail(workflow);

        var catchNode = Assert.Single(detail.Definition.FlowNodes);
        Assert.Equal("[redacted]", catchNode.Message!.ClientSecret);
        Assert.Equal("[redacted]", catchNode.Message.HeaderValue);
        Assert.Equal("[redacted]", detail.Definition.TaskDistribution!.ClientSecret);

        // The cached immutable record (and the node message object it owns) must
        // keep the authored secrets so later authentications still resolve.
        Assert.Equal("raw-message-secret", definition.FlowNodes[0].Message!.ClientSecret);
        Assert.Equal("raw-header-value", definition.FlowNodes[0].Message!.HeaderValue);
        Assert.Equal("raw-distribution-secret", definition.TaskDistribution!.ClientSecret);
    }

    [Fact]
    public void VersionChangeHelpersKeepSummaryDirectionAndAuditShape()
    {
        var source = new WorkflowDefinitionRecord(
            11,
            "Orders",
            "orders",
            2,
            new WorkflowModel(),
            true,
            false,
            DateTimeOffset.UnixEpoch);
        var target = source with { Id = 12, Version = 5 };

        Assert.Equal(InstanceVersionChangeDirections.Upgrade,
            RuntimeProjectionMapper.VersionChangeDirection(source, target));
        Assert.Equal(InstanceVersionChangeDirections.Downgrade,
            RuntimeProjectionMapper.VersionChangeDirection(target, source));

        var summary = RuntimeProjectionMapper.ToVersionSummary(source);
        Assert.Equal(source.Id, summary.Id);
        Assert.Equal(source.WorkflowKey, summary.WorkflowKey);
        Assert.Equal(source.Version, summary.Version);
        Assert.Equal(source.IsPublished, summary.IsPublished);

        var record = new WorkflowInstanceVersionChangeRecord(
            77,
            42,
            source.Id,
            target.Id,
            "admin",
            ["admin"],
            "approved correction",
            DateTimeOffset.UnixEpoch.AddDays(1),
            301,
            907);
        var audit = RuntimeProjectionMapper.ToVersionChangeAudit(record, source, target);
        Assert.Equal(record.Id, audit.Id);
        Assert.Equal(record.InstanceId, audit.InstanceId);
        Assert.Equal(source.Id, audit.SourceWorkflow.Id);
        Assert.Equal(target.Id, audit.TargetWorkflow.Id);
        Assert.Equal(InstanceVersionChangeDirections.Upgrade, audit.Direction);
        Assert.Equal(record.ChangedBy, audit.ChangedBy);
        Assert.Equal(record.ChangedByRoles, audit.ChangedByRoles);
        Assert.Equal(record.Reason, audit.Reason);
        Assert.Equal(record.ChangedAt, audit.ChangedAt);
        Assert.Equal(record.BatchId, audit.BatchId);
        Assert.Equal(record.BatchItemId, audit.BatchItemId);
    }
}
