using System.Globalization;
using System.Numerics;
using BeamSharp.Terms;

namespace BeamSharp.Serialization.Converters;

/// <summary>The conversions that ship with the serializer.</summary>
internal static class BuiltInConverters
{
    private static readonly Dictionary<Type, ErlConverter> Exact = Build();


    public static bool TryGet(Type type, out ErlConverter converter) => Exact.TryGetValue(type, out converter!);

    private static Dictionary<Type, ErlConverter> Build()
    {
        var map = new Dictionary<Type, ErlConverter>();

        void Add<T>(Func<T, ErlSerializerOptions, ErlTerm> write, Func<ErlTerm, ErlSerializerOptions, T> read) =>
            map[typeof(T)] = new DelegateConverter<T>(write, read);

        // Elixir strings are UTF-8 binaries; reading also accepts atoms and charlists.
        Add<string>((v, _) => new ErlBinary(v), (t, _) => TermRead.Text(t));
        Add<char>((v, _) => new ErlInt(v), (t, _) => (char)(int)TermRead.Integer(t));
        Add<bool>((v, _) => v ? ErlAtom.True : ErlAtom.False, (t, _) => TermRead.Bool(t));

        Add<sbyte>((v, _) => new ErlInt(v), (t, _) => TermRead.Checked<sbyte>(TermRead.Integer(t), t));
        Add<byte>((v, _) => new ErlInt(v), (t, _) => TermRead.Checked<byte>(TermRead.Integer(t), t));
        Add<short>((v, _) => new ErlInt(v), (t, _) => TermRead.Checked<short>(TermRead.Integer(t), t));
        Add<ushort>((v, _) => new ErlInt(v), (t, _) => TermRead.Checked<ushort>(TermRead.Integer(t), t));
        Add<int>((v, _) => new ErlInt(v), (t, _) => TermRead.Checked<int>(TermRead.Integer(t), t));
        Add<uint>((v, _) => new ErlInt(v), (t, _) => TermRead.Checked<uint>(TermRead.Integer(t), t));
        Add<long>((v, _) => new ErlInt(v), (t, _) => TermRead.Checked<long>(TermRead.Integer(t), t));
        Add<ulong>((v, _) => new ErlInt(v), (t, _) => TermRead.Checked<ulong>(TermRead.Integer(t), t));
        Add<BigInteger>((v, _) => new ErlInt(v), (t, _) => TermRead.Integer(t));

        Add<float>((v, _) => new ErlFloat(v), (t, _) => (float)TermRead.Number(t));
        Add<double>((v, _) => new ErlFloat(v), (t, _) => TermRead.Number(t));

        // Erlang has no decimal type. This is lossy past 15-17 significant digits; register your own
        // converter (a {sign, coefficient, exponent} tuple, say) if that matters to you.
        Add<decimal>((v, _) => new ErlFloat((double)v), (t, _) => (decimal)TermRead.Number(t));

        // Raw bytes stay a binary rather than becoming a list of integers.
        Add<byte[]>((v, _) => new ErlBinary(v), (t, _) => t switch
        {
            ErlBinary b => b.Data.ToArray(),
            ErlList l when l.IsByteList => l.Items.ToArray().Select(i => (byte)(int)((ErlInt)i).Value).ToArray(),
            _ => throw TermRead.Mismatch(t, "a binary")
        });

        // ISO 8601 text, which DateTime.from_iso8601/1 and friends parse directly on the Elixir side.
        Add<DateTime>((v, _) => new ErlBinary(v.ToString("O", CultureInfo.InvariantCulture)),
            (t, _) => DateTime.Parse(TermRead.Text(t), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Add<DateTimeOffset>((v, _) => new ErlBinary(v.ToString("O", CultureInfo.InvariantCulture)),
            (t, _) => DateTimeOffset.Parse(TermRead.Text(t), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Add<DateOnly>((v, _) => new ErlBinary(v.ToString("O", CultureInfo.InvariantCulture)),
            (t, _) => DateOnly.Parse(TermRead.Text(t), CultureInfo.InvariantCulture));
        Add<TimeOnly>((v, _) => new ErlBinary(v.ToString("O", CultureInfo.InvariantCulture)),
            (t, _) => TimeOnly.Parse(TermRead.Text(t), CultureInfo.InvariantCulture));

        // Microseconds is a real Erlang time unit, so this stays arithmetic-friendly over there.
        // Sub-microsecond precision is truncated.
        Add<TimeSpan>((v, _) => new ErlInt(v.Ticks / 10),
            (t, _) => new TimeSpan(TermRead.Checked<long>(TermRead.Integer(t), t) * 10));

        Add<Guid>((v, _) => new ErlBinary(v.ToString("D", CultureInfo.InvariantCulture)),
            (t, _) => Guid.Parse(TermRead.Text(t)));
        Add<Uri>((v, _) => new ErlBinary(v.ToString()), (t, _) => new Uri(TermRead.Text(t)));

        // object is asymmetric, and deliberately so. Writing one can use the runtime type, which is
        // what lets a Dictionary<string, object> of mixed values work. Reading has nothing to go on:
        // a term carries no type name, so there is no shape to build. Handling that here rather than
        // letting the reflection fallback claim it is what makes it say so -- ObjectConverter<object>
        // finds no members on object, and so writes an empty map and reads back a bare instance,
        // losing the payload in both directions without a word.
        Add<object>(
            (v, o) => v.GetType() == typeof(object)
                ? throw new ErlSerializationException(
                    "cannot serialize a bare object instance: it carries nothing to write")
                : ValueHelper.Write(v, v.GetType(), o),
            (t, _) => throw new ErlSerializationException(
                $"cannot read {t} into object, because nothing in a term says which type to build. " +
                $"Deserialize into the concrete type instead, or use ErlTerm to hold the term as it " +
                $"arrived (Dictionary<string, ErlTerm> rather than Dictionary<string, object>)."));

        // Terms pass through untouched. The set of them is closed, so listing the instantiations
        // here keeps this reachable without any generic machinery.
        void Pass<T>() where T : ErlTerm => map[typeof(T)] = new PassthroughConverter<T>();

        Pass<ErlTerm>();
        Pass<ErlAtom>();
        Pass<ErlInt>();
        Pass<ErlFloat>();
        Pass<ErlBinary>();
        Pass<ErlBitstring>();
        Pass<ErlTuple>();
        Pass<ErlList>();
        Pass<ErlMap>();
        Pass<ErlPid>();
        Pass<ErlPort>();
        Pass<ErlRef>();
        Pass<ErlExport>();
        Pass<ErlFun>();

        return map;
    }
}

/// <summary>Hands a term through unchanged, so raw terms can be embedded in an object.</summary>
internal sealed class PassthroughConverter<T> : ErlConverter<T> where T : ErlTerm
{
    public override ErlTerm Write(T value, ErlSerializerOptions options) => value;

    public override T Read(ErlTerm term, ErlSerializerOptions options) =>
        term as T ?? throw TermRead.Mismatch(term, typeof(T).Name);
}
