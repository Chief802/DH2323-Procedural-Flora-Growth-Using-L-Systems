#pragma once

#include <cmath>
#include <functional>
#include <random>
#include <stack>
#include <string>
#include <vector>

// == Geometry primitives =====================================================
struct Vec3
{
    float x, y, z;
};
struct Segment
{
    Vec3 start, end;
    float radius;
}; // kept for backward compat

// == Plant-node type tag =====================================================
enum class NodeType : int
{
    Branch = 0,
    Leaf = 1,
    Flower = 2
};

/**
 * One output element produced by InterpretFull().
 *
 * Memory layout (56 bytes, no internal padding, matches C# StructLayout.Sequential):
 *   origin   Vec3   12 B   branch base, or position of leaf / flower
 *   end      Vec3   12 B   branch tip  (equals origin for leaf / flower)
 *   heading  Vec3   12 B   turtle H at emission time  (branch forward / leaf face normal)
 *   left     Vec3   12 B   turtle L at emission time  (leaf lateral axis)
 *   radius   float   4 B   branch cross-section radius, or leaf / flower billboard size
 *   type     int     4 B   NodeType cast to int
 */
struct PlantNode
{
    Vec3 origin;
    Vec3 end;
    Vec3 heading; // turtle H
    Vec3 left;    // turtle L
    float radius;
    NodeType type;
};

// =============================================================
//  Symbol
// =============================================================
/**
 * One token in an L-System sentence.
 *
 * Non-parametric:  Symbol('F')
 * Parametric:      Symbol('F', {0.75f})
 *
 * Turtle symbol conventions used in this library:
 *   'F'  params[0] = step length                  (default = defaultStep)
 *   '+' '-' '&' '^' '\\' '/'
 *        params[0] = angle override in degrees     (default = defaultAngle)
 *   '!'  params[0] = absolute radius value
 *   '~'  params[0] = leaf size
 *   '@'  params[0] = flower size
 */
struct Symbol
{
    char letter;
    std::vector<float> params;

    Symbol(char c) : letter(c) {}
    Symbol(char c, std::initializer_list<float> init) : letter(c), params(init) {}
    Symbol(char c, std::vector<float> p) : letter(c), params(std::move(p)) {}

    float param(std::size_t i, float defaultVal = 1.0f) const
    {
        return i < params.size() ? params[i] : defaultVal;
    }
};

using Sentence = std::vector<Symbol>;

inline Sentence MakeSentence(const std::string &s)
{
    Sentence out;
    out.reserve(s.size());
    for (char c : s)
        out.emplace_back(c);
    return out;
}

// =============================================================
//  ProductionRule
// =============================================================
/**
 * predecessor → successor, optionally guarded by a condition and/or
 * weighted for stochastic selection.
 *
 * Stochastic invariant: the probabilities of all rules that share a
 * predecessor (and whose condition is satisfied) should sum to 1.0.
 */
struct ProductionRule
{
    char predecessor;
    float probability; // Weight used for stochastic selection.  1.0 = deterministic.

    // Optional guard. nullptr means always eligible.
    std::function<bool(const std::vector<float> &)> condition;

    // Produces the replacement sentence from the symbol's runtime parameters.
    std::function<Sentence(const std::vector<float> &)> successor;
};

// =============================================================
//  LSystem
// =============================================================
class LSystem
{
public:
    explicit LSystem(unsigned int seed = 42u) : rng_(seed) {}

    void SetSeed(unsigned int seed) { rng_.seed(seed); }

    // == Rule registration ====================================================

    void AddRule(ProductionRule r) { rules_.push_back(std::move(r)); }

    // Deterministic + unconditional shorthand
    void AddRule(char c, std::function<Sentence(const std::vector<float> &)> succ)
    {
        rules_.push_back({c, 1.0f, nullptr, std::move(succ)});
    }

    // Stochastic + unconditional shorthand
    void AddRule(char c, float prob,
                 std::function<Sentence(const std::vector<float> &)> succ)
    {
        rules_.push_back({c, prob, nullptr, std::move(succ)});
    }

    // == Derivation ===========================================================

    /**
     * One rewriting pass: replace every symbol according to the matching rule.
     * Symbols with no eligible rule are kept unchanged (identity production).
     */
    Sentence Step(const Sentence &in)
    {
        Sentence out;
        out.reserve(in.size() * 4);

        for (const Symbol &sym : in)
        {
            struct Candidate
            {
                float prob;
                const ProductionRule *rule;
            };
            std::vector<Candidate> cands;
            float total = 0.f;

            for (const auto &r : rules_)
            {
                if (r.predecessor != sym.letter)
                    continue;
                if (r.condition && !r.condition(sym.params))
                    continue;
                cands.push_back({r.probability, &r});
                total += r.probability;
            }

            if (cands.empty())
            {
                out.push_back(sym);
                continue;
            }

            // Weighted random selection (degenerates to deterministic if one candidate)
            const ProductionRule *chosen = cands.back().rule;
            if (cands.size() == 1u)
            {
                chosen = cands[0].rule;
            }
            else
            {
                float roll = std::uniform_real_distribution<float>(0.f, total)(rng_);
                float cum = 0.f;
                for (auto &c : cands)
                {
                    cum += c.prob;
                    if (roll <= cum)
                    {
                        chosen = c.rule;
                        break;
                    }
                }
            }

            Sentence sub = chosen->successor(sym.params);
            out.insert(out.end(), sub.begin(), sub.end());
        }
        return out;
    }

