// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using UnityEngine;
using Unity.Profiling;
using Microsoft.MixedReality.WebRTC;
namespace WebRTC
{
    [RequireComponent(typeof(Renderer))]
    public class YuvProviderMediaPlayer : MonoBehaviour
    {
        [SerializeField]
        [Range(0.001f, 120f)]
        private float MaxVideoFramerate = 30f;

        private YuvProvider provider = null;

        private Texture2D _textureY = null;
        private Texture2D _textureU = null;
        private Texture2D _textureV = null;

        private float lastUpdateTime = 0.0f;

        private Material videoMaterial;
        private float _minUpdateDelay;
        
        private VideoFrameQueue<I420AVideoFrameStorage> FrameQueue = null;

        private long lastFrameTimestamp = 0;

        private void Start()
        {
            CreateEmptyVideoTextures();

            FrameQueue = new VideoFrameQueue<I420AVideoFrameStorage>(3);

            // Leave 3ms of margin, otherwise it misses 1 frame and drops to ~20 FPS
            // when Unity is running at 60 FPS.
            _minUpdateDelay = Mathf.Max(0f, 1f / Mathf.Max(0.001f, MaxVideoFramerate) - 0.003f);

            if (WebRtcServer.Instance != null)
            {
                provider = WebRtcServer.Instance.DefaultProvider;
            }
        }

        private void OnEnalbe()
        {
            lastFrameTimestamp = 0;
        }

        private void CreateEmptyVideoTextures()
        {
            // Create a default checkboard texture which visually indicates
            // that no data is available. This is useful for debugging and
            // for the user to know about the state of the video.
            _textureY = new Texture2D(1, 1);
            _textureY.SetPixel(0, 0, Color.black);
            _textureY.Apply();
            _textureU = new Texture2D(1, 1);
            _textureU.SetPixel(0, 0, Color.grey);
            _textureU.Apply();
            _textureV = new Texture2D(1, 1);
            _textureV.SetPixel(0, 0, Color.grey);
            _textureV.Apply();

            // Assign that texture to the video player's Renderer component
            videoMaterial = GetComponent<Renderer>().material;
            videoMaterial.SetTexture("_YPlane", _textureY);
            videoMaterial.SetTexture("_UPlane", _textureU);
            videoMaterial.SetTexture("_VPlane", _textureV);
        }

        private void Update()
        {
            if (provider != null)
            {
    #if UNITY_EDITOR
                // Inside the Editor, constantly update _minUpdateDelay to
                // react to user changes to MaxFramerate.

                // Leave 3ms of margin, otherwise it misses 1 frame and drops to ~20 FPS
                // when Unity is running at 60 FPS.
                _minUpdateDelay = Mathf.Max(0f, 1f / Mathf.Max(0.001f, MaxVideoFramerate) - 0.003f);
    #endif
                var curTime = Time.time;
                if (curTime - lastUpdateTime >= _minUpdateDelay)
                {
                    TryProcessFrame();
                    lastUpdateTime = curTime;
                }
            }
        }

        /// <summary>
        /// Internal helper that attempts to process frame data in the frame queue
        /// </summary>
        private void TryProcessFrame()
        {
            // Dummy FrameRequest won't trigger any action. Only a fill for our framequeue.
            FrameRequest fr = new FrameRequest();
            provider.I420AVideoFrameRequest(ref lastFrameTimestamp, in fr, 0, FrameQueue);

            if (FrameQueue.TryDequeue(out I420AVideoFrameStorage frame))
            {
                int lumaWidth = (int)frame.Width;
                int lumaHeight = (int)frame.Height;
                if (_textureY == null || (_textureY.width != lumaWidth || _textureY.height != lumaHeight))
                {
                    _textureY = new Texture2D(lumaWidth, lumaHeight, TextureFormat.R8, false);
                    videoMaterial.SetTexture("_YPlane", _textureY);
                }
                int chromaWidth = lumaWidth / 2;
                int chromaHeight = lumaHeight / 2;
                if (_textureU == null || (_textureU.width != chromaWidth || _textureU.height != chromaHeight))
                {
                    _textureU = new Texture2D(chromaWidth, chromaHeight, TextureFormat.R8, false);
                    videoMaterial.SetTexture("_UPlane", _textureU);
                }
                if (_textureV == null || (_textureV.width != chromaWidth || _textureV.height != chromaHeight))
                {
                    _textureV = new Texture2D(chromaWidth, chromaHeight, TextureFormat.R8, false);
                    videoMaterial.SetTexture("_VPlane", _textureV);
                }

                unsafe
                {
                    fixed (void* buffer = frame.Buffer)
                    {
                        var src = new System.IntPtr(buffer);
                        int lumaSize = lumaWidth * lumaHeight;
                        _textureY.LoadRawTextureData(src, lumaSize);
                        src += lumaSize;
                        int chromaSize = chromaWidth * chromaHeight;
                        _textureU.LoadRawTextureData(src, chromaSize);
                        src += chromaSize;
                        _textureV.LoadRawTextureData(src, chromaSize);
                    }
                }

                _textureY.Apply();
                _textureU.Apply();
                _textureV.Apply();

                // Recycle the video frame packet for a later frame
                FrameQueue.RecycleStorage(frame);
            }
        }
    }
}
