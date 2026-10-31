using System.Text.Json;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class ErrorBoundaryMatchRulesTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("""{ "httpStatusCodes": ["409"] }""")]
    [InlineData("""{ "httpStatusCodes": [409.5] }""")]
    [InlineData("""{ "httpStatusCodes": [true] }""")]
    [InlineData("""{ "httpStatusCodes": [null] }""")]
    [InlineData("""{ "httpStatusRanges": [{ "from": "400", "to": 499 }] }""")]
    [InlineData("""{ "httpStatusRangez": [{ "from": 400, "to": 499 }] }""")]
    [InlineData("""{ "httpStatusRanges": [{ "from": 400, "to": 499, "name": "x" }] }""")]
    public void WebDefaults_RejectWhatWouldChangeRouting(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ErrorBoundaryMatchModel>(json, WebOptions));
    }

    [Fact]
    public void WebDefaults_ReachTheModelThroughAWholeNode()
    {
        var node = JsonSerializer.Deserialize<FlowNodeModel>(
            """{ "id": 1, "name": "x", "type": "errorBoundaryEvent", "errorMatch": { "httpStatusCodes": [409] } }""",
            WebOptions)!;
        Assert.Equal([409], node.ErrorMatch!.HttpStatusCodes);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FlowNodeModel>(
            """{ "id": 1, "name": "x", "type": "errorBoundaryEvent", "errorMatch": { "httpStatusCodes": ["409"] } }""",
            WebOptions));
    }

    [Theory]
    [InlineData("""{ }""", "at least one")]
    [InlineData("""{ "httpStatusCodes": [], "httpStatusRanges": [] }""", "at least one")]
    [InlineData("""{ "httpStatusCodes": null, "httpStatusRanges": null }""", "at least one")]
    [InlineData("""{ "httpStatusCodes": [99] }""", "from 100 through 599")]
    [InlineData("""{ "httpStatusCodes": [0] }""", "from 100 through 599")]
    [InlineData("""{ "httpStatusCodes": [600] }""", "from 100 through 599")]
    [InlineData("""{ "httpStatusCodes": [409, 409] }""", "more than once")]
    [InlineData("""{ "httpStatusRanges": [null] }""", "cannot be null")]
    [InlineData("""{ "httpStatusRanges": [{ "from": 400 }] }""", "both endpoints")]
    [InlineData("""{ "httpStatusRanges": [{ "to": 499 }] }""", "both endpoints")]
    [InlineData("""{ "httpStatusRanges": [{ "from": 499, "to": 400 }] }""", "less than")]
    [InlineData("""{ "httpStatusRanges": [{ "from": 400, "to": 400 }] }""", "less than")]
    [InlineData("""{ "httpStatusRanges": [{ "from": 400, "to": 450 }, { "from": 450, "to": 499 }] }""", "overlaps")]
    [InlineData("""{ "httpStatusCodes": [409], "httpStatusRanges": [{ "from": 400, "to": 499 }] }""", "already covers")]
    public void FindMatcherProblem_RejectsInvalidMatchers(string json, string expected)
    {
        var match = JsonSerializer.Deserialize<ErrorBoundaryMatchModel>(json, WebOptions)!;
        Assert.Contains(expected, ErrorBoundaryMatchRules.FindMatcherProblem(match), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "httpStatusCodes": [100, 599] }""")]
    [InlineData("""{ "httpStatusCodes": [409], "httpStatusRanges": null }""")]
    [InlineData("""{ "httpStatusCodes": [], "httpStatusRanges": [{ "from": 100, "to": 599 }] }""")]
    [InlineData("""{ "httpStatusCodes": [399], "httpStatusRanges": [{ "from": 400, "to": 449 }, { "from": 450, "to": 499 }] }""")]
    public void FindMatcherProblem_AcceptsValidMatchers(string json)
    {
        var match = JsonSerializer.Deserialize<ErrorBoundaryMatchModel>(json, WebOptions)!;
        Assert.Null(ErrorBoundaryMatchRules.FindMatcherProblem(match));
    }

    [Fact]
    public void FindMatcherProblem_CountsSelectorsBeforeDeduplication()
    {
        var atLimit = new ErrorBoundaryMatchModel { HttpStatusCodes = Enumerable.Range(400, 32).ToList() };
        var overLimit = new ErrorBoundaryMatchModel { HttpStatusCodes = Enumerable.Range(400, 33).ToList() };
        var duplicates = new ErrorBoundaryMatchModel { HttpStatusCodes = Enumerable.Repeat(409, 33).ToList() };

        Assert.Null(ErrorBoundaryMatchRules.FindMatcherProblem(atLimit));
        Assert.Contains("at most 32", ErrorBoundaryMatchRules.FindMatcherProblem(overLimit));
        Assert.Contains("at most 32", ErrorBoundaryMatchRules.FindMatcherProblem(duplicates));
    }

    [Theory]
    [InlineData(409, 30, ErrorBoundaryMatchTier.ExactStatus)]
    [InlineData(400, 31, ErrorBoundaryMatchTier.StatusRange)]
    [InlineData(499, 31, ErrorBoundaryMatchTier.StatusRange)]
    [InlineData(500, 32, ErrorBoundaryMatchTier.StatusRange)]
    [InlineData(599, 32, ErrorBoundaryMatchTier.StatusRange)]
    [InlineData(399, 33, ErrorBoundaryMatchTier.CatchAll)]
    [InlineData(0, 33, ErrorBoundaryMatchTier.CatchAll)]
    [InlineData(600, 33, ErrorBoundaryMatchTier.CatchAll)]
    [InlineData(null, 33, ErrorBoundaryMatchTier.CatchAll)]
    public void Select_FollowsPrecedenceRegardlessOfDefinitionOrder(
        int? status,
        int expectedBoundary,
        ErrorBoundaryMatchTier expectedTier)
    {
        var forward = HostWithBoundaries(CatchAll(33), Exact(30, 409), Range(31, 400, 499), Range(32, 500, 599));
        var backward = HostWithBoundaries(Range(32, 500, 599), Range(31, 400, 499), Exact(30, 409), CatchAll(33));

        foreach (var definition in new[] { forward, backward })
        {
            var selection = ErrorBoundaryMatchRules.Select(definition, 10, status);
            Assert.Equal(expectedBoundary, selection.Boundary?.Id);
            Assert.Equal(expectedTier, selection.Tier);
            Assert.Null(selection.Problem);
        }
    }

    [Fact]
    public void Select_WithoutACatchAllFindsNoBoundaryForUnmatchedOrStatuslessFailures()
    {
        var definition = HostWithBoundaries(Exact(30, 409));

        Assert.Null(ErrorBoundaryMatchRules.Select(definition, 10, 500).Boundary);
        Assert.Null(ErrorBoundaryMatchRules.Select(definition, 10, null).Boundary);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(null)]
    public void Select_ReportsAmbiguousDataInsteadOfChoosing(int? status)
    {
        var definition = status is null
            ? HostWithBoundaries(CatchAll(30), CatchAll(31))
            : HostWithBoundaries(Exact(30, 409), Exact(31, 409));

        var selection = ErrorBoundaryMatchRules.Select(definition, 10, status);

        Assert.Null(selection.Boundary);
        Assert.Contains("#30", selection.Problem);
        Assert.Contains("#31", selection.Problem);
    }

    [Fact]
    public void Select_NeverTreatsAnEmptyMatcherAsACatchAll()
    {
        var definition = HostWithBoundaries(new FlowNodeModel
        {
            Id = 30,
            Name = "Empty",
            Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            AttachedToRef = 10,
            ErrorMatch = new ErrorBoundaryMatchModel()
        });

        Assert.Null(ErrorBoundaryMatchRules.Select(definition, 10, null).Boundary);
        Assert.Null(ErrorBoundaryMatchRules.Select(definition, 10, 409).Boundary);
    }

    [Fact]
    public void Normalize_SortsValidMatchersAndKeepsInvalidOnesForValidation()
    {
        var model = HostWithBoundaries(
            new FlowNodeModel
            {
                Id = 30,
                Name = "Valid",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 10,
                ErrorMatch = new ErrorBoundaryMatchModel
                {
                    HttpStatusCodes = [503, 409],
                    HttpStatusRanges =
                    [
                        new HttpStatusRangeModel { From = 500, To = 502 },
                        new HttpStatusRangeModel { From = 400, To = 408 }
                    ]
                }
            },
            new FlowNodeModel
            {
                Id = 31,
                Name = "Empty arrays",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 10,
                ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = [422], HttpStatusRanges = [] }
            },
            new FlowNodeModel
            {
                Id = 32,
                Name = "Invalid",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 10,
                ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = [500, 500, 100], HttpStatusRanges = [] }
            });

        WorkflowModelMigrator.Normalize(model);

        var valid = model.FlowNodes.Single(node => node.Id == 30).ErrorMatch!;
        Assert.Equal([409, 503], valid.HttpStatusCodes);
        Assert.Equal([400, 500], valid.HttpStatusRanges!.Select(range => range.From));
        Assert.Null(model.FlowNodes.Single(node => node.Id == 31).ErrorMatch!.HttpStatusRanges);
        var invalid = model.FlowNodes.Single(node => node.Id == 32).ErrorMatch!;
        Assert.Equal([500, 500, 100], invalid.HttpStatusCodes);
        Assert.NotNull(invalid.HttpStatusRanges);
    }

    [Fact]
    public void Normalize_LeavesOldCatchAllsWithoutAMatcherAndDropsMisplacedOnes()
    {
        var model = HostWithBoundaries(CatchAll(30));
        model.FlowNodes.Single(node => node.Id == 10).ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = [409] };

        WorkflowModelMigrator.Normalize(model);

        Assert.Null(model.FlowNodes.Single(node => node.Id == 30).ErrorMatch);
        Assert.Null(model.FlowNodes.Single(node => node.Id == 10).ErrorMatch);
        Assert.DoesNotContain(
            "errorMatch",
            JsonSerializer.Serialize(model.FlowNodes.Single(node => node.Id == 30)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoringJson_NamesMissingEndpointsAndUnknownMembers()
    {
        var node = JsonDocument.Parse(
            """
            {
              "id": 30,
              "name": "x",
              "type": "errorBoundaryEvent",
              "errorMatch": {
                "httpStatusCodes": ["409"],
                "httpStatusRanges": [{ "from": 400 }, null],
                "other": 1
              }
            }
            """).RootElement;

        var issues = WorkflowAuthoringJson.InspectItem(node, typeof(FlowNodeModel));

        Assert.Contains(issues, issue => issue.Code == "wrong-type" && issue.Pointer == "/errorMatch/httpStatusCodes/0");
        Assert.Contains(issues, issue => issue.Code == "missing-property" && issue.Pointer == "/errorMatch/httpStatusRanges/0");
        Assert.Contains(issues, issue => issue.Code == "wrong-type" && issue.Pointer == "/errorMatch/httpStatusRanges/1");
        Assert.Contains(issues, issue => issue.Code == "unknown-property" && issue.Pointer == "/errorMatch/other");
    }

    [Fact]
    public void AuthoringJson_AcceptsNullSelectorArraysAndPublishesTheSchema()
    {
        var node = JsonDocument.Parse(
            """{ "id": 30, "name": "x", "type": "errorBoundaryEvent", "errorMatch": { "httpStatusCodes": [409], "httpStatusRanges": null } }""").RootElement;

        Assert.Empty(WorkflowAuthoringJson.InspectItem(node, typeof(FlowNodeModel)));
        var schema = JsonDocument.Parse(WorkflowAuthoringJson.CreateSchema()).RootElement;
        var range = schema.GetProperty("$defs").GetProperty(nameof(HttpStatusRangeModel));
        Assert.Equal(["from", "to"], range.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.False(range.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Create_AcceptsTheDocumentedExample()
    {
        var model = ValidatedModel(
            Exact(30, 409),
            Range(31, 400, 499),
            Range(32, 500, 599),
            CatchAll(33));

        Validate(model);
    }

    [Fact]
    public void Create_AcceptsEightBoundariesAndAdjacentRanges()
    {
        var model = ValidatedModel(
            Range(30, 400, 449),
            Range(31, 450, 499),
            Exact(32, 500),
            Exact(33, 501),
            Exact(34, 502),
            Exact(35, 503),
            Exact(36, 504),
            CatchAll(37));

        Validate(model);
    }

    public static TheoryData<string, string, FlowNodeModel[]> InvalidHosts => new()
    {
        { "nine boundaries", "at most 8", [
            Exact(30, 500), Exact(31, 501), Exact(32, 502), Exact(33, 503), Exact(34, 504),
            Exact(35, 505), Exact(36, 506), Exact(37, 507), Exact(38, 508)] },
        { "two catch-alls", "more than one catch-all", [CatchAll(30), CatchAll(31)] },
        { "duplicate exact", "both match HTTP status 409", [Exact(30, 409), Exact(31, 409)] },
        { "touching ranges", "overlapping", [Range(30, 400, 450), Range(31, 450, 499)] },
        { "empty matcher", "at least one", [new FlowNodeModel
        {
            Id = 30,
            Name = "Empty",
            Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            AttachedToRef = 10,
            ErrorMatch = new ErrorBoundaryMatchModel()
        }] }
    };

    [Theory]
    [MemberData(nameof(InvalidHosts))]
    public void Create_RejectsInvalidBoundarySets(string name, string expected, FlowNodeModel[] boundaries)
    {
        Assert.NotEmpty(name);
        var error = Assert.Throws<WorkflowDomainException>(() => Validate(ValidatedModel(boundaries)));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Contains("#", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_AllowsTheSameExactStatusOnDifferentHosts()
    {
        var model = ValidatedModel(Exact(30, 409));
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 11,
            Name = "Second call",
            Type = BpmnFlowNodeTypes.ServiceTask,
            Service = new ServiceTaskModel { Url = "https://tests.local/second", Method = "GET" }
        });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 40,
            Name = "Second boundary",
            Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            AttachedToRef = 11,
            ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = [409] }
        });
        model.SequenceFlows.Single(flow => flow.SourceRef == 10).TargetRef = 11;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 2 });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 4001, SourceRef = 40, TargetRef = 2 });

        Validate(model);
    }

    [Fact]
    public void Create_RejectsAMatcherOnAnotherNodeTypeBeforeMigrationErasesIt()
    {
        var model = ValidatedModel(CatchAll(30));
        model.FlowNodes.Single(node => node.Id == 10).ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = [409] };

        var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));

        Assert.Contains("Flow node #10 defines errorMatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsAMatcherOnAScriptHostAndKeepsItsSingleBoundary()
    {
        var model = ValidatedModel(Exact(30, 409));
        var host = model.FlowNodes.Single(node => node.Id == 10);
        host.Type = BpmnFlowNodeTypes.ScriptTask;
        host.Service = null;
        host.Assignments = [];

        var matcher = Assert.Throws<WorkflowDomainException>(() => Validate(model));
        Assert.Contains("not a REST service task", matcher.Message, StringComparison.Ordinal);

        var two = ValidatedModel(CatchAll(30), CatchAll(31));
        var twoHost = two.FlowNodes.Single(node => node.Id == 10);
        twoHost.Type = BpmnFlowNodeTypes.ScriptTask;
        twoHost.Service = null;
        twoHost.Assignments = [];
        var count = Assert.Throws<WorkflowDomainException>(() => Validate(two));
        Assert.Contains("at most one is allowed", count.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ChecksEveryBoundarysErrorVariableAgainstHostOutputs()
    {
        var model = ValidatedModel(CatchAll(30), Exact(31, 409));
        model.FlowNodes.Single(node => node.Id == 10).Service!.OutputMappings =
        [
            new ServiceOutputMappingModel
            {
                Variable = "failure",
                Path = "result",
                DataType = WorkflowVariableTypes.String,
                IsArray = false
            }
        ];
        model.FlowNodes.Single(node => node.Id == 31).ErrorVariable = "failure";
        model.Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "failure",
                DataType = WorkflowVariableTypes.String,
                Nullable = true
            }
        ];

        var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));
        Assert.Contains("#31", error.Message, StringComparison.Ordinal);
    }

    private static FlowNodeModel Exact(int id, params int[] codes) => new()
    {
        Id = id,
        Name = "Exact " + id,
        Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
        AttachedToRef = 10,
        ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = codes.ToList() }
    };

    private static FlowNodeModel Range(int id, int from, int to) => new()
    {
        Id = id,
        Name = "Range " + id,
        Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
        AttachedToRef = 10,
        ErrorMatch = new ErrorBoundaryMatchModel
        {
            HttpStatusRanges = [new HttpStatusRangeModel { From = from, To = to }]
        }
    };

    private static FlowNodeModel CatchAll(int id) => new()
    {
        Id = id,
        Name = "Catch all " + id,
        Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
        AttachedToRef = 10
    };

    private static WorkflowModel HostWithBoundaries(params FlowNodeModel[] boundaries) => new()
    {
        Id = "matchers",
        Name = "Matchers",
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 10,
                Name = "Call",
                Type = BpmnFlowNodeTypes.ServiceTask,
                Service = new ServiceTaskModel { Url = "https://tests.local/call", Method = "GET" }
            },
            .. boundaries
        ]
    };

    private static WorkflowModel ValidatedModel(params FlowNodeModel[] boundaries)
    {
        var model = new WorkflowModel
        {
            Id = "matchers",
            Name = "Matchers",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 10,
                    Name = "Call",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    Service = new ServiceTaskModel { Url = "https://tests.local/call", Method = "GET" }
                },
                .. boundaries
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 2 },
                .. boundaries.Select(boundary => new SequenceFlowModel
                {
                    Id = boundary.Id * 100 + 1,
                    SourceRef = boundary.Id,
                    TargetRef = 2
                })
            ]
        };
        return model;
    }

    private static void Validate(WorkflowModel model)
    {
        var validator = new WorkflowDefinitionValidator(
            new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
            new ServiceTaskOptions());
        validator.ValidateAuthored(model);
        WorkflowModelMigrator.Normalize(model);
        validator.ValidateNormalized(model);
    }
}
