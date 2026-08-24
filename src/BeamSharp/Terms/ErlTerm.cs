using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace BeamSharp.Terms;

/// <summary>
/// Base class for every Erlang term. Terms are immutable and compare by value, so they can be
/// used as dictionary keys (which is what Erlang maps require).
/// </summary>
public abstract class ErlTerm : IEquatable<ErlTerm>
{
    public abstract bool Equals(ErlTerm? other);

    public override bool Equals(object? obj) => obj is ErlTerm t && Equals(t);

    public override abstract int GetHashCode();

    /// <summary>Renders the term the way Erlang would print it (roughly <c>~p</c>).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        Format(sb);
        return sb.ToString();
    }

    internal abstract void Format(StringBuilder sb);

    public static implicit operator ErlTerm(long value) => new ErlInt(value);
    public static implicit operator ErlTerm(int value) => new ErlInt(value);
    public static implicit operator ErlTerm(double value) => new ErlFloat(value);
    public static implicit operator ErlTerm(bool value) => value ? ErlAtom.True : ErlAtom.False;
    public static implicit operator ErlTerm(byte[] value) => new ErlBinary(value);
}

/// <summary>An Erlang atom, e.g. <c>ok</c> or Elixir's <c>:ok</c>.</summary>
public sealed class ErlAtom : ErlTerm
{
    public static readonly ErlAtom True = new("true");
    public static readonly ErlAtom False = new("false");
    public static readonly ErlAtom Ok = new("ok");
    public static readonly ErlAtom Error = new("error");
    public static readonly ErlAtom Undefined = new("undefined");
    public static readonly ErlAtom Nil = new("nil");
    public static readonly ErlAtom Normal = new("normal");
    public static readonly ErlAtom NoProc = new("noproc");

    public string Name { get; }

    public ErlAtom(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>True when this atom is <c>true</c> or <c>false</c>.</summary>
    public bool IsBoolean => Name is "true" or "false";

    public bool AsBoolean => Name switch
    {
        "true" => true,
        "false" => false,
        _ => throw new InvalidCastException($"atom '{Name}' is not a boolean")
    };

    public override bool Equals(ErlTerm? other) => other is ErlAtom a && a.Name == Name;
    public override int GetHashCode() => HashCode.Combine(nameof(ErlAtom), Name);

    internal override void Format(StringBuilder sb)
    {
        // Unquoted only when it lexes as an unquoted atom.
        var plain = Name.Length > 0 && Name[0] is >= 'a' and <= 'z'
                    && Name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '@');
        if (plain) sb.Append(Name);
        else sb.Append('\'').Append(Name.Replace("'", "\\'")).Append('\'');
    }

    public static implicit operator ErlAtom(string name) => new(name);
}

/// <summary>An Erlang integer of any size. Small values do not allocate.</summary>
public sealed class ErlInt : ErlTerm
{
    public BigInteger Value { get; }

    public ErlInt(BigInteger value) => Value = value;
    public ErlInt(long value) => Value = value;

    public long AsLong => (long)Value;
    public int AsInt => (int)Value;

    /// <summary>The value as an int, or null when it does not fit in one.</summary>
    /// <remarks>
    /// Erlang integers are unbounded, so a term off the wire can be a bignum in a slot the code
    /// reading it expects to be small. <see cref="AsInt"/> throws there rather than truncating, and
    /// a throw is the wrong answer for input a peer chose.
    /// </remarks>
    public int? AsIntOrNull => Value >= int.MinValue && Value <= int.MaxValue ? (int)Value : null;

