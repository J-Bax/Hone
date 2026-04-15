using System.Collections;
using System.Reflection;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.ObjectFactories;

namespace Hone.Core.Config;

/// <summary>
/// Object factory that supports C# records with all-default constructor parameters.
/// YamlDotNet's <see cref="DefaultObjectFactory"/> requires a parameterless constructor,
/// but positional records with default parameter values don't generate one.
/// </summary>
public sealed class RecordAwareObjectFactory : IObjectFactory
{
    private readonly DefaultObjectFactory _inner = new();

    public object Create(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Try constructor with all-default parameters (positional records).
        // Use the constructor with the most parameters to pick the primary
        // record constructor reliably across runtimes.
        ConstructorInfo[] ctors = type.GetConstructors();
        if (ctors.Length > 0)
        {
            ConstructorInfo ctor = ctors.OrderByDescending(c => c.GetParameters().Length).First();
            ParameterInfo[] parameters = ctor.GetParameters();
            if (parameters.Length > 0 && Array.TrueForAll(parameters, static p => p.HasDefaultValue))
            {
                object?[] args = Array.ConvertAll(parameters, static p => p.DefaultValue);
                return ctor.Invoke(args);
            }
        }

        return _inner.Create(type);
    }

    public object? CreatePrimitive(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _inner.CreatePrimitive(type);
    }

    public bool GetDictionary(IObjectDescriptor descriptor, out IDictionary? dictionary, out Type[]? genericArguments)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return _inner.GetDictionary(descriptor, out dictionary, out genericArguments);
    }

    public Type GetValueType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _inner.GetValueType(type);
    }

    public void ExecuteOnDeserializing(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _inner.ExecuteOnDeserializing(value);
    }

    public void ExecuteOnDeserialized(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _inner.ExecuteOnDeserialized(value);
    }

    public void ExecuteOnSerializing(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _inner.ExecuteOnSerializing(value);
    }

    public void ExecuteOnSerialized(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _inner.ExecuteOnSerialized(value);
    }
}
