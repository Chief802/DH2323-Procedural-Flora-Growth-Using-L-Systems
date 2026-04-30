// =
//  PlantRenderer.cs  (revision 3 – final integrated)
//
//  Unity bridge for the libPlantSim native DLL.
//
//  Architecture
//  
//  Six child GameObjects, one pair per element type:
//    Branches-Stable / Branches-Growing
//    Leaves-Stable   / Leaves-Growing
//    Flowers-Stable  / Flowers-Growing
//
//  The Stable mesh is built once per Generate() call and never
//  modified during animation.  The Growing mesh is rebuilt every
//  frame for the subset of nodes that are still animating, then
//  collapsed back into Stable when the animation completes.
//
//  BRANCH GRAPH
//    Flat PlantNode[] is parsed into a directed acyclic graph via
//    spatial hashing of tip positions.  Bishop (parallel-transport)
//    frames and per-segment depth values are propagated in a single
//    BFS pass.  BuildMode (Stable / Growing / Full) controls which
//    segments are emitted in each mesh.  When a new child segment's
//    parent is in the Stable partition, a fresh base ring is emitted
//    at the parent's exact tip world position so the two meshes
//    meet seamlessly without sharing vertex indices.
//
//  FOLIAGE PER TREE-TYPE
//    Each TreeType maps to a (LeafShape, FlowerShape) pair:
//      ParametricTree     → Teardrop   / FivePetal
//      StochasticShrub    → Oval       / Daisy
//      HybridPlant        → Compound   / TulipCup
//      ABOPTree           → Needle     / StarBurst
//      CustomBloomingTree → Compound   / FivePetal
//
//  DISTANCE LOD
//    Camera distance re-evaluated every lodCheckInterval seconds.
//    Changes branchRadialSegments and leaf tessellation without
//    re-invoking the native library.  LOD changes are blocked while
//    an animation is in flight to avoid corrupting the Stable/Growing
//    mesh split.
//
//  ORDERED ANIMATION
//    Branch segments grow depth-first: all roots animate in the first
//    slice, their children in the next, and so on.  Leaves and flowers
//    start only after the branch tip at their origin has finished
//    extending.  Pre-existing nodes (hash seen in the previous
//    generation) are assigned IsNew = false and contribute solely to
//    the Stable mesh; they are never re-animated.
//
//  CONTINUOUS MODE
//    When enabled, iterations auto-increment after each animation
//    ends (after a configurable pause), up to maxIterations.
//    ArmContinuousTimer() is called both after animated and instant
//    Generate() calls so the timer starts correctly in all code paths.
//
//  Keyboard shortcuts (Play mode):
//    Space   – increment iterations, animated grow
//    R       – randomise seed, animated grow
//    1–5     – switch tree type instantly (no animation)
// =

