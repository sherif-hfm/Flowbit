extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SignalCatalogForm = FlowbitUi::Flowbit.Ui.Components.Shared.SignalCatalogForm;
using SignalCatalogFormatting = FlowbitUi::Flowbit.Ui.Components.Shared.SignalCatalogFormatting;
using SignalCatalogPage = FlowbitUi::Flowbit.Ui.Components.Pages.SignalCatalog;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The `/signal-catalog` page (EVENT-04F): the form is checked before any request, Save is
/// offered only after a clean review of the form as it is, a conflict keeps the form, the
/// archive panel offers Archive only when nothing uses the name, a late reply is dropped after
/// an identity change, and catalog text is rendered as text.
/// </summary>
public sealed class SignalCatalogPageTests
{
    private const string Name = "warehouse.product-recall";
    private const string Markup = "<img src=x onerror=alert(1)>";
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-11-24T09:00:00Z");
    private static readonly DateTimeOffset Updated = DateTimeOffset.Parse("2026-11-24T10:30:00.123456Z");

    [Fact]
    public async Task FormProblems_AreReportedBeforeAnyRequest()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        var before = harness.Requests.Count;
        await harness.CallAsync("OpenCreate", (object?)null);

        await harness.CallAsync("ReviewAsync");
        Assert.Equal("Signal name is required.", await harness.ValueAsync<string?>("localProblem"));

        await harness.EditAsync(form => form.Name = "Warehouse Recall");
        await harness.CallAsync("ReviewAsync");
        Assert.Equal($"Signal name must be {SignalEventRules.NameRuleDescription}.", await harness.ValueAsync<string?>("localProblem"));

        await harness.EditAsync(form =>
        {
            form.Name = " " + Name + " ";
            form.AddField().Name = "bad name";
        });
        await harness.CallAsync("ReviewAsync");
        Assert.Equal(
            $"Field 1 name must be {SignalEventRules.PayloadNameRuleDescription}.",
            await harness.ValueAsync<string?>("localProblem"));

        await harness.EditAsync(form =>
        {
            form.Fields[0].Name = "reason";
            form.AddField().Name = "REASON";
        });
        await harness.CallAsync("SaveAsync");
        Assert.Equal("Field 'REASON' appears more than once.", await harness.ValueAsync<string?>("localProblem"));

        await harness.EditAsync(form =>
        {
            form.Fields.RemoveAt(1);
            form.Fields[0].DataType = "text";
        });
        await harness.CallAsync("ReviewAsync");
        Assert.Equal(
            "Field 'reason' dataType must be string, number, boolean, date, datetime, or json.",
            await harness.ValueAsync<string?>("localProblem"));

        Assert.Equal(before, harness.Requests.Count);
        Assert.Contains("Field &#x27;reason&#x27; dataType must be", await harness.HtmlAsync(component), StringComparison.Ordinal);

        // The next edit clears a problem the form found itself; nothing was sent for it.
        await harness.EditAsync(form => form.Fields[0].DataType = "string");
        Assert.Null(await harness.ValueAsync<string?>("localProblem"));
        Assert.DoesNotContain("dataType must be", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_SendsTheCandidateWithTrimmedRolesAndNoEmptySegments()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        await harness.CallAsync("OpenCreate", (object?)null);
        await harness.EditAsync(form =>
        {
            form.Name = Name;
            form.Description = "  A supplier recalled a product.  ";
            var reason = form.AddField();
            reason.Name = "reason";
            reason.Required = true;
            reason.Description = " Why it was recalled. ";
            var skus = form.AddField();
            skus.Name = " skus ";
            skus.IsArray = true;
            skus.Description = "   ";
            var details = form.AddField();
            details.Name = "details";
            details.DataType = WorkflowVariableTypes.Json;
            form.RolesText = " recall-manager, , warehouse-lead ,RECALL-MANAGER,";
            form.AllowClients = false;
        });

        await harness.CallAsync("ReviewAsync");

        var preview = Assert.Single(harness.Requests, request => request.Path == "/api/signal-catalog/preview");
        Assert.Equal(HttpMethod.Post, preview.Method);
        using var body = JsonDocument.Parse(preview.Body!);
        var root = body.RootElement;
        Assert.Equal(Name, root.GetProperty("name").GetString());
        Assert.Equal("A supplier recalled a product.", root.GetProperty("description").GetString());
        Assert.Equal(["recall-manager", "warehouse-lead"], root.GetProperty("throwRoles").EnumerateArray().Select(role => role.GetString()));
        Assert.False(root.GetProperty("allowClients").GetBoolean());
        Assert.True(root.GetProperty("allowWorkflows").GetBoolean());
        var fields = root.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(["reason", "skus", "details"], fields.Select(field => field.GetProperty("name").GetString()));
        Assert.Equal(["string", "string", "json"], fields.Select(field => field.GetProperty("dataType").GetString()));
        Assert.True(fields[0].GetProperty("required").GetBoolean());
        Assert.True(fields[1].GetProperty("isArray").GetBoolean());
        Assert.Equal("Why it was recalled.", fields[0].GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, fields[1].GetProperty("description").ValueKind);
    }

