#include "LSystem.h"

#include <algorithm>
#include <iomanip>
#include <iostream>
#include <vector>

// =============================================================
//  Utilities
// =============================================================

static constexpr int MAX_SEG = 500'000;

/// Axis-aligned bounding box of a generated plant.
struct AABB
{
    float minY = 1e9f, maxY = -1e9f;
    float minX = 1e9f, maxX = -1e9f;
    float minZ = 1e9f, maxZ = -1e9f;

    float height() const { return maxY - minY; }
    float widthX() const { return maxX - minX; }
    float widthZ() const { return maxZ - minZ; }
};

static AABB ComputeAABB(const Segment *segs, int n)
{
    AABB b;
    for (int i = 0; i < n; ++i)
    {
        for (const Vec3 *v : {&segs[i].start, &segs[i].end})
        {
            b.minY = std::min(b.minY, v->y);
            b.maxY = std::max(b.maxY, v->y);
            b.minX = std::min(b.minX, v->x);
            b.maxX = std::max(b.maxX, v->x);
            b.minZ = std::min(b.minZ, v->z);
            b.maxZ = std::max(b.maxZ, v->z);
        }
    }
    return b;
}

static void PrintRow(const std::string &label, int segs, const AABB &bb)
{
    std::cout << "  " << std::left << std::setw(32) << label
              << "  segs=" << std::right << std::setw(6) << segs
              << "  h=" << std::fixed << std::setprecision(2)
              << std::setw(6) << bb.height()
              << "  w=" << std::setw(6) << std::max(bb.widthX(), bb.widthZ())
              << "\n";
}

// =============================================================
//  Example A – Parametric Tree
// =============================================================
/**
 * Rule:
 *   F(l) →  F(l·R) [+(A) F(l·r)] F(l·R) [-(A) F(l·r)] F(l·R)
 *           when l > MIN_LEN           (← parametric condition)
 *
 * Key ideas:
 *   1. The axiom seeds the length:  { F(1.0) }
 *   2. Every child receives l * ratio, encoding taper in the data
 *      rather than as external state.  The tree shrinks naturally.
 *   3. The per-symbol angle parameter ( +(25.7) ) lets different
 *      branches use different angles — try changing ANGLE_MAIN vs
 *      ANGLE_SIDE to create asymmetric canopies.
 */
static void RunParametricTree()
{
    std::cout << "\n╔══════════════════════════════════════════╗\n"
              << "║  Example A – Parametric Tree             ║\n"
              << "╚══════════════════════════════════════════╝\n"
              << "  Rule:  F(l) → F(l·0.6) [+(25.7) F(l·0.5)]\n"
              << "                F(l·0.6) [-(25.7) F(l·0.5)]\n"
              << "                F(l·0.6)    when l > 0.04\n\n";

    constexpr float TRUNK_R = 0.60f;
    constexpr float SIDE_R = 0.50f;
    constexpr float ANGLE_MAIN = 25.7f; // Classic Prusinkiewicz branching angle
    constexpr float MIN_LEN = 0.04f;

    LSystem sys; // Deterministic – seed is irrelevant

    sys.AddRule(ProductionRule{
        'F',
        1.0f,
        // Parametric guard: segments below the threshold are kept as-is,
        // acting as implicit leaf nodes.
        [](const std::vector<float> &p)
        {
            return p.empty() || p[0] > MIN_LEN;
        },
        // Parametric successor: children inherit geometrically scaled lengths.
        // Each symbol carries its length so the interpreter needs no
        // external scaling state.
        [](const std::vector<float> &p)
        {
            float l = p.empty() ? 1.0f : p[0];
            return Sentence{
                Symbol('F', {l * TRUNK_R}),
                Symbol('['),
                Symbol('+', {ANGLE_MAIN}),
                Symbol('F', {l * SIDE_R}),
                Symbol(']'),
                Symbol('F', {l * TRUNK_R}),
                Symbol('['),
                Symbol('-', {ANGLE_MAIN}),
                Symbol('F', {l * SIDE_R}),
                Symbol(']'),
                Symbol('F', {l * TRUNK_R}),
            };
        }});

    std::vector<Segment> buf(MAX_SEG);
    for (int iter : {2, 4, 6, 8})
    {
        Sentence s = sys.Generate({Symbol('F', {1.0f})}, iter);
        int n = Interpret(s, 1.0f, ANGLE_MAIN, buf.data(), MAX_SEG);
        PrintRow("iter " + std::to_string(iter) + "  (sentence=" + std::to_string(s.size()) + ")", n,
                 ComputeAABB(buf.data(), n));
    }
}

// =============================================================
//  Example B – Stochastic Shrub
// =============================================================
/**
 * Three competing productions for 'F':
 *
 *   F → F[+F]F[-F]F   p = 0.34   dense, symmetric
 *   F → F[+F]F        p = 0.33   left-dominant
 *   F → F[-F]F        p = 0.33   right-dominant
 *
 * Key ideas:
 *   1. All three rules share predecessor 'F'.  The LSystem engine
 *      does a weighted random draw at every rewriting step.
 *   2. The seed fully determines the draw sequence → any plant can
 *      be reproduced exactly from its seed alone.
 *   3. Different seeds produce recognisably different silhouettes
 *      from the same 3-rule grammar.
 */
