using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace SakerWatch.TestSupport;

/// <summary>What a minted token should look like. Every property defaults to a valid token; tests change one to break it.</summary>
public sealed record TokenSpec
{
    /// <summary>The <c>sub</c> claim; <see langword="null"/> omits it.</summary>
    public string? Subject { get; init; } = "account-123";

    /// <summary>The <c>email</c> claim.</summary>
    public string? Email { get; init; } = "user@example.com";

    /// <summary>The <c>name</c> claim.</summary>
    public string? Name { get; init; } = "Test User";

    /// <summary>The <c>locale</c> claim.</summary>
    public string? Locale { get; init; } = "es-GT";

    /// <summary>The <c>scope</c> claim.</summary>
    public string? Scope { get; init; } = "openid profile email";

    /// <summary>Adds an <c>act</c> (delegation) claim, as an operator impersonation token carries.</summary>
    public bool Delegated { get; init; }

    /// <summary>Adds the ID-token-only <c>nonce</c> claim.</summary>
    public bool AsIdToken { get; init; }

    /// <summary>True emits <c>scope</c> as a JSON array, as Clavaris does; false as an RFC 9068 space-delimited string.</summary>
    public bool ScopeAsArray { get; init; } = true;

    /// <summary>The <c>aud</c> claim; <see langword="null"/> uses the provider's audience, an empty string omits it.</summary>
    public string? Audience { get; init; }

    /// <summary>The <c>iss</c> claim; <see langword="null"/> uses the provider's issuer.</summary>
    public string? Issuer { get; init; }

    /// <summary>Lifetime counted from <c>iat</c>; negative values give an already expired token. <see langword="null"/> omits <c>exp</c>.</summary>
    public TimeSpan? ExpiresIn { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Offset of <c>nbf</c> from now; <see langword="null"/> omits it.</summary>
    public TimeSpan? NotBefore { get; init; }

    /// <summary>The <c>alg</c> header: <c>RS256</c> (default), <c>RS384</c>, <c>none</c> or <c>HS256</c> (signed with the public key as the secret).</summary>
    public string Algorithm { get; init; } = "RS256";

    /// <summary>The <c>kid</c> header; <see langword="null"/> uses the provider's current key.</summary>
    public string? KeyId { get; init; }

    /// <summary>Sign with an unrelated key instead of the provider's.</summary>
    public bool SignWithForeignKey { get; init; }

    /// <summary>Change the payload after signing, so the signature no longer matches.</summary>
    public bool TamperAfterSigning { get; init; }
}

/// <summary>
/// An in-process stand-in for Clavaris (docs/clavaris-contract.md): the per-Organization issuer path
/// <c>/o/{organizationId}</c>, OIDC discovery, an RS256 JWKS that can rotate, and a token minter that can also produce
/// every malformed or hostile token the validator must refuse. It runs on a <see cref="TestServer"/>, so no port is opened.
/// </summary>
public sealed class FakeIdentityProvider : IAsyncDisposable
{
    private static readonly string[] _codeFlow = ["code"];
    private static readonly string[] _publicSubjects = ["public"];
    private static readonly string[] _signingAlgorithms = ["RS256"];

    private readonly IHost _host;
    private readonly TestServer _server;
    private readonly TimeProvider _clock;
    private readonly List<(string Kid, RSA Key)> _keys = [];
    private readonly Lock _gate = new();
    private int _discoveryRequests;
    private int _jwksRequests;
    private int _stalledRequests;

