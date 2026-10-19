using System.Reflection;
using AwesomeAssertions;
using NetArchTest.Rules;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Mongo;
using SakerWatch.BuildingBlocks.Observability;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Redis;
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
    public void MoneyTypes_AreOnlyBuiltThroughTheirValidatingFactories()
    {
        Type[] validated = [typeof(Money), typeof(Shares), typeof(ExchangeRate), typeof(RelativeChange), typeof(CurrencyCode)];

        foreach (Type type in validated)
        {
            type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty($"{type.Name} is built by a factory that checks it (ADR-0037)");
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Should().OnlyContain(property => property.SetMethod == null, $"{type.Name} is immutable");
        }
    }

    [Fact]
    public void RoundedMoneyTypes_CarryNoArithmetic()
    {
        foreach (Type type in new[] { typeof(DisplayMoney), typeof(DisplayShares) })
        {
            type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name.StartsWith("op_", StringComparison.Ordinal) && method.Name is not ("op_Equality" or "op_Inequality"))
                .Should().BeEmpty($"{type.Name} is for showing, and reusing a rounded figure in a calculation is how cents are lost (ADR-0037)");
        }
    }

    [Fact]
    public void BuildingBlocks_DoNotDependOnModules()
    {
        string[] modules = [.. _modules.Select(m => $"SakerWatch.{m}")];

        foreach (Assembly buildingBlock in new[] { typeof(ISender).Assembly, typeof(WebSetup).Assembly, typeof(HttpClientResilienceOptions).Assembly, typeof(ObservabilitySetup).Assembly, typeof(PostgresOptions).Assembly, typeof(MongoOptions).Assembly, typeof(RedisOptions).Assembly })
        {
            TestResult result = Types.InAssembly(buildingBlock).ShouldNot().HaveDependencyOnAny(modules).GetResult();

            result.IsSuccessful.Should().BeTrue($"{buildingBlock.GetName().Name} must know nothing about modules");
        }
    }

    [Fact]
    public void BuildingBlocksApplication_DoesNotDependOnWebOrPersistenceFrameworks()
    {
        TestResult result = Types.InAssembly(typeof(ISender).Assembly).ShouldNot().HaveDependencyOnAny(
            "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Npgsql", "MongoDB.Driver", "SakerWatch.BuildingBlocks.Persistence").GetResult();

        result.IsSuccessful.Should().BeTrue("the application pipeline is transport- and storage-agnostic");
    }

    [Fact]
    public void ModuleDomains_OnlyDependOnTheKernelBuildingBlock()
    {
        foreach (string module in _modules)
        {
            var domain = Assembly.Load($"SakerWatch.{module}.Domain");

            TestResult result = Types.InAssembly(domain).ShouldNot().HaveDependencyOnAny(
                "SakerWatch.BuildingBlocks.Application", "SakerWatch.BuildingBlocks.Web", "SakerWatch.BuildingBlocks.Observability", "SakerWatch.BuildingBlocks.Persistence")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{module}.Domain may use only BuildingBlocks.Kernel");
        }
    }

    [Fact]
    public void DomainEntities_HaveNoPublicSetters_AndValueObjectsAreSealed()
    {
        foreach (string module in _modules)
        {
            Type[] types = Assembly.Load($"SakerWatch.{module}.Domain").GetTypes();

            string[] settable = [.. types
                .Where(t => t.IsPublic && IsSubclassOfOpenGeneric(t, typeof(Entity<>)))
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(p => p.SetMethod is { IsPublic: true })
                .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")];
            settable.Should().BeEmpty($"{module}.Domain entities change only through methods that keep their invariants");

            string[] open = [.. types.Where(t => t.IsPublic && t.IsSubclassOf(typeof(ValueObject)) && !t.IsSealed && !t.IsAbstract).Select(t => t.Name)];
            open.Should().BeEmpty($"{module}.Domain value objects are sealed so equality stays by value");
        }
    }

    private static bool IsSubclassOfOpenGeneric(Type type, Type openGeneric)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openGeneric)
            {
                return true;
            }
        }

        return false;
    }
}
