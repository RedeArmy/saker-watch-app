using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Resilience;

namespace SakerWatch.Web.Tests;

public sealed class ResilienceTests
{
    private const string _clientName = "provider";

    private sealed class ScriptedHandler(params HttpStatusCode[] script) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(new HttpResponseMessage(script[Math.Min(index, script.Length - 1)]));
        }
    }

    private static ServiceProvider Build(ScriptedHandler primary, Action<HttpClientResilienceOptions>? tweak = null)
    {
        var services = new ServiceCollection();
        services.AddSakerHttpClient(_clientName, new Uri("https://api.provider.test/"), options =>
        {
            options.AllowedHosts = ["api.provider.test"];
            options.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            options.MaxRetries = 2;
            tweak?.Invoke(options);
        }).ConfigurePrimaryHttpMessageHandler(() => primary);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Get_TransientServerErrors_AreRetriedUntilSuccess()
    {
        var primary = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        HttpResponseMessage response = await client.GetAsync("quote");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        primary.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Post_TransientServerError_IsNotRetried()
    {
        var primary = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        HttpResponseMessage response = await client.PostAsync("order", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        primary.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Get_ClientErrors_AreNotRetried()
    {
        var primary = new ScriptedHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        (await client.GetAsync("missing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        primary.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("https://api.provider.test.evil.example/")]
    public async Task Request_ToHostOutsideAllowList_IsRefusedWithoutAnyNetworkCall(string url)
    {
        var primary = new ScriptedHandler(HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        Func<Task> act = async () => await client.GetAsync(url);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*allow-list*");
        primary.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Request_OverPlainHttp_IsRefusedUnlessExplicitlyAllowed()
    {
        var primary = new ScriptedHandler(HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        Func<Task> act = async () => await client.GetAsync("http://api.provider.test/quote");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*scheme*");
        primary.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Request_OverPlainHttp_WorksWhenAllowedForLocalFakes()
    {
        var primary = new ScriptedHandler(HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary, o => o.AllowInsecureHttp = true);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        (await client.GetAsync("http://api.provider.test/quote")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Client_AppliesBaseAddressAndResponseSizeLimit()
    {
        using ServiceProvider provider = Build(new ScriptedHandler(HttpStatusCode.OK), o => o.MaxResponseBytes = 1234);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        client.BaseAddress.Should().Be(new Uri("https://api.provider.test/"));
        client.MaxResponseContentBufferSize.Should().Be(1234);
    }

    [Fact]
    public void Options_WithoutAllowedHosts_FailValidationOnResolve()
    {
        var services = new ServiceCollection();
        services.AddSakerHttpClient(_clientName, new Uri("https://api.provider.test/"), _ => { });
        using ServiceProvider provider = services.BuildServiceProvider();

        Func<HttpClient> act = () => provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>();
    }
}
