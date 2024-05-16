using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP16
    {
        public string HandleRemoteStopTransaction(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            string errorCode = null;
            RemoteStopTransactionRequest remoteStopTransactionRequest = new RemoteStopTransactionRequest();

            int connectorId = 0;
            bool msgWritten = false;

            try
            {
                StatusNotificationRequest statusNotificationRequest = JsonConvert.DeserializeObject<StatusNotificationRequest>(msgIn.JsonPayload);
                connectorId = statusNotificationRequest.ConnectorId;

                if (statusNotificationRequest.Status != StatusNotificationRequestStatus.Finishing)
                {
                    msgOut = null;
                    return errorCode;
                }

                using (OCPPCoreContext dbContext = new OCPPCoreContext(Configuration))
                {
                    //int transactionId = dbContext.Transactions.Where(x => x.ChargePointId == ChargePointStatus.Id && x.ConnectorId == connectorId && !x.StopTime.HasValue);
                    Transaction transaction = dbContext.Transactions.Where(x => x.ChargePointId == ChargePointStatus.Id && x.ConnectorId == connectorId && !x.StopTime.HasValue).FirstOrDefault();

                    if (transaction != null)
                    {

                        //ConnectorStatus connectorStatus = dbContext.ConnectorStatuses.Where(x => x.ChargePointId == ChargePointStatus.Id && x.ConnectorId == connectorId).FirstOrDefault();

                        //update Transaction for auto start transaction
                        //transaction.StopTagId = transaction.StartTagId;
                        //transaction.StopReason = "EmergencyStop";
                        //transaction.StopTime = statusNotificationRequest.Timestamp.Value.UtcDateTime;
                        //transaction.MeterStart = connectorStatus.LastMeter.Value; // Meter value here is always Wh
                        //dbContext.Update<Transaction>(transaction);
                        //dbContext.SaveChanges();

                        //connectorStatus.LastStatus = "Finishing";
                        //dbContext.Update<ConnectorStatus>(connectorStatus);
                        //dbContext.SaveChanges();

                        ChargeTag chargeTags = dbContext.ChargeTags.Where(x => x.ChargePointId == ChargePointStatus.Id && x.Authorize == true).FirstOrDefault();
                        chargeTags.Authorize = false;
                        chargeTags.ChargePointId = "";
                        dbContext.Update<ChargeTag>(chargeTags);
                        dbContext.SaveChanges();

                        remoteStopTransactionRequest.TransactionId = transaction.TransactionId;
                        Logger.LogInformation("RemoteStopTransaction => Save ConnectorStatus: ID={0} / Connector={1} / Meter={2}", ChargePointStatus.Id, connectorId, 0);

                        msgOut.JsonPayload = JsonConvert.SerializeObject(remoteStopTransactionRequest);
                        Logger.LogTrace("RemoteStopTransaction => Response serialized");
                    }
                    else
                    {
                        if (msgOut.TaskCompletionSource != null)
                        {
                            // Set API response as TaskCompletion-result
                            string apiResult = "{\"status\": " + JsonConvert.ToString("Rejected") + "}";
                            Logger.LogTrace("HandleReset => API response: {0}", apiResult);
                            msgIn.Action = "RemoteStopTransaction";
                            msgOut.TaskCompletionSource.SetResult(apiResult);
                        }
                    }
                }
            }
            catch (Exception exp)
            {
                try
                {
                    RemoteStopTransactionResponse remoteStopTransactionResponse = JsonConvert.DeserializeObject<RemoteStopTransactionResponse>(msgIn.JsonPayload);
                    if (msgOut.TaskCompletionSource != null)
                    {
                        // Set API response as TaskCompletion-result
                        string apiResult = "{\"status\": " + JsonConvert.ToString(remoteStopTransactionResponse.Status.ToString()) + "}";
                        Logger.LogTrace("HandleReset => API response: {0}", apiResult);
                        msgIn.Action = "RemoteStopTransaction";
                        msgOut.TaskCompletionSource.SetResult(apiResult);
                    }
                }
                catch (Exception exp1)
                {
                    Logger.LogError(exp, "RemoteStopTransaction => ChargePoint={0} / ConnectorId: {1} / Exception: {2}", ChargePointStatus.Id, connectorId, exp1.Message);
                    errorCode = ErrorCodes.InternalError;
                }
            }

            if (!msgWritten)
            {
                if (!string.IsNullOrEmpty(msgIn.Action))
                    WriteMessageLog(ChargePointStatus.Id, connectorId, "SV", "Request", msgIn.Action, null, errorCode);
            }
            return errorCode;
        }
    }
}