    public override bool Equals(ErlTerm? other) => other is ErlInt i && i.Value == Value;
    public override int GetHashCode() => HashCode.Combine(nameof(ErlInt), Value);
    internal override void Format(StringBuilder sb) => sb.Append(Value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>An Erlang float (always a 64-bit IEEE double on the wire).</summary>
public sealed class ErlFloat : ErlTerm
{
    public double Value { get; }

    public ErlFloat(double value) => Value = value;

    public override bool Equals(ErlTerm? other) => other is ErlFloat f && f.Value.Equals(Value);
    public override int GetHashCode() => HashCode.Combine(nameof(ErlFloat), Value);

    internal override void Format(StringBuilder sb)
    {
        var s = Value.ToString("R", CultureInfo.InvariantCulture);
        sb.Append(s);
        if (s.IndexOfAny(['.', 'e', 'E', 'N', 'I']) < 0) sb.Append(".0");
    }
}

/// <summary>An Erlang binary — Elixir's string type.</summary>
public sealed class ErlBinary : ErlTerm
{
    public byte[] Data { get; }

    public ErlBinary(byte[] data) => Data = data ?? throw new ArgumentNullException(nameof(data));
    public ErlBinary(string utf8) => Data = Encoding.UTF8.GetBytes(utf8);

    public int Length => Data.Length;

    /// <summary>Decodes the binary as UTF-8, which is how Elixir strings are represented.</summary>
    public string AsString() => Encoding.UTF8.GetString(Data);

    public override bool Equals(ErlTerm? other) => other is ErlBinary b && b.Data.AsSpan().SequenceEqual(Data);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(nameof(ErlBinary));
        hc.AddBytes(Data);
        return hc.ToHashCode();
    }

    internal override void Format(StringBuilder sb)
    {
        sb.Append("<<\"").Append(AsString().Replace("\"", "\\\"")).Append("\">>");
    }
}

/// <summary>A bitstring whose length is not a whole number of bytes.</summary>
public sealed class ErlBitstring : ErlTerm
{
    /// <summary>Backing bytes; the final byte is left-aligned and holds <see cref="TrailingBits"/> significant bits.</summary>
    public byte[] Data { get; }

    /// <summary>Number of significant bits in the last byte, 1..8.</summary>
    public int TrailingBits { get; }

    public ErlBitstring(byte[] data, int trailingBits)
    {
        if (trailingBits is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(trailingBits));
        Data = data;
        TrailingBits = trailingBits;
    }

    public long BitLength => Data.Length == 0 ? 0 : (long)(Data.Length - 1) * 8 + TrailingBits;

    public override bool Equals(ErlTerm? other) =>
        other is ErlBitstring b && b.TrailingBits == TrailingBits && b.Data.AsSpan().SequenceEqual(Data);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(nameof(ErlBitstring));
        hc.Add(TrailingBits);
        hc.AddBytes(Data);
        return hc.ToHashCode();
    }

    internal override void Format(StringBuilder sb) => sb.Append("<<...:").Append(BitLength).Append(">>");
}

/// <summary>An Erlang tuple.</summary>
public sealed class ErlTuple : ErlTerm
{
    private readonly ErlTerm[] _items;

    public ErlTuple(params ErlTerm[] items) => _items = items ?? throw new ArgumentNullException(nameof(items));
    public ErlTuple(IEnumerable<ErlTerm> items) => _items = items.ToArray();

    public int Arity => _items.Length;
    public ErlTerm this[int index] => _items[index];
    public ReadOnlySpan<ErlTerm> Items => _items;
    public ErlTerm[] ToArray() => (ErlTerm[])_items.Clone();

    public override bool Equals(ErlTerm? other)
    {
        if (other is not ErlTuple t || t._items.Length != _items.Length) return false;
        for (var i = 0; i < _items.Length; i++)
            if (!_items[i].Equals(t._items[i])) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(nameof(ErlTuple));
        foreach (var i in _items) hc.Add(i);
        return hc.ToHashCode();
    }

    internal override void Format(StringBuilder sb)
    {
        sb.Append('{');
        for (var i = 0; i < _items.Length; i++)
        {
            if (i > 0) sb.Append(',');
            _items[i].Format(sb);
        }
        sb.Append('}');
    }
}

/// <summary>
/// An Erlang list. <see cref="Tail"/> is <c>null</c> for a proper list; a non-null tail makes this an
/// improper list such as <c>[alias | Ref]</c>, which OTP really does put on the wire.
/// </summary>
public sealed class ErlList : ErlTerm
{
    public static readonly ErlList Empty = new([], null);

    private readonly ErlTerm[] _items;

