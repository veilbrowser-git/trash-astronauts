using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Turns hard physics impacts into sound and, above a threshold, into gameplay noise the drone can
    /// hear. Thresholds and a per-object cooldown keep resting contact and gentle drifting silent, so
    /// throwing something down a corridor is a deliberate distraction rather than constant accidental noise.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class ImpactNoiseEmitter : MonoBehaviour
    {
        [Tooltip("Scales gameplay loudness. Heavy objects are louder.")]
        public float loudnessScale = 1f;
        public bool heavy;
        [Tooltip("What the surface is made of: rubber thuds, bare metal clangs.")]
        public TrashAstronauts.World.SurfaceKind surface = TrashAstronauts.World.SurfaceKind.PaintedMetal;
        public float audioSpeedThreshold = 0.7f;
        public float audioCooldown = 0.2f;

        float m_LastAudio = -10f;
        Rigidbody m_Body;

        /// <summary>The last knock's strength as a speed-equivalent (m/s) after impulse and material are counted.</summary>
        public float LastImpactStrength { get; private set; }
        float m_LastNoise = -10f;
        float m_SpawnTime;
        SalvageItem m_Item;
        bool m_LookedForItem;

        XRGrabInteractable m_Grab;

        /// <summary>
        /// Who a noise from this object is attributed to. Salvage sets it when it is picked up; nothing
        /// sets it for junk and cut plates, so offline their knocks belong to nobody (as they always have).
        /// </summary>
        public PlayerId LastHandler { get; set; } = PlayerId.None;

        /// <summary>
        /// Who last had this object in a hand on this machine, whatever it is: junk and cut plates too.
        /// <see cref="PlayerId.None"/> until somebody grabs it. Junk is not shared between machines, so in
        /// a session this is only ever this headset's player.
        /// </summary>
        public PlayerId LastGrabbedBy { get; private set; } = PlayerId.None;

        void OnEnable()
        {
            m_SpawnTime = Time.time;
            if (m_Grab == null) m_Grab = GetComponent<XRGrabInteractable>();
            if (m_Grab != null) m_Grab.selectEntered.AddListener(OnGrabbed);
        }

        void OnDisable()
        {
            if (m_Grab != null) m_Grab.selectEntered.RemoveListener(OnGrabbed);
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            var player = PlayerContext.From(args.interactorObject);
            var director = MissionDirector.Active;
            LastGrabbedBy = player != null ? player.Id : director != null ? director.LocalPlayerId : PlayerId.Local;
        }

        /// <summary>Momentum (kg m/s) of the last hit on a creature, along the contact normal.</summary>
        public float LastCreatureHitMomentum { get; private set; }
        /// <summary>How many times this body has hit a creature.</summary>
        public int CreatureHits { get; private set; }

        /// <summary>
        /// Something heavy thrown hard into the creature knocks it off balance. Momentum is the body's
        /// mass times its closing speed along the contact normal, so a glancing blow counts for less.
        /// Only the machine that simulates this body, and never a client (the host runs the creature).
        /// </summary>
        void TryStaggerCreature(Collision collision)
        {
            if (collision.collider == null || collision.collider.gameObject.layer != TrashAstronauts.World.GameLayers.Enemy) return;
            var drone = collision.collider.GetComponentInParent<EnemyDrone>();
            if (drone == null || !SimulatedHere || OnAClient(MissionDirector.Active)) return;
            if (m_Body == null) m_Body = GetComponent<Rigidbody>();
            Vector3 normal = collision.contactCount > 0 ? collision.GetContact(0).normal : -collision.relativeVelocity.normalized;
            float closing = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, normal));
            LastCreatureHitMomentum = m_Body.mass * closing;
            CreatureHits++;
            float seconds = EnemyDrone.StaggerSecondsFor(LastCreatureHitMomentum);
            if (seconds > 0f) drone.Stagger(seconds);
        }

        Material m_ShardMaterial;
        bool m_LookedForShardMaterial;

        /// <summary>A damaging knock on something delicate: a few shards fly off and it cracks. Cosmetic.</summary>
        void Crack(Vector3 point, Collision collision)
        {
            if (!m_LookedForShardMaterial)
            {
                m_LookedForShardMaterial = true;
                var renderer = GetComponentInChildren<MeshRenderer>();
                if (renderer != null) m_ShardMaterial = renderer.sharedMaterial;
            }
            Vector3 normal = collision.contactCount > 0 ? collision.GetContact(0).normal : Vector3.up;
            // The contact normal points from the other body toward this one: shards fly back off the hit.
            if (Vector3.Dot(normal, transform.position - point) < 0f) normal = -normal;
            TrashAstronauts.World.ShardBurst.Emit(point, normal, m_ShardMaterial);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.impactLight, point, 0.7f, Random.Range(1.7f, 1.9f), 12f);
        }

        static bool OnAClient(MissionDirector director) => director != null && director.Net != null && director.Net.IsClient;

        /// <summary>
        /// Whether this machine is the one that counts this body's knocks. Salvage says so itself. Junk
        /// and cut plates are not shared, so every machine has its own copy: the host counts all of its
        /// own, and a client only what its own player has had in hand (a can you threw still distracts
        /// the creature; one that just drifts about on your machine does not). Always true offline.
        /// </summary>
        public bool SimulatedHere
        {
            get
            {
                if (!m_LookedForItem) { m_Item = GetComponent<SalvageItem>(); m_LookedForItem = true; }
                if (m_Item != null) return m_Item.SimulatedHere;
                var director = MissionDirector.Active;
                if (!OnAClient(director)) return true;
                return LastGrabbedBy.IsValid && LastGrabbedBy == director.LocalPlayerId;
            }
        }

        /// <summary>
        /// Who a gameplay noise from this body is attributed to on this machine. A client's own thrown
        /// junk is its player's noise, so it is passed on to the host with the rest of what that player
        /// does. Everywhere else it is <see cref="LastHandler"/>, as it always was.
        /// </summary>
        public PlayerId NoiseSource
        {
            get
            {
                if (!m_LookedForItem) { m_Item = GetComponent<SalvageItem>(); m_LookedForItem = true; }
                return m_Item == null && OnAClient(MissionDirector.Active) ? LastGrabbedBy : LastHandler;
            }
        }

        void OnCollisionEnter(Collision collision)
        {
            TryStaggerCreature(collision);
            if (Time.time - m_SpawnTime < 1f) return;
            float speed = collision.relativeVelocity.magnitude;
            if (speed < audioSpeedThreshold) return;

            Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;

            // In co-op every machine sees this collision, but only the one simulating the body may turn it
            // into damage and into noise the creature hears (otherwise both would count twice). Offline
            // that is always this machine. The sound itself plays everywhere.
            if (!m_LookedForItem) { m_Item = GetComponent<SalvageItem>(); m_LookedForItem = true; }
            bool simulatedHere = SimulatedHere;

            // Delicate salvage is worth less every time it is banged into something.
            if (m_Item != null && simulatedHere && m_Item.ApplyImpact(speed) > 0) Crack(point, collision);

            // How hard it really was: the collision impulse (heavier things hit harder at the same speed,
            // glancing blows softer) and what it is made of (rubber thuds, bare metal clangs).
            if (m_Body == null) m_Body = GetComponent<Rigidbody>();
            float impulse = collision.impulse.magnitude;
            float strength = TrashAstronauts.World.RealPhysics.ImpactStrength(speed, impulse, m_Body.mass, surface);
            LastImpactStrength = strength;

            if (Time.time - m_LastAudio >= audioCooldown && Sfx.Library != null)
            {
                m_LastAudio = Time.time;
                float volume = Mathf.Clamp01(strength / 6f);
                bool deep = heavy || impulse > 40f;
                // Lighter, springier things sound a little higher.
                float pitch = Random.Range(0.9f, 1.1f) * (surface == TrashAstronauts.World.SurfaceKind.RubberPlastic ? 1.15f : deep ? 0.9f : 1f);
                Sfx.PlayAt(deep ? Sfx.Library.impactHeavy : Sfx.Library.impactLight, point, volume, pitch, 18f);
            }

            var director = MissionDirector.Active;
            if (director == null || director.noise == null) return;
            if (!simulatedHere) return;
            if (NoiseRules.TryEvaluateImpact(strength, Time.time - m_LastNoise, director.Config.noise, out float loudness))
            {
                m_LastNoise = Time.time;
                director.noise.Emit(new NoiseEvent(point, loudness * loudnessScale, NoiseKind.Impact, NoiseSource));
            }
        }
    }
}
