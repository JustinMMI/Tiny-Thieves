using UnityEngine;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(Rigidbody))]
public sealed class TinyRailWagon : MonoBehaviour
{
    [Header("Rail")]
    [SerializeField] private TinyRailPath railPath;
    [SerializeField] private float distanceOnRail;
    [SerializeField] private float pushSpeed = 0.55f;
    [SerializeField] private float railHeightOffset = 0.18f;
    [SerializeField] private bool alignToRail = true;
    [FormerlySerializedAs("rotationSharpness")]
    [SerializeField] private float horizontalRotationSharpness = 8f;
    [SerializeField] private float verticalRotationSharpness = 8f;

    [Header("Physics")]
    [SerializeField] private bool configurePhysics = true;
    [SerializeField] private bool rootColliderIsTrigger = true;
    [SerializeField] private bool childCollidersAreSolid = true;

    [Header("Interaction")]
    [SerializeField] private float interactDistance = 0.9f;
    [SerializeField] private float handAnchorWidth = 0.28f;
    [FormerlySerializedAs("handAnchorLocalCenter")]
    [SerializeField] private Vector3 backHandAnchorLocalCenter = new Vector3(0f, 0.22f, -0.28f);
    [SerializeField] private Vector3 frontHandAnchorLocalCenter = new Vector3(0f, 0.22f, 0.28f);
    [FormerlySerializedAs("handAnchorLocalEulerAngles")]
    [SerializeField] private Vector3 leftHandAnchorLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 rightHandAnchorLocalEulerAngles = Vector3.zero;
    [FormerlySerializedAs("playerOffset")]
    [SerializeField] private Vector3 backPlayerOffset = new Vector3(0f, 0f, -0.42f);
    [SerializeField] private Vector3 frontPlayerOffset = new Vector3(0f, 0f, 0.42f);

    [Header("Wheels")]
    [SerializeField] private Transform[] wheels;
    [SerializeField] private bool[] invertWheelRotation;
    [SerializeField] private float wheelRotationDegreesPerMeter = 720f;

    private BoxCollider wagonCollider;
    private Rigidbody wagonRigidbody;
    private Vector3 targetPhysicsPosition;
    private Quaternion targetPhysicsRotation = Quaternion.identity;
    private bool hasTargetPhysicsPose;
    private float wheelRollAngle;
    private Quaternion[] wheelBaseLocalRotations;

    public float InteractDistance => interactDistance;
    public float DistanceOnRail => distanceOnRail;
    public float WheelRollAngle => wheelRollAngle;

    private void Awake()
    {
        CacheComponents();
        CacheWheelRotations();
        MatchWheelSettingsLength();
        ConfigurePhysics();
        ApplyRailPose(true);
    }

    private void OnValidate()
    {
        CacheComponents();
        CacheWheelRotations();
        MatchWheelSettingsLength();
        horizontalRotationSharpness = Mathf.Max(0f, horizontalRotationSharpness);
        verticalRotationSharpness = Mathf.Max(0f, verticalRotationSharpness);
        ConfigurePhysics();
        ApplyRailPose(true);
    }

    public bool CanInteract(Transform player)
    {
        if (player == null)
        {
            return false;
        }

        Vector3 closest = wagonCollider != null ? wagonCollider.ClosestPoint(player.position) : transform.position;
        return (closest - player.position).sqrMagnitude <= interactDistance * interactDistance;
    }

    public int GetPlayerSide(Transform player)
    {
        if (player == null)
        {
            return 1;
        }

        return GetPlayerSide(player.position);
    }

    public int GetPlayerSide(Vector3 playerPosition)
    {
        float side = Vector3.Dot(playerPosition - GetReferencePosition(), GetReferenceRotation() * Vector3.forward);
        return side >= 0f ? -1 : 1;
    }

    public Vector3 PushFromPlayer(Transform player, float input, float deltaTime)
    {
        if (railPath == null || Mathf.Abs(input) < 0.01f)
        {
            return Vector3.zero;
        }

        Vector3 previousPosition = GetReferencePosition();
        int side = GetPlayerSide(player);
        float previousDistance = distanceOnRail;
        distanceOnRail = railPath.ClampDistance(distanceOnRail + input * side * pushSpeed * deltaTime);
        RotateWheels(distanceOnRail - previousDistance);
        Vector3 targetPosition = ApplyRailPose(false);
        return targetPosition - previousPosition;
    }

