# Real-Time Procedural Flora Growth Using L-Systems

## Abstract
This paper extends the methods for generating flora using L-Systems as described in *The Algorithmic Beauty of Plants* by Przemyslaw Prusinkiewicz and Aristid Lindenmayer. 
This is done by investigating the efficacy of using methods described to generate a wide array of unique instances of flora in real-time using efficient construction 
and rendering techniques. 

Supervisor: [Professor and director of the Embodied Social Agents Lab (ESAL) Dr, Christopher Peters](https://www.kth.se/profile/chpeters)

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

### INTERMISSION 1 - How does a basic L-System work?
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
Although there is value in having a 2D implementation (more on that later), the goal was always to expand to 3D. This meant that two things needed to be changed:
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
