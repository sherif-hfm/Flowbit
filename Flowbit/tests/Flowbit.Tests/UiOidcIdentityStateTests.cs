extern alias FlowbitUi;

using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using System.Reflection;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using SessionRouteView = FlowbitUi::Flowbit.Ui.Components.SessionRouteView;
using WorkflowEditor = FlowbitUi::Flowbit.Ui.Components.Pages.WorkflowEditor;
using TokenPage = FlowbitUi::Flowbit.Ui.Components.Pages.Token;
using Xunit;

namespace Flowbit.Tests;

public sealed class UiOidcIdentityStateTests
{
    [Fact]
    public void Oidc_state_exposes_no_token_and_rejects_development_token_changes()
    {
        var state = new TokenState();
        state.ConfigureOidcMode();
        Assert.False(state.IsDevelopment);
        Assert.False(state.AuthReady);
        Assert.False(state.HasToken);
        Assert.Throws<InvalidOperationException>(() => state.Set("a-development-token"));

        state.ApplyOidcSession("session-a", new ActorContextDto("alice", ["Reviewer"]));

        Assert.True(state.AuthReady);
        Assert.True(state.HasToken);
        Assert.True(state.IdentityResolved);
        Assert.Null(state.Token);
        Assert.Equal("alice", state.CurrentUser);
        Assert.NotNull(state.IdentityKey);
    }

    [Fact]
    public void Same_session_refresh_preserves_identity_and_does_not_reset_editor_or_ai()
    {
        var state = new TokenState();
        state.ApplyOidcSession("session-a", new ActorContextDto("alice", ["Reviewer", "Manager"]));
        var originalKey = state.IdentityKey;
        var notifications = 0;
        state.Changed += () => notifications++;

        state.ApplyOidcSession("session-a", new ActorContextDto("ALICE", ["manager", "reviewer"]));

        Assert.Equal(originalKey, state.IdentityKey);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void Role_change_and_session_replacement_invalidate_previous_identity_revision()
    {
        var state = new TokenState();
        state.ApplyOidcSession("session-a", new ActorContextDto("alice", ["Reviewer"]));
        var originalKey = state.IdentityKey;
        var notifications = 0;
        state.Changed += () => notifications++;
        state.ApplyOidcSession("session-a", new ActorContextDto("alice", ["Manager"]));
        var changedRolesKey = state.IdentityKey;
        Assert.NotEqual(originalKey, changedRolesKey);
        state.ApplyOidcSession("session-b", new ActorContextDto("alice", ["Manager"]));
        Assert.NotEqual(changedRolesKey, state.IdentityKey);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void Revocation_is_idempotent_and_cannot_affect_another_circuit_identity()
    {
        var first = new TokenState();
        var second = new TokenState();
        first.ApplyOidcSession("session-a", new ActorContextDto("alice", ["Reviewer"]));
        second.ApplyOidcSession("session-b", new ActorContextDto("bob", ["Manager"]));
        var notifications = 0;
        first.Changed += () => notifications++;

        first.InvalidateOidcSession();
        first.InvalidateOidcSession();

        Assert.Equal(1, notifications);
        Assert.True(first.AuthReady);
        Assert.False(first.HasToken);
        Assert.Null(first.IdentityKey);
        Assert.Null(first.CurrentUser);
        Assert.Empty(first.CurrentRoles);
        Assert.True(second.HasToken);
        Assert.Equal("bob", second.CurrentUser);
    }

    [Fact]
    public void Existing_editor_stays_mounted_after_expiry_but_new_editor_route_is_denied()
    {
        var state = new TokenState();
        state.ApplyOidcSession("session-a", new ActorContextDto("alice", ["Reviewer"]));
        var boundary = CreateBoundary(state, typeof(WorkflowEditor), new Dictionary<string, object?> { ["Id"] = 17L });
        RememberRoute(boundary);
        Assert.True(CanRender(boundary));

        state.InvalidateOidcSession();
        RememberRoute(boundary);
        Assert.True(CanRender(boundary));

        typeof(SessionRouteView).GetProperty(nameof(SessionRouteView.RouteData))!.SetValue(boundary,
            new RouteData(typeof(WorkflowEditor), new Dictionary<string, object?> { ["Id"] = 18L }));
        RememberRoute(boundary);
        Assert.False(CanRender(boundary));
    }

    [Fact]
    public void Oidc_cannot_render_development_identity_page_even_after_sign_in()
    {
        var state = new TokenState();
        state.ApplyOidcSession("session-a", new ActorContextDto("alice", ["admin"]));
        Assert.False(CanRender(CreateBoundary(state, typeof(TokenPage), new Dictionary<string, object?>())));
    }

    [Fact]
    public void Initial_anonymous_oidc_navigation_never_mounts_operational_page()
    {
        var state = new TokenState();
        state.ConfigureOidcMode();
        var boundary = CreateBoundary(state, typeof(WorkflowEditor), new Dictionary<string, object?>());
        RememberRoute(boundary);
        Assert.False(CanRender(boundary));
        state.InvalidateOidcSession();
        RememberRoute(boundary);
        Assert.False(CanRender(boundary));
    }

    [Fact]
    public async Task Repeated_editor_route_parameters_preserve_unsaved_state_after_session_expiry()
    {
        var state = new TokenState();
        state.ApplyOidcSession("session-a", new ActorContextDto("alice", ["Reviewer"]));
        var editor = new WorkflowEditor();
        typeof(WorkflowEditor).GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, state);
        var parameters = typeof(WorkflowEditor).GetMethod("OnParametersSetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)parameters.Invoke(editor, null)!;
        typeof(WorkflowEditor).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, true);
        typeof(WorkflowEditor).GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, true);
        typeof(WorkflowEditor).GetField("notice", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, "Unsaved local work");
        state.InvalidateOidcSession();

        await (Task)parameters.Invoke(editor, null)!;

        Assert.True((bool)typeof(WorkflowEditor).GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!);
        Assert.True((bool)typeof(WorkflowEditor).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!);
        Assert.Equal("Unsaved local work", typeof(WorkflowEditor).GetField("notice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
    }

    private static SessionRouteView CreateBoundary(TokenState state, Type pageType, IReadOnlyDictionary<string, object?> parameters)
    {
        var boundary = new SessionRouteView();
        typeof(SessionRouteView).GetProperty(nameof(SessionRouteView.RouteData))!.SetValue(boundary, new RouteData(pageType, parameters));
        typeof(SessionRouteView).GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(boundary, state);
        return boundary;
    }

    private static void RememberRoute(SessionRouteView boundary) => typeof(SessionRouteView)
        .GetMethod("RememberEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(boundary, null);
    private static bool CanRender(SessionRouteView boundary) => (bool)typeof(SessionRouteView)
        .GetProperty("CanRenderRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boundary)!;
}