    public Vector3 PushFromNetwork(Vector3 playerPosition, float input, float deltaTime)
    {
        if (railPath == null || Mathf.Abs(input) < 0.01f)
        {
            return Vector3.zero;
        }

        Vector3 previousPosition = GetReferencePosition();
        int side = GetPlayerSide(playerPosition);
        float previousDistance = distanceOnRail;
        distanceOnRail = railPath.ClampDistance(distanceOnRail + input * side * pushSpeed * deltaTime);
        RotateWheels(distanceOnRail - previousDistance);
        Vector3 targetPosition = ApplyRailPose(false);
        return targetPosition - previousPosition;
    }

    public Vector3 PushAlongRail(float railInput, float deltaTime)
    {
        if (railPath == null || Mathf.Abs(railInput) < 0.01f)
        {
            return Vector3.zero;
        }

        Vector3 previousPosition = GetReferencePosition();
        float previousDistance = distanceOnRail;
        distanceOnRail = railPath.ClampDistance(distanceOnRail + railInput * pushSpeed * deltaTime);
        RotateWheels(distanceOnRail - previousDistance);
        Vector3 targetPosition = ApplyRailPose(false);
        return targetPosition - previousPosition;
    }

    public Vector3 GetPlayerFollowPosition(Transform player)
    {
        int side = GetPlayerSide(player);
        Vector3 offset = side < 0 ? frontPlayerOffset : backPlayerOffset;
        return GetReferencePosition() + GetReferenceRotation() * offset;
    }

    public void GetHandAnchors(Transform player, out Vector3 leftAnchor, out Vector3 rightAnchor)
    {
        int side = GetPlayerSide(player);
        Vector3 center = side < 0 ? frontHandAnchorLocalCenter : backHandAnchorLocalCenter;

        Quaternion referenceRotation = GetReferenceRotation();
        Vector3 worldCenter = GetReferencePosition() + referenceRotation * center;
        Vector3 sideVector = referenceRotation * Vector3.right * side;
        float halfWidth = Mathf.Max(0.04f, handAnchorWidth * 0.5f);
        leftAnchor = worldCenter - sideVector * halfWidth;
        rightAnchor = worldCenter + sideVector * halfWidth;
    }

    public Quaternion GetHandRotation(Transform player)
    {
        int side = GetPlayerSide(player);
        Quaternion sideRotation = side < 0 ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
        return GetReferenceRotation() * sideRotation;
    }

    public void GetHandRotations(Transform player, out Quaternion leftRotation, out Quaternion rightRotation)
    {
        Quaternion baseRotation = GetHandRotation(player);
        leftRotation = baseRotation * Quaternion.Euler(leftHandAnchorLocalEulerAngles);
        rightRotation = baseRotation * Quaternion.Euler(rightHandAnchorLocalEulerAngles);
    }

    public Bounds GetWorldBounds()
    {
        if (wagonCollider != null)
        {
            return wagonCollider.bounds;
        }

        Renderer renderer = GetComponentInChildren<Renderer>();
        return renderer != null ? renderer.bounds : new Bounds(transform.position, Vector3.one * 0.4f);
    }

    private Vector3 ApplyRailPose(bool snapRotation)
    {
        if (railPath == null)
        {
            return GetReferencePosition();
        }

        railPath.GetPose(distanceOnRail, out Vector3 position, out Vector3 forward);
        Vector3 targetPosition = position + Vector3.up * railHeightOffset;
        Quaternion targetRotation = GetReferenceRotation();
        if (alignToRail && forward.sqrMagnitude > 0.0001f)
        {
            Quaternion railRotation = Quaternion.LookRotation(forward, Vector3.up);
            targetRotation = snapRotation ? railRotation : SmoothRailRotation(railRotation);
        }

        MoveWagon(targetPosition, targetRotation, snapRotation);
        return targetPosition;
    }

    private Quaternion SmoothRailRotation(Quaternion targetRotation)
    {
        float horizontalFollow = 1f - Mathf.Exp(-horizontalRotationSharpness * Time.deltaTime);
        float verticalFollow = 1f - Mathf.Exp(-verticalRotationSharpness * Time.deltaTime);
        Vector3 currentEuler = GetReferenceRotation().eulerAngles;
        Vector3 targetEuler = targetRotation.eulerAngles;

        return Quaternion.Euler(
            Mathf.LerpAngle(currentEuler.x, targetEuler.x, verticalFollow),
            Mathf.LerpAngle(currentEuler.y, targetEuler.y, horizontalFollow),
            Mathf.LerpAngle(currentEuler.z, targetEuler.z, horizontalFollow));
    }

    private void CacheComponents()
    {
        wagonCollider = GetComponent<BoxCollider>();
        wagonRigidbody = GetComponent<Rigidbody>();
        if (Application.isPlaying && configurePhysics && wagonRigidbody == null)
        {
            wagonRigidbody = gameObject.AddComponent<Rigidbody>();
        }
    }

