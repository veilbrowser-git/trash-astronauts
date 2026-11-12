using System;
using System.Collections;
using System.Collections.Generic;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using static TrashAstronauts.Net.CoopConvert;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Test doubles for co-op stage 3 ("everyone grabs") in the real scene. The scene's own NetSession is left
    /// alone; instead a test rig owns real net-core sessions over an in-memory hub and plays the part of the
    /// session adapter, with exactly the one-line forwards the adapter needs:
    ///  - <see cref="CoopHostRig"/>: the scene is the host. A real NetSessionHost arbitrates through its
    ///    ItemOwnershipTable; its IHostWorld item callbacks go to <see cref="CoopItemHost"/>, the game's
    ///    broadcasts (ICoopBroadcast) go to its Send* methods. Crew members are bots (NetSessionClient +
    ///    BotClientWorld, the plain-C# client the net core ships) with head-only stand-ins in the scene.
    ///  - <see cref="CoopClientRig"/>: the scene is a client. A real NetSessionClient's IClientWorld goes to
    ///    CoopClientApply, and <see cref="CoopItemClient"/>'s requests (ICoopItemRequests) go to its Send*
    ///    methods. The host is the net core's ScriptedHostWorld behind a real NetSessionHost (grabs allowed,
    ///    first pickups broadcast); another crew member is a bot.
    /// Both pump their sessions every frame (receive in Update, send in LateUpdate), on unscaled time.
    /// </summary>
    public sealed class CoopFakeNet : IMissionNet
    {
        public bool client;
        public int slot;
        public readonly List<string> calls = new List<string>();
        public bool IsClient => client;
        public bool IsHost => !client;
        public int LocalSlot => slot;
        public void RequestStart() => calls.Add("start");
        public void RequestExtract() => calls.Add("extract");
        public void RequestDoor(SlidingDoor door, bool desiredOpen) => calls.Add("door");
        public void ReportPointCut(Cuttable panel, int pointIndex) => calls.Add("cut");
        public void ReportSalvageDamage(SalvageItem item, int newValue) => calls.Add("damage " + item.NetIndex + " " + newValue);
        public void ReportWire(WebTripwire wire, WireChange change) => calls.Add("wire");
        public void ReportTranqHit(Vector3 origin, Vector3 hitPoint) => calls.Add("tranq");
        public void ForwardNoise(in NoiseEvent noise) => calls.Add("noise");
        public void ReportPause(bool paused) => calls.Add("pause " + paused);
        public void ReportSettingsChanged(Handedness handedness) => calls.Add("settings");
    }

    /// <summary>A crew member on another machine, as the net core's bot: a real session and its item state machines.</summary>
    public sealed class CoopBot
    {
        public MemoryTransport Transport;
        public BotClientWorld World;
        public NetSessionClient Session;
        public PlayerContext Proxy;
        public byte Slot => Session.LocalSlot;
        public ItemAuthority Item(byte id) => World.Items[id];
        public Vector3 SeenAt(byte id) => ToUnity(World.ItemPoses[id].position);
    }

    /// <summary>Calls the rig every frame: receive early, send late (like NetSession).</summary>
    public sealed class CoopRigPump : MonoBehaviour
    {
        public Action<double> receive, send;
        void Update() { receive?.Invoke(Time.unscaledDeltaTime); }
        void LateUpdate() { send?.Invoke(0); }
    }

    public abstract class CoopRigBase : IDisposable
    {
        public const uint Content = 0x5A17A6E3u;
        public readonly MemoryTransportHub Hub;
        public readonly MissionDirector Director;
        public readonly List<CoopBot> Bots = new List<CoopBot>();
        protected readonly MemoryTransport m_HostTransport;
        CoopRigPump m_Pump;
        int m_Seed = 300;

        public double Now => 1000.0 + Hub.Now;

        /// <summary>While true the frame pump does nothing: no time passes on the network and nothing arrives.</summary>
        public bool Paused;

        protected CoopRigBase(MissionDirector director, int seed)
        {
            Director = director;
            Hub = new MemoryTransportHub(seed);
            m_HostTransport = Hub.Create(NetProtocol.HostPort);
        }

        protected abstract NetSessionHost HostSession { get; }

        /// <summary>Starts pumping every frame.</summary>
        public void StartPump()
        {
            if (m_Pump != null) return;
            m_Pump = new GameObject("Coop Rig Pump").AddComponent<CoopRigPump>();
            m_Pump.receive = dt => { if (!Paused) Receive(dt); };
            m_Pump.send = _ => { if (!Paused) Send(); };
        }

        /// <summary>One step by hand (no frame passes): receive, then send.</summary>
        public void Step(double dt)
        {
            Receive(dt);
            Send();
        }

        protected virtual void Receive(double dt)
        {
            Hub.Advance(Math.Max(0.0, dt));
            double now = Now;
            HostSession.Poll(now);
            foreach (var bot in Bots)
            {
                bot.World.Now = now;
                bot.Session.Poll(now);
            }
        }

        protected virtual void Send()
        {
            double now = Now;
            foreach (var bot in Bots) bot.Session.SendTick(now);
            HostSession.SendTick(now);
        }

        public CoopBot AddBot(Vector3 headAt)
        {
            var bot = new CoopBot { Transport = Hub.Create(), World = new BotClientWorld() };
            bot.Session = new NetSessionClient(bot.Transport, bot.World, new NetClientConfig { contentHash = Content, seed = m_Seed++ });
            bot.World.Session = bot.Session;
            Bots.Add(bot);
            bot.Session.Connect(m_HostTransport.Local, Now);
            for (int i = 0; i < 500 && !bot.Session.Welcomed; i++) Step(0.01);
            if (!bot.Session.Welcomed) throw new InvalidOperationException("bot did not join");
            // Its stand-in in this scene: a head-only crew member (what RemoteAvatar registers).
            var go = new GameObject("Bot Crew " + bot.Slot);
            go.transform.position = headAt - Vector3.up * 1.6f;
            var head = new GameObject("Head").transform;
            head.SetParent(go.transform, false);
            head.localPosition = Vector3.up * 1.6f;
            var player = go.AddComponent<PlayerContext>();
            player.head = head;
            player.AssignId(new PlayerId(bot.Slot), true);
            bot.Proxy = player;
            return bot;
        }

        public void MoveHead(CoopBot bot, Vector3 headAt)
        {
            if (bot.Proxy != null) bot.Proxy.transform.position = headAt - Vector3.up * 1.6f;
        }

        // ---- what a bot's hand does (the plain-C# equivalent of ItemSync)

        public bool BotGrab(CoopBot bot, byte id)
        {
            if (!bot.Item(id).BeginGrab(out byte epoch)) return false;
            bot.Session.SendGrabRequest(id, epoch, 1);
            bot.Session.Flush(Now);
            return true;
        }

        public void BotPose(CoopBot bot, byte id, Vector3 at)
        {
            var a = bot.Item(id);
            if (!a.ShouldStream) return;
            bot.Session.QueueItemPose(id, ToNet(at), NetQuat.Identity, a.WireEpoch, a.HeldLocally);
        }

        public bool BotRelease(CoopBot bot, byte id, Vector3 at, Vector3 releasedFrom)
        {
            var a = bot.Item(id);
            if (!a.Release(Now)) return false;
            bot.Session.SendItemReleased(id, a.WireEpoch, ToNet(at), NetQuat.Identity, ToNet(releasedFrom));
            bot.Session.Flush(Now);
            return true;
        }

        public void BotThrown(CoopBot bot, byte id, Vector3 velocity)
        {
            var a = bot.Item(id);
            bot.Session.SendItemThrown(id, a.WireEpoch, ToNet(velocity), default(NetVec3), velocity.magnitude, velocity.magnitude > 0.8f);
            bot.Session.Flush(Now);
        }

        /// <summary>The bot's flight is over: ItemHandoff at this pose and velocity (the hand-back timer, run to its end).</summary>
        public bool BotHandoff(CoopBot bot, byte id, Vector3 at, Vector3 velocity)
        {
            var a = bot.Item(id);
            if (!a.Tick(Now + ItemAuthority.HandbackMaxSeconds + 1.0, 0f, out byte epoch)) return false;
            bot.Session.SendItemHandoff(id, epoch, ToNet(at), NetQuat.Identity, ToNet(velocity), default(NetVec3));
            bot.Session.Flush(Now);
            return true;
        }

        public virtual void Dispose()
        {
            if (m_Pump != null) UnityEngine.Object.Destroy(m_Pump.gameObject);
            m_Pump = null;
            foreach (var bot in Bots)
            {
                bot.Session.Dispose();
                if (bot.Proxy != null) UnityEngine.Object.Destroy(bot.Proxy.gameObject);
            }
            Bots.Clear();
        }
    }

    /// <summary>The scene hosts: a real NetSessionHost wired to CoopHostRules and CoopItemHost (the adapter's forwards).</summary>
    public sealed class CoopHostRig : CoopRigBase, IHostWorld, ICoopBroadcast
    {
        public readonly NetSessionHost Host;
        public readonly CoopHostRules Rules;
        public readonly CoopItemHost Items;
        public readonly List<string> Log = new List<string>();
        readonly ItemPoseEntry[] m_Poses = new ItemPoseEntry[16];
        float m_PendingTick;

        protected override NetSessionHost HostSession => Host;

        public CoopHostRig(MissionDirector director, int seed = 7) : base(director, seed)
        {
            Host = new NetSessionHost(m_HostTransport, this, new NetHostConfig { contentHash = Content, tugNumber = 4821, seed = seed });
            director.Net = new CoopFakeNet { client = false, slot = 0 };
            Rules = new CoopHostRules(director, this);
            Rules.Clock = () => Now;
            Items = new CoopItemHost(director, this);
        }

        protected override void Receive(double dt)
        {
            base.Receive(dt);
            m_PendingTick += (float)dt;
        }

        protected override void Send()
        {
            // The host's own items: rest detection and this tick's poses (what NetSession does in LateUpdate).
            Rules.Tick(m_PendingTick);
            m_PendingTick = 0f;
            int n = Rules.CollectItemPoses(m_Poses);
            for (int i = 0; i < n; i++) Host.QueueItemPose(m_Poses[i].itemId, m_Poses[i].position, m_Poses[i].rotation, m_Poses[i].held, m_Poses[i].atRest);
            base.Send();
        }

        public int Count(string prefix) { int n = 0; foreach (var l in Log) if (l.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }

        public override void Dispose()
        {
            base.Dispose();
            Items.Dispose();
            Rules.Dispose();
            Host.Dispose();
            if (Director != null) Director.Net = null;
        }

        // ================= IHostWorld: the session adapter's forwards (items to CoopItemHost, the rest to CoopHostRules)
        public MissionPhase Phase => Director.Phase;
        public void FillWorldState(ref WorldStateMsg state) => Rules.FillWorldState(ref state);
        public void OnPlayerJoined(byte slot, byte handedness) => Log.Add("joined " + slot);
        public void OnPlayerLeft(byte slot, PlayerLeaveReason reason) => Log.Add("left " + slot);
        public void OnPlayerAway(byte slot, bool away) { Log.Add("away " + slot + " " + away); Rules.HostPlayerAway(slot, away); }
        public void OnPlayerSettings(byte slot, byte handedness) { }
        public void OnAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) { }
        public void OnNoise(byte slot, NetVec3 position, float loudness, NoiseKind kind) { }
        public void OnStartRequest(byte slot) => Rules.HostStartRequest(slot);
        public void OnExtractRequest(byte slot) => Rules.HostExtractRequest(slot);
        public bool CanGrabItem(byte itemId, byte slot) => Items.CanGrabItem(itemId, slot);
        public void OnItemGranted(byte itemId, byte slot, byte hand) { Log.Add("granted " + itemId + " to " + slot); Items.OnItemGranted(itemId, slot, hand); }
        public void OnItemPose(byte itemId, byte slot, NetVec3 position, NetQuat rotation, bool held, double hostTime) => Items.OnItemPose(itemId, slot, position, rotation, held, hostTime);
        public void OnItemReleased(byte itemId, byte slot, NetVec3 position, NetQuat rotation, NetVec3 releasePosition) { Log.Add("released " + itemId + " by " + slot); Items.OnItemReleased(itemId, slot, position, rotation, releasePosition); }
        public void OnItemThrown(byte itemId, byte slot, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown) { Log.Add("thrown " + itemId + " by " + slot); Items.OnItemThrown(itemId, slot, linearVelocity, angularVelocity, throwSpeed, wasThrown); }
        public void OnItemReturned(in ItemReturn item) { Log.Add("returned " + item.itemId + " " + item.cause); Items.OnItemReturned(in item); }
        public void OnItemDamageReport(byte itemId, byte slot, int newValue) { Log.Add("damagereport " + itemId + " " + newValue); Items.OnItemDamageReport(itemId, slot, newValue); }
        public void OnDoorRequest(byte slot, byte doorId, bool desiredOpen) => Rules.HostDoorRequest(doorId, desiredOpen, slot);
        public void OnPointCutReport(byte slot, byte panel, byte index) => Rules.HostPointCutReport(panel, index, slot);
        public void OnTranqHitReport(byte slot, NetVec3 origin, NetVec3 hitPoint) { }
        public void OnWireReport(byte slot, byte index, NetWireChange change) { }

        // ================= ICoopBroadcast: each to the NetSessionHost method of the same name
        public void RunStart(in RunStartParams parameters) { Log.Add("runstart"); Host.SendRunStart(in parameters); }
        public void WorldState(in WorldStateMsg state) => Host.SendWorldState(in state);
        public void ExtractRefused(byte slot, byte missingSlotsMask) => Host.SendExtractRefused(slot, missingSlotsMask);
        public void RunEnded(in RunEndedMsg result) => Host.SendRunEnded(in result);
        public void ReturnedToHub() => Host.SendReturnedToHub();
        public void HostPause(bool on) => Host.SendHostPause(on);
        public void ItemCollected(byte itemId, byte by) { Log.Add("collected " + itemId + " by " + by); Host.SendItemCollected(itemId, by); }
        public void ItemValue(byte itemId, int value) { Log.Add("value " + itemId + " " + value); Host.SendItemValue(itemId, value); }
        public void ItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot)
        {
            Log.Add("deposited " + itemId + " by " + by + " +" + trickBonus);
            Host.SendItemDeposited(itemId, by, finalValue, trickBonus, lockerSlot);
        }
        public void ItemRecovered(byte itemId, NetVec3 position) { Log.Add("recovered " + itemId); Host.SendItemRecovered(itemId, position); }
        public void ItemRest(byte itemId, NetVec3 position, NetQuat rotation) => Host.SendItemRest(itemId, position, rotation);
        public void DoorState(byte doorId, bool open) => Host.SendDoorState(doorId, open);
        public void PointCut(byte panel, byte index) => Host.SendPointCut(panel, index);
        public void PanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque) => Host.SendPanelOpened(panel, by, plateImpulse, plateTorque);
        public bool LocalGrab(byte itemId)
        {
            bool ok = Host.LocalGrab(itemId);
            Log.Add("localgrab " + itemId + " " + ok);
            return ok;
        }
        public void LocalRelease(byte itemId) { Log.Add("localrelease " + itemId); Host.LocalRelease(itemId); }
        // The creature's messages (stage 4), each to the Send method of the same name (no creature in these rigs' contracts).
        public void CreatureStateChanged(EnemyState state, byte targetSlot) => Host.SendCreatureStateChanged(state, targetSlot);
        public void Tranquilized(float seconds) => Host.SendTranquilized(seconds);
        public void CatchStarted(byte victimSlot) => Host.SendCatchStarted(victimSlot);
        public void BlackoutStarted(float seconds) => Host.SendBlackoutStarted(seconds);
        public void BlackoutEnded(bool instant) => Host.SendBlackoutEnded(instant);
    }

    /// <summary>The scene is a client of a scripted host; CoopClientApply and CoopItemClient behind a real NetSessionClient.</summary>
    public sealed class CoopClientRig : CoopRigBase, IClientWorld, ICoopItemRequests
    {
        public readonly ScriptedHostWorld Scripted = new ScriptedHostWorld { AllowGrabs = true, AutoCollect = true };
        public readonly NetSessionHost Host;
        public readonly NetSessionClient Client;
        public readonly CoopClientApply Apply;
        public CoopItemClient Items { get; private set; }
        public readonly List<string> Sent = new List<string>();
        public readonly List<ItemAuthorityEvent> Events = new List<ItemAuthorityEvent>();
        public int PosesQueued;
        public Vector3 LastQueuedPose;
        readonly PlayerContext m_Player;
        readonly MemoryTransport m_ClientTransport;

        protected override NetSessionHost HostSession => Host;

        public CoopClientRig(MissionDirector director, PlayerContext player, double oneWayLatency = 0, int seed = 9) : base(director, seed)
        {
            m_Player = player;
            Hub.Latency = oneWayLatency;
            Host = new NetSessionHost(m_HostTransport, Scripted, new NetHostConfig { contentHash = Content, tugNumber = 4821, seed = seed });
            Scripted.Session = Host;
            m_ClientTransport = Hub.Create();
            Client = new NetSessionClient(m_ClientTransport, this, new NetClientConfig { contentHash = Content, seed = 77 });
            Apply = new CoopClientApply(director);
        }

        /// <summary>Joins the scene to the host (it takes the slot the host gives).</summary>
        public void JoinScene()
        {
            Client.Connect(m_HostTransport.Local, Now);
            for (int i = 0; i < 1000 && !Client.Welcomed; i++) Step(0.01);
            if (!Client.Welcomed) throw new InvalidOperationException("scene did not join");
        }

        /// <summary>The host starts this contract; steps until it has begun here and on every bot.</summary>
        public void StartRun(in RunStartParams run)
        {
            Scripted.CurrentPhase = MissionPhase.Active;
            Host.SendRunStart(in run);
            for (int i = 0; i < 1000; i++)
            {
                bool all = Director.Phase == MissionPhase.Active;
                foreach (var bot in Bots) all &= bot.World.Items.Length == run.items.Length;
                if (all) break;
                Step(0.01);
            }
        }

        protected override void Receive(double dt)
        {
            Hub.Advance(Math.Max(0.0, dt));
            double now = Now;
            Host.Poll(now);
            Client.Poll(now);
            foreach (var bot in Bots)
            {
                bot.World.Now = now;
                bot.Session.Poll(now);
            }
        }

        protected override void Send()
        {
            double now = Now;
            Client.SendTick(now);
            foreach (var bot in Bots) bot.Session.SendTick(now);
            Host.SendTick(now);
        }

        public int Count(string prefix) { int n = 0; foreach (var l in Sent) if (l.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }

        public override void Dispose()
        {
            base.Dispose();
            if (Items != null) Items.Dispose();
            Client.Dispose();
            Host.Dispose();
            if (Director != null) Director.Net = null;
        }

        // ================= IClientWorld: the session adapter's forwards (to CoopClientApply)
        public void OnWelcome(byte localSlot, in WelcomeMsg welcome)
        {
            m_Player.AssignId(new PlayerId(localSlot), false);
            Director.Net = new CoopFakeNet { client = true, slot = localSlot };
            // Created with the session: from here on this client's hands share the run's items.
            Items = new CoopItemClient(Director, this);
        }
        public void OnRosterChanged(byte slot, RosterChange change, byte handedness) { }
        public void OnWorldState(in WorldStateMsg state) => Apply.ApplyWorldState(in state);
        public void OnHostPause(bool on) => Apply.ApplyHostPause(on);
        public void OnHostSilent(bool silent) => Apply.ApplyHostSilent(silent);
        public void OnSessionEnded(SessionEndReason reason) => Sent.Add("ended " + reason);
        public void OnRemoteAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) { }
        public void OnCreatureState(in CreatureSnapshot creature, double hostTime) { }
        public void OnItemPose(in ItemPoseEntry pose, double hostTime) => Apply.ApplyItemPose(in pose, hostTime);
        public void OnClock(float elapsedSeconds, float oxygenRemainingSeconds, byte flags) => Apply.ApplyClock(elapsedSeconds, oxygenRemainingSeconds, flags);
        public void OnRunStart(in RunStartParams parameters) => Apply.ApplyRunStart(in parameters);
        public void OnExtractRefused(byte missingSlotsMask) => Apply.ApplyExtractRefused(missingSlotsMask);
        public void OnRunEnded(in RunEndedMsg result) => Apply.ApplyRunEnded(in result);
        public void OnReturnedToHub() => Apply.ApplyReturnedToHub();
        public void OnItemOwner(byte itemId, byte ownerSlot, byte epoch, bool held, bool denied) => Events.Add(Apply.ApplyItemOwner(itemId, ownerSlot, epoch, held, denied));
        public void OnItemCollected(byte itemId, byte by) => Apply.ApplyItemCollected(itemId, by);
        public void OnItemRest(byte itemId, byte epoch, NetVec3 position, NetQuat rotation) => Apply.ApplyItemRest(itemId, epoch, position, rotation);
        public void OnItemValue(byte itemId, int value) => Apply.ApplyItemValue(itemId, value);
        public void OnItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot) => Apply.ApplyItemDeposited(itemId, by, finalValue, trickBonus, lockerSlot);
        public void OnItemRecovered(byte itemId, NetVec3 position) => Apply.ApplyItemRecovered(itemId, position);
        public void OnDoorState(byte doorId, bool open) => Apply.ApplyDoorState(doorId, open);
        public void OnPointCut(byte panel, byte index) => Apply.ApplyPointCut(panel, index);
        public void OnPanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque) => Apply.ApplyPanelOpened(panel, by, plateImpulse, plateTorque);
        public void OnCreatureStateChanged(EnemyState state, byte targetSlot) { }
        public void OnTranquilized(float seconds) { }
        public void OnCatchStarted(byte victimSlot) { }
        public void OnTranqTracer(byte by, NetVec3 from, NetVec3 to) { }
        public void OnBlackoutStarted(float seconds) { }
        public void OnBlackoutEnded(bool instant) { }
        public void OnWireChanged(byte index, NetWireChange change, byte by) { }

        // ================= ICoopItemRequests: each to the NetSessionClient method of the matching name
        public void GrabRequest(byte itemId, byte epochSeen, byte hand)
        {
            Sent.Add("grab " + itemId);
            Client.SendGrabRequest(itemId, epochSeen, hand);
            Client.Flush(Now);
        }
        public void ItemReleased(byte itemId, byte epoch, NetVec3 position, NetQuat rotation, NetVec3 releasePosition)
        {
            Sent.Add("released " + itemId);
            Client.SendItemReleased(itemId, epoch, position, rotation, releasePosition);
            Client.Flush(Now);
        }
        public void ItemThrown(byte itemId, byte epoch, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown)
        {
            Sent.Add("thrown " + itemId);
            Client.SendItemThrown(itemId, epoch, linearVelocity, angularVelocity, throwSpeed, wasThrown);
            Client.Flush(Now);
        }
        public void ItemHandoff(byte itemId, byte epoch, NetVec3 position, NetQuat rotation, NetVec3 linearVelocity, NetVec3 angularVelocity)
        {
            Sent.Add("handoff " + itemId);
            Client.SendItemHandoff(itemId, epoch, position, rotation, linearVelocity, angularVelocity);
            Client.Flush(Now);
        }
        public void ItemDamageReport(byte itemId, int newValue)
        {
            Sent.Add("damage " + itemId + " " + newValue);
            Client.SendItemDamageReport(itemId, newValue);
        }
        public void QueueItemPose(byte itemId, NetVec3 position, NetQuat rotation, byte epoch, bool held)
        {
            PosesQueued++;
            LastQueuedPose = ToUnity(position);
            Client.QueueItemPose(itemId, position, rotation, epoch, held);
        }
    }

    /// <summary>Simulated tracked controllers driving the rig's real hands through the real input bindings and XRI.</summary>
    public sealed class SimHands : IDisposable
    {
        public sealed class Controller
        {
            public XRSimulatedController device;
            public XRSimulatedControllerState state;
            public HandRig hand;
            public Vector3 at;
        }

        public readonly Controller Right, Left;
        readonly InputSettings.BackgroundBehavior m_OldBackground;
        readonly InputSettings.EditorInputBehaviorInPlayMode m_OldEditorBehavior;

        public SimHands(PlayerContext player)
        {
            m_OldBackground = InputSystem.settings.backgroundBehavior;
            m_OldEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            Right = Add("Test Right Controller", UnityEngine.InputSystem.CommonUsages.RightHand, player.rightHand);
            Left = Add("Test Left Controller", UnityEngine.InputSystem.CommonUsages.LeftHand, player.leftHand);
        }

        static Controller Add(string name, InternedString usage, HandRig hand)
        {
            var device = InputSystem.AddDevice<XRSimulatedController>(name);
            InputSystem.SetDeviceUsage(device, usage);
            return new Controller
            {
                device = device,
                hand = hand,
                state = new XRSimulatedControllerState { isTracked = true, trackingState = 3, deviceRotation = Quaternion.identity },
            };
        }

        public void Dispose()
        {
            if (Right.device != null) InputSystem.RemoveDevice(Right.device);
            if (Left.device != null) InputSystem.RemoveDevice(Left.device);
            InputSystem.settings.backgroundBehavior = m_OldBackground;
            InputSystem.settings.editorInputBehaviorInPlayMode = m_OldEditorBehavior;
        }

        /// <summary>Queues the controller so its grab point is at this world position (takes effect next frame).</summary>
        public static void Place(Controller c, Vector3 grabPointWorld)
        {
            c.at = grabPointWorld;
            Transform space = c.hand.transform.parent;
            Quaternion rotation = space.rotation;
            Vector3 grabOffset = c.hand.transform.InverseTransformPoint(c.hand.interactor.transform.position);
            Vector3 controllerWorld = grabPointWorld - rotation * grabOffset;
            c.state.devicePosition = space.InverseTransformPoint(controllerWorld);
            c.state.deviceRotation = Quaternion.Inverse(space.rotation) * rotation;
            InputSystem.QueueStateEvent(c.device, c.state);
        }

        public static void SetGrip(Controller c, bool down)
        {
            c.state.grip = down ? 1f : 0f;
            c.state = c.state.WithButton(ControllerButton.GripButton, down);
            InputSystem.QueueStateEvent(c.device, c.state);
        }

        public static IEnumerator Settle(int frames = 3)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
        }

        public static IEnumerator Pose(Controller c, Vector3 at)
        {
            Place(c, at);
            yield return Settle();
        }

        public static IEnumerator Grip(Controller c, bool down)
        {
            SetGrip(c, down);
            yield return Settle();
        }
    }
}
