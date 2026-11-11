using System;
using UnityEngine;

namespace TrashAstronauts.Core
{
    [Serializable]
    public sealed class EnemyTuning
    {
        [Header("Senses")]
        [Min(1f)] public float sightRange = 9f;
        [Range(10f, 180f)] public float sightHalfAngle = 60f;
        [Tooltip("Seconds of continuous line of sight before a patrolling drone commits to a chase.")]
        [Min(0.05f)] public float sightAcquireSeconds = 0.9f;
        [Tooltip("A noise of loudness 1 is heard from this far away. Quieter noises scale down linearly.")]
        [Min(1f)] public float hearingRadiusAtFullLoudness = 16f;

        [Header("Movement (m/s)")]
        public float patrolSpeed = 0.9f;
        public float investigateSpeed = 1.5f;
        public float chaseSpeed = 2.2f;
        public float searchSpeed = 1.1f;

        [Header("Timing (s)")]
        public float investigateLingerSeconds = 4f;
        public float chaseLoseSightSeconds = 3f;
        public float searchSeconds = 7f;

        [Header("Catching")]
        public float catchRadius = 0.95f;
        [Tooltip("Seconds after mission start before the drone leaves its dock.")]
        public float wakeDelaySeconds = 20f;
    }

    [Serializable]
    public sealed class NoiseTuning
    {
        [Tooltip("Impacts slower than this (m/s) are silent for gameplay purposes.")]
        public float impactSpeedThreshold = 2.2f;
        [Tooltip("Impact speed (m/s) that produces loudness 1.")]
        public float impactSpeedForFullLoudness = 7f;
        [Tooltip("Minimum seconds between gameplay noises from the same object.")]
        public float perObjectCooldown = 1.0f;
        public float cuttingLoudness = 0.85f;
        public float cutterIdleLoudness = 0.3f;
        [Tooltip("Seconds between noise pulses while the cutter is running.")]
        public float cuttingNoiseInterval = 0.8f;
        public float panelBreakLoudness = 1.0f;
        public float doorLoudness = 0.45f;
    }

    [Serializable]
    public sealed class CutterTuning
    {
        [Tooltip("Seconds of continuous firing from cold to overheat.")]
        public float secondsToOverheat = 7f;
        [Tooltip("Seconds to cool from fully hot to cold.")]
        public float secondsToCool = 5f;
        [Range(0.05f, 0.9f)] public float restartHeatFraction = 0.35f;
        [Tooltip("Seconds the flame must stay on one cut point to burn through it.")]
        public float secondsPerCutPoint = 0.55f;
    }

    /// <summary>
    /// All mission balancing in one editable asset: Assets/TrashAstronauts/Data/MissionConfig.asset.
    /// </summary>
    [CreateAssetMenu(menuName = "Trash Astronauts/Mission Config", fileName = "MissionConfig")]
    public sealed class MissionConfig : ScriptableObject
    {
        [Header("Contract")]
        [Min(0)] public int quotaCredits = 900;
        [Tooltip("Flat bonus paid on top of recovered salvage when the quota is met.")]
        [Min(0)] public int contractBonusCredits = 150;
        [Tooltip("Generated loot must total at least quota x this margin.")]
        [Min(1f)] public float quotaSafetyMargin = 1.3f;

        [Header("Oxygen")]
        [Tooltip("Seconds of oxygen without upgrades. ~10 minutes by design.")]
        [Min(5f)] public float baseOxygenSeconds = 600f;
        [Range(0f, 1f)] public float oxygenLowFraction = 0.30f;
        [Range(0f, 1f)] public float oxygenCriticalFraction = 0.12f;

        [Header("Progression")]
        [Min(0)] public int oxygenUpgradeCost = 600;
        [Min(0f)] public float oxygenUpgradeBonusSeconds = 180f;
        [Min(0)] public int maxOxygenUpgradeLevel = 1;

        [Header("Loot counts per run")]
        [Min(0)] public int smallScrapCount = 6;
        [Min(0)] public int electronicsCount = 3;
        [Min(0)] public int reactorComponentCount = 1;
        [Min(0)] public int noveltyCount = 1;

        [Header("Tools")]
        public float scannerRange = 9f;
        public float scannerHighlightSeconds = 5f;
        public float scannerCooldownSeconds = 2.5f;
        public CutterTuning cutter = new CutterTuning();

        [Header("Threat")]
        public EnemyTuning enemy = new EnemyTuning();
        public NoiseTuning noise = new NoiseTuning();

        public float GetOxygenCapacity(int upgradeLevel)
        {
            int level = Mathf.Clamp(upgradeLevel, 0, maxOxygenUpgradeLevel);
            return baseOxygenSeconds + oxygenUpgradeBonusSeconds * level;
        }

        public int GetCount(SalvageCategory category)
        {
            switch (category)
            {
                case SalvageCategory.SmallScrap: return smallScrapCount;
                case SalvageCategory.Electronics: return electronicsCount;
                case SalvageCategory.ReactorComponent: return reactorComponentCount;
                case SalvageCategory.Novelty: return noveltyCount;
                default: return 0;
            }
        }
    }
}
