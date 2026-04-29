// =============================================================
//  GenerateFlora3D.cpp  –  C API implementation
//
//  All geometry goes through the same Sentence → InterpretFull
//  pipeline.  The resulting PlantNode stream carries NodeType tags
//  (Branch / Leaf / Flower) and per-node orientation frames, so the
//  Unity bridge can build separate meshes for each plant part.
// =============================================================
#define PLANTSIM_EXPORTS
#include "GenerateFlora3D.h"

#include <iostream>

// =============================================================
//  Example 0 – Parametric Tree
// =============================================================
/**
 * Single parametric rule:
 *   F(l) → F(l·R) [+(A)~(l·ls) F(l·r)] F(l·R) [-(A)~(l·ls) F(l·r)] F(l·R)
 *           when l > MIN_LEN
 *
 * '~' is emitted after each side branch, placing a leaf near the
 * fork.  A second terminal rule replaces a segment that has become
 * too short with a flower '@', so the outer canopy blooms.
 */
static int BuildParametricTree(int iters, PlantNode* out, int maxNodes, unsigned int)
{
    constexpr float TRUNK_R = 0.60f;
    constexpr float SIDE_R  = 0.50f;
    constexpr float LEAF_S  = 0.45f;   // leaf size relative to parent length
    constexpr float ANGLE   = 25.7f;
    constexpr float MIN_LEN = 0.04f;

    LSystem sys;   // deterministic – seed irrelevant

    // ── Growing rule ────────────────────────────────────────────────────────
    sys.AddRule(ProductionRule{
        'F', 1.0f,
        [](const std::vector<float>& p) { return p.empty() || p[0] > MIN_LEN; },
        [](const std::vector<float>& p) {
            float l = p.empty() ? 1.0f : p[0];
            return Sentence {
                Symbol('F', {l * TRUNK_R}),
                Symbol('['), Symbol('+', {ANGLE}),
                    Symbol('~', {l * LEAF_S}),          // leaf near left fork
                    Symbol('F', {l * SIDE_R}),
                Symbol(']'),
                Symbol('F', {l * TRUNK_R}),
                Symbol('['), Symbol('-', {ANGLE}),
                    Symbol('~', {l * LEAF_S}),          // leaf near right fork
                    Symbol('F', {l * SIDE_R}),
                Symbol(']'),
                Symbol('F', {l * TRUNK_R}),
            };
        }
    });

    // ── Terminal rule: replace tiny segments with flowers ────────────────────
    sys.AddRule(ProductionRule{
        'F', 1.0f,
        [](const std::vector<float>& p) { return !p.empty() && p[0] <= MIN_LEN; },
        [](const std::vector<float>& p) {
            float l = p.empty() ? MIN_LEN : p[0];
            return Sentence { Symbol('@', {l * 4.0f}) };   // flower at canopy tip
        }
    });

    Sentence axiom { Symbol('F', {1.0f}) };
    Sentence result = sys.Generate(axiom, iters);

    std::cout << "[ParametricTree]  iter=" << iters
              << "  nodes=" << result.size() << "\n";

    return InterpretFull(result, 1.0f, ANGLE, out, maxNodes);
}


// =============================================================
//  Example 1 – Stochastic Shrub
// =============================================================
/**
 * Three competing productions (sum p = 1.0):
 *   F → F[+F~]F[-F~]F   p = 0.34   dense, symmetric, leaves at tips
 *   F → F[+F~]F         p = 0.33   left-dominant
 *   F → F[-F~]F         p = 0.33   right-dominant
 *
 * '~' after each side branch places a leaf at its tip.
 * Different seeds produce recognisably different silhouettes.
 */
static int BuildStochasticShrub(int iters, PlantNode* out, int maxNodes, unsigned int seed)
{
    LSystem sys(seed);

    sys.AddRule('F', 0.34f, [](const std::vector<float>&) {
        return Sentence {
            {'F'}, {'['}, {'+'}, {'F'}, {'~'}, {']'},
            {'F'}, {'['}, {'-'}, {'F'}, {'~'}, {']'},
            {'F'}
        };
    });

    sys.AddRule('F', 0.33f, [](const std::vector<float>&) {
        return Sentence { {'F'}, {'['}, {'+'}, {'F'}, {'~'}, {']'}, {'F'} };
    });

    sys.AddRule('F', 0.33f, [](const std::vector<float>&) {
        return Sentence { {'F'}, {'['}, {'-'}, {'F'}, {'~'}, {']'}, {'F'} };
    });

    Sentence result = sys.Generate(MakeSentence("F"), iters);

    std::cout << "[StochasticShrub]  iter=" << iters
              << "  seed=" << seed
              << "  nodes=" << result.size() << "\n";

    return InterpretFull(result, 0.3f, 25.0f, out, maxNodes);
}


