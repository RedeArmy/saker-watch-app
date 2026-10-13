using System.Reflection;
using AwesomeAssertions;
using NetArchTest.Rules;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Resilience;
using SakerWatch.BuildingBlocks.Web;

namespace SakerWatch.Architecture.Tests;

public sealed class BuildingBlockRulesTests
{
    private static readonly string[] _modules =
        ["Identity", "MarketData", "Watchlist", "Analysis", "Signals", "Calculator", "Retention"];

    [Fact]
    public void Kernel_IsFrameworkFree()
    {
        TestResult result = Types.InAssembly(typeof(Result).Assembly)
            .ShouldNot().HaveDependencyOnAny(
                "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions", "Serilog", "MongoDB.Driver", "Npgsql", "FluentValidation", "SakerWatch.Identity", "SakerWatch.MarketData")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("the Kernel is pure and shared by every Domain");
    }

    [Fact]
    public void BuildingBlocks_DoNotDependOnModules()
    {
        string[] modules = [.. _modules.Select(m => $"SakerWatch.{m}")];

        foreach (Assembly buildingBlock in new[] { typeof(ISender).Assembly, typeof(WebSetup).Assembly, typeof(HttpClientResilienceOptions).Assembly })
        {
            TestResult result = Types.InAssembly(buildingBlock).ShouldNot().HaveDependencyOnAny(modules).GetResult();

            result.IsSuccessful.Should().BeTrue($"{buildingBlock.GetName().Name} must know nothing about modules");
        }
    }

    [Fact]
    public void BuildingBlocksApplication_DoesNotDependOnWebOrPersistenceFrameworks()
    {
        TestResult result = Types.InAssembly(typeof(ISender).Assembly).ShouldNot().HaveDependencyOnAny(
            "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Npgsql", "MongoDB.Driver").GetResult();

        result.IsSuccessful.Should().BeTrue("the application pipeline is transport- and storage-agnostic");
    }

    [Fact]
    public void ModuleDomains_OnlyDependOnTheKernelBuildingBlock()
    {
        foreach (string module in _modules)
        {
            var domain = Assembly.Load($"SakerWatch.{module}.Domain");

            TestResult result = Types.InAssembly(domain).ShouldNot().HaveDependencyOnAny(
                "SakerWatch.BuildingBlocks.Application", "SakerWatch.BuildingBlocks.Web", "SakerWatch.BuildingBlocks.Observability")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{module}.Domain may use only BuildingBlocks.Kernel");
        }
    }
}
