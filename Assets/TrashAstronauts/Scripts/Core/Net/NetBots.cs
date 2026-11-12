using System;
using System.Collections.Generic;

namespace TrashAstronauts.Core.Net
{
    // Test doubles for the two world interfaces. Plain C#: used by the EditMode tests, by PlayMode tests (the real
    // scene as host against BotClientWorld bots, the real scene as client against a ScriptedHostWorld) and by the
    // Net Bots editor window. Never instantiated by the game itself.

    /// <summary>
    /// An IClientWorld that records what it is told and keeps the simplest possible mirror of it. Items get a
    /// real <see cref="ItemAuthority"/> each, so a bot can grab, carry, throw and hand back like a player.
    /// </summary>
    public sealed class BotClientWorld : IClientWorld
    {
        /// <summary>How many times each message type reached this world, indexed by <see cref="NetMsg"/> id.</summary>
        public readonly int[] Calls = new int[256];
        /// <summary>Message types in arrival order (reliable ones only), capped at 4096.</summary>
        public readonly List<byte> Order = new List<byte>();
        /// <summary>Callbacks of any kind that arrived before OnWelcome. Must stay 0: Welcome is the first thing a joiner is told.</summary>
        public int CallsBeforeWelcome;

        // session
        public byte LocalSlot;
        public bool Welcomed;
        public MissionPhase HostPhase;
        public readonly bool[] Present = new bool[NetProtocol.MaxPlayers];
        public readonly bool[] Away = new bool[NetProtocol.MaxPlayers];
        public readonly byte[] Handedness = new byte[NetProtocol.MaxPlayers];
        public WorldStateMsg World;
        public bool HostPaused;
        public bool HostSilent;
        public int HostSilentChanges;
        public SessionEndReason Ended;
        public int EndedCount;

        // streams
        public readonly AvatarSnapshot[] Avatars = new AvatarSnapshot[NetProtocol.MaxPlayers];
        public readonly int[] AvatarCount = new int[NetProtocol.MaxPlayers];
        public readonly double[] AvatarTime = new double[NetProtocol.MaxPlayers];
        public CreatureSnapshot Creature;
        public double CreatureTime;
        public float ElapsedSeconds, OxygenRemainingSeconds;
        public byte ClockFlagBits;

        // run
        public RunStartParams Run;
        public bool RunHashValid;
        public RunEndedMsg Result;
        public byte ExtractMissingMask;

        // items (sized at RunStart)
        public ItemAuthority[] Items = new ItemAuthority[0];
        public PoseSnapshot[] ItemPoses = new PoseSnapshot[0];
        public int[] ItemValues = new int[0];
        public byte[] ItemCollectedBy = new byte[0];
        public byte[] ItemDepositedBy = new byte[0];
        public byte[] ItemLockerSlot = new byte[0];
        public ItemAuthorityEvent[] ItemLastEvent = new ItemAuthorityEvent[0];
        public int PosesAccepted, PosesDropped, RestsAccepted;
        public int Granted, Denied, Revoked, Returned, GrantedNotHeld;
        /// <summary>
        /// Optional. When set, the bot does what ItemSync must do on <see cref="ItemAuthorityEvent.GrantedNotHeld"/>:
        /// it sends ItemReleased for the item at once.
        /// </summary>
        public NetSessionClient Session;

        // world
        public ushort DoorOpenMask;
        public ushort PanelOpenMask;
        /// <summary>Cut points reported by PointCut, one 32-bit mask per panel (indices above 31 are only counted).</summary>
        public readonly uint[] CutMask = new uint[NetProtocol.MaxPanels];
        public NetVec3 LastPlateImpulse, LastPlateTorque;

        // creature and hazards
        public EnemyState CreatureEventState;
        public byte CreatureEventTarget = NetProtocol.NoSlot;
        public float TranquilizedSeconds;
        public byte CatchVictim = NetProtocol.NoSlot;
        public float BlackoutSeconds;
        public bool BlackoutOn;
        public uint WireSnappedMask, WireBurntMask;

        /// <summary>The time passed to item state changes; set it before pumping the session (tests use the hub clock).</summary>
        public double Now;

        void Count(byte type)
        {
            Calls[type]++;
            if (!Welcomed && type != NetMsg.Welcome) CallsBeforeWelcome++;
            if (NetMsg.IsReliable(type) && Order.Count < 4096) Order.Add(type);
        }

        public void OnWelcome(byte localSlot, in WelcomeMsg welcome)
        {
            Count(NetMsg.Welcome);
            LocalSlot = localSlot;
            Welcomed = true;
            HostPhase = welcome.phase;
            for (int i = 0; i < NetProtocol.MaxPlayers; i++)
            {
                Present[i] = welcome.IsPresent(i);
                Away[i] = welcome.IsAway(i);
                Handedness[i] = welcome.Handedness(i);
            }
            ApplyWorld(in welcome.world);
        }

        public void OnRosterChanged(byte slot, RosterChange change, byte handedness)
        {
            Count(NetMsg.RosterChanged);
            switch (change)
            {
                case RosterChange.Joined: Present[slot] = true; Away[slot] = false; Handedness[slot] = handedness; break;
                case RosterChange.Left: Present[slot] = false; Away[slot] = false; break;
                case RosterChange.Away: Away[slot] = true; break;
                case RosterChange.Back: Away[slot] = false; break;
                case RosterChange.Settings: Handedness[slot] = handedness; break;
            }
        }

