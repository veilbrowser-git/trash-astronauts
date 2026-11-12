using System;
using UnityEngine;

namespace TrashAstronauts.Core
{
    public enum SalvageCategory
    {
        SmallScrap = 0,
        Electronics = 1,
        ReactorComponent = 2,
        Novelty = 3,
    }

    [Flags]
    public enum SalvageCategoryMask
    {
        None = 0,
        SmallScrap = 1 << 0,
        Electronics = 1 << 1,
        ReactorComponent = 1 << 2,
        Novelty = 1 << 3,
        Any = SmallScrap | Electronics | ReactorComponent | Novelty,
    }

    public static class SalvageCategoryExtensions
    {
        public static SalvageCategoryMask ToMask(this SalvageCategory category) => (SalvageCategoryMask)(1 << (int)category);
        public static bool Allows(this SalvageCategoryMask mask, SalvageCategory category) => (mask & category.ToMask()) != 0;
    }

    /// <summary>Plain-data view of a salvage type used by the rules layer (and by tests).</summary>
    [Serializable]
    public struct SalvageSpec
    {
        public string id;
        public SalvageCategory category;
        public int creditValue;

        public SalvageSpec(string id, SalvageCategory category, int creditValue)
        {
            this.id = id;
            this.category = category;
            this.creditValue = creditValue;
        }
    }

    /// <summary>
    /// Editable balancing data for one kind of salvage. Assets live in
    /// Assets/TrashAstronauts/Data/Salvage and are safe to tweak without touching code.
    /// </summary>
    [CreateAssetMenu(menuName = "Trash Astronauts/Salvage Definition", fileName = "Salvage_New")]
    public sealed class SalvageDefinition : ScriptableObject
    {
        [Tooltip("Stable id used in saves, logs and (later) network messages. Never rename after shipping.")]
        public string id = "salvage";
        public string displayName = "Salvage";
        public SalvageCategory category = SalvageCategory.SmallScrap;
        [Min(0)] public int creditValue = 10;
        [Min(0.05f)] public float mass = 1f;
        [Tooltip("Bulky items are large and awkward (slower to swing around) but still carried by one player.")]
        public bool bulky;
        [Tooltip("Bonus items are not part of the planned loot or the quota guarantee; one is hidden somewhere each run.")]
        public bool bonusSpawn;
        [TextArea] public string scannerBlurb = "";
        [Tooltip("Prefab with a SalvageItem component. Assigned by the project builder.")]
        public GameObject prefab;

        public SalvageSpec ToSpec() => new SalvageSpec(id, category, creditValue);
    }
}
