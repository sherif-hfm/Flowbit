using System.Text.Json;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// The call JSON contract: strict parsing, local validation, target checks, and the
/// publication gate, without a database.
/// </summary>
public sealed class CallActivityDefinitionTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static WorkflowDefinitionValidator Validator() => new(
        new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
        new ServiceTaskOptions());

    private static void Validate(WorkflowModel model)
    {
        var validator = Validator();
        validator.ValidateAuthored(model);
        WorkflowModelMigrator.Normalize(model);
        validator.ValidateNormalized(model);
    }

    private static WorkflowDomainException Rejected(WorkflowModel model) =>
        Assert.Throws<WorkflowDomainException>(() => Validate(model));

    private static WorkflowModel ValidParent(
        IReadOnlyList<CallVariableMappingModel>? inputs = null,
        IReadOnlyList<CallVariableMappingModel>? outputs = null,
        bool withBoundary = false,
        string? errorVariable = null) => Parent(
        "parent",
        "target",
        withBoundary,
        errorVariable,
        startInputs: [Input("requestedAmount")],
        variables: [Process("managerApproved"), Process("error", WorkflowVariableTypes.String)],
        inputs,
        outputs);

    private static FlowNodeModel CallNode(WorkflowModel model) =>
        model.FlowNodes.Single(node => node.Id == Call);

    [Fact]
    public void A_valid_call_round_trips_and_omits_what_it_does_not_set()
    {
        var model = ValidParent(
            inputs: [Mapping("requestedAmount", "amount")],
            outputs: [Mapping("approved", "managerApproved", WorkflowVariableTypes.Boolean)]);
        CallNode(model).CallActivity!.VersionBinding = CallActivityVersionBindings.PublishedDefault;

        Validate(model);

        var json = JsonSerializer.Serialize(model);
        Assert.Contains("\"callActivity\":", json);
        Assert.DoesNotContain("versionBinding", json);
        Assert.DoesNotContain("callOnly", json);
        Assert.DoesNotContain("\"isArray\":false", JsonSerializer.Serialize(CallNode(model).CallActivity));
        var reloaded = JsonSerializer.Deserialize<WorkflowModel>(json, WebJson)!;
        WorkflowModelMigrator.Normalize(reloaded);
        Assert.Equal("amount", CallNode(reloaded).CallActivity!.InputMappings.Single().Variable);
    }

    [Fact]
    public void An_older_definition_has_no_call_properties_after_a_round_trip()
    {
        var model = ImmediateChild("plain");
        WorkflowModelMigrator.Normalize(model);
        var json = JsonSerializer.Serialize(model);
        Assert.DoesNotContain("callActivity", json);
        Assert.DoesNotContain("callOnly", json);
    }

    [Fact]
    public void Normalizing_persists_the_declared_spelling_of_mapped_names()
    {
        var model = ValidParent(
            inputs: [Mapping("REQUESTEDamount", "amount")],
            outputs: [Mapping("approved", "MANAGERapproved", WorkflowVariableTypes.Boolean)]);

        Validate(model);

        var call = CallNode(model).CallActivity!;
        Assert.Equal("requestedAmount", call.InputMappings.Single().SourceVariable);
        Assert.Equal("managerApproved", call.OutputMappings.Single().Variable);
    }

    [Theory]
    [InlineData("""{"workflowKey":"t","unknown":1}""")]
    [InlineData("""{"workflowKey":5}""")]
    [InlineData("""{"workflowKey":"t","inputMappings":[{"sourceVariable":"a","variable":"b","dataType":"number","required":"true"}]}""")]
    [InlineData("""{"workflowKey":"t","inputMappings":[{"sourceVariable":"a","variable":"b","dataType":"number","path":"x"}]}""")]
    [InlineData("""{"workflowKey":"t","inputMappings":[{"sourceVariable":"a","variable":"b","dataType":"number","isArray":1}]}""")]
    [InlineData("""{"workflowKey":"t","inputMappings":{"sourceVariable":"a"}}""")]
    public void The_web_json_binder_refuses_wrong_types_and_unknown_members(string config)
    {
        var json = $$"""{"id":2,"name":"n","type":"callActivity","callActivity":{{config}}}""";
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<FlowNodeModel>(json, WebJson));
    }

    [Fact]
    public void The_authoring_contract_reports_call_problems_with_their_pointers()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id": 2, "name": "n", "type": "callActivity",
              "callActivity": {
                "workflowKey": "t",
                "versionBinding": "latest",
                "inputMappings": [ { "sourceVariable": "a", "dataType": "text", "extra": 1 } ]
              }
            }
            """);

        var issues = WorkflowAuthoringJson.InspectItem(document.RootElement, typeof(FlowNodeModel));

        Assert.Contains(issues, issue => issue.Pointer == "/callActivity/versionBinding" && issue.Code == "unsupported-value");
        Assert.Contains(issues, issue => issue.Pointer == "/callActivity/inputMappings/0" && issue.Code == "missing-property");
        Assert.Contains(issues, issue => issue.Pointer == "/callActivity/inputMappings/0/dataType" && issue.Code == "unsupported-value");
        Assert.Contains(issues, issue => issue.Pointer == "/callActivity/inputMappings/0/extra" && issue.Code == "unknown-property");
    }

    public static TheoryData<string, Action<WorkflowModel>, string> InvalidShapes => new()
    {
        { "no config", model => CallNode(model).CallActivity = null, "must define callActivity" },
        { "blank key", model => CallNode(model).CallActivity!.WorkflowKey = " ", "workflow key" },
        { "padded key", model => CallNode(model).CallActivity!.WorkflowKey = " target", "whitespace" },
        { "long key", model => CallNode(model).CallActivity!.WorkflowKey = new string('k', 301), "at most 300" },
        { "interpolated key", model => CallNode(model).CallActivity!.WorkflowKey = "${family}", "not replaced" },
        { "self call", model => CallNode(model).CallActivity!.WorkflowKey = "parent", "own workflow family" },
        { "unknown binding", model => CallNode(model).CallActivity!.VersionBinding = "latest", "versionBinding" },
        { "null mappings", model => CallNode(model).CallActivity!.InputMappings = null!, "must be an array" },
        {
            "too many",
            model => CallNode(model).CallActivity!.InputMappings = Enumerable.Range(0, 101)
                .Select(index => Mapping("requestedAmount", "v" + index))
                .ToList(),
            "at most 100"
        },
        {
            "duplicate destination",
            model => CallNode(model).CallActivity!.InputMappings =
            [
                Mapping("requestedAmount", "amount"),
                Mapping("requestedAmount", "AMOUNT")
            ],
            "more than once"
        },
        {
            "reserved source",
            model => CallNode(model).CallActivity!.InputMappings = [Mapping("sys.user", "who", WorkflowVariableTypes.String)],
            "reserved context prefix"
        },
        {
            "reserved destination",
            model => CallNode(model).CallActivity!.InputMappings = [Mapping("requestedAmount", "config.amount")],
            "reserved context prefix"
        },
        {
            "unknown type",
            model => CallNode(model).CallActivity!.InputMappings = [Mapping("requestedAmount", "amount", "text")],
            "dataType"
        },
        {
            "undeclared input source",
            model => CallNode(model).CallActivity!.InputMappings = [Mapping("nowhere", "amount")],
            "never declared"
        },
        {
            "mismatched input type",
            model => CallNode(model).CallActivity!.InputMappings = [Mapping("requestedAmount", "amount", WorkflowVariableTypes.String)],
            "not declared as string"
        },
        {
            "mismatched input array",
            model => CallNode(model).CallActivity!.InputMappings = [Mapping("requestedAmount", "amount", isArray: true)],
            "not declared as number[]"
        },
        {
            "undeclared output target",
            model => CallNode(model).CallActivity!.OutputMappings = [Mapping("a", "nowhere")],
            "declared process variable"
        },
        {
            "output target of another type",
            model => CallNode(model).CallActivity!.OutputMappings = [Mapping("a", "managerApproved", WorkflowVariableTypes.String)],
            "is boolean"
        },
        {
            "async before",
            model => CallNode(model).AsyncBefore = true,
            "always durable"
        },
        {
            "job policy",
            model => CallNode(model).Job = new JobPolicyModel(),
            "always durable"
        },
        {
            "multi instance",
            model => CallNode(model).MultiInstance = new MultiInstanceModel(),
            "multiInstance"
        },
        {
            "user task settings",
            model => CallNode(model).Roles = ["approver"],
            "only callActivity"
        },
        {
            "no incoming flow",
            model => model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = End,
            "incoming"
        },
        {
            "conditional outgoing flow",
            model => model.SequenceFlows.Single(flow => flow.SourceRef == Call).Condition = "true",
            "unconditional"
        },
        {
            "two outgoing flows",
            model => model.SequenceFlows.Add(new SequenceFlowModel { Id = 299, SourceRef = Call, TargetRef = End }),
            "exactly one"
        }
    };

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public void An_invalid_call_is_rejected_with_an_actionable_message(
        string _,
        Action<WorkflowModel> damage,
        string expected)
    {
        var model = ValidParent();
        damage(model);
        Assert.Contains(expected, Rejected(model).Message);
    }

    [Fact]
    public void A_config_on_the_wrong_node_type_is_rejected_before_normalization_drops_it()
    {
        var model = ValidParent();
        model.FlowNodes.Single(node => node.Id == End).CallActivity = new CallActivityModel { WorkflowKey = "target" };
        Assert.Contains("is not a call activity", Rejected(model).Message);

        var flagged = ValidParent();
        flagged.FlowNodes.Single(node => node.Id == End).CallOnly = true;
        Assert.Contains("is not a start event", Rejected(flagged).Message);
    }

    [Fact]
    public void A_source_may_be_any_stored_variable_the_workflow_declares_or_produces()
    {
        var model = ValidParent(inputs:
        [
            Mapping("requestedAmount", "fromStart"),
            Mapping("managerApproved", "fromProcess", WorkflowVariableTypes.Boolean, required: false),
            Mapping("flowValue", "fromFlow", WorkflowVariableTypes.String),
            Mapping("serviceValue", "fromService"),
            Mapping("serviceStatus", "fromStatus")
        ]);
        model.FlowNodes.Add(new FlowNodeModel { Id = 7, Name = "Review", Type = BpmnFlowNodeTypes.UserTask });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 8,
            Name = "Remote",
            Type = BpmnFlowNodeTypes.ServiceTask,
            AsyncBefore = true,
            Service = new ServiceTaskModel
            {
                Url = "https://tests.local/a",
                Method = "GET",
                TimeoutSeconds = 5,
                StatusVariable = "serviceStatus",
                OutputMappings =
                [
                    new ServiceOutputMappingModel
                    {
                        Variable = "serviceValue",
                        Path = "value",
                        DataType = WorkflowVariableTypes.Number,
                        IsArray = false
                    }
                ]
            }
        });
        model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 7;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 701,
            SourceRef = 7,
            TargetRef = 8,
            Name = "Go",
            Variables = [Input("flowValue", WorkflowVariableTypes.String)]
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 801, SourceRef = 8, TargetRef = Call });

        Validate(model);
    }

    [Fact]
    public void A_shared_alias_cannot_be_a_source_or_an_output_target()
    {
        var shared = new VariableModel
        {
            Id = 30,
            Name = "limit",
            Scope = VariableScopes.Shared,
            SharedKey = "tests.limit",
            Access = SharedVariableAccessModes.ReadWrite,
            DataType = WorkflowVariableTypes.Number
        };

        var source = ValidParent(inputs: [Mapping("limit", "amount")]);
        source.Variables.Add(shared);
        Assert.Contains("shared-variable alias", Rejected(source).Message);

        var target = ValidParent(outputs: [Mapping("a", "limit")]);
        target.Variables.Add(shared);
        Assert.Contains("shared-variable alias", Rejected(target).Message);
    }

    [Fact]
    public void An_output_target_cannot_be_the_boundarys_error_variable()
    {
        var model = ValidParent(
            outputs: [Mapping("a", "error", WorkflowVariableTypes.String)],
            withBoundary: true,
            errorVariable: "error");
        Assert.Contains("errorVariable", Rejected(model).Message);
    }

    [Fact]
    public void Only_one_catch_all_error_boundary_may_attach_and_it_cannot_match_statuses()
    {
        var second = ValidParent(withBoundary: true);
        second.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Another",
            Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            AttachedToRef = Call
        });
        second.FlowNodes.Add(new FlowNodeModel { Id = 7, Name = "End 7", Type = BpmnFlowNodeTypes.EndEvent });
        second.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7 });
        Assert.Contains("at most one", Rejected(second).Message);

        var matched = ValidParent(withBoundary: true);
        matched.FlowNodes.Single(node => node.Id == Boundary).ErrorMatch = new ErrorBoundaryMatchModel
        {
            HttpStatusCodes = [409]
        };
        Assert.Contains("not a REST service task", Rejected(matched).Message);

        foreach (var type in new[] { BpmnFlowNodeTypes.TimerBoundaryEvent, BpmnFlowNodeTypes.ConditionalBoundaryEvent })
        {
            var observed = ValidParent(withBoundary: true);
            var boundary = observed.FlowNodes.Single(node => node.Id == Boundary);
            boundary.Type = type;
            boundary.ErrorVariable = null;
            if (type == BpmnFlowNodeTypes.TimerBoundaryEvent)
            {
                boundary.Timer = new TimerDefinitionModel { TimeDuration = "PT1H" };
            }
            else
            {
                boundary.Conditional = new ConditionalDefinitionModel { Condition = "managerApproved == true" };
            }

            Assert.Contains("durable wait", Rejected(observed).Message);
        }
    }

    [Fact]
    public void Call_only_is_valid_only_on_an_unrestricted_default_start()
    {
        var valid = ImmediateChild("target");
        valid.FlowNodes.Single(node => node.Id == Start).CallOnly = true;
        Validate(valid);

        var roles = ImmediateChild("target");
        roles.FlowNodes.Single(node => node.Id == Start).CallOnly = true;
        roles.FlowNodes.Single(node => node.Id == Start).Roles = ["requester"];
        Assert.Contains("cannot restrict roles", Rejected(roles).Message);

        var idempotent = ImmediateChild("target");
        idempotent.FlowNodes.Single(node => node.Id == Start).CallOnly = true;
        idempotent.FlowNodes.Single(node => node.Id == Start).Idempotency = new IdempotencyModel
        {
            HeaderName = "Idempotency-Key",
            Variable = "requestKey"
        };
        Assert.Contains("cannot define idempotency", Rejected(idempotent).Message);

        // Moving the default leaves the flag behind, and that is an explicit error.
        var moved = ImmediateChild("target");
        moved.FlowNodes.Single(node => node.Id == Start).CallOnly = true;
        moved.FlowNodes.Add(new FlowNodeModel { Id = 9, Name = "Other", Type = BpmnFlowNodeTypes.StartEvent });
        moved.FlowNodes.Add(new FlowNodeModel { Id = 10, Name = "End 2", Type = BpmnFlowNodeTypes.EndEvent });
        moved.SequenceFlows.Add(new SequenceFlowModel { Id = 901, SourceRef = 9, TargetRef = 10 });
        moved.InitialEventId = 9;
        Assert.Contains("is not the default start event", Rejected(moved).Message);
        Assert.True(moved.FlowNodes.Single(node => node.Id == Start).CallOnly);
    }

    [Fact]
    public void A_target_contract_reports_every_problem_with_a_stable_code()
    {
        var parent = ValidParent(
            inputs:
            [
                Mapping("requestedAmount", "amount"),
                Mapping("requestedAmount", "missing"),
                Mapping("requestedAmount", "text")
            ],
            outputs:
            [
                Mapping("nowhere", "managerApproved", WorkflowVariableTypes.Boolean),
                Mapping("count", "error", WorkflowVariableTypes.String)
            ]);
        var target = ImmediateChild(
            "target",
            startInputs:
            [
                Input("amount"),
                Input("text", WorkflowVariableTypes.String),
                Input("needed", WorkflowVariableTypes.String)
            ],
            variables: [Process("count", WorkflowVariableTypes.Number)]);
        var start = target.FlowNodes.Single(node => node.Id == Start);
        start.Roles = ["requester"];
        start.Idempotency = new IdempotencyModel { HeaderName = "Idempotency-Key", Variable = "requestKey" };

        var problems = CallActivityRules.FindTargetProblems(parent, CallNode(parent), target);

        Assert.All(problems, problem => Assert.Equal(CallFailureCodes.TargetIncompatible, problem.Code));
        var messages = string.Join(" | ", problems.Select(problem => problem.Message));
        Assert.Contains("restricts its roles", messages);
        Assert.Contains("idempotency", messages);
        Assert.Contains("does not declare input 'missing'", messages);
        Assert.Contains("input 'text' is string", messages);
        Assert.Contains("required input 'needed'", messages);
        Assert.Contains("result 'nowhere'", messages);
        Assert.Contains("result 'count' is not declared as string", messages);
    }

    [Fact]
    public void A_missing_target_is_unavailable_and_a_target_with_no_plain_start_is_incompatible()
    {
        var parent = ValidParent();
        Assert.Equal(
            CallFailureCodes.TargetUnavailable,
            Assert.Single(CallActivityRules.FindTargetProblems(parent, CallNode(parent), null)).Code);

        var messageOnly = ImmediateChild("target");
        messageOnly.InitialEventId = null;
        Assert.Equal(
            CallFailureCodes.TargetIncompatible,
            Assert.Single(CallActivityRules.FindTargetProblems(parent, CallNode(parent), messageOnly)).Code);
    }

    [Fact]
    public void Nullable_sources_must_reach_a_destination_that_accepts_null_unless_the_mapping_requires_a_value()
    {
        var parent = ValidParent(inputs: [Mapping("managerApproved", "flag", WorkflowVariableTypes.Boolean, required: false)]);
        var optionalBoolean = ImmediateChild("target", startInputs: [Input("flag", WorkflowVariableTypes.Boolean, required: false)]);
        Assert.Contains(
            "does not accept null",
            Assert.Single(CallActivityRules.FindTargetProblems(parent, CallNode(parent), optionalBoolean)).Message);

        var required = ValidParent(inputs: [Mapping("managerApproved", "flag", WorkflowVariableTypes.Boolean)]);
        Assert.Empty(CallActivityRules.FindTargetProblems(required, CallNode(required), optionalBoolean));

        // An optional scalar json input is the one kind that accepts null.
        var json = Parent(
            "parent",
            "target",
            variables: [Process("note", WorkflowVariableTypes.Json)],
            inputs: [Mapping("note", "note", WorkflowVariableTypes.Json, required: false)]);
        var optionalJson = ImmediateChild("target", startInputs: [Input("note", WorkflowVariableTypes.Json, required: false)]);
        Assert.Empty(CallActivityRules.FindTargetProblems(json, CallNode(json), optionalJson));

        var output = Parent(
            "parent",
            "target",
            variables: [Process("solid", WorkflowVariableTypes.Number, nullable: false, defaultValue: 1)],
            outputs: [Mapping("maybe", "solid", required: false)]);
        var nullableResult = ImmediateChild("target", variables: [Process("maybe", WorkflowVariableTypes.Number)]);
        Assert.Contains(
            "not nullable",
            Assert.Single(CallActivityRules.FindTargetProblems(output, CallNode(output), nullableResult)).Message);
    }

    [Fact]
    public void The_publication_gate_covers_call_nodes_and_otherwise_synchronous_call_only_targets()
    {
        var gated = new WorkflowDefinitionReadinessChecker(
            durableProcessingOptions: new DurableProcessingOptions { PublicationEnabled = false });
        var open = new WorkflowDefinitionReadinessChecker(
            durableProcessingOptions: new DurableProcessingOptions { PublicationEnabled = true });
        var caller = ValidParent();
        var callOnly = ImmediateChild("target");
        callOnly.FlowNodes.Single(node => node.Id == Start).CallOnly = true;
        var plain = ImmediateChild("plain");

        Assert.Throws<WorkflowDomainException>(() => gated.EnsureDurablePublicationAllowed(caller, publish: true));
        Assert.Throws<WorkflowDomainException>(() => gated.EnsureDurablePublicationAllowed(callOnly, publish: true));
        gated.EnsureDurablePublicationAllowed(plain, publish: true);
        gated.EnsureDurablePublicationAllowed(caller, publish: false);
        open.EnsureDurablePublicationAllowed(caller, publish: true);
    }

    [Fact]
    public void Option_limits_are_validated_at_startup()
    {
        new CallActivityOptions().Validate();
        new CallActivityOptions { MaxDepth = 1, MaxOpenInvocationsPerRoot = 1 }.Validate();
        new CallActivityOptions { MaxDepth = 32, MaxOpenInvocationsPerRoot = 4096 }.Validate();
        Assert.Throws<InvalidOperationException>(() => new CallActivityOptions { MaxDepth = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CallActivityOptions { MaxDepth = 33 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CallActivityOptions { MaxOpenInvocationsPerRoot = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CallActivityOptions { MaxOpenInvocationsPerRoot = 4097 }.Validate());
    }
}
