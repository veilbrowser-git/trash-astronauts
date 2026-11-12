using System;
using System.Collections.Generic;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op stage 2 on the host: the requests the crew sends and everything the host's game decides that the
    /// crew has to hear. Requests come in through one public method per IHostWorld call (same numeric ids);
    /// every host-side change goes out through <see cref="ICoopBroadcast"/> with the fields of its message.
    /// The rules themselves stay where they are (MissionDirector, CollectionLocker, SlidingDoor, Cuttable):
    /// this class only routes requests into them and listens to their events.
    ///
    /// While it exists the host's contracts are ride-along contracts (<see cref="MissionDirector.CoopRideAlong"/>:
    /// no creature, no blackouts, no webs). Plain C#; the owner calls <see cref="Tick"/> and
    /// <see cref="CollectItemPoses"/> from LateUpdate and <see cref="Dispose"/> when the session ends.
    /// </summary>
    public sealed class CoopHostRules : IDisposable
    {
        readonly MissionDirector m_Director;
        readonly ICoopBroadcast m_Out;
        readonly MissionSession m_Session;
        readonly CoopItemSampler m_Sampler;
        readonly List<SlidingDoor> m_Doors = new List<SlidingDoor>();
        readonly List<Cuttable> m_Panels = new List<Cuttable>();
        readonly List<SalvageItem> m_HookedItems = new List<SalvageItem>();
        readonly Dictionary<string, int> m_ItemIdBySalvage = new Dictionary<string, int>();
        readonly CollectionLocker m_Locker;
        MissionPhase m_LastPhase;
        PlayerId m_LastPanelBy = PlayerId.None;
        WorldStateMsg m_World;
        bool m_Disposed;

        /// <summary>Door and weld requests a crew member may make: a burst of this many...</summary>
        public const float RequestBurst = 6f;
        /// <summary>...refilled at this many per second (a real cutter or hand is far slower).</summary>
        public const float RequestsPerSecond = 4f;
        /// <summary>A door request counts only from a head this close to the door or one of its buttons (metres).</summary>
        public const float DoorReach = 4f;
        /// <summary>A weld report counts only from a head this close to that weld (the cutter's reach plus slack, metres).</summary>
        public const float WeldReach = 3.5f;

        readonly float[] m_Tokens = new float[NetProtocol.MaxPlayers];
        readonly double[] m_TokenTime = new double[NetProtocol.MaxPlayers];
        readonly bool[] m_TokenStarted = new bool[NetProtocol.MaxPlayers];

        /// <summary>Requests refused for coming too fast (spam) or from too far away (a modified client).</summary>
        public int RateLimited { get; private set; }
        public int TooFar { get; private set; }

        /// <summary>Seconds, for the request rate limit. NetSession sets the session clock; default unscaled time.</summary>
        public Func<double> Clock = () => Time.unscaledTimeAsDouble;

        public MissionDirector Director => m_Director;
        public CoopItemSampler Sampler => m_Sampler;
        /// <summary>The creature, its noise and darts, and the blackouts (stage 4).</summary>
        public CoopCreatureHost Creature { get; }

        /// <summary>
        /// Stage 4 switch: contracts started from now on carry the creature (any species) and blackouts.
        /// Off by default, which keeps the ride-along contract. Turned off again with the rules.
        /// </summary>
        public bool CreatureEnabled
        {
            get => m_Director.CoopCreatureEnabled;
            set => m_Director.CoopCreatureEnabled = value;
        }

        public CoopHostRules(MissionDirector director, ICoopBroadcast broadcast)
        {
            m_Director = director ?? throw new ArgumentNullException(nameof(director));
            m_Out = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            m_Session = director.Session;
            m_Sampler = new CoopItemSampler(director);
            m_Locker = director.locker;
            m_LastPhase = m_Session.Phase;
            director.CoopRideAlong = true;

            director.MissionStarted += OnMissionStarted;
            director.MissionEnded += OnMissionEnded;
            director.SalvageDamaged += OnSalvageDamaged;
            director.PanelOpened += OnPanelOpened;
            director.HazardPauseChanged += OnHazardPauseChanged;
            m_Session.SalvageCollected += OnSalvageCollected;
            m_Session.PanelOpened += OnSessionPanelOpened;
            m_Session.PhaseChanged += OnPhaseChanged;
            if (m_Locker != null) m_Locker.Accepted += OnLockerAccepted;
            m_Sampler.CameToRest += OnCameToRest;
            Creature = new CoopCreatureHost(director, broadcast);

            var ids = director.NetIds;
            for (int i = 0; i < ids.Doors.Count; i++)
            {
                var door = director.DoorAt(i);
                if (door == null) continue;
                door.Changed += OnDoorChanged;
                m_Doors.Add(door);
            }
            for (int i = 0; i < ids.Panels.Count; i++)
            {
                var panel = director.PanelAt(i);
                if (panel == null) continue;
                panel.PointCut += OnPointCut;
                m_Panels.Add(panel);
            }
            if (m_Session.Phase == MissionPhase.Active) HookRunItems();
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (m_Director != null)
            {
                m_Director.CoopRideAlong = false;
                m_Director.CoopCreatureEnabled = false;
                m_Director.MissionStarted -= OnMissionStarted;
                m_Director.MissionEnded -= OnMissionEnded;
                m_Director.SalvageDamaged -= OnSalvageDamaged;
                m_Director.PanelOpened -= OnPanelOpened;
                m_Director.HazardPauseChanged -= OnHazardPauseChanged;
            }
            m_Session.SalvageCollected -= OnSalvageCollected;
            m_Session.PanelOpened -= OnSessionPanelOpened;
            m_Session.PhaseChanged -= OnPhaseChanged;
            if (m_Locker != null) m_Locker.Accepted -= OnLockerAccepted;
            m_Sampler.CameToRest -= OnCameToRest;
            Creature.Dispose();
            foreach (var door in m_Doors)
            {
                if (door != null) door.Changed -= OnDoorChanged;
            }
            foreach (var panel in m_Panels)
            {
                if (panel != null) panel.PointCut -= OnPointCut;
            }
            UnhookRunItems();
        }

        // ------------------------------------------------------------------ requests (IHostWorld)

        /// <summary>
        /// A crew member pressed START (IHostWorld.OnStartRequest). Decided as if the host had pressed it; the
        /// contract that starts is broadcast as RunStart. False if one is already running or it did not start.
        /// </summary>
        public bool HostStartRequest(byte slot)
        {
            if (m_Director.Phase == MissionPhase.Active) return false;
            return m_Director.StartMission(new PlayerId(slot));
        }

        /// <summary>
        /// A crew member asked to extract (IHostWorld.OnExtractRequest). Every connected player who is not
        /// Away has to have their head inside the tug; otherwise the requester is told who is missing
        /// (ExtractRefused) and nothing happens. With one player this is the offline rule.
        /// </summary>
        public MissionResult HostExtractRequest(byte slot)
        {
            if (m_Director.Phase != MissionPhase.Active) return null;
            int missing = m_Director.MissingAboardMask();
            if (missing != 0)
            {
                m_Out.ExtractRefused(slot, (byte)(missing & 0xFF));
                return null;
            }
            return m_Director.Extract(new PlayerId(slot));
        }

        /// <summary>
        /// A crew member wants this door open or shut (IHostWorld.OnDoorRequest). A desired state, never a
        /// toggle: two players opening the same door from both sides leave it open. Locked doors ignore it.
        /// The change, if any, goes out as DoorState from the door's own event. False if nothing was done.
        /// </summary>
        public bool HostDoorRequest(byte doorId, bool desiredOpen, byte by)
        {
            if (!TakeToken(by)) return false;
            var door = m_Director.DoorAt(doorId);
            if (door == null || door.locked) return false;
            if (!WithinReach(by, door, DoorReach)) return false;
            if (door.IsOpen == desiredOpen) return false;
            door.SetOpen(desiredOpen, false, new PlayerId(by));
            return true;
        }

        /// <summary>
        /// A crew member's cutter finished one weld (IHostWorld.OnPointCutReport). It is burnt through here
        /// (and broadcast as PointCut); once every weld of the panel is through the host opens it, rolling the
        /// plate's push itself, which goes out with PanelOpened. Repeats and reports for open panels do nothing.
        /// </summary>
        public bool HostPointCutReport(byte panel, byte index, byte by)
        {
            if (m_Director.Phase != MissionPhase.Active) return false;
            if (!TakeToken(by)) return false;
            var cuttable = m_Director.PanelAt(panel);
            if (cuttable == null || cuttable.isTraining || cuttable.IsOpen) return false;
            if (cuttable.cutPoints == null || index >= cuttable.cutPoints.Length || cuttable.cutPoints[index] == null) return false;
            if (!WithinReach(by, cuttable.cutPoints[index].transform.position, WeldReach)) return false;
            var who = new PlayerId(by);
            bool changed = cuttable.ApplyRemotePointCut(index, who);
            if (cuttable.CutCount >= cuttable.TotalCuts) m_Director.RequestOpenPanel(cuttable, who);
            return changed;
        }

        /// <summary>A crew member went Away or came back (IHostWorld.OnPlayerAway): not counted for oxygen or extraction while away.</summary>
        public void HostPlayerAway(byte slot, bool away)
        {
            var player = m_Director.FindPlayer(new PlayerId(slot));
            if (player != null && player.IsRemote) player.IsAway = away;
        }

        // ------------------------------------------------------------------ trust (a modified client in a public game)

        /// <summary>
        /// Token bucket per slot: <see cref="RequestBurst"/> at once, refilled at <see cref="RequestsPerSecond"/>.
        /// A request over the limit is dropped, so a spamming client cannot flood the crew's reliable streams.
        /// </summary>
        bool TakeToken(byte slot)
        {
            if (slot >= m_Tokens.Length) return false;
            double now = Clock != null ? Clock() : Time.unscaledTimeAsDouble;
            if (!m_TokenStarted[slot])
            {
                m_TokenStarted[slot] = true;
                m_Tokens[slot] = RequestBurst;
            }
            else
            {
                double dt = now - m_TokenTime[slot];
                if (dt > 0) m_Tokens[slot] = Mathf.Min(RequestBurst, m_Tokens[slot] + (float)(dt * RequestsPerSecond));
            }
            m_TokenTime[slot] = now;
            if (m_Tokens[slot] < 1f)
            {
                RateLimited++;
                return false;
            }
            m_Tokens[slot] -= 1f;
            return true;
        }

        bool WithinReach(byte slot, Vector3 target, float reach)
        {
            var player = m_Director.FindPlayer(new PlayerId(slot));
            if (player != null && (player.HeadPosition - target).sqrMagnitude <= reach * reach) return true;
            TooFar++;
            return false;
        }

        bool WithinReach(byte slot, SlidingDoor door, float reach)
        {
            var player = m_Director.FindPlayer(new PlayerId(slot));
            if (player != null)
            {
                Vector3 head = player.HeadPosition;
                float r2 = reach * reach;
                if ((head - door.transform.position).sqrMagnitude <= r2) return true;
                if (door.buttons != null)
                {
                    for (int i = 0; i < door.buttons.Length; i++)
                        if (door.buttons[i] != null && (head - door.buttons[i].transform.position).sqrMagnitude <= r2) return true;
                }
            }
            TooFar++;
            return false;
        }

        /// <summary>A crew member's noise (IHostWorld.OnNoise): see <see cref="CoopCreatureHost.HostNoise"/>.</summary>
        public bool HostNoise(NetVec3 position, float loudness, NoiseKind kind, byte bySlot) => Creature.HostNoise(position, loudness, kind, bySlot);

        /// <summary>A crew member's dart hit (IHostWorld.OnTranqHitReport): see <see cref="CoopCreatureHost.HostTranqHitReport"/>.</summary>
        public bool HostTranqHitReport(NetVec3 origin, NetVec3 hitPoint, byte bySlot) => Creature.HostTranqHitReport(origin, hitPoint, bySlot);

        /// <summary>The CreatureState sample due now, if any (10/20 Hz): see <see cref="CoopCreatureHost.TrySampleCreature"/>.</summary>
        public bool TrySampleCreature(double now, out CreatureSnapshot snapshot) => Creature.TrySampleCreature(now, out snapshot);

        // ------------------------------------------------------------------ state for the adapter

        /// <summary>The current door, panel, weld and web state (IHostWorld.FillWorldState). <paramref name="state"/> may arrive dirty.</summary>
        public void FillWorldState(ref WorldStateMsg state)
        {
            state.Clear();
            var ids = m_Director.NetIds;
            for (int i = 0; i < ids.Doors.Count && i < 16; i++)
            {
                var door = m_Director.DoorAt(i);
                if (door != null && door.IsOpen) state.doorOpenMask |= (ushort)(1 << i);
            }
            for (int i = 0; i < ids.Panels.Count; i++)
            {
                var panel = m_Director.PanelAt(i);
                if (panel == null) continue;
                if (panel.IsOpen && i < 16) state.panelOpenMask |= (ushort)(1 << i);
                if (panel.cutPoints == null) continue;
                int offset = ids.CutPointOffset(i);
                for (int k = 0; k < panel.cutPoints.Length; k++)
                {
                    if (panel.cutPoints[k] != null && panel.cutPoints[k].IsCut) state.SetWeldDone(offset + k);
                }
            }
            for (int i = 0; i < ids.Wires.Count && i < 32; i++)
            {
                var wire = m_Director.WireAt(i);
                if (wire == null) continue;
                if (wire.Burnt) state.wireBurntMask |= 1u << i;
                else if (wire.Snapped) state.wireSnappedMask |= 1u << i;
            }
        }

        /// <summary>What the Clock message carries (2 Hz): mission time, the shared tank, and whether hazards are paused.</summary>
        public void ReadClock(out float elapsedSeconds, out float oxygenRemainingSeconds, out byte flags)
        {
            elapsedSeconds = m_Session.ElapsedSeconds;
            oxygenRemainingSeconds = m_Session.Oxygen.Remaining;
            flags = m_Director.HazardsPaused ? ClockFlags.HazardsPaused : (byte)0;
        }

        /// <summary>Advances item rest detection (ItemRest goes out from here). Call once per frame or send tick.</summary>
        public void Tick(float dt)
        {
            if (m_Director.Phase == MissionPhase.Active) m_Sampler.Tick(dt);
        }

        /// <summary>This tick's item poses for the stream (see <see cref="CoopItemSampler.Collect"/>). 0 outside a contract.</summary>
        public int CollectItemPoses(ItemPoseEntry[] output)
        {
            return m_Director.Phase == MissionPhase.Active ? m_Sampler.Collect(output) : 0;
        }

        // ------------------------------------------------------------------ the host's own changes

        void OnMissionStarted()
        {
            HookRunItems();
            m_Sampler.ResetForRun();
            var run = m_Director.CurrentRun;
            m_Out.RunStart(in run);
            FillWorldState(ref m_World);
            m_Out.WorldState(in m_World);
        }

        void OnMissionEnded(MissionResult result)
        {
            var message = RunEndedMsg.From(result);
            m_Out.RunEnded(in message);
        }

        void OnPhaseChanged(MissionPhase phase)
        {
            if (phase == MissionPhase.Hub && m_LastPhase == MissionPhase.Results) m_Out.ReturnedToHub();
            m_LastPhase = phase;
        }

        void OnHazardPauseChanged(bool paused) => m_Out.HostPause(paused);

        void OnSalvageCollected(SalvageRecord record)
        {
            if (record != null && m_ItemIdBySalvage.TryGetValue(record.Id, out int itemId))
                m_Out.ItemCollected((byte)itemId, Slot(record.CollectedBy));
        }

        void OnSalvageDamaged(SalvageItem item, int value)
        {
            if (item != null && item.NetIndex >= 0) m_Out.ItemValue((byte)item.NetIndex, value);
        }

        void OnLockerAccepted(SalvageItem item, int trickBonus, int slotIndex)
        {
            if (item == null || item.NetIndex < 0 || !m_Session.TryGetSalvage(item.SalvageId, out var record)) return;
            m_Out.ItemDeposited((byte)item.NetIndex, Slot(record.DepositedBy), record.Value, trickBonus, (byte)Mathf.Clamp(slotIndex, 0, 255));
        }

        void OnItemRecovered(SalvageItem item)
        {
            if (item != null && item.NetIndex >= 0) m_Out.ItemRecovered((byte)item.NetIndex, CoopConvert.ToNet(item.transform.position));
        }

        void OnCameToRest(SalvageItem item)
        {
            if (item != null && item.NetIndex >= 0)
                m_Out.ItemRest((byte)item.NetIndex, CoopConvert.ToNet(item.transform.position), CoopConvert.ToNet(item.transform.rotation));
        }

        void OnDoorChanged(SlidingDoor door)
        {
            int id = m_Director.NetIds.Doors.IndexOf(door.doorId);
            if (id >= 0 && m_Director.DoorAt(id) == door) m_Out.DoorState((byte)id, door.IsOpen);
        }

        void OnPointCut(Cuttable panel, int index, PlayerId by)
        {
            if (m_Director.Phase != MissionPhase.Active || index < 0) return;
            int id = m_Director.NetIds.Panels.IndexOf(panel.panelId);
            if (id >= 0) m_Out.PointCut((byte)id, (byte)Mathf.Min(index, 255));
        }

        // The ledger hears of an opening (with who did it) just before the panel comes off and rolls its push.
        void OnSessionPanelOpened(string panelId, PlayerId by) => m_LastPanelBy = by;

        void OnPanelOpened(Cuttable panel)
        {
            int id = m_Director.NetIds.Panels.IndexOf(panel.panelId);
            if (id < 0) return;
            m_Out.PanelOpened((byte)id, Slot(m_LastPanelBy), CoopConvert.ToNet(panel.LastPlateImpulse), CoopConvert.ToNet(panel.LastPlateTorque));
            m_LastPanelBy = PlayerId.None;
        }

        void HookRunItems()
        {
            UnhookRunItems();
            m_ItemIdBySalvage.Clear();
            var items = m_Director.RunItems;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                m_ItemIdBySalvage[item.SalvageId] = i;
                item.Recovered += OnItemRecovered;
                m_HookedItems.Add(item);
            }
        }

        void UnhookRunItems()
        {
            foreach (var item in m_HookedItems)
            {
                if (item != null) item.Recovered -= OnItemRecovered;
            }
            m_HookedItems.Clear();
        }

        /// <summary>A player id as a wire slot; anything that is not a crew slot (nobody) is sent as 255.</summary>
        static byte Slot(PlayerId id) => id.Value >= 0 && id.Value < 255 ? (byte)id.Value : (byte)255;
    }
}
