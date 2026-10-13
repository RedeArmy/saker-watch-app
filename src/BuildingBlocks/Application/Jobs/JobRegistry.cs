using System.Reflection;
using System.Text.Json;

namespace SakerWatch.BuildingBlocks.Application.Jobs;

/// <summary>Maps stable job type names to payload types and (de)serializes payloads for the queue.</summary>
public sealed class JobRegistry
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    /// <summary>Builds the registry from the <paramref name="types"/> that carry <see cref="JobTypeAttribute"/>.</summary>
    /// <exception cref="InvalidOperationException">Two payload types share a name.</exception>
    public JobRegistry(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        foreach (Type type in types)
        {
            if (type.GetCustomAttribute<JobTypeAttribute>() is not { } attribute)
            {
                continue;
            }

            if (!_byName.TryAdd(attribute.Name, type))
            {
                throw new InvalidOperationException($"The job type name '{attribute.Name}' is used by both {_byName[attribute.Name].FullName} and {type.FullName}.");
            }

            _byType[type] = attribute.Name;
        }
    }

    /// <summary>Builds the registry from every type of the given assemblies.</summary>
    public static JobRegistry FromAssemblies(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return new JobRegistry(assemblies.SelectMany(a => a.GetTypes()));
    }

    /// <summary>The stable name of the payload type.</summary>
    /// <exception cref="InvalidOperationException">The type has no registered name.</exception>
    public string NameOf(Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        return _byType.TryGetValue(payloadType, out string? name)
            ? name
            : throw new InvalidOperationException($"{payloadType.FullName} is not a registered job payload (missing [JobType] or assembly not scanned).");
    }

    /// <summary>JSON of <paramref name="payload"/>.</summary>
    public static string Serialize(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, payload.GetType(), _options);
    }

    /// <summary>Rebuilds a stored payload.</summary>
    /// <exception cref="InvalidOperationException">The type name is unknown or the payload is empty.</exception>
    public object Deserialize(string name, string payload)
    {
        if (!_byName.TryGetValue(name, out Type? type))
        {
            throw new InvalidOperationException($"Unknown job type '{name}'.");
        }

        return JsonSerializer.Deserialize(payload, type, _options)
            ?? throw new InvalidOperationException($"The payload of job type '{name}' is empty.");
    }
}
