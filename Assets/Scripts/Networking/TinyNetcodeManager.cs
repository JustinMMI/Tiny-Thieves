using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

#if TINY_HAS_RELAY
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
#endif

public sealed class TinyNetcodeManager : MonoBehaviour
{
    private const ushort Port = 7777;
    private const string Localhost = "127.0.0.1";
    private const bool UseUnityRelay = true;
    private const int MaxRelayConnections = 3;
    private const string RelayConnectionType = "udp";
    private const string RelayJoinCodeFileName = "relay_join_code.txt";
    private const float PlayerSendInterval = 0.016f;
    private const float EntitySendInterval = 0.016f;
    private const float ClientRetryInterval = 1f;
    private const float RemoteFollowSharpness = 45f;
    private const float RemoteEntityFollowSharpness = 65f;
    private const float RemoteEntitySnapDistance = 0.7f;
    private const float RemoteEntityTargetLifetime = 0.08f;
    private const float EntityAuthorityHoldTime = 0.35f;
    private const float WagonPushInputTimeout = 0.12f;
    private const string PlayerStateMessage = "TinyPlayerState";
    private const string EntityStateMessage = "TinyEntityState";
    private const string WorldIntentMessage = "TinyWorldIntent";
    private const byte IntentPickupItem = 1;
    private const byte IntentReleaseItem = 2;
    private const byte IntentPushWagon = 3;

    private readonly Dictionary<ulong, RemotePlayer> remotePlayers = new Dictionary<ulong, RemotePlayer>();
    private readonly Dictionary<string, Transform> syncedEntities = new Dictionary<string, Transform>();
    private readonly Dictionary<string, TimedPoseState> remoteEntityTargets = new Dictionary<string, TimedPoseState>();
    private readonly Dictionary<string, PoseState> lastSentEntityPoses = new Dictionary<string, PoseState>();
    private readonly Dictionary<string, EntityAuthority> entityAuthorities = new Dictionary<string, EntityAuthority>();
    private readonly Dictionary<string, ulong> heldEntityOwners = new Dictionary<string, ulong>();
    private readonly Dictionary<ulong, WagonPushState> activeWagonPushes = new Dictionary<ulong, WagonPushState>();

    private static TinyNetcodeManager instance;
    private NetworkManager networkManager;
    private UnityTransport transport;
    private Component localPlayer;
    private float playerSendTimer;
    private float entitySendTimer;
    private float clientRetryTimer;
    private bool started;
    private bool networkStartInProgress;
    private bool handlersRegistered;
    private bool callbacksRegistered;
    private int appliedLocalSkin = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateForScene()
    {
        if (FindFirstObjectByType<TinyNetcodeManager>() != null)
        {
            return;
        }

        GameObject networkObject = new GameObject("Tiny Netcode Manager");
        DontDestroyOnLoad(networkObject);
        networkObject.AddComponent<TinyNetcodeManager>();
    }

    private void Awake()
    {
        instance = this;
        EnsureNetworkManager();
    }

    private void Start()
    {
        localPlayer = FindFirstComponentByTypeName("TinyFirstPersonController");
        if (localPlayer != null)
        {
            ApplyLocalSkin();
        }

        RebuildEntityCache();
        StartNetworkRole();
    }

    private void Update()
    {
        if (networkManager == null)
        {
            return;
        }

        RefreshSceneReferences();

        if (!IsEditorHost() && !networkManager.IsClient && !networkManager.IsListening)
        {
            RetryClientStart();
            return;
        }

        RegisterMessageHandlers();
        ApplyLocalSkin();
        ProcessServerWagonPushes();
        SendLocalState();
        UpdateRemotePlayers();
        ApplyRemoteEntityTargets();
    }

    private void OnDestroy()
    {
        if (networkManager != null && handlersRegistered && networkManager.CustomMessagingManager != null)
        {
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(PlayerStateMessage);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(EntityStateMessage);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(WorldIntentMessage);
        }

        if (networkManager != null && callbacksRegistered)
        {
            networkManager.OnClientDisconnectCallback -= OnClientDisconnect;
        }

        if (instance == this)
        {
            instance = null;
        }
    }

    public static bool IsClientOnlyActive =>
        instance != null
        && instance.networkManager != null
        && instance.networkManager.IsListening
        && !instance.networkManager.IsServer;

    public static bool TrySendItemPickup(Transform item)
    {
        return instance != null && instance.SendItemPickupIntent(item);
    }

    public static bool TrySendItemRelease(Transform item, Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        return instance != null && instance.SendItemReleaseIntent(item, position, rotation, velocity);
    }

    public static bool TrySendWagonPush(Transform wagon, float input, Vector3 playerPosition, Quaternion playerRotation)
    {
        return instance != null && instance.SendWagonPushIntent(wagon, input, playerPosition, playerRotation);
    }

    private void EnsureNetworkManager()
    {
        networkManager = FindFirstObjectByType<NetworkManager>();
        if (networkManager == null)
        {
            GameObject managerObject = new GameObject("Network Manager");
            DontDestroyOnLoad(managerObject);
            networkManager = managerObject.AddComponent<NetworkManager>();
        }

        transport = networkManager.GetComponent<UnityTransport>();
        if (transport == null)
        {
            transport = networkManager.gameObject.AddComponent<UnityTransport>();
        }

        NetworkConfig config = networkManager.NetworkConfig ?? new NetworkConfig();
        networkManager.NetworkConfig = config;
        config.NetworkTransport = transport;
        config.EnableSceneManagement = false;
        config.ConnectionApproval = false;
        config.AutoSpawnPlayerPrefabClientSide = false;
        config.PlayerPrefab = null;
        config.TickRate = 60;
        transport.SetConnectionData(Localhost, Port, Localhost);

        if (!callbacksRegistered)
        {
            networkManager.OnClientDisconnectCallback += OnClientDisconnect;
            callbacksRegistered = true;
        }
    }

    private void StartNetworkRole()
    {
        if (started || networkStartInProgress || networkManager == null)
        {
            return;
        }

        _ = StartNetworkRoleAsync();
    }

    private async Task StartNetworkRoleAsync()
    {
        if (started || networkStartInProgress || networkManager == null)
        {
            return;
        }

        if (IsEditorHost())
        {
            started = await TryStartHostAsync();
        }
        else
        {
            started = await TryStartClientAsync();
        }

    }

