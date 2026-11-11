using UnityEngine;

namespace TrashAstronauts.Audio
{
    /// <summary>
    /// Every sound the game uses, in one asset. All clips are synthesized by the project builder
    /// (see Editor/ProceduralAudio.cs) so there are no third-party audio licenses to track.
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

        [Header("Tools")]
        public AudioClip flashlightClick;
        public AudioClip scannerPing;
        public AudioClip scannerHit;
        public AudioClip cutterLoop;
        public AudioClip cutterSparks;
        public AudioClip cutterOverheat;
        public AudioClip panelBreak;

        [Header("World")]
        public AudioClip impactLight;
        public AudioClip impactHeavy;
        public AudioClip doorMove;
        public AudioClip airlockHiss;
        public AudioClip shipAmbience;
        public AudioClip tugAmbience;

        [Header("Oxygen")]
        public AudioClip oxygenLow;
        public AudioClip oxygenCritical;

        [Header("Drone")]
        public AudioClip droneHum;
        public AudioClip droneAlert;
        public AudioClip droneChase;
        public AudioClip droneLost;
        public AudioClip droneCatch;

        [Header("Results")]
        public AudioClip resultSuccess;
        public AudioClip resultPartial;
        public AudioClip resultFail;
    }
}
