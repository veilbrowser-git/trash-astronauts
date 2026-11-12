using TrashAstronauts.Audio;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Shoving crew mates with your hands (<see cref="CrewPushMsg"/>).
    ///
    /// The pusher's machine does the detecting: an empty hand (open or a fist; not one holding a tool, an item or a
    /// rail) that moves into another player's suit (helmet, torso, backpack or upper arms, approximated by a few
    /// spheres and capsules worked out from the stand-in's pose: plain geometry, no colliders, so nothing else in the
    /// world is touched) is a contact. At or above <see cref="ShoveSpeed"/> it is a shove (a slightly bouncy collision
    /// of two equal 150 kg suits); slower, a gentle press that hands over half the closing speed, so you can nudge a
    /// friend along or ease yourself off them. Direction = the hand's velocity relative to the other player; both hands
    /// within <see cref="GatherSeconds"/> add up into one shove; one target can be shoved once per
    /// <see cref="CooldownSeconds"/>. The pushed player gets at most <see cref="CrewPushMsg.MaxTargetSpeed"/>, the pusher
    /// at most <see cref="CrewPushMsg.MaxPusherSpeed"/> back (Newton's third law), and both go through
    /// ZeroGLocomotion.AddImpulse, so the usual speed cap holds and the camera is never turned. Under gravity (the Moon
    /// cave) the vertical part is dropped: a shove slides you along the ground as a thrust would.
    ///
    /// The shove goes to the host (or straight to its target from the host); the TARGET's machine applies it here, in
    /// <see cref="OnCrewPush"/>, with a haptic bump on both controllers and a low thud. CREW SHOVES off in the settings:
    /// this player neither shoves (a hand still thuds, without recoil) nor is moved. A downed player
    /// (<see cref="CrewDowned"/>) neither shoves nor can be shoved. No shoving through walls: the line from the pusher's
    /// head to the contact must be clear of the environment.
    ///
    /// Lives next to the NetSession, added with <see cref="CoopToolSync"/> when the first crew stand-in appears, and
    /// removes itself once the session has closed. Single-player never creates it.
    /// </summary>
    public sealed class CrewShove : MonoBehaviour
    {
        /// <summary>Hand speed (m/s, relative to the other player) from which a contact is a shove rather than a press.</summary>
        public const float ShoveSpeed = 1.0f;
        /// <summary>Slower than this (m/s) a resting hand does nothing at all.</summary>
        public const float MinPressSpeed = 0.08f;
        /// <summary>Hand speeds above this (m/s) count as this.</summary>
        public const float MaxHandSpeed = 4f;
        /// <summary>Bounce of suit on suit: a shove gives each of the two equal masses (1 + e) / 2 of the closing speed.</summary>
        public const float Restitution = 0.3f;
        /// <summary>A press gives each half the closing speed (no bounce).</summary>
        public const float PressShare = 0.5f;
        /// <summary>Least time between two shoves of the same crew mate (one swing is one shove).</summary>
        public const float CooldownSeconds = 0.35f;
        /// <summary>The other hand may join a shove this long after the first touched (a two-handed shove).</summary>
        public const float GatherSeconds = 0.05f;
        /// <summary>Radius of a glove for the contact test (metres).</summary>
        public const float HandRadius = 0.07f;

        public NetSession session;

        NetSessionHost m_Host;
        NetSessionClient m_Client;
        CrewPushHandler m_OnPush;
        System.Func<byte, byte, bool> m_Allowed;

        // the local hands: 0 left, 1 right
        readonly Vector3[] m_HandLast = new Vector3[2];
        readonly Vector3[] m_HandVelocity = new Vector3[2];
        readonly bool[] m_HandHasLast = new bool[2];
        double m_HandTime = double.NaN;

        // the crew, by slot
        readonly Vector3[] m_CrewLast = new Vector3[NetProtocol.MaxPlayers];
        readonly Vector3[] m_CrewVelocity = new Vector3[NetProtocol.MaxPlayers];
        readonly bool[] m_CrewHasLast = new bool[NetProtocol.MaxPlayers];
        readonly double[] m_Cooldown = new double[NetProtocol.MaxPlayers];
        readonly Pending[] m_Pending = new Pending[NetProtocol.MaxPlayers];

        struct Pending
        {
            public bool open;
            public double since;
            public Vector3 velocity;
            public Vector3 contact;
            public bool shove;
            public bool left, right;
        }

        /// <summary>Shoves this machine sent (tests).</summary>
        public int Sent { get; private set; }
        /// <summary>Velocity change asked for the target by the last shove sent (m/s).</summary>
        public Vector3 LastSentVelocity { get; private set; }
        /// <summary>Slot shoved last.</summary>
        public int LastTarget { get; private set; } = -1;
        /// <summary>Velocity change this player got back from its last shove (m/s).</summary>
        public Vector3 LastRecoil { get; private set; }
        /// <summary>Contacts that only thudded because CREW SHOVES is off here.</summary>
        public int ThudOnly { get; private set; }
        /// <summary>Contacts dropped because a wall was between the pusher's head and the hand.</summary>
        public int BlockedByWall { get; private set; }
        /// <summary>Shoves that moved this player.</summary>
        public int Received { get; private set; }
        /// <summary>Velocity change applied by the last shove that moved this player (after the gravity rule; m/s).</summary>
        public Vector3 LastReceived { get; private set; }
        /// <summary>Shoves for this player that were ignored (CREW SHOVES off, downed, no rig).</summary>
        public int Ignored { get; private set; }
        /// <summary>Thuds played here (pusher's contact thud and the pushed player's own thud).</summary>
        public int Thuds { get; private set; }

        /// <summary>The shove sync for this session, added on first use. Null without a session.</summary>
        public static CrewShove Attach(NetSession session)
        {
            if (session == null) return null;
            var shove = session.GetComponent<CrewShove>();
            if (shove == null) shove = session.gameObject.AddComponent<CrewShove>();
            shove.session = session;
            shove.Bind();
            return shove;
        }

        /// <summary>CREW SHOVES in this headset's profile (on when there is no profile).</summary>
        public static bool EnabledHere(MissionDirector director)
        {
            return director == null || director.Profile == null || director.Profile.Data == null || director.Profile.Data.crewShoves;
        }

        void Bind()
        {
            if (m_OnPush == null) m_OnPush = OnCrewPush;
            if (m_Allowed == null) m_Allowed = Allowed;
            var host = session != null ? session.HostSession : null;
            var client = session != null ? session.ClientSession : null;
            if (host != m_Host)
            {
                Unbind(m_Host, null);
                m_Host = host;
                if (m_Host != null)
                {
                    m_Host.CrewPushReceived += m_OnPush;
                    m_Host.CrewPushAllowed = m_Allowed;
                }
            }
            if (client != m_Client)
            {
                Unbind(null, m_Client);
                m_Client = client;
                if (m_Client != null) m_Client.CrewPushReceived += m_OnPush;
            }
        }

        void Unbind(NetSessionHost host, NetSessionClient client)
        {
            if (host != null)
            {
                host.CrewPushReceived -= m_OnPush;
                if (host.CrewPushAllowed == m_Allowed) host.CrewPushAllowed = null;
            }
            if (client != null) client.CrewPushReceived -= m_OnPush;
        }

        void OnDestroy()
        {
            Unbind(m_Host, m_Client);
            m_Host = null;
            m_Client = null;
        }

        void Update()
        {
            Bind();
            if (m_Host == null && m_Client == null)
            {
                Destroy(this); // the session closed: nothing of co-op stays behind
                return;
            }
            Step(NetSession.Now);
        }

        // ================================================================== the host's extra check

        /// <summary>Neither player is downed (the host's session asks this for every shove it passes on).</summary>
        bool Allowed(byte sender, byte target) => !CrewDowned.Is(ContextOf(sender)) && !CrewDowned.Is(ContextOf(target));

        PlayerContext ContextOf(int slot)
        {
            if (session == null) return null;
            if (slot == session.LocalSlot) return session.director != null ? session.director.LocalPlayer : null;
            var crew = session.Crew(slot);
            return crew != null ? crew.context : null;
        }

        // ================================================================== pushing

        static Vector3 HandPoint(HandRig hand) => hand.interactor != null ? hand.interactor.transform.position : hand.transform.position;

        /// <summary>This hand can shove: empty (open or a fist), not holding a tool, an item or a rail.</summary>
        static bool HandFree(PlayerContext player, HandRig hand)
        {
            if (hand == null || !hand.isActiveAndEnabled) return false;
            if (hand.IsHolding || hand.IsAnchored) return false;
            var zeroG = player.locomotion != null ? player.locomotion.zeroG : null;
            if (zeroG != null && zeroG.IsHandAnchored(hand)) return false;
            if (player.tools != null)
                foreach (var tool in player.tools)
                    if (tool != null && tool.IsHeld && tool.HoldingHand == hand) return false;
            return true;
        }

        void Step(double now)
        {
            var director = session != null ? session.director : null;
            var local = director != null ? director.LocalPlayer : null;
            if (local == null) return;

            float dt = double.IsNaN(m_HandTime) ? 0f : (float)(now - m_HandTime);
            if (dt <= 0f && !double.IsNaN(m_HandTime)) return; // the clock has not moved: nothing new to measure
            m_HandTime = now;
            TrackHand(0, local.leftHand, dt);
            TrackHand(1, local.rightHand, dt);
            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++) TrackCrew(slot, dt);
            if (dt <= 0f) return;

            if (local.IsAway || CrewDowned.Is(local)) return;

            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++)
            {
                if (slot == session.LocalSlot) continue;
                var crew = session.Crew(slot);
                if (crew == null || !crew.HasPose || crew.context == null || crew.context.IsAway || CrewDowned.Is(crew.context))
                {
                    m_Pending[slot] = default;
                    continue;
                }
                if (now >= m_Cooldown[slot])
                {
                    Touch(slot, crew, 0, local, local.leftHand, now);
                    Touch(slot, crew, 1, local, local.rightHand, now);
                }
                ref Pending p = ref m_Pending[slot];
                if (p.open && (now - p.since >= GatherSeconds - 1e-6 || (p.left && p.right))) Fire(slot, local, director, now);
            }
        }

        void TrackHand(int index, HandRig hand, float dt)
        {
            if (hand == null) { m_HandHasLast[index] = false; return; }
            Vector3 position = HandPoint(hand);
            if (m_HandHasLast[index] && dt > 0f)
            {
                Vector3 step = position - m_HandLast[index];
                // A teleport is not a shove.
                m_HandVelocity[index] = step.magnitude > 1.5f ? Vector3.zero : Vector3.Lerp(m_HandVelocity[index], step / dt, 0.7f);
            }
            m_HandLast[index] = position;
            m_HandHasLast[index] = true;
        }

        void TrackCrew(int slot, float dt)
        {
            var crew = session.Crew(slot);
            if (crew == null || crew.head == null || !crew.HasPose)
            {
                m_CrewHasLast[slot] = false;
                m_CrewVelocity[slot] = Vector3.zero;
                return;
            }
            Vector3 position = crew.head.position;
            if (m_CrewHasLast[slot] && dt > 0f)
            {
                Vector3 step = position - m_CrewLast[slot];
                m_CrewVelocity[slot] = step.magnitude > 1f ? Vector3.zero : Vector3.Lerp(m_CrewVelocity[slot], step / dt, 0.3f);
            }
            m_CrewLast[slot] = position;
            m_CrewHasLast[slot] = true;
        }

        void Touch(int slot, RemoteAvatar crew, int index, PlayerContext local, HandRig hand, double now)
        {
            ref Pending p = ref m_Pending[slot];
            if ((index == 0 ? p.left : p.right) || !m_HandHasLast[index] || !HandFree(local, hand)) return;
            Vector3 palm = m_HandLast[index];
            if (!CrewShape.Touches(crew, palm, HandRadius, out Vector3 inward, out Vector3 contact)) return;

            Vector3 relative = m_HandVelocity[index] - m_CrewVelocity[slot];
            float speed = relative.magnitude;
            if (speed < MinPressSpeed) return;
            // Moving into the suit, not sliding past it or pulling out of it.
            if (Vector3.Dot(relative, inward) < 0.3f * speed) return;

            bool shove = speed >= ShoveSpeed;
            float share = shove ? (1f + Restitution) * 0.5f : PressShare;
            Vector3 velocity = relative / speed * (share * Mathf.Min(speed, MaxHandSpeed));

            if (!p.open)
            {
                p.open = true;
                p.since = now;
                p.contact = contact;
            }
            p.velocity += velocity;
            p.shove |= shove;
            if (index == 0) p.left = true; else p.right = true;
        }

        void Fire(int slot, PlayerContext local, MissionDirector director, double now)
        {
            Pending p = m_Pending[slot];
            m_Pending[slot] = default;
            m_Cooldown[slot] = now + CooldownSeconds;
            float raw = p.velocity.magnitude;
            if (raw < 1e-4f) return;
            Vector3 direction = p.velocity / raw;

            // No shoving through walls (or a stand-in that lags on the far side of one).
            Vector3 eye = local.head != null ? local.head.position : local.transform.position;
            if (Physics.Linecast(eye, p.contact, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                BlockedByWall++;
                return;
            }

            float strength = Mathf.Clamp01(raw / CrewPushMsg.MaxTargetSpeed);
            Thud(p.contact, strength * (p.shove ? 1f : 0.5f), 0.7f);
            Pulse(local, p, 0.15f + 0.35f * strength);

            if (!EnabledHere(director))
            {
                ThudOnly++; // a recoil-free thud, so the hand does not feel broken
                return;
            }

            Vector3 target = Vector3.ClampMagnitude(p.velocity, CrewPushMsg.MaxTargetSpeed);
            byte flags = p.shove ? CrewPushMsg.FlagShove : (byte)0;
            bool sent = m_Host != null
                ? m_Host.SendCrewPush((byte)slot, target.ToNet(), p.contact.ToNet(), flags)
                : m_Client != null && m_Client.SendCrewPush((byte)slot, target.ToNet(), p.contact.ToNet(), flags);
            if (!sent) return;
            Sent++;
            LastTarget = slot;
            LastSentVelocity = target;

            // Newton's third law: the same impulse back on the pusher (and whatever it carries), capped.
            var zeroG = local.locomotion != null ? local.locomotion.zeroG : null;
            float mass = zeroG != null ? zeroG.TotalMass : RealPhysics.PlayerBodyMass;
            float targetMass = zeroG != null ? zeroG.BodyMass : RealPhysics.PlayerBodyMass;
            float back = Mathf.Min(raw * targetMass / Mathf.Max(1f, mass), CrewPushMsg.MaxPusherSpeed);
            Vector3 recoil = AlongGround(-direction * back, eye);
            LastRecoil = recoil;
            if (zeroG != null && !zeroG.IsAnchored) zeroG.AddImpulse(recoil);
        }

        void Pulse(PlayerContext local, in Pending p, float amplitude)
        {
            if (p.left && local.leftHand != null) local.leftHand.Pulse(amplitude, 0.05f);
            if (p.right && local.rightHand != null) local.rightHand.Pulse(amplitude, 0.05f);
        }

        void Thud(Vector3 at, float strength, float pitch)
        {
            Thuds++;
            var library = Sfx.LibraryOrNull;
            if (library == null) return;
            Sfx.PlayVaried(library.RandomImpactLight(), at, Mathf.Lerp(0.25f, 0.6f, Mathf.Clamp01(strength)), pitch, 10f, 0.06f, 0.1f);
        }

        /// <summary>Under gravity a shove only slides you along the ground (as a thrust does); in zero-g it is unchanged.</summary>
        static Vector3 AlongGround(Vector3 velocity, Vector3 at)
        {
            Vector3 gravity = MapGravity.GravityAt(at);
            if (gravity.sqrMagnitude < 1e-6f) return velocity;
            return Vector3.ProjectOnPlane(velocity, gravity.normalized);
        }

        // ================================================================== being pushed

        /// <summary>A crew mate shoved this player (heard by the session). Moves the local rig unless CREW SHOVES is off here.</summary>
        void OnCrewPush(byte sender, NetVec3 velocity, NetVec3 contact, byte flags)
        {
            var director = session != null ? session.director : null;
            var local = director != null ? director.LocalPlayer : null;
            var zeroG = local != null && local.locomotion != null ? local.locomotion.zeroG : null;
            if (zeroG == null || !EnabledHere(director) || CrewDowned.Is(local))
            {
                Ignored++;
                return;
            }
            Vector3 eye = local.head != null ? local.head.position : local.transform.position;
            Vector3 v = AlongGround(Vector3.ClampMagnitude(velocity.ToUnity(), CrewPushMsg.MaxTargetSpeed), eye);
            zeroG.AddImpulse(v);
            Received++;
            LastReceived = v;
            float strength = Mathf.Clamp01(velocity.Magnitude / CrewPushMsg.MaxTargetSpeed);
            local.PulseBoth(0.25f + 0.4f * strength, 0.08f);
            Thud(eye + Vector3.down * 0.35f, 0.4f + 0.4f * strength, 0.5f);
        }
    }

    /// <summary>
    /// A crew mate's suit as a few round shapes for the shove test: helmet, torso, backpack and both upper arms, worked
    /// out from the stand-in's posed suit (or from its head alone when the suit is missing). Plain maths, no colliders.
    /// </summary>
    public static class CrewShape
    {
        public const float HelmetRadius = 0.17f;
        public const float TorsoRadius = 0.2f;
        public const float PackRadius = 0.19f;
        public const float ArmRadius = 0.07f;

        /// <summary>
        /// True when a sphere at <paramref name="point"/> (radius <paramref name="radius"/>) overlaps the suit.
        /// <paramref name="inward"/> points from the point into the nearest part, <paramref name="contact"/> is on its surface.
        /// </summary>
        public static bool Touches(RemoteAvatar crew, Vector3 point, float radius, out Vector3 inward, out Vector3 contact)
        {
            inward = Vector3.zero;
            contact = point;
            if (crew == null || crew.head == null) return false;
            float best = float.MaxValue;
            Vector3 bestAxis = point;
            float bestRadius = 0f;

            void Try(Vector3 a, Vector3 b, float r)
            {
                Vector3 axis = Closest(a, b, point);
                float gap = Vector3.Distance(axis, point) - r;
                if (gap < best) { best = gap; bestAxis = axis; bestRadius = r; }
            }

            var suit = crew.suit;
            if (suit != null && suit.isActiveAndEnabled && suit.torso != null)
            {
                Vector3 helmet = suit.helmet != null ? suit.helmet.position : crew.head.position;
                Try(helmet, helmet, HelmetRadius);
                Vector3 neck = suit.torso.position;
                Quaternion body = suit.torso.rotation;
                Try(neck + body * new Vector3(0f, -0.1f, 0f), neck + body * new Vector3(0f, -0.48f, 0f), TorsoRadius);
                Try(neck + body * new Vector3(0f, -0.22f, -0.2f), neck + body * new Vector3(0f, -0.38f, -0.2f), PackRadius);
                for (int side = 0; side < 2; side++)
                {
                    Vector3 shoulder = suit.GetShoulder(side == 0);
                    Vector3 elbow = suit.GetElbow(side == 0);
                    if ((elbow - shoulder).sqrMagnitude > 1e-4f) Try(shoulder, elbow, ArmRadius);
                }
            }
            else
            {
                Vector3 head = crew.head.position;
                Try(head, head, HelmetRadius);
                Try(head + Vector3.down * 0.3f, head + Vector3.down * 0.75f, TorsoRadius + 0.02f);
            }

            if (best > radius) return false;
            Vector3 toward = bestAxis - point;
            inward = toward.sqrMagnitude > 1e-8f ? toward.normalized : (crew.head.position - point).normalized;
            contact = bestAxis - inward * bestRadius;
            return true;
        }

        static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float length = ab.sqrMagnitude;
            if (length < 1e-8f) return a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / length);
        }
    }
}
