// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.MixedReality.WebRTC;

#if UNITY_EDITOR
using UnityEditor;
#endif

#if UNITY_WSA && !UNITY_EDITOR
using Windows.UI.Core;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Capture;
using Windows.ApplicationModel.Core;
#endif

namespace WebRTC
{
    /// <summary>
    /// Enumeration of the different types of ICE servers.
    /// </summary>
    public enum IceType
    {
        /// <summary>
        /// Indicates there is no ICE information
        /// </summary>
        /// <remarks>
        /// Under normal use, this should not be used
        /// </remarks>
        None = 0,

        /// <summary>
        /// Indicates ICE information is of type STUN
        /// </summary>
        /// <remarks>
        /// https://en.wikipedia.org/wiki/STUN
        /// </remarks>
        Stun,

        /// <summary>
        /// Indicates ICE information is of type TURN
        /// </summary>
        /// <remarks>
        /// https://en.wikipedia.org/wiki/Traversal_Using_Relays_around_NAT
        /// </remarks>
        Turn
    }

    [Serializable]
    public class WebRTCErrorEvent : UnityEvent<PeerConnection, string>
    {
    }

    [Serializable]
    public class PeerConnectionEvent : UnityEvent<PeerConnection>
    {
    }

    [Serializable]
    public class DataChannelEvent : UnityEvent<PeerConnection, DataChannel>
    {
    }

    /// <summary>
    /// High-level wrapper for Unity WebRTC functionalities.
    /// This is the API entry point for establishing a connection with a remote peer.
    /// </summary>
    [AddComponentMenu("MixedReality-WebRTC/Peer Connection")]
    public class PeerConnection : MonoBehaviour
    {

        [Header("Behavior settings")]

        [SerializeField]
        [Tooltip("Automatically initialize the peer connection on Start()")]
        private bool autoInitializeOnStart = true;

        [SerializeField]
        [Tooltip("Seconds waited before timing out new ICE connections.")]
        private int newIceConnectionTimeoutS = 30;

        [SerializeField]
        [Tooltip("Default output kilobitrate.")]
        [Range(100, 10000)]
        private uint defaultKiloBitRate = 3000;

        [Header("Signaling Config")]

        [SerializeField]
        private string localPeerId = "";

        [SerializeField]
        private string remotePeerId = "";

        public PeerConnectionEvent OnInitialized { get; private set; } = new PeerConnectionEvent();

        public PeerConnectionEvent OnPreShutdown { get; private set; } = new PeerConnectionEvent();

        public PeerConnectionEvent OnPostShutdown { get; private set; } = new PeerConnectionEvent();

        public DataChannelEvent OnDataChannelAdded { get; private set; } = new DataChannelEvent();

        public DataChannelEvent OnDataChannelRemoved { get; private set; } = new DataChannelEvent();

        public WebRTCErrorEvent OnError { get; private set; } = new WebRTCErrorEvent();

        #region Private variables

        private ConcurrentQueue<Action> _mainThreadWorkQueue = new ConcurrentQueue<Action>();

        private Microsoft.MixedReality.WebRTC.PeerConnection _nativePeer;

        private SignallingHandler Signaler;

        private bool shuttingDown = false;

        private DateTime timeOfLastStateChange = DateTime.Now;

        #endregion

        #region Public methods
        /// <summary>
        /// Retrieves the underlying peer connection object once initialized.
        /// </summary>
        /// <remarks>
        /// If <see cref="OnInitialized"/> has not fired, this will be <c>null</c>.
        /// </remarks>
        public Microsoft.MixedReality.WebRTC.PeerConnection Peer { get; private set; } = null;

        public IceConnectionState State { get; private set; } = IceConnectionState.New;

        public string RemotePeerId => remotePeerId;

        public bool AutoInitializeOnStart => autoInitializeOnStart;

        public Microsoft.MixedReality.WebRTC.PeerConnection.StatsData LatestStats { get; private set; } = new Microsoft.MixedReality.WebRTC.PeerConnection.StatsData();

        public void StartGetStats()
        {
            if (Peer != null)
            {
                Peer.StartGetStats();
            }
        }

