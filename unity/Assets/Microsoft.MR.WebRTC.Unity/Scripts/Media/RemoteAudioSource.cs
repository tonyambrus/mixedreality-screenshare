﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using UnityEngine;
using System.Threading;

namespace WebRTC
{
    /// <summary>
    /// This component represents a remote audio source added as an audio track to an
    /// existing WebRTC peer connection by a remote peer and received locally.
    /// The audio track can optionally be displayed locally with a <see cref="MediaPlayer"/>.
    /// </summary>
    [AddComponentMenu("MixedReality-WebRTC/Remote Audio Source")]
    public class RemoteAudioSource : AudioStreamSource
    {
        /// <summary>
        /// Peer connection this remote audio source is extracted from.
        /// </summary>
        [Header("Audio track")]
        public PeerConnection PeerConnection;

        /// <summary>
        /// Automatically play the remote audio track when it is added.
        /// </summary>
        public bool AutoPlayOnAdded = true;

        /// <summary>
        /// Is the audio source currently playing?
        /// The concept of _playing_ is described in the <see cref="Play"/> function.
        /// </summary>
        /// <seealso cref="Play"/>
        /// <seealso cref="Stop()"/>
        public bool IsPlaying { get; private set; }

        /// <summary>
        /// Internal queue used to marshal work back to the main Unity thread.
        /// </summary>
        private ConcurrentQueue<Action> _mainThreadWorkQueue = new ConcurrentQueue<Action>();

        /// <summary>
        /// Local storage of audio data to be fed to the output
        /// </summary>
        private Microsoft.MixedReality.WebRTC.IAudioReadStream _audioReadStream = null;

        /// <summary>
        /// Cached sample rate since we can't access this in OnAudioFilterRead.
        /// </summary>
        private int _audioSampleRate = 0;

        /// <summary>
        /// Manually start playback of the remote audio feed by registering some listeners
        /// to the peer connection and starting to enqueue audio frames as they become ready.
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
                OnAudioConfigurationChanged(false);
                _audioReadStream = PeerConnection.Peer.CreateAudioReadStream();
            }
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            _audioSampleRate = AudioSettings.outputSampleRate;
        }

        /// <summary>
        /// Stop playback of the remote audio feed and unregister the handler listening to remote
        /// video frames.
        /// 
        /// Note that this is independent of whether or not a remote track is actually present.
        /// In particular this does not fire the <see cref="AudioStreamSource.AudioStreamStopped"/>, which corresponds
        /// to a track being made available to the local peer by the remote peer.
        /// </summary>
        /// <seealso cref="Play()"/>
        /// <seealso cref="IsPlaying"/>
        public void Stop()
        {
            if (IsPlaying)
            {
                IsPlaying = false;
                if (_audioReadStream != null)
                {
                    _audioReadStream.Dispose();
                    _audioReadStream = null;
                }
            }
        }

        /// <summary>
        /// Implementation of <a href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.Awake.html">MonoBehaviour.Awake</a>
        /// which registers some handlers with the peer connection to listen to its <see cref="PeerConnection.OnInitialized"/>
        /// and <see cref="PeerConnection.OnPreShutdown"/> events.
        /// </summary>
        protected void Awake()
        {
            PeerConnection.OnInitialized.AddListener(OnPeerInitialized);
            PeerConnection.OnPreShutdown.AddListener(OnPeerShutdown);
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        /// <summary>
        /// Implementation of <a href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnDestroy.html">MonoBehaviour.OnDestroy</a>
        /// which unregisters all listeners from the peer connection.
        /// </summary>
        protected void OnDestroy()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
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
        /// on remote tracks added and removed, and optionally starts audio playback if the
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
        /// Internal helper callback fired when the peer is shut down, which stops audio playback and
        /// unregister all the event listeners from the peer connection about to be destroyed.
        /// </summary>
        private void OnPeerShutdown(PeerConnection pc)
        {
            Stop();
            if (PeerConnection.Peer != null)
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
            if (trackKind == Microsoft.MixedReality.WebRTC.PeerConnection.TrackKind.Audio)
            {
                // Enqueue invoking the unity event from the main Unity thread, so that listeners
                // can directly access Unity objects from their handler function.
                _mainThreadWorkQueue.Enqueue(() => AudioStreamStarted.Invoke());
            }
        }

        /// <summary>
        /// Internal free-threaded helper callback on track added, which enqueues the
        /// <see cref="VideoStreamSource.VideoStreamStopped"/> event to be fired from the main
        /// Unity thread.
        /// </summary>
        private void TrackRemoved(Microsoft.MixedReality.WebRTC.PeerConnection.TrackKind trackKind)
        {
            if (trackKind == Microsoft.MixedReality.WebRTC.PeerConnection.TrackKind.Audio)
            {
                // Enqueue invoking the unity event from the main Unity thread, so that listeners
                // can directly access Unity objects from their handler function.
                _mainThreadWorkQueue.Enqueue(() => AudioStreamStopped.Invoke());
            }
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (_audioReadStream != null)
            {
                _audioReadStream.ReadAudio(_audioSampleRate, data, channels);
            }
            else
            {
                for (int i = 0; i < data.Length; ++i)
                {
                    data[i] = 0.0f;
                }
            }
        }
    }
}
