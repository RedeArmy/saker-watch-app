using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SakerWatch.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Computes the request hash of ADR-0017: SHA-256 of the canonical form of method, route and request body. The body is
/// the request serialized to JSON with the idempotency key removed and every object's properties sorted, so two
/// requests that differ only in property order or whitespace hash the same.
/// </summary>
public static class RequestHasher
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveIdempotencyKey } },
    };

    /// <summary>Hash of <paramref name="request"/> sent to <paramref name="key"/>'s method and route.</summary>
    public static byte[] Hash(object request, IdempotencyKeyInfo key)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(key);

        JsonNode? body = JsonSerializer.SerializeToNode(request, request.GetType(), _options);
        var canonical = new JsonObject
        {
            ["method"] = key.Method,
            ["route"] = key.Route,
            ["body"] = Canonicalize(body),
        };
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString()));
    }

    /// <summary>Canonical JSON text of a document (sorted properties, no insignificant whitespace); exposed for property tests.</summary>
    public static string Canonical(JsonNode? node) => Canonicalize(node)?.ToJsonString() ?? "null";

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Canonicalize(pair.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Canonicalize)]),
        null => null,
        _ => node.DeepClone(),
    };

    private static void RemoveIdempotencyKey(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (JsonPropertyInfo property in info.Properties.Where(p => p.PropertyType == typeof(IdempotencyKeyInfo)).ToList())
        {
            info.Properties.Remove(property);
        }
    }
}
