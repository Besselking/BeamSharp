using System.Numerics;

namespace BeamSharp.Terms;

/// <summary>
/// Short factory helpers so building terms in C# reads close to how it reads in Elixir.
/// Named <c>Erl</c> rather than <c>Terms</c> so it does not collide with the namespace it lives in.
/// </summary>
public static class Erl
{
    public static ErlAtom Atom(string name) => new(name);
    public static ErlAtom Bool(bool value) => value ? ErlAtom.True : ErlAtom.False;
    public static ErlInt Int(long value) => new(value);
    public static ErlInt Int(BigInteger value) => new(value);
    public static ErlFloat Float(double value) => new(value);

    /// <summary>An Erlang binary holding UTF-8 text — this is what an Elixir string is.</summary>
    public static ErlBinary String(string text) => new(text);

    public static ErlBinary Binary(byte[] data) => new(data);

    /// <summary>An Erlang charlist, i.e. <c>'hello'</c> in Elixir / <c>"hello"</c> in Erlang.</summary>
    public static ErlList CharList(string text) => new(text.Select(c => (ErlTerm)new ErlInt(c)));

    public static ErlTuple Tuple(params ErlTerm[] items) => new(items);
    public static ErlList List(params ErlTerm[] items) => new(items);
    public static ErlList ImproperList(ErlTerm[] items, ErlTerm tail) => new(items, tail);
    public static ErlList Nil => ErlList.Empty;

    public static ErlMap Map(params (ErlTerm Key, ErlTerm Value)[] entries) =>
        new(entries.Select(e => new KeyValuePair<ErlTerm, ErlTerm>(e.Key, e.Value)));

    /// <summary>Builds a map with atom keys, the usual shape for Elixir-facing payloads.</summary>
    public static ErlMap Map(params (string Key, ErlTerm Value)[] entries) =>
        new(entries.Select(e => new KeyValuePair<ErlTerm, ErlTerm>(new ErlAtom(e.Key), e.Value)));

    /// <summary>An Erlang keyword list: <c>[{key, value}, ...]</c>.</summary>
    public static ErlList Keyword(params (string Key, ErlTerm Value)[] entries) =>
        new(entries.Select(e => (ErlTerm)new ErlTuple(new ErlAtom(e.Key), e.Value)));

    public static readonly ErlAtom Ok = ErlAtom.Ok;
    public static readonly ErlAtom Error = ErlAtom.Error;
    public static readonly ErlAtom True = ErlAtom.True;
    public static readonly ErlAtom False = ErlAtom.False;
}

/// <summary>Pattern-matching conveniences for inspecting incoming terms.</summary>
public static class TermExtensions
{
    /// <summary>True when the term is the given atom.</summary>
    public static bool IsAtom(this ErlTerm term, string name) => term is ErlAtom a && a.Name == name;

    /// <summary>Matches a tuple of exactly <paramref name="arity"/> elements.</summary>
    public static bool IsTuple(this ErlTerm term, int arity, out ErlTuple tuple)
    {
        if (term is ErlTuple t && t.Arity == arity)
        {
            tuple = t;
            return true;
        }
        tuple = null!;
        return false;
    }

    /// <summary>Matches <c>{tag, ...}</c> where the first element is the given atom.</summary>
    public static bool IsTagged(this ErlTerm term, string tag, out ErlTuple tuple)
    {
        if (term is ErlTuple t && t.Arity >= 1 && t[0].IsAtom(tag))
        {
            tuple = t;
            return true;
        }
        tuple = null!;
        return false;
    }

    /// <summary>Reads the term as text whether it arrived as a binary, an atom or a charlist.</summary>
    public static string AsText(this ErlTerm term) => term switch
    {
        ErlBinary b => b.AsString(),
        ErlAtom a => a.Name,
        ErlList l when l.IsByteList => new string(l.Items.ToArray().Select(i => (char)((ErlInt)i).AsLong).ToArray()),
        _ => throw new InvalidCastException($"term {term} is not text-like")
    };

    public static long AsLong(this ErlTerm term) =>
        term is ErlInt i ? i.AsLong : throw new InvalidCastException($"term {term} is not an integer");

    public static double AsDouble(this ErlTerm term) => term switch
    {
        ErlFloat f => f.Value,
        ErlInt i => (double)i.Value,
        _ => throw new InvalidCastException($"term {term} is not a number")
    };

    public static bool AsBool(this ErlTerm term) =>
        term is ErlAtom a ? a.AsBoolean : throw new InvalidCastException($"term {term} is not a boolean");
}
