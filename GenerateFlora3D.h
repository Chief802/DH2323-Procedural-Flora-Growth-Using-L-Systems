// =============================================================
//  GenerateFlora3D.h  –  Public C API
//
//  Single exported entry point: GeneratePlant().
//  Returns a flat array of PlantNode values that the caller
//  (e.g. the Unity bridge) can partition by NodeType and build
//  separate meshes for branches, leaves, and flowers.
// =============================================================
#pragma once

#include "LSystem.h"   // Vec3, Segment, PlantNode, NodeType, and the C++ engine

#if defined(_WIN32) || defined(_WIN64)
#  ifdef PLANTSIM_EXPORTS
#    define PLANTSIM_API __declspec(dllexport)
#  else
#    define PLANTSIM_API __declspec(dllimport)
#  endif
#else
#  define PLANTSIM_API __attribute__((visibility("default")))
#endif

extern "C" {

/**
 * Generate one of the built-in L-System plants and write the resulting
 * PlantNode stream to outNodes.  The stream is partitioned by NodeType:
 *
 *   exampleId 0 – Parametric Tree
 *     Deterministic branching; branch lengths taper geometrically via
 *     a parametric condition.  Flower nodes appear at terminal segments.
 *
 *   exampleId 1 – Stochastic Shrub
 *     Three competing productions chosen at random each step.  Leaf
 *     nodes appear at branch tips.  Different seeds → different shapes.
 *
 *   exampleId 2 – Hybrid Plant  (parametric + stochastic)
 *     Two competing parametric rules that both carry scaled child lengths
 *     and compete for selection.  Leaf and flower nodes included.
 *
 *   exampleId 3 – ABOP Sympodial Tree  (Prusinkiewicz & Lindenmayer §2.6)
 *     Whorled branching with geometric segment elongation (F(l) → F(l·lr))
 *     and radius driven by the pipe model (!(w) → !(w·vr)).
 *     Leaf nodes distributed along each arm; flower at the apex.
 *
 * @param exampleId   0–3 (see above)
 * @param iterations  Number of L-System derivation steps
 * @param outNodes    Caller-allocated output buffer
 * @param maxNodes    Capacity of outNodes
 * @param seed        RNG seed (affects stochastic examples; ignored for example 0)
 * @return            Number of PlantNode values written, or 0 on error.
 */
PLANTSIM_API int GeneratePlant(
    int          exampleId,
    int          iterations,
    PlantNode*   outNodes,
    int          maxNodes,
    unsigned int seed
);

} // extern "C"