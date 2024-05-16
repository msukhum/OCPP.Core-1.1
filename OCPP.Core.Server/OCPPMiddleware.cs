using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace OCPP.Core.Server
{
    public partial class OCPPMiddleware
    {
        // Supported OCPP protocols (in order)
        public const string Protocol_OCPP16 = "ocpp1.6";
        public const string Protocol_OCPP20 = "ocpp2.0";
        private static readonly string[] SupportedProtocols = { Protocol_OCPP20, Protocol_OCPP16 /*, "ocpp1.5" */};

        // RegExp for splitting ocpp message parts
        // ^\[\s*(\d)\s*,\s*\"([^"]*)\"\s*,(?:\s*\"(\w*)\"\s*,)?\s*(.*)\s*\]$
        // Third block is optional, because responses don't have an action
        private static string MessageRegExp = "^\\[\\s*(\\d)\\s*,\\s*\"([^\"]*)\"\\s*,(?:\\s*\"(\\w*)\"\\s*,)?\\s*(.*)\\s*\\]$";

        private readonly RequestDelegate _next;
        private readonly ILoggerFactory _logFactory;
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;

        // Dictionary with status objects for each charge point
        public static Dictionary<string, ChargePointStatus> _chargePointStatusDict = new Dictionary<string, ChargePointStatus>();

        // Dictionary for processing asynchronous API calls
        private Dictionary<string, OCPPMessage> _requestQueue = new Dictionary<string, OCPPMessage>();

        public OCPPMiddleware(RequestDelegate next, ILoggerFactory logFactory, IConfiguration configuration)
        {
            _next = next;
            _logFactory = logFactory;
            _configuration = configuration;

            _logger = logFactory.CreateLogger("OCPPMiddleware");

            try
            {
                _logger.LogTrace("OCPPMiddleware => Store/Update status object");
                using (OCPPCoreContext dbContext = new OCPPCoreContext(_configuration))
                {
                    List<ChargePoint> chargePoints = dbContext.ChargePoints.ToList<ChargePoint>();
                    List<ConnectorStatusView> connectorStatusViews = dbContext.ConnectorStatusViews.ToList<ConnectorStatusView>();

                    lock (_chargePointStatusDict)
                    {
                        foreach (ChargePoint chargePoint in chargePoints)
                        {
                            ChargePointStatus chargePointStatus = new ChargePointStatus(chargePoint);
                            if (chargePointStatus.OnlineConnectors == null) chargePointStatus.OnlineConnectors = new Dictionary<int, OnlineConnectorStatus>();

                            // Check if this chargepoint already/still hat a status object
                            if (!_chargePointStatusDict.ContainsKey(chargePoint.ChargePointId))
                            {
                                List<ConnectorStatusView> items = connectorStatusViews.Where(x => x.ChargePointId == chargePoint.ChargePointId).ToList();
                                foreach(var item in items)
                                {
                                    if (!chargePointStatus.OnlineConnectors.ContainsKey(item.ConnectorId))
                                    {
                                        chargePointStatus.OnlineConnectors.Add(item.ConnectorId, new OnlineConnectorStatus());
                                    }
                                }
                                _chargePointStatusDict.Add(chargePoint.ChargePointId, chargePointStatus);
                            }

                        }
                    }
                }
            }
            catch (Exception exp)
            {
                _logger.LogError(exp, "OCPPMiddleware => Error storing status object in dictionary => refuse connection");
            }
        }

        public async Task Invoke(HttpContext context)
        {
            _logger.LogTrace("OCPPMiddleware => Websocket request: Path='{0}'", context.Request.Path);

            ChargePointStatus chargePointStatus = null;

            if (context.Request.Path.StartsWithSegments("/OCPP"))
            {
                string chargepointIdentifier;
                string[] parts = context.Request.Path.Value.Split('/');
                if (string.IsNullOrWhiteSpace(parts[parts.Length - 1]))
                {
                    // (Last part - 1) is chargepoint identifier
                    chargepointIdentifier = parts[parts.Length - 2];
                }
                else
                {
                    // Last part is chargepoint identifier
                    chargepointIdentifier = parts[parts.Length - 1];
                }
                _logger.LogInformation("OCPPMiddleware => Connection request with chargepoint identifier = '{0}'", chargepointIdentifier);

                // Known chargepoint?
                if (!string.IsNullOrWhiteSpace(chargepointIdentifier))
                {
                    using (OCPPCoreContext dbContext = new OCPPCoreContext(_configuration))
                    {
                        ChargePoint chargePoint = dbContext.Find<ChargePoint>(chargepointIdentifier);
                        if (chargePoint != null)
                        {
                            _logger.LogInformation("OCPPMiddleware => SUCCESS: Found chargepoint with identifier={0}", chargePoint.ChargePointId);

                            // Check optional chargepoint authentication
                            if (!string.IsNullOrWhiteSpace(chargePoint.Username))
                            {
                                // Chargepoint MUST send basic authentication header

                                bool basicAuthSuccess = false;
                                string authHeader = context.Request.Headers["Authorization"];
                                if (!string.IsNullOrEmpty(authHeader))
                                {
                                    string[] cred = System.Text.ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(authHeader.Substring(6))).Split(':');
                                    if (cred.Length == 2 && chargePoint.Username == cred[0] && chargePoint.Password == cred[1])
                                    {
                                        // Authentication match => OK
                                        _logger.LogInformation("OCPPMiddleware => SUCCESS: Basic authentication for chargepoint '{0}' match", chargePoint.ChargePointId);
                                        basicAuthSuccess = true;
                                    }
                                    else
                                    {
                                        // Authentication does NOT match => Failure
                                        _logger.LogWarning("OCPPMiddleware => FAILURE: Basic authentication for chargepoint '{0}' does NOT match", chargePoint.ChargePointId);
                                    }
                                }
                                if (basicAuthSuccess == false)
                                {
                                    context.Response.Headers.Add("WWW-Authenticate", "Basic realm=\"OCPP.Core\"");
                                    context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                                    return;
                                }

                            }
                            else if (!string.IsNullOrWhiteSpace(chargePoint.ClientCertThumb))
                            {
                                // Chargepoint MUST send basic authentication header

                                bool certAuthSuccess = false;
                                X509Certificate2 clientCert = context.Connection.ClientCertificate;
                                if (clientCert != null)
                                {
                                    if (clientCert.Thumbprint.Equals(chargePoint.ClientCertThumb, StringComparison.InvariantCultureIgnoreCase))
                                    {
                                        // Authentication match => OK
                                        _logger.LogInformation("OCPPMiddleware => SUCCESS: Certificate authentication for chargepoint '{0}' match", chargePoint.ChargePointId);
                                        certAuthSuccess = true;
                                    }
                                    else
                                    {
                                        // Authentication does NOT match => Failure
                                        _logger.LogWarning("OCPPMiddleware => FAILURE: Certificate authentication for chargepoint '{0}' does NOT match", chargePoint.ChargePointId);
                                    }
                                }
                                if (certAuthSuccess == false)
                                {
                                    context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                                    return;
                                }
                            }
                            else
                            {
                                _logger.LogInformation("OCPPMiddleware => No authentication for chargepoint '{0}' configured", chargePoint.ChargePointId);
                            }

                            // Store chargepoint data
                            chargePointStatus = new ChargePointStatus(chargePoint);
                        }
                        else
                        {
                            _logger.LogWarning("OCPPMiddleware => FAILURE: Found no chargepoint with identifier={0}", chargepointIdentifier);
                        }
                    }
                }

                if (chargePointStatus != null)
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        // Match supported sub protocols
                        string subProtocol = null;
                        foreach (string supportedProtocol in SupportedProtocols)
                        {
                            if (context.WebSockets.WebSocketRequestedProtocols.Contains(supportedProtocol))
                            {
                                subProtocol = supportedProtocol;
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(subProtocol))
                        {
                            // Not matching protocol! => failure
                            string protocols = string.Empty;
                            foreach (string p in context.WebSockets.WebSocketRequestedProtocols)
                            {
                                if (string.IsNullOrEmpty(protocols)) protocols += ",";
                                protocols += p;
                            }
                            _logger.LogWarning("OCPPMiddleware => No supported sub-protocol in '{0}' from charge station '{1}'", protocols, chargepointIdentifier);
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        }
                        else
                        {
                            chargePointStatus.Protocol = subProtocol;

                            bool statusSuccess = false;

                            try
                            {
                                _logger.LogTrace("OCPPMiddleware => Store/Update status object");

                                lock (_chargePointStatusDict)
                                {
                                    // Check if this chargepoint already/still hat a status object
                                    if (!_chargePointStatusDict.ContainsKey(chargepointIdentifier))
                                    {
                                        _chargePointStatusDict.Add(chargepointIdentifier, chargePointStatus);

                                        //if (_chargePointStatusDict[chargepointIdentifier].WebSocket == null) _chargePointStatusDict.Remove(chargepointIdentifier);
                                        //else
                                        //{
                                        //    //_chargePointStatusDict[chargepointIdentifier].Protocol = chargePointStatus.Protocol;
                                        //    //_chargePointStatusDict[chargepointIdentifier].WebSocket = chargePointStatus.WebSocket;
                                        //    // exists => check status
                                        //    if (_chargePointStatusDict[chargepointIdentifier].WebSocket.State != WebSocketState.Open)
                                        //    {
                                        //        // Closed or aborted => remove
                                        //        _chargePointStatusDict.Remove(chargepointIdentifier);
                                        //    }
                                        //}
                                    }
                                    //else
                                    //{
                                    //    _chargePointStatusDict.Add(chargepointIdentifier, chargePointStatus);
                                    //}

                                    _chargePointStatusDict[chargepointIdentifier].Protocol = chargePointStatus.Protocol;
                                    statusSuccess = true;
                                }
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware => Error storing status object in dictionary => refuse connection");
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }

                            if (statusSuccess)
                            {
                                // Handle socket communication
                                _logger.LogTrace("OCPPMiddleware => Waiting for message...");

                                using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync(subProtocol))
                                {
                                    _logger.LogTrace("OCPPMiddleware => WebSocket connection with charge point '{0}'", chargepointIdentifier);
                                    chargePointStatus.WebSocket = webSocket;

                                    if (_chargePointStatusDict.ContainsKey(chargepointIdentifier))
                                    {
                                        _chargePointStatusDict[chargepointIdentifier].WebSocket = chargePointStatus.WebSocket;
                                    }

                                    if (subProtocol == Protocol_OCPP20)
                                    {
                                        // OCPP V2.0
                                        await Receive20(chargePointStatus, context);
                                    }
                                    else
                                    {
                                        // OCPP V1.6
                                        await Receive16(chargePointStatus, context);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // no websocket request => failure
                        _logger.LogWarning("OCPPMiddleware => Non-Websocket request");
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
                else
                {
                    // unknown chargepoint
                    _logger.LogTrace("OCPPMiddleware => no chargepoint: http 412");
                    context.Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                }
            }
            else if (context.Request.Path.StartsWithSegments("/API"))
            {
                // Check authentication (X-API-Key)
                string apiKeyConfig = _configuration.GetValue<string>("ApiKey");
                if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                {
                    // ApiKey specified => check request
                    string apiKeyCaller = context.Request.Headers["X-API-Key"].FirstOrDefault();
                    if (apiKeyConfig == apiKeyCaller)
                    {
                        // API-Key matches
                        _logger.LogInformation("OCPPMiddleware => Success: X-API-Key matches");
                    }
                    else
                    {
                        // API-Key does NOT matches => authentication failure!!!
                        _logger.LogWarning("OCPPMiddleware => Failure: Wrong X-API-Key! Caller='{0}'", apiKeyCaller);
                        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        return;
                    }
                }
                else
                {
                    // No API-Key configured => no authenticatiuon
                    _logger.LogWarning("OCPPMiddleware => No X-API-Key configured!");
                }

                // format: /API/<command>[/chargepointId][/ConnectorId]
                string[] urlParts = context.Request.Path.Value.Split('/');

                if (urlParts.Length >= 3)
                {
                    string cmd = urlParts[2];
                    string urlChargePointId = (urlParts.Length >= 4) ? urlParts[3] : "0";
                    string urlConnectorId = (urlParts.Length >= 5) ? urlParts[4] : null;
                    string urlChargeTagId = (urlParts.Length >= 6) ? urlParts[5] : null;

                    try
                    {
                        var requestReader = new StreamReader(context.Request.Body);
                        var requestContent = requestReader.ReadToEnd();
                        //Console.WriteLine($"Request Body: {requestContent}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Can't rewind body stream. " + ex.Message);
                    }

                    _logger.LogTrace("OCPPMiddleware => cmd='{0}' / id='{1}' / FullPath='{2}')", cmd, urlChargePointId, context.Request.Path.Value);

                    try
                    {

                        if (cmd == "Status")
                        {
                            try
                            {
                                List<ChargePointStatus> statusList = new List<ChargePointStatus>();
                                foreach (ChargePointStatus _status in _chargePointStatusDict.Values)
                                {
                                    if (_status.WebSocket != null)
                                    {
                                        _status.WebSocketStatus = _status.WebSocket.State.ToString();

                                        if (_status.WebSocketStatus != "Open")
                                        {
                                            foreach (int key in _status.OnlineConnectors.Keys)
                                            {
                                                _status.OnlineConnectors[key].Status = Models.ConnectorStatusEnum.Undefined;
                                            }
                                        }
                                    }

                                    statusList.Add(_status);
                                }
                                string jsonStatus = JsonConvert.SerializeObject(statusList);
                                context.Response.ContentType = "application/json";
                                await context.Response.WriteAsync(jsonStatus);
                            }
                            catch (Exception exp)
                            {
                                _logger.LogError(exp, "OCPPMiddleware => Error: {0}", exp.Message);
                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            }

                        }
                        else
                        {
                            ChargePointStatus status = null;
                            if (_chargePointStatusDict.TryGetValue(urlChargePointId, out status))
                            {
                                switch (cmd)
                                {
                                    case "CancelReservation":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await CancelReservation16(status, context);
                                        }
                                        break;
                                    case "ReserveNow":
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await Reserve16(status, context);
                                        }
                                        break;
                                    case "Reset":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            await Reset20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await Reset16(status, context);
                                        }
                                        break;
                                    case "UnlockConnector":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await UnlockConnector16(status, context);
                                        }
                                        break;
                                    case "RemoteStartTransaction":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await RemoteStartTransaction16(status, context);
                                        }
                                        break;
                                    case "RemoteStopTransaction":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await RemoteStopTransaction16(status, context);
                                        }
                                        break;
                                    case "ClearCache":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await ClearCache16(status, context);
                                        }
                                        break;
                                    case "ChangeAvailability":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await ChangeAvailability16(status, context);
                                        }
                                        break;
                                    case "ChangeConfiguration":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await ChangeConfiguration16(status, context);
                                        }
                                        break;
                                    case "ClearChargingProfile":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await ClearChargingProfile16(status, context);
                                        }
                                        break;
                                    case "GetCompositeSchedule":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await GetCompositeSchedule16(status, context);
                                        }
                                        break;
                                    case "GetConfiguration":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await GetConfiguration16(status, context);
                                        }
                                        break;
                                    case "GetDiagnostics":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await GetDiagnostics16(status, context);
                                        }
                                        break;
                                    case "GetLocalListVersion":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await GetLocalListVersion16(status, context);
                                        }
                                        break;
                                    case "SendLocalList":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await SendLocalList16(status, context);
                                        }
                                        break;
                                    case "SetChargingProfile":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await SetChargingProfile16(status, context);
                                        }
                                        break;
                                    case "UpdateFirmware":
                                        // Send message to chargepoint
                                        if (status.Protocol == Protocol_OCPP20)
                                        {
                                            // OCPP V2.0
                                            //await UnlockConnector20(status, context);
                                        }
                                        else
                                        {
                                            // OCPP V1.6
                                            await UpdateFirmware16(status, context);
                                        }
                                        break;
                                    default:
                                        //    // Unknown action/function
                                        _logger.LogWarning("OCPPMiddleware => action/function: {0}", cmd);
                                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                        break;
                                }
                            }
                            else
                            {
                                // Chargepoint offline
                                _logger.LogError("OCPPMiddleware UnlockConnector => Chargepoint offline: {0}", urlChargePointId);
                                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            }
                        }
                    }
                    catch (Exception exp)
                    {
                        _logger.LogError(exp, "OCPPMiddleware UnlockConnector => Error: {0}", exp.Message);
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    }

                }
            }
            else if (context.Request.Path.StartsWithSegments("/"))
            {
                try
                {
                    bool showIndexInfo = _configuration.GetValue<bool>("ShowIndexInfo");
                    if (showIndexInfo)
                    {
                        _logger.LogTrace("OCPPMiddleware => Index status page");

                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync(string.Format("Running...\r\n\r\n{0} chargepoints connected", _chargePointStatusDict.Values.Count));
                    }
                    else
                    {
                        _logger.LogInformation("OCPPMiddleware => Root path with deactivated index page");
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
                catch (Exception exp)
                {
                    _logger.LogError(exp, "OCPPMiddleware => Error: {0}", exp.Message);
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                }
            }
            else
            {
                _logger.LogWarning("OCPPMiddleware => Bad path request");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            }
        }

        bool SocketConnected(Socket s)
        {
            bool part1 = s.Poll(1000, SelectMode.SelectRead);
            bool part2 = (s.Available == 0);
            if (part1 && part2)
                return false;
            else
                return true;
        }
    }

    public static class OCPPMiddlewareExtensions
    {
        public static IApplicationBuilder UseOCPPMiddleware(this IApplicationBuilder builder)
        {
            builder.Use(async (context, next) =>
            {
                context.Request.EnableBuffering();

                using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, 1024, true))
                {
                    var body = await reader.ReadToEndAsync();

                    context.Request.Body.Seek(0, SeekOrigin.Begin);
                }

                await next.Invoke();
            });

            return builder.UseMiddleware<OCPPMiddleware>();
        }
    }
}
