using UnityEngine;

namespace TrashAstronauts.Audio
{
    /// <summary>
    /// Every sound the game uses, in one asset. All clips are synthesized by the project builder
    /// (see Editor/Builder.Audio.cs) so there are no third-party audio licenses to track.
    /// </summary>
    [CreateAssetMenu(menuName = "Trash Astronauts/Audio Library", fileName = "AudioLibrary")]
    public sealed class AudioLibrary : ScriptableObject
    {
        [Header("Interaction")]
        public AudioClip grab;
        public AudioClip buttonClick;
        public AudioClip deposit;
        public AudioClip depositJackpot;
        public AudioClip teleport;
        public AudioClip toolHolster;
        public AudioClip squeak;
        public AudioClip trickShot;
        public AudioClip thruster;
        public AudioClip breathing;
        public AudioClip heartbeat;

        [Header("Tools")]
        public AudioClip flashlightClick;
        public AudioClip scannerPing;
        public AudioClip scannerHit;
        public AudioClip cutterLoop;
        public AudioClip cutterSparks;
        public AudioClip cutterOverheat;
        public AudioClip panelBreak;
        public AudioClip tranqFire;
        public AudioClip tranqHit;

        [Header("World")]
        public AudioClip impactLight;
        public AudioClip impactHeavy;
        [Tooltip("Alternative takes of impactLight (element 0 is the same clip as impactLight). Optional.")]
        public AudioClip[] impactLightVariants;
        [Tooltip("Alternative takes of impactHeavy (element 0 is the same clip as impactHeavy). Optional.")]
        public AudioClip[] impactHeavyVariants;
        public AudioClip doorMove;
        public AudioClip airlockHiss;
        public AudioClip shipAmbience;
        public AudioClip tugAmbience;
        public AudioClip powerDown;
        public AudioClip powerUp;

        [Header("Oxygen")]
        public AudioClip oxygenLow;
        public AudioClip oxygenCritical;

        [Header("Drone")]
        public AudioClip droneHum;
        public AudioClip droneAlert;
        public AudioClip droneChase;
        public AudioClip droneLost;
        public AudioClip droneCatch;
        public AudioClip droneScare;

        [Header("Results")]
        public AudioClip resultSuccess;
        public AudioClip resultPartial;
        public AudioClip resultFail;

        // Private generator so picking a variant never disturbs UnityEngine.Random (gameplay may be seeded).
        static uint s_Random = 0x2545F491u;
        [System.NonSerialized] int m_LastLight = -1;
        [System.NonSerialized] int m_LastHeavy = -1;

        /// <summary>A random light-impact take (never the same one twice in a row); falls back to <see cref="impactLight"/>.</summary>
        public AudioClip RandomImpactLight() => Pick(impactLightVariants, impactLight, ref m_LastLight);

        /// <summary>A random heavy-impact take (never the same one twice in a row); falls back to <see cref="impactHeavy"/>.</summary>
        public AudioClip RandomImpactHeavy() => Pick(impactHeavyVariants, impactHeavy, ref m_LastHeavy);

        /// <summary>
        /// If the clip has alternative takes, returns one of them at random; otherwise returns the clip
        /// unchanged. Lets callers keep passing the single field and still get variety.
        /// </summary>
        public AudioClip Variant(AudioClip clip)
        {
            if (clip == null) return null;
            if (clip == impactLight) return RandomImpactLight();
            if (clip == impactHeavy) return RandomImpactHeavy();
            return clip;
        }

        /// <summary>
        /// True for clips whose pitch carries meaning (alarms, jingles, scanner tones). These should be
        /// played exactly as requested, without random pitch or level variation.
        /// </summary>
        public bool IsTonal(AudioClip clip)
        {
            if (clip == null) return false;
            return clip == deposit || clip == depositJackpot || clip == trickShot
                || clip == scannerPing || clip == scannerHit || clip == cutterOverheat
                || clip == oxygenLow || clip == oxygenCritical || clip == teleport
                || clip == resultSuccess || clip == resultPartial || clip == resultFail;
        }

        static AudioClip Pick(AudioClip[] variants, AudioClip fallback, ref int last)
        {
            if (variants == null || variants.Length == 0) return fallback;
            int index = NextIndex(variants.Length);
            if (index == last && variants.Length > 1) index = (index + 1) % variants.Length;
            last = index;
            return variants[index] != null ? variants[index] : fallback;
        }

        static int NextIndex(int count)
        {
            // xorshift32
            s_Random ^= s_Random << 13;
            s_Random ^= s_Random >> 17;
            s_Random ^= s_Random << 5;
            return (int)(s_Random % (uint)count);
        }
    }
}
