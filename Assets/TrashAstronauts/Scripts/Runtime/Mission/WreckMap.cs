using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// One derelict. Every wreck docks to the same airlock; the director switches exactly one on for each
    /// contract and takes its loot spots, panels, doors, lights and the creature's routes from here.
    /// </summary>
    public sealed class WreckMap : MonoBehaviour
    {
        public string mapId = "map";
        public string displayName = "Derelict";
        [Tooltip("One line for the mission board.")]
        public string blurb = "";

        [Header("Loot and panels")]
        public SalvageSpawnPoint[] spawnPoints;
        public Cuttable[] cuttables;

        [Header("Doors beyond the airlock")]
        public SlidingDoor[] doors;

        [Header("Creature")]
        public Transform dock;
        public EnemyWaypoint[] graph;
        public EnemyWaypoint[] patrolRoute;
        [Tooltip("Doors the creature is able to open.")]
        public SlidingDoor[] creatureDoors;

        [Header("Power (goes out in a blackout)")]
        public Light[] lights;
        public Renderer[] glowStrips;
        public AudioSource[] ambience;

        [Header("Hazards")]
        [Tooltip("Every web tripwire in this wreck. Filled by the Builder; gives each web a stable number for co-op.")]
        public WebTripwire[] tripwires;

        [Tooltip("Loose, physical props (floating junk). Kept outside the map's own hierarchy and switched with it.")]
        public GameObject looseRoot;

        public void SetAboard(bool aboard)
        {
            if (gameObject.activeSelf != aboard) gameObject.SetActive(aboard);
            if (looseRoot != null && looseRoot.activeSelf != aboard) looseRoot.SetActive(aboard);
        }
    }
}
