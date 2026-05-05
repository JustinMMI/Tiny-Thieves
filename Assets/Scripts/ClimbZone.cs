using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
[AddComponentMenu("Tiny Thieves/Climb Zone")]
public sealed class ClimbZone : MonoBehaviour
{
    private static readonly List<ClimbZone> registeredZones = new List<ClimbZone>();

    [Header("Zones")]
    [SerializeField] private BoxCollider climbVolume;
    [SerializeField] private BoxCollider landingZoneOverride;

    [Header("Shape")]
    [SerializeField] private float minClimbHeight = 0.18f;
    [SerializeField] private float maxClimbHeight = 2.4f;
    [SerializeField] private float landingFeetOffset = 0.02f;
    [SerializeField] private float edgePadding = 0.08f;

    [Header("Flow")]
    [SerializeField] private float grabForwardOffset = 0.08f;
    [SerializeField] private float landingForwardOffset = 0.28f;
    [SerializeField] private float activationPadding = 0.12f;
    [SerializeField] private bool requirePlayerBelowLanding = true;

    public readonly struct Route
    {
        public Route(Vector3 grabPosition, Vector3 landingPosition)
        {
            GrabPosition = grabPosition;
            LandingPosition = landingPosition;
        }

        public Vector3 GrabPosition { get; }
        public Vector3 LandingPosition { get; }
    }

    public static IReadOnlyList<ClimbZone> RegisteredZones => registeredZones;

    public BoxCollider VolumeCollider => Volume;

    public BoxCollider LandingZoneOverride => landingZoneOverride;

    private BoxCollider Volume
    {
        get
        {
            if (climbVolume == null)
            {
                climbVolume = GetComponent<BoxCollider>();
            }

            return climbVolume;
        }
    }

    private void OnEnable()
    {
        if (!registeredZones.Contains(this))
        {
            registeredZones.Add(this);
        }
    }

    private void OnDisable()
    {
        registeredZones.Remove(this);
    }

    private void Reset()
    {
        climbVolume = GetComponent<BoxCollider>();
        climbVolume.isTrigger = true;
    }

