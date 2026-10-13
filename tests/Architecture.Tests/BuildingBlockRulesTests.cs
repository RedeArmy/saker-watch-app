using System.Reflection;
using AwesomeAssertions;
using NetArchTest.Rules;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;

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
    public void BuildingBlocksApplication_DoesNotDependOnModulesOrWebFrameworks()
    {
        string[] forbidden =
        [
            .. _modules.Select(m => $"SakerWatch.{m}"),
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "MongoDB.Driver",
        ];

        TestResult result = Types.InAssembly(typeof(ISender).Assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        result.IsSuccessful.Should().BeTrue("building blocks know nothing about modules or infrastructure");
    }

    [Fact]
    public void ModuleDomains_OnlyDependOnTheKernelBuildingBlock()
    {
        foreach (string module in _modules)
        {
            Assembly domain = Assembly.Load($"SakerWatch.{module}.Domain");

            TestResult result = Types.InAssembly(domain).ShouldNot().HaveDependencyOnAny(
                "SakerWatch.BuildingBlocks.Application", "SakerWatch.BuildingBlocks.Web", "SakerWatch.BuildingBlocks.Observability")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{module}.Domain may use only BuildingBlocks.Kernel");
        }
    }
}
