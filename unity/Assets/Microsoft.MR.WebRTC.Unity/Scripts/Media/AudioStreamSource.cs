// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using UnityEngine;
using UnityEngine.Events;

namespace WebRTC
{
    /// <summary>
    /// Unity event corresponding to a new audio stream being started.
    /// </summary>
    [Serializable]
    public class AudioStreamStartedEvent : UnityEvent
    { };

    /// <summary>
    /// Unity event corresponding to an on-going audio stream being stopped.
    /// </summary>
    [Serializable]
    public class AudioStreamStoppedEvent : UnityEvent
    { };

    /// <summary>
    /// Base class for audio sources plugging into the internal peer connection API to
    /// expose a single audio stream to a renderer (<see cref="MediaPlayer"/> or custom).
    /// </summary>
    public abstract class AudioStreamSource : MonoBehaviour
    {
        public AudioStreamStartedEvent AudioStreamStarted { get; private set; } = new AudioStreamStartedEvent();
        public AudioStreamStoppedEvent AudioStreamStopped { get; private set; } = new AudioStreamStoppedEvent();
    }
}