        /// <summary>
        /// Enumerate the video capture devices available as a WebRTC local video feed source.
        /// </summary>
        /// <returns>The list of local video capture devices available to WebRTC.</returns>
        public static Task<List<VideoCaptureDevice>> GetVideoCaptureDevicesAsync()
        {
            return Microsoft.MixedReality.WebRTC.PeerConnection.GetVideoCaptureDevicesAsync();
        }

        public Task InitializeAsync(string localId, string remoteId)
        {
            localPeerId = localId;
            remotePeerId = remoteId;
            return InitializeAsync();
        }

        /// <summary>
        /// Initialize the underlying WebRTC libraries
        /// </summary>
        /// <remarks>
        /// This function is asynchronous, to monitor it's status bind a handler to OnInitialized and OnError
        /// </remarks>
        public Task InitializeAsync(CancellationToken token = default(CancellationToken))
        {
            // if the peer is already set, we refuse to initialize again.
            // Note: for multi-peer scenarios, use multiple WebRTC components.
            if (_nativePeer != null && _nativePeer.Initialized)
            {
                return Task.CompletedTask;
            }

#if UNITY_ANDROID
            AndroidJavaClass systemClass = new AndroidJavaClass("java.lang.System");
            string libname = "jingle_peerconnection_so";
            systemClass.CallStatic("loadLibrary", new object[1] { libname });
            Debug.Log("loadLibrary loaded : " + libname);

            /*
                * Below is equivalent of this java code:
                * PeerConnectionFactory.InitializationOptions.Builder builder = 
                *   PeerConnectionFactory.InitializationOptions.builder(UnityPlayer.currentActivity);
                * PeerConnectionFactory.InitializationOptions options = 
                *   builder.createInitializationOptions();
                * PeerConnectionFactory.initialize(options);
                */

            AndroidJavaClass playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            AndroidJavaObject activity = playerClass.GetStatic<AndroidJavaObject>("currentActivity");
            AndroidJavaClass webrtcClass = new AndroidJavaClass("org.webrtc.PeerConnectionFactory");
            AndroidJavaClass initOptionsClass = new AndroidJavaClass("org.webrtc.PeerConnectionFactory$InitializationOptions");
            AndroidJavaObject builder = initOptionsClass.CallStatic<AndroidJavaObject>("builder", new object[1] { activity });
            AndroidJavaObject options = builder.Call<AndroidJavaObject>("createInitializationOptions");

            if (webrtcClass != null)
            {
                webrtcClass.CallStatic("initialize", new object[1] { options });
            }
#endif

#if UNITY_WSA && !UNITY_EDITOR
            if (UnityEngine.WSA.Application.RunningOnUIThread())
#endif
            {
                return RequestAccessAndInitAsync(token);
            }
#if UNITY_WSA && !UNITY_EDITOR
            else
            {
                UnityEngine.WSA.Application.InvokeOnUIThread(() => RequestAccessAndInitAsync(token), waitUntilDone: true);
                return Task.CompletedTask;
            }
#endif
        }

        public bool IsConnected => State == IceConnectionState.Connected || State == IceConnectionState.Completed;

        /// <summary>
        /// Uninitialize the underlying WebRTC library, effectively cleaning up the allocated peer connection.
        /// </summary>
        /// <remarks>
        /// <see cref="Peer"/> will be <c>null</c> afterward.
        /// </remarks>
        public void Uninitialize()
        {
            bool needsShutdown = (_nativePeer != null && _nativePeer.Initialized);
            if (needsShutdown)
            {
                shuttingDown = true;

                // Fire signals before doing anything else to allow listeners to clean-up,
                // including un-registering any callback and remove any track from the connection.
                OnPreShutdown.Invoke(this);

                _nativePeer.DataChannelAdded -= Peer_DataChannelAdded;
                _nativePeer.DataChannelRemoved -= Peer_DataChannelRemoved;
                _nativePeer.IceStateChanged -= Peer_IceStateChanged;
                _nativePeer.RenegotiationNeeded -= Peer_RenegotiationNeeded;
                _nativePeer.StatsUpdated -= Peer_OnStatsUpdated;

                if (Signaler != null)
                {
                    _nativePeer.IceCandidateReadytoSend -= Signaler.SendIceCandidateMessage;
                    _nativePeer.LocalSdpReadytoSend -= Signaler.SendSdpMessage;
                    Signaler = null;
                }

                // Prevent publicly accessing the native peer after it has been deinitialized.
                // This does not prevent systems caching a reference from accessing it, but it
                // is their responsibility to check that the peer is initialized.
                Peer = null;
            }

            // Close the device even if we haven't finished initializing.
            if (_nativePeer != null)
            {
                // Close the connection and release native resources.
                Debug.Log("Closing native PeerConnection.");
                _nativePeer.Dispose();
                _nativePeer = null;
            }

            if (needsShutdown)
            {
                OnPostShutdown.Invoke(this);
            }
        }

