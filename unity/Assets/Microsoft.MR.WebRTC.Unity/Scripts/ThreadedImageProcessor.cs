using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Unity.Profiling;
using UnityEngine;

namespace WebRTC
{
    public class ThreadedImageProcessor : IDisposable
    {
        /// <summary>
        /// Used to signal that worker threads can safely shutdown. Called before joining.
        /// </summary>
        private bool threadWorkFinished = false;

        /// <summary>
        /// Threads acquire this when they are ready to work or are working. The main thread can use
        /// this to test that all workers are finished working. 
        /// </summary>
        private Semaphore threadWorkSemaphore = null;

        /// <summary>
        /// Event that blocks the start of the thread work. Set this once all shared state has been
        /// established and work is ready to begin.
        /// </summary>
        private EventWaitHandle threadStartWorkEvent = null;

        /// <summary>
        /// Threads acquire this when they are ready to reset or are resetting. The main thread can use
        /// this to test that all threads are finished resetting.
        /// </summary>
        private Semaphore threadWaitSemaphore = null;

        /// <summary>
        /// Event that blocks the 'reset' phase of the worker threads. This is used to signal that the
        /// main thread is done and the workers can reset their state for next frame.
        /// </summary>
        private EventWaitHandle threadStartWaitEvent = null;

        /// <summary>
        /// Preallocated threads that we keep alive in a custom managed pool. We do this because Tasks
        /// have a large amount of garbage overhead that we want to avoid. This optimization brought us
        /// down from 2KB per frame using parallelfor to 0KB per frame.
        /// </summary>
        private Thread[] workerThreads = null;

        /// <summary>
        /// Number of threads to execute the processing callback on.
        /// </summary>
        private int threadCount = 0;

        /// <summary>
        /// Callback executed by the worker threads.
        /// </summary>
        private ProcessLinesCallback callback = null;

        private ProfilerMarker doWorkMarker = new ProfilerMarker("ThreadedImageProcessor.ProcessingLines");
        private ProfilerMarker stopWorkMarker = new ProfilerMarker("ThreadedImageProcessor.WaitingForOtherThreads");

        public delegate void ProcessLinesCallback(int yStart, int yStop);

        public ThreadedImageProcessor(int threadCount, int yStart, int yEnd, ProcessLinesCallback callback)
        {
            this.threadCount = threadCount;
            this.callback = callback;

            threadWorkFinished = false;

            threadWorkSemaphore = new Semaphore(0, threadCount);
            threadStartWorkEvent = new EventWaitHandle(false, EventResetMode.ManualReset);

            threadWaitSemaphore = new Semaphore(0, threadCount);
            threadStartWaitEvent = new EventWaitHandle(false, EventResetMode.ManualReset);

            int linesPerTask = (yEnd - yStart) / threadCount;
            if (linesPerTask <= 0)
            {
                throw new InvalidOperationException("Invalid yStart/yEnd values given.");
            }

            // Setup the threads. Note that on the last thread, we run to the end value, allowing us
            // to deal with line counts that aren't exact multiples of the thread count.
            workerThreads = new Thread[threadCount];
            for (int i = 0; i < workerThreads.Length; ++i)
            {
                int start = yStart + (i * linesPerTask);
                int stop = (i == workerThreads.Length - 1) ? yEnd : yStart + ((i + 1) * linesPerTask);
                workerThreads[i] = new Thread(() => ProcessLinesWorkerThread(start, stop));
                workerThreads[i].Start();
            }
        }

        public void Dispose()
        {
            // Stop the worker threads and wait for them to exit.
            threadWorkFinished = true;
            threadWorkSemaphore.Release(threadCount);
            threadStartWorkEvent.Set();
            for (int i = 0; i < workerThreads.Length; ++i)
            {
                workerThreads[i].Join();
            }
            workerThreads = null;

            // Reset all the events.
            threadWorkSemaphore = new Semaphore(0, threadCount);
            threadStartWorkEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            threadWaitSemaphore = new Semaphore(0, threadCount);
            threadStartWaitEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        }

        public void RunImageProcessing()
        {
            // Transition from work to wait state. This will guarantee the workers have started their work.
            threadWorkSemaphore.Release(threadCount);
            for (int i = 0; i < threadCount; ++i)
            {
                threadWaitSemaphore.WaitOne();
            }
            threadStartWaitEvent.Reset();
            threadStartWorkEvent.Set();

            // Transition from wait to work state. This will guarantee the workers have finished their work.
            threadWaitSemaphore.Release(threadCount);
            for (int i = 0; i < threadCount; ++i)
            {
                threadWorkSemaphore.WaitOne();
            }
            threadStartWorkEvent.Reset();
            threadStartWaitEvent.Set();
        }

        /// <summary>
        /// The goal: The main thread is running *or* the worker threads are running, but never both.
        /// Each are guaranteed to run and to run to completion before the other begins. Basically a
        /// baton hand-off with a guarantee of execution. Semaphores are the traditional "baton" and
        /// what we use here, but we need a bit extra to guarantee that the other party has actually
        /// taken the baton and started executing. For this we use a second semaphore because we need
        /// to be sure that all N batons in our case have been taken (since we have many worker
        /// threads). Lastly, we introduce some events to prevent race conditions around the exchange
        /// of the semaphores. A third semaphore could be used instead.
        /// </summary>
        private void ProcessLinesWorkerThread(int yStart, int yEnd)
        {
            while (true)
            {
                // Work State.
                {
                    // Exchange the semaphores, acquiring the work semaphore indicating we are now the working party.
                    threadWorkSemaphore.WaitOne();
                    threadWaitSemaphore.Release();

                    // Wait for the main thread to signal our work start.
                    threadStartWorkEvent.WaitOne();

                    // Do our work. Test the exit condition just in case.
                    if (threadWorkFinished)
                    {
                        threadWorkSemaphore.Release();
                        return;
                    }

                    try
                    {
                        using (var profileScope = doWorkMarker.Auto())
                        {
                            callback(yStart, yEnd);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                // Wait State.
                using (var profileScope = stopWorkMarker.Auto())
                {
                    // Exchange the semaphores, acquiring the wait semaphore indicating we are now the waiting party.
                    threadWaitSemaphore.WaitOne();
                    threadWorkSemaphore.Release();

                    // Wait for the main thread to signal our wait to start.
                    threadStartWaitEvent.WaitOne();

                    // Test the exit condition just in case.
                    if (threadWorkFinished)
                    {
                        threadWaitSemaphore.Release();
                        return;
                    }
                }
            }
        }
    }
}
