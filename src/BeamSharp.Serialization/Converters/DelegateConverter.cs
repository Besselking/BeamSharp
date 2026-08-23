using BeamSharp.Terms;

namespace BeamSharp.Serialization.Converters;

/// <summary>A converter built from a pair of functions, to keep the built-in table compact.</summary>
internal sealed class DelegateConverter<T>(
    Func<T, ErlSerializerOptions, ErlTerm> write,
    Func<ErlTerm, ErlSerializerOptions, T> read) : ErlConverter<T>
{
    public override ErlTerm Write(T value, ErlSerializerOptions options) => write(value, options);
    public override T Read(ErlTerm term, ErlSerializerOptions options) => read(term, options);
}

/// <summary>Shared helpers for reading scalars back out of terms with useful failure messages.</summary>
internal static class TermRead
{
    public static System.Numerics.BigInteger Integer(ErlTerm term) => term switch
    {
        ErlInt i => i.Value,
        ErlFloat f when Math.Abs(f.Value % 1) < double.Epsilon => new System.Numerics.BigInteger(f.Value),
        _ => throw Mismatch(term, "an integer")
    };

    public static double Number(ErlTerm term) => term switch
    {
        ErlFloat f => f.Value,
        ErlInt i => (double)i.Value,
        _ => throw Mismatch(term, "a number")
    };

    /// <summary>Accepts any of the three ways Erlang carries text.</summary>
    public static string Text(ErlTerm term) => term switch
    {
        ErlBinary b => b.AsString(),
        ErlAtom a => a.Name,
        ErlList l when l.Count == 0 && l.IsProper => string.Empty,
        ErlList l when l.IsByteList => new string(l.Items.ToArray()
            .Select(i => (char)(int)((ErlInt)i).Value).ToArray()),
        _ => throw Mismatch(term, "text")
    };

    public static bool Bool(ErlTerm term) => term switch
    {
        ErlAtom { Name: "true" } => true,
        ErlAtom { Name: "false" } => false,
        _ => throw Mismatch(term, "a boolean")
    };

    /// <summary>
    /// Narrows a bignum to a fixed-width type. BigInteger does not implement IConvertible, so this
    /// goes through its explicit conversions, which raise on overflow rather than wrapping.
    /// </summary>
    public static T Checked<T>(System.Numerics.BigInteger value, ErlTerm term)
    {
        try
        {
            object narrowed = Type.GetTypeCode(typeof(T)) switch
            {
                TypeCode.SByte => (sbyte)value,
                TypeCode.Byte => (byte)value,
                TypeCode.Int16 => (short)value,
                TypeCode.UInt16 => (ushort)value,
                TypeCode.Int32 => (int)value,
                TypeCode.UInt32 => (uint)value,
                TypeCode.Int64 => (long)value,
                TypeCode.UInt64 => (ulong)value,
                _ => throw new ErlSerializationException($"{typeof(T).Name} is not an integer type")
            };

            return (T)narrowed;
        }
        catch (OverflowException ex)
        {
            throw new ErlSerializationException($"{term} does not fit in a {typeof(T).Name}", ex);
        }
    }

    public static ErlSerializationException Mismatch(ErlTerm term, string expected) =>
        new($"expected {expected} but the term was {term}");
}