        #endregion


        #region Unity MonoBehaviour methods

        /// <summary>
        /// Unity Engine Start() hook
        /// </summary>
        /// <remarks>
        /// See <see href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.Start.html"/>
        /// </remarks>
        private void OnEnable()
        {
            OnError.AddListener(OnError_Listener);

            // List video capture devices to Unity console
            GetVideoCaptureDevicesAsync().ContinueWith((prevTask) => {
                var devices = prevTask.Result;
                _mainThreadWorkQueue.Enqueue(() => {
                    foreach (var device in devices)
                    {
                        Debug.Log($"Found video capture device '{device.name}' (id:{device.id}).");
                    }
                });
            });

            if (autoInitializeOnStart)
            {
                InitializeAsync();
            }
        }

        private void Update()
        {
            // Execute any pending work enqueued by background tasks
            while (_mainThreadWorkQueue.TryDequeue(out Action workload))
            {
                workload();
            }

            if (Signaler != null)
            {
                Signaler.Update();
            }

            // Handle ICE state
            if (_nativePeer != null && _nativePeer.Initialized)
            {
                bool badDisconnect = false;
                string shutdownMsg = "";
                switch (State)
                {
                    case IceConnectionState.Failed:
                        shutdownMsg = "Failed to resolve ICE connection. Shutting down PeerConnection";
                        badDisconnect = true;
                        break;
                    case IceConnectionState.Closed:
                        shutdownMsg = "ICE connection closed. Shutting down PeerConnection";
                        break;
                    case IceConnectionState.Disconnected:
                        shutdownMsg = "ICE disconnected. Shutting down PeerConnection";
                        break;
                    case IceConnectionState.New:
                        if ((DateTime.Now - timeOfLastStateChange).TotalSeconds > newIceConnectionTimeoutS)
                        {
                            shutdownMsg = $"ICE connection timeout. More than {newIceConnectionTimeoutS} seconds have passed.";
                        }
                        break;
                }

                if (!string.IsNullOrWhiteSpace(shutdownMsg))
                {
                    if (badDisconnect)
                    {
                        Debug.LogWarning(shutdownMsg);
                    }
                    else
                    {
                        Debug.Log(shutdownMsg);
                    }

                    Uninitialize();
                }
            }
        }

        class AverageTransport
        {
            public long LastTimeStampUs;
            public ulong LastBytesSent;
            public ulong LastBytesReceived;
            public MovingAverage BytesSentPS = new MovingAverage(10);
            public MovingAverage BytesReceivedPS = new MovingAverage(10);

            public void AddSample(Microsoft.MixedReality.WebRTC.PeerConnection.TransportStats s)
            {
                long freq = 1000000;
                long dtt = LastTimeStampUs != 0
                    ? s.TimestampUs - LastTimeStampUs
                    : freq;
                double dt = dtt / (double)freq;
                LastTimeStampUs = s.TimestampUs;

                BytesSentPS.AddSample((float)((s.BytesSent - LastBytesSent) / dt));
                BytesReceivedPS.AddSample((float)((s.BytesReceived - LastBytesReceived) / dt));

                LastBytesSent = s.BytesSent;
                LastBytesReceived = s.BytesReceived;
            }

            public void ToString(StringBuilder b)
            {
                b.AppendLine("Transport");
                b.AppendLine($"    Bytes Received:\t\t{LastBytesReceived}\t\t({BytesReceivedPS.Average * 8.0f / 1000.0f:0.00} kbps)\t\t({BytesReceivedPS.LastSample * 8.0f / 1000.0f:0.00} kbps)");
                b.AppendLine($"    Bytes Sent:\t\t{LastBytesSent}\t\t({BytesSentPS.Average * 8.0f / 1000.0f:0.00} kbps)\t\t({BytesSentPS.LastSample * 8.0f / 1000.0f:0.00} kbps)");
                b.AppendLine("");
            }
        }

