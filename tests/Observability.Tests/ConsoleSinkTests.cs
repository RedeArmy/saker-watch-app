using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Observability.Logging;
using Serilog;

namespace SakerWatch.Observability.Tests;

/// <summary>The console is the log collector's input; it is written off the request path. Console.Out is process-wide, so these run alone.</summary>
[Collection("Console.Out")]
public sealed class ConsoleSinkTests
{
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

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle().Which.Should().Contain("hello \\\"NVDA\\\"").And.Contain("\"Service\":\"svc\"");
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
}
