using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("Tiny Thieves/Item Spawn Manager")]
public sealed class TinyItemSpawnManager : MonoBehaviour
{
    [Serializable]
    private sealed class ItemEntry
    {
        public TinyItem Template;
        [Min(0f)] public float WeightOverride;
        [Min(0)] public int MaxCount;
    }

    [Header("Items")]
    [SerializeField] private Transform itemTemplateRoot;
    [SerializeField] private ItemEntry[] itemTable = Array.Empty<ItemEntry>();
    [SerializeField] private bool autoCollectTemplatesFromRoot = true;
    [SerializeField] private bool collectSceneTemplatesIfRootEmpty = true;
    [SerializeField] private bool hideTemplatesOnStart = true;
    [SerializeField] private bool logSpawnSummary = true;

    [Header("Spawn")]
    [SerializeField] private TinyItemSpawnZone[] spawnZones = Array.Empty<TinyItemSpawnZone>();
    [SerializeField, Min(1)] private int fallbackSeed = 12345;
    [SerializeField] private bool spawnOnStart = true;
    [SerializeField] private bool clearPreviouslySpawnedItems = true;

    private readonly List<TinyItem> spawnedItems = new List<TinyItem>();
    private bool spawned;

    private void Start()
    {
        if (spawnOnStart)
        {
            SpawnForCurrentGame();
        }
    }

    private void OnValidate()
    {
        if (!Application.isPlaying
            && autoCollectTemplatesFromRoot
            && itemTemplateRoot != null
            && (itemTable == null || itemTable.Length == 0))
        {
            RebuildAutoReferences(true);
        }
    }

    public void SpawnForCurrentGame()
    {
        if (spawned)
        {
            return;
        }

        spawned = true;
        RebuildAutoReferences(false);
        if (clearPreviouslySpawnedItems)
        {
            ClearSpawnedItems();
        }

        if (hideTemplatesOnStart)
        {
            SetTemplatesVisible(false);
        }

        int seed = TinyNetcodeManager.CurrentGameplaySeed;
        if (seed == 0)
        {
            seed = fallbackSeed;
        }

        System.Random random = new System.Random(seed);
        Dictionary<TinyItem, int> spawnedCountByTemplate = new Dictionary<TinyItem, int>();
        int skippedEmptyZones = 0;
        for (int zoneIndex = 0; zoneIndex < spawnZones.Length; zoneIndex++)
        {
            TinyItemSpawnZone zone = spawnZones[zoneIndex];
            if (zone == null)
            {
                continue;
            }

            if (random.NextDouble() < zone.EmptyChance)
            {
                skippedEmptyZones++;
                continue;
            }

            int itemCount = random.Next(zone.MinItems, zone.MaxItems + 1);
            for (int i = 0; i < itemCount; i++)
            {
                TinyItem template = PickItemTemplate(random, spawnedCountByTemplate);
                if (template == null)
                {
                    continue;
                }

                Pose pose = zone.GetRandomSpawnPose(random);
                TinyItem item = Instantiate(template, pose.position, pose.rotation, transform);
                item.name = template.name + "_Spawned_" + zoneIndex + "_" + i;
                item.gameObject.SetActive(true);
                ResetSpawnedPhysics(item);
                spawnedItems.Add(item);
                spawnedCountByTemplate.TryGetValue(template, out int count);
                spawnedCountByTemplate[template] = count + 1;
            }
        }

        TinyNetcodeManager.RebuildSyncedEntitiesNow();
        if (logSpawnSummary)
        {
            Debug.Log($"Tiny item spawn: {spawnedItems.Count} items spawned from {GetValidEntryCount()} templates across {GetValidZoneCount()} zones ({skippedEmptyZones} empty rolls).", this);
        }
    }

    [ContextMenu("Refresh Item Table From Root")]
    public void RefreshItemTableFromRoot()
    {
        RebuildAutoReferences(true);
    }

    private TinyItem PickItemTemplate(System.Random random, Dictionary<TinyItem, int> spawnedCountByTemplate)
    {
        if (itemTable == null || itemTable.Length == 0)
        {
            return null;
        }

        float totalWeight = 0f;
        for (int i = 0; i < itemTable.Length; i++)
        {
            totalWeight += GetEntryWeight(itemTable[i], spawnedCountByTemplate);
        }

        if (totalWeight <= 0f)
        {
            return null;
        }

        float roll = (float)random.NextDouble() * totalWeight;
        for (int i = 0; i < itemTable.Length; i++)
        {
            roll -= GetEntryWeight(itemTable[i], spawnedCountByTemplate);
            if (roll <= 0f && itemTable[i] != null)
            {
                return itemTable[i].Template;
            }
        }

        return null;
    }

