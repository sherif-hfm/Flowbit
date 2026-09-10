extern alias FlowbitUi;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ActorClaimsComponent = FlowbitUi::Flowbit.Ui.Components.Shared.ActorClaims;
using Xunit;

namespace Flowbit.Tests;

public sealed class ActorClaimsUiTests
{
    [Fact]
    public async Task MissingAndEmptySnapshotsHaveDistinctDescriptions()
    {
        var missing = await RenderAsync(null);
        var empty = await RenderAsync(new Dictionary<string, string[]>());

        Assert.Contains("Not recorded", missing);
        Assert.DoesNotContain("No selected claims present", missing);
        Assert.Contains("No selected claims present", empty);
        Assert.DoesNotContain("Not recorded", empty);
    }

    [Fact]
    public async Task SnapshotEscapesNamesAndValuesAndPreservesRepeatedValues()
    {
        var html = await RenderAsync(new Dictionary<string, string[]>
        {
            ["<script>name</script>"] = ["<img src=x onerror=alert(1)>", "repeat", "repeat"],
            ["department"] = ["Research & Development"]
        });

        Assert.Contains("<details", html);
        Assert.Contains("<summary", html);
        Assert.Contains("&lt;script&gt;name&lt;/script&gt;", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("Research &amp; Development", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Equal(2, html.Split(">repeat</div>", StringSplitOptions.None).Length - 1);
    }

    private static async Task<string> RenderAsync(IReadOnlyDictionary<string, string[]>? claims)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ActorClaimsComponent>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Claims"] = claims }));
            return component.ToHtmlString();
        });
    }
}
