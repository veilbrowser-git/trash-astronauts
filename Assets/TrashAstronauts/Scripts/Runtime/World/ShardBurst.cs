using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// A few small fragments that fly off a delicate item when it takes a damaging knock, tumble away and
    /// shrink to nothing. Purely cosmetic: no colliders, no physics bodies. One shared pool of
    /// <see cref="PoolSize"/> shards, made on first use, so a hit allocates nothing after warm-up and
    /// never more than a dozen are alive.
    /// </summary>
    public sealed class ShardBurst : MonoBehaviour
    {
        public const int PoolSize = 12;
        public const int ShardsPerHit = 4;
        public const float Lifetime = 1.1f;

        static ShardBurst s_Instance;

        readonly Transform[] m_Shards = new Transform[PoolSize];
        readonly MeshRenderer[] m_Renderers = new MeshRenderer[PoolSize];
        readonly Vector3[] m_Velocity = new Vector3[PoolSize];
        readonly Vector3[] m_Spin = new Vector3[PoolSize];
        readonly float[] m_Age = new float[PoolSize];
        readonly float[] m_Size = new float[PoolSize];
        int m_Next;

        /// <summary>How many shards are flying right now.</summary>
        public static int Alive
        {
            get
            {
                if (s_Instance == null) return 0;
                int count = 0;
                for (int i = 0; i < PoolSize; i++)
                    if (s_Instance.m_Shards[i] != null && s_Instance.m_Shards[i].gameObject.activeSelf) count++;
                return count;
            }
        }

        /// <summary>Total bursts since start (for tests).</summary>
        public static int Bursts { get; private set; }

        /// <summary>Throws a few shards of <paramref name="material"/> off <paramref name="point"/>, away from the surface hit.</summary>
        public static void Emit(Vector3 point, Vector3 normal, Material material)
        {
            if (!Application.isPlaying) return;
            if (s_Instance == null)
            {
                var go = new GameObject("Shard Pool");
                s_Instance = go.AddComponent<ShardBurst>();
                s_Instance.Build();
            }
            s_Instance.Burst(point, normal, material);
        }

        void Build()
        {
            var mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            for (int i = 0; i < PoolSize; i++)
            {
                var shard = new GameObject("Shard");
                shard.transform.SetParent(transform, false);
                shard.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = shard.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                m_Shards[i] = shard.transform;
                m_Renderers[i] = renderer;
                shard.SetActive(false);
            }
        }

        void Burst(Vector3 point, Vector3 normal, Material material)
        {
            Bursts++;
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.up;
            for (int n = 0; n < ShardsPerHit; n++)
            {
                int i = m_Next;
                m_Next = (m_Next + 1) % PoolSize;
                var shard = m_Shards[i];
                if (shard == null) continue;
                if (material != null) m_Renderers[i].sharedMaterial = material;
                shard.position = point;
                shard.rotation = Random.rotation;
                m_Size[i] = Random.Range(0.008f, 0.018f);
                shard.localScale = new Vector3(m_Size[i], m_Size[i] * 0.4f, m_Size[i] * 1.6f);
                m_Velocity[i] = (normal.normalized + Random.insideUnitSphere * 0.8f) * Random.Range(0.4f, 1.1f);
                m_Spin[i] = Random.insideUnitSphere * 720f;
                m_Age[i] = 0f;
                shard.gameObject.SetActive(true);
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < PoolSize; i++)
            {
                var shard = m_Shards[i];
                if (shard == null || !shard.gameObject.activeSelf) continue;
                m_Age[i] += dt;
                if (m_Age[i] >= Lifetime)
                {
                    shard.gameObject.SetActive(false);
                    continue;
                }
                // Weightless: they keep going and tumbling, and shrink away to nothing.
                shard.position += m_Velocity[i] * dt;
                shard.Rotate(m_Spin[i] * dt, Space.Self);
                float k = 1f - m_Age[i] / Lifetime;
                float s = m_Size[i] * k;
                shard.localScale = new Vector3(s, s * 0.4f, s * 1.6f);
            }
        }
    }
}
