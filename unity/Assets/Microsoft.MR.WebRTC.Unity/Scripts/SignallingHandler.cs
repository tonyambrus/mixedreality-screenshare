// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

// Uncomment the following to log SDP messages to  file
//#define LOG_SDP_MESSAGES

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace WebRTC
{
    public class SignallingHandler
    {
        private string localId = null;
        private string remotePeerId = null;
        
        private Microsoft.MixedReality.WebRTC.PeerConnection _nativePeer = null;

        private WebRtcServerConnection connection = null;

#if LOG_SDP_MESSAGES
        // NOTE: You may need change this path to get SDP message logging to work
        private const string SDPMessageLogBasePath = @"C:\Projects\Chaos\GatoConBotas\MediaServer\SDP-Log";

        private enum SignalDirection
        {
            Unknown = 0,
            ToPeer = 1,
            FromPeer = 2
        };
#endif

        [Serializable]
        public class Message
        {
            /// <summary>
            /// Possible message types as-serialized on the wire
            /// </summary>
            public enum WireMessageType
            {
                Unknown = 0,
                // An SDP message initializing a webrtc connection.
                Offer,
                // An SDP mesage finalizing a webrtc connection.
                Answer,
                // ICE message for NAT handling.
                Ice,
                // A request to a known server for a connection.
                Request,
                // A response from a known server with a unique connection id.
                Response,
            }

            /// <summary>
            /// Convert a message type from <see xref="string"/> to <see cref="WireMessageType"/>.
            /// </summary>
            /// <param name="stringType">The message type as <see xref="string"/>.</param>
            /// <returns>The message type as a <see cref="WireMessageType"/> object.</returns>
            public static WireMessageType WireMessageTypeFromString(string stringType)
            {
                if (string.Equals(stringType, "offer", StringComparison.OrdinalIgnoreCase))
                {
                    return WireMessageType.Offer;
                }
                else if (string.Equals(stringType, "answer", StringComparison.OrdinalIgnoreCase))
                {
                    return WireMessageType.Answer;
                }
                throw new ArgumentException($"Unkown signaler message type '{stringType}'");
            }

            /// <summary>
            /// The message type
            /// </summary>
            public WireMessageType MessageType;

            /// <summary>
            /// The primary message contents
            /// </summary>
            public string Data;

            /// <summary>
            /// The data separator needed for proper ICE serialization
            /// </summary>
            public string IceDataSeparator;
        }

        public SignallingHandler(PeerConnection peer, string serverAddress, string localId, string remoteId, float pollTimeMs = 500)
        {
            if (string.IsNullOrEmpty(remoteId))
            {
                throw new ArgumentException(nameof(remoteId));
            }

            if (string.IsNullOrEmpty(localId))
            {
                throw new ArgumentException(nameof(localId));
            }

            if (peer == null)
            {
                throw new ArgumentNullException("peer");
            }

            this.localId = localId;
            this.remotePeerId = remoteId;

            connection = new WebRtcServerConnection(serverAddress, pollTimeMs, $"data/{localId}", peer);
            connection.OnPolledMessageReceived += OnMessageReceived;

            this.PeerConnection = peer;
            _nativePeer = peer.Peer;
        }

        public PeerConnection PeerConnection { get; private set; }

        public void Update()
        {
            connection.Update();
        }

        /// <summary>
        /// Callback fired when an ICE candidate message has been generated and is ready to
        /// be sent to the remote peer by the signaling object.
        /// </summary>
        public void SendIceCandidateMessage(string candidate, int sdpMlineIndex, string sdpMid)
        {
            SendMessageAsync(new Message()
            {
                MessageType = Message.WireMessageType.Ice,
                Data = $"{candidate}|{sdpMlineIndex}|{sdpMid}",
                IceDataSeparator = "|"
            });
        }

        /// <summary>
        /// Callback fired when a local SDP offer has been generated and is ready to
        /// be sent to the remote peer by the signaling object.
        /// </summary>
        public void SendSdpMessage(string type, string sdp)
        {
            Debug.Log($"Sending SDP Message: {sdp}");
            SendMessageAsync(new Message()
            {
                MessageType = Message.WireMessageTypeFromString(type),
                Data = sdp
            });
        }

        private Task SendMessageAsync(Message msg)
        {
#if LOG_SDP_MESSAGES
            LogSDPMessageToFile(msg, SignalDirection.ToPeer, SDPMessageLogBasePath);
#endif
            byte[] data = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg));
            return connection.SendMessageAsync($"data/{localId}/{remotePeerId}", data, "application/json");
        }

        private void OnMessageReceived(string json)
        {
#if LOG_SDP_MESSAGES
            LogSDPMessageToFile(json, SignalDirection.FromPeer, SDPMessageLogBasePath);
#endif
            var msg = JsonUtility.FromJson<Message>(json);

            // if the message is good
            if (msg != null)
            {
                // depending on what type of message we get, we'll handle it differently
                // this is the "glue" that allows two peers to establish a connection.
                switch (msg.MessageType)
                {
                    case Message.WireMessageType.Offer:
                        Debug.Log($"Got SDP offer: {msg.Data}");
                        _nativePeer.SetRemoteDescription("offer", msg.Data);
                        // if we get an offer, we immediately send an answer
                        _nativePeer.CreateAnswer();
                        break;
                    case Message.WireMessageType.Answer:
                        Debug.Log($"Got SDP answer: {msg.Data}");
                        _nativePeer.SetRemoteDescription("answer", msg.Data);
                        break;
                    case Message.WireMessageType.Ice:
                        // this "parts" protocol is defined above, in OnIceCandiateReadyToSend listener
                        var parts = msg.Data.Split(new string[] { msg.IceDataSeparator }, StringSplitOptions.RemoveEmptyEntries);
                        // Note the inverted arguments; candidate is last here, but first in OnIceCandiateReadyToSend
                        _nativePeer.AddIceCandidate(parts[2], int.Parse(parts[1]), parts[0]);
                        break;
                    //case SignalerMessage.WireMessageType.SetPeer:
                    //    // this allows a remote peer to set our text target peer id
                    //    // it is primarily useful when one device does not support keyboard input
                    //    //
                    //    // note: when running this sample on HoloLens (for example) we may use postman or a similar
                    //    // tool to use this message type to set the target peer. This is NOT a production-quality solution.
                    //    TargetIdField.text = msg.Data;
                    //    break;
                    default:
                        Debug.Log("Unknown message: " + msg.MessageType + ": " + msg.Data);
                        break;
                }
            }
            else
            {
                Debug.LogError($"Failed to deserialize JSON message : {json}");
            }
        }

