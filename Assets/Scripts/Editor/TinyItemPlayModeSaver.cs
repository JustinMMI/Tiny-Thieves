using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class TinyItemPlayModeSaver
{
    private struct Snapshot
    {
        public GlobalObjectId Id;
        public string Json;
    }

    private static readonly List<Snapshot> snapshots = new List<Snapshot>();

    static TinyItemPlayModeSaver()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            CaptureTinyItems();
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            RestoreTinyItems();
        }
    }

    private static void CaptureTinyItems()
    {
        snapshots.Clear();
        TinyItem[] items = Object.FindObjectsByType<TinyItem>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        for (int i = 0; i < items.Length; i++)
        {
            TinyItem item = items[i];
            if (item == null)
            {
                continue;
            }

            snapshots.Add(new Snapshot
            {
                Id = GlobalObjectId.GetGlobalObjectIdSlow(item),
                Json = EditorJsonUtility.ToJson(item)
            });
        }
    }

    private static void RestoreTinyItems()
    {
        for (int i = 0; i < snapshots.Count; i++)
        {
            Object target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(snapshots[i].Id);
            TinyItem item = target as TinyItem;
            if (item == null)
            {
                continue;
            }

            EditorJsonUtility.FromJsonOverwrite(snapshots[i].Json, item);
            EditorUtility.SetDirty(item);
            PrefabUtility.RecordPrefabInstancePropertyModifications(item);

            Scene scene = item.gameObject.scene;
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        snapshots.Clear();
    }
}
