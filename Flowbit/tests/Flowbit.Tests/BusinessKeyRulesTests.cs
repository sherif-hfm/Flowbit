using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The one normalization of a business key, shared by starts and signal throws, and
/// the scope a throw or a subscription summary builds from its fields.
/// </summary>
public sealed class BusinessKeyRulesTests
{
    [Theory]
    [InlineData("ORD-1", "ORD-1")]
    [InlineData("  ORD-1\t", "ORD-1")]
    // Trim() removes every Unicode white-space character, such as a no-break space.
    [InlineData("\u00A0ORD 1\u2003", "ORD 1")]
    public void Normalize_TrimsAsAStartDoes(string value, string expected)
    {
        Assert.Equal(BusinessKeyProblem.None, BusinessKeyRules.Normalize(value, out var key));
        Assert.Equal(expected, key);
        Assert.Equal(value.Trim(), key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Normalize_ReportsABlankValue(string? value)
    {
        Assert.Equal(BusinessKeyProblem.Blank, BusinessKeyRules.Normalize(value, out var key));
        Assert.Equal(string.Empty, key);
    }

    [Fact]
    public void Normalize_CountsUnicodeScalarValuesNotUtf16Units()
    {
        // U+1F600 is one scalar value and two UTF-16 units.
        var emoji = char.ConvertFromUtf32(0x1F600);
        var exact = new string('k', 299) + emoji;
        Assert.Equal(301, exact.Length);
        Assert.Equal(BusinessKeyProblem.None, BusinessKeyRules.Normalize(exact, out _));
        Assert.Equal(BusinessKeyProblem.None,
            BusinessKeyRules.Normalize(string.Concat(Enumerable.Repeat(emoji, 300)), out _));
        Assert.Equal(BusinessKeyProblem.None, BusinessKeyRules.Normalize(new string('k', 300), out _));

        Assert.Equal(BusinessKeyProblem.TooLong, BusinessKeyRules.Normalize(new string('k', 301), out _));
        Assert.Equal(BusinessKeyProblem.TooLong, BusinessKeyRules.Normalize(exact + "k", out _));
        // The limit applies after trimming.
        Assert.Equal(BusinessKeyProblem.None, BusinessKeyRules.Normalize("  " + new string('k', 300) + "  ", out _));
    }

    [Fact]
    public void FromRequest_BuildsEachOfTheFourScopes()
    {
        Assert.Equal(SignalBroadcastScopeKind.AllWorkflows, Scope(null, true, null, null).Kind);

        var family = Scope("orders", false, null, null);
        Assert.Equal((SignalBroadcastScopeKind.Family, "orders", null, null),
            (family.Kind, family.WorkflowKey, family.BusinessKey, family.InstanceId));

        var keyed = Scope("orders", false, "  ORD-1 ", null);
        Assert.Equal((SignalBroadcastScopeKind.BusinessKey, "orders", "ORD-1", null),
            (keyed.Kind, keyed.WorkflowKey, keyed.BusinessKey, keyed.InstanceId));

        // allWorkflows false counts as absent beside an instance.
        var instance = Scope(null, false, null, 42);
        Assert.Equal((SignalBroadcastScopeKind.Instance, null, null, 42L),
            (instance.Kind, instance.WorkflowKey, instance.BusinessKey, instance.InstanceId));
        var resolved = instance.WithWorkflowKey("orders");
        Assert.Equal((SignalBroadcastScopeKind.Instance, "orders", 42L),
            (resolved.Kind, resolved.WorkflowKey, resolved.InstanceId));
        Assert.Throws<InvalidOperationException>(() => family.WithWorkflowKey("other"));

        static SignalBroadcastScope Scope(string? workflowKey, bool allWorkflows, string? businessKey, long? instanceId)
        {
            var scope = SignalBroadcastScope.FromRequest(workflowKey, allWorkflows, businessKey, instanceId, out var problem);
            Assert.Null(problem);
            return scope!;
        }
    }

    [Theory]
    // The first problem in the documented order is the one reported.
    [InlineData(" ", false, "  ", 0L, "workflowKey cannot be blank")]
    [InlineData("orders", false, "  ", 0L, "businessKey cannot be blank")]
    [InlineData(null, false, null, 0L, "instanceId must be a positive whole number.")]
    [InlineData(null, false, null, -7L, "instanceId must be a positive whole number.")]
    [InlineData("orders", false, null, 5L, "instanceId states the whole scope")]
    [InlineData(null, true, null, 5L, "instanceId states the whole scope")]
    [InlineData(null, false, "K", 5L, "instanceId states the whole scope")]
    [InlineData(null, false, "K", null, "businessKey needs a workflowKey")]
    [InlineData(null, true, "K", null, "businessKey needs a workflowKey")]
    [InlineData(null, false, null, null, "A signal must name one workflowKey, set allWorkflows to true, or name one instanceId.")]
    [InlineData("orders", true, null, null, "A signal must name one workflowKey, set allWorkflows to true, or name one instanceId.")]
    [InlineData("orders", true, "K", null, "A signal must name one workflowKey, set allWorkflows to true, or name one instanceId.")]
    public void FromRequest_ReportsTheFirstProblem(
        string? workflowKey, bool allWorkflows, string? businessKey, long? instanceId, string expected)
    {
        Assert.Null(SignalBroadcastScope.FromRequest(workflowKey, allWorkflows, businessKey, instanceId, out var problem));
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FromRequest_RefusesAKeyLongerThanTheLimit()
    {
        Assert.Null(SignalBroadcastScope.FromRequest("orders", false, new string('k', 301), null, out var problem));
        Assert.Equal("businessKey can have at most 300 characters.", problem);
    }

    [Fact]
    public void SummaryFilter_TreatsBlankValuesAsNoFilter()
    {
        Assert.Equal(new SignalSubscriptionFilter(null, null, null),
            SignalBroadcastScope.SummaryFilter(" ", "  ", null, out var none));
        Assert.Null(none);
        Assert.Equal(new SignalSubscriptionFilter("orders", null, null),
            SignalBroadcastScope.SummaryFilter("orders", "", null, out _));
        Assert.Equal(new SignalSubscriptionFilter("orders", "ORD-1", null),
            SignalBroadcastScope.SummaryFilter("orders", " ORD-1 ", null, out _));
        Assert.Equal(new SignalSubscriptionFilter(null, null, 42),
            SignalBroadcastScope.SummaryFilter("", " ", 42, out _));
    }

    [Theory]
    [InlineData(null, "K", null, "businessKey needs a workflowKey")]
    [InlineData("orders", null, 5L, "instanceId cannot be combined with workflowKey or businessKey.")]
    [InlineData(null, null, 0L, "instanceId must be a positive whole number.")]
    public void SummaryFilter_ReportsACombinationItCannotCount(
        string? workflowKey, string? businessKey, long? instanceId, string expected)
    {
        Assert.Null(SignalBroadcastScope.SummaryFilter(workflowKey, businessKey, instanceId, out var problem));
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }
}
