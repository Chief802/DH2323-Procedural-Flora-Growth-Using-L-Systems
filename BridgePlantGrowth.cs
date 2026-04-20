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
    public int radialSegments = 12;
    public Material branchMaterial;

    private MeshFilter mf;
    private MeshRenderer mr;

    private Vector3 lastTangent = Vector3.right;

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

        BuildContinuousTubeMesh(segments, count);
        Debug.Log($"Generated {count} segments");
    }

    // Builds a 3D Tubular Mesh / Generalized Cylinder / Swept Surface
    void BuildContinuousTubeMesh(Segment[] segments, int count)
    {
        List<List<Segment>> branches = ExtractBranches(segments, count);

        CombineInstance[] combine = new CombineInstance[branches.Count];

        for (int i = 0; i < branches.Count; i++)
        {
            Mesh branchMesh = BuildTubeForBranch(branches[i]);

            combine[i] = new CombineInstance
            {
                mesh = branchMesh,
                transform = Matrix4x4.identity
            };
        }

        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combine, true, false);

        mf.mesh = finalMesh;
    }

    // Separates branches
    List<List<Segment>> ExtractBranches(Segment[] segments, int count)
    {
        List<List<Segment>> result = new List<List<Segment>>();

        HashSet<int> used = new HashSet<int>();

        for (int i = 0; i < count; i++)
        {
            if (used.Contains(i))
                continue;

            List<Segment> branch = new List<Segment>();
            branch.Add(segments[i]);
            used.Add(i);

            Vector3 currentEnd = ToUnityVector(segments[i].b);

            bool extended = true;

            while (extended)
            {
                extended = false;

                for (int j = 0; j < count; j++)
                {
                    if (used.Contains(j))
                        continue;

                    Vector3 a = ToUnityVector(segments[j].a);

                    if (Vector3.Distance(a, currentEnd) < 0.001f)
                    {
                        branch.Add(segments[j]);
                        used.Add(j);
                        currentEnd = ToUnityVector(segments[j].b);
                        extended = true;
                        break;
                    }
                }
            }

            result.Add(branch);
        }

        return result;
    }

    // Builds the structure of each branch separately
    Mesh BuildTubeForBranch(List<Segment> branch)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector3> normals = new List<Vector3>();

        List<Vector3> path = new List<Vector3>();
        List<float> radii = new List<float>();

        path.Add(ToUnityVector(branch[0].a));
        radii.Add(branch[0].radius);

        foreach (var seg in branch)
        {
            path.Add(ToUnityVector(seg.b));
            radii.Add(seg.radius);
        }

        Vector3 lastNormal = Vector3.up;

        for (int i = 0; i < path.Count; i++)
        {
            Vector3 forward;

            if (i == 0)
                forward = (path[1] - path[0]).normalized;
            else if (i == path.Count - 1)
                forward = (path[i] - path[i - 1]).normalized;
            else
                forward = (path[i + 1] - path[i - 1]).normalized;

            Vector3 tangent = Vector3.Cross(lastNormal, forward);

            if (tangent.sqrMagnitude < 0.001f)
                tangent = Vector3.Cross(Vector3.right, forward);

            tangent.Normalize();

            Vector3 normal = Vector3.Cross(forward, tangent).normalized;
            lastNormal = normal;

            float radius = radii[i];

            for (int s = 0; s < radialSegments; s++)
            {
                float angle = s / (float)radialSegments * Mathf.PI * 2f;

                Vector3 offset =
                    Mathf.Cos(angle) * tangent * radius +
                    Mathf.Sin(angle) * normal * radius;

                vertices.Add(path[i] + offset);
                normals.Add(offset.normalized);
            }
        }

        for (int r = 0; r < path.Count - 1; r++)
        {
            int ring = r * radialSegments;
            int nextRing = (r + 1) * radialSegments;

            for (int s = 0; s < radialSegments; s++)
            {
                int next = (s + 1) % radialSegments;

                int a = ring + s;
                int b = ring + next;
                int c = nextRing + s;
                int d = nextRing + next;

                triangles.Add(a);
                triangles.Add(c);
                triangles.Add(b);

                triangles.Add(b);
                triangles.Add(c);
                triangles.Add(d);
            }
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetNormals(normals);
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();

        return mesh;
    }

    Vector3 ToUnityVector(PlantBridge.Vec3 v)
    {
        return new Vector3(v.x, v.y, v.z);
    }
}