using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Application.Behaviors;
using SakerWatch.BuildingBlocks.Application.Telemetry;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Demo.Features;

namespace SakerWatch.Application.Tests.Pipeline;

public sealed class PipelineTests : IDisposable
{
    private readonly PipelineTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Send_ValidRequest_RunsBehaviorsOutermostFirstThenHandler()
    {
        Result<string> result = await _host.Send(new PingCommand("eder"));

        result.Value.Should().Be("pong:eder");
        _host.Log.Entries.Should().Equal("A:before", "B:before", "handler", "B:after", "A:after");
    }

    [Fact]
    public async Task Send_InvalidRequest_ReturnsValidationErrorWithoutCallingHandler()
    {
        Result<string> result = await _host.Send(new PingCommand(""));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.Code.Should().Be(ValidationBehavior<PingCommand, Result<string>>.ErrorCode);
        result.Error.Details.Should().ContainKey("Name").WhoseValue.Should().Contain("Name is required.");
        _host.Log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_RequestBreakingSeveralRules_ReportsEveryMessageForTheField()
    {
        Result<string> result = await _host.Send(new PingCommand(new string('x', 11)));

        result.Error!.Details!["Name"].Should().Equal("Name is too long.");
    }

    [Fact]
    public async Task Send_RequestWithoutValidators_SkipsValidation()
    {
        Result<int> result = await _host.Send(new ReadQuery());

        result.Value.Should().Be(7);
    }

    [Fact]
    public async Task Send_RequestWithoutHandler_Throws()
    {
        Func<Task> act = async () => await _host.Send(new UnhandledCommand());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Send_Success_EmitsSpanNamedModuleDotUseCaseWithTags()
    {
        using var capture = new SpanCapture("Demo.ReadQuery");

        await _host.Send(new ReadQuery());

        Activity span = capture.Single();
        span.GetTagItem("saker.module").Should().Be("Demo");
        span.GetTagItem("saker.usecase").Should().Be("ReadQuery");
        span.GetTagItem("saker.outcome").Should().Be("success");
        span.Status.Should().Be(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task Send_ExpectedFailure_TagsOutcomeButDoesNotMarkSpanAsError()
    {
        using var capture = new SpanCapture("Demo.RejectedCommand");

        await _host.Send(new RejectedCommand());

        Activity span = capture.Single();
        span.GetTagItem("saker.outcome").Should().Be("failure");
        span.GetTagItem("saker.error_code").Should().Be("demo.conflict");
        span.Status.Should().Be(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task Send_UnexpectedFailure_MarksSpanAsError()
    {
        using var capture = new SpanCapture("Demo.BrokenCommand");

        await _host.Send(new BrokenCommand());

        capture.Single().Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Send_HandlerThrows_RecordsExceptionOnSpanAndRethrows()
    {
        using var capture = new SpanCapture("Demo.ThrowingCommand");

        Func<Task> act = async () => await _host.Send(new ThrowingCommand());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        Activity span = capture.Single();
        span.GetTagItem("saker.outcome").Should().Be("exception");
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.Events.Should().Contain(e => e.Name == "exception");
    }

    [Fact]
    public async Task Send_Success_RecordsDurationMetricWithLowCardinalityTags()
    {
        using var capture = new MetricCapture("sakerwatch_demo_usecase_duration_seconds", "ReadQuery");

        await _host.Send(new ReadQuery());

        MetricCapture.Sample sample = capture.Single();
        sample.Tags.Should().Contain("outcome", "success");
        sample.Tags.Should().NotContainKey("error_type");
    }

    [Fact]
    public async Task Send_ExpectedFailure_RecordsErrorTypeTag()
    {
        using var capture = new MetricCapture("sakerwatch_demo_usecase_duration_seconds", "RejectedCommand");

        await _host.Send(new RejectedCommand());

        MetricCapture.Sample sample = capture.Single();
        sample.Tags.Should().Contain("outcome", "failure");
        sample.Tags.Should().Contain("error_type", "Conflict");
    }

    [Fact]
    public async Task Send_Success_LogsCompletionWithoutRequestContent()
    {
        await _host.Send(new PingCommand("secret"));

        var record = _host.Logs.GetSnapshot().Should().ContainSingle(r => r.Id.Id == 9001).Subject;
        record.Level.Should().Be(LogLevel.Information);
        record.Message.Should().Contain("Demo.PingCommand").And.NotContain("secret");
    }

    [Fact]
    public async Task Send_ExpectedFailure_LogsRejectionAtInformation()
    {
        await _host.Send(new RejectedCommand());

        var record = _host.Logs.GetSnapshot().Should().ContainSingle(r => r.Id.Id == 9002).Subject;
        record.Level.Should().Be(LogLevel.Information);
        record.Message.Should().Contain("demo.conflict");
    }

    [Fact]
    public async Task Send_UnexpectedFailure_LogsAtError()
    {
        await _host.Send(new BrokenCommand());

        _host.Logs.GetSnapshot().Should().ContainSingle(r => r.Id.Id == 9003 && r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Send_HandlerThrows_LogsExceptionAtError()
    {
        Func<Task> act = async () => await _host.Send(new ThrowingCommand());
        await act.Should().ThrowAsync<InvalidOperationException>();

        var record = _host.Logs.GetSnapshot().Should().ContainSingle(r => r.Id.Id == 9004).Subject;
        record.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _spans = [];

        public SpanCapture(string spanName)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == UseCaseTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == spanName)
                    {
                        lock (_spans)
                        {
                            _spans.Add(activity);
                        }
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity Single()
        {
            lock (_spans)
            {
                return _spans.Should().ContainSingle().Subject;
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Sample> _samples = [];

        public MetricCapture(string instrumentName, string useCase)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == UseCaseTelemetry.MeterName && instrument.Name == instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                Dictionary<string, object?> map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
                if (map.TryGetValue("usecase", out object? name) && Equals(name, useCase))
                {
                    lock (_samples)
                    {
                        _samples.Add(new Sample(value, map.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? string.Empty)));
                    }
                }
            });
            _listener.Start();
        }

        public Sample Single()
        {
            lock (_samples)
            {
                return _samples.Should().ContainSingle().Subject;
            }
        }

        public void Dispose() => _listener.Dispose();

        public sealed record Sample(double Seconds, Dictionary<string, string> Tags);
    }
}
