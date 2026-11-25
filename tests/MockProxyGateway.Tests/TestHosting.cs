using Microsoft.AspNetCore.Builder;

namespace MockProxyGateway.Tests;

internal static class TestHosting
{
    // A project reference to the Web SDK host copies its appsettings.json into the
    // test output directory. Synthetic hosts must not inherit that host's settings.
    // Tests of actual file/inline sources supply their own temporary content root,
    // and WebApplicationFactory intentionally uses the actual standalone host.
    private static readonly string ContentRoot = Directory.CreateDirectory(
        Path.Combine(AppContext.BaseDirectory, "isolated-test-host-content")).FullName;

    internal static WebApplicationBuilder CreateBuilder(string environment = "Development") =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ContentRootPath = ContentRoot,
            Args = []
        });
}
