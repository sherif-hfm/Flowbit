using System.Text.Json;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class CronScheduleRulesTests
{
    public static TheoryData<string> AcceptedExpressions() => new(CronScheduleFixtures.Accepted);

    public static TheoryData<string, string> RejectedExpressions()
    {
        var data = new TheoryData<string, string>();
        foreach (var (cron, problem) in CronScheduleFixtures.Rejected)
        {
            data.Add(cron, problem);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AcceptedExpressions))]
    public void TryParse_AcceptsTheDialect(string cron)
    {
        Assert.True(CronScheduleRules.TryParse(cron, out var fields, out var problem), problem);
        Assert.NotNull(fields);
        Assert.Null(CronScheduleRules.Problem(cron));
    }

    [Theory]
    [MemberData(nameof(RejectedExpressions))]
    public void TryParse_RejectsWithASpecificProblem(string cron, string expected)
    {
        Assert.False(CronScheduleRules.TryParse(cron, out var fields, out var problem));
        Assert.Null(fields);
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        Assert.Equal(
            "must be a cron expression with five fields: minute, hour, day of month, month, and day of week.",
            CronScheduleRules.Problem(null));
    }

    [Fact]
    public void TryParse_AcceptsExactlyTheLengthLimit()
    {
        // 59 one-digit items and a two-digit one make a 120-character minute field; four " *" fields make 128.
        var cron = string.Join(',', Enumerable.Repeat("1", 59)) + ",10 * * * *";
        Assert.Equal(128, cron.Length);
        Assert.Null(CronScheduleRules.Problem(cron));
        Assert.Equal("must contain at most 128 characters.", CronScheduleRules.Problem(cron + "1"));
    }

    [Fact]
    public void TryParse_ReadsEachFieldBoundaryAndStep()
    {
        Assert.True(CronScheduleRules.TryParse("0,59 0,23 1,31 1,12 0,6", out var bounds, out _));
        Assert.Equal((1UL << 0) | (1UL << 59), bounds.Minutes);
        Assert.Equal((1u << 0) | (1u << 23), bounds.Hours);
        Assert.Equal((1u << 1) | (1u << 31), bounds.DaysOfMonth);
        Assert.Equal((ushort)((1 << 1) | (1 << 12)), bounds.Months);
        Assert.Equal((byte)((1 << 0) | (1 << 6)), bounds.DaysOfWeek);

        Assert.True(CronScheduleRules.TryParse("*/20 9-17/4 1/10 */5 1-5/2", out var steps, out _));
        Assert.Equal(Bits(0, 20, 40), steps.Minutes);
        Assert.Equal((uint)Bits(9, 13, 17), steps.Hours);
        Assert.Equal((uint)Bits(1, 11, 21, 31), steps.DaysOfMonth);
        Assert.Equal((ushort)Bits(1, 6, 11), steps.Months);
        Assert.Equal((byte)Bits(1, 3, 5), steps.DaysOfWeek);

        Assert.True(CronScheduleRules.TryParse("05 08 * * */2", out var padded, out _));
        Assert.Equal(Bits(5), padded.Minutes);
        Assert.Equal((uint)Bits(8), padded.Hours);
        Assert.Equal((byte)Bits(0, 2, 4, 6), padded.DaysOfWeek);
    }

    [Fact]
    public void TryParse_CollapsesRepeatedListItems()
    {
        Assert.True(CronScheduleRules.TryParse("1,1,2-3,3 * * * *", out var fields, out _));
        Assert.Equal(Bits(1, 2, 3), fields.Minutes);
    }

    [Theory]
    [InlineData("0 17 L * *", true, false)]
    [InlineData("0 17 L * 5", true, true)]
    [InlineData("0 8 13 * 5", false, true)]
    [InlineData("0 8 * * 1-5", false, false)]
    [InlineData("0 8 */2 * 1", false, false)]
    [InlineData("0 8 1 * */2", false, false)]
    public void TryParse_ReadsLastDayAndBothRestrictedDayFields(string cron, bool lastDay, bool bothDays)
    {
        Assert.True(CronScheduleRules.TryParse(cron, out var fields, out _));
        Assert.Equal(lastDay, fields.LastDayOfMonth);
        Assert.Equal(bothDays, fields.RestrictsBothDays);
    }

    [Fact]
    public void CountConfiguredExpressions_CountsCronAsAFourthSchedule()
    {
        Assert.Equal(1, TimerDefinitionRules.CountConfiguredExpressions(new TimerDefinitionModel { Cron = "0 8 * * *" }));
        Assert.Equal(2, TimerDefinitionRules.CountConfiguredExpressions(
            new TimerDefinitionModel { Cron = "0 8 * * *", TimeDuration = "PT1H" }));
        Assert.Equal(0, TimerDefinitionRules.CountConfiguredExpressions(
            new TimerDefinitionModel { Cron = "  ", TimeZone = "Europe/London" }));
    }

    [Fact]
    public void Json_OmitsCronAndTimeZoneWhenAbsentAndRoundTripsThem()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(
            """{"timeDuration":"PT1H"}""",
            JsonSerializer.Serialize(new TimerDefinitionModel { TimeDuration = "PT1H" }, options));

        var json = """{"cron":"0 8 * * 1-5","timeZone":"Europe/London"}""";
        var timer = JsonSerializer.Deserialize<TimerDefinitionModel>(json, options)!;
        Assert.Equal("0 8 * * 1-5", timer.Cron);
        Assert.Equal("Europe/London", timer.TimeZone);
        Assert.Equal(json, JsonSerializer.Serialize(timer, options));
    }

    [Fact]
    public void Migrator_TrimsCronAndZoneWithoutRewritingTheExpression()
    {
        var model = new WorkflowModel
        {
            Id = "cron-trim",
            Name = "Cron trim",
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Scheduled",
                    Type = BpmnFlowNodeTypes.TimerStartEvent,
                    Timer = new TimerDefinitionModel { Cron = "  0\t1-2 * * 1,2,3  ", TimeZone = " Europe/London " }
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Wait",
                    Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H", Cron = " ", TimeZone = "Etc/UTC" }
                }
            ]
        };

        WorkflowModelMigrator.Normalize(model);

        var start = model.FlowNodes[0].Timer!;
        Assert.Equal("0\t1-2 * * 1,2,3", start.Cron);
        Assert.Equal("Europe/London", start.TimeZone);
        // A catch keeps a zone it cannot use, so validation reports it; blank text becomes absent.
        var wait = model.FlowNodes[1].Timer!;
        Assert.Null(wait.Cron);
        Assert.Equal("Etc/UTC", wait.TimeZone);
    }

    [Fact]
    public void Migrator_LeavesOlderTimerDocumentsUnchanged()
    {
        const string json = """
            {"id":"old-timer","name":"Old timer","initialEventId":null,"variables":[],"lanes":[],
             "flowNodes":[{"id":1,"name":"Every two days","type":"timerStartEvent","timer":{"timeCycle":"R/P2D"}}],
             "sequenceFlows":[]}
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var model = JsonSerializer.Deserialize<WorkflowModel>(json, options)!;

        WorkflowModelMigrator.Normalize(model);

        var timer = JsonSerializer.SerializeToElement(model.FlowNodes[0].Timer, options);
        Assert.Equal("""{"timeCycle":"R/P2D"}""", timer.GetRawText());
    }

    private static ulong Bits(params int[] values) =>
        values.Aggregate(0UL, (bits, value) => bits | (1UL << value));
}