    public ErlList(IEnumerable<ErlTerm> items, ErlTerm? tail = null)
    {
        _items = items.ToArray();

        // [X|[]] is [X]: a tail of the empty list is what makes a list proper, not a thing the list
        // ends with. Normalising here keeps one value from having two representations, which would
        // otherwise make equality disagree with Erlang and break round-tripping through the codec.
        Tail = tail is ErlList { Count: 0, IsProper: true } ? null : tail;
    }

    public ErlList(params ErlTerm[] items) : this((IEnumerable<ErlTerm>)items, null) { }

    public ErlTerm? Tail { get; }
    public bool IsProper => Tail is null;
    public int Count => _items.Length;
    public ErlTerm this[int index] => _items[index];
    public ReadOnlySpan<ErlTerm> Items => _items;
    public ErlTerm[] ToArray() => (ErlTerm[])_items.Clone();

    /// <summary>True when every element is an integer in 0..255, i.e. an Erlang "string"/charlist.</summary>
    public bool IsByteList => IsProper && _items.Length > 0 &&
                              _items.All(i => i is ErlInt n && n.Value >= 0 && n.Value <= 255);

    public override bool Equals(ErlTerm? other)
    {
        if (other is not ErlList l || l._items.Length != _items.Length) return false;
        if (Tail is null != (l.Tail is null)) return false;
        if (Tail is not null && !Tail.Equals(l.Tail)) return false;
        for (var i = 0; i < _items.Length; i++)
            if (!_items[i].Equals(l._items[i])) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(nameof(ErlList));
        foreach (var i in _items) hc.Add(i);
        hc.Add(Tail);
        return hc.ToHashCode();
    }

    internal override void Format(StringBuilder sb)
    {
        sb.Append('[');
        for (var i = 0; i < _items.Length; i++)
        {
            if (i > 0) sb.Append(',');
            _items[i].Format(sb);
        }
        if (Tail is not null)
        {
            sb.Append('|');
            Tail.Format(sb);
        }
        sb.Append(']');
    }
}

/// <summary>An Erlang map.</summary>
public sealed class ErlMap : ErlTerm
{
    private readonly Dictionary<ErlTerm, ErlTerm> _entries;

    public ErlMap(IEnumerable<KeyValuePair<ErlTerm, ErlTerm>> entries)
    {
        _entries = new Dictionary<ErlTerm, ErlTerm>();
        foreach (var (k, v) in entries) _entries[k] = v;
    }

    public ErlMap() => _entries = new Dictionary<ErlTerm, ErlTerm>();

    public int Count => _entries.Count;
    public IReadOnlyDictionary<ErlTerm, ErlTerm> Entries => _entries;

    public bool TryGetValue(ErlTerm key, [NotNullWhen(true)] out ErlTerm? value) => _entries.TryGetValue(key, out value);

    /// <summary>Looks up an atom key, the common case for Elixir structs and keyword-ish maps.</summary>
    public ErlTerm? Get(string atomKey) => _entries.TryGetValue(new ErlAtom(atomKey), out var v) ? v : null;

    public ErlTerm? this[ErlTerm key] => _entries.TryGetValue(key, out var v) ? v : null;

    /// <summary>Returns a copy with <paramref name="key"/> set to <paramref name="value"/>.</summary>
    public ErlMap With(ErlTerm key, ErlTerm value)
    {
        var copy = new Dictionary<ErlTerm, ErlTerm>(_entries) { [key] = value };
        return new ErlMap(copy);
    }

    public override bool Equals(ErlTerm? other)
    {
        if (other is not ErlMap m || m._entries.Count != _entries.Count) return false;
        foreach (var (k, v) in _entries)
            if (!m._entries.TryGetValue(k, out var ov) || !v.Equals(ov)) return false;
        return true;
    }

    public override int GetHashCode()
    {
        // Order-independent, as map key order is not significant.
        var acc = nameof(ErlMap).GetHashCode();
        foreach (var (k, v) in _entries) acc ^= HashCode.Combine(k, v);
        return acc;
    }

    internal override void Format(StringBuilder sb)
    {
        sb.Append("#{");
        var first = true;
        foreach (var (k, v) in _entries)
        {
            if (!first) sb.Append(',');
            first = false;
            k.Format(sb);
            sb.Append("=>");
            v.Format(sb);
        }
        sb.Append('}');
    }
}

