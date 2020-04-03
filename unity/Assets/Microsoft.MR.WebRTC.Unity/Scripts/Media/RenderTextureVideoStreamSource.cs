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

namespace WebRTC
{
    /// <summary>
    /// This component represents a local video source added as a video track to an
    /// existing WebRTC peer connection and sent to the remote peer. The video track
    /// can optionally be displayed locally with a <see cref="MediaPlayer"/>.
    /// </summary>
    [AddComponentMenu("MixedReality-WebRTC/Render Target Video Stream Source")]
    public class RenderTargetVideoStreamSource : VideoStreamSource
    {
        /// <summary>
        /// Name of the preferred video codec, or empty to let WebRTC decide.
        /// </summary>
        [SerializeField]
        [Tooltip("Preferred video codec to use if supported")]
        private SdpVideoCodec preferredVideoCodec = SdpVideoCodec.None;

        /// <summary>
        /// Render Texture to read back data from.
        /// </summary>
        [SerializeField]
        [Tooltip("Render Texture to read back data from.")]
        private RenderTexture videoSource = null;

        /// <summary>
        /// Avoid updating the readback texture buffer until the last set of data has been
        /// processed. This can significantly reduce readback rate, but may make the data
        /// being sent more latent.
        /// </summary>
        [SerializeField]
        [Tooltip("When throttled, we don't read back new data until the old data has been read.")]
        private bool throttleTextureReadback = true;

        /// <summary>
        /// Peer connection this local video source will add a video track to.
        /// </summary>
        [Header("Video track")]
        [SerializeField]
        private PeerConnection peerConnection = null;

        /// <summary>
        /// Name of the track. This will be sent in the SDP messages.
        /// </summary>
        [SerializeField]
        [Tooltip("SDP track name.")]
        [SdpToken(allowEmpty: true)]
        private string trackName;

        [SerializeField]
        private bool StartAutomatically = true;

        private ExternalVideoTrackSource ExternalTrackSource { get; set; }
        private NativeArray<Color32> srcData = new NativeArray<Color32>();
        private AsyncGPUReadbackRequest readback;
        private bool srcDataProcessed = true;
        private object srcDataMutex = new object();
        private int videoSourceWidth = 0;
        private int videoSourceHeight = 0;

        /// <summary>
        /// Video track added to the peer connection that this component encapsulates.
        /// </summary>
        public LocalVideoTrack Track { get; private set; }

        protected void Awake()
        {
            FrameQueue = new VideoFrameQueue<I420AVideoFrameStorage>(3);
            peerConnection.OnInitialized.AddListener(OnPeerInitialized);
            peerConnection.OnPreShutdown.AddListener(OnPeerShutdown);
        }

        protected void OnDestroy()
        {
            peerConnection.OnInitialized.RemoveListener(OnPeerInitialized);
            peerConnection.OnPreShutdown.RemoveListener(OnPeerShutdown);
        }

        protected void OnEnable()
        {
            var nativePeer = peerConnection?.Peer;
            if ((nativePeer != null) && nativePeer.Initialized)
            {
                HandleAutoStartIfNeeded(nativePeer);
            }
        }

        protected void OnDisable()
        {
            OnPeerShutdown(peerConnection);
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
            var nativePeer = peerConnection.Peer;
            if (nativePeer != null && nativePeer.Initialized && Track != null)
            {
                VideoStreamStopped.Invoke();
                nativePeer.RemoveLocalVideoTrack(Track);
                Track.Dispose();
                Track = null;
            }

            if (ExternalTrackSource != null)
            {
                ExternalTrackSource.Dispose();
                ExternalTrackSource = null;
            }

            if (srcData.Length > 0)
            {
                srcData.Dispose();
                srcData = new NativeArray<Color32>();
            }

            FrameQueue.Clear();
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

            // Ensure the track has a valid name
            string trackName = this.trackName;
            if (trackName.Length == 0)
            {
                trackName = Guid.NewGuid().ToString();
                this.trackName = trackName;
            }
            SdpTokenAttribute.Validate(trackName, allowEmpty: false);

            FrameQueue.Clear();

            ExternalTrackSource = ExternalVideoTrackSource.CreateFromI420ACallback(I420AVideoFrameRequest);
            Track = nativePeer.AddCustomLocalVideoTrack(trackName, ExternalTrackSource);

            if (Track != null)
            {
                VideoStreamStarted.Invoke();
            }
        }

