﻿using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OCPP.Core.Server.Messages_OCPP20;

namespace OCPP.Core.Server
{
    public partial class OCPPMiddleware
    {
        /// <summary>
        /// Waits for new OCPP V2.0 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task Receive20(ChargePointStatus chargePointStatus, HttpContext context)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP20");
            ControllerOCPP20 controller20 = new ControllerOCPP20(_configuration, _logFactory, chargePointStatus);

            byte[] buffer = new byte[1024 * 4];
            MemoryStream memStream = new MemoryStream(buffer.Length);

            while (chargePointStatus.WebSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await chargePointStatus.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result != null && result.MessageType != WebSocketMessageType.Close)
                {
                    logger.LogTrace("OCPPMiddleware.Receive20 => Receiving segment: {0} bytes (EndOfMessage={1} / MsgType={2})", result.Count, result.EndOfMessage, result.MessageType);
                    memStream.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        // read complete message into byte array
                        byte[] bMessage = memStream.ToArray();
                        // reset memory stream für next message
                        memStream = new MemoryStream(buffer.Length);

                        string dumpDir = _configuration.GetValue<string>("MessageDumpDir");
                        if (!string.IsNullOrWhiteSpace(dumpDir))
                        {
                            string path = Path.Combine(dumpDir, string.Format("{0}_ocpp20-in.txt", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-ffff")));
                            try
                            {
                                // Write incoming message into dump directory
                                File.WriteAllBytes(path, bMessage);
                            }
                            catch(Exception exp)
                            {
                                logger.LogError(exp, "OCPPMiddleware.Receive20 => Error dumping incoming message to path: '{0}'", path);
                            }
                        }

                        string ocppMessage = UTF8Encoding.UTF8.GetString(bMessage);

                        Match match = Regex.Match(ocppMessage, MessageRegExp);
                        if (match != null && match.Groups != null && match.Groups.Count >= 3)
                        {
                            string messageTypeId = match.Groups[1].Value;
                            string uniqueId = match.Groups[2].Value;
                            string action = match.Groups[3].Value;
                            string jsonPaylod = match.Groups[4].Value;
                            logger.LogInformation("OCPPMiddleware.Receive20 => OCPP-Message: Type={0} / ID={1} / Action={2})", messageTypeId, uniqueId, action);

                            string[] urlParts = context.Request.Path.Value.Split('/');
                            string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : null;
                            string urlChargeTagId = (urlParts.Length >= 6) ? urlParts[5] : null;

                            OCPPMessage msgIn = new OCPPMessage(chargePointStatus.Id, urlConnectorId, urlChargeTagId, messageTypeId, uniqueId, action, jsonPaylod);
                            if (msgIn.MessageType == "2")
                            {
                                // Request from chargepoint to OCPP server
                                OCPPMessage msgOut = controller20.ProcessRequest(msgIn);

                                // Send OCPP message with optional logging/dump
                                await SendOcpp20Message(msgOut, logger, chargePointStatus.WebSocket);
                            }
                            else if (msgIn.MessageType == "3" || msgIn.MessageType == "4")
                            {
                                // Process answer from chargepoint
                                if (_requestQueue.ContainsKey(msgIn.UniqueId))
                                {
                                    controller20.ProcessAnswer(msgIn, _requestQueue[msgIn.UniqueId]);
                                    _requestQueue.Remove(msgIn.UniqueId);
                                }
                                else
                                {
                                    logger.LogError("OCPPMiddleware.Receive20 => HttpContext from caller not found / Msg: {0}", ocppMessage);
                                }
                            }
                            else
                            {
                                // Unknown message type
                                logger.LogError("OCPPMiddleware.Receive20 => Unknown message type: {0} / Msg: {1}", msgIn.MessageType, ocppMessage);
                            }
                        }
                        else
                        {
                            logger.LogWarning("OCPPMiddleware.Receive20 => Error in RegEx-Matching: Msg={0})", ocppMessage);
                        }
                    }
                }
                else
                {
                    logger.LogInformation("OCPPMiddleware.Receive20 => Receive: unexpected result: CloseStatus={0} / MessageType={1}", result?.CloseStatus, result?.MessageType);
                    await chargePointStatus.WebSocket.CloseOutputAsync((WebSocketCloseStatus)3001, string.Empty, CancellationToken.None);
                }
            }
            logger.LogInformation("OCPPMiddleware.Receive20 => Websocket closed: State={0} / CloseStatus={1}", chargePointStatus.WebSocket.State, chargePointStatus.WebSocket.CloseStatus);
            ChargePointStatus dummy;
            _chargePointStatusDict.Remove(chargePointStatus.Id, out dummy);
        }

