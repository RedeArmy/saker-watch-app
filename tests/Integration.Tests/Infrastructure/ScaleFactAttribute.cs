namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>
/// A measurement at the size the product is meant to serve (hundreds of listings with years of history). Loading the data takes about a minute,
/// so it runs only when asked for with <c>SAKER_SCALE=1</c> and never in the default suite or in CI.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ScaleFactAttribute : FactAttribute
{
    /// <summary>Environment variable that switches the scale measurements on.</summary>
    public const string SwitchVariable = "SAKER_SCALE";

    /// <summary>Creates the attribute, skipping the test unless the scale run was asked for.</summary>
    public ScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(SwitchVariable) != "1")
        {
            Skip = $"Scale measurement: set {SwitchVariable}=1 to run it.";
        }
    }
}
