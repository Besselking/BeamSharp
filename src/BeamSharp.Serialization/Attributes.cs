namespace BeamSharp.Serialization;

/// <summary>Overrides the Erlang name of a property or field.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ErlPropertyAttribute(string name) : Attribute
{
    /// <summary>The name to use, verbatim — the naming policy is not applied to it.</summary>
    public string Name { get; } = name;
}

/// <summary>Leaves a property or field out of the serialized term entirely.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ErlIgnoreAttribute : Attribute;

/// <summary>
/// Serializes the type as an Elixir struct: a map carrying <c>__struct__</c>, so it arrives on the
/// other side as a real <c>%MyApp.Person{}</c> rather than a plain map.
/// </summary>
/// <remarks>
/// The module name is the fully qualified Erlang atom Elixir uses internally, so
/// <c>MyApp.Person</c> in Elixir is <c>Elixir.MyApp.Person</c> here. Passing the Elixir spelling
/// works too; the <c>Elixir.</c> prefix is added when it is missing.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ErlStructAttribute(string module) : Attribute
{
    /// <summary>The struct module, normalised to the <c>Elixir.</c>-prefixed atom.</summary>
    public string Module { get; } = module.StartsWith("Elixir.", StringComparison.Ordinal)
        ? module
        : "Elixir." + module;
}

/// <summary>
/// Serializes the type as a tagged tuple — <c>{person, Name, Age}</c> — matching the shape of an
/// Erlang record. Members are written in declaration order and read back by position.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ErlRecordAttribute(string tag) : Attribute
{
    /// <summary>The atom in the first tuple slot.</summary>
    public string Tag { get; } = tag;
}

/// <summary>Uses a specific converter for this type, or for this one member.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum |
                AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ErlConvertAttribute(Type converterType) : Attribute
{
    /// <summary>A type deriving from <see cref="ErlConverter{T}"/> with a parameterless constructor.</summary>
    public Type ConverterType { get; } = converterType;
}

/// <summary>Writes a string member as an atom instead of a binary.</summary>
/// <remarks>
/// Only use this for values drawn from a small fixed set. Atoms are not garbage collected on the
/// BEAM, so a stream of distinct atoms from untrusted input is a way to bring a node down.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ErlAsAtomAttribute : Attribute;
