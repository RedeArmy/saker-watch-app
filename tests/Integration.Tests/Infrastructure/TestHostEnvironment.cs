using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>The host environment a real host provides, for tests that build a plain service collection.</summary>
/// <param name="name">Environment name, for example <see cref="Environments.Production"/>.</param>
internal sealed class TestHostEnvironment(string name = "Production") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
