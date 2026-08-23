using System.Globalization;
using System.Numerics;
using BeamSharp.Terms;

namespace BeamSharp.Serialization.Converters;

/// <summary>The conversions that ship with the serializer.</summary>
internal static class BuiltInConverters
{
    private static readonly Dictionary<Type, ErlConverter> Exact = Build();

    /// <summary>Factories for families of types, in priority order.</summary>
    public static readonly ErlConverterFactory[] Factories =
    [
        TermPassthroughFactory.Instance,
        NullableConverterFactory.Instance,
        EnumConverterFactory.Instance,
        TupleConverterFactory.Instance,
        DictionaryConverterFactory.Instance,
        CollectionConverterFactory.Instance
    ];

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
            ErlBinary b => b.Data,
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

        return map;
    }
}

/// <summary>Hands <see cref="ErlTerm"/> values through untouched, so raw terms can be embedded.</summary>
internal sealed class TermPassthroughFactory : ErlConverterFactory
{
    public static readonly TermPassthroughFactory Instance = new();

    public override bool CanConvert(Type type) => typeof(ErlTerm).IsAssignableFrom(type);

    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options) =>
        (ErlConverter)Activator.CreateInstance(typeof(PassthroughConverter<>).MakeGenericType(type))!;

    private sealed class PassthroughConverter<T> : ErlConverter<T> where T : ErlTerm
    {
        public override ErlTerm Write(T value, ErlSerializerOptions options) => value;

        public override T Read(ErlTerm term, ErlSerializerOptions options) =>
            term as T ?? throw TermRead.Mismatch(term, typeof(T).Name);
    }
}
