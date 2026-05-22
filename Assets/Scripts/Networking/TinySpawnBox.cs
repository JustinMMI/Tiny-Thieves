using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
[AddComponentMenu("Tiny Thieves/Spawn Box")]
public sealed class TinySpawnBox : MonoBehaviour
{
    private enum SpawnBoxSkin
    {
        Vert,
        Rouge,
        Bleu,
        Orange
    }

    [SerializeField] private SpawnBoxSkin boxSkin;
    [SerializeField] private int playerSkinIndex;
    [SerializeField] private float openDelay = 0.35f;
    [SerializeField] private Animator animator;
    [SerializeField] private string openTriggerName = "Open";
    [SerializeField] private bool openAutomaticallyAfterSpawn = true;

    private Coroutine openRoutine;
    private bool opened;
    private bool canOpen;
    private bool wasPreparedForSpawn;
    private float partyStartRealtime = -1f;
    private Collider triggerCollider;

    public int PlayerSkinIndex => Mathf.Clamp(playerSkinIndex, 0, 3);
    public bool WasPreparedForSpawn => wasPreparedForSpawn;

    public bool MatchesSpawnedSkin(int skinIndex)
    {
        int clampedSkin = Mathf.Clamp(skinIndex, 0, 3);
        return wasPreparedForSpawn
            ? playerSkinIndex == clampedSkin
            : PlayerSkinIndex == clampedSkin;
    }

    public bool ContainsPlayer(Transform player)
    {
        if (player == null)
        {
            return false;
        }

        if (triggerCollider == null)
        {
            triggerCollider = GetComponent<Collider>();
        }

        if (triggerCollider == null)
        {
            return false;
        }

        CharacterController characterController = player.GetComponent<CharacterController>();
        if (characterController != null)
        {
            Vector3 worldCenter = player.TransformPoint(characterController.center);
            float halfHeight = Mathf.Max(0f, characterController.height * 0.5f - characterController.radius);
            Vector3 top = worldCenter + Vector3.up * halfHeight;
            Vector3 bottom = worldCenter - Vector3.up * halfHeight;
            return ContainsPoint(worldCenter) || ContainsPoint(top) || ContainsPoint(bottom);
        }

        return ContainsPoint(player.position);
    }

    private void Reset()
    {
        animator = GetComponent<Animator>();
        Collider triggerCollider = GetComponent<Collider>();
        if (triggerCollider != null)
        {
            triggerCollider.isTrigger = true;
        }
    }

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        triggerCollider = GetComponent<Collider>();
        if (triggerCollider != null)
        {
            triggerCollider.isTrigger = true;
            triggerCollider.enabled = false;
        }
    }

    public void PrepareForSpawn(int skinIndex, int playerSlot, float gameStartRealtime)
    {
        playerSkinIndex = Mathf.Clamp(skinIndex, 0, 3);
        opened = false;
        canOpen = true;
        wasPreparedForSpawn = true;
        partyStartRealtime = gameStartRealtime >= 0f ? gameStartRealtime : Time.realtimeSinceStartup;
        SetTriggerColliderEnabled(!openAutomaticallyAfterSpawn);

        if (openAutomaticallyAfterSpawn)
        {
            ScheduleOpen();
        }
    }

    public void ResetBox()
    {
        if (openRoutine != null)
        {
            StopCoroutine(openRoutine);
            openRoutine = null;
        }

        opened = false;
        canOpen = false;
        wasPreparedForSpawn = false;
        partyStartRealtime = -1f;
        SetTriggerColliderEnabled(false);

        if (animator != null && !string.IsNullOrWhiteSpace(openTriggerName))
        {
            animator.ResetTrigger(openTriggerName);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!canOpen || !IsPlayerCollider(other))
        {
            return;
        }

        ScheduleOpen();
    }

    private void ScheduleOpen()
    {
        if (!isActiveAndEnabled || !canOpen || opened || openRoutine != null)
        {
            return;
        }

        openRoutine = StartCoroutine(OpenAfterDelay());
    }

    private IEnumerator OpenAfterDelay()
    {
        float startTime = partyStartRealtime >= 0f ? partyStartRealtime : Time.realtimeSinceStartup;
        float remainingDelay = Mathf.Max(0f, openDelay - (Time.realtimeSinceStartup - startTime));
        if (remainingDelay > 0f)
        {
            yield return new WaitForSecondsRealtime(remainingDelay);
        }

        Open();
        openRoutine = null;
    }

    private void Open()
    {
        if (opened)
        {
            return;
        }

        opened = true;
        canOpen = false;
        SetTriggerColliderEnabled(false);
        if (animator != null && !string.IsNullOrWhiteSpace(openTriggerName))
        {
            animator.ResetTrigger(openTriggerName);
            animator.SetTrigger(openTriggerName);
        }
    }

    private void SetTriggerColliderEnabled(bool enabled)
    {
        if (triggerCollider != null)
        {
            triggerCollider.enabled = enabled;
        }
    }

    private bool ContainsPoint(Vector3 point)
    {
        if (triggerCollider == null)
        {
            return false;
        }

        if (triggerCollider is BoxCollider boxCollider)
        {
            Vector3 localPoint = boxCollider.transform.InverseTransformPoint(point) - boxCollider.center;
            Vector3 halfSize = boxCollider.size * 0.5f + Vector3.one * 0.03f;
            return Mathf.Abs(localPoint.x) <= halfSize.x
                && Mathf.Abs(localPoint.y) <= halfSize.y
                && Mathf.Abs(localPoint.z) <= halfSize.z;
        }

        Vector3 closestPoint = triggerCollider.ClosestPoint(point);
        return (closestPoint - point).sqrMagnitude <= 0.0004f;
    }

    private static bool IsPlayerCollider(Collider other)
    {
        Transform current = other != null ? other.transform : null;
        while (current != null)
        {
            if (current.CompareTag("Player"))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }
}
