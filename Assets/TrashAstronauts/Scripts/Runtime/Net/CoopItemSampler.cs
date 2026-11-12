using System;
using System.Collections.Generic;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// On the host: which of this run's salvage items are worth streaming and where they are (plan 2.3).
    /// Held or moving items are sent every tick, at most <see cref="MaxMovingPerTick"/>, those nearest the
    /// crew and fastest first; one resting item is added per tick in rotation as a refresh. An item that has
    /// been slow for <see cref="RestSeconds"/> is reported once through <see cref="CameToRest"/> (the
    /// reliable ItemRest that heals a client which missed its last poses). Plain C#; ticked by its owner.
    /// No allocation per tick.
    /// </summary>
    public sealed class CoopItemSampler
    {
        public const int MaxMovingPerTick = 12;
        public const float RestSeconds = 0.5f;
        /// <summary>
        /// Below these an item counts as still: really still, because in zero-g a slow drift never stops by itself,
        /// and an item declared at rest while it drifts on would jump on the crew's headsets.
        /// </summary>
        public const float StillSpeed = 0.01f, StillSpin = 0.05f;
        /// <summary>A resting item that has moved this far from where it was last rested is streamed again (metres).</summary>
        public const float RestDrift = 0.02f;

        /// <summary>The cap on held or moving items per tick (<see cref="MaxMovingPerTick"/> unless changed).</summary>
        public int maxMoving = MaxMovingPerTick;

        readonly MissionDirector m_Director;
        readonly List<Vector3> m_Viewers = new List<Vector3>(4);
        readonly List<int> m_Candidates = new List<int>(64);
        readonly List<float> m_Priority = new List<float>(64);
        float[] m_SlowFor = new float[0];
        bool[] m_Resting = new bool[0];
        Vector3[] m_RestAt = new Vector3[0];
        int m_RoundRobin;

        /// <summary>An item settled (has been slow for <see cref="RestSeconds"/>). Raised once per settling.</summary>
        public event Action<SalvageItem> CameToRest;

        public CoopItemSampler(MissionDirector director)
        {
            m_Director = director;
        }

        /// <summary>
        /// Call when a contract starts. Every item starts out resting where it spawned (which every machine
        /// already knows), so nothing is streamed or rested until something moves it.
        /// </summary>
        public void ResetForRun()
        {
            int count = m_Director.RunItems.Count;
            m_SlowFor = new float[count];
            m_Resting = new bool[count];
            m_RestAt = new Vector3[count];
            var items = m_Director.RunItems;
            for (int i = 0; i < count; i++)
            {
                m_SlowFor[i] = RestSeconds;
                m_Resting[i] = true;
                m_RestAt[i] = items[i] != null ? items[i].transform.position : Vector3.zero;
            }
            m_RoundRobin = 0;
        }

        static bool Streamable(SalvageItem item) => item != null && !item.IsSecured && !item.IsMounted;

        public bool IsResting(int itemId) => itemId >= 0 && itemId < m_Resting.Length && m_Resting[itemId];

        /// <summary>Speed used for priorities and rest detection: the body's, or 0 for a kinematic body nobody holds.</summary>
        static float Speed(SalvageItem item, out float spin)
        {
            var body = item.Body;
            if (body == null || body.isKinematic)
            {
                spin = 0f;
                return 0f;
            }
            spin = body.angularVelocity.magnitude;
            return body.linearVelocity.magnitude;
        }

        /// <summary>Advances rest detection by <paramref name="dt"/> seconds.</summary>
        public void Tick(float dt)
        {
            var items = m_Director.RunItems;
            if (m_Resting.Length != items.Count) ResetForRun();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!Streamable(item))
                {
                    m_Resting[i] = true;
                    m_SlowFor[i] = RestSeconds;
                    continue;
                }
                float speed = Speed(item, out float spin);
                bool still = !item.IsHeld && speed < StillSpeed && spin < StillSpin;
                if (!still)
                {
                    m_SlowFor[i] = 0f;
                    m_Resting[i] = false;
                    continue;
                }
                if (m_Resting[i])
                {
                    // Crept away from where everyone was told it rests: stream it again until it settles anew.
                    if ((item.transform.position - m_RestAt[i]).sqrMagnitude > RestDrift * RestDrift)
                    {
                        m_Resting[i] = false;
                        m_SlowFor[i] = 0f;
                    }
                    continue;
                }
                m_SlowFor[i] += dt;
                if (m_SlowFor[i] >= RestSeconds)
                {
                    m_Resting[i] = true;
                    m_RestAt[i] = item.transform.position;
                    CameToRest?.Invoke(item);
                }
            }
        }

        /// <summary>
        /// Fills <paramref name="output"/> with this tick's poses: every held or moving item up to
        /// <see cref="maxMoving"/> (and the array's length), most urgent first, then one resting item in
        /// rotation. Returns how many were written. The epoch is left 0: the session fills it at send time.
        /// </summary>
        public int Collect(ItemPoseEntry[] output)
        {
            if (output == null || output.Length == 0) return 0;
            var items = m_Director.RunItems;
            if (m_Resting.Length != items.Count) ResetForRun();

            // Whom the poses are for: the other crew members' heads (every player when there are none).
            // Index loops: foreach over the IReadOnlyList would box an enumerator on every 20 Hz tick.
            m_Viewers.Clear();
            var players = m_Director.Players;
            for (int p = 0; p < players.Count; p++)
            {
                var player = players[p];
                if (player != null && player.IsRemote) m_Viewers.Add(player.HeadPosition);
            }
            if (m_Viewers.Count == 0)
            {
                for (int p = 0; p < players.Count; p++)
                {
                    var player = players[p];
                    if (player != null) m_Viewers.Add(player.HeadPosition);
                }
            }

            m_Candidates.Clear();
            m_Priority.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!Streamable(item) || m_Resting[i]) continue;
                float speed = Speed(item, out _);
                float distance = 0f;
                if (m_Viewers.Count > 0)
                {
                    distance = float.MaxValue;
                    Vector3 at = item.transform.position;
                    foreach (var viewer in m_Viewers) distance = Mathf.Min(distance, Vector3.Distance(viewer, at));
                }
                // Lower goes first: anything in a hand, then the nearest, a fast item counting as nearer.
                float priority = distance - 2f * speed - (item.IsHeld ? 1000f : 0f);
                int at2 = m_Candidates.Count;
                while (at2 > 0 && m_Priority[at2 - 1] > priority) at2--;
                m_Candidates.Insert(at2, i);
                m_Priority.Insert(at2, priority);
            }

            int limit = Mathf.Min(maxMoving, output.Length);
            int written = 0;
            for (int k = 0; k < m_Candidates.Count && written < limit; k++)
                output[written++] = Entry(m_Candidates[k], items[m_Candidates[k]], false);

            // One resting item per tick, in turn, so a copy that went astray is put right sooner or later.
            if (written < output.Length && items.Count > 0)
            {
                for (int tries = 0; tries < items.Count; tries++)
                {
                    int i = m_RoundRobin;
                    m_RoundRobin = (m_RoundRobin + 1) % items.Count;
                    if (!Streamable(items[i]) || !m_Resting[i]) continue;
                    output[written++] = Entry(i, items[i], true);
                    break;
                }
            }
            return written;
        }

        static ItemPoseEntry Entry(int index, SalvageItem item, bool atRest)
        {
            return new ItemPoseEntry
            {
                itemId = (byte)index,
                position = CoopConvert.ToNet(item.transform.position),
                rotation = CoopConvert.ToNet(item.transform.rotation),
                held = item.IsHeld,
                atRest = atRest,
            };
        }
    }
}
