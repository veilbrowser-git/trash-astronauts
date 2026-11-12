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
            if (m_Item != null && simulatedHere) m_Item.ApplyImpact(speed);

            if (Time.time - m_LastAudio >= audioCooldown && Sfx.Library != null)
            {
                m_LastAudio = Time.time;
                float volume = Mathf.Clamp01(speed / 6f);
                Sfx.PlayAt(heavy ? Sfx.Library.impactHeavy : Sfx.Library.impactLight, point, volume, Random.Range(0.9f, 1.1f), 18f);
            }

            var director = MissionDirector.Active;
            if (director == null || director.noise == null) return;
            if (!simulatedHere) return;
            if (NoiseRules.TryEvaluateImpact(speed, Time.time - m_LastNoise, director.Config.noise, out float loudness))
            {
                m_LastNoise = Time.time;
                director.noise.Emit(new NoiseEvent(point, loudness * loudnessScale, NoiseKind.Impact, NoiseSource));
            }
        }
    }
}
