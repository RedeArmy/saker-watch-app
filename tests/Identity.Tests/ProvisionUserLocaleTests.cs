using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Audit;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.Identity.Application.Features.ProvisionUser;
using SakerWatch.Identity.Application.Features.UpdatePreferences;

namespace SakerWatch.Identity.Tests;

/// <summary>The locale claim of a token is stored only when it maps to a supported UI locale.</summary>
public sealed class ProvisionUserLocaleTests
{
    private sealed class RecordingDirectory : IUserDirectory
    {
        public ExternalIdentity? Seen { get; private set; }

        public Task<ResolvedUserResult> ProvisionAsync(Guid newId, ExternalIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Seen = identity;
            return Task.FromResult(new ResolvedUserResult(new ResolvedUser(newId, UserAccountStatus.Active), false));
        }
    }

    private sealed class NoAudit : IAuditTrail
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("es-GT", "es-GT")]
    [InlineData("ES-gt", "es-GT")]
    [InlineData(" en-US ", "en-US")]
    [InlineData("es-MX", "es-GT")]
    [InlineData("es_419", "es-GT")]
    [InlineData("en", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", null)]
    [InlineData("e", null)]
    [InlineData("<script>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task Handle_StoresOnlyASupportedLocale(string? claim, string? expected)
    {
        var directory = new RecordingDirectory();
        var time = new FakeTimeProvider();
        var handler = new ProvisionUserHandler(directory, new NoAudit(), new TimeProviderIdGenerator(time), time);

        await handler.Handle(new ProvisionUserCommand(new ExternalIdentity("https://idp.example/o/1", "sub", null, null, claim)), CancellationToken.None);

        directory.Seen!.Locale.Should().Be(expected);
    }
}
