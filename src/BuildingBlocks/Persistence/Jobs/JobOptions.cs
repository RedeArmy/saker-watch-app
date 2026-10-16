using System.ComponentModel.DataAnnotations;

namespace SakerWatch.BuildingBlocks.Persistence.Jobs;

/// <summary>Job dispatcher settings, bound from the <c>Jobs</c> section (Worker only).</summary>
public sealed class JobOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Jobs";

    /// <summary>How long an idle loop sleeps before looking for work again.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Jobs one worker runs at the same time.</summary>
    [Range(1, 64)]
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>How long a claimed job is reserved; the worker renews it every third of this while the job runs.</summary>
    [Range(typeof(TimeSpan), "00:00:00.300", "04:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay after the first failure; it doubles per attempt.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00")]
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest retry delay.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "1.00:00:00")]
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often the worker looks for jobs whose last allowed attempt crashed it (their lease expired) and parks them as dead. Only
    /// one polling loop does it per interval, however many loops and workers run, so an idle queue costs one such query per interval
    /// instead of one per loop per poll. 0 checks on every poll.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan ParkInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often queue depth and age are sampled for metrics.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan StatsInterval { get; set; } = TimeSpan.FromSeconds(15);
}
