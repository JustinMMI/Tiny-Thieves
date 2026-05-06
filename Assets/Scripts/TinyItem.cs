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

    public string ItemName => itemName;
    public float WeightKilograms => weightKilograms;
    public int Value => value;
    public bool IsHeld { get; private set; }

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
        if (holdPoint == null || IsHeld)
        {
            return;
        }

        CachePhysics();
        IsHeld = true;
        originalParent = transform.parent;

        if (itemRigidbody != null)
        {
            hadRigidbody = true;
            originalIsKinematic = itemRigidbody.isKinematic;
            originalUseGravity = itemRigidbody.useGravity;
            itemRigidbody.isKinematic = true;
            itemRigidbody.useGravity = false;
            itemRigidbody.interpolation = RigidbodyInterpolation.None;
        }

        SetCollidersEnabled(false);

        transform.SetParent(holdPoint, false);
        ApplyHeldLocalPose();
    }

    public void ApplyHeldLocalPose()
    {
        transform.localPosition = holdLocalPosition;
        transform.localRotation = Quaternion.Euler(holdLocalEulerAngles);
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

    public void GetHandAnchors(Transform playerRoot, out Vector3 leftAnchor, out Vector3 rightAnchor)
    {
        Bounds bounds = GetWorldBounds();
        Vector3 side = playerRoot != null ? playerRoot.right : transform.right;
        Vector3 center = bounds.center;
        float halfWidth = Mathf.Max(handAnchorWidth * 0.5f, 0.04f);

        leftAnchor = center - side * halfWidth;
        rightAnchor = center + side * halfWidth;
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

    private void CachePhysics()
    {
        itemRigidbody = GetComponent<Rigidbody>();
        hadRigidbody = itemRigidbody != null;
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