        public void OnWorldState(in WorldStateMsg state)
        {
            Count(NetMsg.WorldState);
            ApplyWorld(in state);
        }

        void ApplyWorld(in WorldStateMsg state)
        {
            World.CopyFrom(in state);
            DoorOpenMask = state.doorOpenMask;
            PanelOpenMask = state.panelOpenMask;
            WireSnappedMask = state.wireSnappedMask;
            WireBurntMask = state.wireBurntMask;
        }

        public void OnHostPause(bool on) { Count(NetMsg.HostPause); HostPaused = on; }
        public void OnHostSilent(bool silent) { HostSilent = silent; HostSilentChanges++; }
        public void OnSessionEnded(SessionEndReason reason) { Ended = reason; EndedCount++; }

        public void OnRemoteAvatar(byte slot, in AvatarSnapshot avatar, double hostTime)
        {
            Count(NetMsg.RemoteAvatar);
            Avatars[slot] = avatar;
            AvatarCount[slot]++;
            AvatarTime[slot] = hostTime;
        }

        public void OnCreatureState(in CreatureSnapshot creature, double hostTime)
        {
            Count(NetMsg.CreatureState);
            Creature = creature;
            CreatureTime = hostTime;
        }

        public void OnItemPose(in ItemPoseEntry pose, double hostTime)
        {
            Count(NetMsg.ItemPoses);
            if (pose.itemId >= Items.Length || !Items[pose.itemId].AcceptPose(pose.epoch)) { PosesDropped++; return; }
            ItemPoses[pose.itemId] = new PoseSnapshot(pose.position, pose.rotation);
            PosesAccepted++;
        }

        public void OnClock(float elapsedSeconds, float oxygenRemainingSeconds, byte flags)
        {
            Count(NetMsg.Clock);
            ElapsedSeconds = elapsedSeconds;
            OxygenRemainingSeconds = oxygenRemainingSeconds;
            ClockFlagBits = flags;
        }

        public void OnRunStart(in RunStartParams parameters)
        {
            Count(NetMsg.RunStart);
            Run = parameters;
            RunHashValid = RunStartCodec.IsPlanHashValid(in parameters);
            int n = parameters.items != null ? parameters.items.Length : 0;
            Items = new ItemAuthority[n];
            ItemPoses = new PoseSnapshot[n];
            ItemValues = new int[n];
            ItemCollectedBy = new byte[n];
            ItemDepositedBy = new byte[n];
            ItemLockerSlot = new byte[n];
            ItemLastEvent = new ItemAuthorityEvent[n];
            for (int i = 0; i < n; i++)
            {
                Items[i] = new ItemAuthority((byte)i, LocalSlot);
                ItemValues[i] = parameters.items[i].value;
                ItemCollectedBy[i] = NetProtocol.NoSlot;
                ItemDepositedBy[i] = NetProtocol.NoSlot;
                ItemLockerSlot[i] = 255;
                ItemPoses[i].rotation = NetQuat.Identity;
            }
            HostPhase = MissionPhase.Active;
        }

        public void OnExtractRefused(byte missingSlotsMask) { Count(NetMsg.ExtractRefused); ExtractMissingMask = missingSlotsMask; }
        public void OnRunEnded(in RunEndedMsg result) { Count(NetMsg.RunEnded); Result = result; HostPhase = MissionPhase.Results; }
        public void OnReturnedToHub() { Count(NetMsg.ReturnedToHub); HostPhase = MissionPhase.Hub; }

        public void OnItemOwner(byte itemId, byte ownerSlot, byte epoch, bool held, bool denied)
        {
            Count(NetMsg.ItemOwner);
            if (itemId >= Items.Length) return;
            ItemAuthorityEvent e = Items[itemId].OnOwner(ownerSlot, epoch, held, denied, Now);
            ItemLastEvent[itemId] = e;
            switch (e)
            {
                case ItemAuthorityEvent.Granted: Granted++; break;
                case ItemAuthorityEvent.Denied: Denied++; break;
                case ItemAuthorityEvent.Revoked: Revoked++; break;
                case ItemAuthorityEvent.Returned: Returned++; break;
                case ItemAuthorityEvent.GrantedNotHeld:
                    GrantedNotHeld++;
                    if (Session != null)
                        Session.SendItemReleased(itemId, Items[itemId].WireEpoch, ItemPoses[itemId].position, ItemPoses[itemId].rotation, ItemPoses[itemId].position);
                    break;
            }
        }

        public void OnItemCollected(byte itemId, byte by)
        {
            Count(NetMsg.ItemCollected);
            if (itemId < ItemCollectedBy.Length && ItemCollectedBy[itemId] == NetProtocol.NoSlot) ItemCollectedBy[itemId] = by;
        }

        public void OnItemRest(byte itemId, byte epoch, NetVec3 position, NetQuat rotation)
        {
            Count(NetMsg.ItemRest);
            if (itemId >= Items.Length || !Items[itemId].AcceptRest(epoch)) return;
            ItemPoses[itemId] = new PoseSnapshot(position, rotation);
            RestsAccepted++;
        }

        public void OnItemValue(byte itemId, int value)
        {
            Count(NetMsg.ItemValue);
            if (itemId < ItemValues.Length) ItemValues[itemId] = value;
        }

