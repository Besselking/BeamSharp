using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace BeamSharp.Terms;

/// <summary>Decodes the Erlang External Term Format into <see cref="ErlTerm"/> values.</summary>
public ref struct TermDecoder
{
    /// <summary>
    /// How deeply terms may nest before decoding gives up.
    /// <para>
    /// Nesting costs two bytes a level on the wire — <c>{104, 1}</c> is a one-element tuple — so a
    /// 40 KB frame can nest twenty thousand deep, and this decoder walks nesting with the call
    /// stack. Overflowing it is not an exception that can be caught: the runtime aborts the process.
    /// For comparison, System.Text.Json defaults to 64.
    /// </para>
    /// </summary>
    public const int DefaultMaxDepth = 256;

    /// <summary>
    /// The largest arity an export may claim. The emulator caps a function at 255 arguments, so a
    /// larger value is malformed rather than merely unusual.
    /// </summary>
    private const int MaxExportArity = 255;

    private ReadOnlySpan<byte> _data;
    private readonly int _maxDepth;
    private int _pos;
    private int _depth;

    public TermDecoder(ReadOnlySpan<byte> data, int maxDepth = DefaultMaxDepth)
    {
        _data = data;
        _maxDepth = maxDepth;
        _pos = 0;
        _depth = 0;
    }

    /// <summary>Bytes consumed so far.</summary>
    public int Position => _pos;

    /// <summary>Decodes one term including the leading version magic byte (131).</summary>
    public static ErlTerm Decode(ReadOnlySpan<byte> data, int maxDepth = DefaultMaxDepth)
    {
        var d = new TermDecoder(data, maxDepth);
        return d.ReadVersionedTerm();
    }

    /// <summary>Decodes one term including the leading 131, reporting how many bytes it used.</summary>
    public static ErlTerm Decode(ReadOnlySpan<byte> data, out int consumed, int maxDepth = DefaultMaxDepth)
    {
        var d = new TermDecoder(data, maxDepth);
        var term = d.ReadVersionedTerm();
        consumed = d.Position;
        return term;
    }

    public ErlTerm ReadVersionedTerm()
    {
        var magic = ReadByte();
        if (magic != TermTags.VersionMagic)
            throw new ErlDecodeException($"expected version magic 131, got {magic}");
        return ReadTerm();
    }

    public ErlTerm ReadTerm()
    {
        if (++_depth > _maxDepth)
            throw new ErlDecodeException(
                $"terms are nested deeper than {_maxDepth} levels; refusing to recurse further");

        try
        {
            return ReadTermCore();
        }
        finally
        {
            _depth--;
        }
    }

    private ErlTerm ReadTermCore()
    {
        var tag = ReadByte();
        switch (tag)
        {
            case TermTags.SmallInteger:
                return new ErlInt(ReadByte());

            case TermTags.Integer:
                return new ErlInt(ReadInt32());

            case TermTags.NewFloat:
                return new ErlFloat(BitConverter.Int64BitsToDouble(ReadInt64()));

            case TermTags.Float:
            {
                // Legacy: 31 bytes of "%.20e" text.
                var text = Encoding.ASCII.GetString(Take(31)).TrimEnd('\0');
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new ErlDecodeException($"'{text}' is not a valid float");
                return new ErlFloat(value);
            }

            case TermTags.SmallBig:
                return ReadBig(ReadByte());

            case TermTags.LargeBig:
                return ReadBig(ReadLength());

            case TermTags.Atom:
                return new ErlAtom(Encoding.Latin1.GetString(Take(ReadUInt16())));

            case TermTags.SmallAtom:
                return new ErlAtom(Encoding.Latin1.GetString(Take(ReadByte())));

            case TermTags.AtomUtf8:
                return new ErlAtom(Encoding.UTF8.GetString(Take(ReadUInt16())));

            case TermTags.SmallAtomUtf8:
                return new ErlAtom(Encoding.UTF8.GetString(Take(ReadByte())));

            case TermTags.SmallTuple:
                return ReadTuple(ReadByte());

            case TermTags.LargeTuple:
                return ReadTuple(ReadCount(bytesPerElement: 1));

            case TermTags.Nil:
                return ErlList.Empty;

            case TermTags.String:
            {
                var len = ReadUInt16();
                var bytes = Take(len);
                var items = new ErlTerm[len];
                for (var i = 0; i < len; i++) items[i] = new ErlInt(bytes[i]);
                return new ErlList(items);
            }

            case TermTags.List:
            {
                var len = ReadCount(bytesPerElement: 1);
                var items = new ErlTerm[len];
                for (var i = 0; i < len; i++) items[i] = ReadTerm();
                var tail = ReadTerm();
                // A NIL_EXT tail means a proper list.
                return new ErlList(items, tail is ErlList { Count: 0, IsProper: true } ? null : tail);
            }

            case TermTags.Binary:
                return new ErlBinary(Take(ReadLength()).ToArray());

            case TermTags.BitBinary:
            {
                var len = ReadLength();
                var bits = ReadByte();
                if (bits is < 1 or > 8)
                    throw new ErlDecodeException($"a bitstring claims {bits} trailing bits; 1 to 8 are valid");
                var data = Take(len).ToArray();
                return bits == 8 ? new ErlBinary(data) : new ErlBitstring(data, bits);
            }

            case TermTags.Map:
            {
                // Two terms per entry, so two bytes is the floor for one.
                var arity = ReadCount(bytesPerElement: 2);
                var entries = new KeyValuePair<ErlTerm, ErlTerm>[arity];
                for (var i = 0; i < arity; i++)
                {
                    var k = ReadTerm();
                    var v = ReadTerm();
                    entries[i] = new KeyValuePair<ErlTerm, ErlTerm>(k, v);
                }
                return new ErlMap(entries);
            }

            case TermTags.NewPid:
            {
                var node = ReadAtomName();
                return new ErlPid(node, ReadUInt32(), ReadUInt32(), ReadUInt32());
            }

            case TermTags.Pid:
            {
                var node = ReadAtomName();
                var id = ReadUInt32();
                var serial = ReadUInt32();
                return new ErlPid(node, id, serial, ReadByte());
            }

            case TermTags.V4Port:
            {
                var node = ReadAtomName();
                return new ErlPort(node, ReadUInt64(), ReadUInt32());
            }

            case TermTags.NewPort:
            {
                var node = ReadAtomName();
                return new ErlPort(node, ReadUInt32(), ReadUInt32());
            }

            case TermTags.Port:
            {
                var node = ReadAtomName();
                var id = ReadUInt32();
                return new ErlPort(node, id, ReadByte());
            }

            case TermTags.NewerReference:
            {
                var len = ReadUInt16();
                Ensure(len, bytesPerElement: 4);
                var node = ReadAtomName();
                var creation = ReadUInt32();
                var ids = new uint[len];
                for (var i = 0; i < len; i++) ids[i] = ReadUInt32();
                return new ErlRef(node, creation, ids);
            }

            case TermTags.NewReference:
            {
                var len = ReadUInt16();
                Ensure(len, bytesPerElement: 4);
                var node = ReadAtomName();
                var creation = (uint)ReadByte();
                var ids = new uint[len];
                for (var i = 0; i < len; i++) ids[i] = ReadUInt32();
                return new ErlRef(node, creation, ids);
            }

            case TermTags.Reference:
            {
                var node = ReadAtomName();
                var id = ReadUInt32();
                return new ErlRef(node, ReadByte(), [id]);
            }

            case TermTags.Export:
            {
                if (ReadTerm() is not ErlAtom mod || ReadTerm() is not ErlAtom fun ||
                    ReadTerm() is not ErlInt arity)
                    throw new ErlDecodeException("an export needs a module atom, a function atom and an arity");
                // Erlang integers are unbounded, so nothing stops a peer putting a bignum in the
                // arity slot. Casting it here threw OverflowException, which escapes the single
                // failure mode the rest of this decoder is careful to present.
                if (arity.AsIntOrNull is not { } fixedArity || fixedArity is < 0 or > MaxExportArity)
                    throw new ErlDecodeException($"an export claims an arity of {arity.Value}");
                return new ErlExport(mod, fun, fixedArity);
            }

            case TermTags.NewFun:
            {
                // Size covers everything after the tag byte, itself included.
                var start = _pos - 1;
                var size = ReadLength();
                if (size < 4) throw new ErlDecodeException($"a fun claims a size of {size} bytes");
                Take(size - 4);
                return new ErlFun(_data.Slice(start, _pos - start).ToArray());
            }

            case TermTags.Fun:
            {
                // No length prefix: walk the structure to find the end.
                var start = _pos - 1;
                var numFree = ReadCount(bytesPerElement: 1);
                ReadTerm(); // Pid
                ReadTerm(); // Module
                ReadTerm(); // Index
                ReadTerm(); // Uniq
                for (var i = 0; i < numFree; i++) ReadTerm();
                return new ErlFun(_data.Slice(start, _pos - start).ToArray());
            }

            case TermTags.AtomCacheRef:
                throw new ErlDecodeException(
                    "atom cache references are not supported; DFLAG_DIST_HDR_ATOM_CACHE must stay unnegotiated");

            default:
                throw new ErlDecodeException($"unknown external term format tag {tag} at offset {_pos - 1}");
        }
    }

    private ErlTuple ReadTuple(int arity)
    {
        var items = new ErlTerm[arity];
        for (var i = 0; i < arity; i++) items[i] = ReadTerm();
        return new ErlTuple(items);
    }

    private ErlInt ReadBig(int byteCount)
    {
        var sign = ReadByte();
        var bytes = Take(byteCount);
        // Erlang stores bignums little-endian magnitude plus a sign byte.
        var magnitude = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        return new ErlInt(sign == 0 ? magnitude : -magnitude);
    }

    private string ReadAtomName()
    {
        var t = ReadTerm();
        if (t is ErlAtom a) return a.Name;
        throw new ErlDecodeException($"expected an atom for a node name, got {t}");
    }

    /// <summary>
    /// Reads an element count and checks it against the bytes actually left.
    /// <para>
    /// Without this, a six-byte frame claiming a hundred million element tuple allocates the array
    /// before discovering the data is not there. Nothing on the wire encodes an element in less
    /// than a byte, so a count larger than the remaining input cannot be honest.
    /// </para>
    /// </summary>
    private int ReadCount(int bytesPerElement)
    {
        var count = ReadLength();
        Ensure(count, bytesPerElement);
        return count;
    }

    /// <summary>Reads a 32-bit length. Anything past int range cannot address real data.</summary>
    private int ReadLength()
    {
        var value = ReadUInt32();
        if (value > int.MaxValue)
            throw new ErlDecodeException($"a term claims a length of {value}, which cannot be addressed");
        return (int)value;
    }

    private void Ensure(int count, int bytesPerElement)
    {
        var remaining = _data.Length - _pos;
        if (count < 0 || (long)count * bytesPerElement > remaining)
            throw new ErlDecodeException(
                $"a term claims {count} elements but only {remaining} bytes remain");
    }

    private byte ReadByte()
    {
        if (_pos >= _data.Length) throw new ErlDecodeException("unexpected end of term data");
        return _data[_pos++];
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
            throw new ErlDecodeException("unexpected end of term data");
        var span = _data.Slice(_pos, count);
        _pos += count;
        return span;
    }

    private ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    private int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    private uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    private ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
    private long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
}

/// <summary>Thrown when a term cannot be read, because it is truncated or uses an unsupported tag.</summary>
public sealed class ErlDecodeException : Exception
{
    /// <summary>Creates the exception with an explanatory message.</summary>
    public ErlDecodeException(string message) : base(message) { }
}
