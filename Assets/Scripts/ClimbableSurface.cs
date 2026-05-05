using UnityEngine;

public sealed class ClimbableSurface : MonoBehaviour
{
    [SerializeField] private float minClimbHeight = 0.12f;
    [SerializeField] private float maxClimbHeight = 1.05f;
    [SerializeField, Range(0f, 1f)] private float minFacingDot = 0.45f;
    [SerializeField, Range(0f, 1f)] private float minTopNormalY = 0.75f;

    public float MinClimbHeight => minClimbHeight;
    public float MaxClimbHeight => maxClimbHeight;
    public float MinFacingDot => minFacingDot;
    public float MinTopNormalY => minTopNormalY;
}
