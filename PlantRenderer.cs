using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlantRenderer : MonoBehaviour
{

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public float x, y, z;
        public static implicit operator Vector3(Vec3 v) => new Vector3(v.x, v.y, v.z);
    }

    public enum NodeType : int { Branch = 0, Leaf = 1, Flower = 2 }


    [StructLayout(LayoutKind.Sequential)]
    public struct PlantNode
    {
        public Vec3 origin;    // branch base, or leaf/flower position
        public Vec3 end;       // branch tip   (= origin for leaf/flower)
        public Vec3 heading;   // turtle H: branch forward / leaf face direction
        public Vec3 left;      // turtle L: leaf lateral axis
        public float radius;    // branch cross-section radius, or leaf/flower size
        public int type;      // NodeType
    }

    // == Inspector ============================================================

    public enum TreeType
    {
        ParametricTree = 0,
        StochasticShrub = 1,
        HybridPlant = 2,
        ABOPTree = 3,
    }

    [Header("L-System")]
    public TreeType treeType = TreeType.ABOPTree;
    public int iterations = 4;
    public uint seed = 67;

    [Header("Branch Geometry")]
    [Tooltip("Cylinder sides per branch segment. 6–10 is a good range.")]
    public int radialSegments = 8;
    public Material branchMaterial;

    [Header("Leaf Geometry")]
    [Tooltip("Global scale multiplier applied to the per-node leaf size.")]
    public float leafScale = 1.0f;
    public Material leafMaterial;

    [Header("Flower Geometry")]
    public int petalCount = 5;
    [Tooltip("Global scale multiplier applied to the per-node flower size.")]
    public float flowerScale = 1.0f;
    [Tooltip("How far petals rise above their attachment plane (0 = flat).")]
    [Range(0f, 1f)]
    public float petalCurvature = 0.3f;
    public Material flowerMaterial;

    // == Private state ========================================================

    const int MAX_NODES = 200_000;
    readonly PlantNode[] _nodeBuffer = new PlantNode[MAX_NODES];

    GameObject _branchGO, _leafGO, _flowerGO;
    MeshFilter _branchMF, _leafMF, _flowerMF;
    MeshRenderer _branchMR, _leafMR, _flowerMR;

    // == Native import ========================================================

    [DllImport("libPlantSim", CallingConvention = CallingConvention.Cdecl)]
    static extern int GeneratePlant(
        int exampleId,
        int iterations,
        [Out] PlantNode[] outNodes,
        int maxNodes,
        uint seed
    );

    // == Unity lifecycle =======================================================

    void Awake()
    {
        (_branchGO, _branchMF, _branchMR) = MakeChild("Branches");
        (_leafGO, _leafMF, _leafMR) = MakeChild("Leaves");
        (_flowerGO, _flowerMF, _flowerMR) = MakeChild("Flowers");

        _branchMR.sharedMaterial = branchMaterial ?? DefaultMaterial(new Color(0.35f, 0.22f, 0.12f));
        _leafMR.sharedMaterial = leafMaterial ?? DefaultMaterial(new Color(0.18f, 0.55f, 0.20f));
        _flowerMR.sharedMaterial = flowerMaterial ?? DefaultMaterial(new Color(0.95f, 0.70f, 0.75f));
    }

    void Start() => Generate();

    void Update()
    {
        if (Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            iterations++;
            Debug.Log($"[PlantRenderer] iterations → {iterations}");
            Generate();
        }

        if (Keyboard.current.rKey.wasPressedThisFrame)
        {
            seed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Debug.Log($"[PlantRenderer] seed → {seed}");
            Generate();
        }

        if (Keyboard.current.digit1Key.wasPressedThisFrame) { treeType = TreeType.ParametricTree; Generate(); }
        if (Keyboard.current.digit2Key.wasPressedThisFrame) { treeType = TreeType.StochasticShrub; Generate(); }
        if (Keyboard.current.digit3Key.wasPressedThisFrame) { treeType = TreeType.HybridPlant; Generate(); }
        if (Keyboard.current.digit4Key.wasPressedThisFrame) { treeType = TreeType.ABOPTree; Generate(); }
    }

    // == Generation ============================================================

    void Generate()
    {
        int count = GeneratePlant((int)treeType, iterations, _nodeBuffer, MAX_NODES, seed);

        if (count <= 0)
        {
            Debug.LogError("[PlantRenderer] Native library returned no nodes.");
            return;
        }
        count = Mathf.Min(count, MAX_NODES);

        // Partition by NodeType into three lists
        var branches = new List<PlantNode>(count);
        var leaves = new List<PlantNode>(count / 3);
        var flowers = new List<PlantNode>(count / 6);

        for (int i = 0; i < count; i++)
        {
            switch ((NodeType)_nodeBuffer[i].type)
            {
                case NodeType.Branch: branches.Add(_nodeBuffer[i]); break;
                case NodeType.Leaf: leaves.Add(_nodeBuffer[i]); break;
                case NodeType.Flower: flowers.Add(_nodeBuffer[i]); break;
            }
        }

        _branchMF.sharedMesh = BuildBranchMesh(branches);
        _leafMF.sharedMesh = BuildLeafMesh(leaves);
        _flowerMF.sharedMesh = BuildFlowerMesh(flowers);

        Debug.Log($"[PlantRenderer] {branches.Count} branches | " +
                  $"{leaves.Count} leaves | {flowers.Count} flowers");
    }

    // == Branch mesh ===========================================================
    //
    // Uses parallel-transport (Bishop) frames to propagate a stable normal
    // vector along the branch chain.  When consecutive segments are connected
    // (start of segment i+1 ≈ end of segment i), the frame is transported
    // with minimal rotation so rings never twist at junctions.
    // When a new branch begins (after a stack pop) a fresh stable frame
    // is computed from global-up.

    Mesh BuildBranchMesh(List<PlantNode> nodes)
    {
        if (nodes.Count == 0) return EmptyMesh("Branches");

        int vCap = nodes.Count * radialSegments * 2;
        int tCap = nodes.Count * radialSegments * 6;

        var verts = new List<Vector3>(vCap);
        var norms = new List<Vector3>(vCap);
        var uvs = new List<Vector2>(vCap);
        var tris = new List<int>(tCap);

        Vector3 prevEnd = Vector3.zero;
        Vector3 prevTangent = Vector3.up;
        Vector3 prevNormal = Vector3.right;
        bool hasPrev = false;

        for (int i = 0; i < nodes.Count; i++)
        {
            Vector3 a = nodes[i].origin;
            Vector3 b = nodes[i].end;
            float r = Mathf.Max(nodes[i].radius, 0.001f);

            Vector3 dir = b - a;
            if (dir.sqrMagnitude < 1e-8f) continue;

            Vector3 tangent = dir.normalized;

            // Propagate frame or restart it
            Vector3 frameNormal;
            if (hasPrev && (a - prevEnd).sqrMagnitude < (r * r))
                frameNormal = ParallelTransport(prevNormal, prevTangent, tangent);
            else
                frameNormal = StableNormal(tangent);

            Vector3 frameBitangent = Vector3.Cross(tangent, frameNormal).normalized;

            // UV: U wraps 0→1 around cylinder, V travels along segment chain
            float vA = i / (float)nodes.Count;
            float vB = (i + 1) / (float)nodes.Count;

            int ringBase = verts.Count;

            for (int s = 0; s < radialSegments; s++)
            {
                float theta = s / (float)radialSegments * Mathf.PI * 2f;
                float cosT = Mathf.Cos(theta);
                float sinT = Mathf.Sin(theta);
                Vector3 outDir = cosT * frameNormal + sinT * frameBitangent;
                float uCoord = (float)s / radialSegments;

                verts.Add(a + outDir * r);
                verts.Add(b + outDir * r);
                norms.Add(outDir);
                norms.Add(outDir);
                uvs.Add(new Vector2(uCoord, vA));
                uvs.Add(new Vector2(uCoord, vB));
            }

            // Stitch rings into quads
            for (int s = 0; s < radialSegments; s++)
            {
                int next = (s + 1) % radialSegments;
                int i0 = ringBase + s * 2;
                int i1 = ringBase + s * 2 + 1;
                int i2 = ringBase + next * 2;
                int i3 = ringBase + next * 2 + 1;

                tris.Add(i0); tris.Add(i2); tris.Add(i1);
                tris.Add(i1); tris.Add(i2); tris.Add(i3);
            }

            prevEnd = b;
            prevTangent = tangent;
            prevNormal = frameNormal;
            hasPrev = true;
        }

        return FinaliseMesh("Branches", verts, norms, uvs, tris, recalcNormals: false);
    }

    // == Leaf mesh =============================================================
    //
    // Each leaf is a teardrop quad (4 vertices) oriented by the turtle frame:
    //   heading (H) → leaf grows in this direction
    //   left    (L) → leaf spreads laterally in this direction
    //   up            = cross(H, L) → leaf face normal
    //
    // Both faces are emitted so leaves are visible from either side.

    Mesh BuildLeafMesh(List<PlantNode> nodes)
    {
        if (nodes.Count == 0) return EmptyMesh("Leaves");

        var verts = new List<Vector3>(nodes.Count * 8);
        var norms = new List<Vector3>(nodes.Count * 8);
        var uvs = new List<Vector2>(nodes.Count * 8);
        var tris = new List<int>(nodes.Count * 12);

        foreach (var node in nodes)
        {
            Vector3 pos = node.origin;
            Vector3 forward = ((Vector3)node.heading).normalized;
            Vector3 right = ((Vector3)node.left).normalized;
            Vector3 faceNrm = Vector3.Cross(forward, right).normalized;

            float size = node.radius * leafScale;
            float halfW = size * 0.35f;

            //  Layout (local space):
            //    p0 = base center
            //    p1 = right shoulder
            //    p2 = tip
            //    p3 = left shoulder
            Vector3 p0 = pos;
            Vector3 p1 = pos + right * halfW + forward * size * 0.40f;
            Vector3 p2 = pos + forward * size;
            Vector3 p3 = pos - right * halfW + forward * size * 0.40f;

            // Front face
            int b = verts.Count;
            verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
            for (int k = 0; k < 4; k++) norms.Add(faceNrm);
            uvs.Add(new Vector2(0.5f, 0.0f));
            uvs.Add(new Vector2(1.0f, 0.4f));
            uvs.Add(new Vector2(0.5f, 1.0f));
            uvs.Add(new Vector2(0.0f, 0.4f));
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);

            // Back face (duplicated vertices with flipped normal)
            b = verts.Count;
            verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
            for (int k = 0; k < 4; k++) norms.Add(-faceNrm);
            uvs.Add(new Vector2(0.5f, 0.0f));
            uvs.Add(new Vector2(1.0f, 0.4f));
            uvs.Add(new Vector2(0.5f, 1.0f));
            uvs.Add(new Vector2(0.0f, 0.4f));
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
        }

        return FinaliseMesh("Leaves", verts, norms, uvs, tris, recalcNormals: false);
    }

    // == Flower mesh ===========================================================
    //
    // Each flower is built from:
    //   • A central disc (filled triangle fan, radius = size * 0.25)
    //   • petalCount petals arranged radially (each a raised quad)
    //
    // The petal quads are slightly tilted upward by petalCurvature so the
    // flower looks cupped rather than flat.

    Mesh BuildFlowerMesh(List<PlantNode> nodes)
    {
        if (nodes.Count == 0) return EmptyMesh("Flowers");

        // Vertices per flower: disc(petalCount+1) + petals(petalCount*4)
        int vPerFlower = (petalCount + 1) + petalCount * 4;
        int tPerFlower = petalCount * 3 + petalCount * 4; // disc fan + petal quads

        var verts = new List<Vector3>(nodes.Count * vPerFlower);
        var norms = new List<Vector3>(nodes.Count * vPerFlower);
        var uvs = new List<Vector2>(nodes.Count * vPerFlower);
        var tris = new List<int>(nodes.Count * tPerFlower * 3);

        foreach (var node in nodes)
        {
            Vector3 center = node.origin;
            Vector3 axis = ((Vector3)node.heading).normalized;  // face normal
            Vector3 right = ((Vector3)node.left).normalized;
            Vector3 up = Vector3.Cross(axis, right).normalized;

            float size = node.radius * flowerScale;
            float discR = size * 0.25f;
            float petalLen = size;
            float petalHalfW = size * 0.28f;

            // == Central disc ================================================
            int discBase = verts.Count;

            verts.Add(center);
            norms.Add(axis);
            uvs.Add(new Vector2(0.5f, 0.5f));

            for (int p = 0; p < petalCount; p++)
            {
                float a = p / (float)petalCount * Mathf.PI * 2f;
                Vector3 d = Mathf.Cos(a) * right + Mathf.Sin(a) * up;
                verts.Add(center + d * discR);
                norms.Add(axis);
                uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a) * 0.25f,
                                    0.5f + 0.5f * Mathf.Sin(a) * 0.25f));
            }

            for (int p = 0; p < petalCount; p++)
            {
                int cur = discBase + 1 + p;
                int next = discBase + 1 + (p + 1) % petalCount;
                tris.Add(discBase); tris.Add(cur); tris.Add(next);
            }

            // == Petals ======================================================
            for (int p = 0; p < petalCount; p++)
            {
                float a = p / (float)petalCount * Mathf.PI * 2f;
                Vector3 petalDir = Mathf.Cos(a) * right + Mathf.Sin(a) * up;

                // Root edge of petal sits at the disc perimeter
                Vector3 rootL = center + petalDir * discR - petalDir * 0f
                                                           + (petalDir - right * Mathf.Sin(a)
                                                              + up * Mathf.Cos(a)).normalized * petalHalfW * 0f;

                // Simpler: two root corners + two tip corners
                Vector3 rootCenter = center + petalDir * discR;
                Vector3 lateral = Vector3.Cross(petalDir, axis).normalized;

                Vector3 rL = rootCenter - lateral * petalHalfW;
                Vector3 rR = rootCenter + lateral * petalHalfW;

                // Tip is displaced outward and lifted by petalCurvature
                Vector3 tipCenter = center + petalDir * petalLen + axis * (petalLen * petalCurvature);
                Vector3 tL = tipCenter - lateral * petalHalfW * 0.4f;
                Vector3 tR = tipCenter + lateral * petalHalfW * 0.4f;

                // Petal normal: midpoint between axis and petalDir (slightly tilted)
                Vector3 petalNorm = (axis + petalDir * petalCurvature).normalized;

                int pb = verts.Count;
                verts.Add(rL); verts.Add(rR); verts.Add(tR); verts.Add(tL);
                for (int k = 0; k < 4; k++) norms.Add(petalNorm);

                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 1f));

                // Front face
                tris.Add(pb); tris.Add(pb + 1); tris.Add(pb + 2);
                tris.Add(pb); tris.Add(pb + 2); tris.Add(pb + 3);
                // Back face
                tris.Add(pb); tris.Add(pb + 2); tris.Add(pb + 1);
                tris.Add(pb); tris.Add(pb + 3); tris.Add(pb + 2);
            }
        }

        return FinaliseMesh("Flowers", verts, norms, uvs, tris, recalcNormals: false);
    }

    // == Frame utilities =======================================================

    // Compute a stable normal for a given tangent using Gram-Schmidt against
    // global-up (or global-right when the tangent is nearly vertical).
    static Vector3 StableNormal(Vector3 tangent)
    {
        Vector3 reference = Mathf.Abs(tangent.y) < 0.99f ? Vector3.up : Vector3.right;
        Vector3 bitangent = Vector3.Cross(tangent, reference).normalized;
        return Vector3.Cross(bitangent, tangent).normalized;
    }

    // Minimally rotate <paramref name="normal"/> so it is perpendicular to
    // <paramref name="toTangent"/> while staying as close as possible to its
    // current orientation (Bishop / parallel-transport frame).
    static Vector3 ParallelTransport(Vector3 normal, Vector3 fromTangent, Vector3 toTangent)
    {
        Vector3 axis = Vector3.Cross(fromTangent, toTangent);
        float sinA = axis.magnitude;
        float cosA = Vector3.Dot(fromTangent, toTangent);

        if (sinA < 1e-6f) return normal;  // parallel tangents → no rotation needed

        float angleDeg = Mathf.Atan2(sinA, cosA) * Mathf.Rad2Deg;
        return Quaternion.AngleAxis(angleDeg, axis / sinA) * normal;
    }

    // == Mesh helpers =========================================================

    Mesh FinaliseMesh(
        string name,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        bool recalcNormals)
    {
        var mesh = new Mesh { name = name };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);

        if (recalcNormals) mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    static Mesh EmptyMesh(string name) => new Mesh { name = name };

    (GameObject go, MeshFilter mf, MeshRenderer mr) MakeChild(string childName)
    {
        var go = new GameObject(childName);
        go.transform.SetParent(transform, worldPositionStays: false);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        return (go, mf, mr);
    }

    static Material DefaultMaterial(Color color)
    {
        var mat = new Material(Shader.Find("Standard")) { color = color };
        return mat;
    }
}