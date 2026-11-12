using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>Numbers every peer must agree on. Changing any wire format means bumping <see cref="Version"/>.</summary>
    public static class NetProtocol
    {
        /// <summary>
        /// 2: proximity voice chat (the Voice stream message) in one line of builds, suit colour (in the settings
        /// byte and the Welcome roster) in another. 3: both. 4: everyone grabs (item ownership in use) and the
        /// helmet lamp's HeadLampOn avatar bit (an older build masks it off). 6 (0.13.0): the co-op revive (PlayerDowned,
        /// PlayerRevived, ReviveProgress, ReviveHold, the downed crew in WorldState, downs and left-behind in RunEnded),
        /// crew shoves (CrewPush) and the Mimic in co-op (the creature's voice as Voice slot 7, and the one-byte "voice
        /// off" notice; see VoiceMsg). (5 was only ever used by unreleased branch builds.) Each version refuses every other.
        /// </summary>
        public const byte Version = 6;
        /// <summary>The host's UDP port. Clients bind an ephemeral port.</summary>
        public const ushort HostPort = 47800;
        public const int MaxDatagram = 1200;
        public const int MaxMessageBytes = ReliableChannel.MaxMessageBytes;
        /// <summary>Marks a connectionless datagram as ours ("TRSH" as bytes on the wire).</summary>
        public const uint Magic = 0x48535254u;

        public const int MaxPlayers = 4;
        public const byte HostSlot = 0;
        /// <summary>"Nobody" in a slot field.</summary>
        public const byte NoSlot = 255;

        /// <summary>20 Hz send tick.</summary>
        public const double SendInterval = 0.05;
        /// <summary>Host marks a client Away after this much silence; a client shows HOST PAUSED after the same.</summary>
        public const double AwaySeconds = 1.5;
        /// <summary>
        /// A peer heard again after at least this much silence gets its unacked reliable messages re-sent at once
        /// (ReliableChannel.ResetBackoff) instead of on their backed-off schedule.
        /// </summary>
        public const double HeardAgainSeconds = 0.25;
        /// <summary>Host keeps an Away client's slot this long, then drops it.</summary>
        public const double SlotHoldSeconds = 45.0;
        /// <summary>A client gives up on a silent host after this long.</summary>
        public const double HostTimeoutSeconds = 60.0;
        /// <summary>A slot handed out by Accept is freed if the client never sends a session datagram within this.</summary>
        public const double PendingSeconds = 5.0;
        public const double HelloInterval = 0.25;
        public const double ConnectTimeoutSeconds = 5.0;
        /// <summary>An item owned by a client but not held is taken back by the host after this long without a handoff.</summary>
        public const double ItemBackstopSeconds = 3.0;
        /// <summary>A released item whose flight is not handed back within this long is taken back (longer than the 3 s flight window).</summary>
        public const double ItemFlightBackstopSeconds = 4.5;

        public const int MaxNoisePerSecond = 8;
        public const int MaxItemPosesPerDatagram = 12;
        /// <summary>Most items one RunStart can carry and stay under the 1000-byte message cap (31 + 5 n).</summary>
        public const int MaxRunItems = 192;
        public const int MaxDoors = 16;
        public const int MaxPanels = 16;
        /// <summary>Cut points of every panel together (the weld bitset in WorldState holds this many bits).</summary>
        public const int MaxCutPoints = 1024;
        public const int MaxWires = 32;

        public static bool IsValidSlot(byte slot) => slot < MaxPlayers;
    }

    /// <summary>
    /// Message type ids. Ids below <see cref="FirstReliable"/> are connectionless or unreliable stream messages
    /// (their length is known from their content); ids from <see cref="FirstReliable"/> up travel as
    /// [type][relSeq u16][length u16][payload] through <see cref="ReliableChannel"/>.
    /// </summary>
    public static class NetMsg
    {
        /// <summary>
        /// The last byte of every session datagram. A datagram that does not end with it exactly where the last
        /// message ends was cut short and is dropped whole (so nothing in it is ever acknowledged by mistake).
        /// </summary>
        public const byte End = 0;

        // connectionless (datagram connId 0)
        public const byte Probe = 1;
        public const byte HostInfo = 2;
        public const byte Hello = 3;
        public const byte Accept = 4;
        public const byte Refuse = 5;
        public const byte Gone = 6;
        public const byte Quit = 7;

        // unreliable streams
        public const byte AvatarState = 16;   // client -> host
        public const byte RemoteAvatar = 17;  // host -> others: slot + AvatarState
        public const byte CreatureState = 18; // host -> clients
        public const byte ItemPoses = 19;     // owner -> host -> others, up to 12 entries
        public const byte Clock = 20;         // host -> clients
        public const byte Noise = 21;         // client -> host
        public const byte Voice = 22;         // client -> host -> the other clients, host -> clients: 1-2 encoded 20 ms voice frames

        public const byte FirstReliable = 64;

        // session
        public const byte Welcome = 64;
        public const byte RosterChanged = 65;
        public const byte WorldState = 66;
        public const byte SettingsChanged = 67;
        public const byte PauseNotice = 68;
        public const byte HostPause = 69;
        public const byte Bye = 70;
        public const byte SessionClosed = 71;

        // run
        public const byte StartRequest = 80;
        public const byte ExtractRequest = 81;
        public const byte RunStart = 82;
        public const byte ExtractRefused = 83;
        public const byte RunEnded = 84;
        public const byte ReturnedToHub = 85;

        // items
        public const byte GrabRequest = 96;
        public const byte ItemOwner = 97;
        public const byte ItemCollected = 98;
        public const byte ItemReleased = 99;
        public const byte ItemThrown = 100;
        public const byte ItemHandoff = 101;
        public const byte ItemRest = 102;
        public const byte ItemDamageReport = 103;
        public const byte ItemValue = 104;
        public const byte ItemDeposited = 105;
        public const byte ItemRecovered = 106;

        // world
        public const byte DoorRequest = 112;
        public const byte DoorState = 113;
        public const byte PointCutReport = 114;
        public const byte PointCut = 115;
        public const byte PanelOpened = 116;

        // creature and hazards
        public const byte CreatureStateChanged = 128;
        public const byte Tranquilized = 129;
        public const byte CatchStarted = 130;
        public const byte TranqHitReport = 131;
        public const byte TranqTracer = 132;
        public const byte BlackoutStarted = 133;
        public const byte BlackoutEnded = 134;
        public const byte WireReport = 135;
        public const byte WireChanged = 136;

        // co-op revive (protocol 5)
        public const byte PlayerDowned = 137;   // host -> all
        public const byte PlayerRevived = 138;  // host -> all
        public const byte ReviveProgress = 139; // host -> all, at most 5 a second per downed player while the ring moves
        public const byte ReviveHold = 140;     // client -> host: my hand is on (or off) this crew member's beacon

        public static bool IsReliable(byte type) => type >= FirstReliable;

        /// <summary>Every message id, for tests and tools.</summary>
        public static readonly byte[] All =
        {
            Probe, HostInfo, Hello, Accept, Refuse, Gone, Quit,
            AvatarState, RemoteAvatar, CreatureState, ItemPoses, Clock, Noise, Voice,
            Welcome, RosterChanged, WorldState, SettingsChanged, PauseNotice, HostPause, Bye, SessionClosed,
            StartRequest, ExtractRequest, RunStart, ExtractRefused, RunEnded, ReturnedToHub,
            GrabRequest, ItemOwner, ItemCollected, ItemReleased, ItemThrown, ItemHandoff, ItemRest, ItemDamageReport, ItemValue, ItemDeposited, ItemRecovered,
            DoorRequest, DoorState, PointCutReport, PointCut, PanelOpened,
            CreatureStateChanged, Tranquilized, CatchStarted, TranqHitReport, TranqTracer, BlackoutStarted, BlackoutEnded, WireReport, WireChanged,
            PlayerDowned, PlayerRevived, ReviveProgress, ReviveHold,
        };

        /// <summary>
        /// Size cap in bytes for a message's payload (connectionless: the whole datagram). -1 for an unknown id.
        /// NetCodecTests pins every codec to its cap and every cap to the 1000-byte message limit.
        /// </summary>
        public static int MaxSize(byte type)
        {
            switch (type)
            {
                case Probe: return ProbeMsg.Size;
                case HostInfo: return HostInfoMsg.Size;
                case Hello: return HelloMsg.Size;
                case Accept: return AcceptMsg.Size;
                case Refuse: return RefuseMsg.Size;
                case Gone: return GoneMsg.Size;
                case Quit: return QuitMsg.Size;
                case AvatarState: return AvatarSnapshot.Size;
                case RemoteAvatar: return RemoteAvatarMsg.MaxSize;
                case CreatureState: return CreatureSnapshot.Size;
                case ItemPoses: return ItemPoseBatch.MaxSize;
                case Clock: return ClockMsg.MaxSize;
                case Noise: return NoiseMsg.MaxSize;
                case Voice: return VoiceMsg.MaxSize;
                case Welcome: return WelcomeMsg.MaxSize;
                case RosterChanged: return RosterChangedMsg.MaxSize;
                case WorldState: return WorldStateMsg.MaxSize;
                case SettingsChanged: return SettingsChangedMsg.MaxSize;
                case PauseNotice: return PauseNoticeMsg.MaxSize;
                case HostPause: return HostPauseMsg.MaxSize;
                case Bye: return 0;
                case SessionClosed: return SessionClosedMsg.MaxSize;
                case StartRequest: return 0;
                case ExtractRequest: return 0;
                case RunStart: return RunStartCodec.MaxSize;
                case ExtractRefused: return ExtractRefusedMsg.MaxSize;
                case RunEnded: return RunEndedMsg.MaxSize;
                case ReturnedToHub: return 0;
                case GrabRequest: return GrabRequestMsg.MaxSize;
                case ItemOwner: return ItemOwnerMsg.MaxSize;
                case ItemCollected: return ItemCollectedMsg.MaxSize;
                case ItemReleased: return ItemReleasedMsg.MaxSize;
                case ItemThrown: return ItemThrownMsg.MaxSize;
                case ItemHandoff: return ItemHandoffMsg.MaxSize;
                case ItemRest: return ItemRestMsg.MaxSize;
                case ItemDamageReport: return ItemDamageReportMsg.MaxSize;
                case ItemValue: return ItemValueMsg.MaxSize;
                case ItemDeposited: return ItemDepositedMsg.MaxSize;
                case ItemRecovered: return ItemRecoveredMsg.MaxSize;
                case DoorRequest: return DoorRequestMsg.MaxSize;
                case DoorState: return DoorStateMsg.MaxSize;
                case PointCutReport: return PointCutReportMsg.MaxSize;
                case PointCut: return PointCutMsg.MaxSize;
                case PanelOpened: return PanelOpenedMsg.MaxSize;
                case CreatureStateChanged: return CreatureStateChangedMsg.MaxSize;
                case Tranquilized: return TranquilizedMsg.MaxSize;
                case CatchStarted: return CatchStartedMsg.MaxSize;
                case TranqHitReport: return TranqHitReportMsg.MaxSize;
                case TranqTracer: return TranqTracerMsg.MaxSize;
                case BlackoutStarted: return BlackoutStartedMsg.MaxSize;
                case BlackoutEnded: return BlackoutEndedMsg.MaxSize;
                case WireReport: return WireReportMsg.MaxSize;
                case WireChanged: return WireChangedMsg.MaxSize;
                case PlayerDowned: return PlayerDownedMsg.MaxSize;
                case PlayerRevived: return PlayerRevivedMsg.MaxSize;
                case ReviveProgress: return ReviveProgressMsg.MaxSize;
                case ReviveHold: return ReviveHoldMsg.MaxSize;
                default: return -1;
            }
        }

        public static string Name(byte type)
        {
            switch (type)
            {
                case Probe: return "Probe";
                case HostInfo: return "HostInfo";
                case Hello: return "Hello";
                case Accept: return "Accept";
                case Refuse: return "Refuse";
                case Gone: return "Gone";
                case Quit: return "Quit";
                case AvatarState: return "AvatarState";
                case RemoteAvatar: return "RemoteAvatar";
                case CreatureState: return "CreatureState";
                case ItemPoses: return "ItemPoses";
                case Clock: return "Clock";
                case Noise: return "Noise";
                case Voice: return "Voice";
                case Welcome: return "Welcome";
                case RosterChanged: return "RosterChanged";
                case WorldState: return "WorldState";
                case SettingsChanged: return "SettingsChanged";
                case PauseNotice: return "PauseNotice";
                case HostPause: return "HostPause";
                case Bye: return "Bye";
                case SessionClosed: return "SessionClosed";
                case StartRequest: return "StartRequest";
                case ExtractRequest: return "ExtractRequest";
                case RunStart: return "RunStart";
                case ExtractRefused: return "ExtractRefused";
                case RunEnded: return "RunEnded";
                case ReturnedToHub: return "ReturnedToHub";
                case GrabRequest: return "GrabRequest";
                case ItemOwner: return "ItemOwner";
                case ItemCollected: return "ItemCollected";
                case ItemReleased: return "ItemReleased";
                case ItemThrown: return "ItemThrown";
                case ItemHandoff: return "ItemHandoff";
                case ItemRest: return "ItemRest";
                case ItemDamageReport: return "ItemDamageReport";
                case ItemValue: return "ItemValue";
                case ItemDeposited: return "ItemDeposited";
                case ItemRecovered: return "ItemRecovered";
                case DoorRequest: return "DoorRequest";
                case DoorState: return "DoorState";
                case PointCutReport: return "PointCutReport";
                case PointCut: return "PointCut";
                case PanelOpened: return "PanelOpened";
                case CreatureStateChanged: return "CreatureStateChanged";
                case Tranquilized: return "Tranquilized";
                case CatchStarted: return "CatchStarted";
                case TranqHitReport: return "TranqHitReport";
                case TranqTracer: return "TranqTracer";
                case BlackoutStarted: return "BlackoutStarted";
                case BlackoutEnded: return "BlackoutEnded";
                case WireReport: return "WireReport";
                case WireChanged: return "WireChanged";
                case PlayerDowned: return "PlayerDowned";
                case PlayerRevived: return "PlayerRevived";
                case ReviveProgress: return "ReviveProgress";
                case ReviveHold: return "ReviveHold";
                default: return "Unknown(" + type + ")";
            }
        }
    }

    // ====================================================================== shared enums

    /// <summary>Why a host turned a Hello down. Values are on the wire.</summary>
    public enum RefuseReason : byte
    {
        None = 0,
        Full = 1,
        VersionMismatch = 2,
        ContentMismatch = 3,
        ContractInProgress = 4,
    }

    /// <summary>What a RosterChanged message says about a slot. Values are on the wire.</summary>
    public enum RosterChange : byte
    {
        Left = 0,
        Joined = 1,
        /// <summary>The player is paused, untracked or silent: not targeted, not counted for oxygen or extraction.</summary>
        Away = 2,
        /// <summary>The player is no longer Away.</summary>
        Back = 3,
        /// <summary>Only the settings changed: handedness or suit colour.</summary>
        Settings = 4,
    }

    /// <summary>What happened to a web tripwire. Values are on the wire.</summary>
    public enum NetWireChange : byte
    {
        Snapped = 0,
        Burnt = 1,
    }

    /// <summary>Why a host says the session is over for a client. Values are on the wire (SessionClosed, Gone).</summary>
    public enum SessionCloseReason : byte
    {
        HostLeft = 0,
        Dropped = 1,
    }

    // ====================================================================== datagram header

    /// <summary>
    /// The 16-byte header on every session datagram: protocol u8, connId u16, senderSlot u8, seq u16, ack u16,
    /// ackBits u32, timeMs u32. After it come the messages and one closing <see cref="NetMsg.End"/> byte. timeMs is the sender's session clock in milliseconds: host time on host -> client
    /// datagrams (clients stamp snapshots with it), the client's own clock the other way.
    /// A connId of 0 marks a connectionless datagram, which has its own short frame (see <see cref="Connectionless"/>).
    /// </summary>
    public struct DatagramHeader
    {
        public const int Size = 16;

        public byte protocol;
        public ushort connId;
        public byte senderSlot;
        public ushort seq;
        public ushort ack;
        public uint ackBits;
        public uint timeMs;

        public void Write(ref NetWriter w)
        {
            w.U8(protocol);
            w.U16(connId);
            w.U8(senderSlot);
            w.U16(seq);
            w.U16(ack);
            w.U32(ackBits);
            w.U32(timeMs);
        }

        public static bool Read(ref NetReader r, out DatagramHeader h)
        {
            h = new DatagramHeader();
            h.protocol = r.U8();
            h.connId = r.U16();
            h.senderSlot = r.U8();
            h.seq = r.U16();
            h.ack = r.U16();
            h.ackBits = r.U32();
            h.timeMs = r.U32();
            return !r.Failed;
        }

        public static uint ToMs(double seconds)
        {
            if (!(seconds > 0)) return 0u;
            return (uint)((ulong)(seconds * 1000.0 + 0.5) & 0xFFFFFFFFul);
        }

        public static double ToSeconds(uint ms) => ms * 0.001;
    }

    // ====================================================================== connectionless

    /// <summary>
    /// Frame of a connectionless datagram: protocol u8, connId u16 (always 0), type u8, magic u32, then the
    /// message. This frame never changes between protocol versions, so builds of different versions can still
    /// tell each other "version mismatch".
    /// </summary>
    public static class Connectionless
    {
        public const int FrameSize = 8;

        public static void WriteFrame(ref NetWriter w, byte protocol, byte type)
        {
            w.U8(protocol);
            w.U16(0);
            w.U8(type);
            w.U32(NetProtocol.Magic);
        }

        /// <summary>True when the datagram starts with a valid connectionless frame. The reader is left at the message.</summary>
        public static bool TryReadFrame(ref NetReader r, out byte protocol, out byte type)
        {
            protocol = r.U8();
            ushort connId = r.U16();
            type = r.U8();
            uint magic = r.U32();
            return !r.Failed && connId == 0 && magic == NetProtocol.Magic;
        }

        /// <summary>Peeks the connId of any datagram (0 = connectionless). False if it is too short to tell.</summary>
        public static bool TryPeekConnId(byte[] data, int length, out ushort connId)
        {
            connId = 0;
            if (data == null || length < 3 || data.Length < 3) return false;
            connId = (ushort)(data[1] | (data[2] << 8));
            return true;
        }
    }

    /// <summary>"Is there a tug here?" 12 bytes. Client -> broadcast and unicast sweep.</summary>
    public struct ProbeMsg
    {
        public const int Size = Connectionless.FrameSize + 4;
        public byte protocol;
        public uint nonce;

        public void WriteDatagram(ref NetWriter w)
        {
            Connectionless.WriteFrame(ref w, protocol, NetMsg.Probe);
            w.U32(nonce);
        }

        public static bool Read(ref NetReader r, byte protocol, out ProbeMsg m)
        {
            m = new ProbeMsg { protocol = protocol, nonce = r.U32() };
            return !r.Failed;
        }
    }

    /// <summary>A host's answer to a Probe, unicast. 20 bytes.</summary>
    public struct HostInfoMsg
    {
        public const int Size = Connectionless.FrameSize + 12;
        public byte protocol;
        public uint nonce;
        public uint contentHash;
        public ushort tugNumber;
        public byte playerCount;
        public MissionPhase phase;

        public void WriteDatagram(ref NetWriter w)
        {
            Connectionless.WriteFrame(ref w, protocol, NetMsg.HostInfo);
            w.U32(nonce);
            w.U32(contentHash);
            w.U16(tugNumber);
            w.U8(playerCount);
            w.U8((byte)phase);
        }

        public static bool Read(ref NetReader r, byte protocol, out HostInfoMsg m)
        {
            m = new HostInfoMsg { protocol = protocol };
            m.nonce = r.U32();
            m.contentHash = r.U32();
            m.tugNumber = r.U16();
            m.playerCount = r.U8();
            byte phase = r.U8();
            if (phase > (byte)MissionPhase.Results || m.playerCount > NetProtocol.MaxPlayers) r.Fail();
            m.phase = (MissionPhase)phase;
            return !r.Failed;
        }
    }

    /// <summary>A client asking to join. 17 bytes. Re-sent every 250 ms until Accept or Refuse.</summary>
    public struct HelloMsg
    {
        public const int Size = Connectionless.FrameSize + 9;
        public byte protocol;
        public uint contentHash;
        public uint clientNonce;
        /// <summary>0 = right, 1 = left (SaveData.handedness).</summary>
        public byte handedness;
        /// <summary>0 = slot colour, 1..12 = suit palette (<see cref="SuitLook"/>); shares the handedness byte.</summary>
        public byte suitColour;

        public void WriteDatagram(ref NetWriter w)
        {
            Connectionless.WriteFrame(ref w, protocol, NetMsg.Hello);
            w.U32(contentHash);
            w.U32(clientNonce);
            w.U8(SuitLook.Pack(handedness, suitColour));
        }

        public static bool Read(ref NetReader r, byte protocol, out HelloMsg m)
        {
            m = new HelloMsg { protocol = protocol };
            m.contentHash = r.U32();
            m.clientNonce = r.U32();
            byte look = r.U8();
            m.handedness = SuitLook.HandednessOf(look);
            m.suitColour = SuitLook.ColourOf(look);
            return !r.Failed;
        }
    }

    /// <summary>Host -> client: you are in. 15 bytes.</summary>
    public struct AcceptMsg
    {
        public const int Size = Connectionless.FrameSize + 7;
        public byte protocol;
        public uint clientNonce;
        public ushort connId;
        public byte slot;

        public void WriteDatagram(ref NetWriter w)
        {
            Connectionless.WriteFrame(ref w, protocol, NetMsg.Accept);
            w.U32(clientNonce);
            w.U16(connId);
            w.U8(slot);
        }

        public static bool Read(ref NetReader r, byte protocol, out AcceptMsg m)
        {
            m = new AcceptMsg { protocol = protocol };
            m.clientNonce = r.U32();
            m.connId = r.U16();
            m.slot = r.U8();
            if (m.connId == 0 || m.slot == 0 || m.slot >= NetProtocol.MaxPlayers) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> client: no. 13 bytes.</summary>
    public struct RefuseMsg
    {
        public const int Size = Connectionless.FrameSize + 5;
        public byte protocol;
        public uint clientNonce;
        public RefuseReason reason;

        public void WriteDatagram(ref NetWriter w)
        {
            Connectionless.WriteFrame(ref w, protocol, NetMsg.Refuse);
            w.U32(clientNonce);
            w.U8((byte)reason);
        }

        public static bool Read(ref NetReader r, byte protocol, out RefuseMsg m)
        {
            m = new RefuseMsg { protocol = protocol };
            m.clientNonce = r.U32();
            byte reason = r.U8();
            if (reason == 0 || reason > (byte)RefuseReason.ContractInProgress) r.Fail();
            m.reason = (RefuseReason)reason;
            return !r.Failed;
        }
    }

    /// <summary>
    /// Host -> a client, outside the reliable stream: "that connection is over". Sent to an address that keeps
    /// sending session datagrams for a connection the host no longer has (reason Dropped: a headset that wakes
    /// after the 45 s drop leaves at once instead of waiting 60 s), and to every client when the host closes the
    /// session (reason HostLeft: the goodbye arrives even if the reliable window is full). 11 bytes.
    /// </summary>
    public struct GoneMsg
    {
        public const int Size = Connectionless.FrameSize + 3;
        public byte protocol;
        public ushort connId;
        public SessionCloseReason reason;

        public void WriteDatagram(ref NetWriter w)
        {
            Connectionless.WriteFrame(ref w, protocol, NetMsg.Gone);
            w.U16(connId);
            w.U8((byte)reason);
        }

        public static bool Read(ref NetReader r, byte protocol, out GoneMsg m)
        {
            m = new GoneMsg { protocol = protocol, connId = r.U16() };
            byte reason = r.U8();
            m.reason = reason <= (byte)SessionCloseReason.Dropped ? (SessionCloseReason)reason : SessionCloseReason.Dropped;
            return !r.Failed;
        }
    }

    /// <summary>
    /// Client -> host, outside the reliable stream: "I am leaving" (or "I gave up on this connection"). The
    /// connectionless twin of the reliable Bye, sent with it, so the host frees the slot at once even when the
    /// reliable window is full or the Bye datagrams are lost. The host takes it only from the address and
    /// connection id of a slot it has. 10 bytes.
    /// </summary>
    public struct QuitMsg
    {
        public const int Size = Connectionless.FrameSize + 2;
        public byte protocol;
        public ushort connId;

        public void WriteDatagram(ref NetWriter w)
        {
            Connectionless.WriteFrame(ref w, protocol, NetMsg.Quit);
            w.U16(connId);
        }

        public static bool Read(ref NetReader r, byte protocol, out QuitMsg m)
        {
            m = new QuitMsg { protocol = protocol, connId = r.U16() };
            return !r.Failed;
        }
    }

    // ====================================================================== streams (unreliable)

    /// <summary>Host -> the other clients: one player's avatar. Slot + the 35-byte AvatarState.</summary>
    public struct RemoteAvatarMsg
    {
        public const int MaxSize = 1 + AvatarSnapshot.Size;
        public byte slot;
        public AvatarSnapshot avatar;

        public void Write(ref NetWriter w)
        {
            w.U8(slot);
            avatar.Write(ref w);
        }

        public static bool Read(ref NetReader r, out RemoteAvatarMsg m)
        {
            m = new RemoteAvatarMsg();
            m.slot = r.U8();
            if (m.slot >= NetProtocol.MaxPlayers) r.Fail();
            return AvatarSnapshot.Read(ref r, out m.avatar) && !r.Failed;
        }
    }

    /// <summary>One salvage item's pose in the item stream. 12 bytes: id, position (cm), rotation, epoch (6 bits) + held + atRest.</summary>
    public struct ItemPoseEntry
    {
        public const int Size = 12;
        public const byte EpochMask = 63;

        public byte itemId;
        public NetVec3 position;
        public NetQuat rotation;
        /// <summary>Low 6 bits of the item's ownership epoch. A pose whose epoch is not the current one is dropped.</summary>
        public byte epoch;
        public bool held;
        public bool atRest;

        public void Write(ref NetWriter w)
        {
            w.U8(itemId);
            Quant.WritePosCm(ref w, position);
            Quant.WriteQuat(ref w, rotation);
            w.U8((byte)((epoch & EpochMask) | (held ? 64 : 0) | (atRest ? 128 : 0)));
        }

        public static bool Read(ref NetReader r, out ItemPoseEntry e)
        {
            e = new ItemPoseEntry();
            e.itemId = r.U8();
            e.position = Quant.ReadPosCm(ref r);
            e.rotation = Quant.ReadQuat(ref r);
            byte packed = r.U8();
            e.epoch = (byte)(packed & EpochMask);
            e.held = (packed & 64) != 0;
            e.atRest = (packed & 128) != 0;
            return !r.Failed;
        }
    }

    /// <summary>Up to 12 item poses in one stream message. The array is owned by the caller and reused.</summary>
    public struct ItemPoseBatch
    {
        public const int MaxEntries = NetProtocol.MaxItemPosesPerDatagram;
        public const int MaxSize = 1 + MaxEntries * ItemPoseEntry.Size;

        public int count;
        public ItemPoseEntry[] entries;

        public void Write(ref NetWriter w)
        {
            int n = entries == null ? 0 : count < 0 ? 0 : count > MaxEntries ? MaxEntries : count;
            if (entries != null && n > entries.Length) n = entries.Length;
            w.U8((byte)n);
            for (int i = 0; i < n; i++) entries[i].Write(ref w);
        }

        /// <summary>Reads into <paramref name="m"/>, reusing its array when it is big enough.</summary>
        public static bool Read(ref NetReader r, ref ItemPoseBatch m)
        {
            int n = r.U8();
            m.count = 0;
            if (n > MaxEntries) { r.Fail(); return false; }
            if (m.entries == null || m.entries.Length < MaxEntries) m.entries = new ItemPoseEntry[MaxEntries];
            for (int i = 0; i < n; i++)
                if (!ItemPoseEntry.Read(ref r, out m.entries[i])) return false;
            if (r.Failed) return false;
            m.count = n;
            return true;
        }
    }

    public static class ClockFlags
    {
        public const byte HazardsPaused = 1;
    }

    /// <summary>Host -> clients at 2 Hz: mission time and the shared oxygen tank, both in tenths of a second.</summary>
    public struct ClockMsg
    {
        public const int MaxSize = 5;
        public const float MaxSeconds = 6553.5f;
        public float elapsedSeconds;
        public float oxygenRemainingSeconds;
        /// <summary><see cref="ClockFlags"/>.</summary>
        public byte flags;

        static ushort Tenths(float seconds)
        {
            if (!(seconds > 0f)) return 0;
            if (seconds >= MaxSeconds) return 65535;
            return (ushort)(seconds * 10f + 0.5f);
        }

        public void Write(ref NetWriter w)
        {
            w.U16(Tenths(elapsedSeconds));
            w.U16(Tenths(oxygenRemainingSeconds));
            w.U8(flags);
        }

        public static bool Read(ref NetReader r, out ClockMsg m)
        {
            m = new ClockMsg();
            m.elapsedSeconds = r.U16() * 0.1f;
            m.oxygenRemainingSeconds = r.U16() * 0.1f;
            m.flags = r.U8();
            return !r.Failed;
        }
    }

    /// <summary>
    /// Client -> host: a gameplay noise made by the sender. The host stamps the source with the sender's slot,
    /// clamps the loudness to the kind's maximum and allows 8 per second per client.
    /// </summary>
    public struct NoiseMsg
    {
        public const int MaxSize = 8;
        public const float MaxLoudness = 2.55f;
        public NetVec3 position;
        /// <summary>Hundredths on the wire, 0..2.55.</summary>
        public float loudness;
        public NoiseKind kind;

        public void Write(ref NetWriter w)
        {
            Quant.WritePosCm(ref w, position);
            float l = loudness;
            w.U8(!(l > 0f) ? (byte)0 : l >= MaxLoudness ? (byte)255 : (byte)(l * 100f + 0.5f));
            w.U8((byte)kind);
        }

        public static bool Read(ref NetReader r, out NoiseMsg m)
        {
            m = new NoiseMsg();
            m.position = Quant.ReadPosCm(ref r);
            m.loudness = r.U8() * 0.01f;
            byte kind = r.U8();
            if (kind > (byte)NoiseKind.Web) r.Fail();
            m.kind = (NoiseKind)kind;
            return !r.Failed;
        }
    }

    // ====================================================================== session (reliable)

    /// <summary>
    /// Latest-value snapshot of the wreck's switchable state. Idempotent. Sent inside Welcome and after RunStart
    /// (and kept so a mid-contract join can be added later). Done welds are a length-prefixed bitset over every
    /// cut point of every panel: bit = NetIdTable.CutPointOffset(panel) + index in Cuttable.cutPoints. The byte
    /// array is reused between reads.
    /// Protocol 5 adds the crew's revive state at the end, fixed size (<see cref="CrewSize"/>): downedMask u8, how many
    /// times each slot was downed (4 x u8), and where each slot lies (4 x PosCm, zero for one who is up). Who is down
    /// is world state: a late joiner would see the downed suits where they lie.
    /// </summary>
    public struct WorldStateMsg
    {
        public const int MaxWeldBytes = NetProtocol.MaxCutPoints / 8;
        public const int CrewSize = 1 + NetProtocol.MaxPlayers + NetProtocol.MaxPlayers * 6;
        public const int MaxSize = 2 + 2 + 1 + MaxWeldBytes + 4 + 4 + CrewSize;

        public ushort doorOpenMask;
        public ushort panelOpenMask;
        public byte[] weldDone;
        public int weldByteCount;
        public uint wireSnappedMask;
        public uint wireBurntMask;
        /// <summary>Bit n = slot n is downed.</summary>
        public byte downedMask;
        /// <summary>Times downed this contract, one byte per slot (slot n in bits 8n..8n+7).</summary>
        public uint downCounts;
        public NetVec3 downedAt0, downedAt1, downedAt2, downedAt3;

        public bool IsDowned(int slot) => slot >= 0 && slot < NetProtocol.MaxPlayers && (downedMask & (1 << slot)) != 0;
        public int DownCount(int slot) => slot >= 0 && slot < NetProtocol.MaxPlayers ? (int)((downCounts >> (slot * 8)) & 0xFFu) : 0;

        public NetVec3 DownedAt(int slot)
        {
            switch (slot)
            {
                case 0: return downedAt0;
                case 1: return downedAt1;
                case 2: return downedAt2;
                case 3: return downedAt3;
                default: return NetVec3.Zero;
            }
        }

        /// <summary>Sets one slot's revive state: downed (at <paramref name="at"/>) or up, and how many times it has been downed.</summary>
        public void SetCrew(int slot, bool downed, NetVec3 at, int downs)
        {
            if (slot < 0 || slot >= NetProtocol.MaxPlayers) return;
            if (downed) downedMask |= (byte)(1 << slot);
            else downedMask &= (byte)~(1 << slot);
            if (!downed) at = NetVec3.Zero;
            switch (slot)
            {
                case 0: downedAt0 = at; break;
                case 1: downedAt1 = at; break;
                case 2: downedAt2 = at; break;
                default: downedAt3 = at; break;
            }
            uint count = (uint)(downs < 0 ? 0 : downs > 255 ? 255 : downs);
            downCounts = (downCounts & ~(0xFFu << (slot * 8))) | (count << (slot * 8));
        }

        public bool IsWeldDone(int bit)
        {
            int at = bit >> 3;
            return bit >= 0 && weldDone != null && at < weldByteCount && at < weldDone.Length && (weldDone[at] & (1 << (bit & 7))) != 0;
        }

        /// <summary>Marks one weld done, growing the bitset as needed. Out-of-range bits are ignored.</summary>
        public void SetWeldDone(int bit)
        {
            if (bit < 0 || bit >= NetProtocol.MaxCutPoints) return;
            int at = bit >> 3;
            if (weldDone == null || weldDone.Length < MaxWeldBytes)
            {
                var grown = new byte[MaxWeldBytes];
                if (weldDone != null) Buffer.BlockCopy(weldDone, 0, grown, 0, Math.Min(weldByteCount, weldDone.Length));
                weldDone = grown;
            }
            while (weldByteCount <= at) weldDone[weldByteCount++] = 0;
            weldDone[at] |= (byte)(1 << (bit & 7));
        }

        /// <summary>Copies another state into this one, reusing this one's weld array.</summary>
        public void CopyFrom(in WorldStateMsg other)
        {
            doorOpenMask = other.doorOpenMask;
            panelOpenMask = other.panelOpenMask;
            wireSnappedMask = other.wireSnappedMask;
            wireBurntMask = other.wireBurntMask;
            downedMask = other.downedMask;
            downCounts = other.downCounts;
            downedAt0 = other.downedAt0;
            downedAt1 = other.downedAt1;
            downedAt2 = other.downedAt2;
            downedAt3 = other.downedAt3;
            weldByteCount = 0;
            int n = other.weldDone == null ? 0 : Math.Min(Math.Min(other.weldByteCount, other.weldDone.Length), MaxWeldBytes);
            if (n <= 0) return;
            if (weldDone == null || weldDone.Length < MaxWeldBytes) weldDone = new byte[MaxWeldBytes];
            Buffer.BlockCopy(other.weldDone, 0, weldDone, 0, n);
            weldByteCount = n;
        }

        public void Clear()
        {
            doorOpenMask = 0;
            panelOpenMask = 0;
            weldByteCount = 0;
            wireSnappedMask = 0u;
            wireBurntMask = 0u;
            downedMask = 0;
            downCounts = 0u;
            downedAt0 = downedAt1 = downedAt2 = downedAt3 = NetVec3.Zero;
        }

        public void Write(ref NetWriter w)
        {
            w.U16(doorOpenMask);
            w.U16(panelOpenMask);
            int n = weldDone == null ? 0 : weldByteCount < 0 ? 0 : weldByteCount > MaxWeldBytes ? MaxWeldBytes : weldByteCount;
            if (weldDone != null && n > weldDone.Length) n = weldDone.Length;
            w.U8((byte)n);
            if (n > 0) w.Bytes(weldDone, 0, n);
            w.U32(wireSnappedMask);
            w.U32(wireBurntMask);
            w.U8((byte)(downedMask & 15));
            w.U32(downCounts);
            for (int i = 0; i < NetProtocol.MaxPlayers; i++) Quant.WritePosCm(ref w, IsDowned(i) ? DownedAt(i) : NetVec3.Zero);
        }

        /// <summary>Reads into <paramref name="m"/>, reusing its weld array when it is big enough.</summary>
        public static bool Read(ref NetReader r, ref WorldStateMsg m)
        {
            m.doorOpenMask = r.U16();
            m.panelOpenMask = r.U16();
            int n = r.U8();
            m.weldByteCount = 0;
            if (n > MaxWeldBytes) { r.Fail(); return false; }
            if (m.weldDone == null || m.weldDone.Length < MaxWeldBytes) m.weldDone = new byte[MaxWeldBytes];
            r.Bytes(m.weldDone, 0, n);
            m.wireSnappedMask = r.U32();
            m.wireBurntMask = r.U32();
            m.downedMask = (byte)(r.U8() & 15);
            m.downCounts = r.U32();
            m.downedAt0 = Quant.ReadPosCm(ref r);
            m.downedAt1 = Quant.ReadPosCm(ref r);
            m.downedAt2 = Quant.ReadPosCm(ref r);
            m.downedAt3 = Quant.ReadPosCm(ref r);
            if (r.Failed) return false;
            m.weldByteCount = n;
            return true;
        }
    }

    /// <summary>
    /// Host -> a client that has just joined: who is aboard, what phase the host is in, and the world state.
    /// The client resets itself for the session when this arrives (MissionDirector.ResetForSession).
    /// </summary>
    public struct WelcomeMsg
    {
        public const int MaxSize = NetProtocol.MaxPlayers + 1 + WorldStateMsg.MaxSize;
        const uint k_Present = 1, k_LeftHanded = 2, k_Away = 4, k_WireBits = 0xF7; // bit 3 is spare

        /// <summary>One byte per slot: bit 0 present, bit 1 left-handed, bit 2 away, bits 4..7 suit colour (<see cref="SuitLook"/>).</summary>
        public uint roster;
        public MissionPhase phase;
        public WorldStateMsg world;

        public bool IsPresent(int slot) => slot >= 0 && slot < NetProtocol.MaxPlayers && ((roster >> (slot * 8)) & k_Present) != 0;
        public byte Handedness(int slot) => slot >= 0 && slot < NetProtocol.MaxPlayers && ((roster >> (slot * 8)) & k_LeftHanded) != 0 ? (byte)1 : (byte)0;
        public bool IsAway(int slot) => slot >= 0 && slot < NetProtocol.MaxPlayers && ((roster >> (slot * 8)) & k_Away) != 0;
        /// <summary>0 = slot colour, 1..12 = suit palette.</summary>
        public byte SuitColour(int slot) => slot >= 0 && slot < NetProtocol.MaxPlayers && IsPresent(slot) ? SuitLook.Clamp((int)((roster >> (slot * 8 + 4)) & 15u)) : SuitLook.Default;

        public void SetSlot(int slot, bool present, byte handedness, bool away) => SetSlot(slot, present, handedness, away, SuitLook.Default);

        public void SetSlot(int slot, bool present, byte handedness, bool away, byte suitColour)
        {
            if (slot < 0 || slot >= NetProtocol.MaxPlayers) return;
            uint bits = present ? k_Present | (handedness != 0 ? k_LeftHanded : 0u) | (away ? k_Away : 0u) | ((uint)SuitLook.Clamp(suitColour) << 4) : 0u;
            roster = (roster & ~(0xFFu << (slot * 8))) | (bits << (slot * 8));
        }

        public int PlayerCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < NetProtocol.MaxPlayers; i++) if (IsPresent(i)) n++;
                return n;
            }
        }

        public void Write(ref NetWriter w)
        {
            for (int i = 0; i < NetProtocol.MaxPlayers; i++) w.U8((byte)((roster >> (i * 8)) & k_WireBits));
            w.U8((byte)phase);
            world.Write(ref w);
        }

        public static bool Read(ref NetReader r, ref WelcomeMsg m)
        {
            m.roster = 0u;
            for (int i = 0; i < NetProtocol.MaxPlayers; i++) m.roster |= (r.U8() & k_WireBits) << (i * 8);
            byte phase = r.U8();
            if (phase > (byte)MissionPhase.Results) r.Fail();
            m.phase = (MissionPhase)phase;
            return WorldStateMsg.Read(ref r, ref m.world) && !r.Failed;
        }
    }

    /// <summary>Host -> all: a slot joined, left, went Away, came back, or changed handedness or suit colour.</summary>
    public struct RosterChangedMsg
    {
        public const int MaxSize = 3;
        public byte slot;
        public RosterChange change;
        public byte handedness;
        /// <summary>0 = slot colour, 1..12 = suit palette; shares the handedness byte.</summary>
        public byte suitColour;

        public void Write(ref NetWriter w)
        {
            w.U8(slot);
            w.U8((byte)change);
            w.U8(SuitLook.Pack(handedness, suitColour));
        }

        public static bool Read(ref NetReader r, out RosterChangedMsg m)
        {
            m = new RosterChangedMsg();
            m.slot = r.U8();
            byte change = r.U8();
            byte look = r.U8();
            m.handedness = SuitLook.HandednessOf(look);
            m.suitColour = SuitLook.ColourOf(look);
            if (m.slot >= NetProtocol.MaxPlayers || change > (byte)RosterChange.Settings) r.Fail();
            m.change = (RosterChange)change;
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: my handedness or suit colour changed.</summary>
    public struct SettingsChangedMsg
    {
        public const int MaxSize = 1;
        public byte handedness;
        /// <summary>0 = slot colour, 1..12 = suit palette; shares the handedness byte.</summary>
        public byte suitColour;
        public void Write(ref NetWriter w) { w.U8(SuitLook.Pack(handedness, suitColour)); }
        public static bool Read(ref NetReader r, out SettingsChangedMsg m)
        {
            byte look = r.U8();
            m = new SettingsChangedMsg { handedness = SuitLook.HandednessOf(look), suitColour = SuitLook.ColourOf(look) };
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: my app paused / resumed (sent synchronously from OnApplicationPause).</summary>
    public struct PauseNoticeMsg
    {
        public const int MaxSize = 1;
        public bool on;
        public void Write(ref NetWriter w) { w.Bool(on); }
        public static bool Read(ref NetReader r, out PauseNoticeMsg m)
        {
            m = new PauseNoticeMsg { on = r.Bool() };
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: the host's hazard pause is on / off.</summary>
    public struct HostPauseMsg
    {
        public const int MaxSize = 1;
        public bool on;
        public void Write(ref NetWriter w) { w.Bool(on); }
        public static bool Read(ref NetReader r, out HostPauseMsg m)
        {
            m = new HostPauseMsg { on = r.Bool() };
            return !r.Failed;
        }
    }

    /// <summary>Host -> a client: the session is over for you.</summary>
    public struct SessionClosedMsg
    {
        public const int MaxSize = 1;
        public SessionCloseReason reason;
        public void Write(ref NetWriter w) { w.U8((byte)reason); }
        public static bool Read(ref NetReader r, out SessionClosedMsg m)
        {
            byte reason = r.U8();
            m = new SessionClosedMsg { reason = reason <= (byte)SessionCloseReason.Dropped ? (SessionCloseReason)reason : SessionCloseReason.Dropped };
            return !r.Failed;
        }
    }

    // ====================================================================== run (reliable)

    /// <summary>
    /// Wire codec for <see cref="RunStartParams"/> (the struct itself belongs to the game-seams code):
    /// seed i32, runIndex u16, mapIndex u8, condition u8, creatureIndex u8, quota i32, oxygenCapacitySeconds f32,
    /// bonusMultiplier f32, wireArmedMask u32, hazardFlags u8, itemCount u8, items[]{pointIndex u8,
    /// definitionIndex u8, value u16, flags u8}, planHash u32. 31 + 5 n bytes; n is capped at
    /// <see cref="NetProtocol.MaxRunItems"/> so the message stays under 1000 bytes.
    /// </summary>
    public static class RunStartCodec
    {
        public const int FixedSize = 31;
        public const int ItemSize = 5;
        public const int MaxSize = FixedSize + ItemSize * NetProtocol.MaxRunItems;

        public static int SizeOf(in RunStartParams p) => FixedSize + ItemSize * (p.items != null ? p.items.Length : 0);

        /// <summary>False (and nothing useful written) when there are more than <see cref="NetProtocol.MaxRunItems"/> items.</summary>
        public static bool Write(ref NetWriter w, in RunStartParams p)
        {
            int count = p.items != null ? p.items.Length : 0;
            if (count > NetProtocol.MaxRunItems) return false;
            w.I32(p.seed);
            w.U16(p.runIndex);
            w.U8(p.mapIndex);
            w.U8(p.condition);
            w.U8(p.creatureIndex);
            w.I32(p.quota);
            w.F32(p.oxygenCapacitySeconds);
            w.F32(p.bonusMultiplier);
            w.U32(p.wireArmedMask);
            w.U8(p.hazardFlags);
            w.U8((byte)count);
            for (int i = 0; i < count; i++)
            {
                w.U8(p.items[i].pointIndex);
                w.U8(p.items[i].definitionIndex);
                w.U16(p.items[i].value);
                w.U8(p.items[i].flags);
            }
            w.U32(p.planHash);
            return !w.Overflow;
        }

        /// <summary>Reads a RunStart. Allocates the items array (once per contract). Does not check planHash: see <see cref="IsPlanHashValid"/>.</summary>
        public static bool Read(ref NetReader r, out RunStartParams p)
        {
            p = new RunStartParams();
            p.seed = r.I32();
            p.runIndex = r.U16();
            p.mapIndex = r.U8();
            p.condition = r.U8();
            p.creatureIndex = r.U8();
            p.quota = r.I32();
            p.oxygenCapacitySeconds = r.F32();
            p.bonusMultiplier = r.F32();
            p.wireArmedMask = r.U32();
            p.hazardFlags = r.U8();
            int count = r.U8();
            if (r.Failed || count > NetProtocol.MaxRunItems || r.Remaining < count * ItemSize + 4) { r.Fail(); return false; }
            p.items = new RunItem[count];
            for (int i = 0; i < count; i++)
            {
                p.items[i].pointIndex = r.U8();
                p.items[i].definitionIndex = r.U8();
                p.items[i].value = r.U16();
                p.items[i].flags = r.U8();
            }
            p.planHash = r.U32();
            return !r.Failed;
        }

        /// <summary>True when the hash carried in the params matches their content (a mismatch means different builds or a corrupt message).</summary>
        public static bool IsPlanHashValid(in RunStartParams p) => p.planHash == p.ComputePlanHash();
    }

    /// <summary>Host -> the requester: extraction refused, these slots are not aboard (bit n = slot n).</summary>
    public struct ExtractRefusedMsg
    {
        public const int MaxSize = 1;
        public byte missingSlotsMask;
        public void Write(ref NetWriter w) { w.U8(missingSlotsMask); }
        public static bool Read(ref NetReader r, out ExtractRefusedMsg m)
        {
            m = new ExtractRefusedMsg { missingSlotsMask = (byte)(r.U8() & 15) };
            return !r.Failed;
        }
    }

    /// <summary>
    /// Host -> all: the contract ended. Mirrors <see cref="MissionResult"/> field for field, the revive penalties
    /// included (protocol 5: times downed per slot, one byte each, and the left-behind mask), so every headset banks
    /// its own share.
    /// </summary>
    public struct RunEndedMsg
    {
        public const int MaxSize = 42;
        public MissionOutcome outcome;
        public int quota;
        public int depositedValue;
        public int depositedCount;
        public int trickBonus;
        public int trickShots;
        public int bonus;
        public int payout;
        public float oxygenRemainingSeconds;
        public float elapsedSeconds;
        /// <summary>Times downed this contract, one byte per slot (slot n in bits 8n..8n+7).</summary>
        public uint crewDowns;
        /// <summary>Bit n = slot n was still downed at extraction.</summary>
        public byte leftBehindMask;

        public static RunEndedMsg From(MissionResult result)
        {
            var m = new RunEndedMsg();
            if (result == null) return m;
            m.outcome = result.Outcome;
            m.quota = result.Quota;
            m.depositedValue = result.DepositedValue;
            m.depositedCount = result.DepositedCount;
            m.trickBonus = result.TrickBonus;
            m.trickShots = result.TrickShots;
            m.bonus = result.Bonus;
            m.payout = result.Payout;
            m.oxygenRemainingSeconds = result.OxygenRemainingSeconds;
            m.elapsedSeconds = result.ElapsedSeconds;
            for (int i = 0; i < NetProtocol.MaxPlayers; i++)
            {
                int d = result.DownsOf(i);
                m.crewDowns |= (uint)(d < 0 ? 0 : d > 255 ? 255 : d) << (i * 8);
            }
            m.leftBehindMask = (byte)(result.LeftBehindMask & 15);
            return m;
        }

        /// <summary>Allocates a MissionResult (once per contract).</summary>
        public MissionResult ToResult()
        {
            return new MissionResult
            {
                Outcome = outcome,
                Quota = quota,
                DepositedValue = depositedValue,
                DepositedCount = depositedCount,
                TrickBonus = trickBonus,
                TrickShots = trickShots,
                Bonus = bonus,
                Payout = payout,
                OxygenRemainingSeconds = oxygenRemainingSeconds,
                ElapsedSeconds = elapsedSeconds,
                CrewDowns = crewDowns != 0u
                    ? new[] { (int)(crewDowns & 0xFF), (int)((crewDowns >> 8) & 0xFF), (int)((crewDowns >> 16) & 0xFF), (int)((crewDowns >> 24) & 0xFF) }
                    : null,
                LeftBehindMask = leftBehindMask & 15,
            };
        }

        public void Write(ref NetWriter w)
        {
            w.U8((byte)outcome);
            w.I32(quota);
            w.I32(depositedValue);
            w.I32(depositedCount);
            w.I32(trickBonus);
            w.I32(trickShots);
            w.I32(bonus);
            w.I32(payout);
            w.F32(oxygenRemainingSeconds);
            w.F32(elapsedSeconds);
            w.U32(crewDowns);
            w.U8((byte)(leftBehindMask & 15));
        }

        public static bool Read(ref NetReader r, out RunEndedMsg m)
        {
            m = new RunEndedMsg();
            byte outcome = r.U8();
            if (outcome > (byte)MissionOutcome.FailedCaught) r.Fail();
            m.outcome = (MissionOutcome)outcome;
            m.quota = r.I32();
            m.depositedValue = r.I32();
            m.depositedCount = r.I32();
            m.trickBonus = r.I32();
            m.trickShots = r.I32();
            m.bonus = r.I32();
            m.payout = r.I32();
            m.oxygenRemainingSeconds = r.F32();
            m.elapsedSeconds = r.F32();
            m.crewDowns = r.U32();
            m.leftBehindMask = (byte)(r.U8() & 15);
            return !r.Failed;
        }
    }

    // ====================================================================== items (reliable)

    /// <summary>Client -> host: I have just grabbed this item locally; make me its owner.</summary>
    public struct GrabRequestMsg
    {
        public const int MaxSize = 3;
        public byte itemId;
        /// <summary>The ownership epoch the client knew when it grabbed. A request with a stale epoch loses.</summary>
        public byte epochSeen;
        /// <summary>0 = left hand, 1 = right hand.</summary>
        public byte hand;

        public void Write(ref NetWriter w) { w.U8(itemId); w.U8(epochSeen); w.U8(hand); }
        public static bool Read(ref NetReader r, out GrabRequestMsg m)
        {
            m = new GrabRequestMsg { itemId = r.U8(), epochSeen = r.U8(), hand = (byte)(r.U8() & 1) };
            return !r.Failed;
        }
    }

    /// <summary>
    /// Host -> all: who simulates this item now. ownerSlot 0 is the host (free, or in the host's hand when held).
    /// Every change of owner raises the epoch; a release keeps it.
    /// </summary>
    public struct ItemOwnerMsg
    {
        public const int MaxSize = 4;
        public byte itemId;
        public byte ownerSlot;
        public byte epoch;
        public bool held;
        /// <summary>
        /// Set only on the copy sent to a requester whose GrabRequest was turned down: "this is your answer, and it
        /// is no". The other fields are the item's unchanged current state. Every GrabRequest gets exactly one
        /// answer: a broadcast naming the requester as holder, or this.
        /// </summary>
        public bool denied;

        public void Write(ref NetWriter w) { w.U8(itemId); w.U8(ownerSlot); w.U8(epoch); w.U8((byte)((held ? 1 : 0) | (denied ? 2 : 0))); }
        public static bool Read(ref NetReader r, out ItemOwnerMsg m)
        {
            m = new ItemOwnerMsg { itemId = r.U8(), ownerSlot = r.U8(), epoch = r.U8() };
            byte bits = r.U8();
            m.held = (bits & 1) != 0;
            m.denied = (bits & 2) != 0;
            if (m.ownerSlot >= NetProtocol.MaxPlayers) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: first pickup of an item (MissionSession.TryCollect).</summary>
    public struct ItemCollectedMsg
    {
        public const int MaxSize = 2;
        public byte itemId;
        public byte by;

        public void Write(ref NetWriter w) { w.U8(itemId); w.U8(by); }
        public static bool Read(ref NetReader r, out ItemCollectedMsg m)
        {
            m = new ItemCollectedMsg { itemId = r.U8(), by = r.U8() };
            if (m.by >= NetProtocol.MaxPlayers) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: I let go. Sent unconditionally from selectExited; the sender keeps simulating the flight.</summary>
    public struct ItemReleasedMsg
    {
        public const int MaxSize = 18;
        public byte itemId;
        public byte epoch;
        public NetVec3 position;
        public NetQuat rotation;
        /// <summary>Where the releasing hand was (trick-shot distance is measured from here).</summary>
        public NetVec3 releasePosition;

        public void Write(ref NetWriter w)
        {
            w.U8(itemId);
            w.U8(epoch);
            Quant.WritePosCm(ref w, position);
            Quant.WriteQuat(ref w, rotation);
            Quant.WritePosCm(ref w, releasePosition);
        }

        public static bool Read(ref NetReader r, out ItemReleasedMsg m)
        {
            m = new ItemReleasedMsg();
            m.itemId = r.U8();
            m.epoch = r.U8();
            m.position = Quant.ReadPosCm(ref r);
            m.rotation = Quant.ReadQuat(ref r);
            m.releasePosition = Quant.ReadPosCm(ref r);
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: follow-up to ItemReleased once the throw velocity is known (two FixedUpdates later).</summary>
    public struct ItemThrownMsg
    {
        public const int MaxSize = 17;
        public const float VelocityScale = 100f;
        public byte itemId;
        public byte epoch;
        /// <summary>m/s, centimetre-per-second steps, +-327 m/s.</summary>
        public NetVec3 linearVelocity;
        /// <summary>rad/s, 0.01 steps, +-327 rad/s.</summary>
        public NetVec3 angularVelocity;
        /// <summary>m/s, 0..655.</summary>
        public float throwSpeed;
        public bool wasThrown;

        public void Write(ref NetWriter w)
        {
            w.U8(itemId);
            w.U8(epoch);
            Quant.WriteVec16(ref w, linearVelocity, VelocityScale);
            Quant.WriteVec16(ref w, angularVelocity, VelocityScale);
            float s = throwSpeed;
            w.U16(!(s > 0f) ? (ushort)0 : s >= 655.35f ? (ushort)65535 : (ushort)(s * 100f + 0.5f));
            w.Bool(wasThrown);
        }

        public static bool Read(ref NetReader r, out ItemThrownMsg m)
        {
            m = new ItemThrownMsg();
            m.itemId = r.U8();
            m.epoch = r.U8();
            m.linearVelocity = Quant.ReadVec16(ref r, 1f / VelocityScale);
            m.angularVelocity = Quant.ReadVec16(ref r, 1f / VelocityScale);
            m.throwSpeed = r.U16() * 0.01f;
            m.wasThrown = r.Bool();
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: the item has slowed down (or 1.5 s passed); take it back with this state.</summary>
    public struct ItemHandoffMsg
    {
        public const int MaxSize = 24;
        public byte itemId;
        public byte epoch;
        public NetVec3 position;
        public NetQuat rotation;
        public NetVec3 linearVelocity;
        public NetVec3 angularVelocity;

        public void Write(ref NetWriter w)
        {
            w.U8(itemId);
            w.U8(epoch);
            Quant.WritePosCm(ref w, position);
            Quant.WriteQuat(ref w, rotation);
            Quant.WriteVec16(ref w, linearVelocity, ItemThrownMsg.VelocityScale);
            Quant.WriteVec16(ref w, angularVelocity, ItemThrownMsg.VelocityScale);
        }

        public static bool Read(ref NetReader r, out ItemHandoffMsg m)
        {
            m = new ItemHandoffMsg();
            m.itemId = r.U8();
            m.epoch = r.U8();
            m.position = Quant.ReadPosCm(ref r);
            m.rotation = Quant.ReadQuat(ref r);
            m.linearVelocity = Quant.ReadVec16(ref r, 1f / ItemThrownMsg.VelocityScale);
            m.angularVelocity = Quant.ReadVec16(ref r, 1f / ItemThrownMsg.VelocityScale);
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: a host-simulated item has settled here (heals a displaced copy).</summary>
    public struct ItemRestMsg
    {
        public const int MaxSize = 12;
        public byte itemId;
        public byte epoch;
        public NetVec3 position;
        public NetQuat rotation;

        public void Write(ref NetWriter w)
        {
            w.U8(itemId);
            w.U8(epoch);
            Quant.WritePosCm(ref w, position);
            Quant.WriteQuat(ref w, rotation);
        }

        public static bool Read(ref NetReader r, out ItemRestMsg m)
        {
            m = new ItemRestMsg();
            m.itemId = r.U8();
            m.epoch = r.U8();
            m.position = Quant.ReadPosCm(ref r);
            m.rotation = Quant.ReadQuat(ref r);
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: an item I simulate took a knock and is now worth this.</summary>
    public struct ItemDamageReportMsg
    {
        public const int MaxSize = 3;
        public byte itemId;
        public ushort newValue;
        public void Write(ref NetWriter w) { w.U8(itemId); w.U16(newValue); }
        public static bool Read(ref NetReader r, out ItemDamageReportMsg m)
        {
            m = new ItemDamageReportMsg { itemId = r.U8(), newValue = r.U16() };
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: an item's value changed (MissionSession.ApplyRemoteValue).</summary>
    public struct ItemValueMsg
    {
        public const int MaxSize = 3;
        public byte itemId;
        public ushort value;
        public void Write(ref NetWriter w) { w.U8(itemId); w.U16(value); }
        public static bool Read(ref NetReader r, out ItemValueMsg m)
        {
            m = new ItemValueMsg { itemId = r.U8(), value = r.U16() };
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: the locker accepted an item into this slot.</summary>
    public struct ItemDepositedMsg
    {
        public const int MaxSize = 7;
        public byte itemId;
        public byte by;
        public ushort finalValue;
        public ushort trickBonus;
        public byte lockerSlot;

        public void Write(ref NetWriter w) { w.U8(itemId); w.U8(by); w.U16(finalValue); w.U16(trickBonus); w.U8(lockerSlot); }
        public static bool Read(ref NetReader r, out ItemDepositedMsg m)
        {
            m = new ItemDepositedMsg { itemId = r.U8(), by = r.U8(), finalValue = r.U16(), trickBonus = r.U16(), lockerSlot = r.U8() };
            if (m.by >= NetProtocol.MaxPlayers && m.by != NetProtocol.NoSlot) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: a lost item was put back here (RecoverLostObjects).</summary>
    public struct ItemRecoveredMsg
    {
        public const int MaxSize = 7;
        public byte itemId;
        public NetVec3 position;
        public void Write(ref NetWriter w) { w.U8(itemId); Quant.WritePosCm(ref w, position); }
        public static bool Read(ref NetReader r, out ItemRecoveredMsg m)
        {
            m = new ItemRecoveredMsg();
            m.itemId = r.U8();
            m.position = Quant.ReadPosCm(ref r);
            return !r.Failed;
        }
    }

    // ====================================================================== world (reliable)

    /// <summary>Client -> host: I pressed a door button and want this state (never a toggle).</summary>
    public struct DoorRequestMsg
    {
        public const int MaxSize = 2;
        public byte doorId;
        public bool desiredOpen;
        public void Write(ref NetWriter w) { w.U8(doorId); w.Bool(desiredOpen); }
        public static bool Read(ref NetReader r, out DoorRequestMsg m)
        {
            m = new DoorRequestMsg { doorId = r.U8(), desiredOpen = r.Bool() };
            if (m.doorId >= NetProtocol.MaxDoors) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: a door is now open / closed.</summary>
    public struct DoorStateMsg
    {
        public const int MaxSize = 2;
        public byte doorId;
        public bool open;
        public void Write(ref NetWriter w) { w.U8(doorId); w.Bool(open); }
        public static bool Read(ref NetReader r, out DoorStateMsg m)
        {
            m = new DoorStateMsg { doorId = r.U8(), open = r.Bool() };
            if (m.doorId >= NetProtocol.MaxDoors) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: I burnt through this cut point.</summary>
    public struct PointCutReportMsg
    {
        public const int MaxSize = 2;
        public byte panel;
        public byte index;
        public void Write(ref NetWriter w) { w.U8(panel); w.U8(index); }
        public static bool Read(ref NetReader r, out PointCutReportMsg m)
        {
            m = new PointCutReportMsg { panel = r.U8(), index = r.U8() };
            if (m.panel >= NetProtocol.MaxPanels) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: this cut point is done.</summary>
    public struct PointCutMsg
    {
        public const int MaxSize = 2;
        public byte panel;
        public byte index;
        public void Write(ref NetWriter w) { w.U8(panel); w.U8(index); }
        public static bool Read(ref NetReader r, out PointCutMsg m)
        {
            m = new PointCutMsg { panel = r.U8(), index = r.U8() };
            if (m.panel >= NetProtocol.MaxPanels) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: a panel opened; the plate starts with the host's rolled impulse so it flies the same everywhere.</summary>
    public struct PanelOpenedMsg
    {
        public const int MaxSize = 14;
        /// <summary>Impulse and torque are sent in hundredths, +-327.</summary>
        public const float ImpulseScale = 100f;
        public byte panel;
        public byte by;
        public NetVec3 plateImpulse;
        public NetVec3 plateTorque;

        public void Write(ref NetWriter w)
        {
            w.U8(panel);
            w.U8(by);
            Quant.WriteVec16(ref w, plateImpulse, ImpulseScale);
            Quant.WriteVec16(ref w, plateTorque, ImpulseScale);
        }

        public static bool Read(ref NetReader r, out PanelOpenedMsg m)
        {
            m = new PanelOpenedMsg();
            m.panel = r.U8();
            m.by = r.U8();
            m.plateImpulse = Quant.ReadVec16(ref r, 1f / ImpulseScale);
            m.plateTorque = Quant.ReadVec16(ref r, 1f / ImpulseScale);
            if (m.panel >= NetProtocol.MaxPanels || (m.by >= NetProtocol.MaxPlayers && m.by != NetProtocol.NoSlot)) r.Fail();
            return !r.Failed;
        }
    }

    // ====================================================================== creature and hazards (reliable)

    /// <summary>Host -> all: the creature changed state (drives the eye light, hum and alert cues; never lost, unlike snapshots).</summary>
    public struct CreatureStateChangedMsg
    {
        public const int MaxSize = 2;
        public EnemyState state;
        public byte targetSlot;

        public void Write(ref NetWriter w) { w.U8((byte)state); w.U8(targetSlot); }
        public static bool Read(ref NetReader r, out CreatureStateChangedMsg m)
        {
            m = new CreatureStateChangedMsg();
            byte state = r.U8();
            m.targetSlot = r.U8();
            if (state > (byte)EnemyState.Search || (m.targetSlot >= NetProtocol.MaxPlayers && m.targetSlot != NetProtocol.NoSlot)) r.Fail();
            m.state = (EnemyState)state;
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: the creature was darted for this long (tenths of a second on the wire).</summary>
    public struct TranquilizedMsg
    {
        public const int MaxSize = 2;
        public float seconds;
        public void Write(ref NetWriter w)
        {
            float s = seconds;
            w.U16(!(s > 0f) ? (ushort)0 : s >= 6553.5f ? (ushort)65535 : (ushort)(s * 10f + 0.5f));
        }
        public static bool Read(ref NetReader r, out TranquilizedMsg m)
        {
            m = new TranquilizedMsg { seconds = r.U16() * 0.1f };
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: the catch began; every machine plays its side of the jumpscare.</summary>
    public struct CatchStartedMsg
    {
        public const int MaxSize = 1;
        public byte victimSlot;
        public void Write(ref NetWriter w) { w.U8(victimSlot); }
        public static bool Read(ref NetReader r, out CatchStartedMsg m)
        {
            m = new CatchStartedMsg { victimSlot = r.U8() };
            if (m.victimSlot >= NetProtocol.MaxPlayers) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: my dart hit the creature (the shooter's own sphere-cast is trusted; the host range-checks).</summary>
    public struct TranqHitReportMsg
    {
        public const int MaxSize = 12;
        public NetVec3 origin;
        public NetVec3 hitPoint;
        public void Write(ref NetWriter w) { Quant.WritePosCm(ref w, origin); Quant.WritePosCm(ref w, hitPoint); }
        public static bool Read(ref NetReader r, out TranqHitReportMsg m)
        {
            m = new TranqHitReportMsg();
            m.origin = Quant.ReadPosCm(ref r);
            m.hitPoint = Quant.ReadPosCm(ref r);
            return !r.Failed;
        }
    }

    /// <summary>Host -> others: draw a dart tracer (Stage 5).</summary>
    public struct TranqTracerMsg
    {
        public const int MaxSize = 13;
        public byte by;
        public NetVec3 from;
        public NetVec3 to;
        public void Write(ref NetWriter w) { w.U8(by); Quant.WritePosCm(ref w, from); Quant.WritePosCm(ref w, to); }
        public static bool Read(ref NetReader r, out TranqTracerMsg m)
        {
            m = new TranqTracerMsg();
            m.by = r.U8();
            m.from = Quant.ReadPosCm(ref r);
            m.to = Quant.ReadPosCm(ref r);
            if (m.by >= NetProtocol.MaxPlayers) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: a blackout began and will last this long (tenths of a second on the wire).</summary>
    public struct BlackoutStartedMsg
    {
        public const int MaxSize = 2;
        public float seconds;
        public void Write(ref NetWriter w)
        {
            float s = seconds;
            w.U16(!(s > 0f) ? (ushort)0 : s >= 6553.5f ? (ushort)65535 : (ushort)(s * 10f + 0.5f));
        }
        public static bool Read(ref NetReader r, out BlackoutStartedMsg m)
        {
            m = new BlackoutStartedMsg { seconds = r.U16() * 0.1f };
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: the blackout is over (instant = skip the fade back).</summary>
    public struct BlackoutEndedMsg
    {
        public const int MaxSize = 1;
        public bool instant;
        public void Write(ref NetWriter w) { w.Bool(instant); }
        public static bool Read(ref NetReader r, out BlackoutEndedMsg m)
        {
            m = new BlackoutEndedMsg { instant = r.Bool() };
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: I touched or burnt this web wire.</summary>
    public struct WireReportMsg
    {
        public const int MaxSize = 2;
        public byte index;
        public NetWireChange change;
        public void Write(ref NetWriter w) { w.U8(index); w.U8((byte)change); }
        public static bool Read(ref NetReader r, out WireReportMsg m)
        {
            m = new WireReportMsg();
            m.index = r.U8();
            byte change = r.U8();
            if (m.index >= NetProtocol.MaxWires || change > (byte)NetWireChange.Burnt) r.Fail();
            m.change = (NetWireChange)change;
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: a web wire changed.</summary>
    public struct WireChangedMsg
    {
        public const int MaxSize = 3;
        public byte index;
        public NetWireChange change;
        public byte by;
        public void Write(ref NetWriter w) { w.U8(index); w.U8((byte)change); w.U8(by); }
        public static bool Read(ref NetReader r, out WireChangedMsg m)
        {
            m = new WireChangedMsg();
            m.index = r.U8();
            byte change = r.U8();
            m.by = r.U8();
            if (m.index >= NetProtocol.MaxWires || change > (byte)NetWireChange.Burnt || (m.by >= NetProtocol.MaxPlayers && m.by != NetProtocol.NoSlot)) r.Fail();
            m.change = (NetWireChange)change;
            return !r.Failed;
        }
    }

    // ====================================================================== co-op revive (reliable, protocol 5)

    /// <summary>Host -> all: this crew member was caught and is downed here (where their head was when it caught them).</summary>
    public struct PlayerDownedMsg
    {
        public const int MaxSize = 7;
        public byte slot;
        public NetVec3 position;
        public void Write(ref NetWriter w) { w.U8(slot); Quant.WritePosCm(ref w, position); }
        public static bool Read(ref NetReader r, out PlayerDownedMsg m)
        {
            m = new PlayerDownedMsg { slot = r.U8() };
            m.position = Quant.ReadPosCm(ref r);
            if (m.slot >= NetProtocol.MaxPlayers) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: this crew member is up again, revived by <see cref="by"/> (255 = nobody in particular).</summary>
    public struct PlayerRevivedMsg
    {
        public const int MaxSize = 2;
        public byte slot;
        public byte by;
        public void Write(ref NetWriter w) { w.U8(slot); w.U8(by); }
        public static bool Read(ref NetReader r, out PlayerRevivedMsg m)
        {
            m = new PlayerRevivedMsg { slot = r.U8(), by = r.U8() };
            if (m.slot >= NetProtocol.MaxPlayers || (m.by >= NetProtocol.MaxPlayers && m.by != NetProtocol.NoSlot)) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Host -> all: how far this downed crew member's revive ring is (0..1 in 255 steps) and who is holding it (255 = nobody).</summary>
    public struct ReviveProgressMsg
    {
        public const int MaxSize = 3;
        public byte slot;
        public float progress;
        public byte rescuerSlot;
        public void Write(ref NetWriter w) { w.U8(slot); w.U8(Quant.ToByte(progress)); w.U8(rescuerSlot); }
        public static bool Read(ref NetReader r, out ReviveProgressMsg m)
        {
            m = new ReviveProgressMsg { slot = r.U8() };
            m.progress = Quant.FromByte(r.U8());
            m.rescuerSlot = r.U8();
            if (m.slot >= NetProtocol.MaxPlayers || (m.rescuerSlot >= NetProtocol.MaxPlayers && m.rescuerSlot != NetProtocol.NoSlot)) r.Fail();
            return !r.Failed;
        }
    }

    /// <summary>Client -> host: my hand closed on (on) or let go of (off) the beacon of the downed crew member in <see cref="slot"/>.</summary>
    public struct ReviveHoldMsg
    {
        public const int MaxSize = 2;
        public byte slot;
        public bool on;
        public void Write(ref NetWriter w) { w.U8(slot); w.Bool(on); }
        public static bool Read(ref NetReader r, out ReviveHoldMsg m)
        {
            m = new ReviveHoldMsg { slot = r.U8(), on = r.Bool() };
            if (m.slot >= NetProtocol.MaxPlayers) r.Fail();
            return !r.Failed;
        }
    }
}
