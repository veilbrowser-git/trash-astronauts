using System;
using System.Collections.Generic;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// Small numbers for the world objects the Builder names with strings. Doors, cut panels, spawn points,
    /// webs and salvage definitions are numbered by ordinal sort of their ids, so every machine running
    /// the same build gets the same numbers without ever sending a string. Wrecks keep the order of
    /// MissionDirector.maps and creatures the order of MissionDirector.enemies (their index is what
    /// <see cref="RunStartParams"/> carries). Object names and hierarchy paths are never used.
    ///
    /// Plain C#: build one from lists of ids. MissionDirector.NetIds builds it from the loaded scene.
    /// </summary>
    public sealed class NetIdTable
    {
        // Field widths of the wire protocol. Content that outgrows them must fail a test, not a session.
        /// <summary>Door open states travel as a 16-bit mask.</summary>
        public const int MaxDoors = 16;
        /// <summary>Open panels travel as a 16-bit mask.</summary>
        public const int MaxPanels = 16;
        /// <summary>Web states travel as 32-bit masks.</summary>
        public const int MaxWires = 32;
        /// <summary>Item ids and the item count are one byte.</summary>
        public const int MaxItems = 255;
        /// <summary>Creature index is one byte with 255 meaning none; the roster is kept far below that.</summary>
        public const int MaxCreatures = 8;
        /// <summary>Spawn point index is one byte.</summary>
        public const int MaxPoints = 255;
        /// <summary>Salvage definition index is one byte.</summary>
        public const int MaxDefinitions = 255;
        /// <summary>Map index is one byte.</summary>
        public const int MaxMaps = 255;
        /// <summary>A cut point is addressed as (panel, index) with a one-byte index.</summary>
        public const int MaxCutPointsPerPanel = 255;
        /// <summary>Item values travel as 16 bits.</summary>
        public const int MaxItemValue = 65535;

        /// <summary>One list of ids and their numbers.</summary>
        public sealed class Table
        {
            readonly string[] m_Ids;
            readonly Dictionary<string, int> m_Index = new Dictionary<string, int>(StringComparer.Ordinal);
            readonly List<string> m_Duplicates = new List<string>();

            /// <param name="ids">The ids. Null and empty entries are dropped and counted in <see cref="InvalidCount"/>.</param>
            /// <param name="sort">True to number by ordinal sort (order of <paramref name="ids"/> does not matter); false to keep the given order.</param>
            public Table(IEnumerable<string> ids, bool sort = true)
            {
                var list = new List<string>();
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        if (string.IsNullOrEmpty(id)) { InvalidCount++; continue; }
                        if (m_Index.ContainsKey(id))
                        {
                            if (!m_Duplicates.Contains(id)) m_Duplicates.Add(id);
                            continue;
                        }
                        m_Index[id] = 0;
                        list.Add(id);
                    }
                }
                if (sort) list.Sort(StringComparer.Ordinal);
                m_Duplicates.Sort(StringComparer.Ordinal);
                m_Ids = list.ToArray();
                for (int i = 0; i < m_Ids.Length; i++) m_Index[m_Ids[i]] = i;
            }

            public int Count => m_Ids.Length;
            /// <summary>The id with this number, or null if there is none.</summary>
            public string this[int index] => index >= 0 && index < m_Ids.Length ? m_Ids[index] : null;
            public IReadOnlyList<string> Ids => m_Ids;
            /// <summary>Ids that were given more than once (each listed once). Empty for healthy content.</summary>
            public IReadOnlyList<string> Duplicates => m_Duplicates;
            /// <summary>How many null or empty ids were given.</summary>
            public int InvalidCount { get; }

            /// <summary>The number of this id, or -1 if it is not in the table.</summary>
            public int IndexOf(string id) => id != null && m_Index.TryGetValue(id, out int index) ? index : -1;
            public bool Contains(string id) => IndexOf(id) >= 0;
        }

        public Table Doors { get; }
        public Table Panels { get; }
        /// <summary>Salvage spawn points of every wreck.</summary>
        public Table Points { get; }
        /// <summary>Web tripwires of every wreck.</summary>
        public Table Wires { get; }
        /// <summary>Salvage catalog definitions.</summary>
        public Table Definitions { get; }
        /// <summary>Wrecks, in MissionDirector.maps order (not sorted).</summary>
        public Table Maps { get; }
        public int CreatureCount { get; }

        readonly int[] m_CutCounts;
        readonly int[] m_CutOffsets;

        /// <param name="panels">Panel id and how many cut points its seam has.</param>
        /// <param name="mapIdsInOrder">Map ids in MissionDirector.maps order.</param>
        public NetIdTable(
            IEnumerable<string> doorIds,
            IEnumerable<KeyValuePair<string, int>> panels,
            IEnumerable<string> pointIds,
            IEnumerable<string> wireIds,
            IEnumerable<string> mapIdsInOrder,
            IEnumerable<string> definitionIds,
            int creatureCount)
        {
            Doors = new Table(doorIds);
            Points = new Table(pointIds);
            Wires = new Table(wireIds);
            Definitions = new Table(definitionIds);
            Maps = new Table(mapIdsInOrder, false);
            CreatureCount = Math.Max(0, creatureCount);

            var panelIds = new List<string>();
            var cuts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (panels != null)
            {
                foreach (var pair in panels)
                {
                    panelIds.Add(pair.Key);
                    if (!string.IsNullOrEmpty(pair.Key) && !cuts.ContainsKey(pair.Key)) cuts[pair.Key] = Math.Max(0, pair.Value);
                }
            }
            Panels = new Table(panelIds);
            m_CutCounts = new int[Panels.Count];
            m_CutOffsets = new int[Panels.Count];
            int offset = 0;
            for (int i = 0; i < Panels.Count; i++)
            {
                m_CutCounts[i] = cuts[Panels[i]];
                m_CutOffsets[i] = offset;
                offset += m_CutCounts[i];
            }
            TotalCutPoints = offset;
        }

        /// <summary>What the hidden bonus item's spawn point is suffixed with in its salvage id.</summary>
        public const string BonusSuffix = "#bonus";

        /// <summary>
        /// The salvage id of one item of a run: "run&lt;N&gt;/&lt;pointId&gt;", with "#bonus" appended for the
        /// bonus item. Built from the run number the run's owner sent, never from a local counter, so it
        /// is the same string on every machine. Null if the item's spawn point is not in this table.
        /// </summary>
        public string SalvageIdFor(int runIndex, RunItem item)
        {
            string pointId = Points[item.pointIndex];
            if (pointId == null) return null;
            bool bonus = (item.flags & RunItem.FlagBonus) != 0;
            return SalvageSpawnPlanner.MakeSalvageId(runIndex, bonus ? pointId + BonusSuffix : pointId);
        }

        /// <summary>How many cut points the panel with this number has (0 if there is no such panel).</summary>
        public int CutPointCount(int panelIndex) => panelIndex >= 0 && panelIndex < m_CutCounts.Length ? m_CutCounts[panelIndex] : 0;
        /// <summary>Position of this panel's first cut point in a bitset of every cut point (panels in table order).</summary>
        public int CutPointOffset(int panelIndex) => panelIndex >= 0 && panelIndex < m_CutOffsets.Length ? m_CutOffsets[panelIndex] : -1;
        public int TotalCutPoints { get; }

        /// <summary>
        /// A fingerprint of the protocol version and of everything that is numbered here. Two builds whose
        /// doors, panels (and their cut point counts), spawn points, webs, wrecks, salvage catalog or
        /// creature roster differ get different values and refuse each other. FNV-1a over the sorted id
        /// strings (UTF-16 code units, so it is the same under Mono and IL2CPP).
        /// </summary>
        public uint ContentHash(int protocolVersion)
        {
            uint h = FnvOffset;
            h = Fnv(h, protocolVersion);
            h = HashTable(h, Doors);
            h = HashTable(h, Panels);
            for (int i = 0; i < m_CutCounts.Length; i++) h = Fnv(h, m_CutCounts[i]);
            h = HashTable(h, Points);
            h = HashTable(h, Wires);
            h = HashTable(h, Maps);
            h = HashTable(h, Definitions);
            h = Fnv(h, CreatureCount);
            return h;
        }

        static uint HashTable(uint h, Table table)
        {
            h = Fnv(h, table.Count);
            for (int i = 0; i < table.Count; i++) h = Fnv(h, table[i]);
            return h;
        }

        /// <summary>
        /// Everything wrong with this content from the protocol's point of view: repeated or empty ids and
        /// counts that do not fit their wire fields. Empty when all is well.
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            CheckTable(problems, "door", Doors, MaxDoors);
            CheckTable(problems, "panel", Panels, MaxPanels);
            CheckTable(problems, "spawn point", Points, MaxPoints);
            CheckTable(problems, "web", Wires, MaxWires);
            CheckTable(problems, "map", Maps, MaxMaps);
            CheckTable(problems, "salvage definition", Definitions, MaxDefinitions);
            if (CreatureCount > MaxCreatures) problems.Add(CreatureCount + " creatures, the protocol allows " + MaxCreatures);
            for (int i = 0; i < m_CutCounts.Length; i++)
            {
                if (m_CutCounts[i] > MaxCutPointsPerPanel)
                    problems.Add("panel " + Panels[i] + " has " + m_CutCounts[i] + " cut points, the protocol allows " + MaxCutPointsPerPanel);
            }
            return problems;
        }

        static void CheckTable(List<string> problems, string what, Table table, int max)
        {
            foreach (var id in table.Duplicates) problems.Add("duplicate " + what + " id '" + id + "'");
            if (table.InvalidCount > 0) problems.Add(table.InvalidCount + " " + what + " id(s) are empty");
            if (table.Count > max) problems.Add(table.Count + " " + what + " ids, the protocol allows " + max);
        }

        // ------------------------------------------------------------------ FNV-1a, shared by the hashes in this folder

        public const uint FnvOffset = 2166136261u;
        public const uint FnvPrime = 16777619u;

        public static uint Fnv(uint hash, byte value) => unchecked((hash ^ value) * FnvPrime);

        public static uint Fnv(uint hash, bool value) => Fnv(hash, value ? (byte)1 : (byte)0);

        public static uint Fnv(uint hash, ushort value)
        {
            hash = Fnv(hash, (byte)(value & 0xff));
            return Fnv(hash, (byte)(value >> 8));
        }

        public static uint Fnv(uint hash, int value)
        {
            unchecked
            {
                uint v = (uint)value;
                hash = Fnv(hash, (byte)(v & 0xff));
                hash = Fnv(hash, (byte)((v >> 8) & 0xff));
                hash = Fnv(hash, (byte)((v >> 16) & 0xff));
                return Fnv(hash, (byte)(v >> 24));
            }
        }

        public static uint Fnv(uint hash, float value) => Fnv(hash, BitConverter.SingleToInt32Bits(value));

        /// <summary>Length, then each UTF-16 code unit little-endian. Null hashes like the empty string.</summary>
        public static uint Fnv(uint hash, string value)
        {
            if (value == null) return Fnv(hash, 0);
            hash = Fnv(hash, value.Length);
            for (int i = 0; i < value.Length; i++) hash = Fnv(hash, (ushort)value[i]);
            return hash;
        }
    }
}
