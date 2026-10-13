using System.Collections.Concurrent;
using SakerWatch.Identity.Contracts;

namespace SakerWatch.TestSupport;

/// <summary>In-memory <see cref="IIdentityProviderAdmin"/> for tests: records deletions and can be told to fail.</summary>
public sealed class FakeIdentityProviderAdmin : IIdentityProviderAdmin
{
    private readonly ConcurrentDictionary<(string Issuer, string Subject), byte> _identities = new();
    private int _calls;
    private int _failuresToInject;

    /// <summary>Number of calls received, including failed ones.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The outcome returned while failures are injected.</summary>
    public ProviderDeletionOutcome InjectedFailure { get; set; } = ProviderDeletionOutcome.TransientFailure;

    /// <summary>Registers an identity so a later delete reports <see cref="ProviderDeletionOutcome.Deleted"/>.</summary>
    public void Add(string issuer, string subject) => _identities[(issuer, subject)] = 0;

    /// <summary>True when the identity still exists.</summary>
    public bool Exists(string issuer, string subject) => _identities.ContainsKey((issuer, subject));

    /// <summary>Makes the next <paramref name="count"/> calls fail with <see cref="InjectedFailure"/>.</summary>
    public void FailNext(int count) => Interlocked.Exchange(ref _failuresToInject, count);

    /// <inheritdoc />
    public Task<ProviderDeletionOutcome> DeleteIdentityAsync(string issuer, string subject, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);

        if (Interlocked.Decrement(ref _failuresToInject) >= 0)
        {
            return Task.FromResult(InjectedFailure);
        }

        Interlocked.Exchange(ref _failuresToInject, 0);
        return Task.FromResult(_identities.TryRemove((issuer, subject), out _) ? ProviderDeletionOutcome.Deleted : ProviderDeletionOutcome.AlreadyAbsent);
    }
}
