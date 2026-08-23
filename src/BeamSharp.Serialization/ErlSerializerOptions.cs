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
    private bool _useReflection = true;

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
        _useReflection = source._useReflection;
        Converters = [.. source.Converters];
        ConverterFactories = [.. source.ConverterFactories];
    }

    /// <summary>Converters for specific types, consulted before anything built in.</summary>
    public IList<ErlConverter> Converters { get; private init; } = [];

    /// <summary>Factories for families of types, consulted after <see cref="Converters"/>.</summary>
    public IList<ErlConverterFactory> ConverterFactories { get; private init; } = [];

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

    /// <summary>
    /// Whether plain objects may be handled by reflection. Set this to false under NativeAOT or
    /// trimming: any type without a registered converter then fails loudly at the call site instead
    /// of silently depending on metadata the trimmer may have removed.
    /// </summary>
    public bool UseReflection
    {
        get => _useReflection;
        set => Set(ref _useReflection, value);
    }

    /// <summary>The atom this configuration uses for null.</summary>
    public ErlAtom NullAtom => _nullValue == ErlNullValue.Nil ? ErlAtom.Nil : ErlAtom.Undefined;

    /// <summary>True once the options have been used, after which they cannot change.</summary>
    public bool IsReadOnly => _readOnly;

    /// <summary>Freezes the options. Called automatically the first time they are used.</summary>
    public void MakeReadOnly() => _readOnly = true;

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
        foreach (var converter in Converters)
            if (converter.HandledType == type)
                return converter;

        foreach (var factory in ConverterFactories)
            if (factory.CanConvert(type))
                return factory.CreateConverter(type, this);

        if (AttributeConverterFactory.Instance.CanConvert(type))
            return AttributeConverterFactory.Instance.CreateConverter(type, this);

        if (BuiltInConverters.TryGet(type, out var builtIn))
            return builtIn;

        foreach (var factory in BuiltInConverters.Factories)
            if (factory.CanConvert(type))
                return factory.CreateConverter(type, this);

        if (!_useReflection)
            throw new ErlSerializationException(
                $"no converter is registered for {type} and UseReflection is off. Register a " +
                $"converter for it, or turn reflection back on if you are not targeting AOT.");

        return ObjectConverterFactory.Instance.CreateConverter(type, this);
    }
}
