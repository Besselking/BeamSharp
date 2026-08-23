using System.Reflection;
using BeamSharp.Terms;

using BeamSharp.Serialization.Converters;

namespace BeamSharp.Serialization.Reflection;

/// <summary>
/// The reflection fallback for plain objects: the catch-all consulted when nothing else claims a
/// type. A source generator would replace this by registering generated converters ahead of it,
/// which is why the shape it produces is defined by the attributes rather than by reflection itself.
/// </summary>
public sealed class ObjectConverterFactory : ErlConverterFactory
{
    public static readonly ObjectConverterFactory Instance = new();


    public override bool CanConvert(Type type) => true;

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = Justifications.ReflectionFallback)]
    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options) =>
        ConverterActivator.Create(typeof(ObjectConverter<>).MakeGenericType(type), options);
}

/// <summary>Writes an object as an Elixir map, an Elixir struct, or a tagged tuple.</summary>
internal sealed class ObjectConverter<T> : ErlConverter<T>
{
    private static readonly ErlAtom StructKey = new("__struct__");

    private readonly Member[] _members;
    private readonly ConstructorInfo? _constructor;
    private readonly Member?[] _constructorMembers;
    private readonly object?[] _constructorDefaults;
    private readonly bool _hasParameterlessConstructor;
    private readonly ErlAtom? _structModule;
    private readonly ErlAtom? _recordTag;
    private readonly bool _atomKeys;

    public ObjectConverter(ErlSerializerOptions options)
    {
        var type = typeof(T);

        _structModule = type.GetCustomAttribute<ErlStructAttribute>() is { } s ? new ErlAtom(s.Module) : null;
        _recordTag = type.GetCustomAttribute<ErlRecordAttribute>() is { } r ? new ErlAtom(r.Tag) : null;

        // An Elixir struct is a map whose keys are atoms, so the key setting does not apply to one.
        _atomKeys = _structModule is not null || options.MapKeyKind == ErlMapKeyKind.Atom;

        _members = CollectMembers(type, options).ToArray();
        (_constructor, _constructorMembers, _constructorDefaults, _hasParameterlessConstructor) =
            PlanConstruction(type, _members);
    }

    // ---------------------------------------------------------------- writing

    public override ErlTerm Write(T value, ErlSerializerOptions options)
    {
        if (value is null) return options.NullAtom;

        if (_recordTag is not null)
        {
            var items = new ErlTerm[_members.Length + 1];
            items[0] = _recordTag;
            for (var i = 0; i < _members.Length; i++)
                items[i + 1] = WriteMember(_members[i], value, options);
            return new ErlTuple(items);
        }

        var entries = new List<KeyValuePair<ErlTerm, ErlTerm>>(_members.Length + 1);
        if (_structModule is not null)
            entries.Add(new KeyValuePair<ErlTerm, ErlTerm>(StructKey, _structModule));

        foreach (var member in _members)
        {
            var raw = member.Get(value);
            if (raw is null && options.IgnoreNullValues) continue;
            entries.Add(new KeyValuePair<ErlTerm, ErlTerm>(
                _atomKeys ? new ErlAtom(member.ErlName) : new ErlBinary(member.ErlName),
                WriteMember(member, value, options)));
        }

        return new ErlMap(entries);
    }

    private static ErlTerm WriteMember(Member member, T value, ErlSerializerOptions options)
    {
        var raw = member.Get(value!);
        if (raw is null) return options.NullAtom;
        return member.Converter is { } converter
            ? converter.WriteUntyped(raw, options)
            : ValueHelper.Write(raw, member.Type, options);
    }

    // ---------------------------------------------------------------- reading

    public override T Read(ErlTerm term, ErlSerializerOptions options) =>
        _recordTag is not null ? ReadRecord(term, options) : ReadMap(term, options);

    private T ReadRecord(ErlTerm term, ErlSerializerOptions options)
    {
        if (term is not ErlTuple tuple || tuple.Arity != _members.Length + 1)
            throw TermRead.Mismatch(term, $"a {_members.Length + 1} element {_recordTag} tuple");

        if (!tuple[0].IsAtom(_recordTag!.Name))
            throw new ErlSerializationException(
                $"expected a tuple tagged {_recordTag} but it was tagged {tuple[0]}");

        var values = new Dictionary<Member, object?>();
        for (var i = 0; i < _members.Length; i++)
            values[_members[i]] = ReadMember(_members[i], tuple[i + 1], options);

        return Construct(values);
    }

    private T ReadMap(ErlTerm term, ErlSerializerOptions options)
    {
        if (term is not ErlMap map)
            throw TermRead.Mismatch(term, $"a map to build a {typeof(T).Name} from");

        var values = new Dictionary<Member, object?>();
        foreach (var member in _members)
        {
            // Accept either key flavour on the way in, whichever the sender happened to use.
            if (!map.TryGetValue(new ErlAtom(member.ErlName), out var value) &&
                !map.TryGetValue(new ErlBinary(member.ErlName), out value))
                continue;

            values[member] = ReadMember(member, value, options);
        }

        return Construct(values);
    }

