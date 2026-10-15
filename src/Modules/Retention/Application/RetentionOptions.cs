using System.ComponentModel.DataAnnotations;

namespace SakerWatch.Retention.Application;

/// <summary>How long data is kept and how a run behaves. Bound from the <c>Retention</c> section; defaults follow data-model.md section 5.</summary>
public sealed class RetentionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Retention";

    /// <summary>Whether the worker schedules a daily run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true (the default) a run only counts what it would remove and reports it. Switch it off after reading a dry run
    /// in the target environment; deleting is the exception, not the default.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Days to keep processed outbox rows.</summary>
    [Range(1, 3650)]
    public int OutboxProcessedDays { get; set; } = 7;

    /// <summary>Days to keep consumer dedupe records.</summary>
    [Range(1, 3650)]
    public int InboxProcessedDays { get; set; } = 7;

    /// <summary>Days to keep succeeded jobs.</summary>
    [Range(1, 3650)]
    public int JobsSucceededDays { get; set; } = 14;

    /// <summary>Days to keep dead-lettered jobs.</summary>
    [Range(1, 3650)]
    public int JobsDeadDays { get; set; } = 90;

    /// <summary>Rows removed per statement, so no single delete holds locks for long.</summary>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 1000;

    /// <summary>Most batches per target per run; a backlog larger than this is finished by the next run.</summary>
    [Range(1, 1000)]
    public int MaxBatchesPerTarget { get; set; } = 50;

    /// <summary>How often the scheduler checks whether today's run exists.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
}
