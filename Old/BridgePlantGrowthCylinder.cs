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
    private MeshFilter mf;
    private MeshRenderer mr;

    private Vector3 lastTangent = Vector3.right;
    public enum TreeType
    {
        ParametricTree = 0,
        StochasticShrub = 1,
        HybridPlant = 2,
        ABOPTree = 3

}

    [Header("L-System")]
    public string axiom = "F";
    public int iterations = 1;
    public float angle = 25f;
    public float step = 0.5f;

    [Header("3D Branch Settings")]
    public float baseRadius = 0.05f;
    public int radialSegments = 12;
    public Material branchMaterial;

    [Header("Tree Type")]
    public TreeType treeType;
    public uint seed = 67;

[DllImport("libPlantSim", CallingConvention = CallingConvention.Cdecl)]
private static extern int GeneratePlant(
    int exampleId,
    int iterations,
    [Out] Segment[] outSegments,
    int maxSegments,
    uint seed
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
    }

    void Generate()
    {
        const int MAX_SEGMENTS = 100000;
        Segment[] segments = new Segment[MAX_SEGMENTS];

        int count = GeneratePlant(
            (int)treeType,
            iterations,
            segments,
            MAX_SEGMENTS,
            seed
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

            float current = segments[i].radius;
            float next = i < count - 1 ? segments[i + 1].radius : current;

            float radius = Mathf.Lerp(current, next, 0.5f);
            radius *= Mathf.Lerp(1f, 0.25f, i / (float)count);

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
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
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
        float overlap = radius * 0.5f;
        start -= axis * overlap;
        end += axis * overlap;

        Vector3 tangent = Vector3.Cross(lastTangent, axis);

        if (tangent.sqrMagnitude < 0.001f)
            tangent = Vector3.Cross(Vector3.up, axis);

        tangent.Normalize();
        lastTangent = tangent;

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

    Vector3 ToUnityVector(PlantBridge.Vec3 v)
    {
        return new Vector3(v.x, v.y, v.z);
    }
}