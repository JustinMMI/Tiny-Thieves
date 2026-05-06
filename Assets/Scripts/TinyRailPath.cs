using UnityEngine;

[DisallowMultipleComponent]
public sealed class TinyRailPath : MonoBehaviour
{
    [SerializeField] private Transform[] points;
    [SerializeField] private bool closedLoop;
    [SerializeField] private Color gizmoColor = new Color(0.92f, 0.68f, 0.18f, 1f);

    private float[] segmentLengths;
    private float totalLength;

    public float Length
    {
        get
        {
            EnsureCache();
            return totalLength;
        }
    }

    private void Awake()
    {
        EnsureCache();
    }

    private void OnValidate()
    {
        RebuildCache();
    }

    public void GetPose(float distance, out Vector3 position, out Vector3 forward)
    {
        EnsureCache();

        int pointCount = GetPointCount();
        if (pointCount == 0)
        {
            position = transform.position;
            forward = transform.forward;
            return;
        }

        if (pointCount == 1 || totalLength <= 0.0001f)
        {
            position = GetPoint(0);
            forward = transform.forward;
            return;
        }

        float remaining = closedLoop ? Mathf.Repeat(distance, totalLength) : Mathf.Clamp(distance, 0f, totalLength);
        int segmentCount = GetSegmentCount(pointCount);

        for (int i = 0; i < segmentCount; i++)
        {
            float segmentLength = segmentLengths[i];
            if (remaining > segmentLength && i < segmentCount - 1)
            {
                remaining -= segmentLength;
                continue;
            }

            Vector3 start = GetPoint(i);
            Vector3 end = GetPoint((i + 1) % pointCount);
            float t = segmentLength > 0.0001f ? remaining / segmentLength : 0f;
            position = Vector3.Lerp(start, end, t);
            forward = (end - start).sqrMagnitude > 0.0001f ? (end - start).normalized : transform.forward;
            return;
        }

        position = GetPoint(pointCount - 1);
        forward = (position - GetPoint(pointCount - 2)).normalized;
    }

    public float ClampDistance(float distance)
    {
        EnsureCache();
        if (totalLength <= 0.0001f)
        {
            return 0f;
        }

        return closedLoop ? Mathf.Repeat(distance, totalLength) : Mathf.Clamp(distance, 0f, totalLength);
    }

    private void EnsureCache()
    {
        if (segmentLengths == null)
        {
            RebuildCache();
        }
    }

    private void RebuildCache()
    {
        int pointCount = GetPointCount();
        int segmentCount = GetSegmentCount(pointCount);
        segmentLengths = new float[segmentCount];
        totalLength = 0f;

        for (int i = 0; i < segmentCount; i++)
        {
            float segmentLength = Vector3.Distance(GetPoint(i), GetPoint((i + 1) % pointCount));
            segmentLengths[i] = segmentLength;
            totalLength += segmentLength;
        }
    }

    private int GetPointCount()
    {
        if (points == null || points.Length == 0)
        {
            return transform.childCount;
        }

        int count = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] != null)
            {
                count++;
            }
        }

        return count;
    }

    private int GetSegmentCount(int pointCount)
    {
        if (pointCount < 2)
        {
            return 0;
        }

        return closedLoop ? pointCount : pointCount - 1;
    }

    private Vector3 GetPoint(int index)
    {
        if (points == null || points.Length == 0)
        {
            return transform.GetChild(index).position;
        }

        int found = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] == null)
            {
                continue;
            }

            if (found == index)
            {
                return points[i].position;
            }

            found++;
        }

        return transform.position;
    }

    private void OnDrawGizmos()
    {
        int pointCount = GetPointCount();
        int segmentCount = GetSegmentCount(pointCount);
        if (segmentCount == 0)
        {
            return;
        }

        Gizmos.color = gizmoColor;
        for (int i = 0; i < pointCount; i++)
        {
            Gizmos.DrawSphere(GetPoint(i), 0.08f);
        }

        for (int i = 0; i < segmentCount; i++)
        {
            Gizmos.DrawLine(GetPoint(i), GetPoint((i + 1) % pointCount));
        }
    }
}
