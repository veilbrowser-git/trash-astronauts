using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op revive: the handle on a downed crew mate's chest pack, shown only while they are down. A glowing red-orange
    /// grip with a soft additive halo that is visible through the dark, a small warm point light so the crew can find the
    /// body from across a room, and a ring of twelve teal lights around the grip that fill as the revive goes on. The halo
    /// and the light breathe slowly (0.6 Hz, never off: no flashing). Part of the RemoteCrew prefab; RemoteAvatar places
    /// it on the suit's chest every frame and feeds it the host's progress.
    /// </summary>
    public sealed class ReviveBeacon : MonoBehaviour
    {
        public GameObject visuals;
        public Transform halo;
        public Light lamp;
        [Tooltip("Ring segments in order round the grip; segment k is lit once progress reaches (k + 1) / count.")]
        public Renderer[] ring;
        public float lampIntensity = 1.3f;
        public float pulseHz = 0.6f;

        public bool Shown { get; private set; }
        public float Progress { get; private set; }
        public int LitSegments { get; private set; }

        void Awake()
        {
            if (visuals != null && !Shown) visuals.SetActive(false);
        }

        public void Show(bool on)
        {
            Shown = on;
            if (visuals != null && visuals.activeSelf != on) visuals.SetActive(on);
            if (lamp != null) lamp.enabled = on;
            if (!on) SetProgress(0f);
        }

        public void SetProgress(float progress)
        {
            Progress = Mathf.Clamp01(progress);
            int lit = ring == null ? 0 : Mathf.Clamp(Mathf.FloorToInt(Progress * ring.Length + 0.001f), 0, ring.Length);
            LitSegments = lit;
            if (ring == null) return;
            for (int i = 0; i < ring.Length; i++)
            {
                if (ring[i] != null) ring[i].enabled = i < lit;
            }
        }

        void Update()
        {
            if (!Shown) return;
            // A slow breath, 80..110 %: easy to spot, nothing that flashes.
            float s = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * pulseHz * Mathf.PI * 2f);
            if (halo != null) halo.localScale = Vector3.one * (1f + 0.2f * s);
            if (lamp != null) lamp.intensity = lampIntensity * (0.8f + 0.3f * s);
        }
    }
}
