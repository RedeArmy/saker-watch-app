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

    private static bool CpuHasAvx()
    {
        const string cpuInfo = "/proc/cpuinfo";
        return !File.Exists(cpuInfo)
            || File.ReadLines(cpuInfo).Any(line => line.StartsWith("flags", StringComparison.Ordinal) && line.Contains(" avx", StringComparison.Ordinal));
    }
}