    private void RetryClientStart()
    {
        clientRetryTimer -= Time.deltaTime;
        if (clientRetryTimer > 0f)
        {
            return;
        }

        clientRetryTimer = ClientRetryInterval;
        _ = TryStartClientAsync();
    }

    private async Task<bool> TryStartClientAsync()
    {
        if (networkManager == null || networkManager.IsListening || networkStartInProgress)
        {
            return false;
        }

#if TINY_HAS_RELAY
        if (UseUnityRelay)
        {
            networkStartInProgress = true;
            string joinCode = GetRelayJoinCode();
            if (string.IsNullOrWhiteSpace(joinCode))
            {
                networkStartInProgress = false;
                Debug.Log("Tiny Relay client waiting for a join code. Use -join CODE or put the code in " + GetRelayJoinCodePath());
                return false;
            }

            try
            {
                await EnsureUnityServicesSignedInAsync();
                var allocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, RelayConnectionType));

                bool relayOk = networkManager.StartClient();
                networkStartInProgress = false;
                Debug.Log(relayOk
                    ? "Tiny Netcode client joining Relay room " + joinCode + "."
                    : "Tiny Netcode Relay client failed to start, will retry.");
                return relayOk;
            }
            catch (Exception exception)
            {
                networkStartInProgress = false;
                Debug.LogWarning("Tiny Relay client failed, will retry.\n" + exception);
                return false;
            }
        }
#endif

        networkStartInProgress = true;
        transport.SetConnectionData(Localhost, Port, Localhost);
        bool ok = networkManager.StartClient();
        networkStartInProgress = false;
        Debug.Log(ok
            ? "Tiny Netcode client trying to join " + Localhost + ":" + Port
            : "Tiny Netcode client failed to start, will retry.");
        return ok;
    }

    private async Task<bool> TryStartHostAsync()
    {
        if (networkManager == null || networkManager.IsListening || networkStartInProgress)
        {
            return false;
        }

        networkStartInProgress = true;
#if TINY_HAS_RELAY
        if (UseUnityRelay)
        {
            try
            {
                await EnsureUnityServicesSignedInAsync();
                var allocation = await RelayService.Instance.CreateAllocationAsync(MaxRelayConnections);
                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, RelayConnectionType));

                bool relayOk = networkManager.StartHost();
                if (relayOk)
                {
                    WriteRelayJoinCode(joinCode);
                    Debug.Log("Tiny Netcode started as Relay host. Join code: " + joinCode + " (saved to " + GetRelayJoinCodePath() + ")");
                }
                else
                {
                    Debug.LogWarning("Tiny Netcode Relay host failed to start.");
                }

                networkStartInProgress = false;
                return relayOk;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Tiny Relay host failed, falling back to local transport.\n" + exception);
            }
        }
#endif

        transport.SetConnectionData(Localhost, Port, Localhost);
        bool ok = networkManager.StartHost();
        networkStartInProgress = false;
        Debug.Log(ok ? "Tiny Netcode started as Editor host on " + Localhost + ":" + Port + "." : "Tiny Netcode host failed to start.");
        return ok;
    }

    private void OnClientDisconnect(ulong clientId)
    {
        if (IsEditorHost() || networkManager == null || clientId != networkManager.LocalClientId)
        {
            return;
        }

        networkManager.Shutdown();
        started = false;
        networkStartInProgress = false;
        handlersRegistered = false;
        clientRetryTimer = 0f;
        Debug.Log("Tiny Netcode client disconnected, waiting for host...");
    }

#if TINY_HAS_RELAY
    private static async Task EnsureUnityServicesSignedInAsync()
    {
        if (UnityServices.State == ServicesInitializationState.Uninitialized)
        {
            await UnityServices.InitializeAsync();
        }

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
    }
