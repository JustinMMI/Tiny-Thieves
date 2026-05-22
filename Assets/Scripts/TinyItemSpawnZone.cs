using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("Tiny Thieves/Item Spawn Zone")]
public sealed class TinyItemSpawnZone : MonoBehaviour
{
    [SerializeField, Min(0)] private int minItems = 1;
    [SerializeField, Min(0)] private int maxItems = 3;
    [SerializeField, Range(0f, 1f)] private float emptyChance = 0.15f;
    [SerializeField] private Vector3 localSize = new Vector3(1f, 0.15f, 1f);
    [SerializeField] private Vector3 localCenter = Vector3.zero;
    [SerializeField] private bool alignItemToZone = true;

    public int MinItems => Mathf.Max(0, minItems);
    public int MaxItems => Mathf.Max(MinItems, maxItems);
    public float EmptyChance => Mathf.Clamp01(emptyChance);

    public Pose GetRandomSpawnPose(System.Random random)
    {
        if (random == null)
        {
            random = new System.Random();
        }

        Vector3 halfSize = Vector3.Max(localSize, Vector3.zero) * 0.5f;
        Vector3 localPoint = localCenter + new Vector3(
            RandomRange(random, -halfSize.x, halfSize.x),
            RandomRange(random, -halfSize.y, halfSize.y),
            RandomRange(random, -halfSize.z, halfSize.z));
        Quaternion rotation = alignItemToZone
            ? transform.rotation
            : Quaternion.Euler(0f, RandomRange(random, 0f, 360f), 0f);

        return new Pose(transform.TransformPoint(localPoint), rotation);
    }

    private static float RandomRange(System.Random random, float min, float max)
    {
        return Mathf.Lerp(min, max, (float)random.NextDouble());
    }

    private void OnValidate()
    {
        maxItems = Mathf.Max(minItems, maxItems);
    }

    private void OnDrawGizmos()
    {
        DrawGizmos(false);
    }

    private void OnDrawGizmosSelected()
    {
        DrawGizmos(true);
    }

    private void DrawGizmos(bool selected)
    {
        Gizmos.color = selected ? new Color(1f, 0.76f, 0.1f, 0.35f) : new Color(1f, 0.76f, 0.1f, 0.12f);
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawCube(localCenter, localSize);
        Gizmos.color = selected ? new Color(1f, 0.76f, 0.1f, 0.95f) : new Color(1f, 0.76f, 0.1f, 0.55f);
        Gizmos.DrawWireCube(localCenter, localSize);
        Gizmos.matrix = previousMatrix;
    }
}
