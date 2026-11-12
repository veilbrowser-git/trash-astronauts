using UnityEngine;
using UnityEngine.Rendering;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// The seam of a cuttable plate drawn as one small mesh on the plate's face (one draw call a panel): for every cell of
    /// every stretch, a line that starts as a dim amber weld bead, darkens and thins as the metal is scored and is a dark
    /// kerf once cut through; and over it the molten line, white-yellow under the flame and cooling through orange and
    /// dull red to nothing (<see cref="CutPoint.GlowSeconds"/>), wide where the flame bit cleanly and thin where it only
    /// scored. It rides on the plate, so a loosened plate carries its line and a freed plate leaves with glowing edges.
    /// Vertex colours only, rebuilt only while something changes or glows.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class CutSeamVisual : MonoBehaviour
    {
        /// <summary>Height of the bead and of the molten line above the face of the plate (they sit just in front of it).</summary>
        public const float BeadLift = 0.0045f, GlowLift = 0.0055f;
        const float k_BeadWidth = 0.011f, k_ScoredWidth = 0.005f, k_KerfWidth = 0.009f;
        const float k_GlowMinWidth = 0.007f, k_GlowMaxWidth = 0.03f;

        static readonly Color k_Bead = new Color(0.55f, 0.29f, 0.06f, 0.95f);
        static readonly Color k_Kerf = new Color(0.02f, 0.017f, 0.016f, 1f);
        static readonly Color k_White = new Color(1f, 0.97f, 0.82f);
        static readonly Color k_Yellow = new Color(1f, 0.8f, 0.32f);
        static readonly Color k_Orange = new Color(1f, 0.44f, 0.06f);
        static readonly Color k_Red = new Color(0.5f, 0.06f, 0.02f);

        Cuttable m_Owner;
        Mesh m_Mesh;
        Vector3[] m_Centres;   // plate-local, per cell
        Vector3[] m_Along;     // half a cell along the seam, plate-local
        Vector3[] m_Across;    // unit across the seam on the face, plate-local
        Vector3[] m_Lift;      // unit out of the face, plate-local
        Vector3[] m_Verts;
        Color32[] m_Colors;
        int m_CellCount;

        public int CellCount => m_CellCount;
        /// <summary>Colour of the molten line at a cell (tests and previews); alpha 0 when it has cooled.</summary>
        public Color32 GlowColor(int cell) => m_Colors != null ? m_Colors[m_CellCount * 4 + cell * 4] : default;
        /// <summary>Colour of the bead / kerf at a cell.</summary>
        public Color32 BeadColor(int cell) => m_Colors != null ? m_Colors[cell * 4] : default;
        /// <summary>Width of the molten line at a cell (metres).</summary>
        public float GlowWidth(int cell) => m_Verts != null ? (m_Verts[m_CellCount * 4 + cell * 4 + 1] - m_Verts[m_CellCount * 4 + cell * 4]).magnitude : 0f;

        /// <summary>Builds the seam mesh for a panel on its plate (plate at its welded home). Null without points or material.</summary>
        public static CutSeamVisual Build(Cuttable owner, Transform plate, Material material)
        {
            if (owner == null || plate == null || material == null || owner.cutPoints == null || owner.cutPoints.Length == 0) return null;
            var go = new GameObject("Seam Line") { layer = plate.gameObject.layer };
            go.transform.SetParent(plate, false);
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            var visual = go.AddComponent<CutSeamVisual>();
            visual.Setup(owner, filter);
            return visual;
        }

        void Setup(Cuttable owner, MeshFilter filter)
        {
            m_Owner = owner;
            var points = owner.cutPoints;
            m_CellCount = points.Length * CutPoint.Cells;
            m_Centres = new Vector3[m_CellCount];
            m_Along = new Vector3[m_CellCount];
            m_Across = new Vector3[m_CellCount];
            m_Lift = new Vector3[m_CellCount];
            Transform space = transform;
            var bounds = new Bounds();
            bool first = true;
            for (int p = 0; p < points.Length; p++)
            {
                var point = points[p];
                for (int c = 0; c < CutPoint.Cells; c++)
                {
                    int i = p * CutPoint.Cells + c;
                    if (point == null) continue;
                    Vector3 axis = space.InverseTransformDirection(point.Axis).normalized;
                    Vector3 normal = space.InverseTransformDirection(point.Normal).normalized;
                    m_Centres[i] = space.InverseTransformPoint(point.CellPosition(c));
                    m_Along[i] = axis * (point.SeamLength / CutPoint.Cells * 0.5f * 1.06f);
                    m_Across[i] = Vector3.Cross(normal, axis).normalized;
                    m_Lift[i] = normal;
                    if (first) { bounds = new Bounds(m_Centres[i], Vector3.zero); first = false; }
                    else bounds.Encapsulate(m_Centres[i]);
                }
            }
            bounds.Expand(0.08f);

            int quads = m_CellCount * 2;
            m_Verts = new Vector3[quads * 4];
            m_Colors = new Color32[quads * 4];
            var uvs = new Vector2[quads * 4];
            var tris = new int[quads * 6];
            for (int q = 0; q < quads; q++)
            {
                // Bead/kerf quads sample the solid half of the profile texture, the molten line its soft half.
                float u = q < m_CellCount ? 0.125f : 0.875f;
                uvs[q * 4 + 0] = new Vector2(u, 0f);
                uvs[q * 4 + 1] = new Vector2(u, 1f);
                uvs[q * 4 + 2] = new Vector2(u, 1f);
                uvs[q * 4 + 3] = new Vector2(u, 0f);
                tris[q * 6 + 0] = q * 4 + 0;
                tris[q * 6 + 1] = q * 4 + 1;
                tris[q * 6 + 2] = q * 4 + 2;
                tris[q * 6 + 3] = q * 4 + 0;
                tris[q * 6 + 4] = q * 4 + 2;
                tris[q * 6 + 5] = q * 4 + 3;
            }
            m_Mesh = new Mesh { name = "Seam Line" };
            m_Mesh.MarkDynamic();
            Rebuild();
            m_Mesh.vertices = m_Verts;
            m_Mesh.uv = uvs;
            m_Mesh.colors32 = m_Colors;
            m_Mesh.triangles = tris;
            m_Mesh.bounds = bounds;
            filter.sharedMesh = m_Mesh;
        }

        /// <summary>Molten-line colour for a temperature (1 white hot .. 0 cold), alpha fading out at the cold end.</summary>
        public static Color GlowRamp(float t)
        {
            Color c;
            if (t >= 0.8f) c = Color.Lerp(k_Yellow, k_White, (t - 0.8f) / 0.2f);
            else if (t >= 0.5f) c = Color.Lerp(k_Orange, k_Yellow, (t - 0.5f) / 0.3f);
            else if (t >= 0.22f) c = Color.Lerp(k_Red, k_Orange, (t - 0.22f) / 0.28f);
            else c = k_Red;
            float a = Mathf.Clamp01(t / 0.22f);
            c.a = a * a * (3f - 2f * a);
            return c;
        }

        /// <summary>Writes positions and colours from the cells' state and uploads them.</summary>
        public void Refresh()
        {
            if (m_Mesh == null) return;
            Rebuild();
            m_Mesh.SetVertices(m_Verts);
            m_Mesh.SetColors(m_Colors);
        }

        void Rebuild()
        {
            var points = m_Owner != null ? m_Owner.cutPoints : null;
            bool open = m_Owner != null && m_Owner.IsOpen;
            for (int p = 0; points != null && p < points.Length; p++)
            {
                var point = points[p];
                for (int c = 0; c < CutPoint.Cells; c++)
                {
                    int i = p * CutPoint.Cells + c;
                    float depth = point != null ? point.DisplayDepthAt(c) : 0f;
                    float glow = point != null ? point.GlowAt(c) : 0f;
                    float bite = point != null ? point.BiteAt(c) : 0f;

                    // Bead -> scored line -> kerf. A freed plate has no kerf left on it, only its hot edge.
                    float beadWidth = depth >= 1f ? k_KerfWidth : Mathf.Lerp(k_BeadWidth, k_ScoredWidth, depth);
                    Color bead = Color.Lerp(k_Bead, k_Kerf, Mathf.Clamp01(depth / 0.6f));
                    if (open) bead.a = 0f;
                    Quad(i, m_Centres[i] + m_Lift[i] * BeadLift, m_Along[i], m_Across[i] * (beadWidth * 0.5f), bead);

                    float glowWidth = Mathf.Lerp(k_GlowMinWidth, k_GlowMaxWidth, Mathf.Clamp01(bite)) * (0.55f + 0.45f * glow);
                    Quad(m_CellCount + i, m_Centres[i] + m_Lift[i] * GlowLift, m_Along[i], m_Across[i] * (glowWidth * 0.5f), glow > 0f ? GlowRamp(glow) : Color.clear);
                }
            }
        }

        void Quad(int q, Vector3 centre, Vector3 along, Vector3 across, Color color)
        {
            int v = q * 4;
            m_Verts[v + 0] = centre - along - across;
            m_Verts[v + 1] = centre - along + across;
            m_Verts[v + 2] = centre + along + across;
            m_Verts[v + 3] = centre + along - across;
            Color32 c = color;
            m_Colors[v + 0] = c;
            m_Colors[v + 1] = c;
            m_Colors[v + 2] = c;
            m_Colors[v + 3] = c;
        }

        void OnDestroy()
        {
            if (m_Mesh != null)
            {
                if (Application.isPlaying) Destroy(m_Mesh);
                else DestroyImmediate(m_Mesh);
            }
        }
    }
}