    [Fact]
    public async Task Warnings_SayWhenClientsBypassTheRolesAndWhenNoRoleIsInSignalsRequiredRole()
    {
        const string clientsWarning =
            "API clients with signals.throw can still throw this signal; their roles are not checked.";
        const string requiredRoleWarning =
            "Users need a role from Signals.RequiredRole as well as one of these roles. None of these roles is in Signals.RequiredRole, so only users who hold both can throw this signal; add the role there to let its holders throw it.";
        await using var harness = new Harness { SignalRoles = ["admin", "Recall-Manager"] };
        var component = await harness.RenderAsync();
        await harness.CallAsync("OpenCreate", (object?)null);

        // No roles: nothing to warn about.
        var html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.DoesNotContain(clientsWarning, html, StringComparison.Ordinal);
        Assert.DoesNotContain(requiredRoleWarning, html, StringComparison.Ordinal);

        await harness.EditAsync(form => form.RolesText = "warehouse-lead");
        html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Contains(clientsWarning, html, StringComparison.Ordinal);
        Assert.Contains(requiredRoleWarning, html, StringComparison.Ordinal);

        // Turning clients off ends the first; a role that Signals.RequiredRole lists, in any case, the second.
        await harness.EditAsync(form =>
        {
            form.AllowClients = false;
            form.RolesText = "warehouse-lead, recall-manager";
        });
        html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.DoesNotContain(clientsWarning, html, StringComparison.Ordinal);
        Assert.DoesNotContain(requiredRoleWarning, html, StringComparison.Ordinal);

        Assert.Equal([clientsWarning, requiredRoleWarning], SignalCatalogFormatting.Warnings(["x"], true, ["admin"]));
        // Without the list's reply the roles of Signals.RequiredRole are unknown, so only the first can show.
        Assert.Equal([clientsWarning], SignalCatalogFormatting.Warnings(["x"], true, null));
    }

    [Fact]
    public async Task Save_IsOfferedOnlyAfterACleanReviewOfTheFormAsItIsNow()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.CallAsync("OpenCreate", (object?)null);
        await harness.EditAsync(FillNewEntry);
        Assert.DoesNotContain("signal-catalog-save", await harness.HtmlAsync(component), StringComparison.Ordinal);

        // Save without a review sends nothing.
        await harness.CallAsync("SaveAsync");
        Assert.Equal("Review the changes before saving.", await harness.ValueAsync<string?>("localProblem"));
        Assert.DoesNotContain(harness.Requests, request => request.Path == "/api/signal-catalog/entries");

        await harness.CallAsync("ReviewAsync");
        var html = await harness.HtmlAsync(component);
        Assert.Contains(SignalCatalogFormatting.AllFitText, html, StringComparison.Ordinal);
        Assert.Contains("id=\"signal-catalog-save\"", html, StringComparison.Ordinal);