#endif

    private static string GetRelayJoinCode()
    {
        string fromArgs = GetCommandLineValue("-join");
        if (string.IsNullOrWhiteSpace(fromArgs))
        {
            fromArgs = GetCommandLineValue("--join");
        }

        if (string.IsNullOrWhiteSpace(fromArgs))
        {
            fromArgs = GetCommandLineValue("-relay");
        }

        if (!string.IsNullOrWhiteSpace(fromArgs))
        {
            return fromArgs.Trim().ToUpperInvariant();
        }

        foreach (string path in GetRelayJoinCodePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                string code = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    return code.Trim().ToUpperInvariant();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Tiny Relay could not read join code file: " + exception.Message);
            }
        }

        return null;
    }

    private static string GetCommandLineValue(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string GetRelayJoinCodePath()
    {
        string[] paths = GetRelayJoinCodePaths();
        return paths.Length > 0 ? paths[0] : Path.Combine(Application.persistentDataPath, RelayJoinCodeFileName);
    }

    private static string[] GetRelayJoinCodePaths()
    {
        List<string> paths = new List<string>();
        string root = Directory.GetParent(Application.dataPath)?.FullName;
        AddRelayJoinCodePath(paths, root);
        if (!string.IsNullOrEmpty(root))
        {
            AddRelayJoinCodePath(paths, Directory.GetParent(root)?.FullName);
        }

        AddRelayJoinCodePath(paths, Directory.GetCurrentDirectory());
        AddRelayJoinCodePath(paths, Application.persistentDataPath);
        return paths.ToArray();
    }

    private static void AddRelayJoinCodePath(List<string> paths, string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        string path = Path.Combine(root, RelayJoinCodeFileName);
        if (!paths.Contains(path))
        {
            paths.Add(path);
        }
    }

    private static void WriteRelayJoinCode(string joinCode)
    {
        try
        {
            File.WriteAllText(GetRelayJoinCodePath(), joinCode.Trim().ToUpperInvariant());
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Tiny Relay could not write join code file: " + exception.Message);
        }
    }

    private void RegisterMessageHandlers()
    {
        if (handlersRegistered || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(PlayerStateMessage, OnPlayerStateMessage);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(EntityStateMessage, OnEntityStateMessage);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(WorldIntentMessage, OnWorldIntentMessage);
        handlersRegistered = true;
    }

    private void SendLocalState()
    {
        if (localPlayer == null
            || !networkManager.IsListening
            || (!networkManager.IsServer && !networkManager.IsConnectedClient)
            || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        playerSendTimer -= Time.deltaTime;
        entitySendTimer -= Time.deltaTime;

        if (playerSendTimer <= 0f)
        {
            playerSendTimer = PlayerSendInterval;
            SendPlayerState();
        }

        if (networkManager.IsServer && entitySendTimer <= 0f)
        {
            entitySendTimer = EntitySendInterval;
            if (syncedEntities.Count == 0)
            {
                RebuildEntityCache();
            }

            foreach (KeyValuePair<string, Transform> entity in syncedEntities)
            {
                if (entity.Value != null && ShouldSendEntityPose(entity.Key, entity.Value))
                {
                    SendEntityState(entity.Key, entity.Value);
                }
            }
        }
    }

    private void RefreshSceneReferences()
    {
        if (localPlayer == null)
        {
            localPlayer = FindFirstComponentByTypeName("TinyFirstPersonController");
            if (localPlayer != null)
            {
                appliedLocalSkin = -1;
                ApplyLocalSkin();
            }
        }

        if (syncedEntities.Count == 0)
        {
            RebuildEntityCache();
        }
    }

    private void SendPlayerState()
    {
        using FastBufferWriter writer = new FastBufferWriter(768, Allocator.Temp);
        Transform playerTransform = localPlayer.transform;
        int skinIndex = GetLocalSkinIndex();
        HandPoseState handPose = GetHandPose(localPlayer.GetComponent("TinyRaymanBody"));
        HeldEntityState heldEntity = GetHeldEntityState(localPlayer);
        writer.WriteValueSafe(networkManager.LocalClientId);
        writer.WriteValueSafe(playerTransform.position);
        writer.WriteValueSafe(playerTransform.rotation);
        writer.WriteValueSafe(GetCurrentPitch(localPlayer));
        writer.WriteValueSafe(skinIndex);
        WriteHandPose(writer, handPose);
        WriteHeldEntityState(writer, heldEntity);
        SendToPeers(PlayerStateMessage, writer);
    }

    private void SendEntityState(string key, Transform entity)
    {
        using FastBufferWriter writer = new FastBufferWriter(768, Allocator.Temp);
        FixedString512Bytes fixedKey = key;
        writer.WriteValueSafe(fixedKey);
        writer.WriteValueSafe(entity.position);
        writer.WriteValueSafe(entity.rotation);
        writer.WriteValueSafe(GetRigidbodyVelocity(entity));
        writer.WriteValueSafe(GetItemHeld(entity));
        writer.WriteValueSafe(GetWagonWheelRollAngle(entity));
        writer.WriteValueSafe(GetWagonRailDistance(entity));
        SendToPeers(EntityStateMessage, writer);
    }

    private void SendToPeers(string messageName, FastBufferWriter writer)
    {
        if (networkManager.IsServer)
        {
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId != networkManager.LocalClientId)
                {
                    networkManager.CustomMessagingManager.SendNamedMessage(messageName, clientId, writer, NetworkDelivery.UnreliableSequenced);
                }
            }
        }
        else
        {
            networkManager.CustomMessagingManager.SendNamedMessage(messageName, NetworkManager.ServerClientId, writer, NetworkDelivery.UnreliableSequenced);
        }
    }

    private bool SendItemPickupIntent(Transform item)
    {
        if (!CanSendWorldIntent(item, out string key))
        {
            return false;
        }

        SendWorldIntent(IntentPickupItem, key, item.position, item.rotation, GetRigidbodyVelocity(item), 0f, Vector3.zero, Quaternion.identity, NetworkDelivery.ReliableSequenced);
        return true;
    }

    private bool SendItemReleaseIntent(Transform item, Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        if (!CanSendWorldIntent(item, out string key))
        {
            return false;
        }

        SendWorldIntent(IntentReleaseItem, key, position, rotation, velocity, 0f, Vector3.zero, Quaternion.identity, NetworkDelivery.ReliableSequenced);
        return true;
    }

    private bool SendWagonPushIntent(Transform wagon, float input, Vector3 playerPosition, Quaternion playerRotation)
    {
        if (!CanSendWorldIntent(wagon, out string key))
        {
            return false;
        }

        SendWorldIntent(IntentPushWagon, key, wagon.position, wagon.rotation, Vector3.zero, input, playerPosition, playerRotation, NetworkDelivery.UnreliableSequenced);
        return true;
    }

    private bool CanSendWorldIntent(Transform entity, out string key)
    {
        key = null;
        if (entity == null
            || networkManager == null
            || !networkManager.IsListening
            || networkManager.IsServer
            || networkManager.CustomMessagingManager == null)
        {
            return false;
        }

        key = GetSyncedEntityKey(entity);
        return !string.IsNullOrEmpty(key);
    }

    private void SendWorldIntent(
        byte intentType,
        string key,
        Vector3 position,
        Quaternion rotation,
        Vector3 velocity,
        float input,
        Vector3 playerPosition,
        Quaternion playerRotation,
        NetworkDelivery delivery)
    {
        using FastBufferWriter writer = new FastBufferWriter(768, Allocator.Temp);
        FixedString512Bytes fixedKey = key;
        writer.WriteValueSafe(intentType);
        writer.WriteValueSafe(fixedKey);
        writer.WriteValueSafe(position);
        writer.WriteValueSafe(rotation);
        writer.WriteValueSafe(velocity);
        writer.WriteValueSafe(input);
        writer.WriteValueSafe(playerPosition);
        writer.WriteValueSafe(playerRotation);
        networkManager.CustomMessagingManager.SendNamedMessage(WorldIntentMessage, NetworkManager.ServerClientId, writer, delivery);
    }

    private void OnPlayerStateMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out ulong ownerClientId);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        reader.ReadValueSafe(out float pitch);
        reader.ReadValueSafe(out int skinIndex);
        HandPoseState handPose = ReadHandPose(reader);
        HeldEntityState heldEntity = ReadHeldEntityState(reader);

        if (ownerClientId == networkManager.LocalClientId)
        {
            return;
        }

        if (networkManager.IsServer)
        {
            heldEntity = GetServerApprovedHeldEntity(senderClientId, heldEntity);
            RelayPlayerPayload(senderClientId, ownerClientId, position, rotation, pitch, skinIndex, handPose, heldEntity);
        }

        ApplyHeldEntityPayload(senderClientId, heldEntity);
        if (heldEntity.HasEntity)
        {
            handPose = default;
        }

        if (!remotePlayers.TryGetValue(ownerClientId, out RemotePlayer remotePlayer))
        {
            remotePlayer = CreateRemotePlayer(ownerClientId, skinIndex);
            remotePlayers.Add(ownerClientId, remotePlayer);
        }

        remotePlayer.TargetPosition = position;
        remotePlayer.TargetRotation = rotation;
        remotePlayer.TargetPitch = pitch;
        remotePlayer.TargetHands = handPose;
        remotePlayer.TargetHeldEntityKey = heldEntity.HasEntity ? heldEntity.Key : null;
        remotePlayer.ApplySkin(skinIndex);
    }

    private void RelayPlayerPayload(
        ulong senderClientId,
        ulong ownerClientId,
        Vector3 position,
        Quaternion rotation,
        float pitch,
        int skinIndex,
        HandPoseState handPose,
        HeldEntityState heldEntity)
    {
        using FastBufferWriter writer = new FastBufferWriter(768, Allocator.Temp);
        writer.WriteValueSafe(ownerClientId);
        writer.WriteValueSafe(position);
        writer.WriteValueSafe(rotation);
        writer.WriteValueSafe(pitch);
        writer.WriteValueSafe(skinIndex);
        WriteHandPose(writer, handPose);
        WriteHeldEntityState(writer, heldEntity);

        foreach (ulong clientId in networkManager.ConnectedClientsIds)
        {
            if (clientId != networkManager.LocalClientId && clientId != senderClientId)
            {
                networkManager.CustomMessagingManager.SendNamedMessage(PlayerStateMessage, clientId, writer, NetworkDelivery.UnreliableSequenced);
            }
        }
    }

    private void OnEntityStateMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out FixedString512Bytes fixedKey);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        reader.ReadValueSafe(out Vector3 velocity);
        reader.ReadValueSafe(out bool isHeld);
        reader.ReadValueSafe(out float wheelRollAngle);
        reader.ReadValueSafe(out float railDistance);

        if (networkManager.IsServer && senderClientId != networkManager.LocalClientId)
        {
            return;
        }

        if (senderClientId == networkManager.LocalClientId)
        {
            return;
        }

        string key = fixedKey.ToString();
        remoteEntityTargets[key] = new TimedPoseState(new PoseState(position, rotation, velocity, isHeld, wheelRollAngle, railDistance), Time.time + RemoteEntityTargetLifetime);
        entityAuthorities[key] = new EntityAuthority(senderClientId, Time.time + EntityAuthorityHoldTime);

        if (networkManager.IsServer)
        {
            using FastBufferWriter writer = new FastBufferWriter(768, Allocator.Temp);
            writer.WriteValueSafe(fixedKey);
            writer.WriteValueSafe(position);
            writer.WriteValueSafe(rotation);
            writer.WriteValueSafe(velocity);
            writer.WriteValueSafe(isHeld);
            writer.WriteValueSafe(wheelRollAngle);
            writer.WriteValueSafe(railDistance);

            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId != networkManager.LocalClientId && clientId != senderClientId)
                {
                    networkManager.CustomMessagingManager.SendNamedMessage(EntityStateMessage, clientId, writer, NetworkDelivery.UnreliableSequenced);
                }
            }
        }
    }

    private void OnWorldIntentMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out byte intentType);
        reader.ReadValueSafe(out FixedString512Bytes fixedKey);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        reader.ReadValueSafe(out Vector3 velocity);
        reader.ReadValueSafe(out float input);
        reader.ReadValueSafe(out Vector3 playerPosition);
        reader.ReadValueSafe(out Quaternion playerRotation);

        if (!networkManager.IsServer)
        {
            return;
        }

        string key = fixedKey.ToString();
        if (!syncedEntities.TryGetValue(key, out Transform entity) || entity == null)
        {
            return;
        }

        switch (intentType)
        {
            case IntentPickupItem:
                if (IsItem(entity))
                {
                    if (heldEntityOwners.TryGetValue(key, out ulong currentOwner) && currentOwner != senderClientId)
                    {
                        return;
                    }

                    heldEntityOwners[key] = senderClientId;
                    remoteEntityTargets.Remove(key);
                    entityAuthorities.Remove(key);
                    InvokeApplyRemoteHeldState(entity, true);
                    entity.SetPositionAndRotation(position, rotation);
                    BroadcastAuthoritativeEntity(key, entity);
                }
                break;

            case IntentReleaseItem:
                if (IsItem(entity))
                {
                    if (heldEntityOwners.TryGetValue(key, out ulong currentOwner) && currentOwner != senderClientId)
                    {
                        return;
                    }

                    heldEntityOwners.Remove(key);
                    remoteEntityTargets.Remove(key);
                    entityAuthorities.Remove(key);
                    InvokeApplyAuthoritativeItemRelease(entity, position, rotation, velocity);
                    BroadcastAuthoritativeEntity(key, entity);
                }
                break;

            case IntentPushWagon:
                if (IsWagon(entity))
                {
                    activeWagonPushes[senderClientId] = new WagonPushState(
                        key,
                        input,
                        playerPosition,
                        Time.time + WagonPushInputTimeout);
                }
                break;
        }
    }

    private void ProcessServerWagonPushes()
    {
        if (networkManager == null || !networkManager.IsServer || activeWagonPushes.Count == 0)
        {
            return;
        }

        List<ulong> expiredClients = null;
        HashSet<string> movedWagons = null;
        foreach (KeyValuePair<ulong, WagonPushState> push in activeWagonPushes)
        {
            if (Time.time > push.Value.ExpireTime || Mathf.Abs(push.Value.Input) < 0.01f)
            {
                expiredClients ??= new List<ulong>();
                expiredClients.Add(push.Key);
                continue;
            }

            if (!syncedEntities.TryGetValue(push.Value.WagonKey, out Transform wagon) || wagon == null || !IsWagon(wagon))
            {
                expiredClients ??= new List<ulong>();
                expiredClients.Add(push.Key);
                continue;
            }

            InvokePushWagonFromNetwork(wagon, push.Value.PlayerPosition, push.Value.Input, Time.deltaTime);
            movedWagons ??= new HashSet<string>();
            movedWagons.Add(push.Value.WagonKey);
        }

        if (expiredClients != null)
        {
            for (int i = 0; i < expiredClients.Count; i++)
            {
                activeWagonPushes.Remove(expiredClients[i]);
            }
        }

        if (movedWagons != null)
        {
            foreach (string wagonKey in movedWagons)
            {
                if (syncedEntities.TryGetValue(wagonKey, out Transform wagon) && wagon != null)
                {
                    BroadcastAuthoritativeEntity(wagonKey, wagon);
                }
            }
        }
    }

    private void BroadcastAuthoritativeEntity(string key, Transform entity)
    {
        if (networkManager.IsServer && entity != null)
        {
            lastSentEntityPoses.Remove(key);
            SendEntityState(key, entity);
        }
    }

    private void ApplyHeldEntityPayload(ulong senderClientId, HeldEntityState heldEntity)
    {
        if (!heldEntity.HasEntity)
        {
            return;
        }

        if (networkManager.IsServer)
        {
            if (!heldEntityOwners.TryGetValue(heldEntity.Key, out ulong ownerClientId) || ownerClientId != senderClientId)
            {
                return;
            }
        }
        else if (!syncedEntities.TryGetValue(heldEntity.Key, out Transform entity) || entity == null || !GetItemHeld(entity))
        {
            return;
        }

        remoteEntityTargets[heldEntity.Key] = new TimedPoseState(
            new PoseState(heldEntity.Position, heldEntity.Rotation, heldEntity.Velocity, true, 0f, 0f),
            Time.time + RemoteEntityTargetLifetime);
        entityAuthorities[heldEntity.Key] = new EntityAuthority(senderClientId, Time.time + EntityAuthorityHoldTime);
    }

    private HeldEntityState GetServerApprovedHeldEntity(ulong senderClientId, HeldEntityState heldEntity)
    {
        if (!heldEntity.HasEntity)
        {
            return default;
        }

        if (!heldEntityOwners.TryGetValue(heldEntity.Key, out ulong ownerClientId) || ownerClientId != senderClientId)
        {
            return default;
        }

        return heldEntity;
    }

    private RemotePlayer CreateRemotePlayer(ulong clientId, int skinIndex)
    {
        GameObject source = localPlayer != null ? localPlayer.gameObject : null;
        GameObject remoteObject = source != null ? Instantiate(source) : GameObject.CreatePrimitive(PrimitiveType.Capsule);
        remoteObject.name = "Remote Player " + clientId;
        remoteObject.tag = "Untagged";
        DisableRemoteControl(remoteObject);

        RemotePlayer remotePlayer = new RemotePlayer(remoteObject.transform);
        remotePlayer.Transform.position = localPlayer != null ? localPlayer.transform.position : Vector3.zero;
        remotePlayer.Transform.rotation = localPlayer != null ? localPlayer.transform.rotation : Quaternion.identity;
        remotePlayer.TargetPosition = remotePlayer.Transform.position;
        remotePlayer.TargetRotation = remotePlayer.Transform.rotation;
        remotePlayer.ApplySkin(skinIndex);
        return remotePlayer;
    }

    private static void DisableRemoteControl(GameObject remoteObject)
    {
        MonoBehaviour controller = GetBehaviourByTypeName(remoteObject, "TinyFirstPersonController");
        if (controller != null)
        {
            controller.enabled = false;
        }

        CharacterController characterController = remoteObject.GetComponent<CharacterController>();
        if (characterController != null)
        {
            characterController.enabled = false;
        }

        Camera[] cameras = remoteObject.GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < cameras.Length; i++)
        {
            cameras[i].enabled = false;
        }

        AudioListener[] listeners = remoteObject.GetComponentsInChildren<AudioListener>(true);
        for (int i = 0; i < listeners.Length; i++)
        {
            listeners[i].enabled = false;
        }
    }

    private void UpdateRemotePlayers()
    {
        float follow = 1f - Mathf.Exp(-RemoteFollowSharpness * Time.deltaTime);
        foreach (RemotePlayer remotePlayer in remotePlayers.Values)
        {
            if (remotePlayer.Transform == null)
            {
                continue;
            }

            remotePlayer.Transform.position = Vector3.Lerp(remotePlayer.Transform.position, remotePlayer.TargetPosition, follow);
            remotePlayer.Transform.rotation = Quaternion.Slerp(remotePlayer.Transform.rotation, remotePlayer.TargetRotation, follow);
            remotePlayer.ApplyPitch();
            remotePlayer.ApplyHands(syncedEntities);
        }
    }

    private void ApplyRemoteEntityTargets()
    {
        float follow = 1f - Mathf.Exp(-RemoteEntityFollowSharpness * Time.deltaTime);
        List<string> expiredTargets = null;
        foreach (KeyValuePair<string, TimedPoseState> target in remoteEntityTargets)
        {
            if (Time.time > target.Value.ExpireTime)
            {
                expiredTargets ??= new List<string>();
                expiredTargets.Add(target.Key);
                continue;
            }

            if (!syncedEntities.TryGetValue(target.Key, out Transform entity) || entity == null)
            {
                continue;
            }

            if (networkManager.IsServer && target.Value.Pose.IsHeld && !heldEntityOwners.ContainsKey(target.Key))
            {
                expiredTargets ??= new List<string>();
                expiredTargets.Add(target.Key);
                continue;
            }

            if (IsHeldByLocalPlayer(entity))
            {
                continue;
            }

            bool snap = (entity.position - target.Value.Pose.Position).sqrMagnitude > RemoteEntitySnapDistance * RemoteEntitySnapDistance;
            InvokeApplyRemoteHeldState(entity, target.Value.Pose.IsHeld);
            InvokeApplyRemoteRailState(entity, target.Value.Pose.RailDistance, target.Value.Pose.WheelRollAngle);
            Vector3 position = snap
                ? target.Value.Pose.Position
                : target.Value.Pose.IsHeld
                    ? target.Value.Pose.Position
                    : Vector3.Lerp(entity.position, target.Value.Pose.Position, follow);
            Quaternion rotation = snap
                ? target.Value.Pose.Rotation
                : target.Value.Pose.IsHeld
                    ? target.Value.Pose.Rotation
                    : Quaternion.Slerp(entity.rotation, target.Value.Pose.Rotation, follow);
            Rigidbody rigidbody = entity.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                ApplyClientVisualAuthority(entity, rigidbody);
                rigidbody.position = position;
                rigidbody.rotation = rotation;
                if (networkManager.IsServer)
                {
                    SetRigidbodyVelocity(rigidbody, target.Value.Pose.Velocity);
                }
            }
            else
            {
                entity.SetPositionAndRotation(position, rotation);
            }

            lastSentEntityPoses[target.Key] = new PoseState(
                position,
                rotation,
                target.Value.Pose.Velocity,
                target.Value.Pose.IsHeld,
                target.Value.Pose.WheelRollAngle,
                target.Value.Pose.RailDistance);
        }

        if (expiredTargets != null)
        {
            for (int i = 0; i < expiredTargets.Count; i++)
            {
                remoteEntityTargets.Remove(expiredTargets[i]);
            }
        }
    }

    private void RebuildEntityCache()
    {
        syncedEntities.Clear();
        Component[] items = FindComponentsByTypeName("TinyItem");
        for (int i = 0; i < items.Length; i++)
        {
            AddSyncedEntity(items[i].transform);
        }

        Component[] wagons = FindComponentsByTypeName("TinyRailWagon");
        for (int i = 0; i < wagons.Length; i++)
        {
            AddSyncedEntity(wagons[i].transform);
        }
    }

    private void AddSyncedEntity(Transform entity)
    {
        string path = GetScenePath(entity);
        if (!string.IsNullOrEmpty(path) && !syncedEntities.ContainsKey(path))
        {
            syncedEntities.Add(path, entity);
        }
    }

    private bool ShouldSendEntityPose(string key, Transform entity)
    {
        if (!networkManager.IsServer && entityAuthorities.TryGetValue(key, out EntityAuthority authority))
        {
            if (Time.time <= authority.ExpireTime && authority.OwnerClientId != networkManager.LocalClientId)
            {
                return false;
            }

            if (Time.time > authority.ExpireTime)
            {
                entityAuthorities.Remove(key);
            }
        }

        if (!lastSentEntityPoses.TryGetValue(key, out PoseState lastPose))
        {
            lastSentEntityPoses[key] = new PoseState(
                entity.position,
                entity.rotation,
                GetRigidbodyVelocity(entity),
                GetItemHeld(entity),
                GetWagonWheelRollAngle(entity),
                GetWagonRailDistance(entity));
            return networkManager.IsServer;
        }

        Vector3 velocity = GetRigidbodyVelocity(entity);
        bool isHeld = GetItemHeld(entity);
        float wheelRollAngle = GetWagonWheelRollAngle(entity);
        float railDistance = GetWagonRailDistance(entity);
        bool moved = (entity.position - lastPose.Position).sqrMagnitude > 0.0004f
            || Quaternion.Angle(entity.rotation, lastPose.Rotation) > 1.5f
            || (velocity - lastPose.Velocity).sqrMagnitude > 0.0025f
            || isHeld != lastPose.IsHeld
            || Mathf.Abs(Mathf.DeltaAngle(wheelRollAngle, lastPose.WheelRollAngle)) > 0.5f
            || Mathf.Abs(railDistance - lastPose.RailDistance) > 0.002f;
        if (moved)
        {
            lastSentEntityPoses[key] = new PoseState(entity.position, entity.rotation, velocity, isHeld, wheelRollAngle, railDistance);
        }

        return moved;
    }

    private static bool IsEditorHost()
    {
#if UNITY_EDITOR
        return true;
#else
        return false;
#endif
    }

    private int GetLocalSkinIndex()
    {
        if (IsEditorHost() || networkManager == null)
        {
            return 0;
        }

        return Mathf.Clamp((int)networkManager.LocalClientId, 1, 3);
    }

    private void ApplyLocalSkin()
    {
        if (localPlayer == null)
        {
            return;
        }

        int skinIndex = GetLocalSkinIndex();
        if (appliedLocalSkin == skinIndex)
        {
            return;
        }

        Component body = localPlayer.GetComponent("TinyRaymanBody");
        if (body != null)
        {
            InvokeSetSkin(body, skinIndex);
            appliedLocalSkin = skinIndex;
        }
    }

    private static string GetScenePath(Transform transform)
    {
        string path = transform.name;
        Transform current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }

    private readonly struct PoseState
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Velocity;
        public readonly bool IsHeld;
        public readonly float WheelRollAngle;
        public readonly float RailDistance;

        public PoseState(Vector3 position, Quaternion rotation, Vector3 velocity, bool isHeld, float wheelRollAngle, float railDistance)
        {
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            IsHeld = isHeld;
            WheelRollAngle = wheelRollAngle;
            RailDistance = railDistance;
        }
    }

    private readonly struct TimedPoseState
    {
        public readonly PoseState Pose;
        public readonly float ExpireTime;

        public TimedPoseState(PoseState pose, float expireTime)
        {
            Pose = pose;
            ExpireTime = expireTime;
        }
    }

    private readonly struct EntityAuthority
    {
        public readonly ulong OwnerClientId;
        public readonly float ExpireTime;

        public EntityAuthority(ulong ownerClientId, float expireTime)
        {
            OwnerClientId = ownerClientId;
            ExpireTime = expireTime;
        }
    }

    private readonly struct WagonPushState
    {
        public readonly string WagonKey;
        public readonly float Input;
        public readonly Vector3 PlayerPosition;
        public readonly float ExpireTime;

        public WagonPushState(string wagonKey, float input, Vector3 playerPosition, float expireTime)
        {
            WagonKey = wagonKey;
            Input = input;
            PlayerPosition = playerPosition;
            ExpireTime = expireTime;
        }
    }

    private readonly struct HandPoseState
    {
        public readonly bool IsValid;
        public readonly Vector3 LeftPosition;
        public readonly Quaternion LeftRotation;
        public readonly Vector3 RightPosition;
        public readonly Quaternion RightRotation;

        public HandPoseState(
            bool isValid,
            Vector3 leftPosition,
            Quaternion leftRotation,
            Vector3 rightPosition,
            Quaternion rightRotation)
        {
            IsValid = isValid;
            LeftPosition = leftPosition;
            LeftRotation = leftRotation;
            RightPosition = rightPosition;
            RightRotation = rightRotation;
        }
    }

    private readonly struct HeldEntityState
    {
        public readonly bool HasEntity;
        public readonly string Key;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Velocity;

        public HeldEntityState(string key, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            HasEntity = !string.IsNullOrEmpty(key);
            Key = key;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
        }
    }

    private sealed class RemotePlayer
    {
        public readonly Transform Transform;
        private readonly Component body;
        private int appliedSkin = -1;

        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public float TargetPitch;
        public HandPoseState TargetHands;
        public string TargetHeldEntityKey;

        public RemotePlayer(Transform transform)
        {
            Transform = transform;
            body = transform != null ? transform.GetComponent("TinyRaymanBody") : null;
        }

        public void ApplySkin(int skinIndex)
        {
            if (body == null || appliedSkin == skinIndex)
            {
                return;
            }

            appliedSkin = skinIndex;
            InvokeSetSkin(body, skinIndex);
        }

        public void ApplyPitch()
        {
            if (body != null)
            {
                InvokeSetCameraPitch(body, TargetPitch);
            }
        }

        public void ApplyHands(Dictionary<string, Transform> syncedEntities)
        {
            if (body == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(TargetHeldEntityKey)
                && syncedEntities != null
                && syncedEntities.TryGetValue(TargetHeldEntityKey, out Transform heldEntity)
                && TryGetItemHandPose(heldEntity, Transform, out HandPoseState heldHandPose))
            {
                InvokeApplyRemoteHandAnchors(body, heldHandPose);
                return;
            }

            if (TargetHands.IsValid)
            {
                InvokeApplyRemoteHandPoses(body, TargetHands);
            }
        }
    }

    private static Component FindFirstComponentByTypeName(string typeName)
    {
        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null && behaviours[i].GetType().Name == typeName)
            {
                return behaviours[i];
            }
        }

        return null;
    }

    private static Component[] FindComponentsByTypeName(string typeName)
    {
        List<Component> matches = new List<Component>();
        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null && behaviours[i].GetType().Name == typeName)
            {
                matches.Add(behaviours[i]);
            }
        }

        return matches.ToArray();
    }

    private static MonoBehaviour GetBehaviourByTypeName(GameObject owner, string typeName)
    {
        MonoBehaviour[] behaviours = owner.GetComponents<MonoBehaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null && behaviours[i].GetType().Name == typeName)
            {
                return behaviours[i];
            }
        }

        return null;
    }

    private static float GetCurrentPitch(Component controller)
    {
        if (controller == null)
        {
            return 0f;
        }

        object value = controller.GetType().GetProperty("CurrentPitch")?.GetValue(controller);
        return value is float pitch ? pitch : 0f;
    }

    private static HandPoseState GetHandPose(Component body)
    {
        if (body == null)
        {
            return default;
        }

        object[] args =
        {
            Vector3.zero,
            Quaternion.identity,
            Vector3.zero,
            Quaternion.identity
        };
        object result = body.GetType().GetMethod("TryGetHandPoses")?.Invoke(body, args);
        if (!(result is bool ok) || !ok)
        {
            return default;
        }

        return new HandPoseState(
            true,
            (Vector3)args[0],
            (Quaternion)args[1],
            (Vector3)args[2],
            (Quaternion)args[3]);
    }

    private static void WriteHandPose(FastBufferWriter writer, HandPoseState handPose)
    {
        writer.WriteValueSafe(handPose.IsValid);
        if (!handPose.IsValid)
        {
            return;
        }

        writer.WriteValueSafe(handPose.LeftPosition);
        writer.WriteValueSafe(handPose.LeftRotation);
        writer.WriteValueSafe(handPose.RightPosition);
        writer.WriteValueSafe(handPose.RightRotation);
    }

    private static HandPoseState ReadHandPose(FastBufferReader reader)
    {
        reader.ReadValueSafe(out bool isValid);
        if (!isValid)
        {
            return default;
        }

        reader.ReadValueSafe(out Vector3 leftPosition);
        reader.ReadValueSafe(out Quaternion leftRotation);
        reader.ReadValueSafe(out Vector3 rightPosition);
        reader.ReadValueSafe(out Quaternion rightRotation);
        return new HandPoseState(true, leftPosition, leftRotation, rightPosition, rightRotation);
    }

    private HeldEntityState GetHeldEntityState(Component controller)
    {
        Transform heldEntity = GetHeldItemTransform(controller);
        if (heldEntity == null)
        {
            return default;
        }

        string key = GetSyncedEntityKey(heldEntity);
        if (string.IsNullOrEmpty(key))
        {
            return default;
        }

        return new HeldEntityState(
            key,
            heldEntity.position,
            heldEntity.rotation,
            GetRigidbodyVelocity(heldEntity));
    }

    private string GetSyncedEntityKey(Transform entity)
    {
        foreach (KeyValuePair<string, Transform> pair in syncedEntities)
        {
            if (pair.Value == entity)
            {
                return pair.Key;
            }
        }

        return null;
    }

    private static void WriteHeldEntityState(FastBufferWriter writer, HeldEntityState heldEntity)
    {
        writer.WriteValueSafe(heldEntity.HasEntity);
        if (!heldEntity.HasEntity)
        {
            return;
        }

        FixedString512Bytes fixedKey = heldEntity.Key;
        writer.WriteValueSafe(fixedKey);
        writer.WriteValueSafe(heldEntity.Position);
        writer.WriteValueSafe(heldEntity.Rotation);
        writer.WriteValueSafe(heldEntity.Velocity);
    }

    private static HeldEntityState ReadHeldEntityState(FastBufferReader reader)
    {
        reader.ReadValueSafe(out bool hasEntity);
        if (!hasEntity)
        {
            return default;
        }

        reader.ReadValueSafe(out FixedString512Bytes fixedKey);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        reader.ReadValueSafe(out Vector3 velocity);
        return new HeldEntityState(fixedKey.ToString(), position, rotation, velocity);
    }

    private static Vector3 GetRigidbodyVelocity(Transform transform)
    {
        Rigidbody rigidbody = transform != null ? transform.GetComponent<Rigidbody>() : null;
        if (rigidbody == null)
        {
            return Vector3.zero;
        }

#if UNITY_6000_0_OR_NEWER
        return rigidbody.linearVelocity;
#else
        return rigidbody.velocity;
#endif
    }

    private static bool GetItemHeld(Transform entity)
    {
        Component item = entity != null ? entity.GetComponent("TinyItem") : null;
        object value = item?.GetType().GetProperty("IsNetworkHeld")?.GetValue(item)
            ?? item?.GetType().GetProperty("IsHeld")?.GetValue(item);
        return value is bool isHeld && isHeld;
    }

    private void ApplyClientVisualAuthority(Transform entity, Rigidbody rigidbody)
    {
        if (networkManager == null
            || networkManager.IsServer
            || rigidbody == null
            || (!IsItem(entity) && !IsWagon(entity)))
        {
            return;
        }

        rigidbody.isKinematic = true;
        rigidbody.useGravity = false;
        rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
    }

    private static Transform GetHeldItemTransform(Component controller)
    {
        object value = controller?.GetType().GetProperty("HeldItemTransform")?.GetValue(controller);
        return value as Transform;
    }

    private bool IsHeldByLocalPlayer(Transform entity)
    {
        return entity != null && GetHeldItemTransform(localPlayer) == entity;
    }

    private static bool IsItem(Transform entity)
    {
        return entity != null && entity.GetComponent("TinyItem") != null;
    }

    private static bool IsWagon(Transform entity)
    {
        return entity != null && entity.GetComponent("TinyRailWagon") != null;
    }

    private static void InvokeApplyRemoteHeldState(Transform entity, bool isHeld)
    {
        Component item = entity != null ? entity.GetComponent("TinyItem") : null;
        item?.GetType().GetMethod("ApplyRemoteHeldState")?.Invoke(item, new object[] { isHeld });
    }

    private static void InvokeApplyAuthoritativeItemRelease(Transform entity, Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        Component item = entity != null ? entity.GetComponent("TinyItem") : null;
        item?.GetType().GetMethod("ApplyAuthoritativeRelease")?.Invoke(item, new object[] { position, rotation, velocity });
    }

    private static bool TryGetItemHandPose(Transform entity, Transform playerRoot, out HandPoseState handPose)
    {
        handPose = default;
        Component item = entity != null ? entity.GetComponent("TinyItem") : null;
        if (item == null)
        {
            return false;
        }

        object[] anchorArgs =
        {
            playerRoot,
            Vector3.zero,
            Vector3.zero
        };
        item.GetType().GetMethod("GetHandAnchors")?.Invoke(item, anchorArgs);

        object[] rotationArgs =
        {
            playerRoot,
            Quaternion.identity,
            Quaternion.identity
        };
        item.GetType().GetMethod("GetHandRotations")?.Invoke(item, rotationArgs);

        handPose = new HandPoseState(
            true,
            (Vector3)anchorArgs[1],
            (Quaternion)rotationArgs[1],
            (Vector3)anchorArgs[2],
            (Quaternion)rotationArgs[2]);
        return true;
    }

    private static float GetWagonWheelRollAngle(Transform entity)
    {
        Component wagon = entity != null ? entity.GetComponent("TinyRailWagon") : null;
        object value = wagon?.GetType().GetProperty("WheelRollAngle")?.GetValue(wagon);
        return value is float wheelRollAngle ? wheelRollAngle : 0f;
    }

    private static float GetWagonRailDistance(Transform entity)
    {
        Component wagon = entity != null ? entity.GetComponent("TinyRailWagon") : null;
        object value = wagon?.GetType().GetProperty("DistanceOnRail")?.GetValue(wagon);
        return value is float railDistance ? railDistance : 0f;
    }

    private static void InvokeApplyRemoteRailState(Transform entity, float railDistance, float wheelRollAngle)
    {
        Component wagon = entity != null ? entity.GetComponent("TinyRailWagon") : null;
        wagon?.GetType().GetMethod("ApplyRemoteRailState")?.Invoke(wagon, new object[] { railDistance, wheelRollAngle });
    }

    private static void InvokePushWagonFromNetwork(Transform entity, Vector3 playerPosition, float input, float deltaTime)
    {
        Component wagon = entity != null ? entity.GetComponent("TinyRailWagon") : null;
        wagon?.GetType().GetMethod("PushFromNetwork")?.Invoke(wagon, new object[] { playerPosition, input, deltaTime });
    }

    private static void SetRigidbodyVelocity(Rigidbody rigidbody, Vector3 velocity)
    {
        if (rigidbody == null)
        {
            return;
        }

#if UNITY_6000_0_OR_NEWER
        rigidbody.linearVelocity = velocity;
#else
        rigidbody.velocity = velocity;
#endif
    }

    private static void InvokeSetSkin(Component body, int skinIndex)
    {
        body?.GetType().GetMethod("SetSkin")?.Invoke(body, new object[] { skinIndex });
    }

    private static void InvokeSetCameraPitch(Component body, float pitch)
    {
        body?.GetType().GetMethod("SetCameraPitch")?.Invoke(body, new object[] { pitch });
    }

    private static void InvokeApplyRemoteHandPoses(Component body, HandPoseState handPose)
    {
        body?.GetType().GetMethod("ApplyRemoteHandPoses")?.Invoke(
            body,
            new object[]
            {
                handPose.LeftPosition,
                handPose.LeftRotation,
                handPose.RightPosition,
                handPose.RightRotation,
                true
            });
    }

    private static void InvokeApplyRemoteHandAnchors(Component body, HandPoseState handPose)
    {
        body?.GetType().GetMethod("ApplyRemoteHandAnchors")?.Invoke(
            body,
            new object[]
            {
                handPose.LeftPosition,
                handPose.LeftRotation,
                handPose.RightPosition,
                handPose.RightRotation,
                true
            });
    }
}
