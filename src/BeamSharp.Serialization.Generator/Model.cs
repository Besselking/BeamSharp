namespace BeamSharp.Serialization.Generator;

/// <summary>How a type is laid out as a term.</summary>
internal enum TermShape
{
    /// <summary>A map with atom or binary keys.</summary>
    Map,

    /// <summary>A map carrying <c>__struct__</c>, so Elixir sees a struct.</summary>
    Struct,

    /// <summary>A tagged tuple, matching an Erlang record.</summary>
    Record
}

/// <summary>One serialized member.</summary>
internal readonly record struct MemberModel(
    string ClrName,
    string TypeName,
    // The literal name when [ErlProperty] set one; null means the naming policy applies.
    string? ExplicitName,
    bool IsField,
    bool IsSettable,
    bool IsInitOnly,
    // Whether the member can hold null, which decides if a null check would be dead code.
    bool CanBeNull,
    bool AsAtom,
    string? ConverterTypeName) : IEquatable<MemberModel>;

/// <summary>Everything the emitter needs about one serializable type.</summary>
internal readonly record struct TypeModel(
    string TypeName,
    string SafeName,
    TermShape Shape,
    string? StructModule,
    string? RecordTag,
    EquatableArray<MemberModel> Members,
    // One entry per constructor parameter, indexing into Members; -1 when nothing matched.
    EquatableArray<int> ConstructorBinding,
    // The C# literal for each constructor parameter's default, used when a key is absent.
    EquatableArray<string> ConstructorDefaults,
    bool UsesParameterlessConstructor) : IEquatable<TypeModel>;

/// <summary>A context class and the types declared on it.</summary>
internal readonly record struct ContextModel(
    string? Namespace,
    string ClassName,
    string Accessibility,
    EquatableArray<TypeModel> Types,
    EquatableArray<AuxiliaryModel> Auxiliaries) : IEquatable<ContextModel>;

/// <summary>The families of member type the generator can root an instantiation for.</summary>
internal enum AuxiliaryKind
{
    Enum,
    Nullable,
    Collection,
    Dictionary
}

/// <summary>One enum member and the atom it maps to.</summary>
internal readonly record struct EnumMemberModel(string ClrName, string? ExplicitName) : IEquatable<EnumMemberModel>;

/// <summary>
/// A type reached through a member that needs its own converter instantiation written out. Going
/// through MakeGenericType instead would leave the native code unreachable under AOT.
/// </summary>
internal readonly record struct AuxiliaryModel(
    AuxiliaryKind Kind,
    string TypeName,
    // Element type for a collection, underlying type for a nullable, key type for a dictionary.
    string Arg1,
    string? Arg2,
    EquatableArray<EnumMemberModel> EnumMembers) : IEquatable<AuxiliaryModel>;
