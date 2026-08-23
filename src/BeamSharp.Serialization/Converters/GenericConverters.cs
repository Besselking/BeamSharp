using System.Diagnostics.CodeAnalysis;
using BeamSharp.Terms;

namespace BeamSharp.Serialization.Converters;

/// <summary>
/// Converts a sequence to and from an Erlang list.
/// <para>
/// Public so generated code can construct a closed instantiation directly. That is what makes the
/// generated path AOT-safe: reaching this through <see cref="Type.MakeGenericType"/> leaves the
/// native code for the instantiation unreachable, whereas a written-out <c>new</c> roots it.
/// </para>
/// </summary>
public sealed class ErlCollectionConverter<TCollection, TElement> : ErlConverter<TCollection>
    where TCollection : IEnumerable<TElement>
{
    public override ErlTerm Write(TCollection value, ErlSerializerOptions options)
    {
        if (value is null) return options.NullAtom;

        var items = new List<ErlTerm>();
        foreach (var item in value) items.Add(ValueHelper.Write(item, typeof(TElement), options));
        return new ErlList(items);
    }

    public override TCollection Read(ErlTerm term, ErlSerializerOptions options)
    {
        if (term is not ErlList list)
            throw TermRead.Mismatch(term, $"a list of {typeof(TElement).Name}");

        var items = new List<TElement>(list.Count);
        foreach (var item in list.Items.ToArray())
            items.Add((TElement)ValueHelper.Read(item, typeof(TElement), options)!);

        var target = typeof(TCollection);
        object materialised =
            target.IsArray ? items.ToArray()
            : target.IsGenericType && target.GetGenericTypeDefinition() == typeof(HashSet<>)
                ? new HashSet<TElement>(items)
                : items;

        return (TCollection)materialised;
    }
}

/// <summary>Converts a dictionary to and from an Erlang map. Public for the same reason.</summary>
public sealed class ErlDictionaryConverter<TDictionary, TKey, TValue> : ErlConverter<TDictionary>
    where TDictionary : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    public override ErlTerm Write(TDictionary value, ErlSerializerOptions options)
    {
        if (value is null) return options.NullAtom;

        var entries = new List<KeyValuePair<ErlTerm, ErlTerm>>();
        foreach (var entry in value)
            entries.Add(new KeyValuePair<ErlTerm, ErlTerm>(
                ValueHelper.Write(entry.Key, typeof(TKey), options),
                ValueHelper.Write(entry.Value, typeof(TValue), options)));
        return new ErlMap(entries);
    }

    public override TDictionary Read(ErlTerm term, ErlSerializerOptions options)
    {
        if (term is not ErlMap map) throw TermRead.Mismatch(term, "a map");

        var result = new Dictionary<TKey, TValue>(map.Count);
        foreach (var (key, value) in map.Entries)
            result[(TKey)ValueHelper.Read(key, typeof(TKey), options)!] =
                (TValue)ValueHelper.Read(value, typeof(TValue), options)!;

        var target = typeof(TDictionary);
        object materialised =
            target.IsGenericType && target.GetGenericTypeDefinition() == typeof(SortedDictionary<,>)
                ? new SortedDictionary<TKey, TValue>(result)
                : result;

        return (TDictionary)materialised;
    }
}

/// <summary>Maps <c>Nullable&lt;T&gt;</c> onto the underlying converter plus a null atom.</summary>
public sealed class ErlNullableConverter<T> : ErlConverter<T?> where T : struct
{
    private readonly ErlConverter<T> _inner;

    /// <summary>Resolves the underlying converter from the options.</summary>
    public ErlNullableConverter(ErlSerializerOptions options) => _inner = options.GetConverter<T>();

    /// <summary>Uses a converter supplied directly.</summary>
    public ErlNullableConverter(ErlConverter<T> inner) => _inner = inner;

    public override ErlTerm Write(T? value, ErlSerializerOptions options) =>
        value.HasValue ? _inner.Write(value.Value, options) : options.NullAtom;

    public override T? Read(ErlTerm term, ErlSerializerOptions options) =>
        ValueHelper.IsNull(term, options) ? null : _inner.Read(term, options);
}

/// <summary>
/// Writes enum members as atoms — <c>Status.InProgress</c> becomes <c>:in_progress</c>. A value that
/// is not a declared member (a flags combination, say) falls back to its integer.
/// </summary>
public sealed class ErlEnumConverter<T> : ErlConverter<T> where T : struct, Enum
{
    private readonly Dictionary<T, string> _toAtom = new();
    private readonly Dictionary<string, T> _fromAtom = new(StringComparer.Ordinal);

    /// <summary>Builds the name mapping, honouring the naming policy and any member overrides.</summary>
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    public ErlEnumConverter(ErlSerializerOptions options)
    {
        foreach (var (value, name, explicitName) in Members(options)) Add(value, name, explicitName);
    }

    /// <summary>Used by generated code, which supplies the members it read at compile time.</summary>
    public ErlEnumConverter(ErlSerializerOptions options, (T Value, string ClrName, string? ExplicitName)[] members)
    {
        foreach (var (value, name, explicitName) in members)
            Add(value, options.EnumNamingPolicy.ConvertName(name), explicitName);
    }

    private void Add(T value, string name, string? explicitName)
    {
        var atom = explicitName ?? name;
        _toAtom.TryAdd(value, atom);
        _fromAtom[atom] = value;
    }

    [RequiresUnreferencedCode("Reads enum member attributes reflectively; generated code passes them in instead.")]
    private static IEnumerable<(T, string, string?)> Members(ErlSerializerOptions options)
    {
        foreach (var name in Enum.GetNames<T>())
        {
            var field = typeof(T).GetField(name)!;
            var explicitName = field.GetCustomAttributes(typeof(ErlPropertyAttribute), false)
                .Cast<ErlPropertyAttribute>().FirstOrDefault()?.Name;
            yield return (Enum.Parse<T>(name), options.EnumNamingPolicy.ConvertName(name), explicitName);
        }
    }

    public override ErlTerm Write(T value, ErlSerializerOptions options) =>
        _toAtom.TryGetValue(value, out var atom)
            ? new ErlAtom(atom)
            : new ErlInt(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));

    public override T Read(ErlTerm term, ErlSerializerOptions options)
    {
        switch (term)
        {
            case ErlAtom a when _fromAtom.TryGetValue(a.Name, out var value):
                return value;
            case ErlAtom a:
                throw new ErlSerializationException(
                    $"'{a.Name}' is not a member of {typeof(T).Name}; expected one of " +
                    string.Join(", ", _fromAtom.Keys));
            case ErlInt i:
                return (T)Enum.ToObject(typeof(T), (long)i.Value);
            default:
                throw TermRead.Mismatch(term, $"an atom naming a {typeof(T).Name}");
        }
    }
}
