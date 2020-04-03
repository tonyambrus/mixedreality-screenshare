using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Microsoft.MixedReality.WebRTC;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace WebRTC
{
    /// <summary>
    /// This class will readback data from a render texture into a local native array, which can
    /// then be filled into a WebRTC FrameRequest. This approach results in us reading back data
    /// every frame and converting to YUV every frame, which is a lot of work compared to the
    /// RenderTextureVideoStreamSource on its own, but scales much better as the connection count
    /// increases.
    /// </summary>
    public class RenderTargetYuvProvider : YuvProvider
    {
        [SerializeField]
        [Tooltip("Camera to back the readback commands off of. If none is provided, the main camera will be used.")]
        private Camera renderCamera = null;

        [SerializeField]
        [Tooltip("Render Texture to read back data from. If none is provided, the RT from the renderCamera will be used.")]
        private RenderTexture renderTexture = null;

        [SerializeField]
        [Tooltip("The maximum number of allocations the frame queue will make.")]
        private int maxFrameQueueSize = 5;

        [SerializeField]
        [Tooltip("When downsampling to chroma, determines if a single source pixel should be used or if a box filter should be applied.")]
        private bool averageChroma = false;

        [SerializeField]
        [Tooltip("Max video playback framerate, in frames per second")]
        [Range(0.001f, 120f)]
        private float maxVideoFramerate = 30f;

        [SerializeField]
        [Tooltip("The number of worker threads to use when converting kinect data for network transfer. More isn't better. Tune this per machine.")]
        [Range(1, 128)]
        private int threadCount = 16;

        private CommandBuffer commandBuffer = null;

        private ThreadedImageProcessor imageProcessor = null;

        private byte[] dstData;
        private NativeArray<Color32> srcData;

        private float minUpdateDelay = 0.0f;
        private float lastUpdateTime = 0.0f;

        private MovingAverage workTimeAverage = new MovingAverage(30);
        private MovingAverage asyncReadbackCompleteDt = new MovingAverage(30);
        private long lastAsyncReadbackCompleteTime = 0;

        public float ReadbacksPerSecond => 1000.0f / asyncReadbackCompleteDt.Average;

        protected void OnEnable()
        {
            EnsureDependencies();
            CreateLocalObjects();
        }

        protected void OnDisable()
        {
            CleanupLocalObjects();
        }

        private void EnsureDependencies()
        {
            // If no camera provided, attempt to fallback to main camera
            if (renderCamera == null)
            {
                renderCamera = Camera.main;
            }

            if (renderCamera == null)
            {
                throw new NullReferenceException("Empty render camera for NativeTextureProvider, and could not find MainCamera as fallback.");
            }

            if (renderTexture == null)
            {
                renderTexture = renderCamera.targetTexture;
            }

            if (renderTexture == null)
            {
                throw new NullReferenceException("Empty render texture for NativeTextureProvider, and could not find a RenderTexture on the provided renderCamera.");
            }
        }

        private void CreateLocalObjects()
        {
            if (commandBuffer != null)
            {
                throw new InvalidOperationException("Command buffer already initialized.");
            }

            Initialize(renderTexture.width, renderTexture.height, 1, maxFrameQueueSize);

            commandBuffer = new CommandBuffer();
            commandBuffer.name = "NativeTextureProvider";

            // Copy readback texture to RAM asynchronously, invoking the given callback once done.
            commandBuffer.BeginSample("Readback");
            commandBuffer.RequestAsyncReadback(renderTexture, 0, TextureFormat.RGBA32, OnAsyncReadbackComplete);
            commandBuffer.EndSample("Readback");

            renderCamera.AddCommandBuffer(CameraEvent.AfterEverything, commandBuffer);

            imageProcessor = new ThreadedImageProcessor(threadCount, 0, renderTexture.height, ProcessLines);

            // Leave 3ms of margin, otherwise it misses 1 frame and drops to ~20 FPS
            // when Unity is running at 60 FPS.
            minUpdateDelay = Mathf.Max(0f, 1f / Mathf.Max(0.001f, maxVideoFramerate) - 0.003f);
        }

        private void CleanupLocalObjects()
        {
            Deinitialize();

            if (renderCamera != null)
            {
                renderCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, commandBuffer);
            }

            if (commandBuffer != null)
            {
                commandBuffer.Dispose();
                commandBuffer = null;
            }

            imageProcessor.Dispose();
            imageProcessor = null;
        }

        private void ProcessLines(int yStart, int yStop)
        {
            for (int y = yStart; y < yStop; ++y)
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
                        if (averageChroma)
                        {
                            // Note we susbtract luma to get to the next row because we step backwards along y.
                            r = (byte)(((int)r + srcData[srcPixelIndex + 1].r + srcData[srcPixelIndex - lumaWidth].r + srcData[srcPixelIndex - lumaWidth + 1].r) >> 2);
                            g = (byte)(((int)g + srcData[srcPixelIndex + 1].g + srcData[srcPixelIndex - lumaWidth].g + srcData[srcPixelIndex - lumaWidth + 1].g) >> 2);
                            b = (byte)(((int)b + srcData[srcPixelIndex + 1].b + srcData[srcPixelIndex - lumaWidth].b + srcData[srcPixelIndex - lumaWidth + 1].b) >> 2);
                        }

                        // Recalculate the dst index to account for the 1/4 res chroma channels.
                        dstPixelIndex = ((y >> 1) * chromaWidth + (x >> 1));

                        // convert rgb => uv.
                        dstData[uOffset + dstPixelIndex] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                        dstData[vOffset + dstPixelIndex] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                    }
                }
            }
        }

        private void OnAsyncReadbackComplete(AsyncGPUReadbackRequest request)
        {

#if UNITY_EDITOR
            // Inside the Editor, constantly update _minUpdateDelay to
            // react to user changes to MaxFramerate.
            // Leave 3ms of margin, otherwise it misses 1 frame and drops to ~20 FPS
            // when Unity is running at 60 FPS.
            minUpdateDelay = Mathf.Max(0f, 1f / Mathf.Max(0.001f, maxVideoFramerate) - 0.003f);
#endif
            var curTime = Time.time;
            if (curTime - lastUpdateTime < minUpdateDelay)
            {
                return;
            }
            lastUpdateTime = curTime;

            // Read back the data from GPU, if available
            if (request.hasError || frameQueues.Count == 0)
            {
                Debug.LogError("Failed to get buffer for writing. Dropping frame.");
                return;
            }

            long startTime = System.Diagnostics.Stopwatch.GetTimestamp();

            dstData = frameQueues[0].TryAcquireForWriting();
            if (dstData == null)
            {
                Debug.LogError("Failed to get buffer for writing. Dropping frame.");
                return;
            }

            try
            {
                srcData = request.GetData<Color32>();
                imageProcessor.RunImageProcessing();

                long stopTime = System.Diagnostics.Stopwatch.GetTimestamp();

                double workTime = ((stopTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency) * 1000.0;
                workTimeAverage.AddSample((float)workTime);

                double dt = ((stopTime - lastAsyncReadbackCompleteTime) / (double)System.Diagnostics.Stopwatch.Frequency) * 1000.0f;
                lastAsyncReadbackCompleteTime = stopTime;
                asyncReadbackCompleteDt.AddSample((float)dt);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                frameQueues[0].ReleaseFromWriting(dstData, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }

#if UNITY_EDITOR
        [CustomEditor(typeof(RenderTargetYuvProvider))]
        public class RenderTargetYuvProviderCustomEditor : UnityEditor.Editor
        {
            public override void OnInspectorGUI()
            {
                RenderTargetYuvProvider provider = (RenderTargetYuvProvider)target;

                DrawDefaultInspector();

                if (Application.isPlaying)
                {
                    EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                    GUILayout.Label($"Readbacks Per Second: {provider.ReadbacksPerSecond}");
                    GUILayout.Label($"Average Work Time: {provider.workTimeAverage.Average}");
                }

            }
        }
#endif
    }
}

