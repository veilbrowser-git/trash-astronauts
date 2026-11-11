using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// A hand-placed location where salvage may appear. Each run picks a random subset, so loot moves
    /// around between runs while the ship layout stays fixed.
    /// </summary>
    public sealed class SalvageSpawnPoint : MonoBehaviour
    {
        [Tooltip("Stable id. Salvage ids are derived from it.")]
        public string pointId = "spawn";
        public SalvageCategoryMask allowed = SalvageCategoryMask.SmallScrap;
        [Tooltip("Panel that seals the compartment this point is in (loot floats free inside).")]
        public Cuttable behindPanel;
        [Tooltip("Mount that physically clamps the item here until its bracket is cut.")]
        public Cuttable mount;

        public Cuttable Gate => mount != null ? mount : behindPanel;

        public SpawnPointInfo ToInfo()
        {
            return new SpawnPointInfo(pointId, allowed, Gate != null ? Gate.panelId : null);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = Gate != null ? new Color(1f, 0.5f, 0.1f, 0.9f) : new Color(0.2f, 1f, 0.5f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.12f);
        }
    }
}
