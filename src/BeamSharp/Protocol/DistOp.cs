namespace BeamSharp.Protocol;

/// <summary>
/// Control-message operation codes. Every distribution frame carries a control tuple whose first
/// element is one of these.
/// </summary>
public enum DistOp
{
    Link = 1,
    Send = 2,
    Exit = 3,
    UnlinkOld = 4,
    NodeLink = 5,
    RegSend = 6,
    GroupLeader = 7,
    Exit2 = 8,

    SendTt = 12,
    ExitTt = 13,
    RegSendTt = 16,
    Exit2Tt = 18,

    MonitorP = 19,
    DemonitorP = 20,
    MonitorPExit = 21,

    SendSender = 22,
    SendSenderTt = 23,

    PayloadExit = 24,
    PayloadExitTt = 25,
    PayloadExit2 = 26,
    PayloadExit2Tt = 27,
    PayloadMonitorPExit = 28,

    SpawnRequest = 29,
    SpawnRequestTt = 30,
    SpawnReply = 31,
    SpawnReplyTt = 32,

    AliasSend = 33,
    AliasSendTt = 34,

    UnlinkId = 35,
    UnlinkIdAck = 36
}
