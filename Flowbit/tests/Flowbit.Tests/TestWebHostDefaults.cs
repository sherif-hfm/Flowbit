using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowbit.Tests;

internal static class TestWebHostDefaults
{
    public static void IsolateFromMachine(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection()
            .UseEphemeralDataProtectionProvider();
    }
}
