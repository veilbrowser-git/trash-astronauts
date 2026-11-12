using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>One piece of salvage in a run. Its position in <see cref="RunStartParams.items"/> is its net id.</summary>
    [Serializable]
    public struct RunItem
    {
        /// <summary>Index of the spawn point in <see cref="NetIdTable.Points"/>.</summary>
        public byte pointIndex;
        /// <summary>Index of the salvage definition in <see cref="NetIdTable.Definitions"/>.</summary>
        public byte definitionIndex;
        /// <summary>What it is worth on this contract (list price times the contract condition).</summary>
        public ushort value;
        public byte flags;

        /// <summary>The hidden bonus item (the rubber duck). Always the last entry; its salvage id ends in "#bonus".</summary>
        public const byte FlagBonus = 1;
    }

    /// <summary>
    /// Everything that decides what a contract is: built once by whoever owns the run (the host, or the
    /// only player offline) and then applied identically by every machine. Nothing here is derived from a
    /// per-process counter or from string.GetHashCode, so two builds of the same content agree on it.
    /// </summary>
    [Serializable]
    public struct RunStartParams
    {
        /// <summary><see cref="creatureIndex"/> value meaning "no creature aboard".</summary>
        public const byte NoCreature = 255;
        /// <summary><see cref="hazardFlags"/> bit: scheduled blackouts happen on this contract.</summary>
        public const byte HazardBlackouts = 1;

        public int seed;
        /// <summary>The run number used in salvage ids ("run&lt;N&gt;/..."), so ids match whatever each machine played before.</summary>
        public ushort runIndex;
        /// <summary>Index in MissionDirector.maps.</summary>
        public byte mapIndex;
        /// <summary>A <see cref="ContractCondition"/>.</summary>
        public byte condition;
        /// <summary>Index in MissionDirector.enemies, or <see cref="NoCreature"/>.</summary>
        public byte creatureIndex;
        public int quota;
        /// <summary>The shared tank for this run, contract condition already applied.</summary>
        public float oxygenCapacitySeconds;
        public float bonusMultiplier;
        /// <summary>Bit i set = the web with index i in <see cref="NetIdTable.Wires"/> is strung this run.</summary>
        public uint wireArmedMask;
        public byte hazardFlags;
        /// <summary>Plan assignments in order, then the bonus item (if any) as the last entry.</summary>
        public RunItem[] items;
        /// <summary><see cref="ComputePlanHash"/> as computed by the sender.</summary>
        public uint planHash;

        /// <summary>
        /// FNV-1a (32 bit) over every field above except planHash, in declaration order, integers
        /// little-endian, floats as their IEEE-754 bit pattern, then each item in order as
        /// pointIndex, definitionIndex, value (little-endian), flags.
        /// </summary>
        public uint ComputePlanHash()
        {
            uint h = NetIdTable.FnvOffset;
            h = NetIdTable.Fnv(h, seed);
            h = NetIdTable.Fnv(h, runIndex);
            h = NetIdTable.Fnv(h, mapIndex);
            h = NetIdTable.Fnv(h, condition);
            h = NetIdTable.Fnv(h, creatureIndex);
            h = NetIdTable.Fnv(h, quota);
            h = NetIdTable.Fnv(h, oxygenCapacitySeconds);
            h = NetIdTable.Fnv(h, bonusMultiplier);
            h = NetIdTable.Fnv(h, (int)wireArmedMask);
            h = NetIdTable.Fnv(h, hazardFlags);
            if (items != null)
            {
                for (int i = 0; i < items.Length; i++)
                {
                    h = NetIdTable.Fnv(h, items[i].pointIndex);
                    h = NetIdTable.Fnv(h, items[i].definitionIndex);
                    h = NetIdTable.Fnv(h, items[i].value);
                    h = NetIdTable.Fnv(h, items[i].flags);
                }
            }
            return h;
        }
    }
}