    private static float GetEntryWeight(ItemEntry entry, Dictionary<TinyItem, int> spawnedCountByTemplate)
    {
        if (entry == null || entry.Template == null)
        {
            return 0f;
        }

        if (entry.MaxCount > 0
            && spawnedCountByTemplate != null
            && spawnedCountByTemplate.TryGetValue(entry.Template, out int count)
            && count >= entry.MaxCount)
        {
            return 0f;
        }

        if (entry.WeightOverride > 0f)
        {
            return entry.WeightOverride;
        }

        return 1f / Mathf.Max(1f, entry.Template.Value);
    }

    private void RebuildAutoReferences(bool forceRefreshItemTable)
    {
        if ((spawnZones == null || spawnZones.Length == 0))
        {
            spawnZones = FindObjectsByType<TinyItemSpawnZone>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            Array.Sort(spawnZones, (left, right) => CompareComponentsByHierarchyPath(left, right));
        }

        if ((autoCollectTemplatesFromRoot || forceRefreshItemTable) && itemTemplateRoot != null)
        {
            Dictionary<TinyItem, ItemEntry> previousEntries = new Dictionary<TinyItem, ItemEntry>();
            if (itemTable != null)
            {
                for (int i = 0; i < itemTable.Length; i++)
                {
                    ItemEntry entry = itemTable[i];
                    if (entry != null && entry.Template != null)
                    {
                        previousEntries[entry.Template] = entry;
                    }
                }
            }

            TinyItem[] templates = itemTemplateRoot.GetComponentsInChildren<TinyItem>(true);
            if (templates.Length == 0 && collectSceneTemplatesIfRootEmpty)
            {
                templates = FindObjectsByType<TinyItem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            }

            Array.Sort(templates, (left, right) => CompareComponentsByHierarchyPath(left, right));
            itemTable = new ItemEntry[templates.Length];
            for (int i = 0; i < templates.Length; i++)
            {
                if (previousEntries.TryGetValue(templates[i], out ItemEntry previousEntry))
                {
                    itemTable[i] = new ItemEntry
                    {
                        Template = templates[i],
                        WeightOverride = previousEntry.WeightOverride,
                        MaxCount = previousEntry.MaxCount
                    };
                }
                else
                {
                    itemTable[i] = new ItemEntry { Template = templates[i] };
                }
            }
        }
    }

    private int GetValidEntryCount()
    {
        int count = 0;
        if (itemTable == null)
        {
            return count;
        }

        for (int i = 0; i < itemTable.Length; i++)
        {
            if (itemTable[i] != null && itemTable[i].Template != null)
            {
                count++;
            }
        }

        return count;
    }

    private int GetValidZoneCount()
    {
        int count = 0;
        if (spawnZones == null)
        {
            return count;
        }

        for (int i = 0; i < spawnZones.Length; i++)
        {
            if (spawnZones[i] != null)
            {
                count++;
            }
        }

        return count;
    }

    private void SetTemplatesVisible(bool visible)
    {
        if (itemTable == null)
        {
            return;
        }

        for (int i = 0; i < itemTable.Length; i++)
        {
            TinyItem template = itemTable[i]?.Template;
            if (template != null)
            {
                template.gameObject.SetActive(visible);
            }
        }
    }

    private void ClearSpawnedItems()
    {
        for (int i = spawnedItems.Count - 1; i >= 0; i--)
        {
            if (spawnedItems[i] != null)
            {
                Destroy(spawnedItems[i].gameObject);
            }
        }

        spawnedItems.Clear();
    }

    private static void ResetSpawnedPhysics(TinyItem item)
    {
        if (item == null || !item.TryGetComponent(out Rigidbody itemRigidbody))
        {
            return;
        }

        itemRigidbody.isKinematic = false;
        itemRigidbody.useGravity = true;
#if UNITY_6000_0_OR_NEWER
        itemRigidbody.linearVelocity = Vector3.zero;
#else
        itemRigidbody.velocity = Vector3.zero;
#endif
        itemRigidbody.angularVelocity = Vector3.zero;
        itemRigidbody.WakeUp();
    }

    private static int CompareComponentsByHierarchyPath(Component left, Component right)
    {
        return string.CompareOrdinal(GetHierarchyPath(left != null ? left.transform : null), GetHierarchyPath(right != null ? right.transform : null));
    }

    private static string GetHierarchyPath(Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        string path = transform.name;
        Transform current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }
}