static void RunStochasticShrub()
{
    std::cout << "\n╔══════════════════════════════════════════╗\n"
              << "║  Example B – Stochastic Shrub            ║\n"
              << "╚══════════════════════════════════════════╝\n"
              << "  Rules:  F → F[+F]F[-F]F  (p=0.34)\n"
              << "          F → F[+F]F       (p=0.33)\n"
              << "          F → F[-F]F       (p=0.33)\n\n";

    auto makeShrub = [](unsigned int seed)
    {
        LSystem sys(seed);

        sys.AddRule('F', 0.34f, [](const std::vector<float> &)
                    { return Sentence{
                          {'F'}, {'['}, {'+'}, {'F'}, {']'}, {'F'}, {'['}, {'-'}, {'F'}, {']'}, {'F'}}; });
        sys.AddRule('F', 0.33f, [](const std::vector<float> &)
                    { return Sentence{{'F'}, {'['}, {'+'}, {'F'}, {']'}, {'F'}}; });
        sys.AddRule('F', 0.33f, [](const std::vector<float> &)
                    { return Sentence{{'F'}, {'['}, {'-'}, {'F'}, {']'}, {'F'}}; });

        return sys;
    };

    std::vector<Segment> buf(MAX_SEG);
    for (unsigned seed : {1u, 42u, 999u, 12345u})
    {
        LSystem sys = makeShrub(seed);
        Sentence s = sys.Generate(MakeSentence("F"), /*iterations=*/5);
        int n = Interpret(s, 0.3f, 25.0f, buf.data(), MAX_SEG);
        PrintRow("seed " + std::to_string(seed) + "  (sentence=" + std::to_string(s.size()) + ")", n,
                 ComputeAABB(buf.data(), n));
    }
}

// =============================================================
//  Example C – Hybrid: Parametric + Stochastic
// =============================================================
/**
 * Two competing parametric productions for 'F(l)':
 *
 *   F(l) →  F(l·0.55) [+(30) F(l·0.45)] F(l·0.55) [-(30) F(l·0.45)] F(l·0.55)
 *           p = 0.5  (three-way split, fairly upright)
 *
 *   F(l) →  F(l·0.6) [+(20) ^(15) F(l·0.5)] F(l·0.6)
 *           p = 0.5  (lean with a twist – 3-D pitch)
 *
 * Key ideas:
 *   1. Both rules are stochastic (chosen randomly each step).
 *   2. Both rules are parametric (children carry scaled lengths).
 *   3. The second rule uses a 3-D combination of turn and pitch,
 *      demonstrating that per-symbol angles can mix rotation axes.
 *   4. The parametric condition still guards against micro-segments.
 */
static void RunHybridPlant()
{
    std::cout << "\n╔══════════════════════════════════════════╗\n"
              << "║  Example C – Hybrid (Parametric+Stoch.)  ║\n"
              << "╚══════════════════════════════════════════╝\n"
              << "  Rules:  F(l) → 3-way split  (p=0.5)\n"
              << "          F(l) → lean+twist   (p=0.5)\n"
              << "  Both rules are parametric AND stochastic.\n\n";

    constexpr float MIN_LEN = 0.05f;

    auto makeHybrid = [MIN_LEN](unsigned int seed)
    {
        LSystem sys(seed);

        // Rule 1 – three-way split (upright habit)
        sys.AddRule(ProductionRule{
            'F', 0.5f,
            [MIN_LEN](const std::vector<float> &p)
            { return p.empty() || p[0] > MIN_LEN; },
            [](const std::vector<float> &p)
            {
                float l = p.empty() ? 1.0f : p[0];
                return Sentence{
                    Symbol('F', {l * 0.55f}),
                    Symbol('['),
                    Symbol('+', {30.f}),
                    Symbol('F', {l * 0.45f}),
                    Symbol(']'),
                    Symbol('F', {l * 0.55f}),
                    Symbol('['),
                    Symbol('-', {30.f}),
                    Symbol('F', {l * 0.45f}),
                    Symbol(']'),
                    Symbol('F', {l * 0.55f}),
                };
            }});

        // Rule 2 – lean with pitch twist (spreading 3-D habit)
        sys.AddRule(ProductionRule{
            'F', 0.5f,
            [MIN_LEN](const std::vector<float> &p)
            { return p.empty() || p[0] > MIN_LEN; },
            [](const std::vector<float> &p)
            {
                float l = p.empty() ? 1.0f : p[0];
                // Branch turns left AND pitches upward (+20° turn, ^15° pitch)
                return Sentence{
                    Symbol('F', {l * 0.6f}),
                    Symbol('['),
                    Symbol('+', {20.f}),
                    Symbol('^', {15.f}),
                    Symbol('F', {l * 0.5f}),
                    Symbol(']'),
                    Symbol('F', {l * 0.6f}),
                };
            }});

        return sys;
    };

    std::vector<Segment> buf(MAX_SEG);
    for (unsigned seed : {7u, 77u, 777u, 7777u})
    {
        LSystem sys = makeHybrid(seed);
        Sentence s = sys.Generate({Symbol('F', {1.0f})}, /*iterations=*/5);
        int n = Interpret(s, 1.0f, 25.0f, buf.data(), MAX_SEG);
        PrintRow("seed " + std::to_string(seed) + "  (sentence=" + std::to_string(s.size()) + ")", n,
                 ComputeAABB(buf.data(), n));
    }
}

// =============================================================
//  main
// =============================================================
int main()
{

    RunParametricTree();
    RunStochasticShrub();
    RunHybridPlant();

    std::cout << "\nDone.\n";
    return 0;
}
