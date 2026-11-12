using System.Collections;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Tools
{
    /// <summary>
    /// Tranquilizer launcher. Each trigger pull spends one dart bought in the tug's shop. A dart that
    /// hits the creature puts it to sleep for a few seconds: long enough to slip past it, not long
    /// enough to stop being afraid of it. It is nearly silent, so it does not draw attention.
    /// </summary>
    public sealed class TranqTool : SuitTool
    {
        public Transform muzzle;
        public TMP_Text readout;
        public LineRenderer tracer;
        public float range = 16f;
        [Tooltip("Aim assist: the dart is a small sphere rather than a perfect line.")]
        public float dartRadius = 0.1f;
        public float tracerSeconds = 0.09f;

        float m_NextRefresh;
        Coroutine m_TracerRoutine;

        public int ShotsFired { get; private set; }
        public int Hits { get; private set; }

        protected override void Start()
        {
            base.Start();
            if (tracer != null) tracer.enabled = false;
            RefreshReadout();
        }

        protected override void OnTriggerDown() => Fire();

        /// <summary>Fires one dart along the muzzle. Returns true if the creature was hit.</summary>
        public bool Fire()
        {
            var director = MissionDirector.Active;
            if (director == null || director.Profile == null) return false;

            Vector3 origin = muzzle != null ? muzzle.position : transform.position;
            Vector3 direction = muzzle != null ? muzzle.forward : transform.forward;

            if (!director.Profile.TryConsumeTranqDart())
            {
                if (HoldingHand != null) HoldingHand.Pulse(0.15f, 0.03f);
                if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.flashlightClick, origin, 0.5f, 0.7f, 4f);
                RefreshReadout();
                return false;
            }

            ShotsFired++;
            Vector3 end = origin + direction * range;
            bool hitCreature = false;
            int mask = GameLayers.EnvironmentMask | (1 << GameLayers.Enemy);
            if (Physics.SphereCast(origin, dartRadius, direction, out RaycastHit hit, range, mask, QueryTriggerInteraction.Ignore))
            {
                end = origin + direction * hit.distance;
                var drone = hit.collider.GetComponentInParent<EnemyDrone>();
                if (drone != null && drone.Tranquilize(director.Config.tranqSleepSeconds))
                {
                    hitCreature = true;
                    Hits++;
                    if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.tranqHit, end, 0.5f, 1f, 14f);
                }
            }

            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.tranqFire, origin, 0.5f, 1f, 8f);
            if (HoldingHand != null) HoldingHand.Pulse(hitCreature ? 0.7f : 0.4f, hitCreature ? 0.15f : 0.06f);
            if (director.noise != null)
                director.noise.Emit(new NoiseEvent(origin, 0.12f, NoiseKind.Impact, HoldingHand != null && HoldingHand.Player != null ? HoldingHand.Player.Id : director.LocalPlayerId));

            if (tracer != null && isActiveAndEnabled)
            {
                if (m_TracerRoutine != null) StopCoroutine(m_TracerRoutine);
                m_TracerRoutine = StartCoroutine(ShowTracer(origin, end));
            }
            RefreshReadout();
            return hitCreature;
        }

        IEnumerator ShowTracer(Vector3 from, Vector3 to)
        {
            tracer.positionCount = 2;
            tracer.SetPosition(0, from);
            tracer.SetPosition(1, to);
            tracer.enabled = true;
            yield return new WaitForSeconds(tracerSeconds);
            tracer.enabled = false;
            m_TracerRoutine = null;
        }

        protected override void Tick(float dt)
        {
            if (Time.time < m_NextRefresh) return;
            m_NextRefresh = Time.time + 0.4f;
            RefreshReadout();
        }

        void RefreshReadout()
        {
            if (readout == null) return;
            var director = MissionDirector.Active;
            int darts = director != null && director.Profile != null ? director.Profile.Data.tranqDarts : 0;
            string text = darts > 0 ? "TRANQ\nDARTS: " + darts : "TRANQ\nEMPTY - buy darts";
            if (readout.text != text) readout.text = text;
        }
    }
}
