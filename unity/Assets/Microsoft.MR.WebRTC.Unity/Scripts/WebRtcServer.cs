using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.MixedReality.WebRTC;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace WebRTC
{
    /// <summary>
    /// The WebRTC server performs two important duties.
    /// 1) Configuration management. Hosting the config remotely allows better management of many
    ///    devices. On awake, this system will request the config from the server for consumption
    ///    by local systems.
    /// 2) Negotiating PeerConnections. This system allows one client to request a connection from
    ///    another. Basically, we generate a pair of unique ids for the connection, spawn a local
    ///    PeerConnection and configure it. We then send those IDs to another client, which will
    ///    spawn its own PeerConnection confgigured with the pair of IDs and then generate an offer back
    ///    to the original requester, who will answer it. This allows a lot of flexibility, including
    ///    one or two directional feeds, options for visualizations, etc. The only requirement is that
    ///    the prefabs used have a PeerConnection on their top level object.
    /// </summary>
    public class WebRtcServer : MonoBehaviour
    {
        const string IpOverrideKey = "WebRtcIpOverride";
        const string IdOverrideKey = "WebRtcIdOverride";

        [Serializable]
        public enum WebRtcServerMode
        {
            PeerToPeer = 1,
            ProduceToSfu = 2,
            ConsumeFromSfu = 3
        }

        public struct ConnectionInfo
        {
            public PeerConnection Connection;
            public DateTime CreationTime;
            public bool Host;

#if UNITY_EDITOR
            public double LastTimestampMs;
            public int LastBytesSent;
            public int LastBytesReceived;
            public MovingAverage kbpsSent;
            public MovingAverage kbpsReceived;
            public bool ShowDetails;
            public Microsoft.MixedReality.WebRTC.PeerConnection.StatsReport LastReport;
#endif
        }

        #region Singleton
        // Manually implement singleton because this class moves around projects a lot so it's nice that it
        // has no dependencies
        private static WebRtcServer _instance = null;
        public static WebRtcServer Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<WebRtcServer>();
                }
                return _instance;
            }
            set
            {
                _instance = value;
            }
        }

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
        }
        #endregion

        [Header("Server Configuration")]

        [SerializeField]
        private string serverAddress = "http://127.0.0.1:3000";

        [SerializeField]
        private string stunServer = "stun.l.google.com:19302";

        [SerializeField]
        private float pollIntervalMs = 500f;

        [SerializeField]
        private WebRtcServerMode mode = WebRtcServerMode.PeerToPeer;

        [Header("Host Configuration (optional)")]

        [SerializeField]
        [Tooltip("Set this to true to enable hosting of content.")]
        private bool allowHostingContent = false;

        [SerializeField]
        [Tooltip("For WebRtc producers, ID for the WebRtc stream is produced. For clients, ID of the peer. Defaults to machine name.")]
        private string hostId = "";

        [SerializeField]
        [Tooltip("Parent to which spawned connection objects are attached.")]
        private Transform hostParent = null;

        [SerializeField]
        [Tooltip("Spawned when responding to a new connection request. If null, responding to requests will be disabled.")]
        private GameObject hostPrefab = null;

        [SerializeField]
        [Tooltip("A unified provider used to scale to a large client number while avoiding duplicate work.")]
        private YuvProvider defaultProvider = null;

        [Header("Debugging")]
        [SerializeField]
        private Microsoft.MixedReality.WebRTC.LoggingSeverity logLevel = Microsoft.MixedReality.WebRTC.LoggingSeverity.None;
        private Microsoft.MixedReality.WebRTC.LoggingSeverity lastLogLevel = Microsoft.MixedReality.WebRTC.LoggingSeverity.None;

        private string sfuStreamConsumed = null;

        private WebRtcServerConnection connection = null;

        private string localId = "";

        private bool initialized = false;

        private List<Task> connectionTasks = new List<Task>();

        private List<ConnectionInfo> connections = new List<ConnectionInfo>();

        public YuvProvider DefaultProvider => defaultProvider;

        public string ServerAddress {
            get => serverAddress;
            set => serverAddress = value;
        }

        PeerConnectionConfiguration _peerConfig = null;
        public PeerConnectionConfiguration PeerConfig
            {
            get
            {
                if (_peerConfig == null)
                {
                    _peerConfig = new PeerConnectionConfiguration();
                    _peerConfig.IceTransportType = IceTransportType.All;
                    _peerConfig.SdpSemantic = SdpSemantic.UnifiedPlan;
                    _peerConfig.BundlePolicy = BundlePolicy.MaxBundle;
                    _peerConfig.IceServers.Add(new IceServer
                    {
                        Urls = { $"stun:{stunServer}" },
                        TurnUserName = "",
                        TurnPassword = ""
                    });
                }
                return _peerConfig;
            }
        }

        public void ProduceWebRtcStream(string remoteId, GameObject prefab, Transform parent = null)
        {
            EnsureInitialized();

            switch (mode)
            {
                case WebRtcServerMode.PeerToPeer:
                    StartConnectionRequest(remoteId, prefab, parent);
                    break;

                case WebRtcServerMode.ProduceToSfu:
                    StartBroadcastingToSfu(remoteId, prefab, parent);
                    break;
                    
                default:
                    break;
            }
        }

        public void ConsumeWebRtcStream(string remoteId, GameObject prefab, Transform parent = null)
        {
            EnsureInitialized();

            switch (mode)
            {
                case WebRtcServerMode.PeerToPeer:
                    StartConnectionRequest(remoteId, prefab, parent);
                    break;

                case WebRtcServerMode.ConsumeFromSfu:
                    sfuStreamConsumed = remoteId;
                    StartConnectionRequest(remoteId, prefab, parent);
                    break;

                default:
                    break;
            }
        }

        private void StartConnectionRequest(string remoteId, GameObject prefab, Transform parent = null)
        {
            const bool DisableWebRTC = false; // DeviceConfig.Overrides.DisableWebRTC
            if (DisableWebRTC)
            {
                Debug.LogWarning("Disabling web rtc by DeviceConfig.Overrides request.");
                return;
            }

            connectionTasks.Add(StartConnectionRequestAsync(remoteId, prefab));
        }

        private void StartBroadcastingToSfu(string remoteId, GameObject prefab, Transform parent = null)
        {
            StartCoroutine(CO_EstablishAsBroadcaster(remoteId));
        }

        private void OnEnable()
        {
            EnsureInitialized();

            switch (mode)
            {
                case WebRtcServerMode.PeerToPeer:
                    break;

                case WebRtcServerMode.ProduceToSfu:
                    StartCoroutine(CO_BroadcastContent(hostId));
                    break;

                case WebRtcServerMode.ConsumeFromSfu:
                    break;
            }
        }

        private void EnsureInitialized()
        {
            if (!initialized)
            {
                const bool QuietLogsHoloportation = true; // DeviceConfig.Overrides.QuietLogsHoloportation
                if (!QuietLogsHoloportation)
                {
                    Microsoft.MixedReality.WebRTC.PeerConnection.SetLogLevel(logLevel);
                    lastLogLevel = logLevel;
                }

                const string MediaSoupServerIp = null; // DeviceConfig.Overrides.MediaSoupServerIp
                if (!string.IsNullOrWhiteSpace(MediaSoupServerIp))
                {
                    serverAddress = MediaSoupServerIp;
                }

#if UNITY_EDITOR
                string overrideServer = PlayerPrefs.GetString(IpOverrideKey, null);
                if (!string.IsNullOrWhiteSpace(overrideServer))
                {
                    serverAddress = overrideServer;
                }
#endif

                if (!serverAddress.EndsWith("/"))
                {
                    serverAddress += "/";
                }

                localId = (mode == WebRtcServerMode.PeerToPeer && allowHostingContent) ? hostId : SystemInfo.deviceName;

#if UNITY_EDITOR
                string overrideId = PlayerPrefs.GetString(IdOverrideKey, null);
                if (!string.IsNullOrWhiteSpace(overrideId))
                {
                    localId = overrideId;
                }
#endif

                // If we are acting as a client, make our id unique
                if (!allowHostingContent)
                {
                    localId = string.Format("{0}-{1}", localId, Guid.NewGuid());
                }

                if (mode == WebRtcServerMode.PeerToPeer)
                {
                    connection = new WebRtcServerConnection(serverAddress, pollIntervalMs, $"requests/{localId}", this);
                    connection.OnPolledMessageReceived += OnMessageReceived;
                }

                initialized = true;
            }
        }

        private IEnumerator CO_BroadcastContent(string streamName)
        {
            yield return CO_EstablishAsBroadcaster(streamName);
        }

        private IEnumerator CO_EstablishAsBroadcaster(string streamName)
        {
            string body = $"{{ \"signalFromId\": \"{localId}\" }}";
            byte[] encodedBody = UTF8Encoding.UTF8.GetBytes(body);
            var www = new UnityWebRequest($"{serverAddress}broadcast/{streamName}", UnityWebRequest.kHttpVerbPOST);
            www.uploadHandler = new UploadHandlerRaw(encodedBody);
            www.uploadHandler.contentType = "application/json";
            www.downloadHandler = new DownloadHandlerBuffer();
            yield return www.SendWebRequest();

            try
            {
                if (www.isNetworkError)
                {
                    throw new Exception("network error");
                }

                if (www.isHttpError)
                {
                    throw new Exception("http error");
                }

                string response = www.downloadHandler.text;
                Debug.Log($"SFU/Transport capabilities received:\n{response}");
                Dictionary<string, object> serverProperties = JsonUtility.FromJson<Dictionary<string, object>>(response);

                // TODO: Create our transports, emit an offer
                connectionTasks.Add(StartBroadcasterConnectionAsync(streamName));
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to etablish as a broadcaster ({e.Message}). Stream will be empty.");
                Debug.LogException(e);
            }
        }

        private Task StartBroadcasterConnectionAsync(string streamName)
        {
            GameObject go = Instantiate(hostPrefab, hostParent);
            PeerConnection pc = go.GetComponent<PeerConnection>();
            if (pc == null || pc.AutoInitializeOnStart)
            {
                Destroy(go);
                throw new Exception("Host prefab must have PeerConnection and be setup not to auto start.");
            }

            AddConnection(pc, true);

            // Initialize a PeerConnection with the connection specific ids
            // The Id of the other party is the SFU server itself, known by the broadcast stream id
            string fullRemoteId = streamName;
            return pc.InitializeAsync(localId, fullRemoteId);
        }

        private async Task ResumeSfuConsumerAsync(string streamName)
        {
            try
            {
                string body = $"{{ \"signalFromId\": \"{localId}\" }}";
                Debug.Log($"{serverAddress}view/{streamName}/resume");
                await SendPostHttpRequestAsync<Dictionary<string, object>>($"{serverAddress}view/{streamName}/resume", body);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private async Task ResumeSfuBroadcasterAsync(string streamName)
        {
            try
            {
                string body = $"{{ \"signalFromId\": \"{localId}\" }}";
                await SendPostHttpRequestAsync<Dictionary<string, object>>($"{serverAddress}broadcast/{streamName}/resume", body);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private Task ShutdownConnectionAsync()
        {
            switch (mode)
            {
                case WebRtcServerMode.ConsumeFromSfu:
                    return ShutdownSfuConsumerConnectionAsync(sfuStreamConsumed, localId);

                case WebRtcServerMode.ProduceToSfu:
                    return ShutdownSfuBroadcasterConnectionAsync(hostId, localId);

                case WebRtcServerMode.PeerToPeer:
                    return Task.CompletedTask;

                default:
                    return Task.CompletedTask;
            }
        }

        private async Task ShutdownSfuBroadcasterConnectionAsync(string streamName, string localId)
        {
            try
            {
                string body = $"{{ \"signalFromId\": \"{localId}\" }}";
                await SendPostHttpRequestAsync<Dictionary<string, object>>($"{serverAddress}broadcast/{streamName}/shutdown", body);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private async Task ShutdownSfuConsumerConnectionAsync(string streamName, string localId)
        {
            try
            {
                string body = $"{{ \"signalFromId\": \"{localId}\" }}";
                await SendPostHttpRequestAsync<Dictionary<string, object>>($"{serverAddress}view/{streamName}/shutdown", body);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private async Task<T> SendPostHttpRequestAsync<T>(string requestUri, string body)
        {
            using (HttpClientHandler httpMessageHandler = new HttpClientHandler())
            {
                using (HttpClient httpClient = new HttpClient(httpMessageHandler))
                {
                    httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using (StringContent httpContent = new StringContent(body, Encoding.UTF8, "application/json"))
                    {
                        using (HttpResponseMessage httpResponseMessage = await httpClient.PostAsync(requestUri, httpContent))
                        {
                            httpResponseMessage.EnsureSuccessStatusCode();
                            return JsonUtility.FromJson<T>(await httpResponseMessage.Content.ReadAsStringAsync());
                        }
                    }
                }
            }
        }

        private void OnDisable()
        {
            if (mode == WebRtcServerMode.PeerToPeer)
            {
                connection.OnPolledMessageReceived -= OnMessageReceived;
                connection = null;
            }

            initialized = false;
        }

        private void Update()
        {
            const bool QuietLogsHoloportation = true; // DeviceConfig.Overrides.QuietLogsHoloportation;
            if (lastLogLevel != logLevel && !QuietLogsHoloportation)
            {
                Microsoft.MixedReality.WebRTC.PeerConnection.SetLogLevel(logLevel);
                lastLogLevel = logLevel;
            }

            // Clean up our connection tasks.
            for (int i = 0; i < connectionTasks.Count;)
            {
                if (connectionTasks[i].IsCompleted)
                {
                    // Log exceptions, but ignore cancellations. They're likely intentional.
                    if (connectionTasks[i].Exception != null)
                    {
                        Exception innerMostException = connectionTasks[i].Exception;

                        while (innerMostException is AggregateException ae)
                        {
                            innerMostException = ae.InnerException;
                        }

                        if (!(innerMostException is OperationCanceledException))
                        {
                            Debug.LogException(connectionTasks[i].Exception);
                        }
                    }

                    connectionTasks.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            // Poll if we are acting in a Peer to peer way
            if (mode == WebRtcServerMode.PeerToPeer)
            {
                // Only poll if we can respond to a message (have a prefab to spawn). Pause polling while handling a message.
                bool pollForRequests = (allowHostingContent && hostPrefab != null);

                connection.Update(poll: pollForRequests);
            }
        }

        /// <summary>
        /// Called when a remote client has requested this instance host content for it. Triggers an RTC connection between the two.
        /// </summary>
        private void OnMessageReceived(string msg)
        {
            // Note we flip them since our idea of remote and local is different from the client.
            string[] splitMsg = msg.Split('|');

            // Basic check to make sure the message is for us
            if ((splitMsg.Length == 2) && (splitMsg[1] == localId))
            {
                connectionTasks.Add(StartConnectionResponseAsync(splitMsg[1], splitMsg[0]));
            }
            else
            {
                Debug.LogError($"Received incorrect request message: {msg}");
            }
        }

        /// <summary>
        /// Adds a tracked host connection for management purposes.
        /// </summary>
        /// <param name="isHost">If true, this machine is the host of the data stream.</param>
        private void AddConnection(PeerConnection pc, bool isHost)
        {
            connections.Add(new ConnectionInfo()
            {
                Connection = pc,
                CreationTime = DateTime.Now,
                Host = isHost
            });

            pc.OnInitialized.AddListener(OnPeerConnectionInitialized);
            pc.OnPostShutdown.AddListener(OnPeerConnectionShutdown);
        }

        private void OnPeerConnectionInitialized(WebRTC.PeerConnection peer)
        {
            peer.OnInitialized.RemoveListener(OnPeerConnectionInitialized);

            peer.Peer.IceStateChanged += OnIceStateChanged;
            peer.Peer.Connected += OnPeerConnected;

            if (!allowHostingContent && mode == WebRtcServerMode.PeerToPeer)
            {
                // We must wait for the object to fully initialize, so it can register any tracks it
                // cares about before we send our message, which should cause the remote to send us an
                // offer. Tracks MUST be reistered before we get an offer.
                string msg = $"{localId}|{peer.RemotePeerId}";
                connection.SendMessageAsync($"requests/{peer.RemotePeerId}", System.Text.Encoding.UTF8.GetBytes(msg), "application/text");
                Debug.Log($"Connection requested from {peer.RemotePeerId}. Waiting for response. [{msg}]");
            }
            else
            {
                peer.CreateOffer();
            }
        }

        private void OnIceStateChanged(Microsoft.MixedReality.WebRTC.IceConnectionState newState)
        {
            Debug.Log($"ICE State: {newState}");
        }

        private async void OnPeerConnected()
        {
            switch (mode)
            {
                case WebRtcServerMode.ConsumeFromSfu:
                    await ResumeSfuConsumerAsync(sfuStreamConsumed);
                    break;

                case WebRtcServerMode.ProduceToSfu:
                    await ResumeSfuBroadcasterAsync(hostId);
                    break;

                default:
                    break;
            }
        }

        private void OnPeerConnectionShutdown(PeerConnection pc)
        {
            pc.OnPostShutdown.RemoveListener(OnPeerConnectionShutdown);

            ShutdownConnectionAsync();

            if (pc.Peer != null)
            {
                pc.Peer.IceStateChanged -= OnIceStateChanged;
                pc.Peer.Connected -= OnPeerConnected;
            }

            // Remove the connection info matching the closing peer connections.
            for (int i = 0; i < connections.Count; ++i)
            {
                if (connections[i].Connection == pc)
                {
                    connections.RemoveAt(i);
                    break;
                }
            }
        }

        List<float> _webrtcFrameRates = new List<float>();
        public List<float> WebRtcTrackFramerates
        {
            get
            {
                _webrtcFrameRates.Clear();
                for (int i = 0; i < connections.Count; ++i)
                {
                    if (connections[i].Connection.IsConnected)
                    {
                        connections[i].Connection.GetTrackFrameRates(_webrtcFrameRates);
                    }
                }
                return _webrtcFrameRates;
            }
        }

        private Task StartConnectionRequestAsync(string remoteId, GameObject gameObj)
        {
            PeerConnection pc = gameObj.GetComponent<PeerConnection>();
            if (pc == null || pc.AutoInitializeOnStart)
            {
                Destroy(gameObj);
                throw new Exception("Client prefab must have PeerConnection and be setup not to auto start.");
            }

            AddConnection(pc, false);

            // Initialize a PeerConnection with the connection specific ids.
            return pc.InitializeAsync(localId, remoteId);
        }

        private Task StartConnectionResponseAsync(string localConnectionId, string remoteConnectionId)
        {
            if (!allowHostingContent || hostPrefab == null)
            {
                throw new Exception("Ignoring a request message since there is no local handler");
            }

            GameObject go = Instantiate(hostPrefab, hostParent);
            PeerConnection pc = go.GetComponent<PeerConnection>();
            if (pc == null || pc.AutoInitializeOnStart)
            {
                Destroy(go);
                throw new Exception("Host prefab must have PeerConnection and be setup not to auto start.");
            }

            AddConnection(pc, true);

            // Initialize a PeerConnection with the connection specific ids.
            return pc.InitializeAsync(localConnectionId, remoteConnectionId);
        }

#if UNITY_EDITOR
        [CustomEditor(typeof(WebRtcServer))]
        public class WebRtcServerCustomEditor : UnityEditor.Editor
        {
            private const int statsUpdateRateMs = 1000;
            private const int statsAveragePeriod = 2;

            private string ipOverride = "";
            private string idOverride = "";

            private System.Diagnostics.Stopwatch statsUpdateTimer;

            private SerializedProperty serverAddress = null;
            private SerializedProperty stunServer = null;
            private SerializedProperty mode = null;
            private SerializedProperty pollIntervalMs = null;
            private SerializedProperty allowHostingContent = null;
            private SerializedProperty hostId = null;
            private SerializedProperty hostParent = null;
            private SerializedProperty hostPrefab = null;
            private SerializedProperty defaultProvider = null;
            private SerializedProperty logLevel = null;

            private int minBitrate = 100 * 1000;
            private int initialBitrate = -1;
            private int maxBitrate = 3000 * 1000;

            public void OnEnable()
            {
                ipOverride = PlayerPrefs.GetString(IpOverrideKey, "");
                idOverride = PlayerPrefs.GetString(IdOverrideKey, "");

                serverAddress = this.serializedObject.FindProperty("serverAddress");
                stunServer = this.serializedObject.FindProperty("stunServer");
                mode = this.serializedObject.FindProperty("mode");
                pollIntervalMs = this.serializedObject.FindProperty("pollIntervalMs");
                allowHostingContent = this.serializedObject.FindProperty("allowHostingContent");
                hostId = this.serializedObject.FindProperty("hostId");
                hostParent = this.serializedObject.FindProperty("hostParent");
                hostPrefab = this.serializedObject.FindProperty("hostPrefab");
                defaultProvider = this.serializedObject.FindProperty("defaultProvider");
                logLevel = this.serializedObject.FindProperty("logLevel");
            }

            public override void OnInspectorGUI()
            {
                WebRtcServer server = (WebRtcServer)target;

                EditorGUILayout.PropertyField(serverAddress);
                EditorGUILayout.PropertyField(stunServer);
                EditorGUILayout.PropertyField(mode);
                EditorGUILayout.PropertyField(pollIntervalMs);
                EditorGUILayout.PropertyField(allowHostingContent);
                EditorGUI.BeginDisabledGroup(allowHostingContent.boolValue == false);
                EditorGUILayout.PropertyField(hostId);
                EditorGUILayout.PropertyField(hostParent);
                EditorGUILayout.PropertyField(hostPrefab);
                EditorGUILayout.PropertyField(defaultProvider);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.PropertyField(logLevel);

                // Warn users to configure the server correctly when hosting is enabled.
                if (allowHostingContent.boolValue && hostPrefab.objectReferenceValue == null)
                {
                    EditorGUILayout.HelpBox("When hosting is enabled, a host prefab must be specified", MessageType.Warning);
                }

                // Clear out these values, so we don't have bogus data attached to the class.
                if (!allowHostingContent.boolValue)
                {
                    hostId.stringValue = "";
                    hostParent.objectReferenceValue = null;
                    hostPrefab.objectReferenceValue = null;
                }

                if (Application.isPlaying)
                {

                    EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                    GUILayout.Label("Bit Rate Controls", EditorStyles.boldLabel);
#if true
                    // Just set the Max. Target it immediately. Pick something paltry for min.
                    // Target is only used for a moment before it picks whatever value it wants.
                    // Max is only respected if it will result in a lower value.
                    maxBitrate = EditorGUILayout.IntSlider("Target kbps", maxBitrate / 1000, 100, 10000) * 1000;
                    initialBitrate = maxBitrate;
                    minBitrate = 10000;
#elif false
                    int minKbps = minBitrate / 1000;
                    int maxKbps = maxBitrate / 1000;
                    minKbps = EditorGUILayout.IntSlider("Min kbps", minKbps, 100, 10000);
                    maxKbps = EditorGUILayout.IntSlider("Max kbps", maxKbps, 100, 10000);
                    minBitrate = Math.Min(minKbps, maxKbps) * 1000;
                    maxBitrate = Math.Max(minKbps, maxKbps) * 1000;
                    initialBitrate = -1;
#else
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Min kbps", EditorStyles.boldLabel);
                    GUILayout.Label("Initial kbps", EditorStyles.boldLabel);
                    GUILayout.Label("max kbps", EditorStyles.boldLabel);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    minBitrate = EditorGUILayout.IntField(minBitrate);
                    initialBitrate = EditorGUILayout.IntField(initialBitrate);
                    maxBitrate = EditorGUILayout.IntField(maxBitrate);
                    EditorGUILayout.EndHorizontal();
#endif
                    if (GUILayout.Button("Set Bitrates On All Connections"))
                    {
                        for (int i = 0; i < server.connections.Count; ++i)
                        {
                            ConnectionInfo ci = server.connections[i];
                            if (ci.Connection.Peer != null)
                            {
                                ci.Connection.Peer.SetBitrate(
                                    minBitrate > 0 ? (uint?)minBitrate : null,
                                    initialBitrate > 0 ? (uint?)initialBitrate : null,
                                    maxBitrate > 0 ? (uint?)maxBitrate : null);
                            }
                        }
                    }

                    if (statsUpdateTimer == null)
                    {
                        statsUpdateTimer = System.Diagnostics.Stopwatch.StartNew();
                    }

                    if (statsUpdateTimer.Elapsed.TotalMilliseconds > statsUpdateRateMs)
                    {
                        DateTime now = DateTime.Now;
                        for (int i = 0; i < server.connections.Count; ++i)
                        {
                            ConnectionInfo ci = server.connections[i];

                            // Trigger a stats update. Due to timing, we may end up processing old data, but that's probably ok.
                            ci.Connection.StartGetStats();

                            // Skip this connection if nothing has updated.
                            if (ci.Connection.LatestStats.TimestampMs == ci.LastTimestampMs)
                            {
                                continue;
                            }

                            double deltaS = (ci.Connection.LatestStats.TimestampMs - ci.LastTimestampMs) / 1000.0;

                            if (ci.kbpsSent == null)
                            {
                                ci.kbpsSent = new MovingAverage(statsAveragePeriod);
                            }
                            int sentDelta = ci.Connection.LatestStats.BytesSent - ci.LastBytesSent;
                            ci.kbpsSent.AddSample((float)(sentDelta * 8 / deltaS / 1000.0f));

                            if (ci.kbpsReceived == null)
                            {
                                ci.kbpsReceived = new MovingAverage(1);
                            }
                            int receivedDelta = ci.Connection.LatestStats.BytesReceived - ci.LastBytesReceived;
                            ci.kbpsReceived.AddSample((float)(receivedDelta * 8 / deltaS / 1000.0f));

                            ci.LastTimestampMs = ci.Connection.LatestStats.TimestampMs;
                            ci.LastBytesSent = ci.Connection.LatestStats.BytesSent;
                            ci.LastBytesReceived = ci.Connection.LatestStats.BytesReceived;

                            server.connections[i] = ci;
                        }
                        statsUpdateTimer.Restart();
                    }

                    int activeConnections = server.connections.Sum(item => item.Connection.IsConnected ? 1 : 0);
                    int pendingConnections = server.connections.Count - activeConnections;

                    EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

                    if (server.DefaultProvider != null)
                    {
                        GUILayout.Label("Texture Provider Stats", EditorStyles.boldLabel);
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.BeginVertical(GUILayout.Width(100));
                        GUILayout.Label("Allocations:");
                        GUILayout.Label("Dropped Writes:");
                        GUILayout.Label("Dropped Reads:");
                        GUILayout.Label("Queue Size:");
                        GUILayout.Label("");
                        GUILayout.Label("Writes Per Second:");
                        GUILayout.Label("Reads Per Second Per Reader:");
                        EditorGUILayout.EndVertical();
                        for (int i = 0; i < server.DefaultProvider.TrackCount; ++i)
                        {
                            EditorGUILayout.BeginVertical(GUILayout.Width(50));
                            GUILayout.Label(server.DefaultProvider.GetBufferCount(i).ToString());
                            GUILayout.Label(server.DefaultProvider.GetDroppedWrites(i).ToString());
                            GUILayout.Label(server.DefaultProvider.GetDroppedReads(i).ToString());
                            GUILayout.Label(server.DefaultProvider.GetQueueSize(i).ToString());
                            GUILayout.Label("");
                            GUILayout.Label(server.DefaultProvider.GetWritesPerSecond(i).ToString("F2"));
                            GUILayout.Label((activeConnections == 0 ? 0 : server.DefaultProvider.GetReadsPerSecond(i) / activeConnections).ToString("F2"));
                            EditorGUILayout.EndVertical();
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                    GUILayout.Label($"Connection Tasks: {server.connectionTasks.Count}", EditorStyles.boldLabel);
                    GUILayout.Label($"Pending Connections: {pendingConnections}", EditorStyles.boldLabel);

                    GUILayout.Label($"Active Connections: {activeConnections}", EditorStyles.boldLabel);
                    for (int i = 0; i < server.connections.Count; ++i)
                    {
                        ConnectionInfo ci = server.connections[i];

                        if (!ci.Connection.IsConnected)
                        {
                            continue;
                        }

                        bool wasShowingDetails = ci.ShowDetails;
                        string status = ci.ShowDetails
                            ? $"{ci.Connection.RemotePeerId}"
                            : $"{ci.Connection.RemotePeerId} [{ci.Connection.State.ToString("G")}] [{(ci.Host ? ci.Connection.LatestStats.TransmitEncodeBitrate / 1000.0 : (ci.kbpsReceived == null ? 0 : ci.kbpsReceived.Average)):0.00} kbps]";

                        ci.ShowDetails = EditorGUILayout.Foldout(ci.ShowDetails, status);
                        if (wasShowingDetails != ci.ShowDetails)
                        {
                            server.connections[i] = ci;
                        }

                        if (ci.ShowDetails)
                        {
                            TimeSpan age = DateTime.Now - ci.CreationTime;
                            // GUILayout.Label($"Remote id: {ci.Connection.RemotePeerId}");
                            GUILayout.Label($"State: {ci.Connection.State.ToString("G")}");
                            GUILayout.Label($"Age: {age:hh\\:mm\\:ss}");
                            GUILayout.Label($"RTT: {ci.Connection.LatestStats.RoundTripTime}ms");
                            GUILayout.Label($"Audio Input Level: {ci.Connection.LatestStats.AudioInputLevel}");
                            GUILayout.Label($"Audio Output Level: {ci.Connection.LatestStats.AudioOutputLevel}");
                            GUILayout.Label($"Encode Bitrate (kbps): [target] {ci.Connection.LatestStats.TargetEncodeBitrate / 1000.0} [actual] {ci.Connection.LatestStats.ActualEncodeBitrate / 1000.0} [transmit] {ci.Connection.LatestStats.TransmitEncodeBitrate / 1000.0}");
                            if (ci.Host)
                            {
                                GUILayout.Label($"Send Bandwidth (kbps): {(ci.kbpsSent == null ? 0 : ci.kbpsSent.Average):0.00} / {ci.Connection.LatestStats.AvailableSendBandwidth / 1000.0f:0.00}");
                            }
                            else
                            {
                                GUILayout.Label($"Recv Bandwidth (kbps): {(ci.kbpsReceived == null ? 0 : ci.kbpsReceived.Average):0.00} / {ci.Connection.LatestStats.AvailableReceiveBandwidth / 1000.0f:0.00}");
                            }
                            GUILayout.Label("");
                            GUILayout.Label("Advanced Stats", EditorStyles.boldLabel);
                            if (GUILayout.Button("Update Advanced Stats"))
                            {
                                var task = ci.Connection.Peer.GetSimpleStatsAsync();
                                task.Wait();
                                if (task.Exception != null)
                                {
                                    Debug.LogException(task.Exception);
                                }
                                else
                                {
                                    if (ci.LastReport != null)
                                    {
                                        ci.LastReport.Dispose();
                                    }
                                    ci.LastReport = task.Result;
                                    server.connections[i] = ci;
                                }
                            }
                            if (ci.LastReport != null)
                            {

                                foreach (var s in ci.LastReport.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.TransportStats>())
                                {
                                    GUILayout.Label("Transport", EditorStyles.boldLabel);
                                    GUILayout.Label($"Bytes Sent: {s.BytesSent}");
                                    GUILayout.Label($"Bytes Received: {s.BytesReceived}");
                                }

                                if (ci.Host)
                                {
                                    foreach (var s in ci.LastReport.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.AudioSenderStats>())
                                    {
                                        GUILayout.Label($"Track: {s.TrackIdentifier}", EditorStyles.boldLabel);
                                        GUILayout.Label($"Sample Duration: {s.TotalSamplesDuration}");
                                        GUILayout.Label($"Audio Energy: {s.TotalAudioEnergy}");
                                        GUILayout.Label($"Packets Sent: {s.PacketsSent}");
                                        GUILayout.Label($"Bytes Sent: {s.BytesSent}");
                                    }

                                    foreach (var s in ci.LastReport.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.VideoSenderStats>())
                                    {
                                        GUILayout.Label($"Track: {s.TrackIdentifier}", EditorStyles.boldLabel);
                                        GUILayout.Label($"Frames Sent: {s.FramesSent}");
                                        GUILayout.Label($"Huge Frames Sent: {s.HugeFramesSent}");
                                        GUILayout.Label($"Frames Encoded: {s.FramesEncoded}");
                                        GUILayout.Label($"Packets Sent: {s.PacketsSent}");
                                        GUILayout.Label($"Bytes Sent: {s.BytesSent}");
                                    }
                                }
                                else
                                {
                                    foreach (var s in ci.LastReport.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.AudioReceiverStats>())
                                    {
                                        GUILayout.Label($"Track: {s.TrackIdentifier}", EditorStyles.boldLabel);
                                        GUILayout.Label($"Sample Duration: {s.TotalSamplesDuration}");
                                        GUILayout.Label($"Audio Energy: {s.TotalAudioEnergy}");
                                        GUILayout.Label($"Packets Received: {s.PacketsReceived}");
                                        GUILayout.Label($"Bytes Received: {s.BytesReceived}");
                                    }

                                    foreach (var s in ci.LastReport.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.VideoReceiverStats>())
                                    {
                                        GUILayout.Label($"Track: {s.TrackIdentifier}", EditorStyles.boldLabel);
                                        GUILayout.Label($"Frames Received: {s.FramesReceived}");
                                        GUILayout.Label($"Frames Dropped: {s.FramesDropped}");
                                        GUILayout.Label($"Frames Decoded: {s.FramesDecoded}");
                                        GUILayout.Label($"Packets Received: {s.PacketsReceived}");
                                        GUILayout.Label($"Bytes Received: {s.BytesReceived}");
                                    }
                                }
                                foreach (var s in ci.LastReport.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.DataChannelStats>())
                                {
                                    GUILayout.Label($"Track: {s.DataChannelIdentifier}", EditorStyles.boldLabel);
                                    if (ci.Host)
                                    {
                                        GUILayout.Label($"Messages Sent: {s.MessagesSent}");
                                        GUILayout.Label($"Bytes Sent: {s.BytesSent}");
                                    }
                                    else
                                    {
                                        GUILayout.Label($"Messages Received: {s.MessagesReceived}");
                                        GUILayout.Label($"Bytes Received: {s.BytesReceived}");
                                    }
                                }
                            }
                        }
                        GUILayout.Space(10);
                    }

                    Repaint();
                }
                else
                {
                    statsUpdateTimer = null;

                    EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                    GUILayout.Label("Local Overrides", EditorStyles.boldLabel);
                    ipOverride = EditorGUILayout.TextField("Local Server IP Override", ipOverride);
                    idOverride = EditorGUILayout.TextField("Local WebRTC ID Override", idOverride);
                    if (GUILayout.Button("Update Local Preferences"))
                    {
                        PlayerPrefs.SetString(IdOverrideKey, idOverride);
                        PlayerPrefs.SetString(IpOverrideKey, ipOverride);
                    }
                }

                serializedObject.ApplyModifiedProperties();
            }
        }
#endif
                }
}
