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

### Some Update Highlights
2026 April 12
The feedback to the first project specification draft was returned, allowing goals beyond the implementation aspect to be set.
The focus was consequently set on evaluating the algorithm and its implementation. This was investigated from the following perspectives
- Speed, in terms of how quickly flora could be generated
- Memory, in terms of how expensive a floral instance is
- Realism, in terms of how well the structure of real flora is captured by the L-System
These aspects were then taken from the individual level of one plant, to investigating how scaling to many more floral instances affect them.

2026 April 14
Before an evaluation can take place, there needs to be something to evaluate. The current short-term goal for the project was at this point to implement both a basic plant, 
and to be able to have it be shown in Unity.