    private void OnValidate()
    {
        if (climbVolume == null)
        {
            climbVolume = GetComponent<BoxCollider>();
        }

        if (climbVolume != null)
        {
            climbVolume.isTrigger = true;
        }

        minClimbHeight = Mathf.Max(0f, minClimbHeight);
        maxClimbHeight = Mathf.Max(minClimbHeight, maxClimbHeight);
        edgePadding = Mathf.Max(0f, edgePadding);
        landingForwardOffset = Mathf.Max(0f, landingForwardOffset);
        activationPadding = Mathf.Max(0f, activationPadding);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.TryGetComponent(out TinyFirstPersonController controller))
        {
            controller.RegisterClimbZone(this);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.TryGetComponent(out TinyFirstPersonController controller))
        {
            controller.UnregisterClimbZone(this);
        }
    }

    public bool TryGetRoute(Vector3 playerPosition, CharacterController controller, out Route route)
    {
        route = default;

        BoxCollider volume = Volume;
        if (volume == null || !volume.enabled || !volume.isTrigger)
        {
            return false;
        }

        if (!ContainsController(volume, controller, activationPadding))
        {
            return false;
        }

        Bounds climbBounds = volume.bounds;
        Bounds landingBounds = landingZoneOverride != null && landingZoneOverride.enabled
            ? landingZoneOverride.bounds
            : climbBounds;

        float climbHeight = landingBounds.max.y - playerPosition.y;
        if (climbHeight < minClimbHeight || climbHeight > maxClimbHeight)
        {
            return false;
        }

        if (requirePlayerBelowLanding && playerPosition.y >= landingBounds.max.y)
        {
            return false;
        }

        float padding = Mathf.Max(edgePadding, controller != null ? controller.radius : 0f);
        Vector3 climbDirection = GetClimbDirection(playerPosition, climbBounds, landingBounds, volume);
        Vector3 landingProbePosition = playerPosition + climbDirection * landingForwardOffset;
        Vector3 landingPosition = GetNearestTopPoint(landingBounds, landingProbePosition, padding);
        landingPosition.y += landingFeetOffset;

        Vector3 grabPosition = GetNearestTopPoint(climbBounds, playerPosition, padding * 0.5f);
        grabPosition.y = Mathf.Clamp(playerPosition.y + 0.08f, climbBounds.min.y, climbBounds.max.y);

        if (climbDirection.sqrMagnitude > 0.0001f)
        {
            grabPosition -= climbDirection.normalized * grabForwardOffset;
        }

        route = new Route(grabPosition, landingPosition);
        return true;
    }

    private static Vector3 GetClimbDirection(Vector3 playerPosition, Bounds climbBounds, Bounds landingBounds, BoxCollider volume)
    {
        Vector3 direction = Vector3.ProjectOnPlane(landingBounds.center - climbBounds.center, Vector3.up);
        if (direction.sqrMagnitude > 0.0001f)
        {
            return direction.normalized;
        }

        direction = Vector3.ProjectOnPlane(landingBounds.center - playerPosition, Vector3.up);
        if (direction.sqrMagnitude > 0.0001f)
        {
            return direction.normalized;
        }

        Vector3 localPlayer = volume.transform.InverseTransformPoint(playerPosition) - volume.center;
        Vector3 localDirection = Mathf.Abs(localPlayer.x) > Mathf.Abs(localPlayer.z)
            ? new Vector3(-Mathf.Sign(localPlayer.x), 0f, 0f)
            : new Vector3(0f, 0f, -Mathf.Sign(localPlayer.z));

        if (localDirection.sqrMagnitude < 0.0001f)
        {
            localDirection = Vector3.forward;
        }

        return Vector3.ProjectOnPlane(volume.transform.TransformDirection(localDirection), Vector3.up).normalized;
    }

    public bool ContainsPlayer(Vector3 playerPosition, CharacterController controller)
    {
        BoxCollider volume = Volume;
        return volume != null && volume.enabled && volume.isTrigger && ContainsController(volume, controller, activationPadding);
    }

    private static bool ContainsController(BoxCollider box, CharacterController controller, float extraPadding)
    {
        if (controller == null)
        {
            return false;
        }

        Transform controllerTransform = controller.transform;
        Vector3 center = controllerTransform.TransformPoint(controller.center);
        Vector3 up = controllerTransform.up;
        float radius = controller.radius;
        float halfLine = Mathf.Max(0f, controller.height * 0.5f - radius);
        Vector3 bottom = center - up * halfLine;
        Vector3 top = center + up * halfLine;
        float tolerance = radius + extraPadding;

        return ContainsPoint(box, bottom, tolerance)
            || ContainsPoint(box, Vector3.Lerp(bottom, top, 0.25f), tolerance)
            || ContainsPoint(box, center, tolerance)
            || ContainsPoint(box, Vector3.Lerp(bottom, top, 0.75f), tolerance)
            || ContainsPoint(box, top, tolerance);
    }

    private static bool ContainsPoint(BoxCollider box, Vector3 worldPoint, float tolerance)
    {
        Vector3 closestPoint = box.ClosestPoint(worldPoint);
        return (closestPoint - worldPoint).sqrMagnitude <= tolerance * tolerance;
    }

    private static Vector3 GetNearestTopPoint(Bounds bounds, Vector3 position, float padding)
    {
        float minX = Mathf.Min(bounds.min.x + padding, bounds.max.x);
        float maxX = Mathf.Max(bounds.max.x - padding, bounds.min.x);
        float minZ = Mathf.Min(bounds.min.z + padding, bounds.max.z);
        float maxZ = Mathf.Max(bounds.max.z - padding, bounds.min.z);

        return new Vector3(
            Mathf.Clamp(position.x, minX, maxX),
            bounds.max.y,
            Mathf.Clamp(position.z, minZ, maxZ));
    }

    private void OnDrawGizmosSelected()
    {
        DrawGizmosDebug(
            new Color(0.1f, 0.65f, 1f, 0.22f),
            new Color(0.1f, 0.65f, 1f, 0.9f),
            new Color(0.2f, 1f, 0.45f, 0.22f),
            new Color(0.2f, 1f, 0.45f, 0.9f));
    }

    public void DrawGizmosDebug(Color climbFill, Color climbWire, Color landingFill, Color landingWire)
    {
        BoxCollider volume = Volume;
        if (volume == null)
        {
            return;
        }

        DrawBox(volume, climbFill, climbWire);

        if (landingZoneOverride != null)
        {
            DrawBox(landingZoneOverride, landingFill, landingWire);
        }
    }

    private static void DrawBox(BoxCollider box, Color fill, Color wire)
    {
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Gizmos.matrix = box.transform.localToWorldMatrix;
        Gizmos.color = fill;
        Gizmos.DrawCube(box.center, box.size);
        Gizmos.color = wire;
        Gizmos.DrawWireCube(box.center, box.size);
        Gizmos.matrix = previousMatrix;
    }
}
