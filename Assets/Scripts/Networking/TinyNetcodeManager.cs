using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

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
    private const float PingSendInterval = 0.5f;
    private const string PlayerStateMessage = "TinyPlayerState";
    private const string EntityStateMessage = "TinyEntityState";
    private const string WorldIntentMessage = "TinyWorldIntent";
    private const string WorldEventMessage = "TinyWorldEvent";
    private const string PingMessage = "TinyPing";
    private const string StartGameMessage = "TinyStartGame";
    private const string LobbyStateMessage = "TinyLobbyState";
    private const string SkinRequestMessage = "TinySkinRequest";
    private const int SkinCount = 4;
    private const byte IntentPickupItem = 1;
    private const byte IntentReleaseItem = 2;
    private const byte IntentPushWagon = 3;
    private const byte IntentGrabWagon = 4;
    private const byte IntentReleaseWagon = 5;
    private const byte IntentActivateLever = 6;
    private const byte EventActivateLever = 1;
    private const byte EventTeamMoney = 2;

#if TINY_HAS_RELAY
    private static Task unityServicesInitializationTask;
    private static Task unityAuthenticationTask;
#endif

    private readonly Dictionary<ulong, RemotePlayer> remotePlayers = new Dictionary<ulong, RemotePlayer>();
    private readonly Dictionary<string, Transform> syncedEntities = new Dictionary<string, Transform>();
    private readonly Dictionary<string, TimedPoseState> remoteEntityTargets = new Dictionary<string, TimedPoseState>();
    private readonly Dictionary<string, PoseState> lastSentEntityPoses = new Dictionary<string, PoseState>();
    private readonly Dictionary<string, EntityAuthority> entityAuthorities = new Dictionary<string, EntityAuthority>();
    private readonly Dictionary<string, ulong> heldEntityOwners = new Dictionary<string, ulong>();
    private readonly Dictionary<ulong, WagonPushState> activeWagonPushes = new Dictionary<ulong, WagonPushState>();
    private readonly List<ulong> lobbyClientOrder = new List<ulong>();
    private readonly Dictionary<ulong, int> lobbySkinAssignments = new Dictionary<ulong, int>();

    private static TinyNetcodeManager instance;
    private NetworkManager networkManager;
    private UnityTransport transport;
    private Component localPlayer;
    [SerializeField] private bool autoStartNetwork;
    [SerializeField] private float testRoundDurationSeconds = 900f;
    private float playerSendTimer;
    private float entitySendTimer;
    private float clientRetryTimer;
    private float pingSendTimer;
    private float lobbyStateSendTimer;
    private float displayedPingMs;
    private bool started;
    private bool networkStartInProgress;
    private bool handlersRegistered;
    private bool callbacksRegistered;
    private int appliedLocalSkin = -1;
    private NetworkStartRole requestedStartRole = NetworkStartRole.None;
    private string relayJoinCodeOverride;
    private string currentRelayJoinCode;
    private int lastKnownLobbyPlayerCount;
    private int localSelectedSkin = -1;
    private int pendingLocalSpawnSlot = -1;
    private float pendingGameStartRealtime = -1f;
    private string appliedSpawnSceneName;
    private bool gameOverTriggered;
    private int teamMoney;
    private bool roundTimerForcedRed;

    private enum NetworkStartRole
    {
        None,
        Host,
        Client
    }

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
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void Start()
    {
        localPlayer = FindFirstComponentByTypeName("TinyFirstPersonController");
        if (localPlayer != null)
        {
            ApplyLocalSkin();
        }

        RebuildEntityCache();
        if (autoStartNetwork)
        {
            StartNetworkRole();
        }
    }

    private void Update()
    {
        if (networkManager == null)
        {
            return;
        }

        RefreshSceneReferences();

        if (!IsEditorHost()
            && requestedStartRole == NetworkStartRole.Client
            && !networkManager.IsClient
            && !networkManager.IsListening)
        {
            RetryClientStart();
            return;
        }

        RegisterMessageHandlers();
        ApplyLocalSkin();
        ProcessServerWagonPushes();
        UpdatePing();
        UpdateRoundTimer();
        SendLobbyState();
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
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(WorldEventMessage);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(PingMessage);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(StartGameMessage);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(LobbyStateMessage);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(SkinRequestMessage);
        }

        if (networkManager != null && callbacksRegistered)
        {
            networkManager.OnClientConnectedCallback -= OnClientConnected;
            networkManager.OnClientDisconnectCallback -= OnClientDisconnect;
        }

        if (instance == this)
        {
            instance = null;
        }

        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    public static bool IsClientOnlyActive =>
        instance != null
        && instance.networkManager != null
        && instance.networkManager.IsListening
        && !instance.networkManager.IsServer;

    public static bool IsNetworkActive =>
        instance != null
        && instance.networkManager != null
        && instance.networkManager.IsListening;

    public static bool IsHostActive =>
        instance != null
        && instance.networkManager != null
        && instance.networkManager.IsListening
        && instance.networkManager.IsServer;

    public static bool IsClientConnected =>
        instance != null
        && instance.networkManager != null
        && instance.networkManager.IsListening
        && (instance.networkManager.IsServer || instance.networkManager.IsConnectedClient);

    public static string CurrentRelayJoinCode => instance != null ? instance.currentRelayJoinCode : string.Empty;

    public static Transform GetRandomAliveSpectateTarget(Transform excludedTransform)
    {
        if (instance == null)
        {
            return null;
        }

        List<Transform> candidates = new List<Transform>();
        foreach (RemotePlayer remotePlayer in instance.remotePlayers.Values)
        {
            if (remotePlayer.Transform != null && remotePlayer.Transform != excludedTransform)
            {
                candidates.Add(remotePlayer.Transform);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates[UnityEngine.Random.Range(0, candidates.Count)];
    }

    public static int ConnectedPlayerCount
    {
        get
        {
            if (instance == null || instance.networkManager == null || !instance.networkManager.IsListening)
            {
                return 0;
            }

            return instance.networkManager.IsServer
                ? Mathf.Max(instance.networkManager.ConnectedClientsIds.Count, instance.lobbyClientOrder.Count)
                : Mathf.Max(1, instance.lastKnownLobbyPlayerCount);
        }
    }

    public static bool IsConnecting =>
        instance != null
        && (instance.networkStartInProgress || instance.requestedStartRole == NetworkStartRole.Client)
        && !IsClientConnected;

    public static int LocalEquippedSkinIndex => instance != null ? instance.GetLocalAssignedSkin() : -1;

    public static int SkinOptionCount => SkinCount;

    public static int GetLobbySkinBySlot(int slot)
    {
        return instance != null ? instance.GetLobbySkinBySlotInternal(slot) : -1;
    }

    public static bool IsSkinTakenByOther(int skinIndex)
    {
        return instance != null && instance.IsSkinTakenByOtherInternal(skinIndex);
    }

    public static bool RequestSkinFromMenu(int skinIndex)
    {
        if (instance == null)
        {
            return false;
        }

        return instance.RequestSkinInternal(skinIndex);
    }

    public static void StartHostFromMenu()
    {
        if (instance == null)
        {
            return;
        }

        instance.requestedStartRole = NetworkStartRole.Host;
        instance.relayJoinCodeOverride = null;
        instance.ResetLobbyState();
        instance.StartNetworkRole();
    }

    public static bool StartClientFromMenu(string joinCode)
    {
        if (instance == null || string.IsNullOrWhiteSpace(joinCode))
        {
            return false;
        }

        instance.requestedStartRole = NetworkStartRole.Client;
        instance.relayJoinCodeOverride = joinCode.Trim().ToUpperInvariant();
        instance.ResetLobbyState();
        instance.StartNetworkRole();
        return true;
    }

    public static void StopFromMenu()
    {
        if (instance == null || instance.networkManager == null)
        {
            return;
        }

        instance.networkManager.Shutdown();
        instance.started = false;
        instance.networkStartInProgress = false;
        instance.currentRelayJoinCode = string.Empty;
        instance.relayJoinCodeOverride = null;
        instance.requestedStartRole = NetworkStartRole.None;
        instance.ResetLobbyState();
    }

    public static void StartGameFromMenu(string sceneName)
    {
        if (instance == null)
        {
            LoadGameplayScene(sceneName);
            return;
        }

        instance.SendStartGame(sceneName);
    }

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

    public static bool TrySendWagonGrab(Transform wagon, Vector3 playerPosition, Quaternion playerRotation)
    {
        return instance != null && instance.SendWagonAttachIntent(wagon, playerPosition, playerRotation, true);
    }

    public static bool TrySendWagonRelease(Transform wagon, Vector3 playerPosition, Quaternion playerRotation)
    {
        return instance != null && instance.SendWagonAttachIntent(wagon, playerPosition, playerRotation, false);
    }

    public static bool TrySendWagonSendLeverActivation(Transform lever)
    {
        return instance != null && instance.SendLeverActivateIntent(lever);
    }

    public static void AddTeamMoneyAndStartFinalMinute(int amount)
    {
        if (instance == null)
        {
            return;
        }

        instance.AddTeamMoneyAndStartFinalMinuteInternal(amount);
    }

    public static void StartFinalMinute()
    {
        if (instance == null)
        {
            return;
        }

        instance.StartFinalMinuteInternal(true);
    }

    public static bool CanUseWagonSide(Transform wagon, Transform player)
    {
        return instance == null || instance.CanUseWagonSideInternal(wagon, player);
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
            networkManager.OnClientConnectedCallback += OnClientConnected;
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

        if (requestedStartRole == NetworkStartRole.Host)
        {
            started = await TryStartHostAsync();
        }
        else if (requestedStartRole == NetworkStartRole.Client)
        {
            started = await TryStartClientAsync();
        }
        else if (IsEditorHost())
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
                if (relayOk)
                {
                    currentRelayJoinCode = joinCode.Trim().ToUpperInvariant();
                }

                networkStartInProgress = false;
                Debug.Log(relayOk
                    ? "Tiny Netcode client joining Relay room " + joinCode + "."
                    : "Tiny Netcode Relay client failed to start, will retry.");
                return relayOk;
            }
            catch (Exception exception)
            {
                networkStartInProgress = false;
                if (requestedStartRole == NetworkStartRole.Client)
                {
                    requestedStartRole = NetworkStartRole.None;
                    relayJoinCodeOverride = null;
                    currentRelayJoinCode = string.Empty;
                }

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
                    EnsureLobbyClient(networkManager.LocalClientId);
                    BroadcastLobbyState(NetworkDelivery.ReliableSequenced);
                    currentRelayJoinCode = joinCode.Trim().ToUpperInvariant();
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
        if (ok)
        {
            EnsureLobbyClient(networkManager.LocalClientId);
            BroadcastLobbyState(NetworkDelivery.ReliableSequenced);
        }

        networkStartInProgress = false;
        Debug.Log(ok ? "Tiny Netcode started as Editor host on " + Localhost + ":" + Port + "." : "Tiny Netcode host failed to start.");
        return ok;
    }

    private void OnClientDisconnect(ulong clientId)
    {
        activeWagonPushes.Remove(clientId);
        if (networkManager != null && networkManager.IsServer)
        {
            RemoveLobbyClient(clientId);
            BroadcastLobbyState(NetworkDelivery.ReliableSequenced);
        }

        if (IsEditorHost() || networkManager == null || clientId != networkManager.LocalClientId)
        {
            Debug.Log("Tiny Netcode client disconnected: " + clientId);
            return;
        }

        networkManager.Shutdown();
        started = false;
        networkStartInProgress = false;
        handlersRegistered = false;
        clientRetryTimer = 0f;
        ResetLobbyState();
        Debug.Log("Tiny Netcode client disconnected, waiting for host...");
    }

    private void OnClientConnected(ulong clientId)
    {
        if (networkManager == null)
        {
            return;
        }

        Debug.Log(networkManager.IsServer
            ? "Tiny Netcode client connected to host: " + clientId + " (" + networkManager.ConnectedClientsIds.Count + " connected)"
            : "Tiny Netcode connected to host as client " + clientId + ".");

        if (networkManager.IsServer)
        {
            EnsureLobbyClient(clientId);
            BroadcastLobbyState(NetworkDelivery.ReliableSequenced);
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        localPlayer = null;
        appliedLocalSkin = -1;
        appliedSpawnSceneName = null;
        syncedEntities.Clear();
        remoteEntityTargets.Clear();
        lastSentEntityPoses.Clear();
        entityAuthorities.Clear();
        heldEntityOwners.Clear();
        activeWagonPushes.Clear();

        foreach (RemotePlayer remotePlayer in remotePlayers.Values)
        {
            if (remotePlayer.Transform != null)
            {
                Destroy(remotePlayer.Transform.gameObject);
            }
        }

        remotePlayers.Clear();
        RefreshSceneReferences();
        ApplyPendingSpawnIfReady();
    }

#if TINY_HAS_RELAY
    private static async Task EnsureUnityServicesSignedInAsync()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            if (unityServicesInitializationTask == null
                || unityServicesInitializationTask.IsCanceled
                || unityServicesInitializationTask.IsFaulted)
            {
                if (UnityServices.State == ServicesInitializationState.Uninitialized)
                {
                    unityServicesInitializationTask = UnityServices.InitializeAsync();
                }
            }

            if (unityServicesInitializationTask != null)
            {
                await unityServicesInitializationTask;
            }
            else
            {
                while (UnityServices.State == ServicesInitializationState.Initializing)
                {
                    await Task.Yield();
                }
            }
        }

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            if (unityAuthenticationTask == null
                || unityAuthenticationTask.IsCanceled
                || unityAuthenticationTask.IsFaulted)
            {
                unityAuthenticationTask = AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            await unityAuthenticationTask;
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

        if (instance != null && !string.IsNullOrWhiteSpace(instance.relayJoinCodeOverride))
        {
            return instance.relayJoinCodeOverride.Trim().ToUpperInvariant();
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
                    Debug.Log("Tiny Relay join code loaded from " + path + ": " + code.Trim().ToUpperInvariant());
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

        AddUniquePath(paths, Path.Combine(root, RelayJoinCodeFileName));
        AddUniquePath(paths, Path.Combine(root, Path.GetFileNameWithoutExtension(RelayJoinCodeFileName)));
    }

    private static void AddUniquePath(List<string> paths, string path)
    {
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
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(WorldEventMessage, OnWorldEventMessage);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(PingMessage, OnPingMessage);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(StartGameMessage, OnStartGameMessage);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(LobbyStateMessage, OnLobbyStateMessage);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(SkinRequestMessage, OnSkinRequestMessage);
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
                ApplyPendingSpawnIfReady();
            }
        }

        if (syncedEntities.Count == 0)
        {
            RebuildEntityCache();
        }
    }

    private void SendPlayerState()
    {
        using FastBufferWriter writer = new FastBufferWriter(1024, Allocator.Temp);
        Transform playerTransform = localPlayer.transform;
        Component body = localPlayer.GetComponent("TinyRaymanBody");
        int skinIndex = GetLocalSkinIndex();
        float pitch = GetCurrentPitch(localPlayer);
        Quaternion cameraRotation = GetCurrentCameraRotation(localPlayer, playerTransform);
        int jumpSequence = GetJumpSequence(body);
        bool jumpAirborne = GetJumpAirborne(body);
        HandPoseState handPose = GetHandPose(body);
        HeldEntityState heldEntity = GetHeldEntityState(localPlayer);
        writer.WriteValueSafe(networkManager.LocalClientId);
        writer.WriteValueSafe(playerTransform.position);
        writer.WriteValueSafe(playerTransform.rotation);
        writer.WriteValueSafe(pitch);
        writer.WriteValueSafe(cameraRotation);
        writer.WriteValueSafe(jumpSequence);
        writer.WriteValueSafe(jumpAirborne);
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
        if (networkManager != null && networkManager.IsServer)
        {
            string serverKey = GetSyncedEntityKey(wagon);
            if (string.IsNullOrEmpty(serverKey))
            {
                return false;
            }

            if (!activeWagonPushes.ContainsKey(networkManager.LocalClientId)
                && !CanClientUseWagonSide(networkManager.LocalClientId, wagon, playerPosition))
            {
                return false;
            }

            activeWagonPushes[networkManager.LocalClientId] = new WagonPushState(
                serverKey,
                input,
                playerPosition,
                Time.time + WagonPushInputTimeout,
                true);

            return true;
        }

        if (!CanSendWorldIntent(wagon, out string key))
        {
            return false;
        }

        SendWorldIntent(IntentPushWagon, key, wagon.position, wagon.rotation, Vector3.zero, input, playerPosition, playerRotation, NetworkDelivery.UnreliableSequenced);
        return true;
    }

    private bool SendWagonAttachIntent(Transform wagon, Vector3 playerPosition, Quaternion playerRotation, bool isGrab)
    {
        if (networkManager != null && networkManager.IsServer)
        {
            string serverKey = GetSyncedEntityKey(wagon);
            if (string.IsNullOrEmpty(serverKey))
            {
                return false;
            }

            if (!isGrab)
            {
                activeWagonPushes.Remove(networkManager.LocalClientId);
                return true;
            }

            if (!CanClientUseWagonSide(networkManager.LocalClientId, wagon, playerPosition))
            {
                return false;
            }

            activeWagonPushes[networkManager.LocalClientId] = new WagonPushState(
                serverKey,
                0f,
                playerPosition,
                Time.time + WagonPushInputTimeout,
                true);
            return true;
        }

        if (!CanSendWorldIntent(wagon, out string key))
        {
            return false;
        }

        SendWorldIntent(isGrab ? IntentGrabWagon : IntentReleaseWagon, key, wagon.position, wagon.rotation, Vector3.zero, 0f, playerPosition, playerRotation, NetworkDelivery.ReliableSequenced);
        return true;
    }

    private bool SendLeverActivateIntent(Transform lever)
    {
        if (lever == null)
        {
            return false;
        }

        if (networkManager != null && networkManager.IsServer)
        {
            string serverKey = GetSyncedEntityKey(lever);
            if (string.IsNullOrEmpty(serverKey))
            {
                RebuildEntityCache();
                serverKey = GetSyncedEntityKey(lever);
                if (string.IsNullOrEmpty(serverKey))
                {
                    return false;
                }
            }

            ActivateLeverFromServer(serverKey, lever);
            return true;
        }

        if (!CanSendWorldIntent(lever, out string key))
        {
            return false;
        }

        SendWorldIntent(IntentActivateLever, key, lever.position, lever.rotation, Vector3.zero, 0f, Vector3.zero, Quaternion.identity, NetworkDelivery.ReliableSequenced);
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
        if (string.IsNullOrEmpty(key))
        {
            RebuildEntityCache();
            key = GetSyncedEntityKey(entity);
        }

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
        if (localPlayer == null)
        {
            RefreshSceneReferences();
            if (localPlayer == null)
            {
                return;
            }
        }

        reader.ReadValueSafe(out ulong ownerClientId);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        reader.ReadValueSafe(out float pitch);
        reader.ReadValueSafe(out Quaternion cameraRotation);
        reader.ReadValueSafe(out int jumpSequence);
        reader.ReadValueSafe(out bool jumpAirborne);
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
            RelayPlayerPayload(senderClientId, ownerClientId, position, rotation, pitch, cameraRotation, jumpSequence, jumpAirborne, skinIndex, handPose, heldEntity);
        }

        ApplyHeldEntityPayload(senderClientId, heldEntity);
        if (heldEntity.HasEntity && !heldEntity.UseLiveHandPose)
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
        remotePlayer.TargetCameraRotation = cameraRotation;
        remotePlayer.TargetJumpSequence = jumpSequence;
        remotePlayer.TargetJumpAirborne = jumpAirborne;
        remotePlayer.TargetHands = handPose;
        remotePlayer.TargetHeldEntityKey = heldEntity.HasEntity ? heldEntity.Key : null;
        remotePlayer.TargetHeldEntityUseLiveHandPose = heldEntity.HasEntity && heldEntity.UseLiveHandPose;
        remotePlayer.ApplySkin(skinIndex);
    }

    private void RelayPlayerPayload(
        ulong senderClientId,
        ulong ownerClientId,
        Vector3 position,
        Quaternion rotation,
        float pitch,
        Quaternion cameraRotation,
        int jumpSequence,
        bool jumpAirborne,
        int skinIndex,
        HandPoseState handPose,
        HeldEntityState heldEntity)
    {
        using FastBufferWriter writer = new FastBufferWriter(1024, Allocator.Temp);
        writer.WriteValueSafe(ownerClientId);
        writer.WriteValueSafe(position);
        writer.WriteValueSafe(rotation);
        writer.WriteValueSafe(pitch);
        writer.WriteValueSafe(cameraRotation);
        writer.WriteValueSafe(jumpSequence);
        writer.WriteValueSafe(jumpAirborne);
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
                    if (!activeWagonPushes.ContainsKey(senderClientId)
                        && !CanClientUseWagonSide(senderClientId, entity, playerPosition))
                    {
                        return;
                    }

                    activeWagonPushes[senderClientId] = new WagonPushState(
                        key,
                        input,
                        playerPosition,
                        Time.time + WagonPushInputTimeout,
                        true);
                }
                break;

            case IntentGrabWagon:
                if (IsWagon(entity) && CanClientUseWagonSide(senderClientId, entity, playerPosition))
                {
                    activeWagonPushes[senderClientId] = new WagonPushState(
                        key,
                        0f,
                        playerPosition,
                        Time.time + WagonPushInputTimeout,
                        true);
                }
                break;

            case IntentReleaseWagon:
                if (IsWagon(entity))
                {
                    if (activeWagonPushes.TryGetValue(senderClientId, out WagonPushState push)
                        && push.WagonKey == key)
                    {
                        activeWagonPushes.Remove(senderClientId);
                    }
                }
                break;

            case IntentActivateLever:
                if (IsLever(entity))
                {
                    ActivateLeverFromServer(key, entity);
                }
                break;
        }
    }

    private void ActivateLeverFromServer(string key, Transform lever)
    {
        if (string.IsNullOrEmpty(key) || lever == null || !IsLever(lever))
        {
            return;
        }

        activeWagonPushes.Clear();
        InvokeActivateLever(lever, true);
        BroadcastWorldEvent(EventActivateLever, key, teamMoney, GetRoundRemainingSeconds(), NetworkDelivery.ReliableSequenced);
    }

    private void BroadcastWorldEvent(byte eventType, string key, int moneyValue, float remainingSeconds, NetworkDelivery delivery)
    {
        if (networkManager == null || !networkManager.IsServer || !networkManager.IsListening || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        using FastBufferWriter writer = new FastBufferWriter(768, Allocator.Temp);
        FixedString512Bytes fixedKey = key ?? string.Empty;
        writer.WriteValueSafe(eventType);
        writer.WriteValueSafe(fixedKey);
        writer.WriteValueSafe(moneyValue);
        writer.WriteValueSafe(remainingSeconds);

        foreach (ulong clientId in networkManager.ConnectedClientsIds)
        {
            if (clientId != networkManager.LocalClientId)
            {
                networkManager.CustomMessagingManager.SendNamedMessage(WorldEventMessage, clientId, writer, delivery);
            }
        }
    }

    private void OnWorldEventMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out byte eventType);
        reader.ReadValueSafe(out FixedString512Bytes fixedKey);
        reader.ReadValueSafe(out int moneyValue);
        reader.ReadValueSafe(out float remainingSeconds);

        string key = fixedKey.ToString();
        switch (eventType)
        {
            case EventActivateLever:
                activeWagonPushes.Clear();
                if (!syncedEntities.ContainsKey(key))
                {
                    RebuildEntityCache();
                }

                if (syncedEntities.TryGetValue(key, out Transform lever) && lever != null && IsLever(lever))
                {
                    InvokeActivateLever(lever, false);
                }
                break;

            case EventTeamMoney:
                teamMoney = Mathf.Max(0, moneyValue);
                SetRoundRemainingSeconds(remainingSeconds);
                roundTimerForcedRed = true;
                break;
        }
    }

    private void SendLobbyState()
    {
        if (networkManager == null || !networkManager.IsServer || !networkManager.IsListening || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        lobbyStateSendTimer -= Time.deltaTime;
        if (lobbyStateSendTimer > 0f)
        {
            return;
        }

        lobbyStateSendTimer = 0.25f;
        foreach (ulong clientId in networkManager.ConnectedClientsIds)
        {
            EnsureLobbyClient(clientId);
        }

        BroadcastLobbyState(NetworkDelivery.UnreliableSequenced);
    }

    private void OnLobbyStateMessage(ulong senderClientId, FastBufferReader reader)
    {
        ReadLobbyState(reader);
    }

    private void BroadcastLobbyState(NetworkDelivery delivery)
    {
        if (networkManager == null || !networkManager.IsServer || !networkManager.IsListening || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        lastKnownLobbyPlayerCount = Mathf.Clamp(lobbyClientOrder.Count, 1, SkinCount);
        using FastBufferWriter writer = new FastBufferWriter(256, Allocator.Temp);
        WriteLobbyState(writer);
        foreach (ulong clientId in networkManager.ConnectedClientsIds)
        {
            if (clientId != networkManager.LocalClientId)
            {
                networkManager.CustomMessagingManager.SendNamedMessage(LobbyStateMessage, clientId, writer, delivery);
            }
        }
    }

    private void WriteLobbyState(FastBufferWriter writer)
    {
        int count = Mathf.Min(lobbyClientOrder.Count, SkinCount);
        writer.WriteValueSafe(count);
        for (int i = 0; i < count; i++)
        {
            ulong clientId = lobbyClientOrder[i];
            int skinIndex = lobbySkinAssignments.TryGetValue(clientId, out int assignedSkin) ? assignedSkin : -1;
            writer.WriteValueSafe(clientId);
            writer.WriteValueSafe(skinIndex);
        }
    }

    private void ReadLobbyState(FastBufferReader reader)
    {
        reader.ReadValueSafe(out int playerCount);
        int count = Mathf.Clamp(playerCount, 0, SkinCount);
        lobbyClientOrder.Clear();
        lobbySkinAssignments.Clear();

        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out ulong clientId);
            reader.ReadValueSafe(out int skinIndex);
            lobbyClientOrder.Add(clientId);
            lobbySkinAssignments[clientId] = skinIndex;
            if (networkManager != null && clientId == networkManager.LocalClientId && skinIndex >= 0)
            {
                localSelectedSkin = Mathf.Clamp(skinIndex, 0, SkinCount - 1);
            }
        }

        lastKnownLobbyPlayerCount = Mathf.Clamp(count, 1, SkinCount);
    }

    private void EnsureLobbyClient(ulong clientId)
    {
        if (!lobbyClientOrder.Contains(clientId))
        {
            lobbyClientOrder.Add(clientId);
        }

        if (!lobbySkinAssignments.ContainsKey(clientId))
        {
            lobbySkinAssignments[clientId] = -1;
        }
    }

    private void RemoveLobbyClient(ulong clientId)
    {
        lobbyClientOrder.Remove(clientId);
        lobbySkinAssignments.Remove(clientId);
        lastKnownLobbyPlayerCount = Mathf.Clamp(lobbyClientOrder.Count, 0, SkinCount);
    }

    private void ResetLobbyState()
    {
        lobbyClientOrder.Clear();
        lobbySkinAssignments.Clear();
        lastKnownLobbyPlayerCount = 0;
        localSelectedSkin = -1;
    }

    private int GetLobbySkinBySlotInternal(int slot)
    {
        if (slot < 0 || slot >= lobbyClientOrder.Count)
        {
            return -1;
        }

        ulong clientId = lobbyClientOrder[slot];
        return lobbySkinAssignments.TryGetValue(clientId, out int skinIndex) ? skinIndex : -1;
    }

    private int GetLocalAssignedSkin()
    {
        if (networkManager != null && lobbySkinAssignments.TryGetValue(networkManager.LocalClientId, out int skinIndex))
        {
            return skinIndex;
        }

        return localSelectedSkin;
    }

    private bool IsSkinTakenByOtherInternal(int skinIndex)
    {
        if (skinIndex < 0 || skinIndex >= SkinCount)
        {
            return false;
        }

        ulong localClientId = networkManager != null ? networkManager.LocalClientId : ulong.MaxValue;
        foreach (KeyValuePair<ulong, int> assignment in lobbySkinAssignments)
        {
            if (assignment.Value == skinIndex && assignment.Key != localClientId)
            {
                return true;
            }
        }

        return false;
    }

    private bool RequestSkinInternal(int skinIndex)
    {
        int clampedSkin = Mathf.Clamp(skinIndex, 0, SkinCount - 1);
        if (networkManager == null || !networkManager.IsListening)
        {
            localSelectedSkin = clampedSkin;
            return true;
        }

        if (networkManager.IsServer)
        {
            return TryAssignSkin(networkManager.LocalClientId, clampedSkin);
        }

        if (!networkManager.IsConnectedClient || networkManager.CustomMessagingManager == null)
        {
            return false;
        }

        using FastBufferWriter writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(clampedSkin);
        networkManager.CustomMessagingManager.SendNamedMessage(SkinRequestMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
        return true;
    }

    private void OnSkinRequestMessage(ulong senderClientId, FastBufferReader reader)
    {
        if (networkManager == null || !networkManager.IsServer)
        {
            return;
        }

        reader.ReadValueSafe(out int skinIndex);
        TryAssignSkin(senderClientId, skinIndex);
    }

    private bool TryAssignSkin(ulong clientId, int skinIndex)
    {
        int clampedSkin = Mathf.Clamp(skinIndex, 0, SkinCount - 1);
        EnsureLobbyClient(clientId);
        foreach (KeyValuePair<ulong, int> assignment in lobbySkinAssignments)
        {
            if (assignment.Key != clientId && assignment.Value == clampedSkin)
            {
                return false;
            }
        }

        lobbySkinAssignments[clientId] = clampedSkin;
        if (networkManager != null && clientId == networkManager.LocalClientId)
        {
            localSelectedSkin = clampedSkin;
            appliedLocalSkin = -1;
        }

        BroadcastLobbyState(NetworkDelivery.ReliableSequenced);
        return true;
    }

    private void AssignMissingSkins()
    {
        if (networkManager == null || !networkManager.IsServer)
        {
            return;
        }

        foreach (ulong clientId in networkManager.ConnectedClientsIds)
        {
            EnsureLobbyClient(clientId);
        }

        List<int> availableSkins = new List<int>();
        for (int skin = 0; skin < SkinCount; skin++)
        {
            bool taken = false;
            foreach (int assignedSkin in lobbySkinAssignments.Values)
            {
                if (assignedSkin == skin)
                {
                    taken = true;
                    break;
                }
            }

            if (!taken)
            {
                availableSkins.Add(skin);
            }
        }

        for (int i = 0; i < lobbyClientOrder.Count; i++)
        {
            ulong clientId = lobbyClientOrder[i];
            if (lobbySkinAssignments.TryGetValue(clientId, out int assignedSkin) && assignedSkin >= 0)
            {
                continue;
            }

            if (availableSkins.Count == 0)
            {
                lobbySkinAssignments[clientId] = Mathf.Clamp(i, 0, SkinCount - 1);
                continue;
            }

            int randomIndex = UnityEngine.Random.Range(0, availableSkins.Count);
            lobbySkinAssignments[clientId] = availableSkins[randomIndex];
            availableSkins.RemoveAt(randomIndex);
        }

        if (networkManager != null && lobbySkinAssignments.TryGetValue(networkManager.LocalClientId, out int localSkin))
        {
            localSelectedSkin = localSkin;
            appliedLocalSkin = -1;
        }
    }

    private void UpdatePing()
    {
        if (networkManager == null || networkManager.CustomMessagingManager == null || !networkManager.IsListening)
        {
            return;
        }

        if (networkManager.IsServer)
        {
            displayedPingMs = 0f;
            return;
        }

        if (!networkManager.IsConnectedClient)
        {
            return;
        }

        pingSendTimer -= Time.deltaTime;
        if (pingSendTimer > 0f)
        {
            return;
        }

        pingSendTimer = PingSendInterval;
        using FastBufferWriter writer = new FastBufferWriter(32, Allocator.Temp);
        writer.WriteValueSafe(false);
        writer.WriteValueSafe(Time.realtimeSinceStartup);
        networkManager.CustomMessagingManager.SendNamedMessage(PingMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.Unreliable);
    }

    private void OnPingMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out bool isReply);
        reader.ReadValueSafe(out float sentTime);

        if (networkManager == null || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        if (networkManager.IsServer && !isReply)
        {
            using FastBufferWriter writer = new FastBufferWriter(32, Allocator.Temp);
            writer.WriteValueSafe(true);
            writer.WriteValueSafe(sentTime);
            networkManager.CustomMessagingManager.SendNamedMessage(PingMessage, senderClientId, writer, NetworkDelivery.Unreliable);
            return;
        }

        if (!networkManager.IsServer && isReply)
        {
            displayedPingMs = Mathf.Max(0f, (Time.realtimeSinceStartup - sentTime) * 1000f);
        }
    }

    private void SendStartGame(string sceneName)
    {
        string targetScene = string.IsNullOrWhiteSpace(sceneName) ? "SampleScene" : sceneName.Trim();
        if (networkManager != null
            && networkManager.IsListening
            && networkManager.IsServer
            && networkManager.CustomMessagingManager != null)
        {
            AssignMissingSkins();
            BroadcastLobbyState(NetworkDelivery.ReliableSequenced);
            FixedString128Bytes fixedSceneName = targetScene;
            pendingLocalSpawnSlot = GetLobbySlotForClient(networkManager.LocalClientId);
            pendingGameStartRealtime = Time.realtimeSinceStartup;
            gameOverTriggered = false;
            teamMoney = 0;
            roundTimerForcedRed = false;

            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId != networkManager.LocalClientId)
                {
                    using FastBufferWriter writer = new FastBufferWriter(512, Allocator.Temp);
                    writer.WriteValueSafe(fixedSceneName);
                    WriteLobbyState(writer);
                    writer.WriteValueSafe(GetLobbySlotForClient(clientId));
                    networkManager.CustomMessagingManager.SendNamedMessage(StartGameMessage, clientId, writer, NetworkDelivery.ReliableSequenced);
                }
            }
        }

        LoadGameplayScene(targetScene);
    }

    private void OnStartGameMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out FixedString128Bytes fixedSceneName);
        ReadLobbyState(reader);
        reader.ReadValueSafe(out pendingLocalSpawnSlot);
        pendingGameStartRealtime = Time.realtimeSinceStartup;
        gameOverTriggered = false;
        teamMoney = 0;
        roundTimerForcedRed = false;
        LoadGameplayScene(fixedSceneName.ToString());
    }

    private static void LoadGameplayScene(string sceneName)
    {
        string targetScene = string.IsNullOrWhiteSpace(sceneName) ? "SampleScene" : sceneName.Trim();
        if (SceneManager.GetActiveScene().name == targetScene)
        {
            instance?.RefreshSceneReferences();
            instance?.ApplyPendingSpawnIfReady();
            return;
        }

        SceneManager.LoadScene(targetScene);
    }

    private void ApplyPendingSpawnIfReady()
    {
        if (localPlayer == null || networkManager == null)
        {
            return;
        }

        Scene activeScene = SceneManager.GetActiveScene();
        if (appliedSpawnSceneName == activeScene.name)
        {
            return;
        }

        TinySpawnZone[] zones = GetSortedSpawnZones();
        if (zones.Length == 0)
        {
            return;
        }

        TinySpawnZone spawnZone = zones[0];
        for (int i = 1; i < zones.Length; i++)
        {
            zones[i].ClearSpawnBoxes();
        }

        int playerSlot = GetLocalLobbySlot();
        int skinIndex = GetLocalSkinIndex();
        if (!spawnZone.TryGetSpawnPointForSkin(skinIndex, playerSlot, out Vector3 position, out Quaternion rotation))
        {
            return;
        }

        TeleportLocalPlayer(position, rotation);
        spawnZone.ConfigureSpawnBoxes(GetLobbySkinsBySlotArray(), pendingGameStartRealtime);
        Debug.Log($"Tiny spawn applied: skin {skinIndex}, slot {playerSlot}, position {position}.");
        appliedSpawnSceneName = activeScene.name;
    }

    private int GetLocalLobbySlot()
    {
        if (pendingLocalSpawnSlot >= 0)
        {
            return Mathf.Clamp(pendingLocalSpawnSlot, 0, SkinCount - 1);
        }

        if (networkManager == null)
        {
            return 0;
        }

        int slot = GetLobbySlotForClient(networkManager.LocalClientId);
        if (slot >= 0)
        {
            return slot;
        }

        return Mathf.Clamp((int)networkManager.LocalClientId, 0, 3);
    }

    private int GetLobbySlotForClient(ulong clientId)
    {
        return lobbyClientOrder.IndexOf(clientId);
    }

    private int[] GetLobbySkinsBySlotArray()
    {
        int[] skinsBySlot = new int[SkinCount];
        for (int i = 0; i < skinsBySlot.Length; i++)
        {
            skinsBySlot[i] = -1;
        }

        int count = Mathf.Min(lobbyClientOrder.Count, skinsBySlot.Length);
        for (int i = 0; i < count; i++)
        {
            ulong clientId = lobbyClientOrder[i];
            skinsBySlot[i] = lobbySkinAssignments.TryGetValue(clientId, out int skinIndex) ? skinIndex : -1;
        }

        return skinsBySlot;
    }

    private void TeleportLocalPlayer(Vector3 position, Quaternion rotation)
    {
        Transform playerTransform = localPlayer.transform;
        CharacterController controller = playerTransform.GetComponent<CharacterController>();
        bool controllerWasEnabled = controller != null && controller.enabled;
        if (controller != null)
        {
            controller.enabled = false;
        }

        playerTransform.SetPositionAndRotation(position, rotation);

        if (controller != null)
        {
            controller.enabled = controllerWasEnabled;
        }
    }

    private static TinySpawnZone[] GetSortedSpawnZones()
    {
        TinySpawnZone[] zones = FindObjectsByType<TinySpawnZone>(FindObjectsSortMode.None);
        Array.Sort(zones, (left, right) => string.CompareOrdinal(GetHierarchyPath(left.transform), GetHierarchyPath(right.transform)));
        return zones;
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

    private void OnGUI()
    {
        if (networkManager == null || !networkManager.IsListening)
        {
            return;
        }

        DrawTeamMoney();
        DrawRoundTimer();

        string text = networkManager.IsServer
            ? "Ping: host"
            : "Ping: " + Mathf.RoundToInt(displayedPingMs) + " ms";
        GUIStyle style = GUI.skin.label;
        Vector2 size = style.CalcSize(new GUIContent(text));
        GUI.Label(new Rect(Screen.width - size.x - 16f, 12f, size.x + 4f, 24f), text);
    }

    private void DrawTeamMoney()
    {
        GUIStyle style = GUI.skin.label;
        int previousFontSize = style.fontSize;
        FontStyle previousFontStyle = style.fontStyle;
        TextAnchor previousAlignment = style.alignment;

        style.fontSize = 24;
        style.fontStyle = FontStyle.Bold;
        style.alignment = TextAnchor.UpperLeft;
        GUI.Label(new Rect(16f, 12f, 320f, 36f), "Argent equipe : " + teamMoney + " $", style);

        style.fontSize = previousFontSize;
        style.fontStyle = previousFontStyle;
        style.alignment = previousAlignment;
    }

    private void UpdateRoundTimer()
    {
        if (gameOverTriggered || pendingGameStartRealtime < 0f || testRoundDurationSeconds <= 0f)
        {
            return;
        }

        if (GetRoundRemainingSeconds() > 0f)
        {
            return;
        }

        gameOverTriggered = true;
        TriggerLocalGameOverDeath();
    }

    private float GetRoundRemainingSeconds()
    {
        if (pendingGameStartRealtime < 0f || testRoundDurationSeconds <= 0f)
        {
            return testRoundDurationSeconds;
        }

        return Mathf.Max(0f, testRoundDurationSeconds - (Time.realtimeSinceStartup - pendingGameStartRealtime));
    }

    private void DrawRoundTimer()
    {
        if (pendingGameStartRealtime < 0f || testRoundDurationSeconds <= 0f)
        {
            return;
        }

        int seconds = Mathf.CeilToInt(GetRoundRemainingSeconds());
        int minutesPart = seconds / 60;
        int secondsPart = seconds % 60;
        string timerText = minutesPart.ToString("00") + ":" + secondsPart.ToString("00");

        GUIStyle style = GUI.skin.label;
        int previousFontSize = style.fontSize;
        TextAnchor previousAlignment = style.alignment;
        FontStyle previousFontStyle = style.fontStyle;
        Color previousColor = GUI.color;

        style.fontSize = 28;
        style.alignment = TextAnchor.UpperCenter;
        style.fontStyle = FontStyle.Bold;
        if (roundTimerForcedRed)
        {
            GUI.color = Color.red;
        }

        GUI.Label(new Rect(0f, 14f, Screen.width, 42f), timerText, style);

        GUI.color = previousColor;
        style.fontSize = previousFontSize;
        style.alignment = previousAlignment;
        style.fontStyle = previousFontStyle;
    }

    private void AddTeamMoneyAndStartFinalMinuteInternal(int amount)
    {
        if (networkManager != null && networkManager.IsListening && !networkManager.IsServer)
        {
            return;
        }

        teamMoney = Mathf.Max(0, teamMoney + Mathf.Max(0, amount));
        StartFinalMinuteInternal(false);
        BroadcastWorldEvent(EventTeamMoney, string.Empty, teamMoney, GetRoundRemainingSeconds(), NetworkDelivery.ReliableSequenced);
    }

    private void StartFinalMinuteInternal(bool broadcast)
    {
        SetRoundRemainingSeconds(60f);
        roundTimerForcedRed = true;
        gameOverTriggered = false;

        if (broadcast)
        {
            BroadcastWorldEvent(EventTeamMoney, string.Empty, teamMoney, GetRoundRemainingSeconds(), NetworkDelivery.ReliableSequenced);
        }
    }

    private void SetRoundRemainingSeconds(float seconds)
    {
        float remaining = Mathf.Max(0f, seconds);
        if (testRoundDurationSeconds <= 0f)
        {
            testRoundDurationSeconds = remaining;
        }

        float elapsed = pendingGameStartRealtime >= 0f ? Time.realtimeSinceStartup - pendingGameStartRealtime : 0f;
        pendingGameStartRealtime = Time.realtimeSinceStartup - Mathf.Max(0f, elapsed);
        testRoundDurationSeconds = Mathf.Max(elapsed + remaining, 0.01f);
    }

    private void TriggerLocalGameOverDeath()
    {
        localPlayer?.GetType().GetMethod("TriggerEndOfGameDeath")?.Invoke(localPlayer, null);
    }

    private void ProcessServerWagonPushes()
    {
        if (networkManager == null || !networkManager.IsServer || activeWagonPushes.Count == 0)
        {
            return;
        }

        List<ulong> expiredClients = null;
        Dictionary<string, WagonFramePush> wagonPushes = null;
        foreach (KeyValuePair<ulong, WagonPushState> push in activeWagonPushes)
        {
            if (Time.time > push.Value.ExpireTime)
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

            int side = InvokeGetWagonPlayerSide(wagon, push.Value.PlayerPosition);
            if (Mathf.Abs(push.Value.Input) < 0.01f)
            {
                continue;
            }

            wagonPushes ??= new Dictionary<string, WagonFramePush>();
            wagonPushes.TryGetValue(push.Value.WagonKey, out WagonFramePush framePush);
            framePush.Add(side, push.Value.Input);
            wagonPushes[push.Value.WagonKey] = framePush;
        }

        if (expiredClients != null)
        {
            for (int i = 0; i < expiredClients.Count; i++)
            {
                activeWagonPushes.Remove(expiredClients[i]);
            }
        }

        if (wagonPushes != null)
        {
            foreach (KeyValuePair<string, WagonFramePush> push in wagonPushes)
            {
                if (syncedEntities.TryGetValue(push.Key, out Transform wagon) && wagon != null)
                {
                    float railInput = Mathf.Clamp(push.Value.CombinedRailInput, -1f, 1f);
                    if (Mathf.Abs(railInput) > 0.01f)
                    {
                        InvokePushWagonAlongRail(wagon, railInput, Time.deltaTime);
                        BroadcastAuthoritativeEntity(push.Key, wagon);
                    }
                }
            }
        }
    }

    private bool CanUseWagonSideInternal(Transform wagon, Transform player)
    {
        if (wagon == null || player == null || networkManager == null || !networkManager.IsServer)
        {
            return true;
        }

        string wagonKey = GetSyncedEntityKey(wagon);
        if (string.IsNullOrEmpty(wagonKey))
        {
            return true;
        }

        int requestedSide = InvokeGetWagonPlayerSide(wagon, player.position);
        foreach (KeyValuePair<ulong, WagonPushState> push in activeWagonPushes)
        {
            if (push.Value.WagonKey != wagonKey || Time.time > push.Value.ExpireTime)
            {
                continue;
            }

            if (InvokeGetWagonPlayerSide(wagon, push.Value.PlayerPosition) == requestedSide)
            {
                return false;
            }
        }

        return true;
    }

    private bool CanClientUseWagonSide(ulong clientId, Transform wagon, Vector3 playerPosition)
    {
        string wagonKey = GetSyncedEntityKey(wagon);
        if (string.IsNullOrEmpty(wagonKey))
        {
            return true;
        }

        int requestedSide = InvokeGetWagonPlayerSide(wagon, playerPosition);
        foreach (KeyValuePair<ulong, WagonPushState> push in activeWagonPushes)
        {
            if (push.Key == clientId || push.Value.WagonKey != wagonKey || Time.time > push.Value.ExpireTime)
            {
                continue;
            }

            if (InvokeGetWagonPlayerSide(wagon, push.Value.PlayerPosition) == requestedSide)
            {
                return false;
            }
        }

        return true;
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
            remotePlayer.ApplyLookAndJump();
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

        Component[] levers = FindComponentsByTypeName("TinyWagonSendLever");
        for (int i = 0; i < levers.Length; i++)
        {
            AddSyncedEntity(levers[i].transform);
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
        int assignedSkin = GetLocalAssignedSkin();
        if (assignedSkin >= 0)
        {
            return Mathf.Clamp(assignedSkin, 0, SkinCount - 1);
        }

        if (networkManager == null)
        {
            return 0;
        }

        return IsEditorHost() ? 0 : Mathf.Clamp((int)networkManager.LocalClientId, 1, SkinCount - 1);
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
        public readonly bool IsAttached;

        public WagonPushState(string wagonKey, float input, Vector3 playerPosition, float expireTime, bool isAttached)
        {
            WagonKey = wagonKey;
            Input = input;
            PlayerPosition = playerPosition;
            ExpireTime = expireTime;
            IsAttached = isAttached;
        }
    }

    private struct WagonFramePush
    {
        private bool hasBackSide;
        private bool hasFrontSide;
        private float backSideInput;
        private float frontSideInput;

        public float CombinedRailInput => backSideInput + frontSideInput;

        public void Add(int side, float input)
        {
            if (side < 0)
            {
                if (hasFrontSide)
                {
                    return;
                }

                hasFrontSide = true;
                frontSideInput = input * side;
                return;
            }

            if (hasBackSide)
            {
                return;
            }

            hasBackSide = true;
            backSideInput = input * side;
        }
    }

    private readonly struct HandPoseState
    {
        public readonly bool IsValid;
        public readonly Vector3 LeftPosition;
        public readonly Quaternion LeftRotation;
        public readonly Vector3 RightPosition;
        public readonly Quaternion RightRotation;
        public readonly bool LeftActive;
        public readonly bool RightActive;

        public HandPoseState(
            bool isValid,
            Vector3 leftPosition,
            Quaternion leftRotation,
            Vector3 rightPosition,
            Quaternion rightRotation)
            : this(isValid, leftPosition, leftRotation, rightPosition, rightRotation, true, true)
        {
        }

        public HandPoseState(
            bool isValid,
            Vector3 leftPosition,
            Quaternion leftRotation,
            Vector3 rightPosition,
            Quaternion rightRotation,
            bool leftActive,
            bool rightActive)
        {
            IsValid = isValid;
            LeftPosition = leftPosition;
            LeftRotation = leftRotation;
            RightPosition = rightPosition;
            RightRotation = rightRotation;
            LeftActive = leftActive;
            RightActive = rightActive;
        }
    }

    private readonly struct HeldEntityState
    {
        public readonly bool HasEntity;
        public readonly string Key;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Velocity;
        public readonly bool UseLiveHandPose;

        public HeldEntityState(string key, Vector3 position, Quaternion rotation, Vector3 velocity, bool useLiveHandPose = false)
        {
            HasEntity = !string.IsNullOrEmpty(key);
            Key = key;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            UseLiveHandPose = useLiveHandPose;
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
        public Quaternion TargetCameraRotation;
        public int TargetJumpSequence;
        public bool TargetJumpAirborne;
        public HandPoseState TargetHands;
        public string TargetHeldEntityKey;
        public bool TargetHeldEntityUseLiveHandPose;
        private int appliedJumpSequence;

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

        public void ApplyLookAndJump()
        {
            if (body != null)
            {
                InvokeSetCameraPitch(body, TargetPitch);
                if (TargetJumpSequence > appliedJumpSequence)
                {
                    appliedJumpSequence = TargetJumpSequence;
                }

                if (appliedJumpSequence > 0)
                {
                    InvokeApplyRemoteJumpState(body, appliedJumpSequence, TargetJumpAirborne);
                }
            }
        }

        public void ApplyHands(Dictionary<string, Transform> syncedEntities)
        {
            if (body == null)
            {
                return;
            }

            if (!TargetHeldEntityUseLiveHandPose
                && !string.IsNullOrEmpty(TargetHeldEntityKey)
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

    private static Quaternion GetCurrentCameraRotation(Component controller, Transform fallback)
    {
        object value = controller?.GetType().GetProperty("CurrentCameraWorldRotation")?.GetValue(controller);
        return value is Quaternion rotation
            ? rotation
            : fallback != null
                ? fallback.rotation
                : Quaternion.identity;
    }

    private static int GetJumpSequence(Component body)
    {
        object value = body?.GetType().GetProperty("JumpSequence")?.GetValue(body);
        return value is int sequence ? sequence : 0;
    }

    private static bool GetJumpAirborne(Component body)
    {
        object value = body?.GetType().GetProperty("IsJumpAirborne")?.GetValue(body);
        return value is bool isAirborne && isAirborne;
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
            GetRigidbodyVelocity(heldEntity),
            GetIsClimbingWithHeldItem(controller));
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
        writer.WriteValueSafe(heldEntity.UseLiveHandPose);
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
        reader.ReadValueSafe(out bool useLiveHandPose);
        return new HeldEntityState(fixedKey.ToString(), position, rotation, velocity, useLiveHandPose);
    }

    private static bool GetIsClimbingWithHeldItem(Component controller)
    {
        object value = controller?.GetType().GetProperty("IsClimbingWithHeldItem")?.GetValue(controller);
        return value is bool isClimbingWithHeldItem && isClimbingWithHeldItem;
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

    private static bool IsLever(Transform entity)
    {
        return entity != null && entity.GetComponent("TinyWagonSendLever") != null;
    }

    private static void InvokeActivateLever(Transform entity, bool serverAuthoritative)
    {
        Component lever = entity != null ? entity.GetComponent("TinyWagonSendLever") : null;
        lever?.GetType().GetMethod("ActivateFromNetwork")?.Invoke(lever, new object[] { serverAuthoritative });
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
            (Quaternion)rotationArgs[2],
            GetItemLeftHandActive(item),
            GetItemRightHandActive(item));
        return true;
    }

    private static bool GetItemLeftHandActive(Component item)
    {
        object value = item?.GetType().GetProperty("LeftHandActive")?.GetValue(item);
        return !(value is bool active) || active;
    }

    private static bool GetItemRightHandActive(Component item)
    {
        object value = item?.GetType().GetProperty("RightHandActive")?.GetValue(item);
        return !(value is bool active) || active;
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

    private static void InvokePushWagonAlongRail(Transform entity, float railInput, float deltaTime)
    {
        Component wagon = entity != null ? entity.GetComponent("TinyRailWagon") : null;
        wagon?.GetType().GetMethod("PushAlongRail")?.Invoke(wagon, new object[] { railInput, deltaTime });
    }

    private static int InvokeGetWagonPlayerSide(Transform entity, Vector3 playerPosition)
    {
        Component wagon = entity != null ? entity.GetComponent("TinyRailWagon") : null;
        object value = wagon?.GetType().GetMethod("GetPlayerSide", new[] { typeof(Vector3) })?.Invoke(wagon, new object[] { playerPosition });
        return value is int side ? side : 1;
    }

    private static void SetRigidbodyVelocity(Rigidbody rigidbody, Vector3 velocity)
    {
        if (rigidbody == null || rigidbody.isKinematic)
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

    private static void InvokeSetCameraLook(Component body, float pitch, Quaternion cameraRotation)
    {
        var method = body?.GetType().GetMethod("SetCameraLook", new[] { typeof(float), typeof(Quaternion) });
        if (method != null)
        {
            method.Invoke(body, new object[] { pitch, cameraRotation });
            return;
        }

        InvokeSetCameraPitch(body, pitch);
    }

    private static void InvokeNotifyJump(Component body)
    {
        body?.GetType().GetMethod("NotifyJump")?.Invoke(body, Array.Empty<object>());
    }

    private static void InvokeApplyRemoteJumpState(Component body, int sequence, bool isAirborne)
    {
        var method = body?.GetType().GetMethod("ApplyRemoteJumpState", new[] { typeof(int), typeof(bool) });
        if (method != null)
        {
            method.Invoke(body, new object[] { sequence, isAirborne });
            return;
        }

        if (isAirborne)
        {
            InvokeNotifyJump(body);
        }
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
        var selectiveMethod = body?.GetType().GetMethod(
            "ApplyRemoteHandAnchors",
            new[]
            {
                typeof(Vector3),
                typeof(Quaternion),
                typeof(Vector3),
                typeof(Quaternion),
                typeof(bool),
                typeof(bool),
                typeof(bool)
            });
        if (selectiveMethod != null)
        {
            selectiveMethod.Invoke(
                body,
                new object[]
                {
                    handPose.LeftPosition,
                    handPose.LeftRotation,
                    handPose.RightPosition,
                    handPose.RightRotation,
                    handPose.LeftActive,
                    handPose.RightActive,
                    true
                });
            return;
        }

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