#if LOG_SDP_MESSAGES
        private static void LogSDPMessageToFile(string rawMessage, SignalDirection direction, string basePath)
        {
            string fromTo = direction == SignalDirection.FromPeer ? "Consumer" : "Producer";
            string destination = Path.Combine(basePath, string.Format("{0}-{1:d/M/yyyy HH:mm:ss}-message.txt", fromTo, DateTime.Now).Replace("/", ".").Replace(":", "."));
            System.IO.File.WriteAllText(destination, rawMessage);
        }

        private static void LogSDPMessageToFile(Message msg, SignalDirection direction, string basePath)
        {
            string fromTo = direction == SignalDirection.FromPeer ? "Consumer" : "Producer";
            string destination = string.Empty;

            switch (msg.MessageType)
            {
                case Message.WireMessageType.Offer:
                    destination = Path.Combine(basePath, string.Format("{0}-{1:d/M/yyyy HH:mm:ss}-offer.txt", fromTo, DateTime.Now).Replace("/", ".").Replace(":", "."));
                    break;
                case Message.WireMessageType.Answer:
                    destination = Path.Combine(basePath, string.Format("{0}-{1:d/M/yyyy HH:mm:ss}-answer.txt", fromTo, DateTime.Now).Replace("/", ".").Replace(":", "."));
                    break;
                case Message.WireMessageType.Ice:
                    destination = Path.Combine(basePath, string.Format("{0}-{1:d/M/yyyy HH:mm:ss}-ice.txt", fromTo, DateTime.Now).Replace("/", ".").Replace(":", "."));
                    break;
                case Message.WireMessageType.Request:
                    destination = Path.Combine(basePath, string.Format("{0}-{1:d/M/yyyy HH:mm:ss}-request.txt", fromTo, DateTime.Now).Replace("/", ".").Replace(":", "."));
                    break;
                case Message.WireMessageType.Response:
                    destination = Path.Combine(basePath, string.Format("{0}-{1:d/M/yyyy HH:mm:ss}-response.txt", fromTo, DateTime.Now).Replace("/", ".").Replace(":", "."));
                    break;
                default:
                    Debug.Log("Unknown message: " + msg.MessageType + ": " + msg.Data);
                    break;
            }

            if (!string.IsNullOrEmpty(destination))
            {
                System.IO.File.WriteAllText(destination, msg.Data);
            }
        }
#endif
    }
}
