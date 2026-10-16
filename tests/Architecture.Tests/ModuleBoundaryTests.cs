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
        foreach (string m in _modules)
        {
            TestResult result = Types.InAssembly(Load(m, "Domain"))
                .ShouldNot().HaveDependencyOnAny(
                    $"SakerWatch.{m}.Application", $"SakerWatch.{m}.Infrastructure", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Serilog", "MongoDB.Driver", "Npgsql")
                .GetResult();
            result.IsSuccessful.Should().BeTrue($"{m}.Domain must stay pure");
        }
    }

    [Fact]
    public void Layers_RespectDependencyDirection()
    {
        foreach (string m in _modules)
        {
            Types.InAssembly(Load(m, "Application")).ShouldNot()
                .HaveDependencyOn($"SakerWatch.{m}.Infrastructure").GetResult().IsSuccessful.Should().BeTrue();
            Types.InAssembly(Load(m, "Contracts")).ShouldNot()
                .HaveDependencyOnAny($"SakerWatch.{m}.Domain", $"SakerWatch.{m}.Application", $"SakerWatch.{m}.Infrastructure")
                .GetResult().IsSuccessful.Should().BeTrue();
        }
    }

    [Fact]
    public void Contracts_AreFrameworkFree()
    {
        foreach (string m in _modules)
        {
            TestResult result = Types.InAssembly(Load(m, "Contracts"))
                .ShouldNot().HaveDependencyOnAny("Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Npgsql", "MongoDB.Driver", "StackExchange.Redis", "Microsoft.IdentityModel")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{m}.Contracts is the public surface other modules compile against");
        }
    }

    [Fact]
    public void AllModuleAssemblies_AreLoadable_SoRulesAreNotVacuouslySkipped()
    {
        foreach (string m in _modules)
            foreach (string layer in new[] { "Domain", "Application", "Infrastructure", "Contracts" })
            {
                Types.InAssembly(Load(m, layer)).GetTypes().Should().NotBeEmpty($"{m}.{layer} must contain types");
            }
    }

    [Fact]
    public void Modules_OnlyUseOtherModulesContracts()
    {
        foreach (string m in _modules)
            foreach (string layer in new[] { "Domain", "Application", "Infrastructure" })
            {
                string[] others = _modules.Where(o => o != m).SelectMany(o => new[]
                {
                    $"SakerWatch.{o}.Domain", $"SakerWatch.{o}.Application", $"SakerWatch.{o}.Infrastructure",
                }).ToArray();
                TestResult result = Types.InAssembly(Load(m, layer)).ShouldNot().HaveDependencyOnAny(others).GetResult();
                result.IsSuccessful.Should().BeTrue($"{m}.{layer} may only use other modules' Contracts");
            }
    }

    [Fact]
    public void ModuleApplication_TalksToStorageAndTheWebOnlyThroughPorts()
    {
        foreach (string m in _modules)
        {
            TestResult result = Types.InAssembly(Load(m, "Application")).ShouldNot().HaveDependencyOnAny(
                "Npgsql", "Microsoft.EntityFrameworkCore", "MongoDB.Driver", "MongoDB.Bson", "StackExchange.Redis", "Microsoft.AspNetCore")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{m}.Application depends on ports its Infrastructure implements, never on a driver or the web stack");
        }
    }

    [Fact]
    public void ModuleApplication_DoesNotReachIntoTheInfrastructureBuildingBlocks()
    {
        foreach (string m in _modules)
        {
            TestResult result = Types.InAssembly(Load(m, "Application")).ShouldNot().HaveDependencyOnAny(
                "SakerWatch.BuildingBlocks.Persistence", "SakerWatch.BuildingBlocks.Mongo", "SakerWatch.BuildingBlocks.Redis",
                "SakerWatch.BuildingBlocks.Web", "SakerWatch.BuildingBlocks.Resilience")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{m}.Application may use only BuildingBlocks.Kernel and BuildingBlocks.Application");
        }
    }

    [Fact]
    public void ModuleContracts_AreDtosAndPorts_NotImplementations()
    {
        foreach (string m in _modules)
        {
            Types.InAssembly(Load(m, "Contracts")).That().AreClasses().And().AreNotAbstract().And().AreNotSealed().And().DoNotHaveName("AssemblyMarker")
                .GetTypes().Should().BeEmpty($"{m}.Contracts exposes sealed records/DTOs and interfaces, so another module cannot inherit its behavior");
        }
    }
}