// =============================================================
//  Example 2 – Hybrid Plant  (parametric + stochastic)
// =============================================================
/**
 * Two competing parametric rules, each chosen with p = 0.5:
 *
 *   Rule A (upright split):
 *     F(l) → F(l·0.55) [+(30)~(l·0.4) F(l·0.45)]
 *             F(l·0.55) [-(30)~(l·0.4) F(l·0.45)] F(l·0.55)
 *
 *   Rule B (lean + 3-D twist):
 *     F(l) → F(l·0.6) [+(20)^(15) F(l·0.5)~(l·0.35)] F(l·0.6)
 *
 * Leaves appear near forks; a flower is placed at terminal segments.
 */
static int BuildHybridPlant(int iters, PlantNode* out, int maxNodes, unsigned int seed)
{
    constexpr float MIN_LEN = 0.05f;

    LSystem sys(seed);

    // Rule A – symmetric upright split
    sys.AddRule(ProductionRule{
        'F', 0.5f,
        [MIN_LEN](const std::vector<float>& p) { return p.empty() || p[0] > MIN_LEN; },
        [](const std::vector<float>& p) {
            float l = p.empty() ? 1.0f : p[0];
            return Sentence {
                Symbol('F', {l * 0.55f}),
                Symbol('['), Symbol('+', {30.f}),
                    Symbol('~', {l * 0.40f}),
                    Symbol('F', {l * 0.45f}),
                Symbol(']'),
                Symbol('F', {l * 0.55f}),
                Symbol('['), Symbol('-', {30.f}),
                    Symbol('~', {l * 0.40f}),
                    Symbol('F', {l * 0.45f}),
                Symbol(']'),
                Symbol('F', {l * 0.55f}),
            };
        }
    });

    // Rule B – lean with 3-D pitch twist
    sys.AddRule(ProductionRule{
        'F', 0.5f,
        [MIN_LEN](const std::vector<float>& p) { return p.empty() || p[0] > MIN_LEN; },
        [](const std::vector<float>& p) {
            float l = p.empty() ? 1.0f : p[0];
            return Sentence {
                Symbol('F', {l * 0.6f}),
                Symbol('['),
                    Symbol('+', {20.f}), Symbol('^', {15.f}),
                    Symbol('F', {l * 0.5f}),
                    Symbol('~', {l * 0.35f}),
                Symbol(']'),
                Symbol('F', {l * 0.6f}),
            };
        }
    });

    // Terminal rule – tiny segment → flower
    sys.AddRule(ProductionRule{
        'F', 1.0f,
        [MIN_LEN](const std::vector<float>& p) { return !p.empty() && p[0] <= MIN_LEN; },
        [](const std::vector<float>& p) {
            float l = p.empty() ? MIN_LEN : p[0];
            return Sentence { Symbol('@', {l * 3.5f}) };
        }
    });

    Sentence axiom { Symbol('F', {1.0f}) };
    Sentence result = sys.Generate(axiom, iters);

    std::cout << "[HybridPlant]  iter=" << iters
              << "  seed=" << seed
              << "  nodes=" << result.size() << "\n";

    return InterpretFull(result, 1.0f, 25.0f, out, maxNodes);
}


// =============================================================
//  Example 3 – ABOP Sympodial Tree  (Prusinkiewicz §2.6)
// =============================================================
/**
 * Three interacting parametric rules modelling a monopodial tree
 * with whorled branching:
 *
 *   p1: A → !(vr) F(50) [&(a) F(50) A ~(2.0)] /(d1)
 *                        [&(a) F(50) A ~(2.0)] /(d2)
 *                        [&(a) F(50) A ~(2.0)]
 *   p2: F(l) → F(l·lr)         (segment elongation per step)
 *   p3: !(w) → !(w·vr)         (radius fattening per step)
 *
 * '~' placed after each F(50)A arm puts a leaf near every branch apex.
 * A flower '@' is emitted at the meristem apex on each A expansion so
 * the very top of each branch cluster blooms.
 */
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
//  Exported C API
// =============================================================
extern "C" {

PLANTSIM_API int GeneratePlant(
    int exampleId, int iterations,
    PlantNode* outNodes, int maxNodes, unsigned int seed
) {
    switch (exampleId) {
    case 0: return BuildParametricTree(iterations, outNodes, maxNodes, seed);
    case 1: return BuildStochasticShrub(iterations, outNodes, maxNodes, seed);
    case 2: return BuildHybridPlant   (iterations, outNodes, maxNodes, seed);
    case 3: return BuildABOPTree      (iterations, outNodes, maxNodes, seed);
    default:
        std::cerr << "[GeneratePlant] Unknown exampleId: " << exampleId << "\n";
        return 0;
    }
}

} // extern "C"