namespace SakerWatch.Application.Tests.Pipeline;

/// <summary>
/// Tests that capture spans or metrics of the shared demo use cases through process-wide listeners. xUnit runs the classes of one
/// collection one after the other, so a span emitted by one class is never counted by another's capture.
/// </summary>
[CollectionDefinition(Name)]
public static class PipelineTelemetryGroup
{
    /// <summary>Collection name.</summary>
    public const string Name = "pipeline-telemetry";
}
