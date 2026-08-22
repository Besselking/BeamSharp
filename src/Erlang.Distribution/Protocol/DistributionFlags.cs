namespace Erlang.Distribution.Protocol;

/// <summary>
/// Distribution capability flags, mirroring <c>kernel/include/dist.hrl</c> in OTP 29.
/// Both sides advertise a set and the intersection is what the connection uses.
/// </summary>
[Flags]
public enum DistributionFlags : ulong
{
    None = 0,

    Published = 0x01,
    AtomCache = 0x02,
    ExtendedReferences = 0x04,
    DistMonitor = 0x08,
    FunTags = 0x10,
    DistMonitorName = 0x20,
    HiddenAtomCache = 0x40,
    NewFunTags = 0x80,
    ExtendedPidsPorts = 0x100,
    ExportPtrTag = 0x200,
    BitBinaries = 0x400,
    NewFloats = 0x800,
    UnicodeIo = 0x1000,
    DistHdrAtomCache = 0x2000,
    SmallAtomTags = 0x4000,
    Utf8Atoms = 0x10000,
    MapTag = 0x20000,
    BigCreation = 0x40000,
    SendSender = 0x80000,
    BigSeqTraceLabels = 0x100000,
    ExitPayload = 0x400000,
    Fragments = 0x800000,
    Handshake23 = 0x1000000,
    UnlinkId = 0x2000000,
    Mandatory25Digest = 0x4000000,

    Spawn = 0x01UL << 32,
    NameMe = 0x02UL << 32,
    V4Nc = 0x04UL << 32,
    Alias = 0x08UL << 32,
    AltActSig = 0x20UL << 32,
    NativeRecords = 0x40UL << 32,

    /// <summary>Flags every OTP 26+ node insists on. Omitting any of these gets the connection rejected.</summary>
    Mandatory =
        ExtendedReferences | FunTags | ExtendedPidsPorts | NewFunTags | ExportPtrTag |
        BitBinaries | NewFloats | Utf8Atoms | MapTag | BigCreation | Handshake23 |
        UnlinkId | V4Nc,

    /// <summary>
    /// What this library advertises. Deliberately excludes the atom cache and fragmentation:
    /// both are pure optimisations, and leaving them out keeps every frame a self-contained
    /// pass-through message.
    /// </summary>
    Default =
        Mandatory |
        Published |
        DistMonitor |
        DistMonitorName |
        SmallAtomTags |
        UnicodeIo |
        SendSender |
        Alias |
        Spawn
}
