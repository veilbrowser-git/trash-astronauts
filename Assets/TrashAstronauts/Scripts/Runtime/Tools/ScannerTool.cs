using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Tools
{
    /// <summary>
    /// Salvage scanner. Point it at an item to identify it and read its value on the tool's screen.
    /// Pull the trigger to ping: visible loot within range is highlighted and tagged for a few seconds.
    /// Loot sealed behind an uncut panel has no line of sight and does not show up.
    /// </summary>
    public sealed class ScannerTool : SuitTool
    {
        public TMP_Text screen;
        public Transform emitter;
        public float identifyRange = 3.5f;
        public float identifyRadius = 0.12f;

        float m_NextPing;
        float m_NextIdentify;
        float m_MessageUntil;
        SalvageItem m_Identified;

        public int LastPingCount { get; private set; }

        protected override void OnDrawn() => SetScreen("SCANNER\nAim: identify\nTrigger: ping");
        protected override void OnDocked() => SetScreen("SCANNER\nSTANDBY");

        protected override void OnTriggerDown() => Ping();

        /// <summary>Highlights visible salvage in range. Returns how many items were found.</summary>
        public int Ping()
        {
            var director = MissionDirector.Active;
            if (director == null) return 0;
            if (Time.time < m_NextPing)
            {
                SetScreen("RECHARGING...", 0.6f);
                return 0;
            }

            var config = director.Config;
            m_NextPing = Time.time + config.scannerCooldownSeconds;
            Vector3 origin = emitter != null ? emitter.position : transform.position;
            float range = config.scannerRange * (director.Profile.Data.scannerUpgrade ? config.scannerUpgradeRangeMultiplier : 1f);

            int found = 0;
            int total = 0;
            var items = director.Items;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.IsSecured) continue;
                Vector3 target = item.transform.position;
                if (Vector3.Distance(origin, target) > range) continue;
                if (Physics.Linecast(origin, target, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                item.ShowScan(config.scannerHighlightSeconds);
                found++;
                total += item.Value;
            }

            LastPingCount = found;
            if (Sfx.Library != null)
            {
                Sfx.PlayAt(Sfx.Library.scannerPing, origin, 0.7f, 1f, 8f);
                if (found > 0) Sfx.PlayAt(Sfx.Library.scannerHit, origin, 0.6f, 1f + 0.05f * Mathf.Min(found, 6), 8f);
            }
            if (HoldingHand != null) HoldingHand.Pulse(found > 0 ? 0.5f : 0.2f, found > 0 ? 0.12f : 0.05f);

            SetScreen(found > 0
                ? "PING: " + found + (found == 1 ? " item" : " items") + "\n" + total + " cr in range"
                : "PING: nothing\nin range", 3f);
            return found;
        }

        protected override void Tick(float dt)
        {
            if (!IsHeld || Time.time < m_NextIdentify) return;
            m_NextIdentify = Time.time + 0.12f;

            Vector3 origin = emitter != null ? emitter.position : transform.position;
            Vector3 direction = emitter != null ? emitter.forward : transform.forward;
            SalvageItem item = null;
            int mask = GameLayers.SalvageMask | GameLayers.EnvironmentMask;
            if (Physics.SphereCast(origin, identifyRadius, direction, out RaycastHit hit, identifyRange, mask, QueryTriggerInteraction.Ignore))
                item = hit.collider.GetComponentInParent<SalvageItem>();

            if (item != m_Identified)
            {
                m_Identified = item;
                if (item != null && HoldingHand != null) HoldingHand.Pulse(0.15f, 0.03f);
            }

            if (Time.time < m_MessageUntil) return;
            if (item != null && item.definition != null)
            {
                var def = item.definition;
                SetScreen(def.displayName + "\n" + def.creditValue + " cr\n" + def.scannerBlurb);
            }
            else
            {
                SetScreen("SCANNER\nAim: identify\nTrigger: ping");
            }
        }

        void SetScreen(string text, float holdSeconds = 0f)
        {
            if (holdSeconds > 0f) m_MessageUntil = Time.time + holdSeconds;
            if (screen != null && screen.text != text) screen.text = text;
        }
    }
}
