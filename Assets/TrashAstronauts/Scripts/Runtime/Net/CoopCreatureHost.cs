using System;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op stage 4 on the host: the one creature, its noise and darts, and the blackouts. The creature runs
    /// exactly as offline (EnemyDrone.Step against every player who is not Away); this class only hears what it
    /// does and tells the crew, and turns the crew's reports into the same calls a local player's tools make.
    ///
    /// Out, through <see cref="ICoopBroadcast"/>: CreatureStateChanged, Tranquilized, CatchStarted,
    /// BlackoutStarted, BlackoutEnded, and the CreatureState stream through <see cref="TrySampleCreature"/>.
    /// In: <see cref="HostNoise"/> (IHostWorld.OnNoise) and <see cref="HostTranqHitReport"/> (IHostWorld.OnTranqHitReport).
    /// Owned by <see cref="CoopHostRules"/>; plain C#.
    /// </summary>
    public sealed class CoopCreatureHost : IDisposable
    {
        /// <summary>CreatureState interval while it roams, sleeps or searches (10 Hz).</summary>
        public const double CalmInterval = 0.1;
        /// <summary>CreatureState interval while it investigates or chases (20 Hz).</summary>
        public const double AlertInterval = 0.05;
        /// <summary>A reported dart may have flown this much further than the tool's range (the shooter's view is a little old).</summary>
        public const float TranqRangeSlack = 2f;
        /// <summary>A reported hit point must be this close to the creature's body or its route point.</summary>
        public const float TranqHitTolerance = 2.5f;
        /// <summary>Range of the dart when no tranquilizer is found on the host's own rig (TranqTool.range).</summary>
        public const float DefaultTranqRange = 16f;

        readonly MissionDirector m_Director;
        readonly ICoopBroadcast m_Out;
        readonly EnemyDrone[] m_Enemies;
        readonly BlackoutController m_Blackout;
        // Loudest a crew member's noise may be, by NoiseKind (Impact, Cutting, PanelBreak, Door, Voice, Thruster, Web).
        // Panel, door and web noises are made by the host itself when it applies the change, so they are not taken.
        readonly float[] m_NoiseMax = { 1f, 1f, 0f, 0f, 1f, 1f, 0f };
        readonly int[] m_NoiseInWindow = new int[NetProtocol.MaxPlayers];
        readonly float[] m_NoiseWindowStart = new float[NetProtocol.MaxPlayers];
        double m_NextSample = double.NegativeInfinity;
        bool m_Disposed;

        public CoopCreatureHost(MissionDirector director, ICoopBroadcast broadcast)
        {
            m_Director = director ?? throw new ArgumentNullException(nameof(director));
            m_Out = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            m_Enemies = director.enemies ?? new EnemyDrone[0];
            foreach (var enemy in m_Enemies)
            {
                if (enemy == null) continue;
                enemy.StateChangedForCrew += OnStateChanged;
                enemy.CatchBegan += OnCatchBegan;
                enemy.Darted += OnDarted;
            }
            m_Blackout = director.blackout;
            if (m_Blackout != null) m_Blackout.Changed += OnBlackoutChanged;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            foreach (var enemy in m_Enemies)
            {
                if (enemy == null) continue;
                enemy.StateChangedForCrew -= OnStateChanged;
                enemy.CatchBegan -= OnCatchBegan;
                enemy.Darted -= OnDarted;
            }
            if (m_Blackout != null) m_Blackout.Changed -= OnBlackoutChanged;
        }

        /// <summary>The creature hunting this contract, or null when none is aboard (or no contract runs).</summary>
        public EnemyDrone Creature
        {
            get
            {
                if (m_Director.Phase != MissionPhase.Active || !m_Director.CreatureAboard) return null;
                var creature = m_Director.ActiveEnemy;
                return creature != null && !creature.IsReplica ? creature : null;
            }
        }

        // counters, for tests and the debug line
        public int NoiseAccepted { get; private set; }
        public int NoiseRefused { get; private set; }
        public int TranqAccepted { get; private set; }
        public int TranqRefused { get; private set; }

        /// <summary>Loudest noise of a kind taken from a crew member; louder is clamped, 0 drops the kind.</summary>
        public void SetNoiseMax(NoiseKind kind, float maxLoudness)
        {
            int k = (int)kind;
            if (k >= 0 && k < m_NoiseMax.Length) m_NoiseMax[k] = maxLoudness > 0f ? maxLoudness : 0f;
        }

        // ------------------------------------------------------------------ stream

        /// <summary>
        /// The CreatureState sample to send now, if one is due: 10 a second, 20 while it investigates or chases.
        /// False when no creature is aboard or the last one went out too recently. Call every send tick with the
        /// host's clock (any monotonic seconds), then pass the sample to NetSessionHost.SetCreature.
        /// </summary>
        public bool TrySampleCreature(double now, out CreatureSnapshot snapshot)
        {
            snapshot = default(CreatureSnapshot);
            var creature = Creature;
            if (creature == null || !creature.IsRunning) return false;
            if (now < m_NextSample) return false;
            var state = creature.State;
            double interval = state == EnemyState.Investigate || state == EnemyState.Chase ? AlertInterval : CalmInterval;
            // A few ms of tolerance, so a 20 Hz send tick that lands a hair early still makes the cadence.
            m_NextSample = now + interval - 0.01;
            snapshot = creature.SampleSnapshot();
            return true;
        }

        /// <summary>The creature as it is now, whenever asked (no rate). False when none is aboard.</summary>
        public bool SampleCreature(out CreatureSnapshot snapshot)
        {
            var creature = Creature;
            snapshot = creature != null ? creature.SampleSnapshot() : default(CreatureSnapshot);
            return creature != null;
        }

        // ------------------------------------------------------------------ crew reports

        /// <summary>
        /// A noise a crew member made (IHostWorld.OnNoise). It is credited to <paramref name="bySlot"/> whatever the
        /// client claimed, clamped to the loudest its kind may be (panel, door and web noises are dropped: the
        /// host makes those itself), at most 8 a second per crew member, and fed to the noise system exactly as a
        /// noise of the host's own player would be. True if it was emitted.
        /// </summary>
        public bool HostNoise(NetVec3 position, float loudness, NoiseKind kind, byte bySlot)
        {
            int k = (int)kind;
            var noise = m_Director.noise;
            if (noise == null || noise.Muted || k < 0 || k >= m_NoiseMax.Length || bySlot >= NetProtocol.MaxPlayers
                || m_Director.Phase != MissionPhase.Active || !(loudness > 0f) || !(m_NoiseMax[k] > 0f))
            {
                NoiseRefused++;
                return false;
            }
            float now = Time.unscaledTime;
            if (now - m_NoiseWindowStart[bySlot] >= 1f)
            {
                m_NoiseWindowStart[bySlot] = now;
                m_NoiseInWindow[bySlot] = 0;
            }
            if (m_NoiseInWindow[bySlot] >= NetProtocol.MaxNoisePerSecond)
            {
                NoiseRefused++;
                return false;
            }
            m_NoiseInWindow[bySlot]++;
            int before = noise.EmittedCount;
            noise.Emit(new NoiseEvent(CoopConvert.ToUnity(position), Mathf.Min(loudness, m_NoiseMax[k]), kind, new PlayerId(bySlot)));
            bool emitted = noise.EmittedCount != before;
            if (emitted) NoiseAccepted++;
            else NoiseRefused++;
            return emitted;
        }

        /// <summary>
        /// A crew member's dart hit the creature on their screen (IHostWorld.OnTranqHitReport). The shooter's own
        /// sphere-cast is trusted, within reason: a creature must be aboard and awake to it (running, not in a
        /// jumpscare), the hit no further from the muzzle than the tool's range plus <see cref="TranqRangeSlack"/>,
        /// and within <see cref="TranqHitTolerance"/> of where the creature is here. Then it is darted exactly as by
        /// the host's own tool, and Tranquilized goes out. True if it was darted.
        /// </summary>
        public bool HostTranqHitReport(NetVec3 origin, NetVec3 hitPoint, byte bySlot)
        {
            var creature = Creature;
            Vector3 from = CoopConvert.ToUnity(origin), at = CoopConvert.ToUnity(hitPoint);
            bool ok = creature != null && creature.IsRunning && !creature.IsScaring && bySlot < NetProtocol.MaxPlayers
                && Vector3.Distance(from, at) <= TranqRange() + TranqRangeSlack
                && DistanceToCreature(creature, at) <= TranqHitTolerance;
            if (ok && creature.Tranquilize(m_Director.Config.tranqSleepSeconds))
            {
                TranqAccepted++;
                if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.tranqHit, at, 0.5f, 1f, 14f);
                return true;
            }
            TranqRefused++;
            return false;
        }

        static float DistanceToCreature(EnemyDrone creature, Vector3 point)
        {
            float d = Vector3.Distance(point, creature.transform.position);
            var walker = creature.Walker;
            if (walker != null && walker.surface != null) d = Mathf.Min(d, Vector3.Distance(point, walker.surface.position));
            return d;
        }

        /// <summary>The dart's range: the host's own tranquilizer's (every rig carries the same tool).</summary>
        public float TranqRange()
        {
            var local = m_Director.LocalPlayer;
            if (local != null && local.tools != null)
            {
                foreach (var tool in local.tools)
                {
                    if (tool is Tools.TranqTool tranq) return tranq.range;
                }
            }
            return DefaultTranqRange;
        }

        // ------------------------------------------------------------------ the creature's own changes

        bool IsTheCreature(EnemyDrone drone) => drone != null && drone == m_Director.ActiveEnemy && m_Director.CreatureAboard
            && m_Director.Phase == MissionPhase.Active && !drone.IsReplica;

        static byte Slot(PlayerId id) => id.Value >= 0 && id.Value < NetProtocol.MaxPlayers ? (byte)id.Value : NetProtocol.NoSlot;

        void OnStateChanged(EnemyDrone drone, EnemyState state, PlayerId quarry)
        {
            if (IsTheCreature(drone)) m_Out.CreatureStateChanged(state, Slot(quarry));
        }

        void OnCatchBegan(EnemyDrone drone, PlayerId victim)
        {
            if (IsTheCreature(drone) && Slot(victim) != NetProtocol.NoSlot) m_Out.CatchStarted(Slot(victim));
        }

        void OnDarted(EnemyDrone drone, float seconds)
        {
            if (IsTheCreature(drone)) m_Out.Tranquilized(seconds);
        }

        /// <summary>
        /// The host's blackouts go to the crew. The darkness of a jumpscare does not: every machine plays its own
        /// jumpscare, lights and all.
        /// </summary>
        void OnBlackoutChanged(bool on)
        {
            if (m_Director.Phase != MissionPhase.Active || AnyScaring()) return;
            if (on) m_Out.BlackoutStarted(m_Blackout.LastDuration);
            else m_Out.BlackoutEnded(m_Blackout.LastEndInstant);
        }

        bool AnyScaring()
        {
            foreach (var enemy in m_Enemies)
            {
                if (enemy != null && enemy.IsScaring) return true;
            }
            return false;
        }
    }
}
