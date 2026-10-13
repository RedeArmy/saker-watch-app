using System.Text.RegularExpressions;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>
/// Reads container image references from <c>deploy/docker-compose.yml</c>, so the compose file is the single
/// place where image versions live (Dependabot bumps it and the tests follow).
/// </summary>
internal static partial class ComposeImages
{
    private const string _solutionFile = "SakerWatch.slnx";
    private const string _composeFile = "deploy/docker-compose.yml";

    /// <summary>Returns the image of the compose service whose image repository starts with <paramref name="repository"/>.</summary>
    public static string For(string repository)
    {
        string compose = Path.Combine(FindRepositoryRoot(), _composeFile);
        foreach (string line in File.ReadLines(compose))
        {
            Match match = ImageLine().Match(line);
            if (match.Success && match.Groups["image"].Value.StartsWith(repository + ":", StringComparison.Ordinal))
            {
                return match.Groups["image"].Value;
            }
        }

        throw new InvalidOperationException($"No image '{repository}:*' found in {_composeFile}.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, _solutionFile)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException($"{_solutionFile} not found above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>[^\s#]+)")]
    private static partial Regex ImageLine();
}
