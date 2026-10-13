using Serilog;
using Flowbit.Ui.Components;
using Flowbit.Ui.Clients;
using Flowbit.Ui.Auth;
using Flowbit.Ui.Dashboard;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting UI host...");
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Filter.ByExcluding(log => log.Properties.TryGetValue("SourceContext", out var source)
            && (source.ToString().Contains("Duende.AccessTokenManagement", StringComparison.Ordinal)
                || source.ToString().Contains("Microsoft.AspNetCore.Authentication.OpenIdConnect", StringComparison.Ordinal))));

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    var authentication = builder.AddFlowbitUiAuthentication();
    builder.Services.AddScoped<DashboardService>();

    builder.Services.AddHttpClient(SessionHttpClientFactory.ApiClientName, client =>
    {
        var baseUrl = builder.Configuration["WorkflowApi:BaseUrl"]
            ?? "http://localhost:5017";
        client.BaseAddress = new Uri(baseUrl);
        client.Timeout = TimeSpan.FromMinutes(6);
    })
    .RedactLoggedHeaders(_ => true)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

    builder.Services.AddHttpClient(WorkflowApiClient.AiClientName, client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["WorkflowApi:BaseUrl"] ?? "http://localhost:5017");
        client.Timeout = WorkflowApiClient.AiRequestTimeout;
    })
    .RedactLoggedHeaders(_ => true)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

    builder.Services.AddHttpClient(ApiActorContextClient.ClientName, client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["WorkflowApi:BaseUrl"] ?? "http://localhost:5017");
        client.Timeout = TimeSpan.FromSeconds(15);
    }).RedactLoggedHeaders(_ => true)
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    builder.Services.AddScoped<SessionHttpClientFactory>();
    builder.Services.AddScoped(sp =>
    {
        var factory = sp.GetRequiredService<SessionHttpClientFactory>();
        return new WorkflowApiClient(factory.CreateClient(SessionHttpClientFactory.ApiClientName), factory);
    });

    var app = builder.Build();

    app.UseForwardedHeaders();
    app.UseFlowbitHttpsBoundary(authentication, app.Environment);
    app.UseSerilogRequestLogging();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();

    if (!authentication.IsDevelopmentToken)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/token", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });
    }

    app.UseAntiforgery();
    app.MapFlowbitAuthentication(authentication);

    app.MapStaticAssets();
    var components = app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();
    if (!authentication.IsDevelopmentToken) components.RequireAuthorization();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "UI Host terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
