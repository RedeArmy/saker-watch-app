using AwesomeAssertions;
using SakerWatch.Identity.Contracts;
using SakerWatch.TestSupport;

namespace SakerWatch.Identity.Tests;

public sealed class IdentityProviderAdminTests
{
    private const string _issuer = "http://fake-idp.test/o/org";
    private const string _subject = "account-1";

    [Fact]
    public async Task Delete_ExistingIdentity_ReportsDeleted_ThenAlreadyAbsent()
    {
        var admin = new FakeIdentityProviderAdmin();
        admin.Add(_issuer, _subject);

        ProviderDeletionOutcome first = await admin.DeleteIdentityAsync(_issuer, _subject, CancellationToken.None);
        ProviderDeletionOutcome second = await admin.DeleteIdentityAsync(_issuer, _subject, CancellationToken.None);

        first.Should().Be(ProviderDeletionOutcome.Deleted);
        second.Should().Be(ProviderDeletionOutcome.AlreadyAbsent, "deleting twice must be safe");
        admin.Exists(_issuer, _subject).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_WithInjectedFailures_FailsThenRecovers()
    {
        var admin = new FakeIdentityProviderAdmin();
        admin.Add(_issuer, _subject);
        admin.FailNext(2);

        ProviderDeletionOutcome a = await admin.DeleteIdentityAsync(_issuer, _subject, CancellationToken.None);
        ProviderDeletionOutcome b = await admin.DeleteIdentityAsync(_issuer, _subject, CancellationToken.None);
        ProviderDeletionOutcome c = await admin.DeleteIdentityAsync(_issuer, _subject, CancellationToken.None);

        a.Should().Be(ProviderDeletionOutcome.TransientFailure);
        b.Should().Be(ProviderDeletionOutcome.TransientFailure);
        c.Should().Be(ProviderDeletionOutcome.Deleted);
        admin.Calls.Should().Be(3);
    }
}