    private void ConfigurePhysics()
    {
        if (!configurePhysics)
        {
            return;
        }

        if (wagonCollider != null)
        {
            wagonCollider.isTrigger = rootColliderIsTrigger;
        }

        if (childCollidersAreSolid)
        {
            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null && colliders[i] != wagonCollider)
                {
                    colliders[i].isTrigger = false;
                }
            }
        }

        if (wagonRigidbody == null)
        {
            return;
        }

        wagonRigidbody.isKinematic = true;
        wagonRigidbody.useGravity = false;
        wagonRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        wagonRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    private void RotateWheels(float distanceDelta)
    {
        if (wheels == null || wheels.Length == 0 || Mathf.Abs(distanceDelta) < 0.00001f)
        {
            return;
        }

        wheelRollAngle -= distanceDelta * wheelRotationDegreesPerMeter;
        ApplyWheelRotations();
    }

    public void ApplyRemoteWheelRollAngle(float angle)
    {
        wheelRollAngle = angle;
        ApplyWheelRotations();
    }

    public void ApplyRemoteRailState(float railDistance, float wheelAngle)
    {
        distanceOnRail = railPath != null ? railPath.ClampDistance(railDistance) : railDistance;
        wheelRollAngle = wheelAngle;
        ApplyWheelRotations();
        ApplyRailPose(false);
    }

    private void ApplyWheelRotations()
    {
        if (wheels == null || wheels.Length == 0)
        {
            return;
        }

        for (int i = 0; i < wheels.Length; i++)
        {
            if (wheels[i] != null)
            {
                Quaternion baseRotation = wheelBaseLocalRotations != null && i < wheelBaseLocalRotations.Length
                    ? wheelBaseLocalRotations[i]
                    : Quaternion.identity;
                float direction = invertWheelRotation != null && i < invertWheelRotation.Length && invertWheelRotation[i] ? -1f : 1f;
                wheels[i].localRotation = baseRotation * Quaternion.Euler(0f, 0f, wheelRollAngle * direction);
            }
        }
    }

    private void CacheWheelRotations()
    {
        if (wheels == null)
        {
            wheelBaseLocalRotations = null;
            return;
        }

        wheelBaseLocalRotations = new Quaternion[wheels.Length];
        for (int i = 0; i < wheels.Length; i++)
        {
            wheelBaseLocalRotations[i] = wheels[i] != null ? wheels[i].localRotation : Quaternion.identity;
        }
    }

    private void MatchWheelSettingsLength()
    {
        if (wheels == null)
        {
            invertWheelRotation = null;
            return;
        }

        if (invertWheelRotation != null && invertWheelRotation.Length == wheels.Length)
        {
            return;
        }

        bool[] previous = invertWheelRotation;
        invertWheelRotation = new bool[wheels.Length];
        if (previous == null)
        {
            return;
        }

        int count = Mathf.Min(previous.Length, invertWheelRotation.Length);
        for (int i = 0; i < count; i++)
        {
            invertWheelRotation[i] = previous[i];
        }
    }

    private void MoveWagon(Vector3 targetPosition, Quaternion targetRotation, bool snap)
    {
        if (Application.isPlaying && wagonRigidbody != null && !snap)
        {
            targetPhysicsPosition = targetPosition;
            targetPhysicsRotation = targetRotation;
            hasTargetPhysicsPose = true;
            wagonRigidbody.MovePosition(targetPosition);
            wagonRigidbody.MoveRotation(targetRotation);
            return;
        }

        targetPhysicsPosition = targetPosition;
        targetPhysicsRotation = targetRotation;
        hasTargetPhysicsPose = true;
        if (wagonRigidbody != null)
        {
            wagonRigidbody.position = targetPosition;
            wagonRigidbody.rotation = targetRotation;
        }
        else
        {
            transform.SetPositionAndRotation(targetPosition, targetRotation);
        }
    }

    private Vector3 GetReferencePosition()
    {
        return hasTargetPhysicsPose ? targetPhysicsPosition : transform.position;
    }

    private Quaternion GetReferenceRotation()
    {
        return hasTargetPhysicsPose ? targetPhysicsRotation : transform.rotation;
    }

    private void OnDrawGizmosSelected()
    {
        GetHandAnchors(null, out Vector3 leftAnchor, out Vector3 rightAnchor);
        Gizmos.color = Color.cyan;
        Gizmos.DrawSphere(leftAnchor, 0.04f);
        Gizmos.DrawSphere(rightAnchor, 0.04f);
    }
}
