using System.Text.Json;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class SharedVariableWorkflowModelTests
{
    [Fact]
    public void VariableBindingJsonIsOptionalForInstanceScopeAndExplicitForSharedScope()
    {
        var instance = new VariableModel { Id = 1, Name = "amount", DataType = "number" };
        var shared = new VariableModel
        {
            Id = 2,
            Name = "exchangeRate",
            Scope = VariableScopes.Shared,
            SharedKey = "finance.usdToSar",
            Access = SharedVariableAccessModes.Read,
            DataType = "number"
        };

        var instanceJson = JsonSerializer.Serialize(instance);
        var sharedJson = JsonSerializer.Serialize(shared);
        var roundTrip = JsonSerializer.Deserialize<VariableModel>(sharedJson)!;

        Assert.DoesNotContain("\"scope\"", instanceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sharedKey\"", instanceJson, StringComparison.Ordinal);
        Assert.Contains("\"scope\":\"shared\"", sharedJson, StringComparison.Ordinal);
        Assert.Contains("\"sharedKey\":\"finance.usdToSar\"", sharedJson, StringComparison.Ordinal);
        Assert.Equal(SharedVariableAccessModes.Read, roundTrip.Access);
    }

    [Fact]
    public void MigratorCanonicalizesTopLevelSharedBindingAndClearsNestedBindingMetadata()
    {
        var defaultValue = JsonSerializer.SerializeToElement(1);
        var model = new WorkflowModel
        {
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "rate",
                    Scope = "SHARED",
                    SharedKey = " finance.usdToSar ",
                    Access = "READWRITE",
                    DataType = "number",
                    Required = true,
                    DefaultValue = defaultValue
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 2,
                            Name = "input",
                            Scope = VariableScopes.Shared,
                            SharedKey = "invalid.nested",
                            Access = SharedVariableAccessModes.ReadWrite
                        }
                    ]
                }
            ]
        };

        WorkflowModelMigrator.Normalize(model);

        var shared = Assert.Single(model.Variables);
        Assert.Equal(VariableScopes.Shared, shared.Scope);
        Assert.Equal("finance.usdToSar", shared.SharedKey);
        Assert.Equal(SharedVariableAccessModes.ReadWrite, shared.Access);
        Assert.False(shared.Required);
        Assert.Null(shared.DefaultValue);
        var nested = Assert.Single(model.FlowNodes[0].Variables);
        Assert.Null(nested.Scope);
        Assert.Null(nested.SharedKey);
        Assert.Null(nested.Access);
    }

    [Fact]
    public void MigratorPreservesUnsupportedTopLevelScopeForValidation()
    {
        var model = new WorkflowModel
        {
            Variables = [new VariableModel { Id = 1, Name = "value", Scope = "tenant" }]
        };

        WorkflowModelMigrator.Normalize(model);

        Assert.Equal("tenant", model.Variables[0].Scope);
    }
}
