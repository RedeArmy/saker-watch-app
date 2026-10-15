using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using SakerWatch.BuildingBlocks.Application.Audit;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Tracing;

namespace SakerWatch.BuildingBlocks.Persistence.Audit;

/// <summary>Audit settings, bound from the <c>Audit</c> section.</summary>
public sealed class AuditOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Audit";

    /// <summary>Base64 secret (at least 32 bytes) that keys the actor reference. Keep it out of the repository and never rotate it casually: it changes every actor reference.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ActorPepper { get; set; } = string.Empty;
}

/// <summary>Cross-field validation of <see cref="AuditOptions"/>.</summary>
internal sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        string pepper = options.ActorPepper;
        if (string.IsNullOrEmpty(pepper))
        {
            return ValidateOptionsResult.Fail("Audit:ActorPepper is required.");
        }

        // Base64 decodes to at most three bytes per four characters, so a buffer that size never rejects a long but valid secret.
        Span<byte> buffer = new byte[pepper.Length];
        if (!Convert.TryFromBase64String(pepper, buffer, out int written))
        {
            return ValidateOptionsResult.Fail("Audit:ActorPepper must be valid Base64.");
        }

        return written >= ActorReference.MinimumPepperBytes
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Audit:ActorPepper must decode to at least {ActorReference.MinimumPepperBytes} bytes (it decodes to {written}).");
    }
}

/// <summary>
/// <see cref="IAuditTrail"/> over <c>platform.append_audit</c>, which serializes writers and extends the hash chain. The actor
/// is stored as a keyed hash of the user id; trace and correlation ids give the lineage (ADR-0016).
/// </summary>
public sealed class AuditTrail : IAuditTrail
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly DbSession _session;
    private readonly ICorrelationContext _correlation;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _pepper;

    /// <summary>Creates the audit trail.</summary>
    public AuditTrail(DbSession session, ICorrelationContext correlation, TimeProvider timeProvider, IOptions<AuditOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _session = session;
        _correlation = correlation;
        _timeProvider = timeProvider;
        _pepper = Convert.FromBase64String(options.Value.ActorPepper);
    }

    /// <inheritdoc />
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        NpgsqlTransaction transaction = _session.Transaction
            ?? throw new InvalidOperationException("The audit trail needs an active unit of work, so the entry commits with the change it records.");

        await using var append = new NpgsqlCommand(
            "SELECT platform.append_audit(@at, @type, @actor, @subject, @details, @trace, @correlation)",
            _session.Connection,
            transaction);
        append.Parameters.AddWithValue("at", _timeProvider.GetUtcNow());
        append.Parameters.AddWithValue("type", auditEvent.EventType);
        append.Parameters.AddWithValue("actor", auditEvent.ActorUserId is { } actor ? ActorReference.For(actor, _pepper) : "system");
        append.Parameters.AddWithValue("subject", (object?)auditEvent.SubjectRef ?? DBNull.Value);
        append.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(auditEvent.Details ?? new { }, _json) });
        append.Parameters.AddWithValue("trace", (object?)System.Diagnostics.Activity.Current?.TraceId.ToString() ?? DBNull.Value);
        append.Parameters.AddWithValue("correlation", (object?)_correlation.CorrelationId ?? DBNull.Value);
        await append.ExecuteScalarAsync(cancellationToken);
    }
}