        class AverageVideoTrack
        {
            public string Name;
            public long TrackTimeStampUs;
            public ulong LastFramesReceived;
            public ulong LastFramesDropped;
            public MovingAverage FramesReceivedPS = new MovingAverage(10);
            public MovingAverage FramesDroppedPS = new MovingAverage(10);
            public long RtpTimeStampUs;
            public ulong LastFramesDecoded;
            public ulong LastPacketsReceived;
            public ulong LastBytesReceived;
            public MovingAverage FramesDecodedPS = new MovingAverage(10);
            public MovingAverage PacketsReceivedPS = new MovingAverage(10);
            public MovingAverage BytesReceivedPS = new MovingAverage(10);

            public void AddSample(Microsoft.MixedReality.WebRTC.PeerConnection.VideoReceiverStats s)
            {
                Name = s.TrackIdentifier;

                // Track Stats
                long freq = 1000000;
                long dttT = TrackTimeStampUs != 0
                    ? s.TrackStatsTimestampUs - TrackTimeStampUs
                    : freq;
                double dtT = dttT / (double)freq;
                TrackTimeStampUs = s.TrackStatsTimestampUs;

                FramesReceivedPS.AddSample((float)((s.FramesReceived - LastFramesReceived) / dtT));
                LastFramesReceived = s.FramesReceived;

                // Frames can... undrop?
                if (s.FramesDropped >= LastFramesDropped)
                {
                    FramesDroppedPS.AddSample((float)((s.FramesDropped - LastFramesDropped) / dtT));
                    LastFramesDropped = s.FramesDropped;
                }

                // RTP Stats
                long dttR = RtpTimeStampUs != 0
                    ? s.RtpStatsTimestampUs - RtpTimeStampUs
                    : freq;
                double dtR = dttR / (double)freq;
                RtpTimeStampUs = s.RtpStatsTimestampUs;

                FramesDecodedPS.AddSample((float)((s.FramesDecoded - LastFramesDecoded) / dtR));
                PacketsReceivedPS.AddSample((float)((s.PacketsReceived - LastPacketsReceived) / dtR));
                BytesReceivedPS.AddSample((float)((s.BytesReceived - LastBytesReceived) / dtR));

                LastFramesDecoded = s.FramesDecoded;
                LastPacketsReceived = s.PacketsReceived;
                LastBytesReceived = s.BytesReceived;
            }

            public void ToString(StringBuilder b)
            {
                b.AppendLine($"Track: {Name}");
                b.AppendLine($"    Frames Received:\t\t{LastFramesReceived}\t\t({FramesReceivedPS.Average:0.00} fps)\t\t({FramesReceivedPS.LastSample:0.00} fps)");
                b.AppendLine($"    Frames Dropped:\t\t{LastFramesDropped}\t\t({FramesDroppedPS.Average:0.00} fps)  \t\t({FramesDroppedPS.LastSample:0.00} fps)");
                b.AppendLine($"    Frames Decoded:\t\t{LastFramesDecoded}\t\t({FramesDecodedPS.Average:0.00} fps)\t\t({FramesDecodedPS.LastSample:0.00} fps)");
                b.AppendLine($"    Packets Received:\t\t{LastPacketsReceived}\t\t({PacketsReceivedPS.Average:0.00} pps)\t\t({PacketsReceivedPS.LastSample:0.00} pps)");
                b.AppendLine($"    Bytes Received:\t\t{LastBytesReceived}\t\t({BytesReceivedPS.Average * 8.0f/ 1000.0f:0.00} kbps)\t\t({(BytesReceivedPS.LastSample) * 8.0f / 1000.0f:0.00} kbps)");
                b.AppendLine("");
            }
        }

        public void GetTrackFrameRates(List<float> webrtcFrameRates)
        {
            if (IsConnected && videoTracks != null)
            {
                for (int i = 0; i < videoTracks.Count; ++i)
                {
                    if (videoTracks[i].LastFramesDecoded > 0)
                    {
                        // Each sample is about a second of data.
                        webrtcFrameRates.Add(videoTracks[i].FramesDecodedPS.LastSample);
                        webrtcFrameRates.Add(videoTracks[i].FramesReceivedPS.LastSample);
                    }
                }
            }
        }

