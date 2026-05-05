using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public sealed class BoxColliderBakerWindow : EditorWindow
{
    private const string ContainerName = "__Generated_Box_Colliders";

    [SerializeField] private GameObject root;
    [SerializeField] private float cellSize = 0.2f;
    [SerializeField] private float surfaceThickness = 0.12f;
    [SerializeField] private int maxColliders = 800;
    [SerializeField] private bool includeInactive;
    [SerializeField] private bool clearPrevious = true;
    [SerializeField] private bool isTrigger;

    [MenuItem("Tools/Tiny Thieves/Box Collider Baker")]
    public static void Open()
    {
        BoxColliderBakerWindow window = GetWindow<BoxColliderBakerWindow>("Box Collider Baker");
        window.root = Selection.activeGameObject;
        window.Show();
    }

    [MenuItem("Tools/Tiny Thieves/Generate Box Colliders From Selection")]
    public static void GenerateFromSelection()
    {
        if (Selection.activeGameObject == null)
        {
            EditorUtility.DisplayDialog("Box Collider Baker", "Select the house or model root first.", "OK");
            return;
        }

        Generate(Selection.activeGameObject, 0.2f, 0.12f, 800, false, true, false);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Generate Box Colliders From Meshes", EditorStyles.boldLabel);
        root = (GameObject)EditorGUILayout.ObjectField("Root", root, typeof(GameObject), true);

        EditorGUILayout.Space();
        cellSize = EditorGUILayout.FloatField("Cell Size", cellSize);
        surfaceThickness = EditorGUILayout.FloatField("Surface Thickness", surfaceThickness);
        maxColliders = EditorGUILayout.IntField("Max Colliders", maxColliders);
        includeInactive = EditorGUILayout.Toggle("Include Inactive Meshes", includeInactive);
        clearPrevious = EditorGUILayout.Toggle("Clear Previous", clearPrevious);
        isTrigger = EditorGUILayout.Toggle("Is Trigger", isTrigger);

        EditorGUILayout.HelpBox(
            "Small Cell Size = more precise openings, but more colliders. Good starting values for a toy-scale house: Cell Size 0.12-0.25 and Surface Thickness 0.08-0.18.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(root == null))
        {
            if (GUILayout.Button("Generate Box Colliders"))
            {
                Generate(root, cellSize, surfaceThickness, maxColliders, includeInactive, clearPrevious, isTrigger);
            }

            if (GUILayout.Button("Clear Generated Colliders"))
            {
                ClearGenerated(root);
            }
        }
    }

    private static void Generate(
        GameObject rootObject,
        float requestedCellSize,
        float requestedThickness,
        int requestedMaxColliders,
        bool includeInactiveMeshes,
        bool shouldClearPrevious,
        bool generatedAsTrigger)
    {
        if (rootObject == null)
        {
            return;
        }

        float size = Mathf.Max(0.01f, requestedCellSize);
        int thicknessInCells = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(0.01f, requestedThickness) / size));
        int colliderLimit = Mathf.Max(1, requestedMaxColliders);

        if (shouldClearPrevious)
        {
            ClearGenerated(rootObject);
        }

        MeshFilter[] meshFilters = rootObject.GetComponentsInChildren<MeshFilter>(includeInactiveMeshes);
        if (meshFilters.Length == 0)
        {
            EditorUtility.DisplayDialog("Box Collider Baker", "No MeshFilter found under the selected object.", "OK");
            return;
        }

        Dictionary<Vector3Int, bool> occupiedCells = new Dictionary<Vector3Int, bool>();
        Matrix4x4 rootWorldToLocal = rootObject.transform.worldToLocalMatrix;

        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter filter = meshFilters[i];
            Mesh mesh = filter.sharedMesh;
            if (mesh == null)
            {
                continue;
            }

            Matrix4x4 meshToRoot = rootWorldToLocal * filter.transform.localToWorldMatrix;
            AddMeshSurfaceCells(mesh, meshToRoot, size, thicknessInCells, occupiedCells);
        }

        if (occupiedCells.Count == 0)
        {
            EditorUtility.DisplayDialog("Box Collider Baker", "No occupied cells were generated. Try a larger Surface Thickness.", "OK");
            return;
        }

        List<BoxInt> boxes = MergeCells(occupiedCells, colliderLimit);
        GameObject container = new GameObject(ContainerName);
        Undo.RegisterCreatedObjectUndo(container, "Generate Box Colliders");
        container.transform.SetParent(rootObject.transform, false);
        container.transform.localPosition = Vector3.zero;
        container.transform.localRotation = Quaternion.identity;
        container.transform.localScale = Vector3.one;

        for (int i = 0; i < boxes.Count; i++)
        {
            BoxInt box = boxes[i];
            GameObject child = new GameObject("Box Collider " + i.ToString("000"));
            Undo.RegisterCreatedObjectUndo(child, "Generate Box Colliders");
            child.transform.SetParent(container.transform, false);

            BoxCollider collider = Undo.AddComponent<BoxCollider>(child);
            collider.isTrigger = generatedAsTrigger;
            collider.center = (ToVector3(box.Min) + ToVector3(box.Max + Vector3Int.one)) * size * 0.5f;
            collider.size = ToVector3(box.Size) * size;
        }

        Selection.activeGameObject = container;
        EditorUtility.DisplayDialog(
            "Box Collider Baker",
            $"Generated {boxes.Count} BoxColliders from {occupiedCells.Count} sampled cells.",
            "OK");
    }

    private static void ClearGenerated(GameObject rootObject)
    {
        if (rootObject == null)
        {
            return;
        }

        Transform existing = rootObject.transform.Find(ContainerName);
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing.gameObject);
        }
    }

    private static void AddMeshSurfaceCells(
        Mesh mesh,
        Matrix4x4 meshToRoot,
        float size,
        int thicknessInCells,
        Dictionary<Vector3Int, bool> occupiedCells)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = meshToRoot.MultiplyPoint3x4(vertices[triangles[i]]);
            Vector3 b = meshToRoot.MultiplyPoint3x4(vertices[triangles[i + 1]]);
            Vector3 c = meshToRoot.MultiplyPoint3x4(vertices[triangles[i + 2]]);

            float longestEdge = Mathf.Max((a - b).magnitude, Mathf.Max((b - c).magnitude, (c - a).magnitude));
            int steps = Mathf.Max(1, Mathf.CeilToInt(longestEdge / (size * 0.75f)));

            for (int u = 0; u <= steps; u++)
            {
                for (int v = 0; v <= steps - u; v++)
                {
                    float fu = u / (float)steps;
                    float fv = v / (float)steps;
                    Vector3 point = a + (b - a) * fu + (c - a) * fv;
                    AddCellWithThickness(point, size, thicknessInCells, occupiedCells);
                }
            }
        }
    }

    private static void AddCellWithThickness(
        Vector3 point,
        float size,
        int thicknessInCells,
        Dictionary<Vector3Int, bool> occupiedCells)
    {
        Vector3Int center = WorldToCell(point, size);
        int radius = Mathf.Max(0, thicknessInCells - 1);

        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                for (int z = -radius; z <= radius; z++)
                {
                    Vector3Int cell = center + new Vector3Int(x, y, z);
                    occupiedCells[cell] = true;
                }
            }
        }
    }

    private static List<BoxInt> MergeCells(Dictionary<Vector3Int, bool> occupiedCells, int colliderLimit)
    {
        int coarseFactor = 1;
        List<BoxInt> boxes;
        do
        {
            boxes = MergeCellsAtScale(occupiedCells, coarseFactor);
            coarseFactor *= 2;
        }
        while (boxes.Count > colliderLimit && coarseFactor <= 64);

        return boxes;
    }

    private static List<BoxInt> MergeCellsAtScale(Dictionary<Vector3Int, bool> occupiedCells, int coarseFactor)
    {
        Dictionary<Vector3Int, bool> cells = new Dictionary<Vector3Int, bool>();
        foreach (Vector3Int cell in occupiedCells.Keys)
        {
            cells[FloorDiv(cell, coarseFactor)] = true;
        }

        List<BoxInt> boxes = MergeExactCells(cells);
        if (coarseFactor == 1)
        {
            return boxes;
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            BoxInt box = boxes[i];
            boxes[i] = new BoxInt(box.Min * coarseFactor, box.Max * coarseFactor + Vector3Int.one * (coarseFactor - 1));
        }

        return boxes;
    }

    private static List<BoxInt> MergeExactCells(Dictionary<Vector3Int, bool> occupiedCells)
    {
        HashSet<Vector3Int> remaining = new HashSet<Vector3Int>(occupiedCells.Keys);
        List<BoxInt> boxes = new List<BoxInt>();

        while (remaining.Count > 0)
        {
            Vector3Int start = default;
            foreach (Vector3Int cell in remaining)
            {
                start = cell;
                break;
            }

            BoxInt box = GrowBox(start, remaining);
            RemoveBoxCells(box, remaining);
            boxes.Add(box);
        }

        return boxes;
    }

    private static BoxInt GrowBox(Vector3Int start, HashSet<Vector3Int> remaining)
    {
        Vector3Int min = start;
        Vector3Int max = start;

        bool grew;
        do
        {
            grew = TryGrowAxis(ref min, ref max, remaining, Vector3Int.right)
                || TryGrowAxis(ref min, ref max, remaining, Vector3Int.up)
                || TryGrowAxis(ref min, ref max, remaining, new Vector3Int(0, 0, 1));
        }
        while (grew);

        return new BoxInt(min, max);
    }

    private static bool TryGrowAxis(ref Vector3Int min, ref Vector3Int max, HashSet<Vector3Int> remaining, Vector3Int axis)
    {
        Vector3Int nextMax = max + axis;
        if (ContainsEntireBox(min, nextMax, remaining))
        {
            max = nextMax;
            return true;
        }

        Vector3Int nextMin = min - axis;
        if (ContainsEntireBox(nextMin, max, remaining))
        {
            min = nextMin;
            return true;
        }

        return false;
    }

    private static bool ContainsEntireBox(Vector3Int min, Vector3Int max, HashSet<Vector3Int> remaining)
    {
        for (int x = min.x; x <= max.x; x++)
        {
            for (int y = min.y; y <= max.y; y++)
            {
                for (int z = min.z; z <= max.z; z++)
                {
                    if (!remaining.Contains(new Vector3Int(x, y, z)))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static void RemoveBoxCells(BoxInt box, HashSet<Vector3Int> remaining)
    {
        for (int x = box.Min.x; x <= box.Max.x; x++)
        {
            for (int y = box.Min.y; y <= box.Max.y; y++)
            {
                for (int z = box.Min.z; z <= box.Max.z; z++)
                {
                    remaining.Remove(new Vector3Int(x, y, z));
                }
            }
        }
    }

    private static Vector3Int WorldToCell(Vector3 point, float size)
    {
        return new Vector3Int(
            Mathf.FloorToInt(point.x / size),
            Mathf.FloorToInt(point.y / size),
            Mathf.FloorToInt(point.z / size));
    }

    private static Vector3Int FloorDiv(Vector3Int value, int divisor)
    {
        return new Vector3Int(
            FloorDiv(value.x, divisor),
            FloorDiv(value.y, divisor),
            FloorDiv(value.z, divisor));
    }

    private static int FloorDiv(int value, int divisor)
    {
        return value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
    }

    private static Vector3 ToVector3(Vector3Int value)
    {
        return new Vector3(value.x, value.y, value.z);
    }

    private struct BoxInt
    {
        public BoxInt(Vector3Int min, Vector3Int max)
        {
            Min = min;
            Max = max;
        }

        public Vector3Int Min { get; }
        public Vector3Int Max { get; }
        public Vector3Int Size => Max - Min + Vector3Int.one;
    }
}
