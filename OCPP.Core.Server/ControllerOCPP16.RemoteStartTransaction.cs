using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP16
    {
        public string HandleRemoteStartTransaction(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            string errorCode = null;
            RemoteStartTransactionRequest remoteStartTransactionRequest = new RemoteStartTransactionRequest();

            int connectorId = 0;
            bool msgWritten = false;

            try
            {
                StatusNotificationRequest statusNotificationRequest = JsonConvert.DeserializeObject<StatusNotificationRequest>(msgIn.JsonPayload);
                connectorId = statusNotificationRequest.ConnectorId;

                //if (statusNotificationRequest.Status != StatusNotificationRequestStatus.Preparing)
                //{
                //    msgOut = null;
                //    return errorCode;
                //}

                remoteStartTransactionRequest.ConnectorId = Convert.ToInt32(msgIn.ConnectorId);
                remoteStartTransactionRequest.IdTag = Configuration.GetSection("TagIDTest").Value;
                remoteStartTransactionRequest.ChargingProfile = new ChargingProfile();

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

                Logger.LogInformation("RemoteStartTransaction => Save ConnectorStatus: ID={0} / Connector={1} / Meter={2}", ChargePointStatus.Id, connectorId, 0);

                msgOut.JsonPayload = JsonConvert.SerializeObject(remoteStartTransactionRequest);

                Logger.LogTrace("RemoteStartTransaction => Response serialized Data:"+ msgOut.JsonPayload);
                //using (OCPPCoreContext dbContext = new OCPPCoreContext(Configuration))
                //{
                //    ChargeTag chargeTags = dbContext.ChargeTags.Where(x => x.ChargePointId == ChargePointStatus.Id && x.Authorize == true).FirstOrDefault();

                //    if (chargeTags != null)
                //    {
                //        //New Transaction for auto start transaction
                //        //Transaction transaction = new Transaction();
                //        //transaction.ChargePointId = ChargePointStatus.Id;
                //        //transaction.ConnectorId = connectorId;
                //        //transaction.StartTagId = chargeTags.TagId;
                //        //transaction.StartTime = DateTime.UtcNow;
                //        //transaction.MeterStart = 0; // Meter value here is always Wh
                //        //dbContext.Add<Transaction>(transaction);
                //        //dbContext.SaveChanges();

                //        remoteStartTransactionRequest.IdTag = ChargePointStatus.Id;
                //        remoteStartTransactionRequest.ChargingProfile = new ChargingProfile();

                //        ChargingProfile chargingProfile = new ChargingProfile();
                //        chargingProfile.ChargingProfileId = 158798;
                //        chargingProfile.RecurrencyKind = RecurrencyKind.Daily;
                //        chargingProfile.Kind = ChargingProfileKind.Absolute;
                //        chargingProfile.Purpose = ChargingProfilePurpose.TxProfile;
                //        chargingProfile.ChargingSchedule = new ChargingSchedule();
                //        chargingProfile.ChargingSchedule.ChargingRateUnit = ChargingRateUnit.W;
                //        chargingProfile.ChargingSchedule.ChargingSchedulePeriod = new List<ChargingSchedulePeriod>();
                //        List<ChargingSchedulePeriod> chargingSchedulePeriod = new List<ChargingSchedulePeriod>();
                //        chargingSchedulePeriod.Add(new ChargingSchedulePeriod() { StartPeriod = 0, Limit = 1100.0 });
                //        chargingSchedulePeriod.Add(new ChargingSchedulePeriod() { StartPeriod = 780, Limit = 9000.0 });
                //        chargingSchedulePeriod.Add(new ChargingSchedulePeriod() { StartPeriod = 1680, Limit = 4500.0 });
                //        chargingProfile.ChargingSchedule.ChargingSchedulePeriod = chargingSchedulePeriod;
                //        chargingProfile.ChargingSchedule.Duration = 1680;
                //        chargingProfile.StackLevel = 0;
                //        //chargingProfile.TransactionId = transaction.TransactionId;
                //        remoteStartTransactionRequest.ChargingProfile = chargingProfile;

                //        Logger.LogInformation("RemoteStartTransaction => Save ConnectorStatus: ID={0} / Connector={1} / Meter={2}", ChargePointStatus.Id, connectorId, 0);

                //        msgOut.JsonPayload = JsonConvert.SerializeObject(remoteStartTransactionRequest);
                //        Logger.LogTrace("RemoteStartTransaction => Response serialized");
                //    }
                //    else
                //    {
                //        if (msgOut.TaskCompletionSource != null)
                //        {
                //            // Set API response as TaskCompletion-result
                //            string apiResult = "{\"status\": " + JsonConvert.ToString("Rejected") + "}";
                //            Logger.LogTrace("HandleReset => API response: {0}", apiResult);

                //            msgOut.TaskCompletionSource.SetResult(apiResult);
                //        }
                //    }
            //}
            }
            catch (Exception exp)
            {
                try
                {
                    RemoteStartTransactionResponse remoteStopTransactionResponse = JsonConvert.DeserializeObject<RemoteStartTransactionResponse>(msgIn.JsonPayload);
                    if (msgOut.TaskCompletionSource != null)
                    {
                        // Set API response as TaskCompletion-result
                        string apiResult = "{\"status\": " + JsonConvert.ToString(remoteStopTransactionResponse.Status.ToString()) + "}";
                        Logger.LogTrace("HandleReset => API response: {0}", apiResult);
                        
                        msgOut.TaskCompletionSource.SetResult(apiResult);
                    }
                }
                catch (Exception exp1)
                {
                    Logger.LogError(exp, "RemoteStartTransaction => ChargePoint={0} / ConnectorId: {1} / Exception: {2}", ChargePointStatus.Id, connectorId, exp1.Message);
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
