using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
[AddComponentMenu("Tiny Thieves/Damage Zone")]
public sealed class TinyDamageZone : MonoBehaviour
{
    [SerializeField] private float damagePerSecond = 20f;
    [SerializeField] private bool useBoundsFallbackWhenNotTrigger = true;
    [SerializeField] private Vector3 boundsFallbackPadding = new Vector3(0f, 0.18f, 0f);

    private readonly Collider[] overlapResults = new Collider[32];
    private readonly List<TinyFirstPersonController> damagedPlayers = new List<TinyFirstPersonController>();
    private Collider zoneCollider;

    private void Awake()
    {
        zoneCollider = GetComponent<Collider>();
    }

    private void Reset()
    {
        Collider zoneCollider = GetComponent<Collider>();
        if (zoneCollider != null)
        {
            zoneCollider.isTrigger = true;
        }
    }

    private void Update()
    {
        if (!useBoundsFallbackWhenNotTrigger || zoneCollider == null || zoneCollider.isTrigger)
        {
            return;
        }

        Bounds bounds = zoneCollider.bounds;
        int hitCount = Physics.OverlapBoxNonAlloc(
            bounds.center,
            bounds.extents + boundsFallbackPadding,
            overlapResults,
            Quaternion.identity,
            ~0,
            QueryTriggerInteraction.Ignore);

        damagedPlayers.Clear();
        for (int i = 0; i < hitCount; i++)
        {
            TinyFirstPersonController player = GetPlayer(overlapResults[i]);
            if (player == null || damagedPlayers.Contains(player))
            {
                continue;
            }

            damagedPlayers.Add(player);
            DamagePlayer(player, Time.deltaTime);
        }
    }

    private void OnTriggerStay(Collider other)
    {
        DamagePlayer(GetPlayer(other), Time.deltaTime);
    }

    private void OnCollisionStay(Collision collision)
    {
        if (collision == null)
        {
            return;
        }

        DamagePlayer(GetPlayer(collision.collider), Time.deltaTime);
    }

    private static TinyFirstPersonController GetPlayer(Collider other)
    {
        return other != null ? other.GetComponentInParent<TinyFirstPersonController>() : null;
    }

    private void DamagePlayer(TinyFirstPersonController player, float deltaTime)
    {
        if (player == null || !player.enabled || player.IsDead)
        {
            return;
        }

        player.TakeDamage(damagePerSecond * deltaTime);
    }
}
