using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class WebRtcServerConnection
{
    private string serverAddress = "http://127.0.0.1:3000";
    private float pollIntervalMs = 500f;

    private string pollTarget = "";

    private MonoBehaviour parent = null;

    private ConcurrentQueue<Action> _mainThreadWorkQueue = new ConcurrentQueue<Action>();

    private float timeSincePollMs = 0f;

    private bool lastGetComplete = true;

    public Action<string> OnPolledMessageReceived;

    public WebRtcServerConnection(string serverAddress, float pollIntervalMs, string pollTarget, MonoBehaviour parent)
    {
        if (string.IsNullOrEmpty(serverAddress))
        {
            throw new InvalidOperationException("");
        }
        if (pollIntervalMs == 0)
        {
            throw new InvalidOperationException("");
        }
        if (string.IsNullOrEmpty(pollTarget))
        {
            throw new InvalidOperationException("");
        }
        if (parent == null)
        {
            throw new InvalidOperationException("");
        }

        this.serverAddress = serverAddress;
        if (!this.serverAddress.EndsWith("/"))
        {
            this.serverAddress += "/";
        }

        this.pollIntervalMs = pollIntervalMs;
        this.pollTarget = pollTarget;
        this.parent = parent;
    }

    public void Update(bool poll = true)
    {
        // Execute any pending work enqueued by background tasks
        while (_mainThreadWorkQueue.TryDequeue(out Action workload))
        {
            workload();
        }

        if (poll)
        {
            // if we have not reached our PollTimeMs value...
            if (timeSincePollMs <= pollIntervalMs)
            {
                // we keep incrementing our local counter until we do.
                timeSincePollMs += Time.deltaTime * 1000.0f;
                return;
            }

            // if we have a pending request still going, don't queue another yet.
            if (!lastGetComplete)
            {
                return;
            }

            // when we have reached our PollTimeMs value...
            timeSincePollMs = 0f;

            // begin the poll and process.
            lastGetComplete = false;

            parent.StartCoroutine(CO_GetAndProcessFromServer());
        }
    }

    /// <summary>
    /// Pushes a message to the main thread for processing.
    /// </summary>
    public Task SendMessageAsync(string sendTarget, byte[] message, string contentType)
    {
        // This method needs to return a Task object which gets completed once the signaler message
        // has been sent. Because the implementation uses a Unity coroutine, use a reset event to
        // signal the task to complete from the coroutine after the message is sent.
        // Note that the coroutine is a Unity object so needs to be started from the main Unity thread.
        var mre = new ManualResetEvent(false);
        _mainThreadWorkQueue.Enqueue(() => parent.StartCoroutine(PostToServerAndWait(sendTarget, message, contentType, mre)));
        return Task.Run(() => mre.WaitOne());
    }

    /// <summary>
    /// Internal helper to wrap a coroutine into a synchronous call
    /// for use inside a <see cref="Task"/> object.
    /// </summary>
    private IEnumerator PostToServerAndWait(string sendTarget, byte[] message, string contentType, ManualResetEvent mre)
    {
        // Start the coroutine and wait for it to finish
        yield return parent.StartCoroutine(PostToServer(sendTarget, message, contentType));
        mre.Set();
    }

    /// <summary>
    /// Internal helper for sending HTTP data to the node-dss server using POST
    /// </summary>
    private IEnumerator PostToServer(string target, byte[] data, string contentType)
    {
        if (target.Length == 0 || data.Length == 0)
        {
            throw new InvalidOperationException("");
        }


        Debug.Log($"POST: {serverAddress}{target}\n{System.Text.Encoding.UTF8.GetString(data)}");

        var www = new UnityWebRequest($"{serverAddress}{target}", UnityWebRequest.kHttpVerbPOST);
        www.uploadHandler = new UploadHandlerRaw(data);
        www.uploadHandler.contentType = contentType;
        yield return www.SendWebRequest();

        if (www.isNetworkError || www.isHttpError)
        {
            Debug.Log($"Failed to send message to remote peer {serverAddress}{target}: {www.error}\n{System.Text.Encoding.UTF8.GetString(data)}");
        }
    }

    /// <summary>
    /// Internal coroutine helper for receiving HTTP data from the DSS server using GET
    /// and processing it as needed
    /// </summary>
    private IEnumerator CO_GetAndProcessFromServer()
    {
        var www = UnityWebRequest.Get($"{serverAddress}{pollTarget}");
        yield return www.SendWebRequest();

        if (!www.isNetworkError && !www.isHttpError)
        {
            Debug.Log($"GET: {serverAddress}{pollTarget}\n{www.downloadHandler.text}");
            OnPolledMessageReceived?.Invoke(www.downloadHandler.text);
        }
        else if (www.isNetworkError)
        {
            Debug.LogError($"Network error trying to send data to {serverAddress}: {www.error}");
        }
        else
        {
            // This is very spammy because the node-dss protocol uses 404 as regular "no data yet" message, which is an HTTP error
            //Debug.LogError($"HTTP error: {www.error}");
        }

        lastGetComplete = true;
    }
}