        List<AverageTransport> transports = new List<AverageTransport>();
        List<AverageVideoTrack> videoTracks = new List<AverageVideoTrack>();

        MovingAverage localFrameRate;

        StringBuilder aggressiveStats = new StringBuilder();
        WaitForSeconds statsWaiter = new WaitForSeconds(1);
        private System.Collections.IEnumerator HandleAggressiveLogging()
        {
            while (Peer != null)
            {
                if (!_nativePeer.IsConnected)
                {
                    yield return false;
                    continue;
                }

                var task = Peer.GetSimpleStatsAsync();
                while (!task.IsCompleted)
                {
                    yield return false;
                }

                if (task.Exception != null)
                {
                    Debug.LogException(task.Exception);
                }
                else
                {
                    Microsoft.MixedReality.WebRTC.PeerConnection.StatsReport report = task.Result;

                    int i = 0;
                    foreach (var s in report.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.TransportStats>())
                    {
                        if (i >= transports.Count)
                        {
                            transports.Add(new AverageTransport());
                        }
                        transports[i].AddSample(s);
                        i++;
                    }

                    i = 0;
                    foreach (var s in report.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.VideoReceiverStats>())
                    {
                        if (i >= videoTracks.Count)
                        {
                            videoTracks.Add(new AverageVideoTrack());
                        }
                        videoTracks[i].AddSample(s);
                        i++;
                    }

                    const bool AggressiveStatsForWebRTC = false;
                    if (AggressiveStatsForWebRTC)
                    {
                        aggressiveStats.Clear();
                        foreach (var t in transports)
                        {
                            t.ToString(aggressiveStats);
                        }
                        foreach (var t in videoTracks)
                        {
                            t.ToString(aggressiveStats);
                        }
                        Debug.Log(aggressiveStats.ToString());
                    }

                    report.Dispose();
                }

                yield return statsWaiter;
            }
        }

        private void OnDisable()
        {
            Uninitialize();
            OnError.RemoveListener(OnError_Listener);
        }

        private void OnDestroy()
        {
            Uninitialize();
        }

        #endregion


        #region Private implementation

        /// <summary>
        /// Internal helper to ensure device access and continue initialization.
        /// </summary>
        /// <remarks>
        /// On UWP this must be called from the main UI thread.
        /// </remarks>
        private Task RequestAccessAndInitAsync(CancellationToken token)
        {
#if UNITY_WSA && !UNITY_EDITOR
            // On UWP the app must have the "webcam" capability, and the user must allow webcam
            // access. So check that access before trying to initialize the WebRTC library, as this
            // may result in a popup window being displayed the first time, which needs to be accepted
            // before the camera can be accessed by WebRTC.
            var mediaAccessRequester = new MediaCapture();
            var mediaSettings = new MediaCaptureInitializationSettings();
            mediaSettings.AudioDeviceId = "";
            mediaSettings.VideoDeviceId = "";
            mediaSettings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;
            mediaSettings.PhotoCaptureSource = PhotoCaptureSource.VideoPreview;
            mediaSettings.SharingMode = MediaCaptureSharingMode.SharedReadOnly; // for MRC and lower res camera
            var accessTask = mediaAccessRequester.InitializeAsync(mediaSettings).AsTask(token);
            return accessTask.ContinueWith(prevTask =>
            {
                token.ThrowIfCancellationRequested();

                if (prevTask.Exception == null)
                {
                    InitializePluginAsync(token);
                }
                else
                {
                    _mainThreadWorkQueue.Enqueue(() =>
                    {
                        OnError.Invoke(this, $"Audio/Video access failure: {prevTask.Exception.Message}.");
                    });
                }
            }, token);
#else
            return InitializePluginAsync(token);
#endif
        }

