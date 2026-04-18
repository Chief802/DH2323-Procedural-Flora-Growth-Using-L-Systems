#define PLANTSIM_EXPORTS
#include "GenerateFlora3D.h"
#include <string>

#include <vector>
#include <stack>
#include <cmath>

#include <iostream>

struct TurtleState {
    Vec3 pos; // x, y, z;
    Vec3 U; // Direction up
    Vec3 L; // Direction to the left
    Vec3 H; // Heading
    float radius;    // Branch radius
};

std::string ApplyRules(const std::string& sentence) {
    std::string newSentence;
    for (char c : sentence) {
        // If you see an F, replace with the following
        /*
            Forward
            Branch: Pitch up, turn left, and go forward
            Branch: Pitch down, roll left, go forward, and pitch up twice
            Branch: Pitch down, roll right, and go forward
            Turn right
        */
        if (c == 'F') newSentence += "F[+&F]F[-/F]F";
        else newSentence += c;
    }
    return newSentence;
};

/// @brief Rotates a vector v around an axis by an angle
/// @param v 
/// @param axis 
/// @param angle 
/// @return 
Vec3 Rotate(Vec3 v, Vec3 axis, float angle) {
    float cosA = cos(angle);
    float sinA = sin(angle);

    float x = v.x * cosA + (axis.y * v.z - axis.z * v.y) * sinA;
    float y = v.y * cosA + (axis.z * v.x - axis.x * v.z) * sinA;
    float z = v.z * cosA + (axis.x * v.y - axis.y * v.x) * sinA;
    return {x, y, z};
}

void RotateTurtle(TurtleState& t, char op, float alpha) {
    // Rotational matrices for  U, L, and H
    /*
            cos a,  sin a   0
    R_U =   -sin a  cos a   0
            0       0       1

            cos a   0       -sin a
    R_L     0       1       0
            sin a   0       cos a

            1       0       0
    R_H     0   cos a       -sin a
            0   sin a       cos  a
    */ 
   Vec3 oldU = t.H;
   Vec3 oldL = t.L;
   Vec3 oldH = t.H;

   // Since the angle determines wether a left or right rotation takes place, 
   // each matrix of rotation only needs to be taken care of once
   switch (op)
   {
    case '+': // Turn Left (Rotate around U)
    case '-': // Turn Right
        t.H = { oldH.x * cos(alpha) + oldL.x * sin(alpha), oldH.y * cos(alpha) + oldL.y * sin(alpha), oldH.z * cos(alpha) + oldL.z * sin(alpha) };
        t.L = { -oldH.x * sin(alpha) + oldL.x * cos(alpha), -oldH.y * sin(alpha) + oldL.y * cos(alpha), -oldH.z * sin(alpha) + oldL.z * cos(alpha) };
        break;
    case '&': // Pitch Down (Rotate around L)
    case '^': // Pitch Up
        t.H = { oldH.x * cos(alpha) - oldU.x * sin(alpha), oldH.y * cos(alpha) - oldU.y * sin(alpha), oldH.z * cos(alpha) - oldU.z * sin(alpha) };
        t.U = { oldH.x * sin(alpha) + oldU.x * cos(alpha), oldH.y * sin(alpha) + oldU.y * cos(alpha), oldH.z * sin(alpha) + oldU.z * cos(alpha) };
        break;
    case '\\': // Roll Left (Rotate around H)
    case '/':  // Roll Right
        t.L = { oldL.x * cos(alpha) - oldU.x * sin(alpha), oldL.y * cos(alpha) - oldU.y * sin(alpha), oldL.z * cos(alpha) - oldU.z * sin(alpha) };
        t.U = { oldL.x * sin(alpha) + oldU.x * cos(alpha), oldL.y * sin(alpha) + oldU.y * cos(alpha), oldL.z * sin(alpha) + oldU.z * cos(alpha) };
        break;
   
   default:
    break;
   }
}

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
    // Angle in radians from input degrees
    float angleRad = angleDeg * 3.1415926f / 180.0f;

    // Initial state: Position at origin, H pointing Up (Y-axis), U pointing Forward (Z-axis)
    TurtleState turtle = {
        {0, 0, 0},      // Position
        {0, 0, -1},      // U (Up)
        {-1, 0, 0},     // L (Left)
        {0, 1, 0},      // H (Heading)
        0.2f            // Branch radius
    };

    std::stack<TurtleState> stack;

    int segmentCount = 0;

    for (char c : commands) {
        if (c == 'F') {
            Vec3 newPos = {
                turtle.pos.x + step * turtle.H.x,
                turtle.pos.y + step * turtle.H.y,
                turtle.pos.z + step * turtle.H.z
            };

            if (segmentCount < maxSegments) {
                outSegments[segmentCount++] = { turtle.pos, newPos, turtle.radius };
            }
            turtle.pos = newPos;
        }
        else if (c == '+') RotateTurtle(turtle, '+', angleRad);
        else if (c == '-') RotateTurtle(turtle, '+', -angleRad);
        else if (c == '&') RotateTurtle(turtle, '&', angleRad);
        else if (c == '^') RotateTurtle(turtle, '&', -angleRad);
        else if (c == '\\') RotateTurtle(turtle, '\\', angleRad);
        else if (c == '/') RotateTurtle(turtle, '\\', -angleRad);
        else if (c == '|') RotateTurtle(turtle, '+', 3.1415926f);
        else if (c == '[') {
            stack.push(turtle); 
            turtle.radius *= 0.707;
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