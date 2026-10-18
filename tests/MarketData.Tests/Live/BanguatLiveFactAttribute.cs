namespace SakerWatch.MarketData.Tests.Live;

/// <summary>
/// A test that calls the public Banco de Guatemala service. There is no key and no quota to spend, but the service is not ours, so it is
/// skipped unless <c>SAKER_LIVE_BANGUAT=1</c>: CI and a plain <c>dotnet test</c> never call it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BanguatLiveFactAttribute : FactAttribute
{
    /// <summary>Environment variable that switches the live check on.</summary>
    public const string SwitchVariable = "SAKER_LIVE_BANGUAT";

    /// <summary>Creates the attribute, skipping the test when the live run was not asked for.</summary>
    public BanguatLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(SwitchVariable) != "1")
        {
            Skip = $"Live check: set {SwitchVariable}=1 to run it.";
        }
    }
}
