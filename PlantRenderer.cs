using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// <summary>
/// Main component for procedural plant generation and rendering. 
/// Interfaces with an external C++ library (libPlantSim) to generate L-System nodes, 
/// and handles the construction of dynamic, animated meshes in Unity.
/// </summary>
public class PlantRenderer : MonoBehaviour
{
    /// <summary>
    /// A C-compatible Vector3 struct used for marshalling data to and from the native plugin.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public float x, y, z;
        public static implicit operator Vector3(Vec3 v) => new Vector3(v.x, v.y, v.z);
    }

    /// <summary>Defines the biological category of a generated node.</summary>
    public enum NodeType : int { Branch = 0, Leaf = 1, Flower = 2 }

    /// <summary>
    /// Represents a single segment or element of the plant, mapped directly to the native C++ struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PlantNode
    {
        public Vec3 origin;
        public Vec3 end;
        public Vec3 heading;
        public Vec3 left;
        public float radius;
        public int type;
    }

    /// <summary>
    /// Replaces Tuple to prevent boxing and GC allocation in Dictionaries. 
    /// Used primarily for spatial hashing of coordinates.
    /// </summary>
    public readonly struct Vector3IntKey : IEquatable<Vector3IntKey>
    {
        public readonly int x, y, z;
        public Vector3IntKey(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(Vector3IntKey other) => x == other.x && y == other.y && z == other.z;
        public override int GetHashCode() => (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
    }

    /// <summary>Quantizes a floating-point vector into integer space for fast spatial lookups.</summary>
    static Vector3IntKey Quantize(Vector3 v, float scale = 1000f)
        => new Vector3IntKey((int)(v.x * scale), (int)(v.y * scale), (int)(v.z * scale));

    /// <summary>
    /// Represents the topological tree structure of the branches, built from flat segment data.
    /// Used to calculate continuous normals (preventing mesh twisting) and growth hierarchies.
    /// </summary>
    sealed class BranchGraph
    {
        public struct Segment
        {
            public Vector3 Origin, End;
            public float Radius;
            public int Parent;
            public int[] Children;
            public Vector3 FrameNormal; // The "up" vector of this segment, preventing twisting.
            public int Depth;           // Distance from the root.
            public int TipRingStart;    // Index of the vertex ring in the generated mesh.
        }

        public Segment[] Segments;
        public int[] Roots;
        public int MaxDepth;

        /// <summary>Builds a directed acyclic graph (DAG) from a flat list of unorganized branch nodes.</summary>
        public static BranchGraph Build(List<PlantNode> nodes)
        {
            int n = nodes.Count;
            var segs = new Segment[n];
            var childLists = new List<int>[n];

            // Initialize segments
            for (int i = 0; i < n; i++)
            {
                segs[i] = new Segment
                {
                    Origin = nodes[i].origin,
                    End = nodes[i].end,
                    Radius = Mathf.Max(nodes[i].radius, 0.001f),
                    Parent = -1,
                    Children = Array.Empty<int>(),
                    TipRingStart = -1,
                };
                childLists[i] = new List<int>(2);
            }

            // Map segment end positions to their indices to easily find parents
            var tipIndex = new Dictionary<Vector3IntKey, int>(n);
            for (int i = 0; i < n; i++)
                tipIndex[Quantize(segs[i].End)] = i;

            // Resolve parent-child relationships by matching origins to previous ends
            for (int i = 0; i < n; i++)
            {
                if (!tipIndex.TryGetValue(Quantize(segs[i].Origin), out int pi) || pi == i) continue;
                float tol = segs[i].Radius * 2f;
                // Verify the connection distance is within tolerance
                if ((segs[pi].End - segs[i].Origin).sqrMagnitude < tol * tol)
                {
                    segs[i].Parent = pi;
                    childLists[pi].Add(i);
                }
            }

            for (int i = 0; i < n; i++)
                segs[i].Children = childLists[i].ToArray();

            // Find all root nodes (nodes with no parents)
            var roots = new List<int>(8);
            int maxDepth = 0;
            for (int i = 0; i < n; i++)
                if (segs[i].Parent < 0) roots.Add(i);

            // Breadth-first search to calculate depth and orientation (FrameNormal)
            var queue = new Queue<int>(roots);
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                Vector3 tan = (segs[idx].End - segs[idx].Origin).normalized;

                if (segs[idx].Parent < 0)
                {
                    segs[idx].Depth = 0;
                    segs[idx].FrameNormal = StableNormal(tan);
                }
                else
                {
                    int pi = segs[idx].Parent;
                    Vector3 pTan = (segs[pi].End - segs[pi].Origin).normalized;
                    segs[idx].Depth = segs[pi].Depth + 1;
                    // Use parallel transport to orient this segment relative to its parent smoothly
                    segs[idx].FrameNormal = ParallelTransport(segs[pi].FrameNormal, pTan, tan);
                }

                if (segs[idx].Depth > maxDepth) maxDepth = segs[idx].Depth;
                foreach (int c in segs[idx].Children) queue.Enqueue(c);
            }

            return new BranchGraph { Segments = segs, Roots = roots.ToArray(), MaxDepth = maxDepth };
        }

        /// <summary>Generates an initial stable normal (up vector) given a forward tangent.</summary>
        static Vector3 StableNormal(Vector3 t)
        {
            Vector3 r = Mathf.Abs(t.y) < 0.99f ? Vector3.up : Vector3.right;
            return Vector3.Cross(Vector3.Cross(t, r).normalized, t).normalized;
        }

        /// <summary>Rotates a normal vector from an old tangent direction to a new one, avoiding twists.</summary>
        static Vector3 ParallelTransport(Vector3 n, Vector3 from, Vector3 to)
        {
            Vector3 axis = Vector3.Cross(from, to);
            float sinA = axis.magnitude, cosA = Vector3.Dot(from, to);
            if (sinA < 1e-6f) return n; // Tangents are parallel
            return Quaternion.AngleAxis(Mathf.Atan2(sinA, cosA) * Mathf.Rad2Deg, axis / sinA) * n;
        }
    }

    // Configuration Enums
    public enum TreeType { ParametricTree, StochasticShrub, HybridPlant, ABOPTree, CustomBloomingTree }
    public enum LeafShape { Teardrop, Oval, Compound, Needle }
    public enum FlowerShape { FivePetal, Daisy, TulipCup, StarBurst }

    // Maps TreeType profiles to specific leaf and flower shapes
    static readonly Dictionary<TreeType, (LeafShape leaf, FlowerShape flower)> TypeShapes = new()
    {
        [TreeType.ParametricTree] = (LeafShape.Teardrop, FlowerShape.FivePetal),
        [TreeType.StochasticShrub] = (LeafShape.Oval, FlowerShape.Daisy),
        [TreeType.HybridPlant] = (LeafShape.Compound, FlowerShape.TulipCup),
        [TreeType.ABOPTree] = (LeafShape.Needle, FlowerShape.StarBurst),
        [TreeType.CustomBloomingTree] = (LeafShape.Compound, FlowerShape.FivePetal),
    };

    /// <summary>Settings to dictate mesh resolution at various camera distances.</summary>
    [Serializable]
    public struct LodSettings
    {
        public float maxDistance;
        public int branchRadialSegments; // How "round" the branches are
        public int leafDetail;           // Polygons per leaf
    }

    /// <summary>Stores timing data for a specific node during a growth animation.</summary>
    struct NodeAnim { public float StartT, EndT; public bool IsNew; }

    [Header("L-System")]
    public TreeType treeType = TreeType.ABOPTree;
    public int iterations = 4;
    public uint seed = 67;

    [Header("Branch Geometry")]
    public int radialSegments = 8;
    public Material branchMaterial;

    [Header("Leaf Geometry")]
    public float leafScale = 1.0f;
    public Material leafMaterial;

    [Header("Flower Geometry")]
    public int petalCount = 5;
    public float flowerScale = 1.0f;
    [Range(0f, 1f)] public float petalCurvature = 0.3f;
    public Material flowerMaterial;

    [Header("LOD")]
    public LodSettings[] lodSettings = new[]
    {
        new LodSettings { maxDistance = 10f, branchRadialSegments = 10, leafDetail = 1 },
        new LodSettings { maxDistance = 30f, branchRadialSegments = 6,  leafDetail = 1 },
        new LodSettings { maxDistance = 60f, branchRadialSegments = 4,  leafDetail = 2 },
        new LodSettings { maxDistance = 999f, branchRadialSegments = 3, leafDetail = 4 },
    };
    public float lodCheckInterval = 0.3f;

    [Header("Growth Animation")]
    public float growthDuration = 1.6f;
    [Range(0, 2)] public int growthEasing = 1;

    [Header("Continuous Mode")]
    public bool continuousMode = false;
    public float stepInterval = 3.0f;
    public int maxIterations = 8;
    
    // Memory Pre-allocations to prevent GC spikes during generation
    const int MAX_NODES = 200_000;
    readonly PlantNode[] _nodeBuffer = new PlantNode[MAX_NODES];

    List<PlantNode> _curBranches = new();
    List<PlantNode> _curLeaves = new();
    List<PlantNode> _curFlowers = new();
    BranchGraph _cachedGraph;

    readonly HashSet<ulong> _knownHashes = new();
    readonly Dictionary<ulong, NodeAnim> _timeline = new();
    float _animProgress;
    Coroutine _growthCoroutine;

    float _continuousTimer;
    bool _continuousTimerArmed;
    int _activeLod;
    float _lodTimer;
    int _currentRadialSegs;

    // Cached Mesh Buffers to prevent per-frame array allocations
    readonly List<Vector3> _verts = new(65536);
    readonly List<Vector3> _norms = new(65536);
    readonly List<Vector2> _uvs = new(65536);
    readonly List<int> _tris = new(65536 * 3);

    /* Optimizing by splitting the meshes into Stable (Old growth) and Growing (New growth).
     * This prevents having to push hundreds of thousands of vertices to the GPU every frame 
     * during a growth animation; only the new/growing segments update dynamically.
     */
    Mesh _stableBranchMesh, _growingBranchMesh;
    Mesh _stableLeafMesh, _growingLeafMesh;
    Mesh _stableFlowerMesh, _growingFlowerMesh;

    GameObject _stableBranchGO, _growingBranchGO;
    GameObject _stableLeafGO, _growingLeafGO;
    GameObject _stableFlowerGO, _growingFlowerGO;

    /// <summary>External call to the native C++ library handling the mathematical L-System generation.</summary>
    [DllImport("libPlantSim", CallingConvention = CallingConvention.Cdecl)]
    static extern int GeneratePlant(int exampleId, int iterations, [Out] PlantNode[] outNodes, int maxNodes, uint seed);

    void Awake()
    {
        // Initialize persistent meshes once at startup to avoid instantiation overhead
        _stableBranchMesh = CreateMesh("Branches-Stable", false);
        _growingBranchMesh = CreateMesh("Branches-Growing", true);
        _stableLeafMesh = CreateMesh("Leaves-Stable", false);
        _growingLeafMesh = CreateMesh("Leaves-Growing", true);
        _stableFlowerMesh = CreateMesh("Flowers-Stable", false);
        _growingFlowerMesh = CreateMesh("Flowers-Growing", true);

        // Bind meshes to visible GameObjects
        _stableBranchGO = MakeChild("Branches-Stable", _stableBranchMesh, branchMaterial);
        _growingBranchGO = MakeChild("Branches-Growing", _growingBranchMesh, branchMaterial);
        _stableLeafGO = MakeChild("Leaves-Stable", _stableLeafMesh, leafMaterial);
        _growingLeafGO = MakeChild("Leaves-Growing", _growingLeafMesh, leafMaterial);
        _stableFlowerGO = MakeChild("Flowers-Stable", _stableFlowerMesh, flowerMaterial);
        _growingFlowerGO = MakeChild("Flowers-Growing", _growingFlowerMesh, flowerMaterial);

        _currentRadialSegs = radialSegments;
    }

    Mesh CreateMesh(string meshName, bool isDynamic)
    {
        var m = new Mesh { name = meshName, indexFormat = IndexFormat.UInt32 };
        if (isDynamic) m.MarkDynamic(); // Optimizes mesh for frequent GPU uploads (VBO changes)
        return m;
    }

    GameObject MakeChild(string childName, Mesh sharedMesh, Material mat)
    {
        var go = new GameObject(childName);
        go.transform.SetParent(transform, worldPositionStays: false);
        go.AddComponent<MeshFilter>().sharedMesh = sharedMesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat ?? DefaultMaterial(Color.white);
        return go;
    }

    static Material DefaultMaterial(Color color) => new Material(Shader.Find("Standard")) { color = color };

    void Start() => Generate(animate: false);

    void Update()
    {
        HandleInput();
        UpdateLod();
        UpdateContinuousMode();
    }

    void HandleInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        
        // Debugging inputs to increment iterations, randomize seed, and swap plant type
        if (kb.spaceKey.wasPressedThisFrame) { iterations++; Generate(animate: true); }
        if (kb.rKey.wasPressedThisFrame) { seed = (uint)UnityEngine.Random.Range(1, int.MaxValue); Generate(animate: true); }
        if (kb.digit1Key.wasPressedThisFrame) { treeType = TreeType.ParametricTree; Generate(animate: false); }
    }

    /// <summary>Handles automatic incremental growth over time when Continuous Mode is enabled.</summary>
    void UpdateContinuousMode()
    {
        if (!continuousMode || !_continuousTimerArmed || _growthCoroutine != null) return;
        _continuousTimer -= Time.deltaTime;
        if (_continuousTimer > 0f) return;
        
        _continuousTimerArmed = false;
        if (iterations >= maxIterations) return;
        iterations++;
        Generate(animate: true);
    }

    void ArmContinuousTimer()
    {
        if (!continuousMode || iterations >= maxIterations) return;
        _continuousTimer = stepInterval;
        _continuousTimerArmed = true;
    }

    /// <summary>Checks distance from the camera to swap the level of detail (LOD) geometry.</summary>
    void UpdateLod()
    {
        _lodTimer -= Time.deltaTime;
        if (_lodTimer > 0f || _growthCoroutine != null) return;
        _lodTimer = lodCheckInterval;

        Camera cam = Camera.main;
        if (cam == null || lodSettings == null || lodSettings.Length == 0) return;

        float dist = Vector3.Distance(cam.transform.position, transform.position);
        int newLod = lodSettings.Length - 1;
        
        // Determine correct LOD tier
        for (int i = 0; i < lodSettings.Length; i++)
            if (dist <= lodSettings[i].maxDistance) { newLod = i; break; }

        if (newLod == _activeLod) return;
        _activeLod = newLod;
        _currentRadialSegs = lodSettings[_activeLod].branchRadialSegments;
        
        RebuildAllFull(); // Force a rebuild if the LOD tier changes
    }

    /// <summary>Main entry point for calculating the plant logic and rebuilding the meshes.</summary>
    void Generate(bool animate)
    {
        if (_growthCoroutine != null)
        {
            StopCoroutine(_growthCoroutine);
            _growthCoroutine = null;
            _continuousTimerArmed = false;
        }

        // 1. Fetch unorganized node data from C++ plugin
        int count = GeneratePlant((int)treeType, iterations, _nodeBuffer, MAX_NODES, seed);
        if (count <= 0) return;
        count = Mathf.Min(count, MAX_NODES);

        _curBranches.Clear();
        _curLeaves.Clear();
        _curFlowers.Clear();

        // 2. Sort nodes by type
        for (int i = 0; i < count; i++)
            switch ((NodeType)_nodeBuffer[i].type)
            {
                case NodeType.Branch: _curBranches.Add(_nodeBuffer[i]); break;
                case NodeType.Leaf: _curLeaves.Add(_nodeBuffer[i]); break;
                case NodeType.Flower: _curFlowers.Add(_nodeBuffer[i]); break;
            }

        // 3. Build topologic relationships between branches
        _cachedGraph = BranchGraph.Build(_curBranches);

        if (animate)
        {
            // 4. Calculate timing for nodes if animating
            ComputeTimelines();
            var (leafShape, flowerShape) = TypeShapes[treeType];
            int leafDetail = ActiveLeafDetail();

            // Populate stable meshes with ONLY old nodes to keep them off the dynamic update loop
            BuildBranchMesh(BuildMode.Stable, _stableBranchMesh);
            BuildLeafMesh(_curLeaves, leafShape, leafDetail, true, FilterMode.OnlyOld, _stableLeafMesh);
            BuildFlowerMesh(_curFlowers, flowerShape, true, FilterMode.OnlyOld, _stableFlowerMesh);

            RefreshKnownHashes();
            _animProgress = 0f;
            _growthCoroutine = StartCoroutine(GrowthAnimation());
        }
        else
        {
            // If instantly generating, rebuild everything completely and skip animations
            _timeline.Clear();
            RefreshKnownHashes();
            RebuildAllFull();
            ArmContinuousTimer();
        }
    }

    /// <summary>Records the current layout to differentiate "old" structure from "new" structure later.</summary>
    void RefreshKnownHashes()
    {
        _knownHashes.Clear();
        foreach (var n in _curBranches) _knownHashes.Add(NodeHash(n));
        foreach (var n in _curLeaves) _knownHashes.Add(NodeHash(n));
        foreach (var n in _curFlowers) _knownHashes.Add(NodeHash(n));
    }

    /// <summary>
    /// Calculates exactly when each node should start and stop growing based on its depth in the tree.
    /// Simulates natural propagation from root to tips.
    /// </summary>
    void ComputeTimelines()
    {
        _timeline.Clear();
        int D = Mathf.Max(1, _cachedGraph.MaxDepth);
        const float FOLIAGE_WINDOW = 0.18f; // Leaves and flowers grow in the last 18% of the animation

        var tipEndT = new Dictionary<Vector3IntKey, float>(_curBranches.Count);
        float branchWindow = (1f - FOLIAGE_WINDOW) / (D + 1);
        var bfsOrder = new List<int>(_cachedGraph.Segments.Length);

        // Map order from root out to the extremities
        var bfsQ = new Queue<int>(_cachedGraph.Roots);
        while (bfsQ.Count > 0)
        {
            int bi = bfsQ.Dequeue();
            bfsOrder.Add(bi);
            foreach (int c in _cachedGraph.Segments[bi].Children) bfsQ.Enqueue(c);
        }

        var segEndT = new float[_cachedGraph.Segments.Length];

        // Process branches
        foreach (int i in bfsOrder)
        {
            ulong h = NodeHash(_curBranches[i]);
            bool old = _knownHashes.Contains(h);
            NodeAnim a;

            if (old)
            {
                // Already grown, keep static
                a = new NodeAnim { StartT = 0f, EndT = 0f, IsNew = false };
                segEndT[i] = 0f;
            }
            else
            {
                // Calculate growth window based on parent's end time
                int pi = _cachedGraph.Segments[i].Parent;
                float startT = (pi < 0) ? 0f : segEndT[pi];
                float endT = Mathf.Min(1f - FOLIAGE_WINDOW, startT + branchWindow);
                a = new NodeAnim { StartT = startT, EndT = endT, IsNew = true };
                segEndT[i] = endT;
            }

            _timeline[h] = a;
            var key = Quantize(_curBranches[i].end);
            
            if (!tipEndT.TryGetValue(key, out float prev) || segEndT[i] > prev)
                tipEndT[key] = segEndT[i];
        }

        // Process foliage (Starts after its parent branch is fully grown)
        void AssignFoliage(List<PlantNode> nodes)
        {
            foreach (var node in nodes)
            {
                ulong h = NodeHash(node);
                if (_knownHashes.Contains(h))
                {
                    _timeline[h] = new NodeAnim { StartT = 0f, EndT = 0f, IsNew = false };
                    continue;
                }

                var key = Quantize(node.origin);
                if (!tipEndT.TryGetValue(key, out float parentEndT))
                    parentEndT = 1f - FOLIAGE_WINDOW;

                _timeline[h] = new NodeAnim
                {
                    StartT = parentEndT,
                    EndT = Mathf.Min(1f, parentEndT + FOLIAGE_WINDOW),
                    IsNew = true,
                };
            }
        }

        AssignFoliage(_curLeaves);
        AssignFoliage(_curFlowers);
    }

    /// <summary>Coroutine handling the frame-by-frame mesh update of "new" segments.</summary>
    IEnumerator GrowthAnimation()
    {
        var (leafShape, flowerShape) = TypeShapes[treeType];
        int leafDetail = ActiveLeafDetail();
        float elapsed = 0f;

        while (elapsed < growthDuration)
        {
            elapsed += Time.deltaTime;
            _animProgress = Mathf.Clamp01(elapsed / growthDuration);

            // Rebuild growing meshes inside existing dynamic mesh objects
            BuildBranchMesh(BuildMode.Growing, _growingBranchMesh);
            BuildLeafMesh(_curLeaves, leafShape, leafDetail, false, FilterMode.OnlyNew, _growingLeafMesh);
            BuildFlowerMesh(_curFlowers, flowerShape, false, FilterMode.OnlyNew, _growingFlowerMesh);

            yield return null;
        }

        // Once complete, merge everything back into the static mesh 
        _animProgress = 1f;
        _growthCoroutine = null;
        RebuildAllFull();
        ArmContinuousTimer();
    }

    /// <summary>Forces a complete redraw of the stable mesh, skipping the animation phase.</summary>
    void RebuildAllFull()
    {
        var (leafShape, flowerShape) = TypeShapes[treeType];
        int leafDetail = ActiveLeafDetail();

        BuildBranchMesh(BuildMode.Full, _stableBranchMesh);
        BuildLeafMesh(_curLeaves, leafShape, leafDetail, true, FilterMode.All, _stableLeafMesh);
        BuildFlowerMesh(_curFlowers, flowerShape, true, FilterMode.All, _stableFlowerMesh);

        _growingBranchMesh.Clear();
        _growingLeafMesh.Clear();
        _growingFlowerMesh.Clear();
    }

    /// <summary>Gets the 0 to 1 normalized growth state of a node based on current animation time.</summary>
    float GetGrowth(in PlantNode node, bool foliage = false)
    {
        ulong h = NodeHash(node);
        if (!_timeline.TryGetValue(h, out NodeAnim a) || !a.IsNew) return 1f;
        if (_animProgress <= a.StartT) return 0f;
        if (_animProgress >= a.EndT) return 1f;
        float t = (_animProgress - a.StartT) / (a.EndT - a.StartT);
        return foliage ? EaseSpring(t) : Ease(t);
    }

    // Easing functions for organic motion
    float Ease(float t) => growthEasing switch { 0 => t, 1 => t * t * (3f - 2f * t), 2 => 1f - Mathf.Exp(-6f * t) * Mathf.Cos(11f * t), _ => t };
    static float EaseSpring(float t) => 1f - Mathf.Exp(-7f * t) * Mathf.Cos(10f * t);

    public enum BuildMode { Stable, Growing, Full }
    public enum FilterMode { All, OnlyOld, OnlyNew }

    /// <summary>Procedurally generates the cylindrical meshes representing the branches.</summary>
    void BuildBranchMesh(BuildMode mode, Mesh targetMesh)
    {
        _verts.Clear(); _norms.Clear(); _uvs.Clear(); _tris.Clear();

        if (_curBranches.Count == 0 || _cachedGraph == null)
        {
            targetMesh.Clear();
            return;
        }

        int R = _currentRadialSegs;
        int S = _cachedGraph.Segments.Length;

        var tipRingStart = new int[S];
        for (int i = 0; i < S; i++) tipRingStart[i] = -1;

        var tipPositions = new Vector3[S];
        var tipTangents = new Vector3[S];

        var queue = new Queue<int>(_cachedGraph.Roots);
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            ref var seg = ref _cachedGraph.Segments[idx];
            foreach (int c in seg.Children) queue.Enqueue(c);

            Vector3 dir = seg.End - seg.Origin;
            float segLen = dir.magnitude;
            if (segLen < 1e-5f) continue;

            ulong h = NodeHash(_curBranches[idx]);
            bool isNew = _timeline.TryGetValue(h, out var ta) && ta.IsNew;

            bool render = mode == BuildMode.Full || (mode == BuildMode.Stable && !isNew) || (mode == BuildMode.Growing && isNew);
            if (!render) continue;

            float growT = (mode == BuildMode.Full || !isNew) ? 1f : GetGrowth(_curBranches[idx]);
            if (growT < 0.001f) continue;

            Vector3 tangent = dir / segLen;
            Vector3 frameN = seg.FrameNormal;
            Vector3 frameB = Vector3.Cross(tangent, frameN).normalized;
            float r = seg.Radius;

            // Connect this segment's base to the parent's tip to prevent gaps
            int baseStart = (seg.Parent >= 0 && tipRingStart[seg.Parent] >= 0) ? tipRingStart[seg.Parent] : _verts.Count;
            if (baseStart == _verts.Count) EmitRing(seg.Origin, r, frameN, frameB, 0f, R); // No parent; generate new base

            Vector3 tipPos = seg.Origin + tangent * (segLen * growT);
            float tipR = r * Mathf.Lerp(0.05f, 1f, growT); // Tapering slightly at the tip

            tipRingStart[idx] = _verts.Count;
            EmitRing(tipPos, tipR, frameN, frameB, 1f, R);

            tipPositions[idx] = tipPos;
            tipTangents[idx] = tangent;

            // Stitch the base ring and tip ring together with triangles
            for (int s = 0; s < R; s++)
            {
                int sn = (s + 1) % R;
                int b0 = baseStart + s, b1 = baseStart + sn;
                int t0 = tipRingStart[idx] + s, t1 = tipRingStart[idx] + sn;
                
                // Two triangles per quad segment
                _tris.Add(b0); _tris.Add(b1); _tris.Add(t0);
                _tris.Add(t0); _tris.Add(b1); _tris.Add(t1);
            }
        }

        // Cap off the ends of branches that have no children
        for (int idx = 0; idx < S; idx++)
        {
            if (tipRingStart[idx] < 0) continue;
            bool anyChildRendered = false;
            foreach (int c in _cachedGraph.Segments[idx].Children)
                if (tipRingStart[c] >= 0) { anyChildRendered = true; break; }
            if (anyChildRendered) continue; // Not an end branch

            int capIdx = _verts.Count;
            _verts.Add(tipPositions[idx]); _norms.Add(tipTangents[idx]); _uvs.Add(new Vector2(0.5f, 0.5f));
            for (int s = 0; s < R; s++)
            {
                _tris.Add(capIdx);
                _tris.Add(tipRingStart[idx] + (s + 1) % R);
                _tris.Add(tipRingStart[idx] + s);
            }
        }

        FlushToMesh(targetMesh);
    }

    /// <summary>Generates geometry for foliage depending on current tree type settings.</summary>
    void BuildLeafMesh(List<PlantNode> nodes, LeafShape shape, int lodDetail, bool fullyGrown, FilterMode filter, Mesh targetMesh)
    {
        _verts.Clear(); _norms.Clear(); _uvs.Clear(); _tris.Clear();

        foreach (var node in nodes)
        {
            ulong h = NodeHash(node);
            bool isNew = _timeline.TryGetValue(h, out var a) && a.IsNew;

            if (filter == FilterMode.OnlyOld && isNew) continue;
            if (filter == FilterMode.OnlyNew && !isNew) continue;

            float growT = fullyGrown ? 1f : GetGrowth(node, foliage: true);
            if (growT < 0.01f) continue;

            Vector3 pos = node.origin;
            Vector3 fwd = ((Vector3)node.heading).normalized;
            Vector3 right = ((Vector3)node.left).normalized;
            Vector3 faceN = Vector3.Cross(fwd, right).normalized;
            float size = node.radius * leafScale * growT;

            switch (shape)
            {
                case LeafShape.Teardrop: EmitTeardrop(pos, fwd, right, faceN, size); break;
                case LeafShape.Oval: EmitOval(pos, fwd, right, faceN, size, lodDetail); break;
                case LeafShape.Compound: EmitCompound(pos, fwd, right, faceN, size); break;
                case LeafShape.Needle: EmitNeedle(pos, fwd, right, faceN, size); break;
            }
        }

        FlushToMesh(targetMesh);
    }

    /// <summary>Generates geometry for blossoms depending on current tree type settings.</summary>
    void BuildFlowerMesh(List<PlantNode> nodes, FlowerShape shape, bool fullyGrown, FilterMode filter, Mesh targetMesh)
    {
        _verts.Clear(); _norms.Clear(); _uvs.Clear(); _tris.Clear();

        foreach (var node in nodes)
        {
            ulong h = NodeHash(node);
            bool isNew = _timeline.TryGetValue(h, out var a) && a.IsNew;

            if (filter == FilterMode.OnlyOld && isNew) continue;
            if (filter == FilterMode.OnlyNew && !isNew) continue;

            float growT = fullyGrown ? 1f : GetGrowth(node, foliage: true);
            if (growT < 0.01f) continue;

            Vector3 center = node.origin;
            Vector3 axis = ((Vector3)node.heading).normalized;
            Vector3 right = ((Vector3)node.left).normalized;
            Vector3 up2 = Vector3.Cross(axis, right).normalized;
            float size = node.radius * flowerScale * growT;

            switch (shape)
            {
                case FlowerShape.FivePetal: EmitRadialFlower(center, axis, right, up2, size, 5, 0.25f, 0.28f, petalCurvature); break;
                case FlowerShape.Daisy: EmitRadialFlower(center, axis, right, up2, size, 12, 0.18f, 0.14f, 0.06f); break;
                case FlowerShape.TulipCup: EmitTulip(center, axis, right, up2, size); break;
                case FlowerShape.StarBurst: EmitStar(center, axis, right, up2, size); break;
            }
        }

        FlushToMesh(targetMesh);
    }

    // --- Mesh Emission Helpers Below --- //

    /// <summary>Emits a circle of vertices to represent a slice of a branch.</summary>
    void EmitRing(Vector3 center, float radius, Vector3 frameN, Vector3 frameB, float vCoord, int R)
    {
        for (int s = 0; s < R; s++)
        {
            float theta = s / (float)R * Mathf.PI * 2f;
            Vector3 outDir = Mathf.Cos(theta) * frameN + Mathf.Sin(theta) * frameB;
            _verts.Add(center + outDir * radius);
            _norms.Add(outDir);
            _uvs.Add(new Vector2((float)s / R, vCoord)); // Map UV horizontally along the texture
        }
    }

    void EmitTeardrop(Vector3 pos, Vector3 fwd, Vector3 right, Vector3 n, float size)
    {
        float hw = size * 0.35f;
        Vector3 p0 = pos, p1 = pos + right * hw + fwd * size * 0.40f, p2 = pos + fwd * size, p3 = pos - right * hw + fwd * size * 0.40f;
        EmitDoubleSidedQuad(p0, p1, p2, p3, n);
    }

    void EmitOval(Vector3 pos, Vector3 fwd, Vector3 right, Vector3 n, float size, int lodDetail)
    {
        int segs = Mathf.Max(4, 12 / lodDetail);
        Vector3 center = pos + fwd * size * 0.5f;

        int cf = _verts.Count; _verts.Add(center); _norms.Add(n); _uvs.Add(new Vector2(0.5f, 0.5f));
        int cb = _verts.Count; _verts.Add(center); _norms.Add(-n); _uvs.Add(new Vector2(0.5f, 0.5f));
        int rimBase = _verts.Count;

        for (int s = 0; s <= segs; s++)
        {
            float a = s / (float)segs * Mathf.PI * 2f;
            float c = Mathf.Cos(a), si = Mathf.Sin(a);
            Vector3 rim = pos + fwd * (size * 0.5f + size * 0.5f * si) + right * (size * 0.38f * c);
            _verts.Add(rim); _norms.Add(n); _uvs.Add(new Vector2(0.5f + 0.5f * c, 0.5f + 0.5f * si));
            _verts.Add(rim); _norms.Add(-n); _uvs.Add(new Vector2(0.5f + 0.5f * c, 0.5f + 0.5f * si));
        }

        for (int s = 0; s < segs; s++)
        {
            int f0 = rimBase + s * 2, f1 = rimBase + (s + 1) * 2;
            _tris.Add(cf); _tris.Add(f0); _tris.Add(f1);
            _tris.Add(cb); _tris.Add(f1 + 1); _tris.Add(f0 + 1);
        }
    }

    void EmitCompound(Vector3 pos, Vector3 fwd, Vector3 right, Vector3 n, float size)
    {
        float leafLen = size * 0.32f;
        for (int i = 0; i < 3; i++)
        {
            float t = (i + 1f) / 4f;
            Vector3 pivot = pos + fwd * size * t;
            float ls = leafLen * (1f - t * 0.3f);
            EmitTeardrop(pivot, (fwd + right * 0.65f).normalized, right, n, ls);
            EmitTeardrop(pivot, (fwd - right * 0.65f).normalized, -right, -n, ls);
        }
        EmitTeardrop(pos + fwd * size * 0.85f, fwd, right, n, size * 0.28f);
    }

    void EmitNeedle(Vector3 pos, Vector3 fwd, Vector3 right, Vector3 n, float size)
    {
        float hw = size * 0.055f;
        EmitDoubleSidedQuad(pos, pos + right * hw, pos + fwd * size, pos - right * hw, n);
    }

    /// <summary>Helper method to ensure foliage can be seen from both sides without writing a custom shader.</summary>
    void EmitDoubleSidedQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 n)
    {
        int b = _verts.Count;
        _verts.Add(p0); _verts.Add(p1); _verts.Add(p2); _verts.Add(p3);
        for (int k = 0; k < 4; k++) _norms.Add(n);
        _uvs.Add(new Vector2(0.5f, 0f)); _uvs.Add(new Vector2(1f, 0.4f)); _uvs.Add(new Vector2(0.5f, 1f)); _uvs.Add(new Vector2(0f, 0.4f));
        _tris.Add(b); _tris.Add(b + 1); _tris.Add(b + 2); _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 3);

        b = _verts.Count; // Flip normals and vertex wind order for backface
        _verts.Add(p0); _verts.Add(p1); _verts.Add(p2); _verts.Add(p3);
        for (int k = 0; k < 4; k++) _norms.Add(-n);
        _uvs.Add(new Vector2(0.5f, 0f)); _uvs.Add(new Vector2(1f, 0.4f)); _uvs.Add(new Vector2(0.5f, 1f)); _uvs.Add(new Vector2(0f, 0.4f));
        _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 1); _tris.Add(b); _tris.Add(b + 3); _tris.Add(b + 2);
    }

    void EmitRadialFlower(Vector3 center, Vector3 axis, Vector3 right, Vector3 up2, float size, int petals, float discFrac, float hwFrac, float curvature)
    {
        float discR = size * discFrac, petalLen = size, hw = size * hwFrac;
        int db = _verts.Count;
        _verts.Add(center); _norms.Add(axis); _uvs.Add(new Vector2(0.5f, 0.5f));
        for (int p = 0; p < petals; p++)
        {
            float a = p / (float)petals * Mathf.PI * 2f;
            Vector3 d = Mathf.Cos(a) * right + Mathf.Sin(a) * up2;
            _verts.Add(center + d * discR); _norms.Add(axis); _uvs.Add(new Vector2(0.5f + 0.25f * Mathf.Cos(a), 0.5f + 0.25f * Mathf.Sin(a)));
        }
        for (int p = 0; p < petals; p++) { _tris.Add(db); _tris.Add(db + 1 + p); _tris.Add(db + 1 + (p + 1) % petals); }

        for (int p = 0; p < petals; p++)
        {
            float a = p / (float)petals * Mathf.PI * 2f;
            Vector3 pd = (Mathf.Cos(a) * right + Mathf.Sin(a) * up2).normalized;
            Vector3 lat = Vector3.Cross(pd, axis).normalized;
            Vector3 rC = center + pd * discR;
            Vector3 tC = center + pd * petalLen + axis * (petalLen * curvature); // Push tip upward based on curvature
            Vector3 pn = (axis + pd * curvature).normalized;

            int pb = _verts.Count;
            _verts.Add(rC - lat * hw); _verts.Add(rC + lat * hw); _verts.Add(tC + lat * lat * hw * 0.35f); _verts.Add(tC - lat * hw * 0.35f);
            for (int k = 0; k < 4; k++) _norms.Add(pn);
            _uvs.Add(Vector2.zero); _uvs.Add(new Vector2(1, 0)); _uvs.Add(Vector2.one); _uvs.Add(new Vector2(0, 1));
            
            // Double-sided quad layout
            _tris.Add(pb); _tris.Add(pb + 1); _tris.Add(pb + 2); _tris.Add(pb); _tris.Add(pb + 2); _tris.Add(pb + 3);
            _tris.Add(pb); _tris.Add(pb + 2); _tris.Add(pb + 1); _tris.Add(pb); _tris.Add(pb + 3); _tris.Add(pb + 2);
        }
    }

    void EmitTulip(Vector3 center, Vector3 axis, Vector3 right, Vector3 up2, float size)
    {
        float discR = size * 0.18f, pLen = size, pW = size * 0.32f;
        int db = _verts.Count;
        _verts.Add(center); _norms.Add(axis); _uvs.Add(new Vector2(0.5f, 0.5f));
        for (int p = 0; p < 6; p++)
        {
            float a = p / 6f * Mathf.PI * 2f;
            _verts.Add(center + (Mathf.Cos(a) * right + Mathf.Sin(a) * up2) * discR);
            _norms.Add(axis); _uvs.Add(new Vector2(0.5f, 0.5f));
        }
        for (int p = 0; p < 6; p++) { _tris.Add(db); _tris.Add(db + 1 + p); _tris.Add(db + 1 + (p + 1) % 6); }

        for (int p = 0; p < 6; p++)
        {
            float a = p / 6f * Mathf.PI * 2f;
            Vector3 pd = (Mathf.Cos(a) * right + Mathf.Sin(a) * up2).normalized;
            Vector3 lat = Vector3.Cross(pd, axis).normalized;
            int vBase = _verts.Count;

            // Builds a multi-segmented curved petal 
            for (int ri = 0; ri <= 3; ri++)
            {
                float t = ri / 3f;
                float curl = Mathf.Sin(t * Mathf.PI) * 0.35f;
                Vector3 rc = center + pd * (discR + pLen * t) + axis * (pLen * t * (0.55f - curl));
                float w = pW * (1f - t * 0.28f);
                Vector3 n2 = (axis + pd * (1f - t * 0.7f)).normalized;
                _verts.Add(rc - lat * w); _norms.Add(n2); _uvs.Add(new Vector2(0f, t));
                _verts.Add(rc + lat * w); _norms.Add(n2); _uvs.Add(new Vector2(1f, t));
            }

            for (int ri = 0; ri < 3; ri++)
            {
                int i0 = vBase + ri * 2;
                _tris.Add(i0); _tris.Add(i0 + 1); _tris.Add(i0 + 2); _tris.Add(i0 + 1); _tris.Add(i0 + 3); _tris.Add(i0 + 2);
                _tris.Add(i0); _tris.Add(i0 + 2); _tris.Add(i0 + 1); _tris.Add(i0 + 1); _tris.Add(i0 + 2); _tris.Add(i0 + 3);
            }
        }
    }

    void EmitStar(Vector3 center, Vector3 axis, Vector3 right, Vector3 up2, float size)
    {
        float discR = size * 0.10f, pLen = size * 1.25f, pW = size * 0.09f;
        int db = _verts.Count;
        _verts.Add(center); _norms.Add(axis); _uvs.Add(new Vector2(0.5f, 0.5f));
        for (int p = 0; p < 8; p++)
        {
            float a = p / 8f * Mathf.PI * 2f;
            _verts.Add(center + (Mathf.Cos(a) * right + Mathf.Sin(a) * up2) * discR);
            _norms.Add(axis); _uvs.Add(new Vector2(0.5f + 0.25f * Mathf.Cos(a), 0.5f + 0.25f * Mathf.Sin(a)));
        }
        for (int p = 0; p < 8; p++) { _tris.Add(db); _tris.Add(db + 1 + p); _tris.Add(db + 1 + (p + 1) % 8); }

        for (int p = 0; p < 8; p++)
        {
            float a = p / 8f * Mathf.PI * 2f;
            Vector3 pd = (Mathf.Cos(a) * right + Mathf.Sin(a) * up2).normalized;
            Vector3 lat = Vector3.Cross(pd, axis).normalized;
            int pb = _verts.Count;
            _verts.Add(center + pd * discR - lat * pW); _norms.Add(axis); _uvs.Add(new Vector2(0f, 0f));
            _verts.Add(center + pd * discR + lat * pW); _norms.Add(axis); _uvs.Add(new Vector2(1f, 0f));
            _verts.Add(center + pd * pLen); _norms.Add(axis); _uvs.Add(new Vector2(0.5f, 1f));
            _tris.Add(pb); _tris.Add(pb + 1); _tris.Add(pb + 2); _tris.Add(pb); _tris.Add(pb + 2); _tris.Add(pb + 1);
        }
    }

    /// <summary>Commits the populated geometry lists to the actual Unity Mesh object.</summary>
    void FlushToMesh(Mesh mesh)
    {
        mesh.Clear(keepVertexLayout: true);
        mesh.SetVertices(_verts);
        mesh.SetNormals(_norms);
        mesh.SetUVs(0, _uvs);
        mesh.SetTriangles(_tris, 0);
        mesh.RecalculateBounds();
    }

    /// <summary>Computes a deterministic hash ID for a node based on its spatial coordinates.</summary>
    static ulong NodeHash(in PlantNode n)
    {
        const float Q = 100f; // Precision scale for floating point coords
        ulong h = (ulong)(uint)(int)(n.origin.x * Q);
        h = h * 2654435761ul ^ (ulong)(uint)(int)(n.origin.y * Q);
        h = h * 2654435761ul ^ (ulong)(uint)(int)(n.origin.z * Q);
        h = h * 2654435761ul ^ (ulong)(uint)(int)(n.end.x * Q);
        h = h * 2654435761ul ^ (ulong)(uint)(int)(n.end.y * Q);
        h = h * 2654435761ul ^ (ulong)(uint)(int)(n.end.z * Q);
        return h;
    }

    int ActiveLeafDetail() => (lodSettings != null && _activeLod < lodSettings.Length) ? lodSettings[_activeLod].leafDetail : 1;
}