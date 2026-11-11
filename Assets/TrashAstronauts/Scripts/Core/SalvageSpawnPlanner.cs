using System;
using System.Collections.Generic;

namespace TrashAstronauts.Core
{
    /// <summary>Rules-side description of a curated spawn location in the wreck.</summary>
    [Serializable]
    public struct SpawnPointInfo
    {
        public string id;
        public SalvageCategoryMask allowed;
        /// <summary>Id of the cuttable panel/mount that must be opened to reach this point, or empty.</summary>
        public string requiresPanelId;

        public SpawnPointInfo(string id, SalvageCategoryMask allowed, string requiresPanelId = null)
        {
            this.id = id;
            this.allowed = allowed;
            this.requiresPanelId = requiresPanelId;
        }

        public bool IsGated => !string.IsNullOrEmpty(requiresPanelId);
    }

    public struct SpawnAssignment
    {
        public string salvageId;
        public string spawnPointId;
        public string definitionId;
        public int value;
        public SalvageCategory category;
    }

    public sealed class SpawnPlan
    {
        public readonly List<SpawnAssignment> Assignments = new List<SpawnAssignment>();
        public int TotalValue;
        /// <summary>Value reachable without cutting anything open.</summary>
        public int UngatedValue;
        /// <summary>Quota actually used for the run. Normally the requested quota; lowered only if the level cannot support it.</summary>
        public int Quota;
        public bool QuotaWasLowered;
    }

    /// <summary>
    /// Chooses which salvage appears at which curated spawn point for a run.
    /// Deterministic for a given seed, so a future host can send only the seed to clients.
    /// Guarantees: one item per point, categories respected, and total loot value >= quota x margin
    /// (topping up with extra items, or lowering the quota as a last resort).
    /// </summary>
    public static class SalvageSpawnPlanner
    {
        static readonly SalvageCategory[] k_PlacementOrder =
        {
            SalvageCategory.ReactorComponent,
            SalvageCategory.Novelty,
            SalvageCategory.Electronics,
            SalvageCategory.SmallScrap,
        };

        public static string MakeSalvageId(int runIndex, string spawnPointId) => "run" + runIndex + "/" + spawnPointId;

        public static SpawnPlan Plan(
            IReadOnlyList<SpawnPointInfo> points,
            IReadOnlyList<SalvageSpec> specs,
            Func<SalvageCategory, int> countFor,
            int quota,
            float safetyMargin,
            int seed,
            int runIndex)
        {
            var plan = new SpawnPlan { Quota = Math.Max(0, quota) };
            var rng = new Random(seed);
            var free = new List<SpawnPointInfo>(points);
            Shuffle(free, rng);

            foreach (var category in k_PlacementOrder)
            {
                int wanted = countFor != null ? countFor(category) : 0;
                for (int i = 0; i < wanted; i++)
                {
                    if (!TryPlace(plan, free, specs, category, rng, runIndex)) break;
                }
            }

            int required = (int)Math.Ceiling(plan.Quota * Math.Max(1f, safetyMargin));

            // Top up with the most valuable thing that still fits somewhere.
            bool placed = true;
            while (plan.TotalValue < required && free.Count > 0 && placed)
            {
                placed = false;
                foreach (var category in CategoriesByBestValue(specs))
                {
                    if (TryPlace(plan, free, specs, category, rng, runIndex))
                    {
                        placed = true;
                        break;
                    }
                }
            }

            if (plan.TotalValue < required)
            {
                plan.Quota = (int)Math.Floor(plan.TotalValue / Math.Max(1f, safetyMargin));
                plan.QuotaWasLowered = true;
            }

            return plan;
        }

        static bool TryPlace(SpawnPlan plan, List<SpawnPointInfo> free, IReadOnlyList<SalvageSpec> specs,
            SalvageCategory category, Random rng, int runIndex)
        {
            var candidates = new List<SalvageSpec>();
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].category == category) candidates.Add(specs[i]);
            }
            if (candidates.Count == 0) return false;

            int pointIndex = -1;
            for (int i = 0; i < free.Count; i++)
            {
                if (free[i].allowed.Allows(category)) { pointIndex = i; break; }
            }
            if (pointIndex < 0) return false;

            var point = free[pointIndex];
            free.RemoveAt(pointIndex);
            var spec = candidates[rng.Next(candidates.Count)];

            plan.Assignments.Add(new SpawnAssignment
            {
                salvageId = MakeSalvageId(runIndex, point.id),
                spawnPointId = point.id,
                definitionId = spec.id,
                value = spec.creditValue,
                category = spec.category,
            });
            plan.TotalValue += spec.creditValue;
            if (!point.IsGated) plan.UngatedValue += spec.creditValue;
            return true;
        }

        static List<SalvageCategory> CategoriesByBestValue(IReadOnlyList<SalvageSpec> specs)
        {
            var best = new Dictionary<SalvageCategory, int>();
            for (int i = 0; i < specs.Count; i++)
            {
                if (!best.TryGetValue(specs[i].category, out int value) || specs[i].creditValue > value)
                    best[specs[i].category] = specs[i].creditValue;
            }
            var ordered = new List<SalvageCategory>(best.Keys);
            ordered.Sort((a, b) => best[b].CompareTo(best[a]));
            return ordered;
        }

        static void Shuffle<T>(List<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
