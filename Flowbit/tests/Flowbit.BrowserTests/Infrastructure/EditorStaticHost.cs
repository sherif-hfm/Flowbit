using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>
/// Serves the real copied editor through in-process Kestrel on an ephemeral
/// loopback port. Only the editor document (plus a harmless favicon response)
/// is exposed: no directory browsing, no repository root.
/// </summary>
public sealed class EditorStaticHost : IAsyncDisposable
{
    private WebApplication? app;

    public string BaseAddress { get; private set; } = string.Empty;

    public async Task StartAsync(string editorHtmlPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(editorHtmlPath))
        {
            throw new InvalidOperationException(
                $"The editor fixture is missing: {editorHtmlPath}. The project copies it from the repository root at build time.");
        }

        var html = await File.ReadAllTextAsync(editorHtmlPath, cancellationToken);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            Args = new[] { "--urls", "http://127.0.0.1:0" },
        });
        // This test-only host must not inherit appsettings or machine endpoints.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();

        app = builder.Build();

        app.MapGet("/", () => Results.Text(html, "text/html; charset=utf-8"));
        app.MapGet("/flowbit-editor.html", () => Results.Text(html, "text/html; charset=utf-8"));
        app.MapGet("/favicon.ico", () => Results.StatusCode(StatusCodes.Status204NoContent));
        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /\n", "text/plain"));
        app.MapGet("/{*path}", () => Results.StatusCode(StatusCodes.Status404NotFound));

        await app.StartAsync(cancellationToken);
        BaseAddress = app.Urls
            .FirstOrDefault(url => url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The editor host did not bind to loopback IPv4.");
    }

    public async ValueTask DisposeAsync()
    {
        if (app is null)
        {
            return;
        }
        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdown.Token);
        }
        finally
        {
            await app.DisposeAsync();
            app = null;
        }
    }
}
