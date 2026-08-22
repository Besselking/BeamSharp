namespace Erlang.Distribution.Terms;

/// <summary>External Term Format tag bytes. Verified against OTP 29 output.</summary>
internal static class TermTags
{
    public const byte VersionMagic = 131;

    public const byte NewFloat = 70;
    public const byte BitBinary = 77;
    public const byte AtomCacheRef = 82;
    public const byte NewPid = 88;
    public const byte NewPort = 89;
    public const byte NewerReference = 90;
    public const byte SmallInteger = 97;
    public const byte Integer = 98;
    public const byte Float = 99;
    public const byte Atom = 100;          // latin-1, 2-byte length (deprecated)
    public const byte Reference = 101;     // pre-OTP-19
    public const byte Port = 102;
    public const byte Pid = 103;
    public const byte SmallTuple = 104;
    public const byte LargeTuple = 105;
    public const byte Nil = 106;
    public const byte String = 107;
    public const byte List = 108;
    public const byte Binary = 109;
    public const byte SmallBig = 110;
    public const byte LargeBig = 111;
    public const byte NewFun = 112;
    public const byte Export = 113;
    public const byte NewReference = 114;
    public const byte SmallAtom = 115;     // latin-1, 1-byte length (deprecated)
    public const byte Map = 116;
    public const byte Fun = 117;
    public const byte AtomUtf8 = 118;
    public const byte SmallAtomUtf8 = 119;
    public const byte V4Port = 120;
}
