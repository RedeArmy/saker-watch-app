using System.Reflection;
using System.Text.Json;
using SakerWatch.BuildingBlocks.Kernel.Domain;

namespace SakerWatch.BuildingBlocks.Application.Events;

/// <summary>Maps stable event names to CLR types and serializes messages for the outbox.</summary>
public sealed class IntegrationEventRegistry
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Type> _byName;
    private readonly Dictionary<Type, string> _byType;

    /// <summary>Builds the registry from the <paramref name="types"/> that carry <see cref="IntegrationEventNameAttribute"/>.</summary>
    /// <exception cref="InvalidOperationException">Two types share a name, or an attributed type is not an <see cref="IDomainEvent"/>.</exception>
    public IntegrationEventRegistry(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        _byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        _byType = [];
        foreach (Type type in types)
        {
            if (type.GetCustomAttribute<IntegrationEventNameAttribute>() is not { } attribute)
            {
                continue;
            }

            if (!typeof(IDomainEvent).IsAssignableFrom(type))
            {
                throw new InvalidOperationException($"{type.FullName} has an integration event name but is not an IDomainEvent.");
            }

            if (!_byName.TryAdd(attribute.Name, type))
            {
                throw new InvalidOperationException($"The integration event name '{attribute.Name}' is used by both {_byName[attribute.Name].FullName} and {type.FullName}.");
            }

            _byType[type] = attribute.Name;
        }
    }

    /// <summary>Builds the registry from every type of the given assemblies.</summary>
    public static IntegrationEventRegistry FromAssemblies(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return new IntegrationEventRegistry(assemblies.SelectMany(a => a.GetTypes()));
    }

    /// <summary>The stable name of <paramref name="message"/>'s type.</summary>
    /// <exception cref="InvalidOperationException">The type has no registered name.</exception>
    public string NameOf(IDomainEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _byType.TryGetValue(message.GetType(), out string? name)
            ? name
            : throw new InvalidOperationException($"{message.GetType().FullName} is not a registered integration event (missing [IntegrationEventName] or assembly not scanned).");
    }

    /// <summary>JSON payload of <paramref name="message"/>.</summary>
    public static string Serialize(IDomainEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, message.GetType(), _options);
    }

    /// <summary>Rebuilds a stored message.</summary>
    /// <exception cref="InvalidOperationException">The name is unknown or the payload does not fit the type.</exception>
    public IDomainEvent Deserialize(string name, string payload)
    {
        if (!_byName.TryGetValue(name, out Type? type))
        {
            throw new InvalidOperationException($"Unknown integration event '{name}'.");
        }

        return (IDomainEvent?)JsonSerializer.Deserialize(payload, type, _options)
            ?? throw new InvalidOperationException($"The payload of '{name}' is empty.");
    }
}
