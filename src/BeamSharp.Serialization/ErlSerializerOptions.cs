using System.Collections.Concurrent;
using BeamSharp.Serialization.Converters;
using BeamSharp.Terms;

namespace BeamSharp.Serialization;

/// <summary>How the keys of a serialized object are represented.</summary>
public enum ErlMapKeyKind
{
    /// <summary><c>%{name: "x"}</c> — the idiomatic shape for a known, fixed set of fields.</summary>
    Atom,

    /// <summary><c>%{"name" =&gt; "x"}</c> — safer for keys that come from untrusted input.</summary>
    Binary
}

/// <summary>Which atom stands in for <c>null</c>.</summary>
public enum ErlNullValue
{
    /// <summary><c>nil</c>, what Elixir uses.</summary>
    Nil,

    /// <summary><c>undefined</c>, what Erlang uses.</summary>
    Undefined
}

/// <summary>Controls how objects map onto terms.</summary>
public sealed class ErlSerializerOptions
{
    private readonly ConcurrentDictionary<Type, ErlConverter> _cache = new();
    private bool _readOnly;

    private ErlNamingPolicy _propertyNamingPolicy = ErlNamingPolicy.SnakeCase;
    private ErlNamingPolicy _enumNamingPolicy = ErlNamingPolicy.SnakeCase;
    private ErlMapKeyKind _mapKeyKind = ErlMapKeyKind.Atom;
    private ErlNullValue _nullValue = ErlNullValue.Nil;
    private bool _ignoreNullValues;
    private bool _includeFields;

    /// <summary>The default options: snake_case names, atom keys, <c>nil</c> for null.</summary>
    public static ErlSerializerOptions Default { get; } = CreateDefault();

    private static ErlSerializerOptions CreateDefault()
    {
        var options = new ErlSerializerOptions();
        options.MakeReadOnly();
        return options;
    }

    public ErlSerializerOptions() { }

    /// <summary>Copies another set of options, producing a mutable instance.</summary>
    public ErlSerializerOptions(ErlSerializerOptions source)
    {
        _propertyNamingPolicy = source._propertyNamingPolicy;
        _enumNamingPolicy = source._enumNamingPolicy;
        _mapKeyKind = source._mapKeyKind;
        _nullValue = source._nullValue;
        _ignoreNullValues = source._ignoreNullValues;
        _includeFields = source._includeFields;
        Converters = new FreezableList<ErlConverter>(source.Converters);
        ConverterFactories = new FreezableList<ErlConverterFactory>(source.ConverterFactories);
    }

    /// <summary>Converters for specific types, consulted before anything built in.</summary>
    public IList<ErlConverter> Converters { get; private init; } = new FreezableList<ErlConverter>();

    /// <summary>Factories for families of types, consulted after <see cref="Converters"/>.</summary>
    public IList<ErlConverterFactory> ConverterFactories { get; private init; } =
        new FreezableList<ErlConverterFactory>();

    /// <summary>How CLR member names become Erlang names. Defaults to snake_case.</summary>
    public ErlNamingPolicy PropertyNamingPolicy
    {
        get => _propertyNamingPolicy;
        set => Set(ref _propertyNamingPolicy, value);
    }

    /// <summary>How enum member names become atoms. Defaults to snake_case.</summary>
    public ErlNamingPolicy EnumNamingPolicy
    {
        get => _enumNamingPolicy;
        set => Set(ref _enumNamingPolicy, value);
    }

    /// <summary>Whether object keys are atoms or binaries. Defaults to atoms.</summary>
    public ErlMapKeyKind MapKeyKind
    {
        get => _mapKeyKind;
        set => Set(ref _mapKeyKind, value);
    }

    /// <summary>The atom written for null. Defaults to <c>nil</c>.</summary>
    public ErlNullValue NullValue
    {
        get => _nullValue;
        set => Set(ref _nullValue, value);
    }

    /// <summary>Omits null members rather than writing them as <c>nil</c>.</summary>
    public bool IgnoreNullValues
    {
        get => _ignoreNullValues;
        set => Set(ref _ignoreNullValues, value);
    }

    /// <summary>Includes public fields as well as public properties.</summary>
    public bool IncludeFields
    {
        get => _includeFields;
        set => Set(ref _includeFields, value);
    }


    /// <summary>The atom this configuration uses for null.</summary>
    public ErlAtom NullAtom => _nullValue == ErlNullValue.Nil ? ErlAtom.Nil : ErlAtom.Undefined;

    /// <summary>True once the options have been used, after which they cannot change.</summary>
    public bool IsReadOnly => _readOnly;

    /// <summary>Freezes the options. Called automatically the first time they are used.</summary>
    public void MakeReadOnly()
    {
        // The collections have to freeze alongside the scalars. Converters is consulted ahead of the
        // built-in scalars, so one added here reaches even int, and _cache already holds whatever
        // was resolved before it -- leaving one type with two mappings depending on when it was
        // first seen, which is worse than either mapping would be on its own.
        _readOnly = true;
        (Converters as FreezableList<ErlConverter>)?.Freeze();
        (ConverterFactories as FreezableList<ErlConverterFactory>)?.Freeze();
    }

    private void Set<T>(ref T field, T value)
    {
        if (_readOnly)
            throw new InvalidOperationException(
                "these options are already in use and can no longer be changed; copy them with " +
                "new ErlSerializerOptions(existing) instead");
        field = value;
    }

    /// <summary>Finds the converter for <typeparamref name="T"/>.</summary>
    public ErlConverter<T> GetConverter<T>() => (ErlConverter<T>)GetConverter(typeof(T));

    /// <summary>Finds the converter for <paramref name="type"/>.</summary>
    public ErlConverter GetConverter(Type type)
    {
        MakeReadOnly();
        return _cache.GetOrAdd(type, static (t, self) => self.CreateConverter(t), this);
    }

    private ErlConverter CreateConverter(Type type)
    {
        // Exact registrations win, then the built-in scalars, then anything a factory claims. The
        // ordering matters because the reflection fallback's factory claims every type, so it has to
        // be reached last.
        foreach (var converter in Converters)
            if (converter.HandledType == type)
                return converter;

        if (BuiltInConverters.TryGet(type, out var builtIn))
            return builtIn;

        foreach (var factory in ConverterFactories)
            if (factory.CanConvert(type))
                return factory.CreateConverter(type, this);

        throw new ErlSerializationException(
            $"no converter is registered for {type}. Either declare it on a generated " +
            $"ErlSerializerContext with [ErlSerializable(typeof({Friendly(type)}))], or reference the " +
            $"BeamSharp.Serialization.Reflection package and call options.AddReflectionFallback() " +
            $"to handle it by reflection.");
    }

    /// <summary>Renders <c>HashSet&lt;int&gt;</c> rather than <c>HashSet`1</c> in diagnostics.</summary>
    private static string Friendly(Type type)
    {
        if (!type.IsGenericType) return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Friendly))}>";
    }
}