        // A review that finds problems lists them with links to the versions and offers no Save.
        harness.Impact = new SignalCatalogImpactDto(Name, 1,
            [new SignalCatalogProblemDto(41, "claims-review", "Claims review", 3, 4,
                "Signal catch event #4 mapping to 'policyVersion' is string, but the catalog entry of 'warehouse.product-recall' defines 'version' as number.")],
            false);
        await harness.EditAsync(form => form.Description = "Changed");
        await harness.CallAsync("ReviewAsync");
        html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Contains("href=\"workflows/41/edit\"", html, StringComparison.Ordinal);
        Assert.Contains("claims-review v3 (#41)</a>: Signal catch event #4 mapping to 'policyVersion' is string", html, StringComparison.Ordinal);
        Assert.DoesNotContain("signal-catalog-save", html, StringComparison.Ordinal);
        await harness.CallAsync("SaveAsync");
        Assert.DoesNotContain(harness.Requests, request => request.Path == "/api/signal-catalog/entries");

        harness.Impact = null;
        await harness.CallAsync("ReviewAsync");
        await harness.CallAsync("SaveAsync");

        var create = Assert.Single(harness.Requests, request => request.Path == "/api/signal-catalog/entries");
        Assert.Equal(HttpMethod.Post, create.Method);
        using (var body = JsonDocument.Parse(create.Body!))
        {
            Assert.Equal(Name, body.RootElement.GetProperty("name").GetString());
            Assert.Equal("Changed", body.RootElement.GetProperty("description").GetString());
        }

