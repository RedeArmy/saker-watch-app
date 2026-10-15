using System.Globalization;

namespace SakerWatch.BuildingBlocks.Resilience;

/// <summary>
/// Refuses any request whose host and port are not on the allow-list (or that is plain HTTP when not permitted), so a
/// caller-controlled URL can never turn the server into a proxy (SSRF). An entry is a host name, which allows only the default
/// port of the request's scheme, or <c>host:port</c> for one specific port. It sits outside the resilience pipeline,
/// so a refusal is never retried.
/// </summary>
/// <param name="allowedHosts">Host names (or <c>host:port</c>) that may be called.</param>
/// <param name="allowInsecureHttp">Whether plain <c>http</c> is permitted.</param>
internal sealed class AllowedHostsHandler(IEnumerable<string> allowedHosts, bool allowInsecureHttp) : DelegatingHandler
{
    // A null port stands for "the default port of the scheme".
    private readonly Dictionary<string, HashSet<int?>> _allowed = Parse(allowedHosts);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("Outbound request has no absolute URI.");

        // A relative URI has no host to read; refuse it before touching one.
        if (!uri.IsAbsoluteUri)
        {
            throw new HttpRequestException("Outbound request URI is not absolute.");
        }

        if (!_allowed.TryGetValue(Bare(uri.Host), out HashSet<int?>? ports) || !ports.Contains(uri.IsDefaultPort ? null : uri.Port))
        {
            throw new HttpRequestException($"Outbound host '{uri.Authority}' is not on the allow-list.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            throw new HttpRequestException($"Outbound scheme '{uri.Scheme}' is not permitted.");
        }

        return base.SendAsync(request, cancellationToken);
    }

    // Uri.Host keeps the brackets of an IPv6 literal, so entries are compared without them.
    private static string Bare(string host) => host.Trim('[', ']');

    private static Dictionary<string, HashSet<int?>> Parse(IEnumerable<string> entries)
    {
        var parsed = new Dictionary<string, HashSet<int?>>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in entries)
        {
            (string host, int? port) = Split(entry);
            if (!parsed.TryGetValue(host, out HashSet<int?>? ports))
            {
                parsed[host] = ports = [];
            }

            ports.Add(port);
        }

        return parsed;
    }

    // "host", "host:port", "[v6]", "[v6]:port" or a bare IPv6 literal such as "::1" (more than one colon, so no port).
    private static (string Host, int? Port) Split(string entry)
    {
        int colon = entry.LastIndexOf(':');
        bool bracketed = entry.StartsWith('[');
        bool singleColon = entry.IndexOf(':', StringComparison.Ordinal) == colon;
        bool portAllowed = colon > 0 && (bracketed ? entry[colon - 1] == ']' : singleColon);
        if (portAllowed && int.TryParse(entry.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            return (Bare(entry[..colon]), port);
        }

        return (Bare(entry), null);
    }
}
