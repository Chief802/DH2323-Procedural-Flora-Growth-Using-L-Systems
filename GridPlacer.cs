using UnityEngine;

public class GridPlacer : MonoBehaviour
{
    [Header("Prefab")]
    public GameObject plantPrefab; 

    [Header("Grid Settings")]
    public int gridSizeX = 10;
    public int gridSizeY = 10;
    public float spacing = 10f;

    [Header("Seed Settings")]
    public bool randomizeSeeds = true;
    public uint baseSeed = 1;

    void Start()
    {
        GenerateGrid();
    }

    void GenerateGrid()
    {
        Random.InitState(0);
        Vector3 offset = new Vector3(
            (gridSizeX - 1) * spacing * 0.5f,
            0f,
            (gridSizeY - 1) * spacing * 0.5f
        );

        for (int x = 0; x < gridSizeX; x++)
        {
            for (int y = 0; y < gridSizeY; y++)
            {
                Vector3 position = new Vector3(x * spacing, 0f, y * spacing) - offset;

                GameObject obj = Instantiate(plantPrefab, position, Quaternion.identity, transform);

                var plant = obj.GetComponent<PlantRenderer>();
                if (plant != null)
                {
                    plant.seed = (uint)Random.Range(1, int.MaxValue);
                }
            }
        }
    }
}