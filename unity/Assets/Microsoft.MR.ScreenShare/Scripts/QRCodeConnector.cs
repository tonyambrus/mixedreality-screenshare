using Microsoft.MixedReality.QR;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using WebRTC;

public class QRCodeConnector : MonoBehaviour
{
    [Serializable]
    public struct ConnectionData
    {
        public string url;
        public string streamName;
    }

    public bool enableAudio = true;

    public Text text;
    [SerializeField] protected GameObject prefab = null;
    [SerializeField] protected Transform parent = null;
    private AudioSource audioSource = null;
    protected GameObject gameObj = null;
    private Task<QRCodeWatcherAccessStatus> capabilityTask;
    private QRCodeWatcherAccessStatus accessStatus;
    private QRCodeWatcher qrTracker;
    private bool isSupported;
    private bool capabilityInitialized;
    private bool isTrackerRunning;
    private ConcurrentQueue<string> qrCodes = new ConcurrentQueue<string>();
    private List<string> msgs = new List<string>();

    private string Text
    {
        get => text.text;
        set => text.text = value;
    }

    public void WriteLine(string line)
    {
        msgs.Add(line);
        while (msgs.Count > 10)
        {
            msgs.RemoveAt(0);
        }
        Text = string.Join("\n", msgs);
    }

    public IEnumerator Start()
    {
        Application.logMessageReceived += OnLogMessage;

        accessStatus = QRCodeWatcherAccessStatus.NotDeclaredByApp;
        isSupported = QRCodeWatcher.IsSupported();
        WriteLine($"QRCodeWatcher.IsSupported = {isSupported}");

        WriteLine($"RequestingAccess...");
        QRCodeWatcher.RequestAccessAsync().ContinueWith(t => accessStatus = t.Result);
        capabilityInitialized = true;

        // get QRCode
        while (accessStatus != QRCodeWatcherAccessStatus.Allowed)
        {
            yield return null;
        }

        WriteLine($"Access granted.");

        WriteLine($"Setting up Tracking...");
        SetupQRTracking();

        WriteLine($"Starting Tracking...");
        StartQRTracking();

        WriteLine($"Waiting for QR Code...");

        while (true)
        {
            // wait for QR Code
            foreach (var qr in qrTracker.GetList())
            {
                var qrCodeData = qr.Data;

                var elapsed = DateTime.Now - qr.LastDetectedTime;
                if (elapsed > TimeSpan.FromSeconds(30))
                {
                    continue;
                }

                WriteLine($"QRCode={qrCodeData}");

                // connect
                try
                {
                    if (ConnectToUrl(qrCodeData))
                    {
                        
                        yield break;
                    }
                }
                catch
                {
                }
            }

            yield return null;
        }
    }

    public string debugUrl = "http://192.168.1.158:3000/channel/screenShare/#screenShare";

    [ContextMenu("Connect")]
    public void Connect()
    {
        ConnectToUrl(debugUrl);
    }

    private bool ConnectToUrl(string url)
    {
        var uri = new Uri(url);
        if (!uri.IsWellFormedOriginalString() || string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        // spawn prefab
        if (gameObj == null)
        {
            gameObj = Instantiate(prefab, parent);
        }

        audioSource = gameObj.GetComponentInChildren<AudioSource>();

        var serverAddress = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        var streamName = uri.Fragment.TrimStart('#');

        WriteLine($"Connecting to server = {serverAddress}");
        WriteLine($"StreamName = {streamName}");

        WebRtcServer.Instance.ServerAddress = serverAddress;
        WebRtcServer.Instance.ConsumeWebRtcStream(streamName, gameObj);
        return true;
    }

    private void OnLogMessage(string text, string stackTrace, LogType type)
    {
        WriteLine($"{type}: {text.Substring(0, Math.Min(400, text.Length))}");
        System.Diagnostics.Debug.WriteLine($"{type}: {text}");
    }

    private void SetupQRTracking()
    {
        try
        {
            qrTracker = new QRCodeWatcher();
            isTrackerRunning = false;
        }
        catch (Exception ex)
        {
            Debug.Log("QRCodesManager : exception starting the tracker " + ex.ToString());
        }
    }

    public void StartQRTracking()
    {
        if (qrTracker != null && !isTrackerRunning)
        {
            Debug.Log("QRCodesManager starting QRCodeWatcher");
            try
            {
                qrTracker.Start();
                isTrackerRunning = true;
            }
            catch (Exception ex)
            {
                Debug.Log("QRCodesManager starting QRCodeWatcher Exception:" + ex.ToString());
            }
        }
    }

    public void StopQRTracking()
    {
        if (isTrackerRunning)
        {
            isTrackerRunning = false;
            if (qrTracker != null)
            {
                qrTracker.Stop();
            }
        }
    }

    private void UpdateState()
    {
        if (audioSource != null && audioSource.enabled != enableAudio)
        {
            audioSource.enabled = enableAudio;
        }
    }
}
