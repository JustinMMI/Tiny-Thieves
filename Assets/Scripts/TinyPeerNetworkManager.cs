using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public sealed class TinyPeerNetworkManager : MonoBehaviour
{
    private const int Port = 7777;
    private const string Localhost = "127.0.0.1";
    private const float SendInterval = 0.05f;
    private const float EntitySendInterval = 0.1f;
    private const float RemoteFollowSharpness = 18f;

    private readonly Queue<ReceivedPacket> pendingPackets = new Queue<ReceivedPacket>();
    private readonly object pendingLock = new object();
    private readonly List<IPEndPoint> clients = new List<IPEndPoint>();
    private readonly Dictionary<string, RemotePlayer> remotePlayers = new Dictionary<string, RemotePlayer>();
    private readonly Dictionary<string, Transform> syncedEntities = new Dictionary<string, Transform>();
    private readonly Dictionary<string, PosePacket> remoteEntityTargets = new Dictionary<string, PosePacket>();
    private readonly Dictionary<string, PosePacket> lastSentEntityPoses = new Dictionary<string, PosePacket>();

    private UdpClient udpClient;
    private Thread receiveThread;
    private IPEndPoint serverEndpoint;
    private TinyFirstPersonController localPlayer;
    private string peerId;
    private bool isHost;
    private bool isRunning;
    private bool hasServerHandshake;
    private float sendTimer;
    private float entitySendTimer;
    private float helloRetryTimer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateForScene()
    {
        if (FindFirstObjectByType<TinyPeerNetworkManager>() != null)
        {
            return;
        }

        GameObject networkObject = new GameObject("Tiny Peer Network Manager");
        DontDestroyOnLoad(networkObject);
        networkObject.AddComponent<TinyPeerNetworkManager>();
    }

    private void Awake()
    {
        peerId = Guid.NewGuid().ToString("N").Substring(0, 8);
#if UNITY_EDITOR
        isHost = true;
#else
        isHost = false;
#endif
    }

    private void Start()
    {
        localPlayer = FindFirstObjectByType<TinyFirstPersonController>();
        RebuildEntityCache();
        StartNetwork();
    }

    private void Update()
    {
        ProcessIncomingPackets();
        RetryClientHello();
        SendLocalState();
        UpdateRemotePlayers();
        ApplyRemoteEntityTargets();
    }

    private void OnDestroy()
    {
        StopNetwork();
    }

    private void StartNetwork()
    {
        try
        {
            udpClient = isHost ? new UdpClient(Port) : new UdpClient(0);
            serverEndpoint = new IPEndPoint(IPAddress.Parse(Localhost), Port);
            isRunning = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();

            if (!isHost)
            {
                SendRaw("HELLO|" + peerId, serverEndpoint);
            }

            Debug.Log(isHost
                ? "Tiny network started as host on UDP :" + Port
                : "Tiny network started as client to " + Localhost + ":" + Port);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Tiny network failed to start: " + exception.Message);
            StopNetwork();
        }
    }

    private void StopNetwork()
    {
        isRunning = false;
        try
        {
            udpClient?.Close();
        }
        catch
        {
        }

        udpClient = null;
    }

    private void ReceiveLoop()
    {
        while (isRunning && udpClient != null)
        {
            try
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] bytes = udpClient.Receive(ref remote);
                string message = Encoding.UTF8.GetString(bytes);
                lock (pendingLock)
                {
                    pendingPackets.Enqueue(new ReceivedPacket(message, remote));
                }
            }
            catch
            {
                if (isRunning)
                {
                    Thread.Sleep(10);
                }
            }
        }
    }

    private void ProcessIncomingPackets()
    {
        while (true)
        {
            ReceivedPacket packet;
            lock (pendingLock)
            {
                if (pendingPackets.Count == 0)
                {
                    break;
                }

                packet = pendingPackets.Dequeue();
            }

            HandlePacket(packet);
        }
    }

    private void HandlePacket(ReceivedPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.Message))
        {
            return;
        }

        string[] parts = packet.Message.Split('|');
        if (parts.Length == 0)
        {
            return;
        }

        if (parts[0] == "HELLO" && isHost)
        {
            AddClient(packet.Remote);
            SendRaw("WELCOME|" + peerId, packet.Remote);
            return;
        }

        if (parts[0] == "WELCOME" && !isHost)
        {
            hasServerHandshake = true;
            return;
        }

        if (isHost)
        {
            AddClient(packet.Remote);
            RelayToClients(packet.Message, packet.Remote);
        }

        if (parts[0] == "P")
        {
            HandlePlayerPacket(parts);
        }
        else if (parts[0] == "E")
        {
            HandleEntityPacket(parts);
        }
    }

    private void SendLocalState()
    {
        if (localPlayer == null || udpClient == null)
        {
            return;
        }

        sendTimer -= Time.deltaTime;
        entitySendTimer -= Time.deltaTime;

        if (sendTimer <= 0f)
        {
            sendTimer = SendInterval;
            Transform playerTransform = localPlayer.transform;
            SendToNetwork(FormatPose("P", peerId, playerTransform, null));
        }

        if (entitySendTimer <= 0f)
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
                    SendToNetwork(FormatPose("E", peerId, entity.Value, entity.Key));
                }
            }
        }
    }

    private void RetryClientHello()
    {
        if (isHost || hasServerHandshake || udpClient == null)
        {
            return;
        }

        helloRetryTimer -= Time.deltaTime;
        if (helloRetryTimer > 0f)
        {
            return;
        }

        helloRetryTimer = 1f;
        SendRaw("HELLO|" + peerId, serverEndpoint);
    }

    private void SendToNetwork(string message)
    {
        if (isHost)
        {
            RelayToClients(message, null);
        }
        else
        {
            SendRaw(message, serverEndpoint);
        }
    }

    private void SendRaw(string message, IPEndPoint endpoint)
    {
        if (udpClient == null || endpoint == null)
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(message);
        udpClient.Send(bytes, bytes.Length, endpoint);
    }

    private void RelayToClients(string message, IPEndPoint except)
    {
        for (int i = clients.Count - 1; i >= 0; i--)
        {
            IPEndPoint client = clients[i];
            if (except != null && EndpointsEqual(client, except))
            {
                continue;
            }

            SendRaw(message, client);
        }
    }

    private void AddClient(IPEndPoint endpoint)
    {
        if (endpoint == null)
        {
            return;
        }

        for (int i = 0; i < clients.Count; i++)
        {
            if (EndpointsEqual(clients[i], endpoint))
            {
                return;
            }
        }

        clients.Add(new IPEndPoint(endpoint.Address, endpoint.Port));
        Debug.Log("Tiny network client connected: " + endpoint);
    }

    private void HandlePlayerPacket(string[] parts)
    {
        if (parts.Length < 9 || parts[1] == peerId)
        {
            return;
        }

        PosePacket pose = ParsePose(parts, 2);
        if (!remotePlayers.TryGetValue(parts[1], out RemotePlayer remotePlayer))
        {
            remotePlayer = CreateRemotePlayer(parts[1]);
            remotePlayers.Add(parts[1], remotePlayer);
        }

        remotePlayer.TargetPosition = pose.Position;
        remotePlayer.TargetRotation = pose.Rotation;
    }

    private void HandleEntityPacket(string[] parts)
    {
        if (parts.Length < 10 || parts[1] == peerId)
        {
            return;
        }

        string key = parts[2];
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        remoteEntityTargets[key] = ParsePose(parts, 3);
    }

    private RemotePlayer CreateRemotePlayer(string remoteId)
    {
        GameObject source = localPlayer != null ? localPlayer.gameObject : null;
        GameObject remoteObject = source != null
            ? Instantiate(source)
            : GameObject.CreatePrimitive(PrimitiveType.Capsule);

        remoteObject.name = "Remote Player " + remoteId;
        remoteObject.tag = "Untagged";
        DisableRemoteControl(remoteObject);

        RemotePlayer remotePlayer = new RemotePlayer(remoteObject.transform);
        remotePlayer.Transform.position = localPlayer != null ? localPlayer.transform.position : Vector3.zero;
        remotePlayer.Transform.rotation = localPlayer != null ? localPlayer.transform.rotation : Quaternion.identity;
        remotePlayer.TargetPosition = remotePlayer.Transform.position;
        remotePlayer.TargetRotation = remotePlayer.Transform.rotation;
        return remotePlayer;
    }

    private static void DisableRemoteControl(GameObject remoteObject)
    {
        TinyFirstPersonController controller = remoteObject.GetComponent<TinyFirstPersonController>();
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
        }
    }

    private void ApplyRemoteEntityTargets()
    {
        float follow = 1f - Mathf.Exp(-RemoteFollowSharpness * Time.deltaTime);
        foreach (KeyValuePair<string, PosePacket> target in remoteEntityTargets)
        {
            if (!syncedEntities.TryGetValue(target.Key, out Transform entity) || entity == null)
            {
                continue;
            }

            Vector3 position = Vector3.Lerp(entity.position, target.Value.Position, follow);
            Quaternion rotation = Quaternion.Slerp(entity.rotation, target.Value.Rotation, follow);
            Rigidbody rigidbody = entity.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.position = position;
                rigidbody.rotation = rotation;
#if UNITY_6000_0_OR_NEWER
                rigidbody.linearVelocity = Vector3.zero;
#else
                rigidbody.velocity = Vector3.zero;
#endif
                rigidbody.angularVelocity = Vector3.zero;
            }
            else
            {
                entity.SetPositionAndRotation(position, rotation);
            }

            lastSentEntityPoses[target.Key] = new PosePacket(position, rotation);
        }
    }

    private void RebuildEntityCache()
    {
        syncedEntities.Clear();
        TinyItem[] items = FindObjectsByType<TinyItem>(FindObjectsSortMode.None);
        for (int i = 0; i < items.Length; i++)
        {
            AddSyncedEntity(items[i].transform);
        }

        TinyRailWagon[] wagons = FindObjectsByType<TinyRailWagon>(FindObjectsSortMode.None);
        for (int i = 0; i < wagons.Length; i++)
        {
            AddSyncedEntity(wagons[i].transform);
        }
    }

    private void AddSyncedEntity(Transform entity)
    {
        if (entity == null)
        {
            return;
        }

        string path = GetScenePath(entity);
        if (!string.IsNullOrEmpty(path) && !syncedEntities.ContainsKey(path))
        {
            syncedEntities.Add(path, entity);
        }
    }

    private bool ShouldSendEntityPose(string key, Transform entity)
    {
        if (isHost)
        {
            lastSentEntityPoses[key] = new PosePacket(entity.position, entity.rotation);
            return true;
        }

        if (!lastSentEntityPoses.TryGetValue(key, out PosePacket lastPose))
        {
            lastSentEntityPoses[key] = new PosePacket(entity.position, entity.rotation);
            return false;
        }

        bool moved = (entity.position - lastPose.Position).sqrMagnitude > 0.0004f
            || Quaternion.Angle(entity.rotation, lastPose.Rotation) > 1.5f;
        if (moved)
        {
            lastSentEntityPoses[key] = new PosePacket(entity.position, entity.rotation);
        }

        return moved;
    }

    private static string FormatPose(string prefix, string senderId, Transform transform, string entityKey)
    {
        Vector3 position = transform.position;
        Quaternion rotation = transform.rotation;
        if (prefix == "E")
        {
            return string.Join("|",
                prefix,
                senderId,
                entityKey,
                F(position.x),
                F(position.y),
                F(position.z),
                F(rotation.x),
                F(rotation.y),
                F(rotation.z),
                F(rotation.w));
        }

        return string.Join("|",
            prefix,
            senderId,
            F(position.x),
            F(position.y),
            F(position.z),
            F(rotation.x),
            F(rotation.y),
            F(rotation.z),
            F(rotation.w));
    }

    private static PosePacket ParsePose(string[] parts, int start)
    {
        return new PosePacket(
            new Vector3(P(parts[start]), P(parts[start + 1]), P(parts[start + 2])),
            new Quaternion(P(parts[start + 3]), P(parts[start + 4]), P(parts[start + 5]), P(parts[start + 6])));
    }

    private static string F(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static float P(string value)
    {
        return float.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool EndpointsEqual(IPEndPoint a, IPEndPoint b)
    {
        return a != null && b != null && a.Address.Equals(b.Address) && a.Port == b.Port;
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

    private readonly struct ReceivedPacket
    {
        public readonly string Message;
        public readonly IPEndPoint Remote;

        public ReceivedPacket(string message, IPEndPoint remote)
        {
            Message = message;
            Remote = remote;
        }
    }

    private readonly struct PosePacket
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public PosePacket(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    private sealed class RemotePlayer
    {
        public readonly Transform Transform;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;

        public RemotePlayer(Transform transform)
        {
            Transform = transform;
        }
    }
}