    private FakeIdentityProvider(string organization, TimeProvider clock)
    {
        Organization = organization;
        _clock = clock;
        AddKey();
        _host = new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app => app.Run(HandleAsync)))
            .Build();
        _server = _host.GetTestServer();
    }

    /// <summary>The organization id used in the issuer path.</summary>
    public string Organization { get; }

    /// <summary>The issuer URL tokens carry: <c>http://fake-idp.test/o/{organization}</c>.</summary>
    public string Issuer => $"{Uri.UriSchemeHttp}://fake-idp.test/o/{Organization}";

    /// <summary>The audience tokens carry by default.</summary>
    public static string Audience => "saker-watch-api";

    /// <summary>When true, discovery and JWKS answer 503 (the provider is down).</summary>
    public bool Unavailable { get; set; }

    /// <summary>When set, discovery and key requests wait for this task before answering (a slow provider); cancel the caller's token to abandon them.</summary>
    public Task? Stall { get; set; }

    /// <summary>How many requests have reached <see cref="Stall"/> so far.</summary>
    public int StalledRequests => Volatile.Read(ref _stalledRequests);

    /// <summary>Completes once <paramref name="count"/> requests are waiting on <see cref="Stall"/>; fails after five real seconds.</summary>
    public async Task WaitForStalledRequestsAsync(int count = 1)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (StalledRequests < count)
        {
            await Task.Yield();
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>When set, the discovery document claims this issuer instead of the real one (a misdirected or hostile document).</summary>
    public string? DiscoveryIssuerOverride { get; set; }

    /// <summary>Number of discovery documents served.</summary>
    public int DiscoveryRequests => Volatile.Read(ref _discoveryRequests);

    /// <summary>Number of JWKS documents served.</summary>
    public int JwksRequests => Volatile.Read(ref _jwksRequests);

    /// <summary>The kid of the key new tokens are signed with.</summary>
    public string CurrentKeyId
    {
        get
        {
            lock (_gate)
            {
                return _keys[^1].Kid;
            }
        }
    }

    /// <summary>A handler that reaches this provider; use it as the primary handler of the metadata HTTP client.</summary>
    public HttpMessageHandler CreateHandler() => _server.CreateHandler();

    /// <summary>Starts a provider.</summary>
    public static async Task<FakeIdentityProvider> StartAsync(string organization = "org-1", TimeProvider? clock = null)
    {
        var provider = new FakeIdentityProvider(organization, clock ?? TimeProvider.System);
        await provider._host.StartAsync();
        return provider;
    }

    /// <summary>Publishes a new signing key and makes it current. The previous keys stay published until retired.</summary>
    public string AddKey()
    {
        var rsa = RSA.Create(2048);
        string kid = $"key-{Guid.CreateVersion7():N}"[..16];
        lock (_gate)
        {
            _keys.Add((kid, rsa));
        }

        return kid;
    }

    /// <summary>Stops publishing a key, as after a rotation grace period.</summary>
    public void RetireKey(string kid)
    {
        lock (_gate)
        {
            _keys.RemoveAll(k => k.Kid == kid);
        }
    }

    /// <summary>Mints a compact JWT described by <paramref name="spec"/>.</summary>
    public string Mint(TokenSpec? spec = null)
    {
        TokenSpec s = spec ?? new TokenSpec();
        DateTimeOffset now = _clock.GetUtcNow();
        (string kid, RSA key) = SigningKey(s);

        var header = new Dictionary<string, object> { ["alg"] = s.Algorithm, ["typ"] = "JWT" };
        if (s.Algorithm != "none")
        {
            header["kid"] = s.KeyId ?? kid;
        }

        var payload = new Dictionary<string, object>
        {
            ["iss"] = s.Issuer ?? Issuer,
            ["iat"] = now.ToUnixTimeSeconds(),
        };
        if (s.Scope is not null)
        {
            payload["scope"] = s.ScopeAsArray ? s.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries) : s.Scope;
        }

        if (s.Delegated)
        {
            payload["act"] = new Dictionary<string, object> { ["sub"] = "platform-operator" };
        }

        if (s.AsIdToken)
        {
            payload["nonce"] = "n1";
        }

        if (s.Subject is not null)
        {
            payload["sub"] = s.Subject;
        }

        if (s.Audience != string.Empty)
        {
            payload["aud"] = new[] { s.Audience ?? Audience };
        }

        if (s.ExpiresIn is { } lifetime)
        {
            payload["exp"] = (now + lifetime).ToUnixTimeSeconds();
        }

        if (s.NotBefore is { } offset)
        {
            payload["nbf"] = (now + offset).ToUnixTimeSeconds();
        }

        foreach ((string name, string? value) in new[] { ("email", s.Email), ("name", s.Name), ("locale", s.Locale) })
        {
            if (value is not null)
            {
                payload[name] = value;
            }
        }

        string signingInput = $"{Encode(header)}.{Encode(payload)}";
        string token = $"{signingInput}.{Base64Url(Sign(s.Algorithm, signingInput, key))}";
        return s.TamperAfterSigning ? Tamper(token) : token;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        lock (_gate)
        {
            foreach ((string _, RSA key) in _keys)
            {
                key.Dispose();
            }
        }
    }

    private (string Kid, RSA Key) SigningKey(TokenSpec spec)
    {
        if (spec.SignWithForeignKey)
        {
            return (CurrentKeyId, RSA.Create(2048));
        }

        lock (_gate)
        {
            return spec.KeyId is { } wanted && _keys.Find(k => k.Kid == wanted) is { Key: not null } match ? match : _keys[^1];
        }
    }

    // RS256 and RS384 are defined with PKCS#1 v1.5 signature padding; the fake must produce what the real provider does.
    [SuppressMessage("Security", "S5542:Encryption algorithms should be used with secure mode and padding scheme", Justification = "RS256/RS384 mandate PKCS#1 v1.5 signatures (RFC 7518).")]
    private static byte[] Sign(string algorithm, string signingInput, RSA key)
    {
        byte[] data = Encoding.ASCII.GetBytes(signingInput);
        return algorithm switch
        {
            "none" => [],
            // The classic confusion attack: an HMAC keyed with the (public) RSA key material.
            "HS256" => HMACSHA256.HashData(key.ExportSubjectPublicKeyInfo(), data),
            "RS384" => key.SignData(data, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1),
            _ => key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        };
    }

    private static string Tamper(string token)
    {
        string[] parts = token.Split('.');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(Pad(parts[1])));
        var payload = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        payload["sub"] = "someone-else";
        return $"{parts[0]}.{Encode(payload)}.{parts[2]}";
    }

    private static string Encode(object value) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(value));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Pad(string base64Url)
    {
        string s = base64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
    }

    private async Task HandleAsync(HttpContext context)
    {
        string prefix = $"/o/{Organization}";
        if (Unavailable)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (Stall is { } stall)
        {
            Interlocked.Increment(ref _stalledRequests);
            await stall.WaitAsync(context.RequestAborted);
        }

        if (context.Request.Path == $"{prefix}/.well-known/openid-configuration")
        {
            Interlocked.Increment(ref _discoveryRequests);
            await WriteJsonAsync(context, new Dictionary<string, object>
            {
                ["issuer"] = DiscoveryIssuerOverride ?? Issuer,
                ["jwks_uri"] = $"{Issuer}/oauth2/jwks",
                ["authorization_endpoint"] = $"{Issuer}/oauth2/authorize",
                ["token_endpoint"] = $"{Issuer}/oauth2/token",
                ["response_types_supported"] = _codeFlow,
                ["subject_types_supported"] = _publicSubjects,
                ["id_token_signing_alg_values_supported"] = _signingAlgorithms,
            });
            return;
        }

        if (context.Request.Path == $"{prefix}/oauth2/jwks")
        {
            Interlocked.Increment(ref _jwksRequests);
            object[] keys;
            lock (_gate)
            {
                keys = [.. _keys.Select(k => (object)Jwk(k.Kid, k.Key))];
            }

            await WriteJsonAsync(context, new Dictionary<string, object> { ["keys"] = keys });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static Dictionary<string, object> Jwk(string kid, RSA key)
    {
        RSAParameters p = key.ExportParameters(includePrivateParameters: false);
        return new Dictionary<string, object>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = kid,
            ["n"] = Base64Url(p.Modulus!),
            ["e"] = Base64Url(p.Exponent!),
        };
    }

    private static Task WriteJsonAsync(HttpContext context, object body)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body), context.RequestAborted);
    }
}