    Sentence Generate(const Sentence &axiom, int iterations)
    {
        Sentence cur = axiom;
        for (int i = 0; i < iterations; ++i)
            cur = Step(cur);
        return cur;
    }

private:
    std::vector<ProductionRule> rules_;
    std::mt19937 rng_;
};

// =============================================================
//  Turtle interpreter — shared state
// =============================================================
namespace detail
{

    struct TurtleState
    {
        Vec3 pos;
        Vec3 U; // Up
        Vec3 L; // Left
        Vec3 H; // Heading (forward)
        float radius;
    };

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

} // namespace detail

// =============================================================
//  InterpretFull  –  primary interpreter, emits PlantNode
// =============================================================
/**
 * Walks the sentence with a 3-D turtle and writes PlantNode values.
 *
 * Symbol semantics (parametric overrides in parentheses):
 *   F(l)   Draw forward, step = l or defaultStep              → Branch node
 *   f(l)   Move forward, no geometry
 *   ~(s)   Emit a Leaf  at current position, size = s
 *   @(s)   Emit a Flower at current position, size = s
 *   !(w)   Set current radius to w  (absolute, not relative)
 *   + -    Turn   left / right    (param[0] = angle override °)
 *   & ^    Pitch  down / up
 *   \ /    Roll   left / right
 *   |      U-turn (180°)
 *   [ ]    Push / pop turtle state;  radius × √½ on push (pipe model)
 *
 * @return  Number of PlantNode values written (<= maxNodes).
 */
inline int InterpretFull(
    const Sentence &commands,
    float defaultStep,
    float defaultAngleDeg,
    PlantNode *outNodes,
    int maxNodes)
{
    using namespace detail;
    constexpr float kPi = 3.14159265f;
    const float kDefaultAngleRad = defaultAngleDeg * kPi / 180.f;

    TurtleState turtle = {
        {0.f, 0.f, 0.f},  // pos
        {0.f, 0.f, -1.f}, // U
        {-1.f, 0.f, 0.f}, // L
        {0.f, 1.f, 0.f},  // H  (+Y = upward growth)
        0.2f              // radius
    };

    std::stack<TurtleState> stk;
    int count = 0;

    auto angleOf = [&](const Symbol &s) -> float
    {
        return s.params.empty() ? kDefaultAngleRad : s.params[0] * kPi / 180.f;
    };

    auto emit = [&](NodeType t, float r, const Vec3 &end)
    {
        if (count < maxNodes)
            outNodes[count++] = {turtle.pos, end, turtle.H, turtle.L, r, t};
    };

    for (const Symbol &sym : commands)
    {
        switch (sym.letter)
        {

        case 'F':
        {
            float step = sym.param(0, defaultStep);
            Vec3 np = {
                turtle.pos.x + step * turtle.H.x,
                turtle.pos.y + step * turtle.H.y,
                turtle.pos.z + step * turtle.H.z};
            emit(NodeType::Branch, turtle.radius, np);
            turtle.pos = np;
            break;
        }

        case 'f':
        {
            float step = sym.param(0, defaultStep);
            turtle.pos.x += step * turtle.H.x;
            turtle.pos.y += step * turtle.H.y;
            turtle.pos.z += step * turtle.H.z;
            break;
        }

        case '~': // Leaf
            emit(NodeType::Leaf, sym.param(0, 0.3f), turtle.pos);
            break;

        case '@': // Flower
            emit(NodeType::Flower, sym.param(0, 0.15f), turtle.pos);
            break;

        case '!': // Set radius
            turtle.radius = sym.param(0, turtle.radius);
            break;

        case '+':
            RotateTurtle(turtle, 'U', angleOf(sym));
            break;
        case '-':
            RotateTurtle(turtle, 'U', -angleOf(sym));
            break;
        case '&':
            RotateTurtle(turtle, 'L', angleOf(sym));
            break;
        case '^':
            RotateTurtle(turtle, 'L', -angleOf(sym));
            break;
        case '\\':
            RotateTurtle(turtle, 'H', angleOf(sym));
            break;
        case '/':
            RotateTurtle(turtle, 'H', -angleOf(sym));
            break;
        case '|':
            RotateTurtle(turtle, 'U', kPi);
            break;

        case '[':
            stk.push(turtle);
            turtle.radius *= 0.7071f; // Each branches descreases by a factor of 1/sqrt(2)
            break;
        case ']':
            if (!stk.empty())
            {
                turtle = stk.top();
                stk.pop();
            }
            break;

        default:
            break;
        }
    }
    return count;
}