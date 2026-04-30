// =============================================================
//  GenerateFlora3D.cpp  –  C API implementation (Stable Topology)
// =============================================================
#define PLANTSIM_EXPORTS
#include "GenerateFlora3D.h"
#include <iostream>

// =============================================================
//  Example 0 – Parametric Tree
// =============================================================
static int BuildParametricTree(int iters, PlantNode *out, int maxNodes, unsigned int)
{
    constexpr float TRUNK_R = 0.60f;
    constexpr float SIDE_R = 0.50f;
    constexpr float LEAF_S = 0.45f;
    constexpr float ANGLE = 25.7f;
    constexpr float MIN_LEN = 0.04f;

    LSystem sys;

    // 'A' is the apical meristem. 'F' segments are permanently left behind.
    sys.AddRule(ProductionRule{
        'A', 1.0f,
        [](const std::vector<float> &p)
        { return p.empty() || p[0] > MIN_LEN; },
        [](const std::vector<float> &p)
        {
            float l = p.empty() ? 1.0f : p[0];
            return Sentence{
                Symbol('F', {l}),
                Symbol('['),
                Symbol('+', {ANGLE}),
                Symbol('~', {l * LEAF_S}),
                Symbol('A', {l * SIDE_R}),
                Symbol(']'),
                Symbol('F', {l}),
                Symbol('['),
                Symbol('-', {ANGLE}),
                Symbol('~', {l * LEAF_S}),
                Symbol('A', {l * SIDE_R}),
                Symbol(']'),
                Symbol('A', {l * TRUNK_R}),
            };
        }});

    sys.AddRule(ProductionRule{
        'A', 1.0f,
        [](const std::vector<float> &p)
        { return !p.empty() && p[0] <= MIN_LEN; },
        [](const std::vector<float> &p)
        {
            float l = p.empty() ? MIN_LEN : p[0];
            return Sentence{Symbol('@', {l * 4.0f})};
        }});

    Sentence axiom{Symbol('A', {1.0f})};
    Sentence result = sys.Generate(axiom, iters);

    std::cout << "[ParametricTree]  iter=" << iters << "  nodes=" << result.size() << "\n";
    return InterpretFull(result, 1.0f, ANGLE, out, maxNodes);
}

// =============================================================
//  Example 1 – Stochastic Shrub
// =============================================================
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

    std::cout << "[StochasticShrub]  iter=" << iters << "  seed=" << seed << "  nodes=" << result.size() << "\n";
    return InterpretFull(result, 0.3f, 25.0f, out, maxNodes);
}

// =============================================================
//  Example 2 – Hybrid Plant
// =============================================================
static int BuildHybridPlant(int iters, PlantNode *out, int maxNodes, unsigned int seed)
{
    constexpr float MIN_LEN = 0.05f;
    LSystem sys(seed);

    sys.AddRule(ProductionRule{
        'A', 0.5f,
        [MIN_LEN](const std::vector<float> &p)
        { return p.empty() || p[0] > MIN_LEN; },
        [](const std::vector<float> &p)
        {
            float l = p.empty() ? 1.0f : p[0];
            return Sentence{
                Symbol('F', {l * 0.55f}),
                Symbol('['),
                Symbol('+', {30.f}),
                Symbol('~', {l * 0.40f}),
                Symbol('A', {l * 0.45f}),
                Symbol(']'),
                Symbol('F', {l * 0.55f}),
                Symbol('['),
                Symbol('-', {30.f}),
                Symbol('~', {l * 0.40f}),
                Symbol('A', {l * 0.45f}),
                Symbol(']'),
                Symbol('A', {l * 0.55f}),
            };
        }});

    sys.AddRule(ProductionRule{
        'A', 0.5f,
        [MIN_LEN](const std::vector<float> &p)
        { return p.empty() || p[0] > MIN_LEN; },
        [](const std::vector<float> &p)
        {
            float l = p.empty() ? 1.0f : p[0];
            return Sentence{
                Symbol('F', {l * 0.6f}),
                Symbol('['),
                Symbol('+', {20.f}),
                Symbol('^', {15.f}),
                Symbol('A', {l * 0.5f}),
                Symbol('~', {l * 0.35f}),
                Symbol(']'),
                Symbol('A', {l * 0.6f}),
            };
        }});

    sys.AddRule(ProductionRule{
        'A', 1.0f,
        [MIN_LEN](const std::vector<float> &p)
        { return !p.empty() && p[0] <= MIN_LEN; },
        [](const std::vector<float> &p)
        {
            float l = p.empty() ? MIN_LEN : p[0];
            return Sentence{Symbol('@', {l * 3.5f})};
        }});

    Sentence axiom{Symbol('A', {1.0f})};
    Sentence result = sys.Generate(axiom, iters);

    std::cout << "[HybridPlant]  iter=" << iters << "  seed=" << seed << "  nodes=" << result.size() << "\n";
    return InterpretFull(result, 1.0f, 25.0f, out, maxNodes);
}

// =============================================================
//  Example 3 – ABOP Sympodial Tree
// =============================================================
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

// =============================================================
//  Example 4 – Custom Blooming Tree
// =============================================================
static int BuildCustomTree(int iters, PlantNode *out, int maxNodes, unsigned int seed)
{
    LSystem sys(seed);

    // We capture a random number generator to evaluate dynamic probabilities
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
                // GROWTH STATE: Branch gets thinner, spawns leaves and new sub-branches
                return Sentence{
                    Symbol('!', {r}),
                    Symbol('F', {l}),

                    // Small leaves along the stem
                    Symbol('['), Symbol('+', {50.f}), Symbol('~', {l * 0.4f}), Symbol(']'),
                    Symbol('['), Symbol('-', {50.f}), Symbol('~', {l * 0.4f}), Symbol(']'),

                    // Side Branch 1 (notice 'r' is scaled down by 0.6 to get thinner)
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

    // Axiom starts at depth 0 with a thick radius of 0.8
    Sentence axiom{Symbol('A', {5.0f, 0.8f, 0.0f})};
    Sentence result = sys.Generate(axiom, iters);

    std::cout << "[CustomTree]  iter=" << iters << "  seed=" << seed << "  nodes=" << result.size() << "\n";
    return InterpretFull(result, 1.0f, 25.0f, out, maxNodes);
}

// =============================================================
//  Exported C API
// =============================================================
extern "C"
{

    PLANTSIM_API int GeneratePlant(
        int exampleId, int iterations,
        PlantNode *outNodes, int maxNodes, unsigned int seed)
    {
        switch (exampleId)
        {
        case 0:
            return BuildParametricTree(iterations, outNodes, maxNodes, seed);
        case 1:
            return BuildStochasticShrub(iterations, outNodes, maxNodes, seed);
        case 2:
            return BuildHybridPlant(iterations, outNodes, maxNodes, seed);
        case 3:
            return BuildABOPTree(iterations, outNodes, maxNodes, seed);
        case 4:
            return BuildCustomTree(iterations, outNodes, maxNodes, seed);
        default:
            std::cerr << "[GeneratePlant] Unknown exampleId: " << exampleId << "\n";
            return 0;
        }
    }
}