        public void OnItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot)
        {
            Count(NetMsg.ItemDeposited);
            if (itemId >= Items.Length) return;
            Items[itemId].OnDeposited();
            ItemDepositedBy[itemId] = by;
            ItemValues[itemId] = finalValue;
            ItemLockerSlot[itemId] = lockerSlot;
        }

        public void OnItemRecovered(byte itemId, NetVec3 position)
        {
            Count(NetMsg.ItemRecovered);
            if (itemId < ItemPoses.Length) ItemPoses[itemId].position = position;
        }

        public void OnDoorState(byte doorId, bool open)
        {
            Count(NetMsg.DoorState);
            if (open) DoorOpenMask |= (ushort)(1 << doorId);
            else DoorOpenMask &= (ushort)~(1 << doorId);
        }

        public void OnPointCut(byte panel, byte index) { Count(NetMsg.PointCut); if (index < 32) CutMask[panel] |= 1u << index; }

        public void OnPanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque)
        {
            Count(NetMsg.PanelOpened);
            PanelOpenMask |= (ushort)(1 << panel);
            LastPlateImpulse = plateImpulse;
            LastPlateTorque = plateTorque;
        }

        public void OnCreatureStateChanged(EnemyState state, byte targetSlot) { Count(NetMsg.CreatureStateChanged); CreatureEventState = state; CreatureEventTarget = targetSlot; }
        public void OnTranquilized(float seconds) { Count(NetMsg.Tranquilized); TranquilizedSeconds = seconds; }
        public void OnCatchStarted(byte victimSlot) { Count(NetMsg.CatchStarted); CatchVictim = victimSlot; }
        public void OnTranqTracer(byte by, NetVec3 from, NetVec3 to) { Count(NetMsg.TranqTracer); }
        public void OnBlackoutStarted(float seconds) { Count(NetMsg.BlackoutStarted); BlackoutSeconds = seconds; BlackoutOn = true; }
        public void OnBlackoutEnded(bool instant) { Count(NetMsg.BlackoutEnded); BlackoutOn = false; }

        public void OnWireChanged(byte index, NetWireChange change, byte by)
        {
            Count(NetMsg.WireChanged);
            if (change == NetWireChange.Snapped) WireSnappedMask |= 1u << index;
            else WireBurntMask |= 1u << index;
        }
    }

    /// <summary>
    /// An IHostWorld with no rules of its own: it records every call and answers from public fields. Bind it to a
    /// <see cref="NetSessionHost"/> and switch on the Auto* flags to get a host that says yes to everything, or
    /// leave them off and drive the session's Send* methods from the test script.
    /// </summary>
    public sealed class ScriptedHostWorld : IHostWorld
    {
        /// <summary>How many times each client message reached this world, indexed by <see cref="NetMsg"/> id (RosterChanged counts joins and leaves).</summary>
        public readonly int[] Calls = new int[256];
        public NetSessionHost Session;

        /// <summary>What <see cref="Phase"/> answers. Set Active to make the host refuse joins.</summary>
        public MissionPhase CurrentPhase = MissionPhase.Hub;
        /// <summary>Copied into every Welcome.</summary>
        public WorldStateMsg WelcomeWorld;
        /// <summary>The answer to CanGrabItem.</summary>
        public bool AllowGrabs = true;
        /// <summary>StartRequest starts <see cref="NextRun"/>.</summary>
        public bool AutoStart;
        public RunStartParams NextRun;
        /// <summary>ExtractRequest is refused with this mask when it is not 0, otherwise the run ends with <see cref="NextResult"/> (when AutoExtract).</summary>
        public bool AutoExtract;
        public byte ExtractMissingMask;
        public RunEndedMsg NextResult;
        /// <summary>Door requests and cut reports are echoed as DoorState / PointCut.</summary>
        public bool AutoWorld;
        /// <summary>A first grab broadcasts ItemCollected.</summary>
        public bool AutoCollect = true;

        public readonly bool[] Joined = new bool[NetProtocol.MaxPlayers];
        public readonly bool[] Away = new bool[NetProtocol.MaxPlayers];
        public readonly byte[] Handedness = new byte[NetProtocol.MaxPlayers];
        public readonly PlayerLeaveReason[] LeftReason = new PlayerLeaveReason[NetProtocol.MaxPlayers];
        public readonly AvatarSnapshot[] Avatars = new AvatarSnapshot[NetProtocol.MaxPlayers];
        public readonly int[] AvatarCount = new int[NetProtocol.MaxPlayers];
        public int JoinCount, LeftCount, AwayChanges;

        public byte LastSlot = NetProtocol.NoSlot;
        public NetVec3 LastNoisePosition;
        public float LastNoiseLoudness;
        public NoiseKind LastNoiseKind;
        public byte LastItem, LastHand, LastDoor, LastPanel, LastIndex;
        public bool LastDesiredOpen;
        public int LastValue;
        public NetVec3 LastPosition, LastReleasePosition, LastLinearVelocity, LastAngularVelocity, LastHitPoint;
        public NetQuat LastRotation;
        public float LastThrowSpeed;
        public bool LastWasThrown;
        public NetWireChange LastWireChange;
        public readonly List<ItemReturn> Returns = new List<ItemReturn>();
        public readonly List<byte> GrantedItems = new List<byte>();
        /// <summary>Optional: runs inside OnPlayerJoined with the new slot (a game that broadcasts something when a player joins).</summary>
        public Action<byte> WhenJoined;
        readonly HashSet<byte> m_Collected = new HashSet<byte>();

        public MissionPhase Phase => CurrentPhase;

        public void FillWorldState(ref WorldStateMsg state)
        {
            state.CopyFrom(in WelcomeWorld);
        }

        public void OnPlayerJoined(byte slot, byte handedness)
        {
            Calls[NetMsg.RosterChanged]++;
            Joined[slot] = true;
            Away[slot] = false;
            Handedness[slot] = handedness;
            JoinCount++;
            WhenJoined?.Invoke(slot);
        }

        public void OnPlayerLeft(byte slot, PlayerLeaveReason reason)
        {
            Calls[NetMsg.RosterChanged]++;
            Joined[slot] = false;
            Away[slot] = false;
            LeftReason[slot] = reason;
            LeftCount++;
        }

        public void OnPlayerAway(byte slot, bool away) { Away[slot] = away; AwayChanges++; }
        public void OnPlayerSettings(byte slot, byte handedness) { Calls[NetMsg.SettingsChanged]++; Handedness[slot] = handedness; }

        public void OnAvatar(byte slot, in AvatarSnapshot avatar, double hostTime)
        {
            Calls[NetMsg.AvatarState]++;
            Avatars[slot] = avatar;
            AvatarCount[slot]++;
        }

        public void OnNoise(byte slot, NetVec3 position, float loudness, NoiseKind kind)
        {
            Calls[NetMsg.Noise]++;
            LastSlot = slot;
            LastNoisePosition = position;
            LastNoiseLoudness = loudness;
            LastNoiseKind = kind;
        }

        public void OnStartRequest(byte slot)
        {
            Calls[NetMsg.StartRequest]++;
            LastSlot = slot;
            if (!AutoStart || Session == null || CurrentPhase == MissionPhase.Active) return;
            if (Session.SendRunStart(in NextRun))
            {
                CurrentPhase = MissionPhase.Active;
                m_Collected.Clear();
            }
        }

        public void OnExtractRequest(byte slot)
        {
            Calls[NetMsg.ExtractRequest]++;
            LastSlot = slot;
            if (!AutoExtract || Session == null || CurrentPhase != MissionPhase.Active) return;
            if (ExtractMissingMask != 0) { Session.SendExtractRefused(slot, ExtractMissingMask); return; }
            CurrentPhase = MissionPhase.Results;
            Session.SendRunEnded(in NextResult);
        }

        public bool CanGrabItem(byte itemId, byte slot) { return AllowGrabs; }

        public void OnItemGranted(byte itemId, byte slot, byte hand)
        {
            Calls[NetMsg.GrabRequest]++;
            LastSlot = slot;
            LastItem = itemId;
            LastHand = hand;
            GrantedItems.Add(itemId);
            if (AutoCollect && Session != null && m_Collected.Add(itemId)) Session.SendItemCollected(itemId, slot);
        }

        public void OnItemPose(byte itemId, byte slot, NetVec3 position, NetQuat rotation, bool held, double hostTime)
        {
            Calls[NetMsg.ItemPoses]++;
            LastItem = itemId;
            LastPosition = position;
            LastRotation = rotation;
        }

        public void OnItemReleased(byte itemId, byte slot, NetVec3 position, NetQuat rotation, NetVec3 releasePosition)
        {
            Calls[NetMsg.ItemReleased]++;
            LastSlot = slot;
            LastItem = itemId;
            LastPosition = position;
            LastRotation = rotation;
            LastReleasePosition = releasePosition;
        }

        public void OnItemThrown(byte itemId, byte slot, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown)
        {
            Calls[NetMsg.ItemThrown]++;
            LastSlot = slot;
            LastItem = itemId;
            LastLinearVelocity = linearVelocity;
            LastAngularVelocity = angularVelocity;
            LastThrowSpeed = throwSpeed;
            LastWasThrown = wasThrown;
        }

        public void OnItemReturned(in ItemReturn item)
        {
            Calls[NetMsg.ItemHandoff]++;
            Returns.Add(item);
        }

        public void OnItemDamageReport(byte itemId, byte slot, int newValue)
        {
            Calls[NetMsg.ItemDamageReport]++;
            LastSlot = slot;
            LastItem = itemId;
            LastValue = newValue;
            if (AutoWorld && Session != null) Session.SendItemValue(itemId, newValue);
        }

        public void OnDoorRequest(byte slot, byte doorId, bool desiredOpen)
        {
            Calls[NetMsg.DoorRequest]++;
            LastSlot = slot;
            LastDoor = doorId;
            LastDesiredOpen = desiredOpen;
            if (AutoWorld && Session != null) Session.SendDoorState(doorId, desiredOpen);
        }

        public void OnPointCutReport(byte slot, byte panel, byte index)
        {
            Calls[NetMsg.PointCutReport]++;
            LastSlot = slot;
            LastPanel = panel;
            LastIndex = index;
            if (AutoWorld && Session != null) Session.SendPointCut(panel, index);
        }

        public void OnTranqHitReport(byte slot, NetVec3 origin, NetVec3 hitPoint)
        {
            Calls[NetMsg.TranqHitReport]++;
            LastSlot = slot;
            LastPosition = origin;
            LastHitPoint = hitPoint;
        }

        public void OnWireReport(byte slot, byte index, NetWireChange change)
        {
            Calls[NetMsg.WireReport]++;
            LastSlot = slot;
            LastIndex = index;
            LastWireChange = change;
            if (AutoWorld && Session != null) Session.SendWireChanged(index, change, slot);
        }
    }

    /// <summary>
    /// A tiny self-consistent "game" so the sessions can be fuzzed without the real one: one authoritative
    /// <see cref="Host"/> model and any number of <see cref="Client"/> mirrors, each with a
    /// <see cref="State.Digest"/>. If the net core is right, every mirror's digest equals the host's once the
    /// network has gone quiet, whatever was lost, reordered or duplicated on the way.
    /// The rules are deliberately the plan's rules in miniature (desired-state doors, cut points opening a panel,
    /// first grab collects, damage only downward and not below a third, deposits lock an item, all-aboard extract).
    /// </summary>
    public static class FakeWorld
    {
        public const int Doors = 5;
        public const int Panels = 4;
        public const int CutPointsPerPanel = 4;
        public const int Wires = 8;
        /// <summary>A head with x below this counts as aboard the tug.</summary>
        public const float AboardLimitX = 5f;

        public sealed class State
        {
            public MissionPhase phase;
            public int runIndex;
            public ushort doorOpenMask;
            public ushort panelOpenMask;
            public readonly ushort[] cutMask = new ushort[Panels];
            public uint wireSnappedMask, wireBurntMask;
            public int itemCount;
            public readonly int[] value = new int[NetProtocol.MaxRunItems];
            public readonly int[] fullValue = new int[NetProtocol.MaxRunItems];
            public readonly SalvageState[] state = new SalvageState[NetProtocol.MaxRunItems];
            public readonly byte[] collectedBy = new byte[NetProtocol.MaxRunItems];
            public readonly byte[] depositedBy = new byte[NetProtocol.MaxRunItems];
            public readonly byte[] lockerSlot = new byte[NetProtocol.MaxRunItems];
            public int depositedCount;
            public int depositedValue;
            public MissionOutcome outcome;
            public int payout;

            public void BeginRun(in RunStartParams p)
            {
                phase = MissionPhase.Active;
                runIndex = p.runIndex;
                doorOpenMask = 0;
                panelOpenMask = 0;
                for (int i = 0; i < cutMask.Length; i++) cutMask[i] = 0;
                wireSnappedMask = 0;
                wireBurntMask = 0;
                itemCount = p.items != null ? p.items.Length : 0;
                for (int i = 0; i < itemCount; i++)
                {
                    value[i] = p.items[i].value;
                    fullValue[i] = p.items[i].value;
                    state[i] = SalvageState.InWreck;
                    collectedBy[i] = NetProtocol.NoSlot;
                    depositedBy[i] = NetProtocol.NoSlot;
                    lockerSlot[i] = 255;
                }
                depositedCount = 0;
                depositedValue = 0;
                outcome = MissionOutcome.None;
                payout = 0;
            }

            public void ApplyWorld(in WorldStateMsg w)
            {
                doorOpenMask = w.doorOpenMask;
                panelOpenMask = w.panelOpenMask;
                wireSnappedMask = w.wireSnappedMask;
                wireBurntMask = w.wireBurntMask;
                for (int p = 0; p < Panels; p++)
                {
                    ushort mask = 0;
                    for (int i = 0; i < CutPointsPerPanel; i++) if (w.IsWeldDone(p * CutPointsPerPanel + i)) mask |= (ushort)(1 << i);
                    cutMask[p] = mask;
                }
            }

            public void FillWorld(ref WorldStateMsg w)
            {
                w.doorOpenMask = doorOpenMask;
                w.panelOpenMask = panelOpenMask;
                w.wireSnappedMask = wireSnappedMask;
                w.wireBurntMask = wireBurntMask;
                for (int p = 0; p < Panels; p++)
                    for (int i = 0; i < CutPointsPerPanel; i++)
                        if ((cutMask[p] & (1 << i)) != 0) w.SetWeldDone(p * CutPointsPerPanel + i);
            }

            /// <summary>FNV-1a over everything two peers must agree on.</summary>
            public uint Digest()
            {
                uint h = 2166136261u;
                h = Mix(h, (uint)phase);
                h = Mix(h, (uint)runIndex);
                h = Mix(h, doorOpenMask);
                h = Mix(h, panelOpenMask);
                for (int i = 0; i < cutMask.Length; i++) h = Mix(h, cutMask[i]);
                h = Mix(h, wireSnappedMask);
                h = Mix(h, wireBurntMask);
                h = Mix(h, (uint)itemCount);
                for (int i = 0; i < itemCount; i++)
                {
                    h = Mix(h, (uint)value[i]);
                    h = Mix(h, (uint)state[i]);
                    h = Mix(h, collectedBy[i]);
                    h = Mix(h, depositedBy[i]);
                    h = Mix(h, lockerSlot[i]);
                }
                h = Mix(h, (uint)depositedCount);
                h = Mix(h, (uint)depositedValue);
                h = Mix(h, (uint)outcome);
                h = Mix(h, (uint)payout);
                return h;
            }

            static uint Mix(uint h, uint v)
            {
                for (int i = 0; i < 4; i++) { h ^= (v >> (8 * i)) & 0xFFu; h *= 16777619u; }
                return h;
            }
        }

        /// <summary>The authoritative side. Assign <see cref="Session"/> after constructing the NetSessionHost with this world.</summary>
        public sealed class Host : IHostWorld
        {
            public readonly State S = new State();
            public NetSessionHost Session;
            public readonly bool[] Aboard = { true, true, true, true };
            public readonly bool[] Away = new bool[NetProtocol.MaxPlayers];
            public int NoiseCount, GrantCount, ReturnCount, StartCount, RefusedExtracts;
            int m_NextRunIndex;
            readonly Random m_Random;

            public Host(int seed = 1) { m_Random = new Random(seed); }

            public MissionPhase Phase => S.phase;
            public void FillWorldState(ref WorldStateMsg state) { S.FillWorld(ref state); }
            public void OnPlayerJoined(byte slot, byte handedness) { Aboard[slot] = true; Away[slot] = false; }
            public void OnPlayerLeft(byte slot, PlayerLeaveReason reason) { Aboard[slot] = true; Away[slot] = false; }
            public void OnPlayerAway(byte slot, bool away) { Away[slot] = away; }
            public void OnPlayerSettings(byte slot, byte handedness) { }
            public void OnAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) { Aboard[slot] = avatar.headPosition.x < AboardLimitX; }
            public void OnNoise(byte slot, NetVec3 position, float loudness, NoiseKind kind) { NoiseCount++; }

            /// <summary>Starts a run with <paramref name="itemCount"/> items (host player pressing START).</summary>
            public bool StartRun(int itemCount)
            {
                if (S.phase == MissionPhase.Active) return false;
                var p = new RunStartParams
                {
                    seed = m_Random.Next(),
                    runIndex = (ushort)(++m_NextRunIndex),
                    mapIndex = 0,
                    condition = 0,
                    creatureIndex = RunStartParams.NoCreature,
                    quota = 100,
                    oxygenCapacitySeconds = 300f,
                    bonusMultiplier = 1f,
                    items = new RunItem[itemCount],
                };
                for (int i = 0; i < itemCount; i++)
                    p.items[i] = new RunItem { pointIndex = (byte)i, definitionIndex = (byte)(i % 7), value = (ushort)(30 + m_Random.Next(0, 200)) };
                p.planHash = p.ComputePlanHash();
                if (!Session.SendRunStart(in p)) return false;
                S.BeginRun(in p);
                StartCount++;
                return true;
            }

            public void OnStartRequest(byte slot) { StartRun(12); }

            public void OnExtractRequest(byte slot)
            {
                if (S.phase != MissionPhase.Active) return;
                byte missing = MissingMask();
                if (missing != 0) { RefusedExtracts++; Session.SendExtractRefused(slot, missing); return; }
                Extract();
            }

            /// <summary>Bit n set = connected, not Away, and not aboard.</summary>
            public byte MissingMask()
            {
                int mask = 0;
                for (int i = 0; i < NetProtocol.MaxPlayers; i++)
                    if (Session.IsConnected(i) && !Session.IsAway(i) && !Aboard[i]) mask |= 1 << i;
                return (byte)mask;
            }

            /// <summary>Ends the run (host player pressing EXTRACT with everyone aboard).</summary>
            public bool Extract()
            {
                if (S.phase != MissionPhase.Active || MissingMask() != 0) return false;
                S.phase = MissionPhase.Results;
                S.outcome = S.depositedValue >= 100 ? MissionOutcome.ContractComplete : MissionOutcome.QuotaMissed;
                S.payout = S.depositedValue;
                var result = new RunEndedMsg { outcome = S.outcome, quota = 100, depositedValue = S.depositedValue, depositedCount = S.depositedCount, payout = S.payout };
                Session.SendRunEnded(in result);
                return true;
            }

            public bool ReturnToHub()
            {
                if (S.phase != MissionPhase.Results) return false;
                S.phase = MissionPhase.Hub;
                Session.SendReturnedToHub();
                return true;
            }

            public bool CanGrabItem(byte itemId, byte slot)
            {
                return S.phase == MissionPhase.Active && itemId < S.itemCount && S.state[itemId] != SalvageState.Deposited;
            }

            public void OnItemGranted(byte itemId, byte slot, byte hand)
            {
                GrantCount++;
                Collect(itemId, slot);
            }

            void Collect(byte itemId, byte by)
            {
                if (S.state[itemId] != SalvageState.InWreck) return;
                S.state[itemId] = SalvageState.Collected;
                S.collectedBy[itemId] = by;
                Session.SendItemCollected(itemId, by);
            }

            /// <summary>The host player's own hand grabs an item.</summary>
            public bool HostGrab(byte itemId)
            {
                if (!CanGrabItem(itemId, 0) || !Session.LocalGrab(itemId)) return false;
                Collect(itemId, 0);
                return true;
            }

            public void OnItemPose(byte itemId, byte slot, NetVec3 position, NetQuat rotation, bool held, double hostTime) { }
            public void OnItemReleased(byte itemId, byte slot, NetVec3 position, NetQuat rotation, NetVec3 releasePosition) { }
            public void OnItemThrown(byte itemId, byte slot, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown) { }
            public void OnItemReturned(in ItemReturn item) { ReturnCount++; }

            public void OnItemDamageReport(byte itemId, byte slot, int newValue)
            {
                Damage(itemId, newValue);
            }

            /// <summary>Lowers an item's value if the rules allow (lower than now, not below a third of full, not deposited).</summary>
            public bool Damage(byte itemId, int newValue)
            {
                if (S.phase != MissionPhase.Active || itemId >= S.itemCount || S.state[itemId] == SalvageState.Deposited) return false;
                if (newValue >= S.value[itemId] || newValue * 3 < S.fullValue[itemId]) return false;
                S.value[itemId] = newValue;
                Session.SendItemValue(itemId, newValue);
                return true;
            }

            /// <summary>The locker takes a collected item nobody is holding.</summary>
            public bool Deposit(byte itemId, byte by)
            {
                if (S.phase != MissionPhase.Active || itemId >= S.itemCount || S.state[itemId] != SalvageState.Collected) return false;
                if (Session.Items.IsHeld(itemId)) return false;
                S.state[itemId] = SalvageState.Deposited;
                S.depositedBy[itemId] = by;
                S.lockerSlot[itemId] = (byte)S.depositedCount;
                S.depositedCount++;
                S.depositedValue += S.value[itemId];
                Session.SendItemDeposited(itemId, by, S.value[itemId], 0, S.lockerSlot[itemId]);
                return true;
            }

            public void OnDoorRequest(byte slot, byte doorId, bool desiredOpen) { SetDoor(doorId, desiredOpen); }

            public bool SetDoor(byte doorId, bool open)
            {
                if (doorId >= Doors) return false;
                bool isOpen = (S.doorOpenMask & (1 << doorId)) != 0;
                if (isOpen == open) return false;
                if (open) S.doorOpenMask |= (ushort)(1 << doorId);
                else S.doorOpenMask &= (ushort)~(1 << doorId);
                Session.SendDoorState(doorId, open);
                return true;
            }

            public void OnPointCutReport(byte slot, byte panel, byte index) { Cut(panel, index, slot); }

            public bool Cut(byte panel, byte index, byte by)
            {
                if (S.phase != MissionPhase.Active || panel >= Panels || index >= CutPointsPerPanel) return false;
                if ((S.cutMask[panel] & (1 << index)) != 0) return false;
                S.cutMask[panel] |= (ushort)(1 << index);
                Session.SendPointCut(panel, index);
                if (S.cutMask[panel] == (1 << CutPointsPerPanel) - 1 && (S.panelOpenMask & (1 << panel)) == 0)
                {
                    S.panelOpenMask |= (ushort)(1 << panel);
                    Session.SendPanelOpened(panel, by, new NetVec3(0.5f, 1f, -0.25f), new NetVec3(0f, 2f, 0f));
                }
                return true;
            }

            public void OnTranqHitReport(byte slot, NetVec3 origin, NetVec3 hitPoint) { }

            public void OnWireReport(byte slot, byte index, NetWireChange change) { Wire(index, change, slot); }

            public bool Wire(byte index, NetWireChange change, byte by)
            {
                if (S.phase != MissionPhase.Active || index >= Wires) return false;
                uint bit = 1u << index;
                if (((S.wireSnappedMask | S.wireBurntMask) & bit) != 0) return false;
                if (change == NetWireChange.Snapped) S.wireSnappedMask |= bit;
                else S.wireBurntMask |= bit;
                Session.SendWireChanged(index, change, by);
                return true;
            }
        }

        /// <summary>A mirror fed only by host messages, plus the client-side item authorities. Assign <see cref="Session"/> after constructing the NetSessionClient.</summary>
        public sealed class Client : IClientWorld
        {
            public readonly State S = new State();
            public NetSessionClient Session;
            public ItemAuthority[] Items = new ItemAuthority[0];
            public byte LocalSlot;
            public bool Welcomed;
            public SessionEndReason Ended;
            public readonly bool[] Present = new bool[NetProtocol.MaxPlayers];
            public readonly bool[] Away = new bool[NetProtocol.MaxPlayers];
            public int Granted, Denied, Revoked, Returned, GrantedNotHeld, RefusedExtracts, BadPlanHashes;
            /// <summary>Set before pumping the session.</summary>
            public double Now;

            public void OnWelcome(byte localSlot, in WelcomeMsg welcome)
            {
                LocalSlot = localSlot;
                Welcomed = true;
                S.phase = welcome.phase;
                for (int i = 0; i < NetProtocol.MaxPlayers; i++) { Present[i] = welcome.IsPresent(i); Away[i] = welcome.IsAway(i); }
                S.ApplyWorld(in welcome.world);
            }

            public void OnRosterChanged(byte slot, RosterChange change, byte handedness)
            {
                if (change == RosterChange.Joined) Present[slot] = true;
                else if (change == RosterChange.Left) { Present[slot] = false; Away[slot] = false; }
                else if (change == RosterChange.Away) Away[slot] = true;
                else if (change == RosterChange.Back) Away[slot] = false;
            }

            public void OnWorldState(in WorldStateMsg state) { S.ApplyWorld(in state); }
            public void OnHostPause(bool on) { }
            public void OnHostSilent(bool silent) { }
            public void OnSessionEnded(SessionEndReason reason) { Ended = reason; }
            public void OnRemoteAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) { }
            public void OnCreatureState(in CreatureSnapshot creature, double hostTime) { }
            public void OnItemPose(in ItemPoseEntry pose, double hostTime) { }
            public void OnClock(float elapsedSeconds, float oxygenRemainingSeconds, byte flags) { }

            public void OnRunStart(in RunStartParams parameters)
            {
                if (!RunStartCodec.IsPlanHashValid(in parameters)) BadPlanHashes++;
                S.BeginRun(in parameters);
                Items = new ItemAuthority[S.itemCount];
                for (int i = 0; i < Items.Length; i++) Items[i] = new ItemAuthority((byte)i, LocalSlot);
            }

            public void OnExtractRefused(byte missingSlotsMask) { RefusedExtracts++; }

            public void OnRunEnded(in RunEndedMsg result)
            {
                S.phase = MissionPhase.Results;
                S.outcome = result.outcome;
                S.payout = result.payout;
            }

            public void OnReturnedToHub() { S.phase = MissionPhase.Hub; }

            public void OnItemOwner(byte itemId, byte ownerSlot, byte epoch, bool held, bool denied)
            {
                if (itemId >= Items.Length) return;
                switch (Items[itemId].OnOwner(ownerSlot, epoch, held, denied, Now))
                {
                    case ItemAuthorityEvent.Granted: Granted++; break;
                    case ItemAuthorityEvent.Denied: Denied++; break;
                    case ItemAuthorityEvent.Revoked: Revoked++; break;
                    case ItemAuthorityEvent.Returned: Returned++; break;
                    case ItemAuthorityEvent.GrantedNotHeld:
                        // the host has us holding an item our hand is not on: let go of it at once
                        GrantedNotHeld++;
                        Session.SendItemReleased(itemId, Items[itemId].WireEpoch, NetVec3.Zero, NetQuat.Identity, NetVec3.Zero);
                        break;
                }
            }

            public void OnItemCollected(byte itemId, byte by)
            {
                if (itemId >= S.itemCount || S.state[itemId] != SalvageState.InWreck) return;
                S.state[itemId] = SalvageState.Collected;
                S.collectedBy[itemId] = by;
            }

            public void OnItemRest(byte itemId, byte epoch, NetVec3 position, NetQuat rotation) { }

            public void OnItemValue(byte itemId, int value)
            {
                if (itemId < S.itemCount) S.value[itemId] = value;
            }

            public void OnItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot)
            {
                if (itemId >= S.itemCount || S.state[itemId] == SalvageState.Deposited) return;
                if (itemId < Items.Length) Items[itemId].OnDeposited();
                S.state[itemId] = SalvageState.Deposited;
                S.depositedBy[itemId] = by;
                S.lockerSlot[itemId] = lockerSlot;
                S.value[itemId] = finalValue;
                S.depositedCount++;
                S.depositedValue += finalValue;
            }

            public void OnItemRecovered(byte itemId, NetVec3 position) { }

            public void OnDoorState(byte doorId, bool open)
            {
                if (open) S.doorOpenMask |= (ushort)(1 << doorId);
                else S.doorOpenMask &= (ushort)~(1 << doorId);
            }

            public void OnPointCut(byte panel, byte index)
            {
                if (panel < Panels) S.cutMask[panel] |= (ushort)(1 << index);
            }

            public void OnPanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque) { S.panelOpenMask |= (ushort)(1 << panel); }
            public void OnCreatureStateChanged(EnemyState state, byte targetSlot) { }
            public void OnTranquilized(float seconds) { }
            public void OnCatchStarted(byte victimSlot) { }
            public void OnTranqTracer(byte by, NetVec3 from, NetVec3 to) { }
            public void OnBlackoutStarted(float seconds) { }
            public void OnBlackoutEnded(bool instant) { }

            public void OnWireChanged(byte index, NetWireChange change, byte by)
            {
                if (change == NetWireChange.Snapped) S.wireSnappedMask |= 1u << index;
                else S.wireBurntMask |= 1u << index;
            }

            // ---- what a player (or a fuzzer) does on this client

            /// <summary>Grabs an item if the local rules allow: flips the authority to Mine and sends the GrabRequest.</summary>
            public bool Grab(byte itemId)
            {
                if (itemId >= Items.Length || S.phase != MissionPhase.Active) return false;
                if (!Items[itemId].BeginGrab(out byte epochSeen)) return false;
                Session.SendGrabRequest(itemId, epochSeen, 1);
                return true;
            }

            /// <summary>Lets go of an item this client is holding.</summary>
            public bool Release(byte itemId, double now)
            {
                if (itemId >= Items.Length || !Items[itemId].Release(now)) return false;
                Session.SendItemReleased(itemId, Items[itemId].WireEpoch, NetVec3.Zero, NetQuat.Identity, NetVec3.Zero);
                return true;
            }

            /// <summary>Runs every item's hand-back timer (items are treated as at rest) and streams poses for owned ones.</summary>
            public void TickItems(double now)
            {
                for (int i = 0; i < Items.Length; i++)
                {
                    ItemAuthority item = Items[i];
                    if (item.ShouldStream) Session.QueueItemPose((byte)i, new NetVec3(i, 1f, 0f), NetQuat.Identity, item.WireEpoch, item.HeldLocally);
                    if (item.Tick(now, 0f, out byte handoffEpoch))
                        Session.SendItemHandoff((byte)i, handoffEpoch, new NetVec3(i, 1f, 0f), NetQuat.Identity, NetVec3.Zero, NetVec3.Zero);
                }
            }
        }
    }
}