    private static object? ReadMember(Member member, ErlTerm value, ErlSerializerOptions options)
    {
        if (member.Converter is { } converter)
            return ValueHelper.IsNull(value, options) ? null : converter.ReadUntyped(value, options);
        return ValueHelper.Read(value, member.Type, options);
    }

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = Justifications.ReflectionFallback)]
    private T Construct(Dictionary<Member, object?> values)
    {
        object instance;

        if (_constructor is not null)
        {
            var args = new object?[_constructorMembers.Length];
            for (var i = 0; i < args.Length; i++)
            {
                var member = _constructorMembers[i];
                args[i] = member is not null && values.TryGetValue(member, out var value)
                    ? value
                    : _constructorDefaults[i];
            }

            try
            {
                instance = _constructor.Invoke(args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new ErlSerializationException(
                    $"the constructor of {typeof(T).Name} rejected the deserialized values: " +
                    ex.InnerException.Message, ex.InnerException);
            }
        }
        else if (_hasParameterlessConstructor || typeof(T).IsValueType)
        {
            instance = Activator.CreateInstance<T>()!;
        }
        else
        {
            throw new ErlSerializationException(
                $"{typeof(T)} cannot be deserialized: it has no parameterless constructor and no " +
                $"constructor whose parameters match its members");
        }

        // Anything the constructor did not take, and that has a setter, is assigned afterwards.
        foreach (var (member, value) in values)
        {
            if (member.Set is null || _constructorMembers.Contains(member)) continue;
            member.Set(instance, value);
        }

        return (T)instance;
    }

    // ------------------------------------------------------------ model setup

    private sealed class Member
    {
        public required string ErlName { get; init; }
        public required string ClrName { get; init; }
        public required Type Type { get; init; }
        public required Func<object, object?> Get { get; init; }
        public Action<object, object?>? Set { get; init; }
        public ErlConverter? Converter { get; init; }
    }

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = Justifications.ReflectionFallback)]
    private static List<Member> CollectMembers(Type type, ErlSerializerOptions options)
    {
        var members = new List<Member>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            if (property.GetMethod is null) continue;
            if (property.GetCustomAttribute<ErlIgnoreAttribute>() is not null) continue;
            // Records synthesise this; it is an implementation detail, not data.
            if (property.Name == "EqualityContract") continue;

            members.Add(new Member
            {
                ErlName = NameOf(property, property.Name, options),
                ClrName = property.Name,
                Type = property.PropertyType,
                Get = instance => property.GetValue(instance),
                Set = property.SetMethod is not null ? (instance, value) => property.SetValue(instance, value) : null,
                Converter = ExplicitConverter(property, property.PropertyType)
            });
        }

        if (options.IncludeFields)
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<ErlIgnoreAttribute>() is not null) continue;

                members.Add(new Member
                {
                    ErlName = NameOf(field, field.Name, options),
                    ClrName = field.Name,
                    Type = field.FieldType,
                    Get = instance => field.GetValue(instance),
                    Set = (instance, value) => field.SetValue(instance, value),
                    Converter = ExplicitConverter(field, field.FieldType)
                });
            }

        var duplicate = members.GroupBy(m => m.ErlName).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ErlSerializationException(
                $"{type.Name} maps more than one member onto '{duplicate.Key}': " +
                string.Join(", ", duplicate.Select(m => m.ClrName)));

        return members;
    }

    private static string NameOf(MemberInfo member, string clrName, ErlSerializerOptions options) =>
        member.GetCustomAttribute<ErlPropertyAttribute>()?.Name
        ?? options.PropertyNamingPolicy.ConvertName(clrName);

    private static ErlConverter? ExplicitConverter(MemberInfo member, Type memberType)
    {
        if (member.GetCustomAttribute<ErlConvertAttribute>() is { } convert)
            return AttributeConverterFactory.Create(convert.ConverterType, memberType);

        if (member.GetCustomAttribute<ErlAsAtomAttribute>() is not null)
        {
            if (memberType != typeof(string))
                throw new ErlSerializationException(
                    $"[ErlAsAtom] only applies to string members, but {member.Name} is {memberType.Name}");
            return AtomStringConverter.Instance;
        }

        return null;
    }

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = Justifications.ReflectionFallback)]
    private static (ConstructorInfo?, Member?[], object?[], bool) PlanConstruction(Type type, Member[] members)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // A parameterless constructor means we can create the instance and assign members after.
        if (constructors.Any(c => c.GetParameters().Length == 0))
            return (null, [], [], true);

        // Positional records land here: bind each parameter to the member of the same name.
        var best = constructors
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.GetParameters().All(p =>
                members.Any(m => string.Equals(m.ClrName, p.Name, StringComparison.OrdinalIgnoreCase))));

        if (best is null) return (null, [], [], false);

        var parameters = best.GetParameters();
        var bound = new Member?[parameters.Length];
        var defaults = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            bound[i] = members.FirstOrDefault(m =>
                string.Equals(m.ClrName, parameter.Name, StringComparison.OrdinalIgnoreCase));
            defaults[i] = parameter.HasDefaultValue
                ? parameter.DefaultValue
                : parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null;
        }

        return (best, bound, defaults, false);
    }
}

/// <summary>Writes a string as an atom, for members marked <see cref="ErlAsAtomAttribute"/>.</summary>
internal sealed class AtomStringConverter : ErlConverter<string>
{
    public static readonly AtomStringConverter Instance = new();

    public override ErlTerm Write(string value, ErlSerializerOptions options) => new ErlAtom(value);

    public override string Read(ErlTerm term, ErlSerializerOptions options) => TermRead.Text(term);
}