        Assert.Null(await harness.ValueAsync<SignalCatalogForm?>("form"));
        Assert.Equal($"Signal catalog entry {Name} added.", await harness.ValueAsync<string?>("success"));
        // The list is read again after the save.
        var save = harness.Requests.IndexOf(create);
        Assert.Contains(harness.Requests.Skip(save + 1), request => request.Path.StartsWith("/api/signal-catalog/entries?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnyEdit_EndsTheReview()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.CallAsync("OpenCreate", (object?)null);
        await harness.EditAsync(FillNewEntry);

        // Even a change that never reached the edit handler stops Save: the review describes another form.
        await harness.CallAsync("ReviewAsync");
        await harness.SetFormAsync(form => form.AllowWorkflows = false);
        Assert.DoesNotContain("signal-catalog-save", await harness.HtmlAsync(component), StringComparison.Ordinal);
        await harness.CallAsync("SaveAsync");
        Assert.Equal("Review the changes before saving.", await harness.ValueAsync<string?>("localProblem"));
        Assert.DoesNotContain(harness.Requests, request => request.Path == "/api/signal-catalog/entries");

        await ReviewThenAsync(() => harness.EditAsync(form => form.RolesText = "recall-manager"));
        await ReviewThenAsync(() => harness.EditAsync(form => form.Fields[0].IsArray = true));
        await ReviewThenAsync(() => harness.EditAsync(form => form.Fields[0].DataType = WorkflowVariableTypes.Json));
        await ReviewThenAsync(async () =>
        {
            var form = (await harness.ValueAsync<SignalCatalogForm?>("form"))!;
            await harness.CallAsync("RemoveField", form.Fields[^1]);
        });
        await ReviewThenAsync(() => harness.CallAsync("AddField"));

        async Task ReviewThenAsync(Func<Task> edit)
        {
            await harness.CallAsync("ReviewAsync");
            Assert.Contains("id=\"signal-catalog-save\"", await harness.HtmlAsync(component), StringComparison.Ordinal);
            await edit();
            Assert.Null(await harness.ValueAsync<object?>("review"));
            Assert.DoesNotContain("signal-catalog-save", await harness.HtmlAsync(component), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Conflict_KeepsTheFormAndReloadLatestReplacesItOnlyWhenTheEntryLoads()
    {
        const string conflict = "Signal catalog entry 'warehouse.product-recall' was changed by another request; reload and try again.";
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        var entry = harness.Entries[0];
        await harness.CallAsync("OpenEdit", entry);
        await harness.EditAsync(form => form.Description = "My draft");
        await harness.CallAsync("ReviewAsync");
        harness.Respond = request => request.Method == HttpMethod.Put ? Error(HttpStatusCode.Conflict, conflict) : null;

        await harness.CallAsync("SaveAsync");

        var update = Assert.Single(harness.Requests, request => request.Method == HttpMethod.Put);
        Assert.Equal($"/api/signal-catalog/entries/{Name}", update.Path);
        using (var body = JsonDocument.Parse(update.Body!))
        {
            Assert.Equal(Updated, body.RootElement.GetProperty("expectedUpdatedAt").GetDateTimeOffset());
            Assert.Equal("My draft", body.RootElement.GetProperty("description").GetString());
            Assert.False(body.RootElement.TryGetProperty("name", out _));
        }

        var kept = await harness.ValueAsync<SignalCatalogForm?>("form");
        Assert.NotNull(kept);
        Assert.Equal("My draft", kept.Description);
        Assert.True(await harness.ValueAsync<bool>("editConflict"));
        Assert.Equal($"{conflict} Your form is preserved.", await harness.ValueAsync<string?>("formError"));
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Contains("Reload latest", await harness.HtmlAsync(component), StringComparison.Ordinal);

        // A reload that fails keeps the draft and the conflict.
        harness.Respond = request => request.Method == HttpMethod.Get && request.Path == $"/api/signal-catalog/entries/{Name}"
            ? Error(HttpStatusCode.InternalServerError, "The database is unavailable.")
            : null;
        await harness.CallAsync("ReloadEditAsync");
        Assert.Same(kept, await harness.ValueAsync<SignalCatalogForm?>("form"));
        Assert.Equal("My draft", kept.Description);
        Assert.True(await harness.ValueAsync<bool>("editConflict"));
        Assert.Equal(
            "The latest version could not be loaded, so your changes are kept. Try Reload latest again.",
            await harness.ValueAsync<string?>("formError"));

        // One that loads replaces it with the latest version.
        var newer = entry with { Description = "Changed elsewhere", UpdatedAt = Updated.AddMinutes(5) };
        harness.Respond = null;
        harness.Entries[0] = newer;
        await harness.CallAsync("ReloadEditAsync");
        var latest = (await harness.ValueAsync<SignalCatalogForm?>("form"))!;
        Assert.Equal("Changed elsewhere", latest.Description);
        Assert.Equal(newer.UpdatedAt, latest.ExpectedUpdatedAt);
        Assert.False(await harness.ValueAsync<bool>("editConflict"));
        Assert.Null(await harness.ValueAsync<string?>("formError"));
    }

    [Fact]
    public async Task ArchivePanel_OffersArchiveOnlyWhenEveryCountIsZero()
    {
        await using var harness = new Harness
        {
            Usage = Usage(published: 3, running: 1, waits: 12, versions: [new SignalCatalogVersionUseDto(41, "claims-review", "Claims review", 3, true, [4])])
        };
        var component = await harness.RenderAsync();
        var entry = harness.Entries[0];
        await harness.CallAsync("OpenArchiveAsync", entry);

        Assert.Contains(harness.Requests, request => request.Path.StartsWith($"/api/signal-catalog/entries/{Name}/usage?", StringComparison.Ordinal));
        var html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        foreach (var label in new[]
                 {
                     "Published workflow versions that use it", "Workflow versions with a running instance that use it",
                     "Open signal waits for it", "Workflow families that start on it", "Broadcasts still delivering"
                 })
        {
            Assert.Contains(label, html, StringComparison.Ordinal);
        }
        Assert.Matches(">Open signal waits for it</dt>\\s*<dd[^>]*>12</dd>", html);
        Assert.Contains("claims-review v3", html, StringComparison.Ordinal);
        Assert.Contains("Archiving waits until every count above is zero.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"signal-catalog-archive\"", html, StringComparison.Ordinal);

        await harness.CallAsync("ArchiveAsync");
        Assert.Empty(harness.Js.Calls);
        Assert.DoesNotContain(harness.Requests, request => request.Path.EndsWith("/archive", StringComparison.Ordinal));

        // Nothing uses the name any more: Archive is offered, after a confirmation.
        harness.Usage = Usage();
        await harness.CallAsync("OpenArchiveAsync", entry);
        Assert.Contains("id=\"signal-catalog-archive\"", await harness.HtmlAsync(component), StringComparison.Ordinal);
        await harness.CallAsync("ArchiveAsync");

        Assert.Equal(["confirm"], harness.Js.Calls);
        var archive = Assert.Single(harness.Requests, request => request.Path == $"/api/signal-catalog/entries/{Name}/archive");
        using (var body = JsonDocument.Parse(archive.Body!))
        {
            Assert.Equal(Updated, body.RootElement.GetProperty("expectedUpdatedAt").GetDateTimeOffset());
        }

        Assert.Null(await harness.ValueAsync<SignalCatalogEntryDto?>("archiveTarget"));
        Assert.Equal($"Signal catalog entry {Name} archived.", await harness.ValueAsync<string?>("success"));
    }

    [Fact]
    public async Task Archive_ADeclinedConfirmationSendsNothingAndARefusalIsShown()
    {
        const string blocked = "Signal catalog entry 'warehouse.product-recall' cannot be archived: 1 instance waits for it.";
        await using var harness = new Harness { Usage = Usage() };
        await harness.RenderAsync();
        var entry = harness.Entries[0];
        await harness.CallAsync("OpenArchiveAsync", entry);

        harness.Js.Answer = false;
        await harness.CallAsync("ArchiveAsync");
        Assert.DoesNotContain(harness.Requests, request => request.Path.EndsWith("/archive", StringComparison.Ordinal));
        Assert.NotNull(await harness.ValueAsync<SignalCatalogEntryDto?>("archiveTarget"));

        // A wait that started since the usage read: the API refuses, and the panel says why.
        harness.Js.Answer = true;
        harness.Respond = request => request.Path.EndsWith("/archive", StringComparison.Ordinal)
            ? Error(HttpStatusCode.Conflict, blocked)
            : null;
        await harness.CallAsync("ArchiveAsync");
        Assert.Equal(blocked, await harness.ValueAsync<string?>("archiveError"));
        Assert.NotNull(await harness.ValueAsync<SignalCatalogEntryDto?>("archiveTarget"));
    }

    [Fact]
    public async Task ArchivedFilter_ListsArchivedEntriesWithReactivate()
    {
        await using var harness = new Harness();
        harness.Entries.Add(Entry("claims.retired", SignalCatalogStatuses.Archived));
        var component = await harness.RenderAsync();
        Assert.DoesNotContain("claims.retired", await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.CallAsync("OnStatusChangedAsync", new ChangeEventArgs { Value = SignalCatalogStatuses.Archived });
        Assert.Contains(harness.Requests, request =>
            request.Path.StartsWith("/api/signal-catalog/entries?", StringComparison.Ordinal)
            && request.Path.Contains("status=archived", StringComparison.Ordinal));
        var html = await harness.HtmlAsync(component);
        Assert.Contains("aria-label=\"Reactivate claims.retired\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Edit claims.retired\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(Name, html, StringComparison.Ordinal);

        await harness.CallAsync("ReactivateAsync", harness.Entries[1]);
        var reactivate = Assert.Single(harness.Requests, request => request.Path == "/api/signal-catalog/entries/claims.retired/reactivate");
        using (var body = JsonDocument.Parse(reactivate.Body!))
        {
            Assert.Equal(Updated, body.RootElement.GetProperty("expectedUpdatedAt").GetDateTimeOffset());
        }
        Assert.Equal("Signal catalog entry claims.retired reactivated.", await harness.ValueAsync<string?>("success"));
    }

    [Fact]
    public async Task NamesWithoutAnEntry_ShowTheModeAndOpenAPrefilledForm()
    {
        await using var harness = new Harness
        {
            Uncataloged = new UncatalogedSignalNameListDto(
                [new UncatalogedSignalNameDto("claims.policy-changed", 2), new UncatalogedSignalNameDto("orders.cancelled", 1)],
                true,
                SignalCatalogModes.Required)
        };
        var component = await harness.RenderAsync();

        var html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Contains("claims.policy-changed", html, StringComparison.Ordinal);
        Assert.Contains("2 versions", html, StringComparison.Ordinal);
        Assert.Contains("1 version", html, StringComparison.Ordinal);
        Assert.Contains("More names are in use than are listed.", html, StringComparison.Ordinal);
        Assert.Contains("Catalog mode: Required: publishing a workflow and throwing through the API need an entry", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Create entry for orders.cancelled\"", html, StringComparison.Ordinal);

        await harness.CallAsync("OpenCreate", "orders.cancelled");
        var form = (await harness.ValueAsync<SignalCatalogForm?>("form"))!;
        Assert.False(form.IsEditing);
        Assert.Equal("orders.cancelled", form.Name);
        Assert.Contains("value=\"orders.cancelled\"", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A403_ShowsTheSettingsAdministratorMessage()
    {
        await using var harness = new Harness
        {
            Respond = request => request.Path.StartsWith("/api/signal-catalog/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : null
        };
        var component = await harness.RenderAsync();

        Assert.Equal(SignalCatalogFormatting.SettingsAdministratorMessage, await harness.ValueAsync<string?>("error"));
        Assert.True(await harness.ValueAsync<bool>("loadFailed"));
        Assert.Equal(SignalCatalogFormatting.SettingsAdministratorMessage, await harness.ValueAsync<string?>("uncatalogedError"));

        await harness.CallAsync("OpenCreate", (object?)null);
        await harness.EditAsync(FillNewEntry);
        await harness.CallAsync("ReviewAsync");
        Assert.Equal(SignalCatalogFormatting.SettingsAdministratorMessage, await harness.ValueAsync<string?>("formError"));
        Assert.Contains("Signal catalog unavailable", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityChange_DropsALateReplyAndClearsTheForm()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        await harness.CallAsync("OpenEdit", harness.Entries[0]);
        await harness.EditAsync(form => form.Description = "Draft of the first identity");
        await harness.CallAsync("ReviewAsync");

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Hold = (HttpMethod.Put, held.Task);
        var saving = harness.CallAsync("SaveAsync");
        await Harness.WaitForAsync(() => harness.Requests.Any(request => request.Method == HttpMethod.Put));
        var reads = harness.Requests.Count(IsListRead);

        await harness.ChangeIdentityAsync("other-token");
        held.SetResult();
        await saving;
        await Harness.WaitForAsync(() => harness.Requests.Count(IsListRead) > reads);

        Assert.Null(await harness.ValueAsync<SignalCatalogForm?>("form"));
        Assert.Null(await harness.ValueAsync<string?>("success"));
        Assert.Null(await harness.ValueAsync<string?>("formError"));
        Assert.False(await harness.ValueAsync<bool>("submitting"));

        // A preview that returns after an identity change shows no review either.
        await harness.CallAsync("OpenEdit", harness.Entries[0]);
        var previewHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Hold = (HttpMethod.Post, previewHeld.Task);
        var reviewing = harness.CallAsync("ReviewAsync");
        await Harness.WaitForAsync(() => harness.Requests.Any(request => request.Path == "/api/signal-catalog/preview"));
        await harness.ChangeIdentityAsync("third-token");
        previewHeld.SetResult();
        await reviewing;
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Null(await harness.ValueAsync<SignalCatalogForm?>("form"));
    }

    [Fact]
    public async Task CatalogText_IsRenderedEncoded()
    {
        await using var harness = new Harness
        {
            Impact = new SignalCatalogImpactDto(Name, 1, [new SignalCatalogProblemDto(41, "claims-review", Markup, 3, 4, Markup)], false),
            Uncataloged = new UncatalogedSignalNameListDto([], false, SignalCatalogModes.Optional)
        };
        harness.Entries[0] = harness.Entries[0] with
        {
            Description = Markup,
            ThrowRoles = [Markup],
            Fields = [new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String, Description: Markup)],
            UpdatedBy = Markup
        };
        var component = await harness.RenderAsync();

        var html = await harness.HtmlAsync(component);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);

        // The form shows the same text in its fields, and the review shows a problem's message as text.
        await harness.CallAsync("OpenEdit", harness.Entries[0]);
        await harness.CallAsync("ReviewAsync");
        html = await harness.HtmlAsync(component);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("claims-review v3 (#41)</a>: &lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData(" , ,", new string[0])]
    [InlineData("recall-manager", new[] { "recall-manager" })]
    [InlineData(" recall-manager , warehouse-lead,, Recall-Manager ", new[] { "recall-manager", "warehouse-lead" })]
    public void Roles_AreSplitOnCommasTrimmedAndWithoutEmptySegments(string? text, string[] roles) =>
        Assert.Equal(roles, SignalCatalogFormatting.ParseRoles(text));

    [Fact]
    public void Formatting_DescribesEntriesAndUsage()
    {
        Assert.Equal("Any signal user", SignalCatalogFormatting.RolesLabel([]));
        Assert.Equal("a, b", SignalCatalogFormatting.RolesLabel(["a", "b"]));
        Assert.Equal("No fields", SignalCatalogFormatting.FieldCountText(0));
        Assert.Equal("1 field", SignalCatalogFormatting.FieldCountText(1));
        Assert.Equal("3 fields", SignalCatalogFormatting.FieldCountText(3));
        Assert.True(SignalCatalogFormatting.CanArchive(Usage()));
        Assert.False(SignalCatalogFormatting.CanArchive(Usage(delivering: 1)));
        Assert.False(SignalCatalogFormatting.CanArchive(Usage(starting: 1)));
        Assert.Equal("Optional: names without an entry keep working.", SignalCatalogFormatting.ModeText("unknown"));

        // An edit form reads back the entry it was opened from.
        var form = SignalCatalogForm.Edit(Entry());
        Assert.Equal("recall-manager", form.RolesText);
        Assert.Null(form.Problem());
        Assert.Equal(Entry().Fields, form.FieldDtos);
    }

    private static void FillNewEntry(SignalCatalogForm form)
    {
        form.Name = Name;
        form.Description = "A supplier recalled a product.";
        var reason = form.AddField();
        reason.Name = "reason";
        reason.Required = true;
    }

    private static bool IsListRead(CapturedRequest request) =>
        request.Path.StartsWith("/api/signal-catalog/entries?", StringComparison.Ordinal);

    private static SignalCatalogEntryDto Entry(string name = Name, string status = SignalCatalogStatuses.Active) =>
        new(name,
            "A supplier recalled a product.",
            status,
            [
                new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String, Required: true, Description: "Why it was recalled."),
                new SignalCatalogFieldDto("skus", WorkflowVariableTypes.String, IsArray: true, Required: true)
            ],
            ["recall-manager"],
            false,
            true,
            true,
            Created,
            "alice",
            Updated,
            "alice",
            status == SignalCatalogStatuses.Archived ? Updated : null);

    private static SignalCatalogUsageDto Usage(
        long published = 0,
        long running = 0,
        long waits = 0,
        long starting = 0,
        long delivering = 0,
        IReadOnlyList<SignalCatalogVersionUseDto>? versions = null) =>
        new(Name, published, running, waits, starting, delivering,
            new PagedResult<SignalCatalogVersionUseDto>(versions ?? [], 1, 50, versions?.Count ?? 0));

    private static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        new(status) { Content = JsonContent.Create(new { error = message }) };

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);

    private sealed class ConfirmingJs : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        public bool Answer { get; set; } = true;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            return ValueTask.FromResult((TValue)(object)Answer);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private readonly Activator activator = new();
        private readonly TokenState token = new();
        private readonly List<CapturedRequest> requests = [];

        public Harness()
        {
            var http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("https://flowbit.test") };
            token.Set("admin-token");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new WorkflowApiClient(http));
            services.AddSingleton(token);
            services.AddSingleton<IJSRuntime>(Js);
            services.AddSingleton<IComponentActivator>(activator);
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }

        public ConfirmingJs Js { get; } = new();

        public List<CapturedRequest> Requests
        {
            get
            {
                lock (requests) return [.. requests];
            }
        }

        public List<SignalCatalogEntryDto> Entries { get; } = [Entry()];
        public string Mode { get; set; } = SignalCatalogModes.Optional;
        public IReadOnlyList<string> SignalRoles { get; set; } = ["admin"];

        /// <summary>The preview's reply; null means every live version fits.</summary>
        public SignalCatalogImpactDto? Impact { get; set; }

        public SignalCatalogUsageDto Usage { get; set; } = SignalCatalogPageTests.Usage();

        public UncatalogedSignalNameListDto Uncataloged { get; set; } =
            new([new UncatalogedSignalNameDto("claims.policy-changed", 2)], false, SignalCatalogModes.Optional);

        /// <summary>When it returns a response for a request, the request answers it.</summary>
        public Func<CapturedRequest, HttpResponseMessage?>? Respond { get; set; }

        /// <summary>When set, a request of this method waits for the task before it is answered.</summary>
        public (HttpMethod Method, Task Task)? Hold { get; set; }

        public Task<HtmlRootComponent> RenderAsync() =>
            renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SignalCatalogPage>(ParameterView.Empty));

        public Task<string> HtmlAsync(HtmlRootComponent component) =>
            renderer.Dispatcher.InvokeAsync(component.ToHtmlString);

        public Task CallAsync(string method, params object?[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(SignalCatalogPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Page, arguments);
            if (result is Task task) await task;
            Render();
        });

        /// <summary>Changes the form as typing does: the change, then the inputs' edit handler.</summary>
        public Task EditAsync(Action<SignalCatalogForm> edit) => renderer.Dispatcher.InvokeAsync(() =>
        {
            edit(Form());
            typeof(SignalCatalogPage).GetMethod("OnFormEdited", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Page, null);
            Render();
        });

        /// <summary>Changes the form without its edit handler.</summary>
        public Task SetFormAsync(Action<SignalCatalogForm> edit) => renderer.Dispatcher.InvokeAsync(() =>
        {
            edit(Form());
            Render();
        });

        public Task<T> ValueAsync<T>(string field) => renderer.Dispatcher.InvokeAsync(() =>
            (T)typeof(SignalCatalogPage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(activator.Page)!);

        public Task ChangeIdentityAsync(string newToken) =>
            renderer.Dispatcher.InvokeAsync(() => token.Set(newToken));

        public static async Task WaitForAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (condition()) return;
                await Task.Delay(25);
            }
            Assert.Fail("The condition was not reached.");
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }

        private SignalCatalogForm Form() =>
            (SignalCatalogForm)typeof(SignalCatalogPage).GetField("form", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(activator.Page)!;

        private void Render() =>
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Page, null);

        private sealed class Activator : IComponentActivator
        {
            public SignalCatalogPage? Page { get; private set; }

            public IComponent CreateInstance(Type componentType)
            {
                var component = (IComponent)System.Activator.CreateInstance(componentType)!;
                if (component is SignalCatalogPage page) Page = page;
                return component;
            }
        }

        private sealed class Handler(Harness harness) : HttpMessageHandler
        {
            private const string EntriesPath = "/api/signal-catalog/entries/";

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.PathAndQuery;
                var captured = new CapturedRequest(
                    request.Method,
                    path,
                    request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                lock (harness.requests) harness.requests.Add(captured);

                if (harness.Hold is { } hold && hold.Method == request.Method)
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }

                if (harness.Respond?.Invoke(captured) is { } response)
                {
                    return response;
                }

                if (path.StartsWith("/api/signal-catalog/entries?", StringComparison.Ordinal))
                {
                    var status = path.Contains("status=archived", StringComparison.Ordinal)
                        ? SignalCatalogStatuses.Archived
                        : SignalCatalogStatuses.Active;
                    var items = harness.Entries.Where(entry => entry.Status == status).ToList();
                    return Json(new SignalCatalogListDto(items, 1, 50, items.Count, harness.Mode, harness.SignalRoles));
                }

                if (path == "/api/signal-catalog/uncataloged-names")
                {
                    return Json(harness.Uncataloged);
                }

                if (path == "/api/signal-catalog/preview")
                {
                    return Json(harness.Impact ?? new SignalCatalogImpactDto(Name, 0, [], false));
                }

                if (path == "/api/signal-catalog/entries" && request.Method == HttpMethod.Post)
                {
                    return Json(Entry(), HttpStatusCode.Created);
                }

                if (path.StartsWith(EntriesPath, StringComparison.Ordinal))
                {
                    var rest = path[EntriesPath.Length..];
                    if (rest.Contains("/usage?", StringComparison.Ordinal))
                    {
                        return Json(harness.Usage);
                    }

                    var name = Uri.UnescapeDataString(rest.Split('/')[0]);
                    var entry = harness.Entries.FirstOrDefault(item => item.Name == name);
                    if (entry is null)
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    return rest.EndsWith("/archive", StringComparison.Ordinal)
                        ? Json(entry with { Status = SignalCatalogStatuses.Archived, ArchivedAt = Updated })
                        : rest.EndsWith("/reactivate", StringComparison.Ordinal)
                            ? Json(entry with { Status = SignalCatalogStatuses.Active, ArchivedAt = null })
                            : Json(entry);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
                new(status) { Content = JsonContent.Create(value) };
        }
    }
}
