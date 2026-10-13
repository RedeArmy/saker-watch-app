namespace SakerWatch.BuildingBlocks.Resilience;

/// <summary>
/// Refuses any request whose host is not on the allow-list (or that is plain HTTP when not permitted), so a
/// caller-controlled URL can never turn the server into a proxy (SSRF). It sits outside the resilience pipeline,
/// so a refusal is never retried.
/// </summary>
/// <param name="allowedHosts">Exact host names that may be called.</param>
/// <param name="allowInsecureHttp">Whether plain <c>http</c> is permitted.</param>
internal sealed class AllowedHostsHandler(IEnumerable<string> allowedHosts, bool allowInsecureHttp) : DelegatingHandler
{
    private readonly HashSet<string> _allowedHosts = new(allowedHosts, StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("Outbound request has no absolute URI.");

        if (!uri.IsAbsoluteUri || !_allowedHosts.Contains(uri.Host))
        {
            throw new HttpRequestException($"Outbound host '{uri.Host}' is not on the allow-list.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            throw new HttpRequestException($"Outbound scheme '{uri.Scheme}' is not permitted.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