using System;
using System.Collections;
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

    // Matches PlantNode in LSystem.h (56 bytes, Sequential, no padding).
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


    //  BRANCH GRAPH
    //
    //  Converts the flat PlantNode branch list into a DAG.
    //  Parent→child edges are resolved by spatial-hashing each segment's
    //  tip position at Q = 1000 (sub-millimetre precision).
    //  A single BFS propagates the Bishop frame and depth from each root.

    sealed class BranchGraph
    {
        public struct Segment
        {
            public Vector3 Origin, End;
            public float Radius;
            public int Parent;          // -1 = root
            public int[] Children;
            public Vector3 FrameNormal;     // Bishop parallel-transport frame
            public int Depth;           // 0 = root, increases toward tips
            public int TipRingStart;    // set during mesh build; -1 = unused
        }

        public Segment[] Segments;
        public int[] Roots;
        public int MaxDepth;

        public static BranchGraph Build(List<PlantNode> nodes)
        {
            int n = nodes.Count;
            var segs = new Segment[n];
            var childLists = new List<int>[n];

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

            // Spatial hash: tip world-position → segment index
            const float Q = 1000f;
            var tipIndex = new Dictionary<(int, int, int), int>(n);
            for (int i = 0; i < n; i++)
                tipIndex[Quant(segs[i].End, Q)] = i;   // last-writer-wins is fine for L-system output

            // Resolve parent edges: a segment whose origin matches a known tip
            // becomes a child of that tip's segment.
            for (int i = 0; i < n; i++)
            {
                if (!tipIndex.TryGetValue(Quant(segs[i].Origin, Q), out int pi) || pi == i) continue;
                float tol = segs[i].Radius * 2f;
                if ((segs[pi].End - segs[i].Origin).sqrMagnitude < tol * tol)
                {
                    segs[i].Parent = pi;
                    childLists[pi].Add(i);
                }
            }

            for (int i = 0; i < n; i++)
                segs[i].Children = childLists[i].ToArray();

            // BFS: propagate Bishop frame and depth level from each root
            var roots = new List<int>(8);
            int maxDepth = 0;
            for (int i = 0; i < n; i++)
                if (segs[i].Parent < 0) roots.Add(i);

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
                    segs[idx].FrameNormal = ParallelTransport(segs[pi].FrameNormal, pTan, tan);
                }

                if (segs[idx].Depth > maxDepth) maxDepth = segs[idx].Depth;
                foreach (int c in segs[idx].Children) queue.Enqueue(c);
            }

            return new BranchGraph { Segments = segs, Roots = roots.ToArray(), MaxDepth = maxDepth };
        }

        //  Self-contained frame helpers 

        // Gram-Schmidt stable normal: avoids global-up when tangent is nearly vertical.
        static Vector3 StableNormal(Vector3 t)
        {
            Vector3 r = Mathf.Abs(t.y) < 0.99f ? Vector3.up : Vector3.right;
            return Vector3.Cross(Vector3.Cross(t, r).normalized, t).normalized;
        }

        // Minimally rotate <paramref name="n"/> from <paramref name="from"/>
        // to <paramref name="to"/> tangent (Bishop / parallel-transport frame).
        static Vector3 ParallelTransport(Vector3 n, Vector3 from, Vector3 to)
        {
            Vector3 axis = Vector3.Cross(from, to);
            float sinA = axis.magnitude, cosA = Vector3.Dot(from, to);
            if (sinA < 1e-6f) return n;
            return Quaternion.AngleAxis(Mathf.Atan2(sinA, cosA) * Mathf.Rad2Deg, axis / sinA) * n;
        }

        static (int, int, int) Quant(Vector3 v, float q)
            => ((int)(v.x * q), (int)(v.y * q), (int)(v.z * q));
    }

    
    //  FOLIAGE SHAPE ENUMS & TREE-TYPE → SHAPE MAPPING
    

    public enum TreeType
    {
        ParametricTree = 0,
        StochasticShrub = 1,
        HybridPlant = 2,
        ABOPTree = 3,
        CustomBloomingTree = 4,
    }

    public enum LeafShape { Teardrop, Oval, Compound, Needle }
    public enum FlowerShape { FivePetal, Daisy, TulipCup, StarBurst }

    static readonly Dictionary<TreeType, (LeafShape leaf, FlowerShape flower)> TypeShapes = new()
    {
        [TreeType.ParametricTree] = (LeafShape.Teardrop, FlowerShape.FivePetal),
        [TreeType.StochasticShrub] = (LeafShape.Oval, FlowerShape.Daisy),
        [TreeType.HybridPlant] = (LeafShape.Compound, FlowerShape.TulipCup),
        [TreeType.ABOPTree] = (LeafShape.Needle, FlowerShape.StarBurst),
        [TreeType.CustomBloomingTree] = (LeafShape.Compound, FlowerShape.FivePetal),
    };

    
    //  LOD
    [Serializable]
    public struct LodSettings
    {
        [Tooltip("Camera distance up to which this LOD band is active.")]
        public float maxDistance;
        [Tooltip("Cylinder sides per branch (high quality ≈ 10, low ≈ 3).")]
        public int branchRadialSegments;
        [Tooltip("Oval-leaf segment count divisor: 1 = full, 2 = half, 4 = minimal.")]
        public int leafDetail;
    }

    
    //  ANIMATION TIMELINE ENTRY
    //
    //  StartT / EndT are normalised positions [0, 1] within the current
    //  animation duration.  IsNew = false means the node existed in the
    //  previous generation and must not be re-animated.
    

    struct NodeAnim { public float StartT, EndT; public bool IsNew; }

    
    //  INSPECTOR FIELDS
    

    [Header("L-System")]
    public TreeType treeType = TreeType.ABOPTree;
    public int iterations = 4;
    public uint seed = 67;

    [Header("Branch Geometry")]
    [Tooltip("Fallback segment count when LOD settings are absent.")]
    public int radialSegments = 8;
    public Material branchMaterial;

    [Header("Leaf Geometry")]
    [Tooltip("Global scale multiplier applied on top of the per-node leaf radius.")]
    public float leafScale = 1.0f;
    public Material leafMaterial;

    [Header("Flower Geometry")]
    public int petalCount = 5;
    [Tooltip("Global scale multiplier applied on top of the per-node flower radius.")]
    public float flowerScale = 1.0f;
    [Range(0f, 1f)]
    public float petalCurvature = 0.3f;
    public Material flowerMaterial;

    [Header("LOD")]
    [Tooltip("Bands ordered nearest → farthest. The last band covers infinity.")]
    public LodSettings[] lodSettings = new[]
    {
        new LodSettings { maxDistance = 10f,  branchRadialSegments = 10, leafDetail = 1 },
        new LodSettings { maxDistance = 30f,  branchRadialSegments = 6,  leafDetail = 1 },
        new LodSettings { maxDistance = 60f,  branchRadialSegments = 4,  leafDetail = 2 },
        new LodSettings { maxDistance = 999f, branchRadialSegments = 3,  leafDetail = 4 },
    };
    [Tooltip("Seconds between LOD distance checks.")]
    public float lodCheckInterval = 0.3f;

    [Header("Growth Animation")]
    [Tooltip("Seconds for all new nodes to reach their final size.")]
    public float growthDuration = 1.6f;
    [Tooltip("0 = linear   1 = smoothstep   2 = spring (slight overshoot)")]
    [Range(0, 2)]
    public int growthEasing = 1;

    [Header("Continuous Mode")]
    [Tooltip("Automatically advance one iteration after each animation completes.")]
    public bool continuousMode = false;
    [Tooltip("Pause in seconds between animation end and the next step.")]
    public float stepInterval = 3.0f;
    [Tooltip("Continuous mode stops after this many iterations.")]
    public int maxIterations = 8;

    
    //  PRIVATE STATE
    

    const int MAX_NODES = 200_000;
    readonly PlantNode[] _nodeBuffer = new PlantNode[MAX_NODES];

    //  Cached node lists (partitioned by NodeType) 
    List<PlantNode> _curBranches = new();
    List<PlantNode> _curLeaves = new();
    List<PlantNode> _curFlowers = new();
    BranchGraph _cachedGraph;

    //  Animation tracking 
    // _knownHashes holds every node hash from the PREVIOUS generation so that
    // ComputeTimelines() can identify newly-added nodes (IsNew = true).
    readonly HashSet<ulong> _knownHashes = new();
    readonly Dictionary<ulong, NodeAnim> _timeline = new();
    float _animProgress;              // normalised [0, 1] cursor within current animation
    Coroutine _growthCoroutine;

    //  Continuous mode 
    float _continuousTimer;
    bool _continuousTimerArmed;

    //  LOD 
    int _activeLod;
    float _lodTimer;
    int _currentRadialSegs;

    
    //  CHILD GAME OBJECTS
    //
    //  Each element type has two GameObjects:
    //    Stable  – pre-existing geometry, built once, never touched mid-anim.
    //    Growing – new geometry only, rebuilt per-frame, cleared when done.
    //
    //  Branches additionally use BuildMode to decide which segments belong
    //  in each partition (see BuildBranchMesh).
    

    GameObject _stableBranchGO, _growingBranchGO;
    MeshFilter _stableBranchMF, _growingBranchMF;
    MeshRenderer _stableBranchMR, _growingBranchMR;

    GameObject _stableLeafGO, _growingLeafGO;
    MeshFilter _stableLeafMF, _growingLeafMF;
    MeshRenderer _stableLeafMR, _growingLeafMR;

    GameObject _stableFlowerGO, _growingFlowerGO;
    MeshFilter _stableFlowerMF, _growingFlowerMF;
    MeshRenderer _stableFlowerMR, _growingFlowerMR;

    
    //  NATIVE IMPORT
    [DllImport("libPlantSim", CallingConvention = CallingConvention.Cdecl)]
    static extern int GeneratePlant(
        int exampleId, int iterations,
        [Out] PlantNode[] outNodes, int maxNodes, uint seed);

    
    //  UNITY LIFECYCLE
    void Awake()
    {
        (_stableBranchGO, _stableBranchMF, _stableBranchMR) = MakeChild("Branches-Stable");
        (_growingBranchGO, _growingBranchMF, _growingBranchMR) = MakeChild("Branches-Growing");
        (_stableLeafGO, _stableLeafMF, _stableLeafMR) = MakeChild("Leaves-Stable");
        (_growingLeafGO, _growingLeafMF, _growingLeafMR) = MakeChild("Leaves-Growing");
        (_stableFlowerGO, _stableFlowerMF, _stableFlowerMR) = MakeChild("Flowers-Stable");
        (_growingFlowerGO, _growingFlowerMF, _growingFlowerMR) = MakeChild("Flowers-Growing");

        var branchMat = branchMaterial ?? DefaultMaterial(new Color(0.35f, 0.22f, 0.12f));
        var leafMat = leafMaterial ?? DefaultMaterial(new Color(0.18f, 0.55f, 0.20f));
        var flowerMat = flowerMaterial ?? DefaultMaterial(new Color(0.95f, 0.70f, 0.75f));

        _stableBranchMR.sharedMaterial = branchMat;
        _growingBranchMR.sharedMaterial = branchMat;
        _stableLeafMR.sharedMaterial = leafMat;
        _growingLeafMR.sharedMaterial = leafMat;
        _stableFlowerMR.sharedMaterial = flowerMat;
        _growingFlowerMR.sharedMaterial = flowerMat;

        _currentRadialSegs = radialSegments;
    }

    void Start() => Generate(animate: false);

    void Update()
    {
        HandleInput();
        UpdateLod();
        UpdateContinuousMode();
    }

    
    //  INPUT
    void HandleInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.spaceKey.wasPressedThisFrame) { iterations++; Generate(animate: true); }
        if (kb.rKey.wasPressedThisFrame) { seed = (uint)UnityEngine.Random.Range(1, int.MaxValue); Generate(animate: true); }

        // Tree-type switches: whole structure changes so instant rebuild, no animation.
        if (kb.digit1Key.wasPressedThisFrame) { treeType = TreeType.ParametricTree; Generate(animate: false); }
        if (kb.digit2Key.wasPressedThisFrame) { treeType = TreeType.StochasticShrub; Generate(animate: false); }
        if (kb.digit3Key.wasPressedThisFrame) { treeType = TreeType.HybridPlant; Generate(animate: false); }
        if (kb.digit4Key.wasPressedThisFrame) { treeType = TreeType.ABOPTree; Generate(animate: false); }
        if (kb.digit5Key.wasPressedThisFrame) { treeType = TreeType.CustomBloomingTree; Generate(animate: false); }
    }

    
    //  CONTINUOUS MODE
    

    void UpdateContinuousMode()
    {
        // Only count down when an animation is not running.
        if (!continuousMode || !_continuousTimerArmed || _growthCoroutine != null) return;

        _continuousTimer -= Time.deltaTime;
        if (_continuousTimer > 0f) return;

        _continuousTimerArmed = false;
        if (iterations >= maxIterations)
        {
            Debug.Log("[PlantRenderer] Continuous mode reached maxIterations.");
            return;
        }

        iterations++;
        Debug.Log($"[PlantRenderer] Continuous step → iterations={iterations}");
        Generate(animate: true);
    }

    // Arm the inter-step countdown.  Called from both animated and
    // instant Generate() paths so continuous mode starts correctly in all cases.
    void ArmContinuousTimer()
    {
        if (!continuousMode || iterations >= maxIterations) return;
        _continuousTimer = stepInterval;
        _continuousTimerArmed = true;
    }

    
    //  LOD
    void UpdateLod()
    {
        _lodTimer -= Time.deltaTime;
        // Block LOD changes while animating to avoid corrupting the
        // Stable / Growing mesh split.
        if (_lodTimer > 0f || _growthCoroutine != null) return;
        _lodTimer = lodCheckInterval;

        Camera cam = Camera.main;
        if (cam == null || lodSettings == null || lodSettings.Length == 0) return;

        float dist = Vector3.Distance(cam.transform.position, transform.position);
        int newLod = lodSettings.Length - 1;
        for (int i = 0; i < lodSettings.Length; i++)
            if (dist <= lodSettings[i].maxDistance) { newLod = i; break; }

        if (newLod == _activeLod) return;
        _activeLod = newLod;
        _currentRadialSegs = lodSettings[_activeLod].branchRadialSegments;

        // Retessellate from cached node lists – no native call.
        RebuildAllFull();
        Debug.Log($"[PlantRenderer] LOD → {_activeLod}  ({_currentRadialSegs} radial segs)");
    }

    //  GENERATION
    void Generate(bool animate)
    {
        // Cancel any in-flight animation cleanly.
        if (_growthCoroutine != null)
        {
            StopCoroutine(_growthCoroutine);
            _growthCoroutine = null;
            _continuousTimerArmed = false;
        }

        //  Native L-system call 
        int count = GeneratePlant((int)treeType, iterations, _nodeBuffer, MAX_NODES, seed);
        if (count <= 0) { Debug.LogError("[PlantRenderer] Native library returned no nodes."); return; }
        count = Mathf.Min(count, MAX_NODES);

        // Partition by NodeType
        var newBranches = new List<PlantNode>(count);
        var newLeaves = new List<PlantNode>(count / 3);
        var newFlowers = new List<PlantNode>(count / 6);
        for (int i = 0; i < count; i++)
            switch ((NodeType)_nodeBuffer[i].type)
            {
                case NodeType.Branch: newBranches.Add(_nodeBuffer[i]); break;
                case NodeType.Leaf: newLeaves.Add(_nodeBuffer[i]); break;
                case NodeType.Flower: newFlowers.Add(_nodeBuffer[i]); break;
            }

        // Build graph before replacing lists (ComputeTimelines uses Segments[i].Depth)
        _cachedGraph = BranchGraph.Build(newBranches);
        _curBranches = newBranches;
        _curLeaves = newLeaves;
        _curFlowers = newFlowers;

        Debug.Log($"[PlantRenderer] {_curBranches.Count} branches | " +
                  $"{_curLeaves.Count} leaves | {_curFlowers.Count} flowers");

        if (animate)
        {
            // ComputeTimelines uses _knownHashes which still holds the PREVIOUS generation.
            ComputeTimelines();

            var (leafShape, flowerShape) = TypeShapes[treeType];
            int leafDetail = ActiveLeafDetail();

            // Stable meshes: pre-existing nodes only, built once.
            _stableBranchMF.sharedMesh = BuildBranchMesh(BuildMode.Stable);
            _stableLeafMF.sharedMesh = BuildLeafMesh(FilterOld(_curLeaves), leafShape, leafDetail, fullyGrown: true);
            _stableFlowerMF.sharedMesh = BuildFlowerMesh(FilterOld(_curFlowers), flowerShape, fullyGrown: true);

            // Advance _knownHashes to the full current set for the NEXT generation.
            RefreshKnownHashes();

            _animProgress = 0f;
            _growthCoroutine = StartCoroutine(GrowthAnimation());
        }
        else
        {
            // Instant rebuild: everything fully grown, Stable mesh holds it all.
            _timeline.Clear();
            RefreshKnownHashes();
            RebuildAllFull();
            // Arm continuous mode even for instant rebuilds (e.g. type-switch at iteration 0).
            ArmContinuousTimer();
        }
    }

    //  Node-list partition helpers 

    // Nodes whose hash was in <c>_knownHashes</c> (previous generation).
    List<PlantNode> FilterOld(List<PlantNode> src)
    {
        var result = new List<PlantNode>(src.Count / 2);
        foreach (var n in src)
            if (_knownHashes.Contains(NodeHash(n))) result.Add(n);
        return result;
    }

    // Nodes assigned <c>IsNew = true</c> in the current timeline.
    List<PlantNode> FilterNew(List<PlantNode> src)
    {
        var result = new List<PlantNode>(src.Count / 2);
        foreach (var n in src)
            if (_timeline.TryGetValue(NodeHash(n), out var a) && a.IsNew) result.Add(n);
        return result;
    }

    void RefreshKnownHashes()
    {
        _knownHashes.Clear();
        foreach (var n in _curBranches) _knownHashes.Add(NodeHash(n));
        foreach (var n in _curLeaves) _knownHashes.Add(NodeHash(n));
        foreach (var n in _curFlowers) _knownHashes.Add(NodeHash(n));
    }

    //  TIMELINE COMPUTATION
    //
    //  Branches
    //    A segment at graph depth d in a tree with max depth D is assigned
    //    the normalised window [d/(D+1), (d+1)/(D+1)].  All segments at
    //    the same depth animate in parallel, so a wide tree fans outward
    //    naturally.  Pre-existing segments get IsNew = false and are never
    //    animated.
    //
    //  Foliage
    //    Foliage nodes are delayed until the branch whose tip position
    //    matches their origin has finished extending.  tipEndT is a spatial
    //    map built during the branch pass; a linear-scan fallback handles
    //    the rare case of no exact match.  Foliage then occupies a fixed
    //    FOLIAGE_WINDOW fraction of the total timeline after that point.

    void ComputeTimelines()
    {
        _timeline.Clear();

        int D = Mathf.Max(1, _cachedGraph.MaxDepth);

        // tip world-position → the EndT of the branch that arrives there
        var tipEndT = new Dictionary<(int, int, int), float>(_curBranches.Count);

        //  Branch timelines 
        for (int i = 0; i < _curBranches.Count; i++)
        {
            ulong h = NodeHash(_curBranches[i]);
            bool old = _knownHashes.Contains(h);
            NodeAnim a;

            if (old)
            {
                a = new NodeAnim { StartT = 0f, EndT = 0f, IsNew = false };
            }
            else
            {
                int depth = _cachedGraph.Segments[i].Depth;
                float startT = (float)depth / (D + 1);
                float endT = (float)(depth + 1) / (D + 1);
                a = new NodeAnim { StartT = startT, EndT = endT, IsNew = true };
            }

            _timeline[h] = a;

            // Keep the latest EndT at each tip position (handles branching forks)
            var key = Quantize(_curBranches[i].end);
            if (!tipEndT.TryGetValue(key, out float prev) || a.EndT > prev)
                tipEndT[key] = a.EndT;
        }

        //  Foliage timelines 
        const float FOLIAGE_WINDOW = 0.18f;   // fraction of total duration for foliage growth

        void AssignFoliage(List<PlantNode> nodes)
        {
            foreach (var node in nodes)
            {
                ulong h = NodeHash(node);

                // Pre-existing foliage: mark as old and skip animation.
                if (_knownHashes.Contains(h))
                {
                    _timeline[h] = new NodeAnim { StartT = 0f, EndT = 0f, IsNew = false };
                    continue;
                }

                // Find the branch tip that this foliage node attaches to.
                var key = Quantize(node.origin);
                float parentEndT;
                if (!tipEndT.TryGetValue(key, out parentEndT))
                {
                    // Fallback: linear scan for the nearest branch endpoint.
                    parentEndT = 1f - FOLIAGE_WINDOW;
                    float bestSq = float.MaxValue;
                    for (int i = 0; i < _curBranches.Count; i++)
                    {
                        float sq = ((Vector3)_curBranches[i].end - (Vector3)node.origin).sqrMagnitude;
                        if (sq < bestSq)
                        {
                            bestSq = sq;
                            parentEndT = _timeline[NodeHash(_curBranches[i])].EndT;
                        }
                    }
                }

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

    //  GROWTH ANIMATION COROUTINE

    IEnumerator GrowthAnimation()
    {
        var (leafShape, flowerShape) = TypeShapes[treeType];
        int leafDetail = ActiveLeafDetail();

        // Pre-partition new-node foliage lists once; the stable meshes won't change.
        var newLeaves = FilterNew(_curLeaves);
        var newFlowers = FilterNew(_curFlowers);

        float elapsed = 0f;
        while (elapsed < growthDuration)
        {
            elapsed += Time.deltaTime;
            _animProgress = Mathf.Clamp01(elapsed / growthDuration);

            // Growing branch mesh: only new segments, using BuildMode.Growing.
            // The Stable branch mesh set in Generate() remains untouched.
            _growingBranchMF.sharedMesh = BuildBranchMesh(BuildMode.Growing);

            // Growing foliage: only new nodes, scaled by GetGrowth() per node.
            _growingLeafMF.sharedMesh = BuildLeafMesh(newLeaves, leafShape, leafDetail, fullyGrown: false);
            _growingFlowerMF.sharedMesh = BuildFlowerMesh(newFlowers, flowerShape, fullyGrown: false);

            yield return null;
        }

        _animProgress = 1f;
        _growthCoroutine = null;

        // Collapse: merge everything into stable meshes, clear growing ones.
        RebuildAllFull();
        ArmContinuousTimer();
    }

    // Rebuild all six meshes at full growth from the cached node lists.
    // Used after animation completion and on LOD band changes.
    void RebuildAllFull()
    {
        var (leafShape, flowerShape) = TypeShapes[treeType];
        int leafDetail = ActiveLeafDetail();

        _stableBranchMF.sharedMesh = BuildBranchMesh(BuildMode.Full);
        _stableLeafMF.sharedMesh = BuildLeafMesh(_curLeaves, leafShape, leafDetail, fullyGrown: true);
        _stableFlowerMF.sharedMesh = BuildFlowerMesh(_curFlowers, flowerShape, fullyGrown: true);

        _growingBranchMF.sharedMesh = EmptyMesh("Branches-Growing");
        _growingLeafMF.sharedMesh = EmptyMesh("Leaves-Growing");
        _growingFlowerMF.sharedMesh = EmptyMesh("Flowers-Growing");
    }

    
    //  GROWTH QUERY
    // Returns the normalised growth fraction [0, 1] for a node at the
    // current animation position.  Always 1 for pre-existing (IsNew = false) nodes.
    float GetGrowth(in PlantNode node)
    {
        ulong h = NodeHash(node);
        if (!_timeline.TryGetValue(h, out NodeAnim a) || !a.IsNew) return 1f;
        if (_animProgress <= a.StartT) return 0f;
        if (_animProgress >= a.EndT) return 1f;
        return Ease((_animProgress - a.StartT) / (a.EndT - a.StartT));
    }

    float Ease(float t)
    {
        t = Mathf.Clamp01(t);
        return growthEasing switch
        {
            0 => t,                                                  // linear
            1 => t * t * (3f - 2f * t),                             // smoothstep
            2 => 1f - Mathf.Exp(-6f * t) * Mathf.Cos(11f * t),     // spring (slight overshoot)
            _ => t,
        };
    }

    //  BRANCH MESH
    //
    //  BuildMode controls which segments are emitted:
    //    Stable  – only pre-existing segments (isOld = true)
    //    Growing – only new segments           (isOld = false)
    //    Full    – all segments at growT = 1
    //
    //  Bishop ring-sharing across the full graph is preserved within each
    //  partition.  When a new (Growing) segment's parent is in the Stable
    //  partition, tipRingStart[parent] is -1, so a fresh base ring is
    //  emitted at the parent's exact world-space tip position.  The two
    //  meshes therefore meet seamlessly without sharing vertex data.
    //
    //  A disc cap seals any growing branch tip (growT < 1) so the cylinder
    //  end is always closed.

    public enum BuildMode { Stable, Growing, Full }

    Mesh BuildBranchMesh(BuildMode mode)
    {
        if (_curBranches.Count == 0 || _cachedGraph == null) return EmptyMesh("Branches");

        int R = _currentRadialSegs;
        int S = _cachedGraph.Segments.Length;

        var verts = new List<Vector3>(S * R * 2 + 32);
        var norms = new List<Vector3>(S * R * 2 + 32);
        var uvs = new List<Vector2>(S * R * 2 + 32);
        var tris = new List<int>(S * R * 6 + 32);

        // Per-segment tip-ring vertex index; -1 = not emitted in this pass.
        var tipRingStart = new int[S];
        for (int i = 0; i < S; i++) tipRingStart[i] = -1;

        var queue = new Queue<int>(_cachedGraph.Roots);
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            ref var seg = ref _cachedGraph.Segments[idx];

            // Always enqueue children so the traversal stays complete even
            // when a parent segment is skipped by the filter below.
            foreach (int c in seg.Children) queue.Enqueue(c);

            Vector3 dir = seg.End - seg.Origin;
            float segLen = dir.magnitude;
            if (segLen < 1e-5f) continue;

            ulong h = NodeHash(_curBranches[idx]);

            // Use _timeline (frozen at ComputeTimelines time) rather than _knownHashes,
            // which is overwritten by RefreshKnownHashes() before the coroutine runs.
            // Without this fix every node looks "old" during animation and Growing is empty.
            bool isNew = _timeline.TryGetValue(h, out var ta) && ta.IsNew;

            // Emit geometry only for the segments that belong to this partition.
            bool render = mode == BuildMode.Full
                       || (mode == BuildMode.Stable && !isNew)
                       || (mode == BuildMode.Growing && isNew);
            if (!render) continue;

            // Pre-existing segments and Full-mode always draw at full length.
            // New segments in Growing mode interpolate their tip.
            float growT = (mode == BuildMode.Full || !isNew)
                ? 1f : GetGrowth(_curBranches[idx]);
            if (growT < 0.001f) continue;  // StartT not yet reached; skip

            Vector3 tangent = dir / segLen;
            Vector3 frameN = seg.FrameNormal;
            Vector3 frameB = Vector3.Cross(tangent, frameN).normalized;
            float r = seg.Radius;

            //  Base ring 
            // If the parent was emitted in THIS pass, reuse its tip ring indices.
            // If the parent was in the other partition (tipRingStart == -1), emit
            // a fresh ring precisely at the parent's tip world position so the
            // two meshes meet without gaps.
            int baseStart;
            if (seg.Parent >= 0 && tipRingStart[seg.Parent] >= 0)
            {
                baseStart = tipRingStart[seg.Parent];
            }
            else
            {
                baseStart = verts.Count;
                EmitRing(verts, norms, uvs, seg.Origin, r, frameN, frameB, 0f, R);
            }

            //  Tip ring 
            Vector3 tipPos = seg.Origin + tangent * (segLen * growT);
            float tipR = r * Mathf.Lerp(0.5f, 1f, growT);   // starts thin, widens to full

            tipRingStart[idx] = verts.Count;
            EmitRing(verts, norms, uvs, tipPos, tipR, frameN, frameB, 1f, R);

            // Stitch base → tip with quad pairs
            for (int s = 0; s < R; s++)
            {
                int sn = (s + 1) % R;
                int b0 = baseStart + s, b1 = baseStart + sn;
                int t0 = tipRingStart[idx] + s, t1 = tipRingStart[idx] + sn;
                tris.Add(b0); tris.Add(b1); tris.Add(t0);
                tris.Add(t0); tris.Add(b1); tris.Add(t1);
            }

            //  Growing tip cap 
            // Seals the open end of the cylinder while it is still extending.
            if (growT < 0.999f)
            {
                int capIdx = verts.Count;
                verts.Add(tipPos);
                norms.Add(tangent);
                uvs.Add(new Vector2(0.5f, 0.5f));

                for (int s = 0; s < R; s++)
                {
                    int sn = (s + 1) % R;
                    tris.Add(capIdx);
                    tris.Add(tipRingStart[idx] + s);
                    tris.Add(tipRingStart[idx] + sn);
                }
            }
        }

        return FinaliseMesh("Branches", verts, norms, uvs, tris, recalcNormals: false);
    }

    static void EmitRing(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs,
        Vector3 center, float radius, Vector3 frameN, Vector3 frameB, float vCoord, int R)
    {
        for (int s = 0; s < R; s++)
        {
            float theta = s / (float)R * Mathf.PI * 2f;
            Vector3 outDir = Mathf.Cos(theta) * frameN + Mathf.Sin(theta) * frameB;
            verts.Add(center + outDir * radius);
            norms.Add(outDir);
            uvs.Add(new Vector2((float)s / R, vCoord));
        }
    }

    // LEAF MESH
    Mesh BuildLeafMesh(List<PlantNode> nodes, LeafShape shape, int lodDetail, bool fullyGrown)
    {
        if (nodes.Count == 0) return EmptyMesh("Leaves");

        var verts = new List<Vector3>(nodes.Count * 16);
        var norms = new List<Vector3>(nodes.Count * 16);
        var uvs = new List<Vector2>(nodes.Count * 16);
        var tris = new List<int>(nodes.Count * 24);

        foreach (var node in nodes)
        {
            float growT = fullyGrown ? 1f : GetGrowth(node);
            if (growT < 0.01f) continue;

            Vector3 pos = node.origin;
            Vector3 fwd = ((Vector3)node.heading).normalized;
            Vector3 right = ((Vector3)node.left).normalized;
            Vector3 faceN = Vector3.Cross(fwd, right).normalized;
            float size = node.radius * leafScale * growT;   // scale from 0 → full

            switch (shape)
            {
                case LeafShape.Teardrop: EmitTeardrop(verts, norms, uvs, tris, pos, fwd, right, faceN, size); break;
                case LeafShape.Oval: EmitOval(verts, norms, uvs, tris, pos, fwd, right, faceN, size, lodDetail); break;
                case LeafShape.Compound: EmitCompound(verts, norms, uvs, tris, pos, fwd, right, faceN, size); break;
                case LeafShape.Needle: EmitNeedle(verts, norms, uvs, tris, pos, fwd, right, faceN, size); break;
            }
        }

        return FinaliseMesh("Leaves", verts, norms, uvs, tris, recalcNormals: false);
    }

    // Teardrop (ParametricTree) 
    static void EmitTeardrop(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        Vector3 pos, Vector3 fwd, Vector3 right, Vector3 n, float size)
    {
        float hw = size * 0.35f;
        Vector3 p0 = pos,
                p1 = pos + right * hw + fwd * size * 0.40f,
                p2 = pos + fwd * size,
                p3 = pos - right * hw + fwd * size * 0.40f;
        EmitDoubleSidedQuad(verts, norms, uvs, tris, p0, p1, p2, p3, n);
    }

    // Oval fan (StochasticShrub) 
    // Smooth ellipse built from a triangle fan; segment count driven by LOD.
    static void EmitOval(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        Vector3 pos, Vector3 fwd, Vector3 right, Vector3 n, float size, int lodDetail)
    {
        int segs = Mathf.Max(4, 12 / lodDetail);
        Vector3 center = pos + fwd * size * 0.5f;

        // Duplicate centre vertices: one for front face, one for back face.
        int cf = verts.Count;
        verts.Add(center); norms.Add(n); uvs.Add(new Vector2(0.5f, 0.5f));
        int cb = verts.Count;
        verts.Add(center); norms.Add(-n); uvs.Add(new Vector2(0.5f, 0.5f));
        int rimBase = verts.Count;

        for (int s = 0; s <= segs; s++)
        {
            float a = s / (float)segs * Mathf.PI * 2f;
            float c = Mathf.Cos(a), si = Mathf.Sin(a);
            Vector3 rim = pos
                + fwd * (size * 0.5f + size * 0.5f * si)
                + right * (size * 0.38f * c);
            verts.Add(rim); norms.Add(n); uvs.Add(new Vector2(0.5f + 0.5f * c, 0.5f + 0.5f * si));
            verts.Add(rim); norms.Add(-n); uvs.Add(new Vector2(0.5f + 0.5f * c, 0.5f + 0.5f * si));
        }

        for (int s = 0; s < segs; s++)
        {
            int f0 = rimBase + s * 2, f1 = rimBase + (s + 1) * 2;
            tris.Add(cf); tris.Add(f0); tris.Add(f1);
            tris.Add(cb); tris.Add(f1 + 1); tris.Add(f0 + 1);
        }
    }

    // Compound pinnate (HybridPlant / CustomBloomingTree) 
    // Central vein with paired leaflets plus a terminal tip leaflet.
    static void EmitCompound(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        Vector3 pos, Vector3 fwd, Vector3 right, Vector3 n, float size)
    {
        const int leaflets = 3;
        float leafLen = size * 0.32f;

        for (int i = 0; i < leaflets; i++)
        {
            float t = (i + 1f) / (leaflets + 1f);
            Vector3 pivot = pos + fwd * size * t;
            float ls = leafLen * (1f - t * 0.3f);
            // Left leaflet
            EmitTeardrop(verts, norms, uvs, tris, pivot, (fwd + right * 0.65f).normalized, right, n, ls);
            // Right leaflet (flipped facing so both sides are visible correctly)
            EmitTeardrop(verts, norms, uvs, tris, pivot, (fwd - right * 0.65f).normalized, -right, -n, ls);
        }

        // Terminal tip leaflet
        EmitTeardrop(verts, norms, uvs, tris, pos + fwd * size * 0.85f, fwd, right, n, size * 0.28f);
    }

    // Needle (ABOPTree) 
    // Razor-thin spike mimicking a conifer needle.
    static void EmitNeedle(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        Vector3 pos, Vector3 fwd, Vector3 right, Vector3 n, float size)
    {
        float hw = size * 0.055f;
        Vector3 p0 = pos, p1 = pos + right * hw, p2 = pos + fwd * size, p3 = pos - right * hw;
        EmitDoubleSidedQuad(verts, norms, uvs, tris, p0, p1, p2, p3, n);
    }

    //  Shared: double-sided quad 
    static void EmitDoubleSidedQuad(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 n)
    {
        // Front face
        int b = verts.Count;
        verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
        for (int k = 0; k < 4; k++) norms.Add(n);
        uvs.Add(new Vector2(0.5f, 0f)); uvs.Add(new Vector2(1f, 0.4f));
        uvs.Add(new Vector2(0.5f, 1f)); uvs.Add(new Vector2(0f, 0.4f));
        tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
        tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);

        // Back face (duplicated vertices with flipped normal)
        b = verts.Count;
        verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
        for (int k = 0; k < 4; k++) norms.Add(-n);
        uvs.Add(new Vector2(0.5f, 0f)); uvs.Add(new Vector2(1f, 0.4f));
        uvs.Add(new Vector2(0.5f, 1f)); uvs.Add(new Vector2(0f, 0.4f));
        tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
        tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
    }

    //  FLOWER MESH

    Mesh BuildFlowerMesh(List<PlantNode> nodes, FlowerShape shape, bool fullyGrown)
    {
        if (nodes.Count == 0) return EmptyMesh("Flowers");

        var verts = new List<Vector3>(nodes.Count * 32);
        var norms = new List<Vector3>(nodes.Count * 32);
        var uvs = new List<Vector2>(nodes.Count * 32);
        var tris = new List<int>(nodes.Count * 48);

        foreach (var node in nodes)
        {
            float growT = fullyGrown ? 1f : GetGrowth(node);
            if (growT < 0.01f) continue;

            Vector3 center = node.origin;
            Vector3 axis = ((Vector3)node.heading).normalized;
            Vector3 right = ((Vector3)node.left).normalized;
            Vector3 up2 = Vector3.Cross(axis, right).normalized;
            float size = node.radius * flowerScale * growT;   // scale from 0 → full

            switch (shape)
            {
                case FlowerShape.FivePetal:
                    EmitRadialFlower(verts, norms, uvs, tris, center, axis, right, up2, size, 5, 0.25f, 0.28f, petalCurvature);
                    break;
                case FlowerShape.Daisy:
                    EmitRadialFlower(verts, norms, uvs, tris, center, axis, right, up2, size, 12, 0.18f, 0.14f, 0.06f);
                    break;
                case FlowerShape.TulipCup:
                    EmitTulip(verts, norms, uvs, tris, center, axis, right, up2, size);
                    break;
                case FlowerShape.StarBurst:
                    EmitStar(verts, norms, uvs, tris, center, axis, right, up2, size);
                    break;
            }
        }

        return FinaliseMesh("Flowers", verts, norms, uvs, tris, recalcNormals: false);
    }

    // Radial petal fan (FivePetal / Daisy) 
    static void EmitRadialFlower(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        Vector3 center, Vector3 axis, Vector3 right, Vector3 up2,
        float size, int petals, float discFrac, float hwFrac, float curvature)
    {
        float discR = size * discFrac;
        float petalLen = size;
        float hw = size * hwFrac;

        // Central disc (triangle fan)
        int db = verts.Count;
        verts.Add(center); norms.Add(axis); uvs.Add(new Vector2(0.5f, 0.5f));
        for (int p = 0; p < petals; p++)
        {
            float a = p / (float)petals * Mathf.PI * 2f;
            Vector3 d = Mathf.Cos(a) * right + Mathf.Sin(a) * up2;
            verts.Add(center + d * discR);
            norms.Add(axis);
            uvs.Add(new Vector2(0.5f + 0.25f * Mathf.Cos(a), 0.5f + 0.25f * Mathf.Sin(a)));
        }
        for (int p = 0; p < petals; p++)
        { tris.Add(db); tris.Add(db + 1 + p); tris.Add(db + 1 + (p + 1) % petals); }

        // Petals: tapered quads, cupped upward by curvature
        for (int p = 0; p < petals; p++)
        {
            float a = p / (float)petals * Mathf.PI * 2f;
            Vector3 pd = (Mathf.Cos(a) * right + Mathf.Sin(a) * up2).normalized;
            Vector3 lat = Vector3.Cross(pd, axis).normalized;
            Vector3 rC = center + pd * discR;
            Vector3 tC = center + pd * petalLen + axis * (petalLen * curvature);
            Vector3 pn = (axis + pd * curvature).normalized;

            int pb = verts.Count;
            verts.Add(rC - lat * hw); verts.Add(rC + lat * hw);
            verts.Add(tC + lat * hw * 0.35f); verts.Add(tC - lat * hw * 0.35f);
            for (int k = 0; k < 4; k++) norms.Add(pn);
            uvs.Add(Vector2.zero); uvs.Add(new Vector2(1, 0)); uvs.Add(Vector2.one); uvs.Add(new Vector2(0, 1));

            // Front + back faces
            tris.Add(pb); tris.Add(pb + 1); tris.Add(pb + 2);
            tris.Add(pb); tris.Add(pb + 2); tris.Add(pb + 3);
            tris.Add(pb); tris.Add(pb + 2); tris.Add(pb + 1);
            tris.Add(pb); tris.Add(pb + 3); tris.Add(pb + 2);
        }
    }

    // Tulip cup (HybridPlant) 
    // 6 petals each built from ring segments that curve inward at the tip.
    static void EmitTulip(
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris,
        Vector3 center, Vector3 axis, Vector3 right, Vector3 up2, float size)
    {
        const int petals = 6, rings = 3;
        float discR = size * 0.18f, pLen = size, pW = size * 0.32f;

        // Disc
        int db = verts.Count;
        verts.Add(center); norms.Add(axis); uvs.Add(new Vector2(0.5f, 0.5f));
        for (int p = 0; p < petals; p++)
        {
            float a = p / (float)petals * Mathf.PI * 2f;
            verts.Add(center + (Mathf.Cos(a) * right + Mathf.Sin(a) * up2) * discR);
            norms.Add(axis); uvs.Add(new Vector2(0.5f, 0.5f));
        }
        for (int p = 0; p < petals; p++)
        { tris.Add(db); tris.Add(db + 1 + p); tris.Add(db + 1 + (p + 1) % petals); }

        // Curved petals
        for (int p = 0; p < petals; p++)
        {
            float a = p / (float)petals * Mathf.PI * 2f;
            Vector3 pd = (Mathf.Cos(a) * right + Mathf.Sin(a) * up2).normalized;
            Vector3 lat = Vector3.Cross(pd, axis).normalized;
            int vBase = verts.Count;

            for (int ri = 0; ri <= rings; ri++)
            {
                float t = ri / (float)rings;
                float curl = Mathf.Sin(t * Mathf.PI) * 0.35f;
                Vector3 rc = center + pd * (discR + pLen * t) + axis * (pLen * t * (0.55f - curl));
                float w = pW * (1f - t * 0.28f);
                Vector3 n2 = (axis + pd * (1f - t * 0.7f)).normalized;
                verts.Add(rc - lat * w); norms.Add(n2); uvs.Add(new Vector2(0f, t));
                verts.Add(rc + lat * w); norms.Add(n2); uvs.Add(new Vector2(1f, t));
            }

            for (int ri = 0; ri < rings; ri++)
            {
                int i0 = vBase + ri * 2;
                // Front face
                tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                tris.Add(i0 + 1); tris.Add(i0 + 3); tris.Add(i0 + 2);
                // Back face
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 1);
                tris.Add(i0 + 1); tris.Add(i0 + 2); tris.Add(i0 + 3);
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
        return (go, go.AddComponent<MeshFilter>(), go.AddComponent<MeshRenderer>());
    }

    static Material DefaultMaterial(Color color) =>
        new Material(Shader.Find("Standard")) { color = color };
}