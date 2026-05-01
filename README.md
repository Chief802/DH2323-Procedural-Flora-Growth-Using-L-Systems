# Real-Time Procedural Flora Growth Using L-Systems

## Abstract
This paper extends the methods for generating flora using L-Systems as described in *The Algorithmic Beauty of Plants* by Przemyslaw Prusinkiewicz and Aristid Lindenmayer. 
This is done by investigating the efficacy of using methods described to generate a wide array of unique instances of flora in real-time using efficient construction 
and rendering techniques. 

Supervisor: [Professor and director of the Embodied Social Agents Lab (ESAL) Dr. Christopher Peters](https://www.kth.se/profile/chpeters)

Video Demo: WIP

Full Report: WIP

## Implementation
This study's implementation is in three major parts:
- The axioms describing how different species of flora are grown, as can be found in GenerateFlora3D.cpp
- The interpretation of rules, as can be found in LSystem.h
- The bridge from the algorithm to Unity, as can be found in PlantRenderer.cs

The floral axioms and L-System parser were implemented in C++, whereas the Unity bridge was implemented in C#.
A .dll file needs to be built in order to run the program. No additional packages or third-party APIs were used in Unity. 

## Some Update Highlights
### 2026 April 12 \- Project Start  
The feedback to the first project specification draft was returned, allowing goals beyond the implementation aspect to be set.
The focus was consequently set on evaluating the algorithm and its implementation. This was investigated from the following perspectives
- Speed, in terms of how quickly flora could be generated
- Memory, in terms of how expensive a floral instance is
- Realism, in terms of how well the structure of real flora is captured by the L-System
These aspects were then taken from the individual level of one plant, to investigating how scaling to many more floral instances affect them.

### 2026 April 14 \- The first plant  
Before an evaluation can take place, there needs to be something to evaluate. The current short-term goal for the project was at this point to implement both a basic, 
2-Dimenstional plant, and to be able to have it be shown in Unity (with the use of LineRenderer). This first instance, and its growth stages, can be shown below.

![First version in 2D](Assets/PlantSim2DV01.gif)

### INTERMISSION - How does a basic L-System work?
What Prusinkiewicz et al and related works fundamentally argue, is that the way plants grow is neither unpredictable or inimitable, but rather in accordance to 
algorithms of various complexities that we ourselves can imitate.

When building an L-System, we apply certain rules to certain symbols, imagined as a turtle moving around (by convention).
- F means go forward one segment
- \+ mean turn left a certain amount of degrees degrees
- [ means to start a new branch
- etc.

This plant above follows a very simply rule:  `F :- F[+F]F[-F]F`  
This tells us that *for every segment F, go forward; start a new branch, turn left, and go forward; go forward; start a new branch, turn right, and go forward; and finally go forward again*. By writing more and more sophisticated rules, we can create more and more sophisticated plants.  

### 2026 April 20 \- What if we had even more dimensions?  
Although there is value in having a 2D implementation (which I shall expand upon in the "future work" section whenever that is written), the goal was always to expand to 3D. This meant that two things needed to be changed:
- The way the turtle moves
- The way the plant is rendered  

The TurtleState struct now has a 3D position and three vectors U (Up), L (Left), and H (Heading) for direction.
These allow us to implement  pitch, yaw, and roll for the turtle.

```
inline void RotateTurtle(TurtleState &t, char axis, float alpha)
    {
        const float ca = std::cos(alpha), sa = std::sin(alpha);
        const Vec3 oH = t.H, oU = t.U, oL = t.L;
        switch (axis)
        {
        case 'U':
            t.H = {oH.x * ca + oL.x * sa, oH.y * ca + oL.y * sa, oH.z * ca + oL.z * sa};
            t.L = {-oH.x * sa + oL.x * ca, -oH.y * sa + oL.y * ca, -oH.z * sa + oL.z * ca};
            break;
        case 'L':
            t.H = {oH.x * ca - oU.x * sa, oH.y * ca - oU.y * sa, oH.z * ca - oU.z * sa};
            t.U = {oH.x * sa + oU.x * ca, oH.y * sa + oU.y * ca, oH.z * sa + oU.z * ca};
            break;
        case 'H':
            t.L = {oL.x * ca - oU.x * sa, oL.y * ca - oU.y * sa, oL.z * ca - oU.z * sa};
            t.U = {oL.x * sa + oU.x * ca, oL.y * sa + oU.y * ca, oL.z * sa + oU.z * ca};
            break;
        default:
            break;
        }
    }
```

This first 3D implemented cylinders to connect points for simplicitity, but was later to be changed to something that had more of a justification for its implementation. 
Nevertheless, the implementation looked like the following:
```
void BuildBranchMesh(Segment[] segments, int count)
    {
        if (Keyboard.current.spaceKey.wasPressedThisFrame)
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector3> normals = new List<Vector3>();

        for (int i = 0; i < count; i++)
        {
            Vector3 a = ToUnityVector(segments[i].a);
            Vector3 b = ToUnityVector(segments[i].b);

            float radius = segments[i].radius * Mathf.Lerp(1f, 0.25f, i / (float)count);

            AddCylinder(
                a,
                b,
                radius,
                radialSegments,
                vertices,
                triangles,
                normals
            );
        }

        Mesh mesh = new Mesh
        {
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetNormals(normals);
        mesh.RecalculateBounds();

        mf.mesh = mesh;
    }
```

### 2026 April 29 \- Greater control and refactoring
In order to simulate more advanced plants, a more advanced L-System was required. This was accomplished by expanding the L-System in two major ways
- Making it stochastic
- Making it parametric

A stochastic L-System allows multiple branching paths for one specific rule enabling diversity withing a plant species.
It can additionally be used to set a natural limit for how large a plant can grow, by making it so that end-points, such as flowers,
become more and more likely to occur as the tree has more iterations. This can then culminate in a 100% chance after a set limit, naturally having the plant stop growing.  
An example can be seen below:

```
static int BuildStochasticShrub(int iters, PlantNode *out, int maxNodes, unsigned int seed)
{
    LSystem sys(seed);

    sys.AddRule('A', 0.34f, [](const std::vector<float> &)
                { return Sentence{{'F'}, {'['}, {'+'}, {'A'}, {'~'}, {']'}, {'F'}, {'['}, {'-'}, {'A'}, {'~'}, {']'}, {'A'}}; });
    sys.AddRule('A', 0.33f, [](const std::vector<float> &)
                { return Sentence{{'F'}, {'['}, {'+'}, {'A'}, {'~'}, {']'}, {'A'}}; });
    sys.AddRule('A', 0.33f, [](const std::vector<float> &)
                { return Sentence{{'F'}, {'['}, {'-'}, {'A'}, {'~'}, {']'}, {'A'}}; });

    Sentence result = sys.Generate(MakeSentence("A"), iters);

    return InterpretFull(result, 0.3f, 25.0f, out, maxNodes);
}
```
A parametric L-System on the other hand enables different rules to carry parameters. These can be parameters such as length in the case of movement, an angle in the case of turning, or directly setting a branches radius to a value of choice. Previously, every time a rule appeared it had the same value, limiting choice greatly.  
An example of this can be found from the following implemenation of a tree from *The Algorithmic Beauty of Plants*:

```
static int BuildABOPTree(int iters, PlantNode* out, int maxNodes, unsigned int seed)
{
    constexpr float d1 = 94.74f;
    constexpr float d2 = 132.63f;
    constexpr float a  = 18.95f;
    constexpr float lr = 1.109f;
    constexpr float vr = 1.732f;

    LSystem sys(seed);

    // p1 – Apex expansion with leaves and a single apical flower
    sys.AddRule('A', [](const std::vector<float>&) {
        return Sentence {
            Symbol('!', {vr}),
            Symbol('F', {50.f}),

            Symbol('['),
                Symbol('&', {a}), Symbol('F', {50.f}), Symbol('A'),
                Symbol('~', {2.0f}),   // leaf near this arm's apex
            Symbol(']'),

            Symbol('/', {d1}),

            Symbol('['),
                Symbol('&', {a}), Symbol('F', {50.f}), Symbol('A'),
                Symbol('~', {2.0f}),
            Symbol(']'),

            Symbol('/', {d2}),

            Symbol('['),
                Symbol('&', {a}), Symbol('F', {50.f}), Symbol('A'),
                Symbol('~', {2.0f}),
            Symbol(']'),

            Symbol('@', {1.5f}),       // flower at the meristem apex
        };
    });

    // p2 – Segment elongation
    sys.AddRule(ProductionRule{
        'F', 1.0f, nullptr,
        [](const std::vector<float>& p) {
            float l = p.empty() ? 1.f : p[0];
            return Sentence { Symbol('F', {l * lr}) };
        }
    });

    // p3 – Radius fattening (pipe model)
    sys.AddRule(ProductionRule{
        '!', 1.0f, nullptr,
        [](const std::vector<float>& p) {
            float w = p.empty() ? 1.f : p[0];
            return Sentence { Symbol('!', {w * vr}) };
        }
    });

    Sentence axiom {
        Symbol('!', {1.f}),
        Symbol('F', {200.f}),
        Symbol('/', {45.f}),
        Symbol('A'),
    };

    Sentence result = sys.Generate(axiom, iters);

    std::cout << "[ABOPTree]  iter=" << iters
              << "  seed=" << seed
              << "  nodes=" << result.size() << "\n";

    return InterpretFull(result, 1.0f, 22.5f, out, maxNodes);
}
```

This creates the following tree, at iteration 1 and 5. Note that the performance output shown is of little interest except for the output from the scene.
![ABOP Tree Iteration 1](Assets/ABOP1.png)
![ABOP Tree Iteration 5](Assets/ABOP5.png)  
The next step is to implement animations (a much better stress test) and a nicer way to connect parts of the tree (notice the different cylinders being very visible)

Lastly, the current rules for the L-System are the following:
- F(l)   Draw forward, step = l
- f(l)   Move forward, no geometry
- ~(s)   Emit a Leaf  at current position, size = s   
- @(s)   Emit a Flower at current position, size = s
- !(w)   Set current radius to w
- \+ \-  Yaw   left / right   
- & ^    Pitch  down / up
- \ /    Roll   left / right
- |      U-turn (180°)
- \[]   Start/end branch  

Note that all angle options also take in parameters.
### 2026 May 1 \- Animation, Structural Improvements and Performance Enhancement
After extending to three dimensions, in addition to allowing plants of whichever complexity we want, the next step was to shift the project focus to the Real-Time rendering aspect.  

To begin with, a better tester plant was created that would be both stochastic and parametric, featuring both leaves and flowers. This plant will naturally stop growing after enough iterations have taken place, with a trunk that additionally gets smaller as the plant grows upwards. The rules for it are as follows:  
```
static int BuildCustomTree(int iters, PlantNode *out, int maxNodes, unsigned int seed)
{
    LSystem sys(seed);

    std::mt19937 local_rng(seed);

    sys.AddRule(ProductionRule{
        'A', 1.0f,
        nullptr,
        [&local_rng](const std::vector<float> &p)
        {
            // Extract parameters: [length, radius, depth]
            float l = p.empty() ? 1.0f : p[0];
            float r = p.size() > 1 ? p[1] : 0.8f;     // Starting trunk thickness
            float depth = p.size() > 2 ? p[2] : 0.0f; // Tracks current iteration tier

            // Chance to bloom reaches 100% by depth 8
            float chance = depth >= 8.0f ? 1.0f : depth / 8.0f;
            std::uniform_real_distribution<float> dist(0.0f, 1.0f);

            if (dist(local_rng) < chance)
            {
                // TERMINAL STATE: Branch stops growing, produces a rosette leaf cluster and a flower
                return Sentence{
                    Symbol('!', {r}),
                    Symbol('F', {l}),
                    Symbol('['), Symbol('+', {30.f}), Symbol('~', {l * 0.9f}), Symbol(']'),
                    Symbol('['), Symbol('-', {30.f}), Symbol('~', {l * 0.9f}), Symbol(']'),
                    Symbol('['), Symbol('^', {30.f}), Symbol('~', {l * 0.9f}), Symbol(']'),
                    Symbol('['), Symbol('&', {30.f}), Symbol('~', {l * 0.9f}), Symbol(']'),
                    Symbol('@', {l * 3.0f})};
            }
            else
            {
                // Branch gets thinner, spawns leaves and new sub-branches
                return Sentence{
                    Symbol('!', {r}),
                    Symbol('F', {l}),

                    // Small leaves along the stem
                    Symbol('['), Symbol('+', {50.f}), Symbol('~', {l * 0.4f}), Symbol(']'),
                    Symbol('['), Symbol('-', {50.f}), Symbol('~', {l * 0.4f}), Symbol(']'),

                    // Side Branch 1
                    Symbol('['), Symbol('+', {30.f}), Symbol('^', {15.f}),
                    Symbol('A', {l * 0.85f, r * 0.6f, depth + 1.f}), Symbol(']'),

                    // Side Branch 2
                    Symbol('['), Symbol('-', {35.f}), Symbol('&', {10.f}),
                    Symbol('A', {l * 0.8f, r * 0.55f, depth + 1.f}), Symbol(']'),

                    // Side Branch 3 (adds 3D volume using the roll '\' operator)
                    Symbol('['), Symbol('\\', {90.f}), Symbol('+', {40.f}),
                    Symbol('A', {l * 0.75f, r * 0.5f, depth + 1.f}), Symbol(']'),

                    // Main Trunk elongation (scales 'r' by 0.85 to taper smoothly)
                    Symbol('A', {l * 0.95f, r * 0.85f, depth + 1.f})};
            }
        }});
```

From one iteration to another, branches, leaves, and flowers are created. The goal is to have the transition from one of these iterations to the next appear smooth, such that the aforementioned plant parts are extended from previously existing ones. The user should additionally not need to input anything to have the plant grow from one stage to the next - continuity is important.  

We can save on resources by only investigating nodes that actually need to grow. If a part has been unchanged then we can deem it finished.  
```
if (animate)
{
    // Calculates when each node should start and stop growing based on its depth in the tree.
    ComputeTimelines();
    var (leafShape, flowerShape) = TypeShapes[treeType];
    int leafDetail = ActiveLeafDetail();

    // Populate stable meshes with old nodes to keep them off the dynamic update loop
    BuildBranchMesh(BuildMode.Stable, _stableBranchMesh);
    BuildLeafMesh(_curLeaves, leafShape, leafDetail, true, FilterMode.OnlyOld, _stableLeafMesh);
    BuildFlowerMesh(_curFlowers, flowerShape, true, FilterMode.OnlyOld, _stableFlowerMesh);

    // Records the current layout to differentiate "old" structure from "new" structure later.
    RefreshKnownHashes();
    _animProgress = 0f;
    // Coroutine handling the frame-by-frame mesh update of new segments.
    _growthCoroutine = StartCoroutine(GrowthAnimation());
}
```  

Generating many edges and vertices is not without cost, however...
