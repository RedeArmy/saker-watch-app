using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>
/// Starts a throwaway MongoDB. The image comes from <c>deploy/docker-compose.yml</c>; MongoDB 5 and later need a CPU with
/// AVX, so on a host without it (some VMs) the fixture falls back to <c>mongo:4.4</c>, and
/// <c>SAKER_TEST_MONGO_IMAGE</c> overrides both. The driver supports servers from 4.2.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder(ResolveImage()).Build();

    /// <summary>Root connection string of the running server.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A database with a unique name, so each test starts empty.</summary>
    public IMongoDatabase NewDatabase() =>
        new MongoClient(ConnectionString).GetDatabase($"t_{Guid.CreateVersion7():N}");

    private static string ResolveImage()
    {
        string? overridden = Environment.GetEnvironmentVariable("SAKER_TEST_MONGO_IMAGE");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        return CpuHasAvx() ? ComposeImages.For("mongo") : "mongo:4.4";
    }

    // Only x86 reports its features on a `flags` line of /proc/cpuinfo. Anything else (the arm64 of production and CI lists `Features`) cannot be
    // asked, and is not what the missing-AVX crash is about, so it counts as able to run the image of the compose file.
    internal static bool CpuHasAvx(IEnumerable<string>? cpuInfoLines = null)
    {
        const string cpuInfo = "/proc/cpuinfo";
        if (cpuInfoLines is null && !File.Exists(cpuInfo))
        {
            return true;
        }

        string[] flagLines = [.. (cpuInfoLines ?? File.ReadLines(cpuInfo)).Where(line => line.StartsWith("flags", StringComparison.Ordinal))];
        return flagLines.Length == 0 || flagLines.Any(line => line.Contains(" avx", StringComparison.Ordinal));
    }
}