        /// <summary>
        /// Sends a (Soft-)Reset to the chargepoint
        /// </summary>
        private async Task Reset20(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP20");
            ControllerOCPP20 controller20 = new ControllerOCPP20(_configuration, _logFactory, chargePointStatus);

            Messages_OCPP20.ResetRequest resetRequest = new Messages_OCPP20.ResetRequest();
            resetRequest.Type = Messages_OCPP20.ResetEnumType.OnIdle;
            resetRequest.CustomData = new CustomDataType();
            resetRequest.CustomData.VendorId = ControllerOCPP20.VendorId;

            string jsonResetRequest = JsonConvert.SerializeObject(resetRequest);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "Reset";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp20Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        /// <summary>
        /// Sends a Unlock-Request to the chargepoint
        /// </summary>
        private async Task UnlockConnector20(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP20");
            ControllerOCPP20 controller20 = new ControllerOCPP20(_configuration, _logFactory, chargePointStatus);

            Messages_OCPP20.UnlockConnectorRequest unlockConnectorRequest = new Messages_OCPP20.UnlockConnectorRequest();
            unlockConnectorRequest.EvseId = 0;
            unlockConnectorRequest.CustomData = new CustomDataType();
            unlockConnectorRequest.CustomData.VendorId = ControllerOCPP20.VendorId;

            string jsonResetRequest = JsonConvert.SerializeObject(unlockConnectorRequest);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "UnlockConnector";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp20Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        private async Task SendOcpp20Message(OCPPMessage msg, ILogger logger, WebSocket webSocket)
        {
            string ocppTextMessage = null;

            if (string.IsNullOrEmpty(msg.ErrorCode))
            {
                if (msg.MessageType == "2")
                {
                    // OCPP-Request
                    ocppTextMessage = string.Format("[{0},\"{1}\",\"{2}\",{3}]", msg.MessageType, msg.UniqueId, msg.Action, msg.JsonPayload);
                }
                else
                {
                    // OCPP-Response
                    ocppTextMessage = string.Format("[{0},\"{1}\",{2}]", msg.MessageType, msg.UniqueId, msg.JsonPayload);
                }
            }
            else
            {
                ocppTextMessage = string.Format("[{0},\"{1}\",\"{2}\",\"{3}\",{4}]", msg.MessageType, msg.UniqueId, msg.ErrorCode, msg.ErrorDescription, "{}");
            }
            logger.LogTrace("OCPPMiddleware.OCPP20 => SendOcppMessage: {0}", ocppTextMessage);

            if (string.IsNullOrEmpty(ocppTextMessage))
            {
                // invalid message
                ocppTextMessage = string.Format("[{0},\"{1}\",\"{2}\",\"{3}\",{4}]", "4", string.Empty, Messages_OCPP20.ErrorCodes.ProtocolError, string.Empty, "{}");
            }

            string dumpDir = _configuration.GetValue<string>("MessageDumpDir");
            if (!string.IsNullOrWhiteSpace(dumpDir))
            {
                // Write outgoing message into dump directory
                string path = Path.Combine(dumpDir, string.Format("{0}_ocpp20-out.txt", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-ffff")));
                try
                {
                    File.WriteAllText(path, ocppTextMessage);
                }
                catch (Exception exp)
                {
                    logger.LogError(exp, "OCPPMiddleware.SendOcpp20Message=> Error dumping message to path: '{0}'", path);
                }
            }

            byte[] binaryMessage = UTF8Encoding.UTF8.GetBytes(ocppTextMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(binaryMessage, 0, binaryMessage.Length), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
