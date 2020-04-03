// #define LOG_QUEUE_AGGRESIVELY
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.MixedReality.WebRTC;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace WebRTC
{
    /// <summary>
    /// This class will readback data from a render texture into a local native array, which can
    /// then be filled into a WebRTC FrameRequest. This approach results in us reading back data
    /// every frame and converting to YUV every frame, which is a lot of work compared to the
    /// RenderTextureVideoStreamSource on its own, but scales much better as the connection count
    /// increases.
    /// </summary>
    public class YuvProvider : MonoBehaviour
    {
        /// <summary>
        /// Helper class designed for the NativeTextureProvider *only*. Basically solves the
        /// reader/writer problem for the specific case of a writer on the main unity thread and
        /// many readers on worker threads. Be careful using or modifying this code. It currently
        /// assumes that creation, disposal, and writer operations all happen on the main thread.
        /// It assumes all reader operations are off thread. We feature some anti-patterns for
        /// locking (inconsistent locking order) because we *only* do it on one thread and the
        /// reader threads specifically never double lock. The reason for this is simplicity and
        /// performance. Just be aware of what you're getting into. Usage is generally protected by
        /// exceptions, but making changes here could easily break things.
        /// </summary>
        protected class MultiConsumerFrameQueue : IDisposable
        {
            public struct QueueEntry
            {
                public int RefCount;
                public bool HasBeenAccesed;
                public byte[] Data;
                public long Timestamp;
            }

            private object readMutex = new object();
            private object writeMutex = new object();

            private List<QueueEntry> queuedFrames = new List<QueueEntry>();
            private Queue<byte[]> unusedFrames = new Queue<byte[]>();

            private int bufferSize = 0;
            private int maxQueueSize = 0;
            private int createdBuffers = 0;

            private long lastWrittenTimestamp = 0;

            private bool bufferOutForWrite = false;

            private Thread mainThread = null;

            private MovingAverage releaseFromWriteDt = new MovingAverage(30);
            private long lastReleaseFromWriteTime = 0;

            private MovingAverage releaseFromReadDt = new MovingAverage(30);
            private long lastReleaseFromReadTime = 0;

            private StringBuilder builder = new StringBuilder();

            public int BufferCount => createdBuffers;

            public int DroppedReads { get; private set; }
            public int DroppedWrites { get; private set; }
            public int QueueSize { get; private set; }

            public float WritesPerSecond => 1000.0f / releaseFromWriteDt.Average;

            public float ReadsPerSecond
            {
                get
                {
                    float result;
                    lock (readMutex)
                    {
                        float average = releaseFromReadDt.Average;
                        result = average == 0 ? 0 : (1000.0f / releaseFromReadDt.Average);
                    }
                    return result;
                }
            }

            public MultiConsumerFrameQueue(int bufferSize, int maxQueueSize)
            {
                if (bufferSize <= 0)
                {
                    throw new InvalidOperationException("Cannot support zero or negative buffer sizes.");
                }

                if (maxQueueSize <= 0)
                {
                    throw new InvalidOperationException("Cannot support zero or negative buffer counts.");
                }

                this.bufferSize = bufferSize;
                this.maxQueueSize = maxQueueSize;
                this.mainThread = Thread.CurrentThread;
            }

            /// <summary>
            /// Helper to clean up entries in the queue.
            /// </summary>
            private void TryRemoveOldestEntryFromQueue()
            {
                // This is a safety check. Since we take both locks, it's suddnely possible to
                // create a deadlock if a reader thread decided it wanted to take both locks in a
                // different order. This is technically safe, but generally not how the class was
                // meant to be used, so protect against it for simplicity.
                if (!mainThread.Equals(Thread.CurrentThread))
                {
                    throw new InvalidOperationException("CleanUpQueue is only supported on the main thread.");
                }

                lock (readMutex)
                {
                    lock (writeMutex)
                    {
                        if (queuedFrames.Count == maxQueueSize)
                        {
                            QueueEntry entry = queuedFrames[0];
                            if (entry.RefCount == 0)
                            {
                                unusedFrames.Enqueue(entry.Data);
                                queuedFrames.RemoveAt(0);
                            }
                            else
                            {
                                // Log here since ew are aware of the error and already have a read lock in the specific state that triggered it.
                                builder.Clear();
                                builder.AppendLine("Failed to get a buffer for writing. Likely means readers are falling behind.");
                                for (int i = 0; i < queuedFrames.Count; ++i)
                                {
                                    builder.AppendLine($"    Entry: [timestamp:{queuedFrames[i].Timestamp}] [refcount:{queuedFrames[i].RefCount}] [accessed:{queuedFrames[i].HasBeenAccesed}]");
                                }
                                Debug.LogWarning(builder.ToString());
                            }
                        }
                    }
                }
            }

            public byte[] TryAcquireForWriting()
            {
                // Make sure we only need to lock the queuedFrames and unusedFrames collections.
                if (!mainThread.Equals(Thread.CurrentThread))
                {
                    throw new InvalidOperationException("TryAcquireForWriting must be called from the main thread.");
                }

                // Sanity check.
                if (bufferOutForWrite)
                {
                    throw new InvalidOperationException("Buffer is still out for write. Cannot reacquire");
                }

                byte[] result = null;

                // Try to pull a buffer off the buffer pool.
                lock (writeMutex)
                {
                    // Clean up queue entries to make sure we have items in the unused frame
                    // pool. This helps minimize allocations. 
                    TryRemoveOldestEntryFromQueue();

                    if (unusedFrames.Count > 0)
                    {
                        result = unusedFrames.Dequeue();
                    }
                }

                // If we don't have a valid buffer, we may need to create one.
                if (result == null && (createdBuffers < maxQueueSize))
                { 
                    result = new byte[bufferSize];
                    createdBuffers++;
                }

                if (result != null)
                {
                    bufferOutForWrite = true;
                }
                else
                {
                    DroppedWrites++;
                }

                return result;
            }

            public void ReleaseFromWriting(byte[] data, long timestamp)
            {
                // Make sure we only need to lock the queuedFrames and unusedFrames collections.
                if (!mainThread.Equals(Thread.CurrentThread))
                {
                    throw new InvalidOperationException("ReleaseFromWriting must be called from the main thread.");
                }

                if (!bufferOutForWrite)
                {
                    throw new InvalidOperationException("Buffer is not out for write. Cannot release it.");
                }
                bufferOutForWrite = false;

                if (timestamp <= lastWrittenTimestamp)
                {
                    throw new InvalidOperationException("Timestamps must always increase");
                }
                lastWrittenTimestamp = timestamp;

                lock (readMutex)
                {
                    queuedFrames.Add(new QueueEntry()
                    {
                        RefCount = 0,
                        HasBeenAccesed = false,
                        Data = data,
                        Timestamp = timestamp
                    });

#if LOG_QUEUE_AGGRESIVELY
                    builder.Clear();
                    builder.AppendLine($"Releasing From Write: {timestamp}");
                    for (int i = 0; i < queuedFrames.Count; ++i)
                    {
                        builder.AppendLine($"    Entry: [timestamp:{queuedFrames[i].Timestamp}] [refcount:{queuedFrames[i].RefCount}] [accessed:{queuedFrames[i].HasBeenAccesed}]");
                    }
                    Debug.LogWarning(builder.ToString());
#endif
                }

                long newReleaseFromWriteTime = System.Diagnostics.Stopwatch.GetTimestamp();
                double dt = ((newReleaseFromWriteTime - lastReleaseFromWriteTime) / (double)System.Diagnostics.Stopwatch.Frequency) * 1000.0f;
                lastReleaseFromWriteTime = newReleaseFromWriteTime;
                releaseFromWriteDt.AddSample((float)dt);
            }

            public byte[] TryAcquireForReading(ref long timestamp)
            {
                byte[] result = null;
                lock (readMutex)
                {
                    for (int i = 0; i < queuedFrames.Count && result == null; ++i)
                    { 
                        QueueEntry entry = queuedFrames[i];
                        if (entry.Timestamp > timestamp)
                        {
                            entry.RefCount++;
                            entry.HasBeenAccesed = true;
                            queuedFrames[i] = entry;

                            result = entry.Data;
                            timestamp = entry.Timestamp;
                        }
                    }

#if LOG_QUEUE_AGGRESIVELY
                    builder.Clear();
                    builder.AppendLine($"Acquire For Read: {timestamp} {result != null}");
                    for (int i = 0; i < queuedFrames.Count; ++i)
                    {
                        builder.AppendLine($"    Entry: [timestamp:{queuedFrames[i].Timestamp}] [refcount:{queuedFrames[i].RefCount}] [accessed:{queuedFrames[i].HasBeenAccesed}]");
                    }
                    Debug.LogWarning(builder.ToString());
#endif

                    if (result == null)
                    {
                        DroppedReads++;
                    }
                }
                return result;
            }

            public void ReleaseFromReading(byte[] data)
            {
                if (data == null)
                {
                    return;
                }

                int foundFrameIndex = -1;
                lock (readMutex)
                {
                    for (int i = 0; i < queuedFrames.Count && foundFrameIndex == -1; i++)
                    {
                        if (queuedFrames[i].Data == data)
                        {
                            foundFrameIndex = i;
                            QueueEntry entry = queuedFrames[i];

#if LOG_QUEUE_AGGRESIVELY
                            builder.Clear();
                            builder.AppendLine($"Release For Read: {foundFrameIndex} {entry.Timestamp} {entry.RefCount}");
                            for (int j = 0; j < queuedFrames.Count; ++j)
                            {
                                builder.AppendLine($"    Entry: [timestamp:{queuedFrames[j].Timestamp}] [refcount:{queuedFrames[j].RefCount}] [accessed:{queuedFrames[j].HasBeenAccesed}]");
                            }
                            Debug.LogWarning(builder.ToString());
#endif

                            if (entry.RefCount >= 1)
                            {
                                entry.RefCount--;
                                queuedFrames[i] = entry;
                                break;
                            }
                            else
                            {
                                throw new InvalidOperationException("Invalid ref count");
                            }
                        }
                    }

                    long newReleaseFromReadTime = System.Diagnostics.Stopwatch.GetTimestamp();
                    double dt = ((newReleaseFromReadTime - lastReleaseFromReadTime) / (double)System.Diagnostics.Stopwatch.Frequency) * 1000.0f;
                    lastReleaseFromReadTime = newReleaseFromReadTime;
                    releaseFromReadDt.AddSample((float)dt);
                }

                if (foundFrameIndex == -1)
                {
                    throw new InvalidOperationException("Failed to find a frame for release.");
                }
            }

            public virtual void Dispose()
            {
                // Make sure we only need to lock the queuedFrames and unusedFrames collections.
                if (!mainThread.Equals(Thread.CurrentThread))
                {
                    throw new InvalidOperationException("Dispose must be called from the main thread.");
                }

                if (bufferOutForWrite)
                {
                    throw new InvalidOperationException("Cannot dispose of the queue if a buffer is out for write.");
                }

                // Prevent further allocations.
                maxQueueSize = 0;
                bufferSize = 0;

                // Dispose of our native arrays.
                while (unusedFrames.Count > 0)
                {
                    byte[] data = unusedFrames.Dequeue();
                    data = null;
                    createdBuffers--;
                }

                // Clean up the reader queue.
                lock (readMutex)
                {
                    lock (writeMutex)
                    {
                        // Walk backward so we can find the most relevant unused entry.
                        for (int i = 0; i < queuedFrames.Count;)
                        {
                            QueueEntry entry = queuedFrames[i];

                            // Only ever cleanup entries that aren't being used. Always leave at
                            // least 1 entry for reader use unless we're disposing of the object.
                            if (entry.RefCount == 0)
                            {
                                entry.Data = null;
                                createdBuffers--;
                                queuedFrames.RemoveAt(i);
                            }
                            else
                            {
                                // No removal, so we advance.
                                i++;
                            }
                        }

                        QueueSize = queuedFrames.Count;
                    }

                    if (queuedFrames.Count > 0 || createdBuffers != 0)
                    {
                        throw new InvalidOperationException("Readers must have been externally cleaned up before disposing of this object.");
                    }
                }
            }
        }

        protected List<MultiConsumerFrameQueue> frameQueues = new List<MultiConsumerFrameQueue>();

        protected int lumaWidth = 0;
        protected int lumaHeight = 0;
        protected int lumaSize = 0;

        protected int chromaWidth = 0;
        protected int chromaHeight = 0;
        protected int chromaSize = 0;

        protected int yOffset = 0;
        protected int uOffset = 0;
        protected int vOffset = 0;

        protected int yuvSize = 0;

        private object localQueueMutex = new object();

        public int GetBufferCount(int trackIndex) { return frameQueues[trackIndex].BufferCount; }
        public int GetDroppedReads(int trackIndex) { return frameQueues[trackIndex].DroppedReads; }
        public int GetDroppedWrites(int trackIndex) { return frameQueues[trackIndex].DroppedWrites; }
        public int GetQueueSize(int trackIndex) { return frameQueues[trackIndex].QueueSize; }
        public float GetWritesPerSecond(int trackIndex) { return frameQueues[trackIndex].WritesPerSecond; }
        public float GetReadsPerSecond(int trackIndex) { return frameQueues[trackIndex].ReadsPerSecond; }
        public int TrackCount => frameQueues.Count;

        protected void Initialize(int imageWidth, int imageHeight, int trackCount, int maxFrameQueueSize)
        {
            lumaWidth = imageWidth;
            lumaHeight = imageHeight;
            lumaSize = lumaWidth * lumaHeight;

            chromaWidth = (lumaWidth >> 1);
            chromaHeight = (lumaHeight >> 1);
            chromaSize = chromaWidth * chromaHeight;

            yOffset = 0;
            uOffset = yOffset + lumaSize;
            vOffset = uOffset + chromaSize;

            yuvSize = lumaSize + chromaSize * 2;

            for (int i = 0; i < trackCount; ++i)
            {
                frameQueues.Add(new MultiConsumerFrameQueue(yuvSize, maxFrameQueueSize));
            }
        }

        protected virtual void Deinitialize()
        {
            lumaWidth = 0;
            lumaHeight = 0;
            lumaSize = 0;

            chromaWidth = 0;
            chromaHeight = 0;
            chromaSize = 0;

            yOffset = 0;
            uOffset = 0;
            vOffset = 0;

            yuvSize = 0;

            for (int i = 0; i < frameQueues.Count; ++i)
            {
                frameQueues[i].Dispose();
            }
            frameQueues.Clear();
        }

        /// <summary>
        /// Must be thread safe. Can be called from multiple workers at a time.
        /// </summary>
        public unsafe void I420AVideoFrameRequest(ref long lastTimestamp, in FrameRequest request, int trackIndex, VideoFrameQueue<I420AVideoFrameStorage> localPlaybackQueue)
        {
            if (yuvSize == 0)
            {
                Debug.LogWarning("Invalid yuv image size");
                return;
            }

            if (trackIndex != 0 || trackIndex < 0 || trackIndex >= frameQueues.Count)
            {
                Debug.LogWarning("Invalid track index request");
            }

            // todo prevent the counting backward bug. require advancing timestamps? Allow queuing?
            // fix the client perf issue. wtf...

            byte[] data = frameQueues[trackIndex].TryAcquireForReading(ref lastTimestamp);
            if (data == null)
            {
                return;
            }

            try
            {
                fixed (byte* pData = data)
                {
                    var dataPtr = (IntPtr)(pData);
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

                    if (request.Source != null)
                    {
                        // Does a low level copy syncronously.
                        request.CompleteRequest(frame);
                    }

                    if (localPlaybackQueue != null)
                    {
                        // This queue isn't thread safe. Lock for the caller, so they don't have to lock the other operations in this call.
                        lock (localQueueMutex)
                        {
                            localPlaybackQueue.Enqueue(frame);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                frameQueues[trackIndex].ReleaseFromReading(data);
            }
        }
    }
}
