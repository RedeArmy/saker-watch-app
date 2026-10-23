using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Deployment;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.BuildingBlocks.Redis;
using StackExchange.Redis;

namespace SakerWatch.BuildingBlocks.Web.Deployment;

/// <summary>
/// Lets the API processes see each other (ADR-0048). Each one writes itself into a sorted set in Redis every few seconds with a lease; the size of the
/// set, after the expired members are dropped, is how many are running. <see cref="Count"/> is the larger of that and
/// <see cref="DeploymentOptions.ApiReplicas"/>, so an operator who scales out and forgets to say so is still counted with: the limiters move to Redis and
/// the shares of per-process counts shrink, and the warning says that the setting is wrong. The count never drops below what is declared, and if Redis cannot be
/// reached the last count seen is kept. A host with no Redis counts what it declares.
/// </summary>
public sealed partial class ApiInstanceRegistry : BackgroundService, IApiInstances
{
    // KEYS: the set. ARGV: this process, the lease in ms. The clock is Redis's own, the same for every process. Returns how many members the set has.
    private const string _script = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        redis.call('ZADD', KEYS[1], now + tonumber(ARGV[2]), ARGV[1])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]) * 2)
        return redis.call('ZCARD', KEYS[1])
        """;

    private readonly IConnectionMultiplexer? _redis;
    private readonly DeploymentOptions _deployment;
    private readonly ILogger<ApiInstanceRegistry> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _lease;
    private readonly string _self;
    private readonly RedisKey _key = RedisKeys.For("deployment", "api");
    private int _observed;
    private bool _warned;
    private bool _redisDown;

    /// <summary>Creates the registry.</summary>
    /// <param name="deployment">What the operator declares.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="redis">The shared connection, or <see langword="null"/> in a host that has none.</param>
    /// <param name="interval">How often this process writes itself down (10 s when not given).</param>
    /// <param name="lease">How long a write lasts: a process that stops writing is dropped after it (30 s when not given).</param>
    /// <param name="time">The clock, only to make the identifier of this process (the system clock when not given).</param>
    public ApiInstanceRegistry(
        IOptions<DeploymentOptions> deployment,
        ILogger<ApiInstanceRegistry> logger,
        IConnectionMultiplexer? redis = null,
        TimeSpan? interval = null,
        TimeSpan? lease = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(logger);

        _self = Guid.CreateVersion7((time ?? TimeProvider.System).GetUtcNow()).ToString("N");
        _deployment = deployment.Value;
        _logger = logger;
        _redis = redis;
        _interval = interval ?? TimeSpan.FromSeconds(10);
        _lease = lease ?? TimeSpan.FromSeconds(30);
        SakerTelemetry.Meter.CreateObservableGauge(
            "sakerwatch_deployment_api_instances",
            () => new[]
            {
                new Measurement<int>(_deployment.ApiReplicas, new KeyValuePair<string, object?>("kind", "declared")),
                new Measurement<int>(Volatile.Read(ref _observed), new KeyValuePair<string, object?>("kind", "observed")),
                new Measurement<int>(Count, new KeyValuePair<string, object?>("kind", "counted")),
            },
            description: "API processes: as declared in Deployment:ApiReplicas, as seen in Redis, and the number everything counts with (the larger).");
    }

    /// <inheritdoc />
    public int Count => Math.Max(_deployment.ApiReplicas, Volatile.Read(ref _observed));

    /// <summary>Writes this process into the set and reads how many there are. A failure of Redis keeps the last count.</summary>
    public async Task BeatAsync(CancellationToken cancellationToken)
    {
        if (_redis is null)
        {
            return;
        }

        try
        {
            RedisResult result = await _redis.GetDatabase()
                .ScriptEvaluateAsync(_script, [_key], [_self, (long)_lease.TotalMilliseconds])
                .WaitAsync(cancellationToken);
            Volatile.Write(ref _observed, (int)result);
            Recovered();
            Compare();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            // ObjectDisposedException: the host is being torn down and the connection went first. Nothing to count then.
            if (!_redisDown)
            {
                _redisDown = true;
                LogUnreachable(ex.GetType().Name);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            await BeatAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_redis is not null)
        {
            try
            {
                await _redis.GetDatabase().SortedSetRemoveAsync(_key, _self).WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                // The lease runs out by itself.
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Recovered()
    {
        if (_redisDown)
        {
            _redisDown = false;
            LogReachable();
        }
    }

    private void Compare()
    {
        int observed = Volatile.Read(ref _observed);
        if (observed > _deployment.ApiReplicas && !_warned)
        {
            _warned = true;
            LogUndeclared(observed, _deployment.ApiReplicas);
        }
        else if (observed <= _deployment.ApiReplicas && _warned)
        {
            _warned = false;
            LogDeclared(_deployment.ApiReplicas);
        }
    }

    [LoggerMessage(EventId = 9110, Level = LogLevel.Warning, Message = "{Observed} API processes are running but Deployment:ApiReplicas says {Declared}; counting with {Observed}. Set Deployment__ApiReplicas on every process")]
    private partial void LogUndeclared(int observed, int declared);

    [LoggerMessage(EventId = 9111, Level = LogLevel.Information, Message = "The API processes seen in Redis are no more than Deployment:ApiReplicas ({Declared}) again")]
    private partial void LogDeclared(int declared);

    [LoggerMessage(EventId = 9112, Level = LogLevel.Warning, Message = "Redis could not be reached to count the API processes ({ExceptionType}); counting with the last number seen")]
    private partial void LogUnreachable(string exceptionType);

    [LoggerMessage(EventId = 9113, Level = LogLevel.Information, Message = "Redis can be reached again to count the API processes")]
    private partial void LogReachable();
}
