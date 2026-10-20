namespace SakerWatch.MarketData.Tests.Live;

/// <summary>
/// A test that calls the public SEC EDGAR service. There is no key and no quota, but the SEC requires every caller to declare who it is, so the test is
/// skipped unless <c>SAKER_LIVE_EDGAR=1</c> and <c>Edgar__UserAgent</c> (a name and a contact address) are set: CI and a plain <c>dotnet test</c> never call it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EdgarLiveFactAttribute : FactAttribute
{
    /// <summary>Environment variable that switches the live check on.</summary>
    public const string SwitchVariable = "SAKER_LIVE_EDGAR";

    /// <summary>Environment variable that holds the declared identity (the same name the hosts read).</summary>
    public const string IdentityVariable = "Edgar__UserAgent";

    /// <summary>Creates the attribute, skipping the test when the live run was not asked for.</summary>
    public EdgarLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(SwitchVariable) != "1")
        {
            Skip = $"Live check: set {SwitchVariable}=1 and {IdentityVariable} to run it (see scripts/run-live-edgar.sh).";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(IdentityVariable)))
        {
            Skip = $"Live check: {IdentityVariable} is not set in this environment.";
        }
    }
}
