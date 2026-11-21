using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// PostgreSQL cannot hold a null character (U+0000) in text or jsonb. A request that
/// carries one is answered <c>400</c> with <see cref="PostgresNullCharacter.Message"/> and
/// writes nothing, and the API's own log entries for it never hold the refused text.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class NullCharacterRequestTests(PostgresApiFixture fixture)
{
    private const string Actor = "null-character";
    private const string RequestLogSource = "Serilog.AspNetCore.RequestLoggingMiddleware";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SignalThrow_RefusesANullCharacterInEachTextFieldAndWritesNothing()
    {
        var signal = UniqueSignal();
        var keyed = await CreateAsync(OrderFamily(signal, keyed: true));
        var owner = await StartAsync(keyed.Id, "ORD-A");

        // workflowKey and idempotencyKey fail as text parameters (22021), a payload string
        // inside jsonb (22P05), and businessKey as a text parameter of the keyed family.
        foreach (var request in new[]
                 {
                     new ThrowSignalRequest(signal, keyed.WorkflowKey, BusinessKey: "ORD\0A"),
                     new ThrowSignalRequest(signal, $"{keyed.WorkflowKey}\0"),
                     new ThrowSignalRequest(signal, keyed.WorkflowKey, IdempotencyKey: "retry\0-1"),
                     new ThrowSignalRequest(signal, keyed.WorkflowKey, Payload: JsonSerializer.SerializeToElement(new { note = "a\0b" }))
                 })
        {
            await AssertRefusedAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
            Assert.Equal(1, await db.SignalSubscriptions.CountAsync(item =>
                item.InstanceId == owner && item.Status == SignalSubscriptionStatuses.Active));
        }

        // The same throw without the character is accepted. Its delivery job is processed so
        // that no queued job is left for the tests that lease the next one.
        using var accepted = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest(signal, keyed.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { version = 1 }), BusinessKey: "ORD-A"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var broadcast = (await accepted.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        Assert.Equal(1, broadcast.TotalDeliveryCount);
        await new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(broadcast.Id);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(0, await db.SignalSubscriptions.CountAsync(item =>
                item.InstanceId == owner && item.Status == SignalSubscriptionStatuses.Active));
        }
    }

    [Fact]
    public async Task Searches_RefuseANullCharacterInABusinessKeyFilter()
    {
        var signal = UniqueSignal();
        var keyed = await CreateAsync(OrderFamily(signal, keyed: true));

        await AssertRefusedAsync(HttpMethod.Get,
            $"/api/signal-subscriptions/summary?name={signal}&workflowKey={keyed.WorkflowKey}&businessKey=ORD%00A");
        await AssertRefusedAsync(HttpMethod.Get, "/api/instances?businessKey=ORD%00A");

        using var summary = await SendAsync(HttpMethod.Get,
            $"/api/signal-subscriptions/summary?name={signal}&workflowKey={keyed.WorkflowKey}&businessKey=ORD-A");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
    }

    [Fact]
    public async Task Start_RefusesANullCharacterInABusinessKeyOrAVariableAndCreatesNoInstance()
    {
        var signal = UniqueSignal();
        var keyed = await CreateAsync(OrderFamily(signal, keyed: true));
        var unkeyed = await CreateAsync(OrderFamily(signal, keyed: false));

        // The business key is a text column (22021); an ordinary variable a jsonb value (22P05).
        await AssertRefusedAsync(HttpMethod.Post, "/api/instances", StartRequest(keyed.Id, "ORD\0A"));
        await AssertRefusedAsync(HttpMethod.Post, "/api/instances", StartRequest(unkeyed.Id, "ORD\0A"));

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.WorkflowInstances.AnyAsync(item =>
                item.WorkflowDefinitionId == keyed.Id || item.WorkflowDefinitionId == unkeyed.Id));
            Assert.False(await db.WorkflowBusinessKeyClaims.AnyAsync(item => item.WorkflowKey == keyed.WorkflowKey));
        }

        Assert.True(await StartAsync(keyed.Id, "ORD-A") > 0);
    }

    [Fact]
    public async Task Start_RefusesANullCharacterThatASynchronousScriptWrites()
    {
        var model = ScriptModel("execution.setVariable('note', 'a' + String.fromCharCode(0) + 'b');");
        var created = await CreateAsync(model);

        // The write fails when the transaction saves it, after routing: the request fails
        // as a whole, with the same answer as text the client sent.
        await AssertRefusedAsync(HttpMethod.Post, "/api/instances", new StartInstanceRequest(created.Id, null, null, null));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.WorkflowInstances.AnyAsync(item => item.WorkflowDefinitionId == created.Id));
        }

        var clean = await CreateAsync(ScriptModel("execution.setVariable('note', 'ab');"));
        using var started = await SendAsync(HttpMethod.Post, "/api/instances", new StartInstanceRequest(clean.Id, null, null, null));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
    }

    [Fact]
    public async Task RefusedText_IsLoggedAs400AndOnlyEfCoreQuotesIt()
    {
        const string marker = "NUL-MARKER-6e1d";
        var signal = UniqueSignal();
        var keyed = await CreateAsync(OrderFamily(signal, keyed: true));
        var logs = new CapturingSink();
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(logs)));
        using var client = factory.CreateClient();

        foreach (var request in new[]
                 {
                     new ThrowSignalRequest(signal, keyed.WorkflowKey, BusinessKey: $"{marker}\0"),
                     new ThrowSignalRequest(signal, keyed.WorkflowKey, Payload: JsonSerializer.SerializeToElement(new { note = $"{marker}\0" }))
                 })
        {
            using var message = Authorize(new HttpRequestMessage(HttpMethod.Post, "/api/signal-broadcasts")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            });
            using var response = await client.SendAsync(message);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var entries = logs.Events
            .Where(item => Scalar(item, "SourceContext") as string == RequestLogSource
                && Scalar(item, "RequestPath") as string == "/api/signal-broadcasts")
            .ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(400, Assert.IsType<int>(Scalar(entry, "StatusCode")));
            Assert.Equal(LogEventLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
        });
        Assert.Equal(new[] { "22021", "22P05" }, logs.Events
            .Where(item => item.MessageTemplate.Text.StartsWith("PostgreSQL refused a text value", StringComparison.Ordinal))
            .Select(item => Scalar(item, "SqlState") as string));

        // EF Core logs every failed save with its exception, whose data includes PostgreSQL's
        // context line; for a refused jsonb value that line quotes the JSON up to the null
        // character. Only those entries may hold the text, and only for the payload.
        static bool HoldsMarker(LogEvent item) =>
            item.RenderMessage().Contains(marker, StringComparison.Ordinal)
            || item.Exception?.ToString().Contains(marker, StringComparison.Ordinal) == true;
        Assert.All(logs.Events.Where(HoldsMarker), item =>
        {
            Assert.StartsWith("Microsoft.EntityFrameworkCore.", Scalar(item, "SourceContext") as string);
            Assert.Equal("22P05", Assert.IsType<Npgsql.PostgresException>(item.Exception?.InnerException ?? item.Exception).SqlState);
        });
    }

    private async Task AssertRefusedAsync(HttpMethod method, string path, object? body = null)
    {
        using var response = await SendAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{method} {path}: {(int)response.StatusCode} {text}");
        using var document = JsonDocument.Parse(text);
        Assert.Equal(PostgresNullCharacter.Message, document.RootElement.GetProperty("error").GetString());
    }

    private static string UniqueSignal() => $"test.nul-{Guid.NewGuid():N}";

    /// <summary>The signal catch workflow with an order number to start, its business key when keyed.</summary>
    private static WorkflowModel OrderFamily(string signal, bool keyed)
    {
        var model = SignalRuntimeTests.CatchModel(signal);
        var start = model.FlowNodes.Single(node => node.Id == 1);
        start.Variables =
        [
            new VariableModel { Id = 1, Name = "orderNumber", DataType = WorkflowVariableTypes.String, Required = true }
        ];
        if (keyed)
        {
            start.BusinessKey = new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active };
        }
        return model;
    }

    /// <summary>A start, a synchronous JavaScript task running <paramref name="script"/>, a user task, and an end.</summary>
    private static WorkflowModel ScriptModel(string script) => new()
    {
        Id = $"null-character-script-{Guid.NewGuid():N}",
        Name = "Null character script",
        InitialEventId = 1,
        Variables = [new VariableModel { Id = 1, Name = "note", DataType = WorkflowVariableTypes.String, Nullable = true }],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Write the note",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.JavaScript,
                UsesFlowInfo = false,
                Script = script
            },
            new FlowNodeModel { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Done" }
        ]
    };

    private static StartInstanceRequest StartRequest(long workflowId, string orderNumber) =>
        new(workflowId, null, null, new Dictionary<string, JsonElement>
        {
            ["orderNumber"] = JsonSerializer.SerializeToElement(orderNumber)
        });

    private async Task<WorkflowDetailDto> CreateAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task<long> StartAsync(long workflowId, string orderNumber)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances", StartRequest(workflowId, orderNumber));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = Authorize(new HttpRequestMessage(method, path));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        }
        return await fixture.Client.SendAsync(request);
    }

    private static HttpRequestMessage Authorize(HttpRequestMessage request)
    {
        ApiTestAuth.Authorize(request, Actor, ["admin"]);
        return request;
    }

    private static object? Scalar(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue scalar
            ? scalar.Value
            : null;

    // Receives the API host's Serilog events, which it reads from its services.
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> events = new();

        public IReadOnlyCollection<LogEvent> Events => events.ToArray();

        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
