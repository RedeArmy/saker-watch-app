using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SakerWatch.BuildingBlocks.Application.Telemetry;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.BuildingBlocks.Observability.Logging;
using Serilog.Events;

namespace SakerWatch.Observability.Tests;

// Building a host through AddSakerObservability replaces the process-wide Serilog logger (Log.Logger) and disposing it closes whatever that
// logger is at that moment. Two such hosts alive at once, in parallel tests, close each other's logger: ConsoleSinkTests lost its last lines
// in about one run in seventeen when a class like this one ran beside it. They share the collection so they run one after the other.
[Collection("Console.Out")]
public sealed class EnrichmentAndSetupTests
{
    [Fact]
    public void Events_CarryTheServiceName()
    {
        var (logger, sink) = CollectingSink.Create("saker-api");

        logger.Information("hello");

        sink.Events.Single().Properties["Service"].ToString().Should().Be("\"saker-api\"");
    }

    [Fact]
    public void Events_InsideAnActivity_CarryTraceAndSpanIds()
    {
        var (logger, sink) = CollectingSink.Create();
        using var source = new ActivitySource("test.enrichment");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.enrichment",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using Activity activity = source.StartActivity("work")!;
        logger.Information("inside");

        LogEvent logged = sink.Events.Single();
        logged.Properties["TraceId"].ToString().Should().Contain(activity.TraceId.ToString());
        logged.Properties["SpanId"].ToString().Should().Contain(activity.SpanId.ToString());
    }

    [Fact]
    public void Events_OutsideAnActivity_HaveNoTraceIds()
    {
        var (logger, sink) = CollectingSink.Create();

        logger.Information("outside");

        sink.Events.Single().Properties.Should().NotContainKey("TraceId");
    }

    [Fact]
    public void FrameworkNamespaces_AreQuietedBelowWarning()
    {
        var (logger, sink) = CollectingSink.Create();

        logger.ForContext("SourceContext", "Microsoft.AspNetCore.Routing").Information("noisy");
        logger.ForContext("SourceContext", "SakerWatch.Something").Information("kept");

        sink.Events.Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)).Should().Equal("kept");
    }

    [Fact]
    public void AddSakerObservability_RegistersOurSourceAndTheServiceResource()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.AddSakerObservability("saker-test");
        using IHost host = builder.Build();

        var tracer = host.Services.GetRequiredService<TracerProvider>();
        using Activity? span = UseCaseTelemetry.Source.StartActivity("Test.Span");

        span.Should().NotBeNull("the SakerWatch source is subscribed");
        tracer.GetResource().Attributes.Should().Contain(a => a.Key == "service.name" && (string)a.Value == "saker-test");
    }

    private static bool RegistersAnOtlpExporter(string? endpoint)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        // An explicit value, null included, wins over the environment of the machine that runs the test.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint });
        builder.AddSakerObservability("saker-test");

        using IHost host = builder.Build();

        host.Services.GetRequiredService<TracerProvider>().Should().NotBeNull();
        return builder.Services.Any(descriptor => (descriptor.ServiceType.FullName ?? string.Empty).Contains("Otlp", StringComparison.OrdinalIgnoreCase)
            || (descriptor.ImplementationType?.FullName ?? string.Empty).Contains("Otlp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddSakerObservability_WithoutOtlpEndpoint_RegistersNoExporter_AndStillBuilds() =>
        RegistersAnOtlpExporter(endpoint: null).Should().BeFalse("telemetry leaves the process only when the endpoint is configured");

    [Fact]
    public void AddSakerObservability_WithAnOtlpEndpoint_RegistersTheExporter() =>
        RegistersAnOtlpExporter("http://127.0.0.1:4317").Should().BeTrue();

    [Fact]
    public void ApplySakerDefaults_WithOtlpEndpoint_BuildsALoggerThatNeverThrowsWhenTheCollectorIsDown()
    {
        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration()
            .ApplySakerDefaults("saker-test", writeToConsole: false, otlpEndpoint: "http://127.0.0.1:1")
            .CreateLogger();

        Action act = () => logger.Information("collector is unreachable {Attempt}", 1);

        act.Should().NotThrow();
    }
}