/// <summary>A process identifier.</summary>
public sealed class ErlPid : ErlTerm
{
    public ErlPid(string node, uint id, uint serial, uint creation)
    {
        Node = node;
        Id = id;
        Serial = serial;
        Creation = creation;
    }

    public string Node { get; }
    public uint Id { get; }
    public uint Serial { get; }
    public uint Creation { get; }

    public override bool Equals(ErlTerm? other) =>
        other is ErlPid p && p.Node == Node && p.Id == Id && p.Serial == Serial && p.Creation == Creation;

    public override int GetHashCode() => HashCode.Combine(nameof(ErlPid), Node, Id, Serial, Creation);

    internal override void Format(StringBuilder sb) =>
        sb.Append(CultureInfo.InvariantCulture, $"<{Node}.{Id}.{Serial}>");
}

/// <summary>A port identifier. Ports are opaque here; they exist so terms round-trip.</summary>
public sealed class ErlPort : ErlTerm
{
    public ErlPort(string node, ulong id, uint creation)
    {
        Node = node;
        Id = id;
        Creation = creation;
    }

    public string Node { get; }
    public ulong Id { get; }
    public uint Creation { get; }

    public override bool Equals(ErlTerm? other) =>
        other is ErlPort p && p.Node == Node && p.Id == Id && p.Creation == Creation;

    public override int GetHashCode() => HashCode.Combine(nameof(ErlPort), Node, Id, Creation);
    internal override void Format(StringBuilder sb) =>
        sb.Append(CultureInfo.InvariantCulture, $"#Port<{Node}.{Id}>");
}

/// <summary>A reference — also what an Elixir <c>alias</c> is made of.</summary>
public sealed class ErlRef : ErlTerm
{
    public ErlRef(string node, uint creation, uint[] ids)
    {
        Node = node;
        Creation = creation;
        Ids = ids;
    }

    public string Node { get; }
    public uint Creation { get; }
    public uint[] Ids { get; }

    public override bool Equals(ErlTerm? other) =>
        other is ErlRef r && r.Node == Node && r.Creation == Creation && r.Ids.AsSpan().SequenceEqual(Ids);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(nameof(ErlRef));
        hc.Add(Node);
        hc.Add(Creation);
        foreach (var i in Ids) hc.Add(i);
        return hc.ToHashCode();
    }

    internal override void Format(StringBuilder sb) => sb.Append("#Ref<").Append(Node).Append('.')
        .AppendJoin('.', Ids).Append('>');
}

/// <summary>An external function reference such as <c>fun lists:reverse/1</c>.</summary>
public sealed class ErlExport : ErlTerm
{
    public ErlExport(ErlAtom module, ErlAtom function, int arity)
    {
        Module = module;
        Function = function;
        Arity = arity;
    }

    public ErlAtom Module { get; }
    public ErlAtom Function { get; }
    public int Arity { get; }

    public override bool Equals(ErlTerm? other) =>
        other is ErlExport e && e.Module.Equals(Module) && e.Function.Equals(Function) && e.Arity == Arity;

    public override int GetHashCode() => HashCode.Combine(nameof(ErlExport), Module, Function, Arity);
    internal override void Format(StringBuilder sb) =>
        sb.Append(CultureInfo.InvariantCulture, $"fun {Module.Name}:{Function.Name}/{Arity}");
}

/// <summary>
/// A closure. We cannot run Erlang code, so the body is kept as the raw encoded bytes and re-emitted
/// verbatim — enough to receive a fun and hand it back later.
/// </summary>
public sealed class ErlFun : ErlTerm
{
    public ErlFun(byte[] encoded) => Encoded = encoded;

    /// <summary>The complete external representation, including the leading tag byte.</summary>
    public byte[] Encoded { get; }

    public override bool Equals(ErlTerm? other) => other is ErlFun f && f.Encoded.AsSpan().SequenceEqual(Encoded);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(nameof(ErlFun));
        hc.AddBytes(Encoded);
        return hc.ToHashCode();
    }

    internal override void Format(StringBuilder sb) => sb.Append("#Fun<...>");
}
