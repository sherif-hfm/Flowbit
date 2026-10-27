using System.Text.Json;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class TypedInputsScriptTests
{
    [Fact]
    public void Registers_once_and_reports_offsets_skipped_times_and_rejected_entries()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        using var engine = new Engine(options => options
            .LocalTimeZone(berlin)
            .TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute(Stubs);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "typed-inputs.js"));
        engine.Execute(script);
        engine.Execute(script);

        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              const registration = registrations[0];
              const args = (type, value, badInput = false) =>
                registration.options.createEventArgs({ target: { type, value, validity: { badInput } } });
              return JSON.stringify({
                count: registrations.length,
                name: registration.name,
                browserEventName: registration.options.browserEventName,
                commitName: registrations[1].name,
                commitEventName: registrations[1].options.browserEventName,
                sameArgs: registrations[1].options.createEventArgs === registration.options.createEventArgs,
                summer: args('datetime-local', '2026-09-09T10:30'),
                winter: args('datetime-local', '2026-01-15T10:30:15'),
                skipped: args('datetime-local', '2026-03-29T02:30'),
                partial: args('datetime-local', '', true),
                number: args('number', '', true),
                plain: args('number', '12.5'),
                missingTarget: registration.options.createEventArgs({})
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(2, root.GetProperty("count").GetInt32());
        Assert.Equal("typedinput", root.GetProperty("name").GetString());
        Assert.Equal("input", root.GetProperty("browserEventName").GetString());
        Assert.Equal("typedcommit", root.GetProperty("commitName").GetString());
        Assert.Equal("focusout", root.GetProperty("commitEventName").GetString());
        Assert.True(root.GetProperty("sameArgs").GetBoolean());
        AssertArgs(root.GetProperty("summer"), "2026-09-09T10:30", badInput: false, offset: 120, skipped: false);
        AssertArgs(root.GetProperty("winter"), "2026-01-15T10:30:15", badInput: false, offset: 60, skipped: false);
        AssertArgs(root.GetProperty("skipped"), "2026-03-29T02:30", badInput: false, offset: null, skipped: true);
        AssertArgs(root.GetProperty("partial"), string.Empty, badInput: true, offset: null, skipped: false);
        AssertArgs(root.GetProperty("number"), string.Empty, badInput: true, offset: null, skipped: false);
        AssertArgs(root.GetProperty("plain"), "12.5", badInput: false, offset: null, skipped: false);
        AssertArgs(root.GetProperty("missingTarget"), string.Empty, badInput: false, offset: null, skipped: false);
    }

    [Fact]
    public void Wheel_leaves_only_a_focused_typed_number_box()
    {
        using var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute(Stubs);
        engine.Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "typed-inputs.js")));

        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              const box = (type, typed) => ({
                type, blurred: false,
                hasAttribute: name => typed && name === 'data-typed-input',
                blur() { this.blurred = true; }
              });
              const wheel = target => {
                document.activeElement = target;
                wheelListeners.forEach(listener => listener({ target }));
                return target.blurred;
              };
              const unfocused = box('number', true);
              document.activeElement = null;
              wheelListeners.forEach(listener => listener({ target: unfocused }));
              return JSON.stringify({
                passive: wheelOptions.passive === true,
                typedNumber: wheel(box('number', true)),
                otherNumber: wheel(box('number', false)),
                typedDate: wheel(box('date', true)),
                unfocused: unfocused.blurred
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("passive").GetBoolean());
        Assert.True(root.GetProperty("typedNumber").GetBoolean());
        Assert.False(root.GetProperty("otherNumber").GetBoolean());
        Assert.False(root.GetProperty("typedDate").GetBoolean());
        Assert.False(root.GetProperty("unfocused").GetBoolean());
    }

    private const string Stubs = """
        const registrations = [];
        const wheelListeners = [];
        let wheelOptions = null;
        const window = {
          Blazor: { registerCustomEventType: (name, options) => registrations.push({ name, options }) }
        };
        const document = {
          activeElement: null,
          addEventListener(type, listener, options) {
            if (type === 'wheel') { wheelListeners.push(listener); wheelOptions = options; }
          }
        };
        """;

    private static void AssertArgs(JsonElement args, string value, bool badInput, int? offset, bool skipped)
    {
        Assert.Equal(value, args.GetProperty("value").GetString());
        Assert.Equal(badInput, args.GetProperty("badInput").GetBoolean());
        var offsetMinutes = args.GetProperty("offsetMinutes");
        Assert.Equal(offset, offsetMinutes.ValueKind == JsonValueKind.Null ? null : offsetMinutes.GetInt32());
        Assert.Equal(skipped, args.GetProperty("skippedLocalTime").GetBoolean());
    }
}