        private void Update()
        {
            lock (srcDataMutex)
            {
                if (!throttleTextureReadback || srcDataProcessed)
                {
                    // Readback syncronously for now.
                    AsyncGPUReadbackRequest readback = AsyncGPUReadback.Request(videoSource);
                    readback.WaitForCompletion();
                    if (!readback.hasError)
                    {
                        // Cache width and height since we can't access them directly off thread.
                        videoSourceWidth = videoSource.width;
                        videoSourceHeight = videoSource.height;

                        // Not entirely sure what the cleanup pattern is for readback native
                        // buffers, but I get 'already disposed' errors if I don't make a copy. We
                        // can do this better once we move to native.
                        NativeArray<Color32> tmpSrcData = readback.GetData<Color32>();
                        if (srcData.Length != tmpSrcData.Length)
                        {
                            if (srcData.Length > 0)
                            {
                                srcData.Dispose();
                            }
                            srcData = new NativeArray<Color32>(tmpSrcData.Length, Allocator.Persistent);
                        }
                        tmpSrcData.CopyTo(srcData);
                        srcDataProcessed = false;
                    }
                }
            }
        }

        private unsafe void I420AVideoFrameRequest(in FrameRequest request)
        {
            lock (srcDataMutex)
            {
                // Make sure we have something to update.
                if (srcData.Length == 0 || videoSourceWidth == 0 || videoSourceHeight == 0 || srcDataProcessed)
                {
                    return;
                }
                srcDataProcessed = true;

                int lumaWidth = videoSourceWidth;
                int lumaHeight = videoSourceHeight;
                int lumaSize = lumaWidth * lumaHeight;

                int chromaWidth = (lumaWidth >> 1);
                int chromaHeight = (lumaHeight >> 1);
                int chromaSize = chromaWidth * chromaHeight;

                int yOffset = 0;
                int uOffset = yOffset + lumaSize;
                int vOffset = uOffset + chromaSize;

                // DANGER WILL ROBINSON. This can aggresively crash unity...
                var dstData = stackalloc byte[lumaSize * 1 + chromaSize * 2];

                for (int y = 0; y < lumaHeight; ++y)
                {
                    for (int x = 0; x < lumaWidth; ++x)
                    {
                        // Invert Y to handle DX v. OpenGL differences.
                        int srcPixelIndex = (lumaHeight - 1 - y) * lumaWidth + x;
                        int dstPixelIndex = (y * lumaWidth + x);

                        byte r = srcData[srcPixelIndex].r;
                        byte g = srcData[srcPixelIndex].g;
                        byte b = srcData[srcPixelIndex].b;

                        // Convert rgb => y 1:1.
                        dstData[yOffset + dstPixelIndex] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

                        // Down sample every rgb quad to a uv value.
                        if (x % 2 == 0 && y % 2 == 0)
                        {
                            // Consider averaging the quad to avoid aliasing.
#if AVERAGE_CHROMA
                            // Note we susbtract luma to get to the next row because we step backwards along y.
                            r = (byte)(((int)r + srcData[srcPixelIndex + 1].r + srcData[srcPixelIndex - lumaWidth].r + srcData[srcPixelIndex - lumaWidth + 1].r) >> 2);
                            g = (byte)(((int)g + srcData[srcPixelIndex + 1].g + srcData[srcPixelIndex - lumaWidth].g + srcData[srcPixelIndex - lumaWidth + 1].g) >> 2);
                            b = (byte)(((int)b + srcData[srcPixelIndex + 1].b + srcData[srcPixelIndex - lumaWidth].b + srcData[srcPixelIndex - lumaWidth + 1].b) >> 2);
#endif
                            // Recalculate the dst index to account for the 1/4 res chroma channels.
                            dstPixelIndex = ((y >> 1) * chromaWidth + (x >> 1));

                            // convert rgb => uv.
                            dstData[uOffset + dstPixelIndex] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                            dstData[vOffset + dstPixelIndex] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                        }
                    }
                }

                var dataPtr = new IntPtr(dstData);
                var frame = new I420AVideoFrame
                {
                    dataY = dataPtr + yOffset,
                    dataU = dataPtr + uOffset,
                    dataV = dataPtr + vOffset,
                    dataA = IntPtr.Zero,
                    strideY = lumaWidth,
                    strideU = chromaWidth,
                    strideV = chromaWidth,
                    strideA = 0,
                    width = (uint)lumaWidth,
                    height = (uint)lumaHeight
                };
                request.CompleteRequest(frame);
            }
        }

        public static void YuvToRgb(float y, float u, float v, ref byte r, ref byte g, ref byte b)
        {
            r = (byte)(Mathf.Clamp01(1.164f * (y - (16.0f / 255.0f)) + 1.793f * (v - 0.5f)) * 255);
            g = (byte)(Mathf.Clamp01(1.164f * (y - (16.0f / 255.0f)) - 0.534f * (v - 0.5f) - 0.213f * (u - 0.5f)));
            b = (byte)(Mathf.Clamp01(1.164f * (y - (16.0f / 255.0f)) + 2.115f * (u - 0.5f)) * 255);
        }

        public static void RgbToYuv(byte r, byte g, byte b, ref byte y, ref byte u, ref byte v)
        {
            y = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
            u = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
            v = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
        }
    }
}
