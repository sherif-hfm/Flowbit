extern alias FlowbitUi;

using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using InstanceDetail = FlowbitUi::Flowbit.Ui.Components.Pages.InstanceDetail;
using StartVariableInputParser = FlowbitUi::Flowbit.Ui.Components.Pages.StartVariableInputParser;
using TypedInputEventArgs = FlowbitUi::Flowbit.Ui.Components.Shared.TypedInputEventArgs;
using VariableInputConversion = FlowbitUi::Flowbit.Ui.Components.Shared.VariableInputConversion;
using VariableInputField = FlowbitUi::Flowbit.Ui.Components.Shared.VariableInputField;
using VariableInputKind = FlowbitUi::Flowbit.Ui.Components.Shared.VariableInputKind;

namespace Flowbit.Tests;

public sealed class VariableInputFieldTests
{
    [Theory]
    [InlineData(WorkflowVariableTypes.String, false, false, "Text")]
    [InlineData(WorkflowVariableTypes.String, false, true, "Options")]
    [InlineData(WorkflowVariableTypes.Number, false, true, "Options")]
    [InlineData(WorkflowVariableTypes.Number, false, false, "Number")]
    [InlineData(WorkflowVariableTypes.Boolean, false, false, "Boolean")]
    [InlineData(WorkflowVariableTypes.Date, false, false, "Date")]
    [InlineData(WorkflowVariableTypes.DateTime, false, false, "DateTime")]
    [InlineData(WorkflowVariableTypes.Json, false, false, "Structured")]
    [InlineData(WorkflowVariableTypes.String, true, true, "Structured")]
    [InlineData(WorkflowVariableTypes.Date, true, false, "Structured")]
    public void Each_declaration_gets_the_control_for_its_type(
        string dataType,
        bool isArray,
        bool withOptions,
        string expected)
    {
        var variable = Variable(dataType, isArray);
        if (withOptions)
        {
            variable.Options = [Option(dataType == WorkflowVariableTypes.Number ? "1" : "\"a\"")];
        }

        Assert.Equal(Enum.Parse<VariableInputKind>(expected), VariableInputConversion.KindOf(variable));
    }

    [Fact]
    public void Options_use_labels_and_plain_decimal_values_and_skip_unusable_entries()
    {
        var numbers = Variable(WorkflowVariableTypes.Number);
        // 1e-29 would become 0 and 9007199254740993 cannot pass through the editor unchanged.
        numbers.Options =
        [
            Option("2.50", "  High  "), Option("1e3"), Option("\"text\""), new() { Value = null },
            Option("1e-29"), Option("9007199254740993"), Option("1e400")
        ];
        var strings = Variable(WorkflowVariableTypes.String);
        strings.Options = [Option("\"approved\"", "Approve"), Option("\"rejected\""), Option("5")];

        Assert.Equal(
            [("2.50", "High"), ("1000", "1000")],
            VariableInputConversion.OptionsOf(numbers).Select(option => (option.Value, option.Text)));
        Assert.Equal(
            [("approved", "Approve"), ("rejected", "rejected")],
            VariableInputConversion.OptionsOf(strings).Select(option => (option.Value, option.Text)));
    }

    [Theory]
    [InlineData("2026-09-09T10:30", 180, "2026-09-09T10:30:00+03:00")]
    [InlineData("2026-09-09T10:30", 0, "2026-09-09T10:30:00Z")]
    [InlineData("2026-09-09T10:30:15", -150, "2026-09-09T10:30:15-02:30")]
    [InlineData("2026-09-09T10:30:15.250", 345, "2026-09-09T10:30:15+05:45")]
    [InlineData("2026-09-09T10:30", 841, null)]
    [InlineData("2026-09-09", 0, null)]
    [InlineData("2026-09-09T10:30", null, null)]
    public void Date_time_values_carry_the_browser_offset(string local, int? offset, string? expected) =>
        Assert.Equal(expected, VariableInputConversion.ComposeDateTime(local, offset));

    [Theory]
    [InlineData("DateTime", "2026-09-09T10:30:00+03:00", "2026-09-09T10:30")]
    [InlineData("DateTime", "2026-09-09T10:30", "")]
    [InlineData("Number", "1250.50", "1250.50")]
    [InlineData("Date", "2026-09-09", "2026-09-09")]
    [InlineData("Number", "\u0000invalid", "")]
    public void Pickers_show_the_local_part_of_a_stored_value(string kind, string stored, string expected) =>
        Assert.Equal(expected, VariableInputConversion.DomValue(Enum.Parse<VariableInputKind>(kind), stored));

