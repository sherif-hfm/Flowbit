using System.Net;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;

namespace Flowbit.Api.Hosting;

public static class ApiForwardedHeaders
{
    public static IServiceCollection AddFlowbitForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HostFilteringOptions>(filter => filter.AllowedHosts = AllowedHosts(configuration));
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            // Direct host filtering can precede this middleware. Restrict the
            // replacement host as well, while preserving the API's '*' default.
            var allowedHosts = AllowedHosts(configuration);
            if (!allowedHosts.Contains("*", StringComparer.Ordinal))
                foreach (var host in allowedHosts) forwarded.AllowedHosts.Add(host);
            // Keep the framework's loopback defaults. Never clear the trusted
            // proxy lists: empty lists would trust arbitrary forwarded headers.
            foreach (var proxy in configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
            {
                if (!IPAddress.TryParse(proxy, out var address))
                    throw new InvalidOperationException("ReverseProxy:KnownProxies entries must be valid proxy IP addresses.");
                forwarded.KnownProxies.Add(address);
            }
        });
        return services;
    }

    private static string[] AllowedHosts(IConfiguration configuration) => (configuration["AllowedHosts"] ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
