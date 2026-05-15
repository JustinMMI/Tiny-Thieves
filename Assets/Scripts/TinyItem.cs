using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public sealed class TinyItem : MonoBehaviour
{
    [Header("Item")]
    [SerializeField] private string itemName = "Objet";
    [SerializeField, Min(0f)] private float weightKilograms = 1f;
    [SerializeField, Min(0)] private int value = 10;

    [Header("Hold")]
    [SerializeField] private Vector3 holdLocalPosition = Vector3.zero;
    [SerializeField] private Vector3 holdLocalEulerAngles = Vector3.zero;
    [SerializeField, Range(0.05f, 0.45f)] private float handAnchorWidth = 0.18f;
    [SerializeField, InspectorName("Main gauche active")] private bool leftHandActive = true;
    [SerializeField, InspectorName("Main droite active")] private bool rightHandActive = true;
    [SerializeField] private Vector3 leftHandGripLocalOffset = Vector3.zero;
    [SerializeField] private Vector3 rightHandGripLocalOffset = Vector3.zero;
    [SerializeField] private Vector3 leftHandGripEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 rightHandGripEulerAngles = Vector3.zero;

    [Header("Physics")]
    [SerializeField] private bool configurePhysics = true;
    [SerializeField] private bool syncMassWithWeight = true;
    [SerializeField, Min(1)] private int solverIterations = 12;
    [SerializeField, Min(1)] private int solverVelocityIterations = 4;

    private Rigidbody itemRigidbody;
    private Collider[] itemColliders;
    private bool[] colliderEnabledStates;
    private Transform originalParent;
    private bool hadRigidbody;
    private bool originalIsKinematic;
    private bool originalUseGravity;
    private bool isRemoteHeld;
    private bool hasOriginalPhysics;

    public string ItemName => itemName;
    public float WeightKilograms => weightKilograms;
    public int Value => value;
    public bool LeftHandActive => leftHandActive;
    public bool RightHandActive => rightHandActive;
    public bool IsHeld { get; private set; }
    public bool IsNetworkHeld => IsHeld || isRemoteHeld;

    private void Awake()
    {
        CachePhysics();
        ConfigurePhysics();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying || !IsHeld)
        {
            CachePhysics();
            ConfigurePhysics();
        }

        if (Application.isPlaying && IsHeld)
        {
            ApplyHeldLocalPose();
        }
    }

    public void PickUp(Transform holdPoint)
    {
        PickUp(holdPoint, false);
    }

    public void PickUp(Transform holdPoint, bool preserveWorldPose)
    {
        if (holdPoint == null || IsHeld)
        {
            return;
        }

        CachePhysics();
        isRemoteHeld = false;
        IsHeld = true;
        originalParent = transform.parent;

        if (itemRigidbody != null)
        {
            hadRigidbody = true;
            if (!hasOriginalPhysics)
            {
                originalIsKinematic = itemRigidbody.isKinematic;
                originalUseGravity = itemRigidbody.useGravity;
                hasOriginalPhysics = true;
            }

            itemRigidbody.isKinematic = true;
            itemRigidbody.useGravity = false;
            itemRigidbody.interpolation = RigidbodyInterpolation.None;
        }

        SetCollidersEnabled(false);

        transform.SetParent(holdPoint, preserveWorldPose);
        if (!preserveWorldPose)
        {
            ApplyHeldLocalPose();
        }
    }

    public void ApplyHeldLocalPose()
    {
        transform.localPosition = holdLocalPosition;
        transform.localRotation = Quaternion.Euler(holdLocalEulerAngles);
    }

    public void GetHoldLocalPose(out Vector3 localPosition, out Quaternion localRotation)
    {
        localPosition = holdLocalPosition;
        localRotation = Quaternion.Euler(holdLocalEulerAngles);
    }

    public void Drop(Vector3 worldPosition, Quaternion worldRotation)
    {
        Release(worldPosition, worldRotation, Vector3.zero);
    }

    public void Throw(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocity)
    {
        Release(worldPosition, worldRotation, velocity);
    }

    private void Release(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocity)
    {
        if (!IsHeld)
        {
            return;
        }

        isRemoteHeld = false;
        transform.SetParent(originalParent, true);
        transform.position = worldPosition;
        transform.rotation = worldRotation;

        RestoreColliders();

        if (hadRigidbody && itemRigidbody != null)
        {
            itemRigidbody.isKinematic = originalIsKinematic;
            itemRigidbody.useGravity = originalUseGravity;
            ConfigurePhysics();
#if UNITY_6000_0_OR_NEWER
            itemRigidbody.linearVelocity = velocity;
#else
            itemRigidbody.velocity = velocity;
#endif
            itemRigidbody.angularVelocity = Vector3.zero;
        }

        IsHeld = false;
    }

    public void ApplyRemoteHeldState(bool remoteHeld)
    {
        if (IsHeld)
        {
            return;
        }

        if (remoteHeld)
        {
            if (isRemoteHeld)
            {
                return;
            }

            CachePhysics();
            isRemoteHeld = true;
            if (itemRigidbody != null)
            {
                itemRigidbody.isKinematic = true;
                itemRigidbody.useGravity = false;
                itemRigidbody.interpolation = RigidbodyInterpolation.None;
#if UNITY_6000_0_OR_NEWER
                itemRigidbody.linearVelocity = Vector3.zero;
#else
                itemRigidbody.velocity = Vector3.zero;
#endif
                itemRigidbody.angularVelocity = Vector3.zero;
            }

            SetCollidersEnabled(false);
            return;
        }

        isRemoteHeld = false;
        RestoreColliders();
        if (itemRigidbody != null)
        {
            itemRigidbody.isKinematic = originalIsKinematic;
            itemRigidbody.useGravity = originalUseGravity;
            ConfigurePhysics();
        }
    }

    public void ApplyAuthoritativeRelease(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocity)
    {
        if (IsHeld)
        {
            Release(worldPosition, worldRotation, velocity);
            return;
        }

        isRemoteHeld = false;
        transform.SetParent(originalParent, true);
        transform.SetPositionAndRotation(worldPosition, worldRotation);
        RestoreColliders();

        if (itemRigidbody != null)
        {
            itemRigidbody.isKinematic = originalIsKinematic;
            itemRigidbody.useGravity = originalUseGravity;
            ConfigurePhysics();
#if UNITY_6000_0_OR_NEWER
            itemRigidbody.linearVelocity = velocity;
#else
            itemRigidbody.velocity = velocity;
#endif
            itemRigidbody.angularVelocity = Vector3.zero;
        }
    }

    public void GetHandAnchors(Transform playerRoot, out Vector3 leftAnchor, out Vector3 rightAnchor)
    {
        Vector3 side = transform.right;
        Vector3 center = transform.TransformPoint(GetStableLocalBoundsCenter());
        float halfWidth = Mathf.Max(handAnchorWidth * 0.5f, 0.04f);

        leftAnchor = center - side * halfWidth + transform.TransformVector(leftHandGripLocalOffset);
        rightAnchor = center + side * halfWidth + transform.TransformVector(rightHandGripLocalOffset);
    }

    public void GetHandRotations(Transform playerRoot, out Quaternion leftRotation, out Quaternion rightRotation)
    {
        Quaternion baseRotation = transform.rotation;
        leftRotation = baseRotation * Quaternion.Euler(leftHandGripEulerAngles);
        rightRotation = baseRotation * Quaternion.Euler(rightHandGripEulerAngles);
    }

    public Bounds GetWorldBounds()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return new Bounds(transform.position, Vector3.one * 0.18f);
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private Vector3 GetStableLocalBoundsCenter()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return Vector3.zero;
        }

        Bounds localBounds = default;
        bool hasBounds = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer itemRenderer = renderers[i];
            if (itemRenderer == null)
            {
                continue;
            }

            Bounds rendererBounds = itemRenderer.localBounds;
            Vector3 min = rendererBounds.min;
            Vector3 max = rendererBounds.max;
            EncapsulateLocalPoint(ref localBounds, ref hasBounds, itemRenderer.transform.TransformPoint(new Vector3(min.x, min.y, min.z)));
            EncapsulateLocalPoint(ref localBounds, ref hasBounds, itemRenderer.transform.TransformPoint(new Vector3(min.x, min.y, max.z)));
            EncapsulateLocalPoint(ref localBounds, ref hasBounds, itemRenderer.transform.TransformPoint(new Vector3(min.x, max.y, min.z)));
            EncapsulateLocalPoint(ref localBounds, ref hasBounds, itemRenderer.transform.TransformPoint(new Vector3(min.x, max.y, max.z)));
            EncapsulateLocalPoint(ref localBounds, ref hasBounds, itemRenderer.transform.TransformPoint(new Vector3(max.x, min.y, min.z)));
            EncapsulateLocalPoint(ref localBounds, ref hasBounds, itemRenderer.transform.TransformPoint(new Vector3(max.x, min.y, max.z)));
            EncapsulateLocalPoint(ref localBounds, ref hasBounds, itemRenderer.transform.TransformPoint(new Vector3(max.x, max.y, min.z)));
            EncapsulateLocalPoint(ref localBounds, ref hasBounds, itemRenderer.transform.TransformPoint(new Vector3(max.x, max.y, max.z)));
        }

        return hasBounds ? localBounds.center : Vector3.zero;
    }

    private void EncapsulateLocalPoint(ref Bounds localBounds, ref bool hasBounds, Vector3 worldPoint)
    {
        Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
        if (!hasBounds)
        {
            localBounds = new Bounds(localPoint, Vector3.zero);
            hasBounds = true;
            return;
        }

        localBounds.Encapsulate(localPoint);
    }

    private void CachePhysics()
    {
        itemRigidbody = GetComponent<Rigidbody>();
        hadRigidbody = itemRigidbody != null;
        if (!IsHeld && !isRemoteHeld && itemRigidbody != null && !hasOriginalPhysics)
        {
            originalIsKinematic = itemRigidbody.isKinematic;
            originalUseGravity = itemRigidbody.useGravity;
            hasOriginalPhysics = true;
        }

        itemColliders = GetComponentsInChildren<Collider>(true);
        colliderEnabledStates = new bool[itemColliders.Length];

        for (int i = 0; i < itemColliders.Length; i++)
        {
            colliderEnabledStates[i] = itemColliders[i] != null && itemColliders[i].enabled;
        }
    }

    private void ConfigurePhysics()
    {
        if (!configurePhysics || itemRigidbody == null)
        {
            return;
        }

        if (syncMassWithWeight)
        {
            itemRigidbody.mass = Mathf.Max(0.05f, weightKilograms);
        }

        itemRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        itemRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        itemRigidbody.solverIterations = Mathf.Max(1, solverIterations);
        itemRigidbody.solverVelocityIterations = Mathf.Max(1, solverVelocityIterations);
    }

    private void SetCollidersEnabled(bool enabled)
    {
        for (int i = 0; i < itemColliders.Length; i++)
        {
            if (itemColliders[i] != null)
            {
                itemColliders[i].enabled = enabled;
            }
        }
    }

    private void RestoreColliders()
    {
        for (int i = 0; i < itemColliders.Length; i++)
        {
            if (itemColliders[i] != null)
            {
                itemColliders[i].enabled = i < colliderEnabledStates.Length && colliderEnabledStates[i];
            }
        }
    }
}