        /// <summary>
        /// Internal handler to actually initialize the 
        /// </summary>
        private Task InitializePluginAsync(CancellationToken token)
        {
            shuttingDown = false;

            Debug.Log("Initializing WebRTC plugin...");
            var config = WebRtcServer.Instance.PeerConfig;
            _nativePeer = new Microsoft.MixedReality.WebRTC.PeerConnection();
            return _nativePeer.InitializeAsync(config, token).ContinueWith((initTask) => {
                token.ThrowIfCancellationRequested();

                if (initTask.Exception != null)
                {
                    _mainThreadWorkQueue.Enqueue(() => {
                        var errorMessage = new StringBuilder();
                        errorMessage.Append("WebRTC plugin initializing failed. See full log for exception details.\n");
                        Exception ex = initTask.Exception;
                        while (ex is AggregateException ae)
                        {
                            errorMessage.Append($"AggregationException: {ae.Message}\n");
                            ex = ae.InnerException;
                        }
                        errorMessage.Append($"Exception: {ex.Message}");
                        OnError.Invoke(this, errorMessage.ToString());
                    });
                    throw initTask.Exception;
                }

                _mainThreadWorkQueue.Enqueue(OnPostInitialize);
            }, token);
        }

        /// <summary>
        /// Callback fired on the main UI thread once the WebRTC plugin was initialized successfully.
        /// </summary>
        private void OnPostInitialize()
        {
            Debug.Log("WebRTC plugin initialized successfully.");

            _nativePeer.StatsUpdated += Peer_OnStatsUpdated;
            _nativePeer.RenegotiationNeeded += Peer_RenegotiationNeeded;
            _nativePeer.IceStateChanged += Peer_IceStateChanged;
            _nativePeer.DataChannelAdded += Peer_DataChannelAdded;
            _nativePeer.DataChannelRemoved += Peer_DataChannelRemoved;

            // Once the peer is initialized, it becomes publicly accessible.
            // This prevent scripts from accessing it before it is initialized,
            // or worse before it is constructed in Awake(). This happens because
            // some scripts try to access Peer in OnEnabled(), which won't work
            // if Unity decided to initialize that script before the current one.
            // However subsequent calls will (and should) work as expected.
            Peer = _nativePeer;

            Signaler = new SignallingHandler(this, WebRtcServer.Instance.ServerAddress, localPeerId, remotePeerId);
            _nativePeer.LocalSdpReadytoSend += Signaler.SendSdpMessage;
            _nativePeer.IceCandidateReadytoSend += Signaler.SendIceCandidateMessage;

            _nativePeer.SetBitrate(100000, defaultKiloBitRate * 1000, defaultKiloBitRate * 1000);

            OnInitialized.Invoke(this);

            const bool QuietLogsHoloportation = true; // DeviceConfig.Overrides.QuietLogsHoloportation
            if (!QuietLogsHoloportation)
            {
                StartCoroutine(HandleAggressiveLogging());
            }
        }

        private void Peer_DataChannelAdded(DataChannel dc)
        {
            OnDataChannelAdded?.Invoke(this, dc);
        }

        private void Peer_DataChannelRemoved(DataChannel dc)
        {
            OnDataChannelRemoved?.Invoke(this, dc);
        }

        private void Peer_IceStateChanged(IceConnectionState newState)
        {
            timeOfLastStateChange = DateTime.Now;
            State = newState;
        }

        public bool OfferCreated { get; private set; } = false;
        public void CreateOffer()
        {
            Debug.Log("Creating Offer");
            OfferCreated = true;
            _nativePeer.CreateOffer();
        }

        private void Peer_RenegotiationNeeded()
        {
            // If already connected, update the connection on the fly.
            // If not, wait for user action and don't automatically connect.
            if (_nativePeer.IsConnected && !shuttingDown)
            {
                Debug.Log("Configuration Changed. Renogiating Offer.");
                CreateOffer();
            }
        }

        private void Peer_OnStatsUpdated(in Microsoft.MixedReality.WebRTC.PeerConnection.StatsData obj)
        {
            LatestStats = obj;
        }

        /// <summary>
        /// Internal handler for on-error.
        /// </summary>
        /// <param name="error">The error message</param>
        private void OnError_Listener(PeerConnection pc, string error)
        {
            Debug.LogError(error);
        }

        #endregion

#if UNITY_EDITOR
        [CustomEditor(typeof(PeerConnection))]
        public class PeerConnectionCustomEditor : UnityEditor.Editor
        {
            public override void OnInspectorGUI()
            {
                PeerConnection peer = (PeerConnection)target;

                DrawDefaultInspector();

                if (Application.isPlaying)
                {
                    if (GUILayout.Button("Force Bitrate To 1000kbps"))
                    {
                        peer._nativePeer.SetBitrate(1000000, 1000000, 1000000);
                    }
                }
            }
        }
#endif
    }
}
