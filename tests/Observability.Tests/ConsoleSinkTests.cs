using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.BuildingBlocks.Observability.Logging;
using Serilog;

namespace SakerWatch.Observability.Tests;

/// <summary>The console is the log collector's input; it is written off the request path. Console.Out is process-wide, so these run alone.</summary>
[Collection("Console.Out")]
public sealed class ConsoleSinkTests
{
    private static readonly Action<Microsoft.Extensions.Logging.ILogger, Exception?> _lastWords =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, "LastWords"), "last words before shutdown");

    private sealed class BlockingWriter : TextWriter
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly StringBuilder _written = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Written
        {
            get
            {
                lock (_written)
                {
                    return _written.ToString();
                }
            }
        }

        public void Release() => _release.Set();

        public override void Write(char value)
        {
            _release.Wait();
            lock (_written)
            {
                _written.Append(value);
            }
        }

        public override void Write(string? value)
        {
            _release.Wait();
            lock (_written)
            {
                _written.Append(value);
            }
        }
    }

    private static async Task<T> WithConsole<T>(TextWriter writer, Func<Task<T>> action)
    {
        TextWriter original = Console.Out;
        Console.SetOut(writer);
        try
        {
            return await action();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task Events_ReachTheConsoleAsOneJsonLineEach_OnceTheLoggerIsFlushed()
    {
        var writer = new StringWriter();

        await WithConsole(writer, () =>
        {
            Serilog.Core.Logger logger = new LoggerConfiguration().ApplySakerDefaults("svc").CreateLogger();
            logger.Information("hello {Ticker}", "NVDA");
            logger.Dispose();
            return Task.FromResult(0);
        });

        // Console.Out is process-wide: keep only this logger's lines in case another test's host writes while it is swapped.
        string[] lines = [.. writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.Contains("\"Service\":\"svc\"", StringComparison.Ordinal))];
        lines.Should().ContainSingle().Which.Should().Contain("hello \\\"NVDA\\\"");
    }

    [Fact]
    public async Task ABlockedConsole_DoesNotBlockTheCallerOrGrowWithoutBound()
    {
        var writer = new BlockingWriter();

        TimeSpan elapsed = await WithConsole(writer, () =>
        {
            Serilog.Core.Logger logger = new LoggerConfiguration().ApplySakerDefaults("svc").CreateLogger();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < SakerLogging.ConsoleQueueLength * 2; i++)
            {
                logger.Information("event {Index}", i);
            }

            TimeSpan took = clock.Elapsed;
            writer.Release();
            logger.Dispose();
            return Task.FromResult(took);
        });

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "writing to a stalled console must not hold the caller");
        writer.Written.Should().Contain("event 0", "what was queued is written once the console recovers");
    }

    [Fact]
    public async Task ADisposedHost_FlushesTheQueuedEvents_SoTheLastLinesBeforeShutdownAreNotLost()
    {
        var writer = new StringWriter();

        await WithConsole(writer, () =>
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            builder.AddSakerObservability("svc-flush");
            using IHost host = builder.Build();
            _lastWords(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test"), null);
            return Task.FromResult(0);
        });

        writer.ToString().Should().Contain("last words before shutdown");
    }
}
