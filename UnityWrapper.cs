using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;


public class PlantBridge : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public float x, y, z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Segment
    {
        public Vec3 a;
        public Vec3 b;
        public float radius;
    }

    [Header("L-System")]
    public string axiom = "F";
    public int iterations = 1;
    public float angle = 25f;
    public float step = 0.5f;

    [Header("3D Branch Settings")]
    public float baseRadius = 0.05f;
    public int radialSegments = 8;
    public Material branchMaterial;

    private MeshFilter mf;
    private MeshRenderer mr;


    // Required for Unity to understand the dll
    [DllImport("libPlantSim", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int GeneratePlantSegments(
        string axiom,
        int iterations,
        float angleDegrees,
        float stepLength,
        [Out] Segment[] outSegments,
        int maxSegments
    );

    // When you press start in Unity, run Start()
    void Start()
    {
        mf = GetComponent<MeshFilter>();
        mr = GetComponent<MeshRenderer>();

        if (branchMaterial == null)
        {
            if (mr.sharedMaterial != null)
            {
                branchMaterial = mr.sharedMaterial;
            }
            else
            {
                branchMaterial = new Material(Shader.Find("Standard"));
                branchMaterial.color = new Color(0.35f, 0.22f, 0.12f);
            }
        }

        mr.material = branchMaterial;

        Generate();
    }

    
    void Update()
    {
        bool spacePress = Keyboard.current.spaceKey.wasPressedThisFrame;
        if (spacePress)
        {
            // Iterate one additional stage
            iterations++;
            Debug.Log($"Iteration: {iterations}");
            Generate();
        }
        
        MoveCamera();
    }

    void Generate()
    {
        const int MAX_SEGMENTS = 100000;
        Segment[] segments = new Segment[MAX_SEGMENTS];

        int count = GeneratePlantSegments(
            axiom,
            iterations,
            angle,
            step,
            segments,
            MAX_SEGMENTS
        );

        if (count > MAX_SEGMENTS)
        {
            Debug.LogError("Buffer overflow risk!");
            count = MAX_SEGMENTS;
        }
        else if (count <= 0)
        {
            Debug.LogError("Plant generation failed or returned no segments.");
            return;
        }

       BuildBranchMesh(segments, count);
        UpdateCamera(segments, count);
        Debug.Log($"Generated {count} segments");
    }

    // Builds a 3D mesh
    void BuildBranchMesh(Segment[] segments, int count)
    {
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

    // Adds a Cylinder
      void AddCylinder(
        Vector3 start,
        Vector3 end,
        float radius,
        int sides,
        List<Vector3> vertices,
        List<int> triangles,
        List<Vector3> normals)
        {
            int startIndex = vertices.Count;

            Vector3 axis = (end - start).normalized;

            Vector3 tangent =
                Vector3.Cross(axis, Vector3.up).sqrMagnitude < 0.01f
                ? Vector3.Cross(axis, Vector3.right).normalized
                : Vector3.Cross(axis, Vector3.up).normalized;

            Vector3 bitangent = Vector3.Cross(axis, tangent).normalized;

            for (int i = 0; i < sides; i++)
            {
                float t = i / (float)sides * Mathf.PI * 2f;

                Vector3 circle =
                    Mathf.Cos(t) * tangent * radius +
                    Mathf.Sin(t) * bitangent * radius;

                vertices.Add(start + circle);
                vertices.Add(end + circle);

                Vector3 normal = circle.normalized;
                normals.Add(normal);
                normals.Add(normal);
            }

        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;

            int i0 = startIndex + i * 2;
            int i1 = startIndex + i * 2 + 1;
            int i2 = startIndex + next * 2;
            int i3 = startIndex + next * 2 + 1;

            triangles.Add(i0);
            triangles.Add(i2);
            triangles.Add(i1);

            triangles.Add(i1);
            triangles.Add(i2);
            triangles.Add(i3);
        }
    }

    void UpdateCamera(Segment[] segments, int count)
    {
        if (Camera.main == null || count == 0)
            return;

        Bounds bounds = new Bounds(ToUnityVector(segments[0].a), Vector3.zero);

        for (int i = 0; i < count; i++)
        {
            bounds.Encapsulate(ToUnityVector(segments[i].a));
            bounds.Encapsulate(ToUnityVector(segments[i].b));
        }

        float size = bounds.size.magnitude;
        float fov = Camera.main.fieldOfView;
        float distance =
            (size * 0.7f) /
            Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

        Vector3 dir = new Vector3(1, 0.7f, -1).normalized;

        Camera.main.transform.position =
            bounds.center + dir * distance;

        Camera.main.transform.LookAt(bounds.center);
    }

    void MoveCamera()
    {
        Vector3 direction = Vector3.zero;
        float moveSpeed = 5f;

        // Get input values
        float moveForward = 0f;
        float moveSideways = 0f;
        float moveVertical = 0f;

        if (Keyboard.current.wKey.isPressed) moveForward += 1;
        if (Keyboard.current.sKey.isPressed) moveForward -= 1;
        if (Keyboard.current.aKey.isPressed) moveSideways -= 1;
        if (Keyboard.current.dKey.isPressed) moveSideways += 1;
        if (Keyboard.current.eKey.isPressed) moveVertical += 1;
        if (Keyboard.current.qKey.isPressed) moveVertical -= 1;

        // Flatten the forward and right vectors so Y-rotation doesn't affect speed/direction
        Vector3 forward = transform.forward;
        Vector3 right = transform.right;
        forward.y = 0;
        right.y = 0;
        forward.Normalize();
        right.Normalize();

        // Calculate final direction
        direction = (forward * moveForward) + (right * moveSideways) + (Vector3.up * moveVertical);

        // Check if direction is not zero to avoid console warnings with .normalized
        if (direction.sqrMagnitude > 0.001f)
        {
            Camera.main.transform.position += direction.normalized * moveSpeed * Time.deltaTime;
        }
    }

    // Helper function for converting the PlantBridge class vector to a vector understandable by Unity
    Vector3 ToUnityVector(PlantBridge.Vec3 v)
    {
        return new Vector3(v.x, v.y, v.z);
    }

}