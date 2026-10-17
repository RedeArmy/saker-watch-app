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
    public async Task ZeroRetries_IsAllowed_AndMeansASingleAttempt()
    {
        var primary = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary, o => o.MaxRetries = 0);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        HttpResponseMessage response = await client.GetAsync("quote");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        primary.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Get_TooManyRequests_IsRetriedByDefault()
    {
        var primary = new ScriptedHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        HttpResponseMessage response = await client.GetAsync("quote");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        primary.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Get_TooManyRequests_IsNotRetried_WhenTheCallerTracksTheQuota()
    {
        var primary = new ScriptedHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary, o => o.RateLimitIsTransient = false);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        HttpResponseMessage response = await client.GetAsync("quote");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        primary.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ARunOfRateLimitAnswers_DoesNotOpenTheCircuit_WhenTheCallerTracksTheQuota()
    {
        var primary = new ScriptedHandler([.. Enumerable.Repeat(HttpStatusCode.TooManyRequests, 14), HttpStatusCode.OK]);
        using ServiceProvider provider = Build(primary, o =>
        {
            o.RateLimitIsTransient = false;
            o.MaxRetries = 0;
            o.CircuitMinimumThroughput = 5;
        });
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        for (int i = 0; i < 14; i++)
        {
            (await client.GetAsync("quote")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        (await client.GetAsync("quote")).StatusCode.Should().Be(HttpStatusCode.OK, "the circuit stayed closed, so the next call reached the service");
        primary.Calls.Should().Be(15);
    }

    [Fact]
    public async Task ARunOfRateLimitAnswers_OpensTheCircuit_ByDefault()
    {
        var primary = new ScriptedHandler(HttpStatusCode.TooManyRequests);
        using ServiceProvider provider = Build(primary, o =>
        {
            o.MaxRetries = 0;
            o.CircuitMinimumThroughput = 5;
        });
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        Func<Task> act = async () =>
        {
            for (int i = 0; i < 14; i++)
            {
                await client.GetAsync("quote");
            }
        };

        await act.Should().ThrowAsync<Polly.CircuitBreaker.BrokenCircuitException>();
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

    [Theory]
    [InlineData("https://api.provider.test:8443/quote")]
    [InlineData("https://api.provider.test:80/quote")]
    [InlineData("http://api.provider.test:443/quote")]
    public async Task AHostNameAlone_AllowsOnlyTheDefaultPort(string url)
    {
        var primary = new ScriptedHandler(HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary, o => o.AllowInsecureHttp = true);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        Func<Task> act = () => client.GetAsync(url);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*allow-list*");
        primary.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AHostWithAPort_AllowsThatPortOnly()
    {
        var primary = new ScriptedHandler(HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary, o => o.AllowedHosts = ["api.provider.test:8443"]);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        (await client.GetAsync("https://api.provider.test:8443/quote")).StatusCode.Should().Be(HttpStatusCode.OK);
        Func<Task> otherPort = () => client.GetAsync("https://api.provider.test:9443/quote");
        Func<Task> defaultPort = () => client.GetAsync("https://api.provider.test/quote");

        await otherPort.Should().ThrowAsync<HttpRequestException>().WithMessage("*allow-list*");
        await defaultPort.Should().ThrowAsync<HttpRequestException>().WithMessage("*allow-list*");
    }

    [Theory]
    [InlineData("::1", "http://[::1]/quote", true)]
    [InlineData("[::1]", "http://[::1]/quote", true)]
    [InlineData("[::1]:8080", "http://[::1]:8080/quote", true)]
    [InlineData("[::1]:8080", "http://[::1]/quote", false)]
    [InlineData("::1", "http://[::2]/quote", false)]
    public async Task IPv6Literals_AreMatchedWithOrWithoutBrackets(string entry, string url, bool allowed)
    {
        var primary = new ScriptedHandler(HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary, o =>
        {
            o.AllowedHosts = [entry];
            o.AllowInsecureHttp = true;
        });
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(_clientName);

        Func<Task> act = () => client.GetAsync(url);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            primary.Calls.Should().Be(1);
        }
        else
        {
            await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*allow-list*");
        }
    }

    [Fact]
    public async Task ARelativeRequestUri_IsRefusedCleanly()
    {
        var primary = new ScriptedHandler(HttpStatusCode.OK);
        using ServiceProvider provider = Build(primary);
        using var invoker = new HttpMessageInvoker(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(_clientName));

        Func<Task> act = () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri("/quote", UriKind.Relative)), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*not absolute*");
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

    [Fact]
    public void ThePrimaryHandler_FollowsNoRedirectsAndKeepsNoCookies()
    {
        using SocketsHttpHandler handler = SakerHttpClientExtensions.CreatePrimaryHandler();

        handler.AllowAutoRedirect.Should().BeFalse("a redirect would be followed below the host allow-list");
        handler.UseCookies.Should().BeFalse("a cookie set by one call must not travel with the next");
        handler.PooledConnectionLifetime.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }
}