    [Fact]
    public void Picker_events_store_parser_text_or_block_submission_with_a_message()
    {
        var cases = new (VariableInputKind Kind, TypedInputEventArgs Args, string Stored, string? Error)[]
        {
            (VariableInputKind.Number, new() { Value = "1250.50" }, "1250.50", null),
            (VariableInputKind.Number, new() { Value = "" }, "", null),
            (VariableInputKind.Number, new() { Value = "", BadInput = true }, VariableInputConversion.Invalid,
                "Enter a number in plain decimal notation, for example 1250.50."),
            (VariableInputKind.Number, new() { Value = "1e3" }, VariableInputConversion.Invalid,
                "Enter a number in plain decimal notation, for example 1250.50."),
            (VariableInputKind.Date, new() { Value = "2026-09-09" }, "2026-09-09", null),
            (VariableInputKind.Date, new() { Value = "", BadInput = true }, VariableInputConversion.Invalid, "Enter a complete date."),
            (VariableInputKind.Date, new() { Value = "20260-09-09" }, VariableInputConversion.Invalid,
                "Enter a date between 0001-01-01 and 9999-12-31."),
            (VariableInputKind.DateTime, new() { Value = "2026-09-09T10:30", OffsetMinutes = 180, TimeZone = "Africa/Cairo" },
                "2026-09-09T10:30:00+03:00", null),
            (VariableInputKind.DateTime, new() { Value = "", BadInput = true }, VariableInputConversion.Invalid,
                "Enter a complete date and time."),
            (VariableInputKind.DateTime, new() { Value = "2026-03-29T02:30", SkippedLocalTime = true, TimeZone = "Europe/Berlin" },
                VariableInputConversion.Invalid,
                "This time does not exist in Europe/Berlin because of a daylight-saving change."),
            (VariableInputKind.DateTime, new() { Value = "2026-09-09T10:30" }, VariableInputConversion.Invalid,
                "Your browser did not report a UTC offset for this time. Choose another time.")
        };

        foreach (var (kind, args, stored, error) in cases)
        {
            var result = VariableInputConversion.Interpret(kind, args);
            Assert.Equal(stored, result.Value);
            Assert.Equal(error, result.Error);
        }
    }

    // Each control stores the text a person could type, so the typed parser turns it
    // into the same JSON: strings verbatim, numbers with their scale, ISO text as is.
    [Theory]
    [InlineData(WorkflowVariableTypes.String, "Reviewed and approved", JsonValueKind.String, "Reviewed and approved")]
    [InlineData(WorkflowVariableTypes.String, "approved", JsonValueKind.String, "approved")]
    [InlineData(WorkflowVariableTypes.Number, "1250.50", JsonValueKind.Number, "1250.50")]
    [InlineData(WorkflowVariableTypes.Number, "2.50", JsonValueKind.Number, "2.50")]
    [InlineData(WorkflowVariableTypes.Boolean, "true", JsonValueKind.True, "true")]
    [InlineData(WorkflowVariableTypes.Boolean, "false", JsonValueKind.False, "false")]
    [InlineData(WorkflowVariableTypes.Date, "2026-09-09", JsonValueKind.String, "2026-09-09")]
    [InlineData(WorkflowVariableTypes.DateTime, "2026-09-09T10:30:00+03:00", JsonValueKind.String, "2026-09-09T10:30:00+03:00")]
    [InlineData(WorkflowVariableTypes.DateTime, "2026-09-09T10:30:00Z", JsonValueKind.String, "2026-09-09T10:30:00Z")]
    public void Control_values_submit_what_typing_the_same_text_submits(
        string dataType,
        string stored,
        JsonValueKind kind,
        string expected)
    {
        Assert.True(StartVariableInputParser.TryParse(Variable(dataType), stored, out var value, out var error), error);
        Assert.Equal(kind, value.ValueKind);
        Assert.Equal(expected, kind == JsonValueKind.String ? value.GetString() : value.GetRawText());
    }

