namespace SakerWatch.Retention.Domain;

/// <summary>The data sets the Retention module trims (data-model.md section 5). Each has a rule for when a row is old enough to go.</summary>
public enum RetentionTarget
{
    /// <summary><c>platform.idempotency_keys</c> past their own <c>expires_at</c>.</summary>
    IdempotencyKeys,

    /// <summary><c>platform.outbox_messages</c> that were processed more than the configured age ago.</summary>
    OutboxProcessed,

    /// <summary><c>platform.inbox_messages</c> (consumer dedupe records) older than the configured age.</summary>
    InboxProcessed,

    /// <summary>Finished <c>platform.job_queue</c> rows that succeeded.</summary>
    JobsSucceeded,

    /// <summary>Dead-lettered <c>platform.job_queue</c> rows, kept longer so they can be investigated.</summary>
    JobsDead,
}
