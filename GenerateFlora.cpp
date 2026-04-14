#define PLANTSIM_EXPORTS
#include "GenerateFlora.h"
#include <string>

#include <vector>
#include <stack>
#include <cmath>

#include <iostream>

struct TurtleState {
    float x, y, z;
    float angle; 
};

std::string ApplyRules(const std::string& sentence) {
    std::string newSentence;
    for (char c : sentence) {
        if (c == 'F') newSentence += "F[+F]F[-F]F";
        else newSentence += c;
    }
    return newSentence;
};

std::string GenerateLSystem(const std::string& axiom, int iterations) {
    std::string current = axiom;

    for (int i = 0; i < iterations; i++) {
        current = ApplyRules(current);
    }

    return current;
};

int Interpret(
    const std::string& commands,
    float step,
    float angleDeg,
    Segment* outSegments,
    int maxSegments
) {
    float angleRad = angleDeg * 3.1415926f / 180.0f;

    TurtleState turtle = {0, 0, 0, 3.1415926f / 2.0f};
    std::stack<TurtleState> stack;

    int segmentCount = 0;

    for (char c : commands) {
        if (c == 'F') {
            float newX = turtle.x + step * cos(turtle.angle);
            float newY = turtle.y + step * sin(turtle.angle);

            if (segmentCount < maxSegments) {
                outSegments[segmentCount++] = {
                    {turtle.x, turtle.y, 0},
                    {newX, newY, 0}
                };
            }

            turtle.x = newX;
            turtle.y = newY;
        }
        else if (c == '+') {
            turtle.angle += angleRad;
        }
        else if (c == '-') {
            turtle.angle -= angleRad;
        }
        else if (c == '[') {
            stack.push(turtle);
        }
        else if (c == ']') {
            if (!stack.empty()) {
                turtle = stack.top();
                stack.pop();
            }
        }
    }

    return segmentCount;
}

extern "C" PLANTSIM_API int GeneratePlantSegments(
    const char* axiom,
    int iterations,
    float angleDegrees,
    float stepLength,
    Segment* outSegments,
    int maxSegments

) {
    std::string sentence = GenerateLSystem(axiom, iterations);
    std::cout << "Generated string length: " << sentence.length() << std::endl;
    return Interpret(
        sentence,
        stepLength,
        angleDegrees,
        outSegments,
        maxSegments
    );
}