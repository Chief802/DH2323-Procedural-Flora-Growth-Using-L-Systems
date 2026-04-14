using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;


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
    }

    public string axiom = "F";
    public int iterations = 0;
    public float angle = 25f;
    public float step = 0.5f;
    private LineRenderer lr;

    [DllImport("libPlantSim", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int GeneratePlantSegments(
        string axiom,
        int iterations,
        float angleDegrees,
        float stepLength,
        [Out] Segment[] outSegments,
        int maxSegments
    );

    void Start()
    {
        Generate();
    }

    void Generate()
    {
        const int MAX_SEGMENTS = 10000;
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

        DrawWithLineRenderer(segments, count);
        UpdateCamera(segments, count);
        Debug.Log($"Generated {count} segments");
    }

    void Update()
    {
        if (Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            iterations++;
            Debug.Log($"Iteration: {iterations}");
            Generate();
        }
    }

    void UpdateCamera(Segment[] segments, int count)
    {
        if (segments == null || count == 0 || Camera.main == null)
            return;

        // Compute bounds of plant
        Bounds bounds = new Bounds(ToUnityVector(segments[0].a), Vector3.zero);

        for (int i = 0; i < count; i++)
        {
            bounds.Encapsulate(ToUnityVector(segments[i].a));
            bounds.Encapsulate(ToUnityVector(segments[i].b));
        }

        // Center camera on plant
        Camera.main.transform.position = new Vector3(
            bounds.center.x,
            bounds.center.y,
            Camera.main.transform.position.z
        );

        // Adjust zoom
        float padding = 1.2f;

        float sizeX = bounds.size.x;
        float sizeY = bounds.size.y;

        Camera.main.orthographicSize =
            Mathf.Max(sizeX, sizeY) * 0.5f * padding;
    }

    Vector3 ToUnityVector(PlantBridge.Vec3 v)
    {
        return new Vector3(v.x, v.y, v.z);
    }
    void DrawWithLineRenderer(Segment[] segments, int count)
    {
        if (lr == null)
        {
            lr = gameObject.AddComponent<LineRenderer>();

            lr.material = new Material(Shader.Find("Unlit/Color"));
            lr.startColor = Color.green;
            lr.endColor = Color.green;
            lr.widthMultiplier = 0.02f;
            lr.useWorldSpace = true;
        }

        lr.positionCount = count * 2;

        Vector3[] points = new Vector3[count * 2];

        for (int i = 0; i < count; i++)
        {
            points[i * 2] = new Vector3(
                segments[i].a.x,
                segments[i].a.y,
                segments[i].a.z
            );

            points[i * 2 + 1] = new Vector3(
                segments[i].b.x,
                segments[i].b.y,
                segments[i].b.z
            );
        }

        lr.SetPositions(points);
    }

}