    [Fact]
    public void Parser_refuses_a_rejected_entry_explicitly()
    {
        var parsed = StartVariableInputParser.TryParse(
            Variable(WorkflowVariableTypes.Number),
            VariableInputConversion.Invalid,
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("Variable 'value' has an incomplete or invalid number value.", error);
    }

    [Fact]
    public async Task Labelled_field_shows_help_default_hint_and_accessible_descriptions()
    {
        var variable = Variable(WorkflowVariableTypes.String);
        variable.Label = "Review outcome";
        variable.HelpText = "Choose one.\nAsk your lead if unsure.";
        variable.DefaultValue = JsonSerializer.SerializeToElement("approved");
        variable.Options = [Option("\"approved\"", "Approve"), Option("\"rejected\"", "Reject")];
        await using var harness = new Harness();

        var html = WebUtility.HtmlDecode(await harness.RenderAsync(variable, "field-1", "rejected"));

        Assert.Contains("<label class=\"form-label\" for=\"field-1\">Review outcome <span class=\"field-type\">value · string</span>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"field-1-help\"", html, StringComparison.Ordinal);
        Assert.Contains("Choose one.\nAsk your lead if unsure.", html, StringComparison.Ordinal);
        Assert.Contains("Leave blank to use the default.", html, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"field-1-help field-1-default\"", html, StringComparison.Ordinal);
        // Static rendering writes an empty attribute without its value.
        Assert.Contains("<option value>Choose a value</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"approved\">Approve</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"rejected\" selected>Reject</option>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-required", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_unlabelled_fields_render_the_typed_controls()
    {
        await using var harness = new Harness();

        var number = await harness.RenderAsync(Variable(WorkflowVariableTypes.Number, required: true), "n", null);
        var date = await harness.RenderAsync(Variable(WorkflowVariableTypes.Date), "d", "2026-09-09");
        var dateTime = await harness.RenderAsync(Variable(WorkflowVariableTypes.DateTime), "t", "2026-09-09T10:30:00Z");
        var flag = await harness.RenderAsync(Variable(WorkflowVariableTypes.Boolean), "b", "false");
        var list = await harness.RenderAsync(Variable(WorkflowVariableTypes.Number, isArray: true), "l", "1, 2");

        Assert.Contains("for=\"n\">value <span class=\"field-type\">number</span><span class=\"required-mark\" aria-hidden=\"true\"> *</span>", number, StringComparison.Ordinal);
        Assert.Contains("type=\"number\" step=\"any\"", number, StringComparison.Ordinal);
        // typed-inputs.js blurs only marked number boxes on wheel.
        Assert.Contains("data-typed-input", number, StringComparison.Ordinal);
        Assert.Contains("data-typed-input", date, StringComparison.Ordinal);
        Assert.Contains("data-typed-input", dateTime, StringComparison.Ordinal);
        Assert.DoesNotContain("data-typed-input", list, StringComparison.Ordinal);
        Assert.Contains("aria-required=\"true\"", number, StringComparison.Ordinal);
        Assert.Contains("type=\"date\" max=\"9999-12-31\" value=\"2026-09-09\"", date, StringComparison.Ordinal);
        Assert.Contains("type=\"datetime-local\" max=\"9999-12-31T23:59\" value=\"2026-09-09T10:30\"", dateTime, StringComparison.Ordinal);
        Assert.Contains("Sent as 2026-09-09T10:30:00Z.", dateTime, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"t-preview\"", dateTime, StringComparison.Ordinal);
        Assert.Contains("<option value=\"false\" selected>False</option>", flag, StringComparison.Ordinal);
        Assert.Contains("<textarea id=\"l\" class=\"form-control font-monospace\" rows=\"3\" placeholder=\"JSON array or comma-separated values\"", list, StringComparison.Ordinal);
        Assert.Contains(">1, 2</textarea>", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picker_events_emit_values_show_errors_and_reset_only_on_outside_changes()
    {
        await using var harness = new Harness();
        var variable = Variable(WorkflowVariableTypes.DateTime);
        await harness.RenderAsync(variable, "when", null);

        await harness.TypeAsync(new TypedInputEventArgs
        {
            Value = "2026-09-09T10:30",
            OffsetMinutes = 180,
            TimeZone = "Africa/Cairo"
        });
        Assert.Equal("2026-09-09T10:30:00+03:00", harness.Emitted.Last());
        await harness.SetValueAsync(harness.Emitted.Last());
        var accepted = WebUtility.HtmlDecode(await harness.HtmlAsync());
        Assert.Contains("Sent as 2026-09-09T10:30:00+03:00 (Africa/Cairo).", accepted, StringComparison.Ordinal);
        // The picker keeps the value it rendered first; the browser already shows the new one.
        Assert.Contains("value=\"\"", accepted, StringComparison.Ordinal);

        await harness.TypeAsync(new TypedInputEventArgs { Value = "", BadInput = true });
        await harness.SetValueAsync(harness.Emitted.Last());
        var rejected = await harness.HtmlAsync();
        Assert.Equal(VariableInputConversion.Invalid, harness.Emitted.Last());
        Assert.Contains("Enter a complete date and time.", rejected, StringComparison.Ordinal);
        Assert.Contains("aria-invalid=\"true\"", rejected, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"when-preview when-error\"", rejected, StringComparison.Ordinal);

        await harness.SetValueAsync(null);
        var reset = await harness.HtmlAsync();
        Assert.DoesNotContain("Enter a complete date and time.", reset, StringComparison.Ordinal);
        Assert.Contains("Uses your browser&#x27;s time zone.", reset, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Option_captions_follow_the_declaration_when_its_values_stay_the_same()
    {
        await using var harness = new Harness();
        var first = Variable(WorkflowVariableTypes.String, name: "outcome");
        first.Options = [Option("\"approved\"", "Approve"), Option("\"rejected\"", "Reject")];
        var before = await harness.RenderAsync(first, "start-variable-outcome", "approved");

        // Another start event declares the same input with other captions.
        var second = Variable(WorkflowVariableTypes.String, name: "outcome");
        second.Options = [Option("\"approved\"", "Accept"), Option("\"rejected\"", "Decline")];
        await harness.SetVariableAsync(second, "approved");
        var after = await harness.HtmlAsync();

        Assert.Contains(">Approve</option>", before, StringComparison.Ordinal);
        Assert.Contains(">Accept</option>", after, StringComparison.Ordinal);
        Assert.Contains(">Decline</option>", after, StringComparison.Ordinal);
        Assert.DoesNotContain(">Approve</option>", after, StringComparison.Ordinal);
        Assert.Empty(harness.Emitted);
    }

    [Fact]
    public void Instance_detail_actions_submit_typed_values()
    {
        var page = new InstanceDetail();
        var inputs = (Dictionary<string, string?>)typeof(InstanceDetail)
            .GetField("flowInputs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!;
        var flow = new SequenceFlowModel
        {
            Id = 201,
            Variables =
            [
                Variable(WorkflowVariableTypes.Json, name: "context"),
                Variable(WorkflowVariableTypes.Number, isArray: true, name: "amounts"),
                Variable(WorkflowVariableTypes.Number, name: "total"),
                Variable(WorkflowVariableTypes.String, name: "note")
            ]
        };
        inputs["201:context"] = "{\"source\":\"detail\"}";
        inputs["201:amounts"] = "1, 2.5";
        inputs["201:total"] = "1250.50";
        inputs["201:note"] = "  ";
        var buildPayload = typeof(InstanceDetail).GetMethod("BuildPayload", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var payload = (Dictionary<string, JsonElement>)buildPayload.Invoke(page, [flow])!;

        Assert.Equal(["context", "amounts", "total"], payload.Keys);
        Assert.Equal("detail", payload["context"].GetProperty("source").GetString());
        Assert.Equal("[1,2.5]", payload["amounts"].GetRawText());
        Assert.Equal("1250.50", payload["total"].GetRawText());
        inputs["201:total"] = VariableInputConversion.Invalid;
        var error = Assert.Throws<TargetInvocationException>(() => buildPayload.Invoke(page, [flow]));
        Assert.IsType<FormatException>(error.InnerException);
    }

    private static VariableModel Variable(
        string dataType,
        bool isArray = false,
        bool required = false,
        string name = "value") => new()
    {
        Id = 1,
        Name = name,
        DataType = dataType,
        IsArray = isArray,
        Required = required
    };

    private static VariableOptionModel Option(string json, string? label = null) =>
        new() { Value = JsonDocument.Parse(json).RootElement.Clone(), Label = label };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private readonly CapturingActivator activator = new();
        private HtmlRootComponent? root;
        private VariableModel? variable;
        private string inputId = string.Empty;

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IComponentActivator>(activator);
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }

        public List<string?> Emitted { get; } = [];

        public async Task<string> RenderAsync(VariableModel field, string id, string? value)
        {
            variable = field;
            inputId = id;
            root = await renderer.Dispatcher.InvokeAsync(() =>
                renderer.RenderComponentAsync<VariableInputField>(Parameters(value)));
            return await HtmlAsync();
        }

        // Scoped CSS adds a generated b-* attribute to each element; the assertions ignore it.
        public async Task<string> HtmlAsync() =>
            Regex.Replace(
                await renderer.Dispatcher.InvokeAsync(root!.Value.ToHtmlString),
                @" b-[a-z0-9]{10}(?==""""|[ >/])",
                string.Empty);

        public Task TypeAsync(TypedInputEventArgs args) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var handler = typeof(VariableInputField).GetMethod("OnTypedInputAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)handler.Invoke(activator.Field, [args])!;
        });

        public Task SetValueAsync(string? value) => renderer.Dispatcher.InvokeAsync(() =>
            activator.Field!.SetParametersAsync(Parameters(value)));

        public Task SetVariableAsync(VariableModel field, string? value)
        {
            variable = field;
            return SetValueAsync(value);
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }

        private ParameterView Parameters(string? value) => ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(VariableInputField.Variable)] = variable,
            [nameof(VariableInputField.InputId)] = inputId,
            [nameof(VariableInputField.Value)] = value,
            [nameof(VariableInputField.ValueChanged)] = EventCallback.Factory.Create<string?>(this, Emitted.Add)
        });
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public VariableInputField? Field { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is VariableInputField field) Field = field;
            return component;
        }
    }
}
