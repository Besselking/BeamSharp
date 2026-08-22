using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace BeamSharp.Terms;

/// <summary>
/// Encodes <see cref="ErlTerm"/> values into the Erlang External Term Format.
/// Always emits the modern tags (UTF-8 atoms, NEW_PID_EXT, NEWER_REFERENCE_EXT, V4_PORT_EXT),
/// which is what the mandatory OTP 26+ distribution flags require anyway.
/// </summary>
public sealed class TermEncoder
{
    private byte[] _buffer;
    private int _length;

    public TermEncoder(int initialCapacity = 256) => _buffer = new byte[initialCapacity];

    public int Length => _length;
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    /// <summary>The written bytes without copying, for handing straight to a socket.</summary>
    public ArraySegment<byte> Segment => new(_buffer, 0, _length);

    /// <summary>Overwrites four already-written bytes, used to backfill a length prefix.</summary>
    public void PatchUInt32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(offset), value);

    public void Clear() => _length = 0;

    /// <summary>Encodes a single term with the leading version magic byte.</summary>
    public static byte[] Encode(ErlTerm term)
    {
        var e = new TermEncoder();
        e.WriteVersionedTerm(term);
        return e.Written.ToArray();
    }

    public void WriteVersionedTerm(ErlTerm term)
    {
        WriteByte(TermTags.VersionMagic);
        WriteTerm(term);
    }

    public void WriteTerm(ErlTerm term)
    {
        switch (term)
        {
            case ErlAtom a:
                WriteAtom(a.Name);
                break;

            case ErlInt i:
                WriteInteger(i.Value);
                break;

            case ErlFloat f:
                WriteByte(TermTags.NewFloat);
                WriteInt64(BitConverter.DoubleToInt64Bits(f.Value));
                break;

            case ErlBinary b:
                WriteByte(TermTags.Binary);
                WriteUInt32((uint)b.Data.Length);
                WriteBytes(b.Data);
                break;

            case ErlBitstring bs:
                WriteByte(TermTags.BitBinary);
                WriteUInt32((uint)bs.Data.Length);
                WriteByte((byte)bs.TrailingBits);
                WriteBytes(bs.Data);
                break;

            case ErlTuple t:
                if (t.Arity < 256)
                {
                    WriteByte(TermTags.SmallTuple);
                    WriteByte((byte)t.Arity);
                }
                else
                {
                    WriteByte(TermTags.LargeTuple);
                    WriteUInt32((uint)t.Arity);
                }
                foreach (var item in t.Items) WriteTerm(item);
                break;

            case ErlList l:
                WriteList(l);
                break;

            case ErlMap m:
                WriteByte(TermTags.Map);
                WriteUInt32((uint)m.Count);
                foreach (var (k, v) in m.Entries)
                {
                    WriteTerm(k);
                    WriteTerm(v);
                }
                break;

            case ErlPid p:
                WriteByte(TermTags.NewPid);
                WriteAtom(p.Node);
                WriteUInt32(p.Id);
                WriteUInt32(p.Serial);
                WriteUInt32(p.Creation);
                break;

            case ErlPort port:
                WriteByte(TermTags.V4Port);
                WriteAtom(port.Node);
                WriteUInt64(port.Id);
                WriteUInt32(port.Creation);
                break;

            case ErlRef r:
                WriteByte(TermTags.NewerReference);
                WriteUInt16((ushort)r.Ids.Length);
                WriteAtom(r.Node);
                WriteUInt32(r.Creation);
                foreach (var id in r.Ids) WriteUInt32(id);
                break;

            case ErlExport ex:
                WriteByte(TermTags.Export);
                WriteAtom(ex.Module.Name);
                WriteAtom(ex.Function.Name);
                WriteInteger(ex.Arity);
                break;

            case ErlFun fn:
                WriteBytes(fn.Encoded);
                break;

            default:
                throw new ArgumentException($"cannot encode term of type {term.GetType().Name}", nameof(term));
        }
    }

    private void WriteList(ErlList l)
    {
        if (l.Count == 0 && l.IsProper)
        {
            WriteByte(TermTags.Nil);
            return;
        }

        // A proper list of bytes is what Erlang calls a string; STRING_EXT is its compact form.
        if (l.Count <= ushort.MaxValue && l.IsByteList)
        {
            WriteByte(TermTags.String);
            WriteUInt16((ushort)l.Count);
            foreach (var item in l.Items) WriteByte((byte)((ErlInt)item).AsLong);
            return;
        }

        WriteByte(TermTags.List);
        WriteUInt32((uint)l.Count);
        foreach (var item in l.Items) WriteTerm(item);
        if (l.Tail is null) WriteByte(TermTags.Nil);
        else WriteTerm(l.Tail);
    }

    private void WriteAtom(string name)
    {
        var byteCount = Encoding.UTF8.GetByteCount(name);
        if (byteCount < 256)
        {
            WriteByte(TermTags.SmallAtomUtf8);
            WriteByte((byte)byteCount);
        }
        else
        {
            WriteByte(TermTags.AtomUtf8);
            WriteUInt16((ushort)byteCount);
        }
        Ensure(byteCount);
        Encoding.UTF8.GetBytes(name, _buffer.AsSpan(_length));
        _length += byteCount;
    }

    private void WriteInteger(BigInteger value)
    {
        if (value >= 0 && value <= byte.MaxValue)
        {
            WriteByte(TermTags.SmallInteger);
            WriteByte((byte)value);
            return;
        }

        if (value >= int.MinValue && value <= int.MaxValue)
        {
            WriteByte(TermTags.Integer);
            WriteInt32((int)value);
            return;
        }

        var sign = value.Sign < 0 ? (byte)1 : (byte)0;
        var magnitude = BigInteger.Abs(value).ToByteArray(isUnsigned: true, isBigEndian: false);

        if (magnitude.Length < 256)
        {
            WriteByte(TermTags.SmallBig);
            WriteByte((byte)magnitude.Length);
        }
        else
        {
            WriteByte(TermTags.LargeBig);
            WriteUInt32((uint)magnitude.Length);
        }
        WriteByte(sign);
        WriteBytes(magnitude);
    }

    // --- raw buffer helpers -------------------------------------------------

    public void WriteByte(byte b)
    {
        Ensure(1);
        _buffer[_length++] = b;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    private void WriteUInt16(ushort v)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_length), v);
        _length += 2;
    }

    private void WriteInt32(int v)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_length), v);
        _length += 4;
    }

    private void WriteUInt32(uint v)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), v);
        _length += 4;
    }

    private void WriteUInt64(ulong v)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), v);
        _length += 8;
    }

    private void WriteInt64(long v)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_length), v);
        _length += 8;
    }

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        var size = Math.Max(_buffer.Length * 2, _length + extra);
        Array.Resize(ref _buffer, size);
    }
}
