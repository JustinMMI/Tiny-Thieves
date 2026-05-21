using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("Tiny Thieves/Wagon Send Lever")]
public sealed class TinyWagonSendLever : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TinyRailWagon targetWagon;
    [SerializeField] private Transform sendDirectionReference;
    [SerializeField] private Animator leverAnimator;

    [Header("Interaction")]
    [SerializeField] private float interactDistance = 1.15f;
    [SerializeField] private float wagonRequiredDistance = 1.8f;

    [Header("Send")]
    [SerializeField] private float sendDistance = 7f;
    [SerializeField] private float sendSpeed = 3.5f;
    [SerializeField] private float sellDelay = 1.8f;
    [SerializeField] private bool useCustomCargoBounds = true;
    [SerializeField] private Vector3 cargoLocalCenter = new Vector3(0f, 0.28f, 0f);
    [SerializeField] private Vector3 cargoLocalSize = new Vector3(0.95f, 0.75f, 1.05f);
    [SerializeField] private Vector3 cargoBoundsPadding = new Vector3(0.12f, 0.35f, 0.12f);

    [Header("Animation")]
    [SerializeField] private string activateTriggerName = "Activate";
    [SerializeField] private string activateBoolName = "IsActivate";

    [Header("Debug")]
    [SerializeField] private bool showCargoZoneGizmo = true;
    [SerializeField] private Color cargoZoneGizmoColor = new Color(1f, 0.82f, 0.1f, 0.24f);

    private Coroutine sendRoutine;
    private Collider[] leverColliders;
    private bool activated;

    private sealed class CargoLock
    {
        public TinyItem Item;
        public Transform Transform;
        public Transform OriginalParent;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public Rigidbody Rigidbody;
        public bool HadRigidbody;
        public bool WasKinematic;
        public bool UsedGravity;
        public Collider[] Colliders;
        public bool[] ColliderStates;
    }

    private void Awake()
    {
        CacheComponents();
    }

    private void OnValidate()
    {
        CacheComponents();
    }

    public bool CanInteract(Transform player)
    {
        if (activated || player == null || !IsWagonCloseEnough())
        {
            return false;
        }

        Bounds bounds = GetWorldBounds();
        return (bounds.ClosestPoint(player.position) - player.position).sqrMagnitude <= interactDistance * interactDistance;
    }

    public void TryActivate()
    {
        if (activated || !IsWagonCloseEnough())
        {
            return;
        }

        if (TinyNetcodeManager.IsNetworkActive)
        {
            TinyNetcodeManager.TrySendWagonSendLeverActivation(transform);
            return;
        }

        ActivateFromNetwork(true);
    }

    public void ActivateFromNetwork(bool serverAuthoritative)
    {
        if (activated || (serverAuthoritative && !IsWagonCloseEnough()))
        {
            return;
        }

        activated = true;
        PlayLeverAnimation();
        ReleaseAttachedPlayers();

        if (sendRoutine != null)
        {
            StopCoroutine(sendRoutine);
        }

        sendRoutine = StartCoroutine(SendRoutine(serverAuthoritative));
    }

    public Bounds GetWorldBounds()
    {
        if (leverColliders == null || leverColliders.Length == 0)
        {
            CacheComponents();
        }

        bool hasBounds = false;
        Bounds bounds = new Bounds(transform.position, Vector3.one * 0.25f);
        for (int i = 0; i < leverColliders.Length; i++)
        {
            Collider leverCollider = leverColliders[i];
            if (leverCollider == null || !leverCollider.enabled)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = leverCollider.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(leverCollider.bounds);
            }
        }

        return bounds;
    }

    private IEnumerator SendRoutine(bool serverAuthoritative)
    {
        TinyRailWagon wagon = targetWagon != null ? targetWagon : FindFirstObjectByType<TinyRailWagon>();
        if (wagon == null)
        {
            yield break;
        }

        Vector3 startPosition = wagon.transform.position;
        Quaternion startRotation = wagon.transform.rotation;
        List<CargoLock> cargoItems = LockCargoItems(wagon);
        Vector3 direction = GetSendDirection(wagon);
        Vector3 targetPosition = startPosition + direction * Mathf.Max(0f, sendDistance);
        float duration = Mathf.Max(0.05f, sendDistance / Mathf.Max(0.05f, sendSpeed));
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            wagon.transform.SetPositionAndRotation(Vector3.Lerp(startPosition, targetPosition, t), startRotation);
            yield return null;
        }

        wagon.transform.SetPositionAndRotation(targetPosition, startRotation);

        if (sellDelay > duration)
        {
            yield return new WaitForSeconds(sellDelay - duration);
        }

        int soldValue = SellCargoItems(cargoItems);
        if (serverAuthoritative && soldValue > 0)
        {
            TinyNetcodeManager.AddTeamMoneyAndStartFinalMinute(soldValue);
        }
        else if (serverAuthoritative)
        {
            TinyNetcodeManager.StartFinalMinute();
        }
    }

    private Vector3 GetSendDirection(TinyRailWagon wagon)
    {
        Transform reference = sendDirectionReference != null ? sendDirectionReference : wagon.transform;
        Vector3 direction = reference.forward;
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
    }

    private bool IsWagonCloseEnough()
    {
        TinyRailWagon wagon = targetWagon != null ? targetWagon : FindFirstObjectByType<TinyRailWagon>();
        if (wagon == null)
        {
            return false;
        }

        Bounds leverBounds = GetWorldBounds();
        Bounds wagonBounds = wagon.GetWorldBounds();
        Vector3 closestLeverPoint = leverBounds.ClosestPoint(wagonBounds.center);
        Vector3 closestWagonPoint = wagonBounds.ClosestPoint(leverBounds.center);
        return (closestLeverPoint - closestWagonPoint).sqrMagnitude <= wagonRequiredDistance * wagonRequiredDistance;
    }

    private List<CargoLock> LockCargoItems(TinyRailWagon wagon)
    {
        List<CargoLock> cargoItems = new List<CargoLock>();
        Bounds cargoBounds = GetCargoDetectionBounds(wagon);

        TinyItem[] items = FindObjectsByType<TinyItem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < items.Length; i++)
        {
            TinyItem item = items[i];
            if (item == null || item.IsNetworkHeld)
            {
                continue;
            }

            Bounds itemBounds = item.GetWorldBounds();
            if (!IsItemInsideCargoBounds(item.transform, itemBounds, cargoBounds))
            {
                continue;
            }

            cargoItems.Add(LockCargoItem(item, wagon.transform));
        }

        return cargoItems;
    }

    private Bounds GetCargoDetectionBounds(TinyRailWagon wagon)
    {
        if (useCustomCargoBounds)
        {
            Vector3 size = new Vector3(
                Mathf.Max(0.01f, cargoLocalSize.x),
                Mathf.Max(0.01f, cargoLocalSize.y),
                Mathf.Max(0.01f, cargoLocalSize.z));

            Vector3 half = size * 0.5f;
            Bounds bounds = new Bounds(wagon.transform.TransformPoint(cargoLocalCenter), Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        bounds.Encapsulate(wagon.transform.TransformPoint(cargoLocalCenter + Vector3.Scale(half, new Vector3(x, y, z))));
                    }
                }
            }

            bounds.Expand(cargoBoundsPadding);
            return bounds;
        }

        Bounds wagonBounds = wagon.GetWorldBounds();
        wagonBounds.Expand(cargoBoundsPadding);
        return wagonBounds;
    }

    private static bool IsItemInsideCargoBounds(Transform itemTransform, Bounds itemBounds, Bounds cargoBounds)
    {
        if (itemTransform != null && cargoBounds.Contains(itemTransform.position))
        {
            return true;
        }

        if (cargoBounds.Contains(itemBounds.center) || cargoBounds.Intersects(itemBounds))
        {
            return true;
        }

        Vector3 min = itemBounds.min;
        Vector3 max = itemBounds.max;
        return cargoBounds.Contains(new Vector3(min.x, min.y, min.z))
            || cargoBounds.Contains(new Vector3(min.x, min.y, max.z))
            || cargoBounds.Contains(new Vector3(min.x, max.y, min.z))
            || cargoBounds.Contains(new Vector3(min.x, max.y, max.z))
            || cargoBounds.Contains(new Vector3(max.x, min.y, min.z))
            || cargoBounds.Contains(new Vector3(max.x, min.y, max.z))
            || cargoBounds.Contains(new Vector3(max.x, max.y, min.z))
            || cargoBounds.Contains(new Vector3(max.x, max.y, max.z));
    }

    private CargoLock LockCargoItem(TinyItem item, Transform wagon)
    {
        Transform itemTransform = item.transform;
        CargoLock cargo = new CargoLock
        {
            Item = item,
            Transform = itemTransform,
            OriginalParent = itemTransform.parent,
            LocalPosition = wagon.InverseTransformPoint(itemTransform.position),
            LocalRotation = Quaternion.Inverse(wagon.rotation) * itemTransform.rotation,
            Rigidbody = itemTransform.GetComponent<Rigidbody>()
        };

        cargo.HadRigidbody = cargo.Rigidbody != null;
        if (cargo.Rigidbody != null)
        {
            cargo.WasKinematic = cargo.Rigidbody.isKinematic;
            cargo.UsedGravity = cargo.Rigidbody.useGravity;
            cargo.Rigidbody.isKinematic = true;
            cargo.Rigidbody.useGravity = false;
#if UNITY_6000_0_OR_NEWER
            cargo.Rigidbody.linearVelocity = Vector3.zero;
#else
            cargo.Rigidbody.velocity = Vector3.zero;
#endif
            cargo.Rigidbody.angularVelocity = Vector3.zero;
        }

        cargo.Colliders = itemTransform.GetComponentsInChildren<Collider>(true);
        cargo.ColliderStates = new bool[cargo.Colliders.Length];
        for (int i = 0; i < cargo.Colliders.Length; i++)
        {
            if (cargo.Colliders[i] == null)
            {
                continue;
            }

            cargo.ColliderStates[i] = cargo.Colliders[i].enabled;
            cargo.Colliders[i].enabled = false;
        }

        itemTransform.SetParent(wagon, true);
        itemTransform.localPosition = cargo.LocalPosition;
        itemTransform.localRotation = cargo.LocalRotation;
        return cargo;
    }

    private int SellCargoItems(List<CargoLock> cargoItems)
    {
        int totalValue = 0;
        if (cargoItems == null)
        {
            return totalValue;
        }

        for (int i = 0; i < cargoItems.Count; i++)
        {
            CargoLock cargo = cargoItems[i];
            if (cargo == null || cargo.Item == null)
            {
                continue;
            }

            totalValue += cargo.Item.Value;
            Destroy(cargo.Item.gameObject);
        }

        return totalValue;
    }

    private void ReleaseAttachedPlayers()
    {
        TinyFirstPersonController[] players = FindObjectsByType<TinyFirstPersonController>(FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null)
            {
                players[i].ForceReleaseWagon(targetWagon);
            }
        }
    }

    private void PlayLeverAnimation()
    {
        if (leverAnimator == null)
        {
            leverAnimator = GetComponentInChildren<Animator>();
        }

        if (leverAnimator == null)
        {
            return;
        }

        TrySetAnimatorTrigger(leverAnimator, activateTriggerName);
        TrySetAnimatorBool(leverAnimator, activateBoolName, true);
    }

    private static void TrySetAnimatorTrigger(Animator animator, string triggerName)
    {
        if (animator == null || string.IsNullOrWhiteSpace(triggerName))
        {
            return;
        }

        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].name == triggerName && parameters[i].type == AnimatorControllerParameterType.Trigger)
            {
                animator.SetTrigger(triggerName);
                return;
            }
        }
    }

    private static void TrySetAnimatorBool(Animator animator, string boolName, bool value)
    {
        if (animator == null || string.IsNullOrWhiteSpace(boolName))
        {
            return;
        }

        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].name == boolName && parameters[i].type == AnimatorControllerParameterType.Bool)
            {
                animator.SetBool(boolName, value);
                return;
            }
        }
    }

    private void CacheComponents()
    {
        leverColliders = GetComponentsInChildren<Collider>(true);
        if (leverAnimator == null)
        {
            leverAnimator = GetComponentInChildren<Animator>();
        }
    }

    private void OnDrawGizmos()
    {
        if (!showCargoZoneGizmo || !useCustomCargoBounds)
        {
            return;
        }

        TinyRailWagon wagon = targetWagon != null ? targetWagon : FindFirstObjectByType<TinyRailWagon>();
        if (wagon == null)
        {
            return;
        }

        Matrix4x4 previousMatrix = Gizmos.matrix;
        Color previousColor = Gizmos.color;
        Vector3 size = new Vector3(
            Mathf.Max(0.01f, cargoLocalSize.x),
            Mathf.Max(0.01f, cargoLocalSize.y),
            Mathf.Max(0.01f, cargoLocalSize.z));

        Gizmos.matrix = Matrix4x4.TRS(wagon.transform.position, wagon.transform.rotation, Vector3.one);
        Gizmos.color = cargoZoneGizmoColor;
        Gizmos.DrawCube(cargoLocalCenter, size);
        Gizmos.color = new Color(cargoZoneGizmoColor.r, cargoZoneGizmoColor.g, cargoZoneGizmoColor.b, Mathf.Clamp01(cargoZoneGizmoColor.a * 3f));
        Gizmos.DrawWireCube(cargoLocalCenter, size);

        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
    }
}
