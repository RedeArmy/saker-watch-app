using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.BuildingBlocks.Redis;
using StackExchange.Redis;

namespace SakerWatch.BuildingBlocks.Web.RateLimiting;

/// <summary>What one limiter allows: which limiter it is, how many permits a window holds and how long the window is.</summary>
/// <param name="Scope">The limiter this is (<c>global</c>, <c>expensive</c>, <c>preauth</c>): each has its own limit and its own count.</param>
/// <param name="PermitLimit">Permits in a window.</param>
/// <param name="Window">The length of a window.</param>
public sealed record LimiterPolicy(string Scope, int PermitLimit, TimeSpan Window);

/// <summary>
/// The limiter of one caller in one scope (ADR-0048): a sliding window counter that counts in the memory of the process or, when several API processes
/// run, in Redis, so that they count the caller together. Time is cut in windows of <c>window</c>; a request is let through when the permits of the
/// current window plus those of the window before it, weighted by how much of that one still overlaps the last <c>window</c>, leave room for it. A burst
/// at the end of one window followed by another at the start of the next therefore does not get twice the limit, as it would with a fixed window.
/// Only the requests let through are counted.
/// <para>
/// Neither path ever waits: asking with zero permits answers yes or no at once (the built-in limiter of the framework, asked for zero permits on a full
/// window, waits for the next window instead of answering no). When Redis is the place to count and cannot be reached the request is counted in the
/// memory of the process, with the same limit, and the fall back is counted in <c>sakerwatch_http_rate_limiter_fallback_total</c>; the limiter never
/// turns off, and each process then allows the whole limit.
/// </para>
/// </summary>
public sealed class CallerRateLimiter : RateLimiter
{
    private static readonly Counter<long> _fallbacks = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_http_rate_limiter_fallback_total",
        description: "Requests the rate limiter counted in the process because Redis could not be reached, by limiter scope.");

    // KEYS: the prefix of the caller's counters. ARGV: permits to take (0 asks without taking), limit, window in ms, the time in ms (negative: use the
    // clock of Redis, which is the same for every process; a given time is for tests). Returns {1 | 0, ms to wait}. Windows are numbered from the epoch;
    // the counter of a window lives for two windows, long enough to be the one before.
    private const string _script = """
        local now = tonumber(ARGV[4])
        if now < 0 then
            local t = redis.call('TIME')
            now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        end
        local window = tonumber(ARGV[3])
        local index = math.floor(now / window)
        local elapsed = now - index * window
        local current = tonumber(redis.call('GET', KEYS[1] .. ':' .. index) or '0')
        local previous = tonumber(redis.call('GET', KEYS[1] .. ':' .. (index - 1)) or '0')
        local estimate = previous * (1 - elapsed / window) + current
        local permits = tonumber(ARGV[1])
        local wanted = permits
        if wanted == 0 then wanted = 1 end
        if estimate + wanted > tonumber(ARGV[2]) then
            return {0, window - elapsed}
        end
        if permits > 0 then
            redis.call('INCRBY', KEYS[1] .. ':' .. index, permits)
            redis.call('PEXPIRE', KEYS[1] .. ':' .. index, window * 2)
        end
        return {1, 0}
        """;

    private readonly IConnectionMultiplexer? _redis;
    private readonly Func<bool> _countInRedis;
    private readonly RedisKey _prefix;
    private readonly string _scope;
    private readonly int _limit;
    private readonly long _windowMs;
    private readonly TimeProvider _time;
    private readonly bool _redisClock;
    private readonly Lock _gate = new();

    private long _index = long.MinValue;
    private double _current;
    private double _previous;
    private long _lastUsed;
    private long _succeeded;
    private long _failed;

    /// <summary>Creates the limiter of one caller in one scope.</summary>
    /// <param name="redis">The shared connection, or <see langword="null"/> where there is none (the count is then always in memory).</param>
    /// <param name="countInRedis">Asked for each request: whether the count is kept in Redis now (several API processes) or in memory.</param>
    /// <param name="policy">The scope, the permits in a window and the length of the window.</param>
    /// <param name="partitionKey">The caller (a user or an address).</param>
    /// <param name="time">The clock of the process: for the count in memory and for how long a limiter has been idle.</param>
    /// <param name="redisClock">True (as deployed) to take the time of a request from Redis, the same for every process; false to take it from <paramref name="time"/> (tests).</param>
    public CallerRateLimiter(
        IConnectionMultiplexer? redis,
        Func<bool> countInRedis,
        LimiterPolicy policy,
        string partitionKey,
        TimeProvider time,
        bool redisClock = true)
    {
        ArgumentNullException.ThrowIfNull(countInRedis);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentNullException.ThrowIfNull(time);

        _redis = redis;
        _countInRedis = countInRedis;
        _scope = policy.Scope;
        _limit = policy.PermitLimit;
        _windowMs = (long)policy.Window.TotalMilliseconds;
        _time = time;
        _redisClock = redisClock;
        _lastUsed = time.GetTimestamp();
        // The caller is a user id or an address: hashed, so the key has only characters a key may have and no address sits in Redis.
        _prefix = RedisKeys.For("ratelimit", policy.Scope, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(partitionKey)))[..32]);
    }

    /// <inheritdoc />
    public override TimeSpan? IdleDuration => _time.GetElapsedTime(Interlocked.Read(ref _lastUsed));

    /// <inheritdoc />
    public override RateLimiterStatistics? GetStatistics()
    {
        double estimate;
        lock (_gate)
        {
            estimate = Estimate(NowMs());
        }

        return new RateLimiterStatistics
        {
            CurrentAvailablePermits = Math.Max(0, (long)Math.Floor(_limit - estimate)),
            CurrentQueuedCount = 0,
            TotalSuccessfulLeases = Interlocked.Read(ref _succeeded),
            TotalFailedLeases = Interlocked.Read(ref _failed),
        };
    }

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => InMemory(permitCount);

    /// <inheritdoc />
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        if (_redis is null || !_countInRedis())
        {
            return InMemory(permitCount);
        }

        try
        {
            long now = _redisClock ? -1 : _time.GetUtcNow().ToUnixTimeMilliseconds();
            RedisResult result = await _redis.GetDatabase()
                .ScriptEvaluateAsync(_script, [_prefix], [permitCount, _limit, _windowMs, now])
                .WaitAsync(cancellationToken);
            long[] answer = (long[]?)result ?? throw new RedisException("The rate limit script returned nothing.");
            return Lease(answer[0] == 1, answer[1]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _fallbacks.Add(1, new KeyValuePair<string, object?>("scope", _scope));
            return InMemory(permitCount);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private double Estimate(long nowMs)
    {
        long index = nowMs / _windowMs;
        // Windows that have gone by empty the counts: the one before the current is the previous only if it is the one just ended.
        double current = 0;
        double previous = 0;
        if (index == _index)
        {
            (current, previous) = (_current, _previous);
        }
        else if (index == _index + 1)
        {
            previous = _current;
        }

        return (previous * (1 - ((double)(nowMs - (index * _windowMs)) / _windowMs))) + current;
    }

    private RateLimitLease InMemory(int permitCount)
    {
        long now = NowMs();
        long index = now / _windowMs;
        bool acquired;
        lock (_gate)
        {
            if (index != _index)
            {
                (_current, _previous, _index) = index == _index + 1 ? (0d, _current, index) : (0d, 0d, index);
            }

            double estimate = Estimate(now);
            acquired = estimate + Math.Max(permitCount, 1) <= _limit;
            if (acquired)
            {
                _current += permitCount;
            }
        }

        return Lease(acquired, _windowMs - (now - (index * _windowMs)));
    }

    private RateLimitLease Lease(bool acquired, long millisecondsToWait)
    {
        Interlocked.Exchange(ref _lastUsed, _time.GetTimestamp());
        if (acquired)
        {
            Interlocked.Increment(ref _succeeded);
            return new Granted();
        }

        Interlocked.Increment(ref _failed);
        return new Denied(TimeSpan.FromMilliseconds(Math.Max(millisecondsToWait, 1000)));
    }

    private sealed class Granted : RateLimitLease
    {
        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class Denied(TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
