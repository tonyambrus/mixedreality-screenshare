﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using UnityEngine;

namespace WebRTC
{
    /// <summary>
    /// This component represents a local audio source added as an audio track to an
    /// existing WebRTC peer connection and sent to the remote peer. The audio track
    /// can optionally be rendered locally with a <see cref="MediaPlayer"/>.
    /// </summary>
    [AddComponentMenu("MixedReality-WebRTC/Mic Audio Stream Source")]
    public class MicAudioStreamSource : AudioStreamSource
    {
        /// <summary>
        /// Automatically start local audio capture when this component is enabled.
        /// </summary>
        [Header("Local audio capture")]
        [Tooltip("Automatically audio capture when this component is enabled")]
        public bool AutoAddTrack = true;

        /// <summary>
        /// Name of the preferred audio codec, or empty to let WebRTC decide.
        /// See https://en.wikipedia.org/wiki/RTP_audio_video_profile for the standard SDP names.
        /// </summary>
        [Tooltip("SDP name of the preferred audio codec to use if supported")]
        public string PreferredAudioCodec = string.Empty;

        /// <summary>
        /// Peer connection this local audio source will add an audio track to.
        /// </summary>
        [Header("Audio track")]
        public PeerConnection PeerConnection;

        protected void Awake()
        {
            PeerConnection.OnInitialized.AddListener(OnPeerInitialized);
            PeerConnection.OnPreShutdown.AddListener(OnPeerShutdown);
        }

        protected void OnDestroy()
        {
            PeerConnection.OnInitialized.RemoveListener(OnPeerInitialized);
            PeerConnection.OnPreShutdown.RemoveListener(OnPeerShutdown);
        }

        protected void OnEnable()
        {
            var nativePeer = PeerConnection?.Peer;
            if ((nativePeer != null) && nativePeer.Initialized)
            {
                DoAutoStartActions(nativePeer);
            }
        }

        protected void OnDisable()
        {
            var nativePeer = PeerConnection.Peer;
            if ((nativePeer != null) && nativePeer.Initialized)
            {
                AudioStreamStopped.Invoke();
                nativePeer.RemoveLocalAudioTrack();
            }
        }

        private void OnPeerInitialized(PeerConnection pc)
        {
            var nativePeer = PeerConnection.Peer;
            nativePeer.PreferredAudioCodec = PreferredAudioCodec;

            // Only perform auto-start actions (add track, start capture) if the component
            // is enabled. Otherwise just do nothing, this component is idle.
            if (enabled)
            {
                DoAutoStartActions(nativePeer);
            }
        }

        private async void DoAutoStartActions(Microsoft.MixedReality.WebRTC.PeerConnection nativePeer)
        {
            if (AutoAddTrack)
            {
                // Force again PreferredAudioCodec right before starting the local capture,
                // so that modifications to the property done after OnPeerInitialized() are
                // accounted for.
                nativePeer.PreferredAudioCodec = PreferredAudioCodec;

                await nativePeer.AddLocalAudioTrackAsync();
                AudioStreamStarted.Invoke();
            }
        }

        private void OnPeerShutdown(PeerConnection pc)
        {
            AudioStreamStopped.Invoke();
            var nativePeer = PeerConnection.Peer;
            nativePeer.RemoveLocalAudioTrack();
        }
    }
}
