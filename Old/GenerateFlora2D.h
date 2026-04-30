#pragma once

#ifdef PLANTSIM_EXPORTS
#define PLANTSIM_API __declspec(dllexport)
#else
#define PLANTSIM_API __declspec(dllimport)
#endif

extern "C" {

struct Vec3 {
    float x, y, z;
};

struct Segment {
    Vec3 a;
    Vec3 b;
};

PLANTSIM_API int GeneratePlantSegments(
    const char* axiom,
    int iterations,
    float angleDegrees,
    float stepLength,
    Segment* outSegments,
    int maxSegments
);

}