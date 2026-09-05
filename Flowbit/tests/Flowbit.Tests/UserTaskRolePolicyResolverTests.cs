using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class UserTaskRolePolicyResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("shared")]
    public void ResolveRoles_CapturesObservedInstanceOrSharedValue(string? scope)
    {
        var definition = Definition(scope);
        var values = new Dictionary<string, JsonElement>
        {
            ["reviewRoles"] = JsonSerializer.SerializeToElement(new[] { " Finance ", "finance", "Legal", " " })
        };
        var captured = UserTaskRolePolicyResolver.ResolveRoles(definition, "REVIEWROLES", [], values);
        values["reviewRoles"] = JsonSerializer.SerializeToElement(new[] { "Manager" });
        Assert.Equal(["Finance", "Legal"], captured);
        Assert.Equal(["Manager"], UserTaskRolePolicyResolver.ResolveRoles(definition, "reviewRoles", [], values));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"Finance\"")]
    [InlineData("[]")]
    [InlineData("[\" \" ]")]
    [InlineData("[\"Finance\",null]")]
    [InlineData("[\"Finance\",1]")]
    public void ResolveRoles_RejectsInvalidValues(string json)
    {
        var values = new Dictionary<string, JsonElement> { ["reviewRoles"] = JsonDocument.Parse(json).RootElement.Clone() };
        Assert.Throws<WorkflowDomainException>(() =>
            UserTaskRolePolicyResolver.ResolveRoles(Definition(), "reviewRoles", [], values));
    }

    [Fact]
    public void ResolveRoles_RejectsMissingValueButPreservesLiteralCompatibility()
    {
        Assert.Throws<WorkflowDomainException>(() =>
            UserTaskRolePolicyResolver.ResolveRoles(Definition(), "reviewRoles", [], new Dictionary<string, JsonElement>()));
        Assert.Empty(UserTaskRolePolicyResolver.ResolveRoles(Definition(), null, [], new Dictionary<string, JsonElement>()));
        var legacy = Enumerable.Range(0, 101).Select(index => $"role-{index}").ToArray();
        Assert.Equal(101, UserTaskRolePolicyResolver.ResolveRoles(Definition(), null, legacy, new Dictionary<string, JsonElement>()).Count);
    }

    [Fact]
    public void NormalizeManagedRoles_EnforcesInputBoundsAndNullsButAllowsExplicitUnrestricted()
    {
        Assert.Empty(UserTaskRolePolicyResolver.NormalizeManagedRoles([]));
        Assert.Equal(["Finance", "Legal"], UserTaskRolePolicyResolver.NormalizeManagedRoles([" Finance ", "finance", "Legal"]));
        Assert.Throws<WorkflowDomainException>(() => UserTaskRolePolicyResolver.NormalizeManagedRoles(null!));
        Assert.Throws<WorkflowDomainException>(() => UserTaskRolePolicyResolver.NormalizeManagedRoles([null!]));
        Assert.Throws<WorkflowDomainException>(() => UserTaskRolePolicyResolver.NormalizeManagedRoles(Enumerable.Repeat("role", 101)));
        Assert.Single(UserTaskRolePolicyResolver.NormalizeManagedRoles([string.Concat(Enumerable.Repeat("😀", 300))]));
        Assert.Throws<WorkflowDomainException>(() => UserTaskRolePolicyResolver.NormalizeManagedRoles([string.Concat(Enumerable.Repeat("😀", 301))]));
    }

    [Fact]
    public void SharedVariablePlan_ReadsTaskAndActionRoleSourcesOnTaskEntry()
    {
        var definition = Definition("shared");
        definition.Variables.Add(new VariableModel { Name = "actionRoles", Scope = "shared", SharedKey = "test.actions", DataType = "string", IsArray = true });
        definition.FlowNodes = [new FlowNodeModel { Id = 2, Type = "userTask", RolesVariable = "reviewRoles" }];
        definition.SequenceFlows = [new SequenceFlowModel { Id = 20, SourceRef = 2, TargetRef = 3, RolesVariable = "actionRoles" }];
        var plan = SharedVariableAccessPlanner.Build(definition, ConditionalEventDependencyPlan.Empty);
        Assert.Equal(["actionRoles", "reviewRoles"], plan.ForNode(2).ReadAliases.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(plan.ForNode(2).ProducerAliases);
        Assert.Empty(plan.ForFlow(20).ProducerAliases);
    }

    [Fact]
    public void WithResolvedRoles_DoesNotMutateAuthoredRoles()
    {
        var authored = new SequenceFlowModel { RolesVariable = "reviewRoles", Variables = [new VariableModel { Name = "note" }] };
        var projected = authored.WithResolvedRoles(["Manager"]);
        Assert.Empty(authored.Roles);
        Assert.Equal("reviewRoles", authored.RolesVariable);
        Assert.Null(projected.RolesVariable);
        Assert.Equal(["Manager"], projected.Roles);
        Assert.Same(authored.Variables, projected.Variables);
    }

    private static WorkflowModel Definition(string? scope = null) => new()
    {
        Variables = [new VariableModel { Name = "reviewRoles", DataType = "string", IsArray = true, Scope = scope, SharedKey = scope == "shared" ? "test.roles" : null }]
    };
}
