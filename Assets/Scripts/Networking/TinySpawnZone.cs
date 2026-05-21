using UnityEngine;

[AddComponentMenu("Tiny Thieves/Spawn Zone")]
public sealed class TinySpawnZone : MonoBehaviour
{
    [SerializeField] private Transform[] playerSpawnPoints = new Transform[4];
    [SerializeField] private TinySpawnBox[] spawnBoxesBySkin = new TinySpawnBox[4];

    public int SpawnPointCount => playerSpawnPoints != null ? playerSpawnPoints.Length : 0;

    public bool TryGetSpawnPoint(int playerSlot, out Vector3 position, out Quaternion rotation)
    {
        position = transform.position;
        rotation = transform.rotation;

        Transform spawnPoint = ResolveSpawnPoint(playerSlot);
        if (spawnPoint == null)
        {
            return false;
        }

        position = spawnPoint.position;
        rotation = spawnPoint.rotation;
        return true;
    }

    public bool TryGetSpawnPointForSkin(int skinIndex, int fallbackPlayerSlot, out Vector3 position, out Quaternion rotation)
    {
        position = transform.position;
        rotation = transform.rotation;

        Transform spawnPoint = ResolveSpawnPoint(GetSpawnPointIndexForSkin(skinIndex, fallbackPlayerSlot));
        if (spawnPoint != null)
        {
            position = spawnPoint.position;
            rotation = spawnPoint.rotation;
            return true;
        }

        return TryGetSpawnPoint(fallbackPlayerSlot, out position, out rotation);
    }

    public void ConfigureSpawnBoxes(int[] playerSkinsBySlot, float gameStartRealtime)
    {
        ResetSpawnBoxes();

        if (playerSkinsBySlot == null)
        {
            return;
        }

        for (int slot = 0; slot < playerSkinsBySlot.Length; slot++)
        {
            int skinIndex = playerSkinsBySlot[slot];
            if (skinIndex < 0)
            {
                continue;
            }

            TinySpawnBox spawnBox = ResolveSpawnBox(skinIndex);
            if (spawnBox == null)
            {
                continue;
            }

            spawnBox.gameObject.SetActive(true);
            spawnBox.PrepareForSpawn(skinIndex, slot, gameStartRealtime);
            Debug.Log($"Tiny spawn box armed: skin {skinIndex}, slot {slot}, box {spawnBox.name}.");
        }
    }

    private static int GetSpawnPointIndexForSkin(int skinIndex, int fallbackPlayerSlot)
    {
        switch (skinIndex)
        {
            case 0: // Vert
                return 0;
            case 1: // Rouge
                return 1;
            case 3: // Orange
                return 2;
            case 2: // Bleu
                return 3;
            default:
                return fallbackPlayerSlot;
        }
    }

    private TinySpawnBox ResolveSpawnBox(int skinIndex)
    {
        if (spawnBoxesBySkin == null)
        {
            return null;
        }

        TinySpawnBox namedSpawnBox = ResolveSpawnBoxByName(skinIndex);
        if (namedSpawnBox != null)
        {
            return namedSpawnBox;
        }

        for (int i = 0; i < spawnBoxesBySkin.Length; i++)
        {
            TinySpawnBox spawnBox = spawnBoxesBySkin[i];
            if (spawnBox != null && spawnBox.PlayerSkinIndex == skinIndex)
            {
                return spawnBox;
            }
        }

        if (skinIndex >= 0 && skinIndex < spawnBoxesBySkin.Length)
        {
            return spawnBoxesBySkin[skinIndex];
        }

        return null;
    }

    private TinySpawnBox ResolveSpawnBoxByName(int skinIndex)
    {
        string token = GetSkinNameToken(skinIndex);
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        for (int i = 0; i < spawnBoxesBySkin.Length; i++)
        {
            TinySpawnBox spawnBox = spawnBoxesBySkin[i];
            if (spawnBox != null && ContainsToken(spawnBox.name, token))
            {
                return spawnBox;
            }
        }

        return null;
    }

    private static string GetSkinNameToken(int skinIndex)
    {
        switch (skinIndex)
        {
            case 0:
                return "vert";
            case 1:
                return "rouge";
            case 2:
                return "bleu";
            case 3:
                return "orange";
            default:
                return null;
        }
    }

    private static bool ContainsToken(string objectName, string token)
    {
        return !string.IsNullOrWhiteSpace(objectName)
            && objectName.ToLowerInvariant().Contains(token);
    }

    private Transform ResolveSpawnPoint(int playerSlot)
    {
        if (playerSpawnPoints != null && playerSpawnPoints.Length > 0)
        {
            int index = Mathf.Clamp(playerSlot, 0, playerSpawnPoints.Length - 1);
            if (playerSpawnPoints[index] != null)
            {
                return playerSpawnPoints[index];
            }
        }

        Transform[] childSpawnPoints = GetChildSpawnPoints();
        if (childSpawnPoints.Length == 0)
        {
            return null;
        }

        return childSpawnPoints[Mathf.Clamp(playerSlot, 0, childSpawnPoints.Length - 1)];
    }

    private Transform[] GetChildSpawnPoints()
    {
        int count = 0;
        for (int i = 0; i < transform.childCount; i++)
        {
            if (IsSpawnPointName(transform.GetChild(i).name))
            {
                count++;
            }
        }

        Transform[] spawnPoints = new Transform[count];
        int index = 0;
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            if (IsSpawnPointName(child.name))
            {
                spawnPoints[index++] = child;
            }
        }

        System.Array.Sort(spawnPoints, (left, right) => string.CompareOrdinal(left.name, right.name));
        return spawnPoints;
    }

    private static bool IsSpawnPointName(string objectName)
    {
        return !string.IsNullOrWhiteSpace(objectName)
            && objectName.Trim().ToLowerInvariant().StartsWith("spawn");
    }

    public void ClearSpawnBoxes()
    {
        ResetSpawnBoxes();
    }

    private void ResetSpawnBoxes()
    {
        if (spawnBoxesBySkin == null)
        {
            return;
        }

        for (int i = 0; i < spawnBoxesBySkin.Length; i++)
        {
            TinySpawnBox spawnBox = spawnBoxesBySkin[i];
            if (spawnBox == null)
            {
                continue;
            }

            spawnBox.ResetBox();
            spawnBox.gameObject.SetActive(true);
        }
    }

    private void OnDrawGizmos()
    {
        if (playerSpawnPoints == null)
        {
            return;
        }

        Gizmos.color = new Color(0.25f, 1f, 0.35f, 0.9f);
        for (int i = 0; i < playerSpawnPoints.Length; i++)
        {
            Transform spawnPoint = playerSpawnPoints[i];
            if (spawnPoint == null)
            {
                continue;
            }

            Gizmos.DrawSphere(spawnPoint.position, 0.05f);
            Gizmos.DrawLine(spawnPoint.position, spawnPoint.position + spawnPoint.forward * 0.18f);
        }
    }
}
