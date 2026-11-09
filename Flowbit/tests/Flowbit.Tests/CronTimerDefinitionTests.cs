using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class CronTimerDefinitionTests
{
    [Theory]
    [InlineData("0 8 * * 1-5", "Europe/London")]
    [InlineData("*/15 9-17 * * 1-5", "Etc/UTC")]
    [InlineData("0 17 L * 5", "America/New_York")]
    [InlineData("  0 8 29 2 *  ", "  Asia/Tokyo ")]
    [InlineData("\u0085 0 8 * * 1-5\u0085", "\u0085Europe/London\u2028")]
    public void Validate_AcceptsACronTimerStart(string cron, string timeZone)
    {
        var model = TimerStartModel(new TimerDefinitionModel { Cron = cron, TimeZone = timeZone });

        TimerExpressionDefinitionTests.Validate(model);

        Assert.Equal(cron.Trim(), model.FlowNodes[0].Timer!.Cron);
        Assert.Equal(timeZone.Trim(), model.FlowNodes[0].Timer!.TimeZone);
    }

    public static TheoryData<TimerDefinitionModel, string> InvalidTimerStarts() => new()
    {
        {
            new TimerDefinitionModel { Cron = "0 8 * * *", TimeDuration = "PT1H", TimeZone = "Etc/UTC" },
            "Timer event #1 must define exactly one of timeDate, timeDuration, timeCycle, or cron."
        },
        {
            new TimerDefinitionModel { TimeZone = "Etc/UTC" },
            "Timer event #1 must define exactly one of timeDate, timeDuration, timeCycle, or cron."
        },
        {
            new TimerDefinitionModel { Cron = "0 8 * * 7", TimeZone = "Etc/UTC" },
            "Timer start event #1 timer.cron day-of-week value 7 is not allowed; write Sunday as 0."
        },
        {
            new TimerDefinitionModel { Cron = "0 8 30 2 *", TimeZone = "Etc/UTC" },
            "Timer start event #1 timer.cron '0 8 30 2 *' never matches a calendar date; check its day-of-month, month, and day-of-week fields."
        },
        {
            new TimerDefinitionModel { Cron = "5/- 8 * * *", TimeZone = "Etc/UTC" },
            "Timer start event #1 timer.cron minute item '5/-' is malformed; write *, */n, a, a-b, a/n, or a-b/n."
        },
        {
            // U+0085 is white space to .NET, so this cron is blank; the U+FEFF byte-order mark is not.
            new TimerDefinitionModel { Cron = "\u0085", TimeZone = "Etc/UTC" },
            "Timer event #1 must define exactly one of timeDate, timeDuration, timeCycle, or cron."
        },
        {
            new TimerDefinitionModel { Cron = "\uFEFF", TimeZone = "Etc/UTC" },
            "Timer start event #1 timer.cron '\uFEFF' has 1 field; write five: minute, hour, day of month, month, and day of week. Seconds and years are not supported."
        },
        {
            new TimerDefinitionModel { Cron = "0 8 * * *", TimeZone = "\u0085" },
            "Timer start event #1 timer.timeZone is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC."
        },
        {
            new TimerDefinitionModel { Cron = "@daily", TimeZone = "Etc/UTC" },
            "Timer start event #1 timer.cron '@daily' is an alias, which is not supported; write five fields: minute, hour, day of month, month, and day of week."
        },
        {
            new TimerDefinitionModel { Cron = "0 8 * * *" },
            "Timer start event #1 timer.timeZone is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC."
        },
        {
            new TimerDefinitionModel { Cron = "0 8 * * *", TimeZone = "Mars/Olympus" },
            "Timer start event #1 timer.timeZone 'Mars/Olympus' is not an IANA time zone that this server knows, such as Europe/London."
        },
        {
            new TimerDefinitionModel { Cron = "0 8 * * *", TimeZone = new string('A', 101) },
            "Timer start event #1 timer.timeZone must contain at most 100 characters."
        },
        {
            new TimerDefinitionModel { TimeDuration = "PT1H", TimeZone = "Europe/London" },
            "Timer start event #1 timer.timeZone needs a cron schedule; only cron takes a time zone."
        },
        {
            // A calendar in the same zone is still a calendar, which only a duration counts in.
            new TimerDefinitionModel { Cron = "0 8 * * *", TimeZone = "Etc/UTC", Calendar = "examples.weekdays" },
            "Timer event #1 names a calendar, but only a timeDuration can be counted in a business calendar."
        }
    };

    [Theory]
    [MemberData(nameof(InvalidTimerStarts))]
    public void Validate_RejectsAnInvalidCronTimerStart(TimerDefinitionModel timer, string expected)
    {
        var error = Assert.Throws<WorkflowDomainException>(() =>
            TimerExpressionDefinitionTests.Validate(TimerStartModel(timer)));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void Validate_RejectsAZoneWithOtherCasing()
    {
        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(
            TimerStartModel(new TimerDefinitionModel { Cron = "0 8 * * *", TimeZone = "europe/london" })));

        // Linux hosts never find another casing; Windows can, and then names the exact one.
        Assert.Contains(error.Message, MiscasedZoneMessages("Timer start event #1 timer.timeZone"));
    }

    internal static string[] MiscasedZoneMessages(string prefix) =>
    [
        $"{prefix} 'europe/london' must be written 'Europe/London'.",
        $"{prefix} 'europe/london' is not an IANA time zone that this server knows, such as Europe/London."
    ];

    [Fact]
    public void Validate_KeepsTheThreeWayMessageOnCatchesAndBoundaries()
    {
        var model = TimerExpressionDefinitionTests.Model("2030-01-01T09:00:00Z", "PT1H");
        model.FlowNodes.Single(node => node.Id == 4).Timer = new TimerDefinitionModel();

        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(model));

        Assert.Equal("Timer event #4 must define exactly one of timeDate, timeDuration, or timeCycle.", error.Message);
    }

    [Theory]
    [InlineData(3, "cron", "Timer catch event #3 timer.cron is supported only on timer start events.")]
    [InlineData(3, "timeZone", "Timer catch event #3 timer.timeZone is supported only on timer start events.")]
    [InlineData(4, "cron", "Timer boundary event #4 timer.cron is supported only on timer start events.")]
    [InlineData(4, "timeZone", "Timer boundary event #4 timer.timeZone is supported only on timer start events.")]
    public void Validate_RejectsCronOnCatchesAndBoundaries(int nodeId, string property, string expected)
    {
        var model = TimerExpressionDefinitionTests.Model("2030-01-01T09:00:00Z", "PT1H");
        var timer = model.FlowNodes.Single(node => node.Id == nodeId).Timer!;
        if (property == "cron")
        {
            timer.Cron = "0 8 * * *";
        }
        else
        {
            timer.TimeZone = "Europe/London";
        }

        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(model));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void Validate_RejectsCronOnACatchEvenWithoutAnotherSchedule()
    {
        var model = TimerExpressionDefinitionTests.Model("2030-01-01T09:00:00Z", "PT1H");
        model.FlowNodes.Single(node => node.Id == 3).Timer = new TimerDefinitionModel { Cron = "0 8 * * *", TimeZone = "Etc/UTC" };

        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(model));

        Assert.Equal("Timer catch event #3 timer.cron is supported only on timer start events.", error.Message);
    }

    internal static WorkflowModel TimerStartModel(TimerDefinitionModel timer) => new()
    {
        Id = "cron-definition",
        Name = "Cron definition",
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Scheduled", Type = BpmnFlowNodeTypes.TimerStartEvent, Timer = timer },
            new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
    };
}
