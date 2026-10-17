namespace SakerWatch.MarketData.Tests.Live;

/// <summary>
/// A test that calls a real, rate-limited external service with the owner's key. It is skipped unless <c>SAKER_LIVE_TWELVEDATA=1</c> and
/// <c>TwelveData__ApiKey</c> are set in the environment, so CI and a plain <c>dotnet test</c> never spend credits.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
    /// <summary>Environment variable that switches the live checks on.</summary>
    public const string SwitchVariable = "SAKER_LIVE_TWELVEDATA";

    /// <summary>Environment variable that holds the API key (the same name the hosts read).</summary>
    public const string KeyVariable = "TwelveData__ApiKey";

    /// <summary>Creates the attribute, skipping the test when the live run was not asked for.</summary>
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(SwitchVariable) != "1")
        {
            Skip = $"Live check: set {SwitchVariable}=1 and {KeyVariable} to run it (see scripts/run-live-twelvedata.sh).";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable)))
        {
            Skip = $"Live check: {KeyVariable} is not set in this environment.";
        }
    }
}
