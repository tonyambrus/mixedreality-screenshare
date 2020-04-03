// #define AVERAGE_CHROMA

using System.Collections;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Rendering;
using Unity.Collections;
using Microsoft.MixedReality.WebRTC;
#if UNITY_EDITOR
using UnityEditor;
#endif
namespace WebRTC
{
    public enum SdpVideoCodec
    {
        None,
        H264,
        H265,
        VP8,
        VP9,
        Custom
    }

    /// <summary>
    /// This component represents a local video source added as a video track to an
    /// existing WebRTC peer connection and sent to the remote peer. The video track
    /// can optionally be displayed locally with a <see cref="MediaPlayer"/>.
    /// </summary>
    [AddComponentMenu("MixedReality-WebRTC/Native Array Video Stream Source")]
    public class YuvProviderVideoStreamSource : VideoStreamSource
    {
        [SerializeField]
        [Tooltip("Preferred video codec to use if supported")]
        private SdpVideoCodec preferredVideoCodec = SdpVideoCodec.None;

        [Header("Video track")]
        [SerializeField]
        [Tooltip("Peer connection this local video source will add a video track to.")]
        private PeerConnection peerConnection = null;

        [SerializeField]
        [Tooltip("Track names. Add more if more are needed. Must match the Default Provider.")]
        private string[] trackNames = null;

        [SerializeField]
        private bool StartAutomatically = true;

        [SerializeField]
        [Tooltip("Fills in the local frame queue, allowing visualization using a MediaPlayer. Costs performance by triggering extra copies.")]
        private bool supportLocalPlayer = false;

        private YuvProvider videoProvider = null;

        private bool tracksAdded = false;

        private long lastFrameTimestamp = 0;

        private List<ExternalVideoTrackSource> externalTrackSources = new List<ExternalVideoTrackSource>();

        private List<LocalVideoTrack> tracks = new List<LocalVideoTrack>();

        protected void Awake()
        {
            peerConnection.OnInitialized.AddListener(OnPeerInitialized);
            peerConnection.OnPreShutdown.AddListener(OnPeerShutdown);
        }

        protected void OnEnable()
        {
            if (supportLocalPlayer && FrameQueue == null)
            {
                FrameQueue = new VideoFrameQueue<I420AVideoFrameStorage>(3);
            }

            videoProvider = WebRtcServer.Instance.DefaultProvider;
            if (videoProvider == null)
            {
                throw new InvalidOperationException("Failed to get a default provider from the webrtc connection.");
            }

            var nativePeer = peerConnection?.Peer;
            if ((nativePeer != null) && nativePeer.Initialized)
            {
                HandleAutoStartIfNeeded(nativePeer);
            }

            lastFrameTimestamp = 0;
        }

        public void AddTrackToPeer()
        {
            var nativePeer = peerConnection?.Peer;
            if (!tracksAdded && nativePeer != null && nativePeer.Initialized)
            {
                AddLocalVideoTrackImpl(nativePeer);
                tracksAdded = true;
            }
        }

        protected void OnDisable()
        {
            OnPeerShutdown(peerConnection);
        }

        protected void OnDestroy()
        {
            peerConnection.OnInitialized.RemoveListener(OnPeerInitialized);
            peerConnection.OnPreShutdown.RemoveListener(OnPeerShutdown);
        }

        private void OnPeerInitialized(PeerConnection pc)
        {
            var nativePeer = peerConnection.Peer;
            nativePeer.PreferredVideoCodec = Enum.GetName(typeof(SdpVideoCodec), preferredVideoCodec);

            // Only perform auto-start actions (add track, start capture) if the component
            // is enabled. Otherwise just do nothing, this component is idle.
            if (enabled)
            {
                HandleAutoStartIfNeeded(nativePeer);
            }
        }

        private void OnPeerShutdown(PeerConnection pc)
        {
            if (tracksAdded)
            {
                VideoStreamStopped.Invoke();
            }

            var nativePeer = peerConnection.Peer;
            if (nativePeer != null && nativePeer.Initialized)
            {
                for (int i = 0; i < tracks.Count; ++i)
                {
                    nativePeer.RemoveLocalVideoTrack(tracks[i]);
                    tracks[i].Dispose();
                }
                tracks.Clear();
            }

            for (int i = 0; i < externalTrackSources.Count; ++i)
            {
                externalTrackSources[i].Dispose();
            }
            externalTrackSources.Clear();

            if (supportLocalPlayer && FrameQueue != null)
            {
                FrameQueue.Clear();
            }
        }

        private void HandleAutoStartIfNeeded(Microsoft.MixedReality.WebRTC.PeerConnection nativePeer)
        {
            if (StartAutomatically)
            {
                AddLocalVideoTrackImpl(nativePeer);
            }
        }

        private void AddLocalVideoTrackImpl(Microsoft.MixedReality.WebRTC.PeerConnection nativePeer)
        {
            // Force again PreferredVideoCodec right before starting the local capture,
            // so that modifications to the property done after OnPeerInitialized() are
            // accounted for.
            nativePeer.PreferredVideoCodec = Enum.GetName(typeof(SdpVideoCodec), preferredVideoCodec);

            if (trackNames.Length == 0)
            {
                throw new InvalidOperationException("No tracks have been specified. Cannot start");
            }

            for (int i = 0; i < trackNames.Length; ++i)
            {
                // Ensure the track has a valid name
                string trackName = trackNames[i];
                if (trackName.Length == 0)
                {
                    trackName = Guid.NewGuid().ToString();
                    trackNames[i] = trackName;
                }
                SdpTokenAttribute.Validate(trackName, allowEmpty: false);

                externalTrackSources.Add(ExternalVideoTrackSource.CreateFromI420ACallback(I420AVideoFrameRequest));
                tracks.Add(nativePeer.AddCustomLocalVideoTrack(trackName, externalTrackSources[i]));
            }

            if (supportLocalPlayer && FrameQueue != null)
            {
                FrameQueue.Clear();
            }

            VideoStreamStarted.Invoke();
        }
        
        private unsafe void I420AVideoFrameRequest(in FrameRequest request)
        {
            int trackIndex = -1;
            for (int i = 0; i < externalTrackSources.Count; ++i)
            {
                if (externalTrackSources[i] == request.Source)
                {
                    trackIndex = i;
                    break;
                }
            }

            if (trackIndex == -1)
            {
                throw new InvalidOperationException("Unknown track source.");
            }

            videoProvider.I420AVideoFrameRequest(ref lastFrameTimestamp, in request, trackIndex, FrameQueue);
        }

#if UNITY_EDITOR
        [CustomEditor(typeof(YuvProviderVideoStreamSource))]
        public class NativeTextureVideoStreamSourceCustomEditor : UnityEditor.Editor
        {
            public override void OnInspectorGUI()
            {
                YuvProviderVideoStreamSource source = (YuvProviderVideoStreamSource)target;

                DrawDefaultInspector();

                EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                if (GUILayout.Button("Add Track"))
                {
                    source.AddTrackToPeer();
                }
            }
        }
#endif
    }
}
