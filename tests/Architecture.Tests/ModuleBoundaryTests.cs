using System.Reflection;
using AwesomeAssertions;
using NetArchTest.Rules;

namespace SakerWatch.Architecture.Tests;

public sealed class ModuleBoundaryTests
{
    private static readonly string[] _modules =
        ["Identity", "MarketData", "Watchlist", "Analysis", "Signals", "Calculator", "Retention"];

    private static Assembly Load(string module, string layer) =>
        Assembly.Load($"SakerWatch.{module}.{layer}");

    [Fact]
    public void Domain_DoesNotDependOnOtherLayers()
    {
        foreach (var m in _modules)
        {
            var result = Types.InAssembly(Load(m, "Domain"))
                .ShouldNot().HaveDependencyOnAny(
                    $"SakerWatch.{m}.Application", $"SakerWatch.{m}.Infrastructure", "Microsoft.EntityFrameworkCore")
                .GetResult();
            result.IsSuccessful.Should().BeTrue($"{m}.Domain must stay pure");
        }
    }

    [Fact]
    public void Layers_RespectDependencyDirection()
    {
        foreach (var m in _modules)
        {
            Types.InAssembly(Load(m, "Application")).ShouldNot()
                .HaveDependencyOn($"SakerWatch.{m}.Infrastructure").GetResult().IsSuccessful.Should().BeTrue();
            Types.InAssembly(Load(m, "Contracts")).ShouldNot()
                .HaveDependencyOnAny($"SakerWatch.{m}.Domain", $"SakerWatch.{m}.Application", $"SakerWatch.{m}.Infrastructure")
                .GetResult().IsSuccessful.Should().BeTrue();
        }
    }

    [Fact]
    public void AllModuleAssemblies_AreLoadable_SoRulesAreNotVacuouslySkipped()
    {
        foreach (var m in _modules)
            foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Contracts" })
            {
                Types.InAssembly(Load(m, layer)).GetTypes().Should().NotBeEmpty($"{m}.{layer} must contain types");
            }
    }

    [Fact]
    public void Modules_OnlyUseOtherModulesContracts()
    {
        foreach (var m in _modules)
            foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
            {
                var others = _modules.Where(o => o != m).SelectMany(o => new[]
                {
                $"SakerWatch.{o}.Domain", $"SakerWatch.{o}.Application", $"SakerWatch.{o}.Infrastructure",
            }).ToArray();
                var result = Types.InAssembly(Load(m, layer)).ShouldNot().HaveDependencyOnAny(others).GetResult();
                result.IsSuccessful.Should().BeTrue($"{m}.{layer} may only use other modules' Contracts");
            }
    }
}
