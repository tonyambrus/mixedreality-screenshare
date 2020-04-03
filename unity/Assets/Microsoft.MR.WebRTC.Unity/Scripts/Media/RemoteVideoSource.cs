// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.MixedReality.WebRTC;
using System;
using System.Collections.Concurrent;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace WebRTC
{
    /// <summary>
    /// This component represents a remote video source added as a video track to an
    /// existing WebRTC peer connection by a remote peer and received locally.
    /// The video track can optionally be displayed locally with a <see cref="MediaPlayer"/>.
    /// </summary>
    [AddComponentMenu("MixedReality-WebRTC/Remote Video Source")]
    public class RemoteVideoSource : VideoStreamSource
    {
        [SerializeField]
        private Renderer videoRenderer = null;

        /// <summary>
        /// Peer connection this remote video source is extracted from.
        /// </summary>
        [Header("Video track")]
        public PeerConnection PeerConnection;

        /// <summary>
        /// Automatically play the remote video track when it is added.
        /// This is equivalent to manually calling <see cref="Play"/> when the peer connection
        /// is initialized.
        /// </summary>
        /// <seealso cref="Play"/>
        /// <seealso cref="Stop()"/>
        public bool AutoPlayOnAdded = true;

        /// <summary>
        /// Is the video source currently playing?
        /// The concept of _playing_ is described in the <see cref="Play"/> function.
        /// </summary>
        /// <seealso cref="Play"/>
        /// <seealso cref="Stop()"/>
        public bool IsPlaying { get; private set; }

        public Renderer VideoRenderer => videoRenderer;

        /// <summary>
        /// Internal queue used to marshal work back to the main Unity thread.
        /// </summary>
        private ConcurrentQueue<Action> _mainThreadWorkQueue = new ConcurrentQueue<Action>();

        /// <summary>
        /// Manually start playback of the remote video feed by registering some listeners
        /// to the peer connection and starting to enqueue video frames as they become ready.
        /// 
        /// Because the WebRTC implementation uses a push model, calling <see cref="Play"/> does
        /// not necessarily start producing frames immediately. Instead, this starts listening for
        /// incoming frames from the remote peer. When a track is actually added by the remote peer
        /// and received locally, the <see cref="VideoStreamSource.VideoStreamStarted"/> event is fired, and soon
        /// after frames will start being available for rendering in the internal frame queue. Note that
        /// this event may be fired before <see cref="Play"/> is called, in which case frames are
        /// produced immediately.
        /// 
        /// If <see cref="AutoPlayOnAdded"/> is <c>true</c> then this is called automatically
        /// as soon as the peer connection is initialized.
        /// </summary>
        /// <remarks>
        /// This is only valid while the peer connection is initialized, that is after the
        /// <see cref="PeerConnection.OnInitialized"/> event was fired.
        /// </remarks>
        /// <seealso cref="Stop()"/>
        /// <seealso cref="IsPlaying"/>
        public void Play()
        {
            if (!IsPlaying)
            {
                IsPlaying = true;
                PeerConnection.Peer.I420ARemoteVideoFrameReady += I420ARemoteVideoFrameReady;
            }
        }

        /// <summary>
        /// Stop playback of the remote video feed and unregister the handler listening to remote
        /// video frames.
        /// 
        /// Note that this is independent of whether or not a remote track is actually present.
        /// In particular this does not fire the <see cref="VideoStreamSource.VideoStreamStopped"/>, which corresponds
        /// to a track being made available to the local peer by the remote peer.
        /// </summary>
        /// <seealso cref="Play()"/>
        /// <seealso cref="IsPlaying"/>
        public void Stop()
        {
            if (IsPlaying)
            {
                IsPlaying = false;
                PeerConnection.Peer.I420ARemoteVideoFrameReady -= I420ARemoteVideoFrameReady;
            }
        }

        /// <summary>
        /// Implementation of <a href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.Awake.html">MonoBehaviour.Awake</a>
        /// which registers some handlers with the peer connection to listen to its <see cref="PeerConnection.OnInitialized"/>
        /// and <see cref="PeerConnection.OnPreShutdown"/> events.
        /// </summary>
        protected void Awake()
        {
            FrameQueue = new VideoFrameQueue<I420AVideoFrameStorage>(5);
            PeerConnection.OnInitialized.AddListener(OnPeerInitialized);
            PeerConnection.OnPreShutdown.AddListener(OnPeerShutdown);
        }

        /// <summary>
        /// Implementation of <a href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnDestroy.html">MonoBehaviour.OnDestroy</a>
        /// which unregisters all listeners from the peer connection.
        /// </summary>
        protected void OnDestroy()
        {
            PeerConnection.OnInitialized.RemoveListener(OnPeerInitialized);
            PeerConnection.OnPreShutdown.RemoveListener(OnPeerShutdown);
        }

        /// <summary>
        /// Implementation of <a href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.Update.html">MonoBehaviour.Update</a>
        /// to execute from the current Unity main thread any background work enqueued from free-threaded callbacks.
        /// </summary>
        protected void Update()
        {
            // Execute any pending work enqueued by background tasks
            while (_mainThreadWorkQueue.TryDequeue(out Action workload))
            {
                workload();
            }
        }

        /// <summary>
        /// Internal helper callback fired when the peer is initialized, which starts listening for events
        /// on remote tracks added and removed, and optionally starts video playback if the
        /// <see cref="AutoPlayOnAdded"/> property is <c>true</c>.
        /// </summary>
        private void OnPeerInitialized(PeerConnection pc)
        {
            PeerConnection.Peer.TrackAdded += TrackAdded;
            PeerConnection.Peer.TrackRemoved += TrackRemoved;

            if (AutoPlayOnAdded)
            {
                Play();
            }
        }

        /// <summary>
        /// Internal helper callback fired when the peer is shut down, which stops video playback and
        /// unregister all the event listeners from the peer connection about to be destroyed.
        /// </summary>
        private void OnPeerShutdown(PeerConnection pc)
        {
            Stop();
            if (IsPlaying)
            {
                PeerConnection.Peer.TrackAdded -= TrackAdded;
                PeerConnection.Peer.TrackRemoved -= TrackRemoved;
            }
        }

        /// <summary>
        /// Internal free-threaded helper callback on track added, which enqueues the
        /// <see cref="VideoStreamSource.VideoStreamStarted"/> event to be fired from the main
        /// Unity thread.
        /// </summary>
        private void TrackAdded(Microsoft.MixedReality.WebRTC.PeerConnection.TrackKind trackKind)
        {
            if (trackKind == Microsoft.MixedReality.WebRTC.PeerConnection.TrackKind.Video)
            {
                // Enqueue invoking the unity event from the main Unity thread, so that listeners
                // can directly access Unity objects from their handler function.
                _mainThreadWorkQueue.Enqueue(() => VideoStreamStarted.Invoke());
            }
        }

        /// <summary>
        /// Internal free-threaded helper callback on track added, which enqueues the
        /// <see cref="VideoStreamSource.VideoStreamStopped"/> event to be fired from the main
        /// Unity thread.
        /// </summary>
        private void TrackRemoved(Microsoft.MixedReality.WebRTC.PeerConnection.TrackKind trackKind)
        {
            if (trackKind == Microsoft.MixedReality.WebRTC.PeerConnection.TrackKind.Video)
            {
                // Enqueue invoking the unity event from the main Unity thread, so that listeners
                // can directly access Unity objects from their handler function.
                _mainThreadWorkQueue.Enqueue(() => VideoStreamStopped.Invoke());
            }
        }

#if UNITY_EDITOR
        uint width = 0;
        uint height = 0;
        object mutex = new object();
#endif

        /// <summary>
        /// Interal help callback on remote video frame ready. Enqueues the newly-available video
        /// frame into the internal <see cref="VideoStreamSource.FrameQueue"/> for later consumption by
        /// a video renderer.
        /// </summary>
        /// <param name="frame">The newly-available video frame from the remote peer</param>
        private void I420ARemoteVideoFrameReady(in I420AVideoFrame frame)
        {
            if (frame.width < 3000 &&
                frame.height < 3000 &&
                frame.strideY <= frame.width && // doesn't include padding but this isn't a real robust test.
                frame.strideU <= frame.width &&
                frame.strideV <= frame.width &&
                frame.strideA <= frame.width)
            {
                // This does not need to enqueue work, because FrameQueue is thread-safe
                // and can be manipulated from any thread (does not access Unity objects).
                FrameQueue.Enqueue(frame);

#if UNITY_EDITOR
                lock (mutex)
                {
                    width = frame.width;
                    height = frame.height;
                }
#endif
            }
            else
            {
                Debug.LogError("Bad frame detected, skipping");
                Debug.LogError($"WxH: {frame.width}x{frame.height}");
                Debug.LogError($"Stride YUVA: {frame.strideY},{frame.strideU},{frame.strideV},{frame.strideA}");
            }
        }

#if UNITY_EDITOR
    [CustomEditor(typeof(RemoteVideoSource))]
    public class RemoteVideoSourceCustomEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            RemoteVideoSource source = (RemoteVideoSource)target;

            DrawDefaultInspector();

            // FrameQueue will be null if the object has never awoken. Happens in odd instances, such as viewing prefabs while in play mode.
            if (Application.isPlaying && source.FrameQueue != null)
            {
                EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                lock (source.mutex)
                {
                    GUILayout.Label($"Resolution: {source.width}x{source.height}");
                }
                GUILayout.Label($"Queued Per Second:   {source.FrameQueue.QueuedFramesPerSecond.ToString("F2")}");
                GUILayout.Label($"Dequeued Per Second: {source.FrameQueue.DequeuedFramesPerSecond.ToString("F2")}");
                GUILayout.Label($"Dropped Per Second:  {source.FrameQueue.DroppedFramesPerSecond.ToString("F2")}");
                Repaint();
            }
        }
    }
#endif
    }
}
