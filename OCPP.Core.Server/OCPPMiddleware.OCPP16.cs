using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;
using OCPP.Core.Server.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OCPP.Core.Server
{
    public partial class OCPPMiddleware
    {
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task Receive16(ChargePointStatus chargePointStatus, HttpContext context)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            byte[] buffer = new byte[1024 * 4];
            MemoryStream memStream = new MemoryStream(buffer.Length);

            while (chargePointStatus.WebSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await chargePointStatus.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result != null && result.MessageType != WebSocketMessageType.Close)
                {
                    logger.LogTrace("OCPPMiddleware.Receive16 => Receiving segment: {0} bytes (EndOfMessage={1} / MsgType={2})", result.Count, result.EndOfMessage, result.MessageType);
                    memStream.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        // read complete message into byte array
                        byte[] bMessage = memStream.ToArray();
                        // reset memory stream für next message
                        memStream = new MemoryStream(buffer.Length);

                        string dumpDir = _configuration.GetValue<string>("MessageDumpDir");
                        //string pathDumpDir = ControllerContext.HttpContext.Server.MapPath(dumpDir);
                        if (!Directory.Exists(dumpDir)) Directory.CreateDirectory(dumpDir);
                        if (!string.IsNullOrWhiteSpace(dumpDir))
                        {
                            string path = Path.Combine(dumpDir, string.Format("{0}_ocpp16-in.txt", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-ffff")));
                            try
                            {
                                // Write incoming message into dump directory
                                File.WriteAllBytes(path, bMessage);
                            }
                            catch (Exception exp)
                            {
                                logger.LogError(exp, "OCPPMiddleware.Receive16 => Error dumping incoming message to path: '{0}'", path);
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
                            logger.LogInformation("OCPPMiddleware.Receive16 => OCPP-Message: Type={0} / ID={1} / Action={2})", messageTypeId, uniqueId, action);

                            string[] urlParts = context.Request.Path.Value.Split('/');
                            string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : null;
                            string urlChargeTagId = (urlParts.Length >= 6) ? urlParts[5] : null;

                            OCPPMessage msgIn = new OCPPMessage(chargePointStatus.Id, urlConnectorId, urlChargeTagId, messageTypeId, uniqueId, action, jsonPaylod);
                            if (msgIn.MessageType == "2")
                            {

                                // Request from chargepoint to OCPP server
                                OCPPMessage msgOut = controller16.ProcessRequest(msgIn);

                                // Send OCPP message with optional logging/dump
                                await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

                                //Auto start/stop
                                //if(msgIn.Action == "StatusNotification")
                                //{
                                //    StatusNotificationRequest statusNotificationRequest = JsonConvert.DeserializeObject<StatusNotificationRequest>(msgIn.JsonPayload);
                                //    if (statusNotificationRequest.Status == StatusNotificationRequestStatus.Preparing)
                                //        _ = RemoteStartTransaction16(chargePointStatus, msgIn);
                                //    else if(statusNotificationRequest.Status == StatusNotificationRequestStatus.Finishing)
                                //        _ = RemoteStopTransaction16(chargePointStatus, msgIn);
                                //}

                                if (msgIn.Action == "StatusNotification")
                                {
                                    try
                                    {
                                        StatusNotificationRequest statusNotificationRequest = JsonConvert.DeserializeObject<StatusNotificationRequest>(msgIn.JsonPayload);
                                        ConnectorStatusEnum newStatus = ConnectorStatusEnum.Undefined;
                                        switch (statusNotificationRequest.Status)
                                        {
                                            case StatusNotificationRequestStatus.Available:
                                                newStatus = ConnectorStatusEnum.Available;
                                                break;
                                            case StatusNotificationRequestStatus.Preparing:
                                                newStatus = ConnectorStatusEnum.Preparing;
                                                break;
                                            case StatusNotificationRequestStatus.Charging:
                                                newStatus = ConnectorStatusEnum.Charging;
                                                break;
                                            case StatusNotificationRequestStatus.Reserved:
                                                newStatus = ConnectorStatusEnum.Reserved;
                                                break;
                                            case StatusNotificationRequestStatus.SuspendedEVSE:
                                            case StatusNotificationRequestStatus.SuspendedEV:
                                                newStatus = ConnectorStatusEnum.Occupied;
                                                break;
                                            case StatusNotificationRequestStatus.Finishing:
                                                newStatus = ConnectorStatusEnum.Finishing;
                                                break;
                                            case StatusNotificationRequestStatus.Unavailable:
                                                newStatus = ConnectorStatusEnum.Unavailable;
                                                break;
                                            case StatusNotificationRequestStatus.Faulted:
                                                newStatus = ConnectorStatusEnum.Faulted;
                                                break;
                                        }
                                        _chargePointStatusDict[msgIn.ChargePointId].OnlineConnectors[statusNotificationRequest.ConnectorId].Status = newStatus;
                                    }
                                    catch (Exception ex) { logger.LogError(ex.Message); }
                                }
                                else if (msgIn.Action == "MeterValues")
                                {
                                    MeterValuesRequest meterValueRequest = JsonConvert.DeserializeObject<MeterValuesRequest>(msgIn.JsonPayload);

                                    using (OCPPCoreContext dbContext = new OCPPCoreContext(_configuration))
                                    {
                                        ConnectorStatus connectorStatus = dbContext.Find<ConnectorStatus>(msgIn.ChargePointId, meterValueRequest.ConnectorId);
                                        if (connectorStatus != null)
                                        {
                                            _chargePointStatusDict[msgIn.ChargePointId].OnlineConnectors[meterValueRequest.ConnectorId].ChargeRateKW = connectorStatus.CurrentChargeKW;
                                            _chargePointStatusDict[msgIn.ChargePointId].OnlineConnectors[meterValueRequest.ConnectorId].MeterKWH = connectorStatus.LastMeter;
                                            _chargePointStatusDict[msgIn.ChargePointId].OnlineConnectors[meterValueRequest.ConnectorId].SoC = connectorStatus.StateOfCharge;
                                        }
                                    }
                                }
                            }
                            else if (msgIn.MessageType == "3" || msgIn.MessageType == "4")
                            {
                                // Process answer from chargepoint
                                if (_requestQueue.ContainsKey(msgIn.UniqueId))
                                {
                                    controller16.ProcessAnswer(msgIn, _requestQueue[msgIn.UniqueId]);
                                    _requestQueue.Remove(msgIn.UniqueId);
                                }
                                else
                                {
                                    logger.LogError("OCPPMiddleware.Receive16 => HttpContext from caller not found / Msg: {0}", ocppMessage);
                                }


                            }
                            else
                            {
                                // Unknown message type
                                logger.LogError("OCPPMiddleware.Receive16 => Unknown message type: {0} / Msg: {1}", msgIn.MessageType, ocppMessage);
                            }
                        }
                        else
                        {
                            logger.LogWarning("OCPPMiddleware.Receive16 => Error in RegEx-Matching: Msg={0})", ocppMessage);
                        }
                    }
                }
                else
                {
                    logger.LogInformation("OCPPMiddleware.Receive16 => WebSocket Closed: CloseStatus={0} / MessageType={1}", result?.CloseStatus, result?.MessageType);
                    await chargePointStatus.WebSocket.CloseOutputAsync((WebSocketCloseStatus)3001, string.Empty, CancellationToken.None);
                }
            }
            logger.LogInformation("OCPPMiddleware.Receive16 => Websocket closed: State={0} / CloseStatus={1}", chargePointStatus.WebSocket.State, chargePointStatus.WebSocket.CloseStatus);
            //ChargePointStatus dummy;
            //_chargePointStatusDict.Remove(chargePointStatus.Id, out dummy);
        }

        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task RemoteStartTransaction16(ChargePointStatus chargePointStatus, OCPPMessage msgIn)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            msgIn.Action = "RemoteStartTransaction";

            OCPPMessage msgOut = new OCPPMessage();
            // Request from chargepoint to OCPP server
            msgOut = controller16.ProcessRequest(msgIn);

            if (msgOut != null)
            {
                msgOut.MessageType = "2";
                msgOut.Action = "RemoteStartTransaction";
                msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

                // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
                _requestQueue.Add(msgOut.UniqueId, msgOut);

                // Send OCPP message with optional logging/dump
                await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

                // Wait for asynchronous chargepoint response and processing
                string apiResult = await msgOut.TaskCompletionSource.Task;

                // 
            }
        }
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task RemoteStopTransaction16(ChargePointStatus chargePointStatus, OCPPMessage msgIn)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            msgIn.Action = "RemoteStopTransaction";

            OCPPMessage msgOut = new OCPPMessage();
            // Request from chargepoint to OCPP server
            msgOut = controller16.ProcessRequest(msgIn);

            if (msgOut != null)
            {
                msgOut.MessageType = "2";
                msgOut.Action = "RemoteStopTransaction";
                msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

                // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
                _requestQueue.Add(msgOut.UniqueId, msgOut);

                // Send OCPP message with optional logging/dump
                await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

                // Wait for asynchronous chargepoint response and processing
                string apiResult = await msgOut.TaskCompletionSource.Task;

                // 
            }
        }
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task Reset16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            ResetRequest request = new ResetRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<ResetRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                //string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                //apiCallerContext.Response.StatusCode = 200;
                //apiCallerContext.Response.ContentType = "application/json";
                //await apiCallerContext.Response.WriteAsync(_apiResult);
                //return;
                request = new ResetRequest();
                request.Type = ResetRequestType.Soft;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "Reset";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task ChangeAvailability16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            ChangeAvailabilityRequest request = new ChangeAvailabilityRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<ChangeAvailabilityRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                //string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                //apiCallerContext.Response.StatusCode = 200;
                //apiCallerContext.Response.ContentType = "application/json";
                //await apiCallerContext.Response.WriteAsync(_apiResult);
                //return;
                string[] urlParts = apiCallerContext.Request.Path.Value.Split('/');
                string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : "0";

                request = new ChangeAvailabilityRequest();
                request.Type = ChangeAvailabilityRequestType.Operative.ToString();
                request.ConnectorId = Convert.ToInt32(urlConnectorId);
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "ChangeAvailability";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task GetConfiguration16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            GetConfigurationRequest request = new GetConfigurationRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<GetConfigurationRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "GetConfiguration";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task GetDiagnostics16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            GetDiagnosticsRequest request = new GetDiagnosticsRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<GetDiagnosticsRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "GetDiagnostics";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task GetLocalListVersion16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            GetLocalListVersionRequest request = new GetLocalListVersionRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<GetLocalListVersionRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "GetLocalListVersion";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task SendLocalList16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            SendLocalListRequest request = new SendLocalListRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<SendLocalListRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "SendLocalList";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task SetChargingProfile16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            SetChargingProfileRequest request = new SetChargingProfileRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<SetChargingProfileRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "SetChargingProfile";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task UpdateFirmware16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            UpdateFirmwareRequest request = new UpdateFirmwareRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<UpdateFirmwareRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "UpdateFirmware";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task GetCompositeSchedule16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            GetCompositeScheduleRequest request = new GetCompositeScheduleRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<GetCompositeScheduleRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "GetCompositeSchedule";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task ChangeConfiguration16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            ChangeConfigurationRequest request = new ChangeConfigurationRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<ChangeConfigurationRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "ChangeConfiguration";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task ClearChargingProfile16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            ClearChargingProfileRequest request = new ClearChargingProfileRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<ClearChargingProfileRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "ClearChargingProfile";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }
        /// <summary>
        /// Waits for new OCPP V1.6 messages on the open websocket connection and delegates processing to a controller
        /// </summary>
        private async Task ClearCache16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.ResetRequest resetRequest = new Messages_OCPP16.ResetRequest();
            //resetRequest.Type = Messages_OCPP16.ResetRequestType.Soft;
            ClearCacheRequest request = new ClearCacheRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<ClearCacheRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                //string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                //apiCallerContext.Response.StatusCode = 200;
                //apiCallerContext.Response.ContentType = "application/json";
                //await apiCallerContext.Response.WriteAsync(_apiResult);
                //return;
                request = new ClearCacheRequest();
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "ClearCache";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

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
        private async Task UnlockConnector16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            //ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            //Messages_OCPP16.UnlockConnectorRequest unlockConnectorRequest = new Messages_OCPP16.UnlockConnectorRequest();
            //unlockConnectorRequest.ConnectorId = 0;
            UnlockConnectorRequest request = new UnlockConnectorRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<UnlockConnectorRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                //string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                //apiCallerContext.Response.StatusCode = 200;
                //apiCallerContext.Response.ContentType = "application/json";
                //await apiCallerContext.Response.WriteAsync(_apiResult);
                //return;
                string[] urlParts = apiCallerContext.Request.Path.Value.Split('/');
                string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : "0";
                request = new UnlockConnectorRequest();
                request.ConnectorId = Convert.ToInt32(urlConnectorId);
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "UnlockConnector";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        /// <summary>
        /// Sends a Reserve to the chargepoint
        /// </summary>
        private async Task Reserve16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

            DateTime now = DateTime.Now;
            ReserveNowRequest request = new ReserveNowRequest();
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                logger.LogDebug(requestBodyText);

                request = JsonConvert.DeserializeObject<ReserveNowRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            using (OCPPCoreContext dbContext = new OCPPCoreContext(_configuration))
            {

                //Reservation _reservation = dbContext.Reservations.Where(x => x.ChargePointId == chargePointStatus.Id && x.ConnectorId == request.ConnectorId && x.Status == false).FirstOrDefault();
                //if (_reservation != null)
                //{
                //    // Wait for asynchronous chargepoint response and processing
                //    string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";

                //    // 
                //    apiCallerContext.Response.StatusCode = 200;
                //    apiCallerContext.Response.ContentType = "application/json";
                //    await apiCallerContext.Response.WriteAsync(_apiResult);
                //    return;
                //}

                Reservation reservation = new Reservation();
                reservation.ChargePointId = chargePointStatus.Id;
                reservation.ConnectorId = request.ConnectorId;
                reservation.ReservationTime = now;
                reservation.ReservationExpiryTime = now.AddMinutes(15.00);
                reservation.TagId = request.IdTag;

                dbContext.Add<Reservation>(reservation);
                dbContext.SaveChanges();

                request.ExpiryDate = reservation.ReservationExpiryTime;
                request.ReservationId = reservation.ReservationID;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "ReserveNow";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }
        //private async Task Reserve16(ChargePointStatus chargePointStatus, int connectorId, string chargeTagID, HttpContext apiCallerContext)
        //{
        //    ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
        //    ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

        //    DateTime now = DateTime.UtcNow;
        //    int reservationID = 0;
        //    using (OCPPCoreContext dbContext = new OCPPCoreContext(_configuration))
        //    {

        //        Reservation _reservation = dbContext.Reservations.Where(x => x.ChargePointId == chargePointStatus.Id && x.ConnectorId == connectorId && x.Status == false).FirstOrDefault();
        //        if(_reservation != null)
        //        {
        //            // Wait for asynchronous chargepoint response and processing
        //            string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";

        //            // 
        //            apiCallerContext.Response.StatusCode = 200;
        //            apiCallerContext.Response.ContentType = "application/json";
        //            await apiCallerContext.Response.WriteAsync(_apiResult);
        //            return;
        //        }

        //        Reservation reservation = new Reservation();
        //        reservation.ChargePointId = chargePointStatus.Id;
        //        reservation.ConnectorId = connectorId;
        //        reservation.ReservationTime = now;
        //        reservation.ReservationExpiryTime = now.AddMinutes(15.00);
        //        reservation.TagId = chargeTagID;

        //        dbContext.Add<Reservation>(reservation);
        //        dbContext.SaveChanges();

        //        reservationID = reservation.ReservationID;
        //    }
        //    ReserveNowRequest reserveRequest = new ReserveNowRequest();
        //    reserveRequest.ConnectorId = connectorId;
        //    reserveRequest.IdTag = chargeTagID;
        //    reserveRequest.ExpiryDate = now.AddMinutes(15.00);
        //    reserveRequest.ReservationId = reservationID;

        //    string jsonResetRequest = JsonConvert.SerializeObject(reserveRequest);

        //    OCPPMessage msgOut = new OCPPMessage();
        //    msgOut.MessageType = "2";
        //    msgOut.Action = "ReserveNow";
        //    msgOut.UniqueId = Guid.NewGuid().ToString("N");
        //    msgOut.JsonPayload = jsonResetRequest;
        //    msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

        //    // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
        //    _requestQueue.Add(msgOut.UniqueId, msgOut);

        //    // Send OCPP message with optional logging/dump
        //    await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

        //    // Wait for asynchronous chargepoint response and processing
        //    string apiResult = await msgOut.TaskCompletionSource.Task;

        //    // 
        //    apiCallerContext.Response.StatusCode = 200;
        //    apiCallerContext.Response.ContentType = "application/json";
        //    await apiCallerContext.Response.WriteAsync(apiResult);
        //}

        /// <summary>
        /// Sends a CancelReservation to the chargepoint
        /// </summary>
        public async Task CancelReservation16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");

            CancelReservationRequest request = new CancelReservationRequest();

            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                request = JsonConvert.DeserializeObject<CancelReservationRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (request == null)
            {
                string _apiResult = "{\"status\": " + JsonConvert.ToString("Faulted") + "}";
                apiCallerContext.Response.StatusCode = 200;
                apiCallerContext.Response.ContentType = "application/json";
                await apiCallerContext.Response.WriteAsync(_apiResult);
                return;
            }

            string jsonResetRequest = JsonConvert.SerializeObject(request);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "CancelReservation";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }
        //public async Task CancelReservation16(ChargePointStatus chargePointStatus, int reservationID, HttpContext apiCallerContext)
        //{
        //    ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
        //    ControllerOCPP16 controller16 = new ControllerOCPP16(_configuration, _logFactory, chargePointStatus);

        //    using (OCPPCoreContext dbContext = new OCPPCoreContext(_configuration))
        //    {
        //        Reservation reservation = dbContext.Find<Reservation>(reservationID);
        //        reservation.Status = true;
        //        reservation.StatusReason = "CancelReservation";
        //        dbContext.Update<Reservation>(reservation);
        //        dbContext.SaveChanges();
        //    }
        //    CancelReservationRequest cancelReservationRequest = new CancelReservationRequest();
        //    cancelReservationRequest.ReservationId = reservationID.ToString();

        //    string jsonResetRequest = JsonConvert.SerializeObject(cancelReservationRequest);

        //    OCPPMessage msgOut = new OCPPMessage();
        //    msgOut.MessageType = "2";
        //    msgOut.Action = "CancelReservation";
        //    msgOut.UniqueId = Guid.NewGuid().ToString("N");
        //    msgOut.JsonPayload = jsonResetRequest;
        //    msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

        //    // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
        //    _requestQueue.Add(msgOut.UniqueId, msgOut);

        //    // Send OCPP message with optional logging/dump
        //    await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

        //    // Wait for asynchronous chargepoint response and processing
        //    string apiResult = await msgOut.TaskCompletionSource.Task;

        //    // 
        //    apiCallerContext.Response.StatusCode = 200;
        //    apiCallerContext.Response.ContentType = "application/json";
        //    await apiCallerContext.Response.WriteAsync(apiResult);
        //}

        /// <summary>
        /// Sends a RemoteStartTransaction16 to the chargepoint
        /// </summary>
        private async Task RemoteStartTransaction16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            RemoteStartTransactionRequest remoteStartTransactionRequest = new RemoteStartTransactionRequest();
            string apiResult = string.Empty;
            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                remoteStartTransactionRequest = JsonConvert.DeserializeObject<RemoteStartTransactionRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }

            if (remoteStartTransactionRequest == null)
            {
                string[] urlParts = apiCallerContext.Request.Path.Value.Split('/');
                string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : "0";

                remoteStartTransactionRequest = new RemoteStartTransactionRequest();
                remoteStartTransactionRequest.ConnectorId = Convert.ToInt32(urlConnectorId);
                remoteStartTransactionRequest.IdTag = _configuration.GetSection("TagIDTest").Value; //chargeTags.TagId;
                remoteStartTransactionRequest.ChargingProfile = new ChargingProfile();
            }


            ChargingProfile chargingProfile = new ChargingProfile();
            chargingProfile.ChargingProfileId = 158798;
            chargingProfile.RecurrencyKind = RecurrencyKind.Daily;
            chargingProfile.Kind = ChargingProfileKind.Absolute;
            chargingProfile.Purpose = ChargingProfilePurpose.TxProfile;
            chargingProfile.ChargingSchedule = new ChargingSchedule();
            chargingProfile.ChargingSchedule.ChargingRateUnit = ChargingRateUnit.W;
            chargingProfile.ChargingSchedule.ChargingSchedulePeriod = new List<ChargingSchedulePeriod>();
            List<ChargingSchedulePeriod> chargingSchedulePeriod = new List<ChargingSchedulePeriod>();
            chargingSchedulePeriod.Add(new ChargingSchedulePeriod() { StartPeriod = 0, Limit = 1100.0 });
            chargingSchedulePeriod.Add(new ChargingSchedulePeriod() { StartPeriod = 780, Limit = 9000.0 });
            chargingSchedulePeriod.Add(new ChargingSchedulePeriod() { StartPeriod = 1680, Limit = 4500.0 });
            chargingProfile.ChargingSchedule.ChargingSchedulePeriod = chargingSchedulePeriod;
            chargingProfile.ChargingSchedule.Duration = 1680;
            chargingProfile.StackLevel = 0;
            //chargingProfile.TransactionId = transaction.TransactionId;
            remoteStartTransactionRequest.ChargingProfile = chargingProfile;

            string jsonResetRequest = JsonConvert.SerializeObject(remoteStartTransactionRequest);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "RemoteStartTransaction";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();


            logger.LogInformation("RemoteStartTransaction => Save ConnectorStatus: ID={0} / Connector={1} ", chargePointStatus.Id, remoteStartTransactionRequest.ConnectorId);


            logger.LogTrace("RemoteStartTransaction => Response serialized Data:" + msgOut.JsonPayload);

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            apiResult = await msgOut.TaskCompletionSource.Task;
            //}
            //else
            //{
            //    apiResult = "{\"status\": " + JsonConvert.ToString("Rejected") + "}";
            //    logger.LogTrace("HandleReset => API response: {0}", apiResult);
            //}

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";

            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        /// <summary>
        /// Sends a RemoteStopTransaction16 to the chargepoint
        /// </summary>
        private async Task RemoteStopTransaction16(ChargePointStatus chargePointStatus, HttpContext apiCallerContext)
        {
            ILogger logger = _logFactory.CreateLogger("OCPPMiddleware.OCPP16");
            RemoteStopTransactionRequest remoteStopTransactionRequest = new RemoteStopTransactionRequest();

            try
            {
                if (apiCallerContext.Request.Body.CanSeek)
                {
                    // Reset the position to zero to read from the beginning.
                    apiCallerContext.Request.Body.Position = 0;
                }

                var requestBodyText = new StreamReader(apiCallerContext.Request.Body).ReadToEnd();
                remoteStopTransactionRequest = JsonConvert.DeserializeObject<RemoteStopTransactionRequest>(requestBodyText);

            }
            catch (Exception ex)
            {
                Console.WriteLine("Can't rewind body stream. " + ex.Message);
            }
            if (remoteStopTransactionRequest == null)
            {
                string[] urlParts = apiCallerContext.Request.Path.Value.Split('/');
                string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : "0";

                remoteStopTransactionRequest = new RemoteStopTransactionRequest();

                using (OCPPCoreContext dbContext = new OCPPCoreContext(_configuration))
                {
                    Transaction chargeTags = dbContext.Transactions.Where(x => x.ChargePointId == chargePointStatus.Id && x.ConnectorId.ToString() == urlConnectorId && x.StopTime == null).FirstOrDefault();
                    remoteStopTransactionRequest.TransactionId = chargeTags.TransactionId;
                }
            }
            string jsonResetRequest = JsonConvert.SerializeObject(remoteStopTransactionRequest);

            OCPPMessage msgOut = new OCPPMessage();
            msgOut.MessageType = "2";
            msgOut.Action = "RemoteStopTransaction";
            msgOut.UniqueId = Guid.NewGuid().ToString("N");
            msgOut.JsonPayload = jsonResetRequest;
            msgOut.TaskCompletionSource = new TaskCompletionSource<string>();

            // store HttpContext with MsgId for later answer processing (=> send anwer to API caller)
            _requestQueue.Add(msgOut.UniqueId, msgOut);

            // Send OCPP message with optional logging/dump
            await SendOcpp16Message(msgOut, logger, chargePointStatus.WebSocket);

            // Wait for asynchronous chargepoint response and processing
            string apiResult = await msgOut.TaskCompletionSource.Task;

            // 
            apiCallerContext.Response.StatusCode = 200;
            apiCallerContext.Response.ContentType = "application/json";
            await apiCallerContext.Response.WriteAsync(apiResult);
        }

        private async Task SendOcpp16Message(OCPPMessage msg, ILogger logger, WebSocket webSocket)
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
            logger.LogTrace("OCPPMiddleware.OCPP16 => SendOcppMessage: {0}", ocppTextMessage);

            if (string.IsNullOrEmpty(ocppTextMessage))
            {
                // invalid message
                ocppTextMessage = string.Format("[{0},\"{1}\",\"{2}\",\"{3}\",{4}]", "4", string.Empty, Messages_OCPP16.ErrorCodes.ProtocolError, string.Empty, "{}");
            }

            string dumpDir = _configuration.GetValue<string>("MessageDumpDir");
            if (!string.IsNullOrWhiteSpace(dumpDir))
            {
                // Write outgoing message into dump directory
                string path = Path.Combine(dumpDir, string.Format("{0}_ocpp16-out.txt", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-ffff")));
                try
                {
                    File.WriteAllText(path, ocppTextMessage);
                }
                catch (Exception exp)
                {
                    logger.LogError(exp, "OCPPMiddleware.SendOcpp16Message=> Error dumping message to path: '{0}'", path);
                }
            }

            byte[] binaryMessage = UTF8Encoding.UTF8.GetBytes(ocppTextMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(binaryMessage, 0, binaryMessage.Length), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
