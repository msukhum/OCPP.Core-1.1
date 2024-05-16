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
        public string HandleReserveNow(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            string errorCode = null;

            Logger.LogTrace("Processing reserveNow Response...");
            ReserveNowResponse reserveNowResponse = JsonConvert.DeserializeObject<ReserveNowResponse>(msgIn.JsonPayload);
            Logger.LogTrace("reserveNow => Response serialized");

            if(reserveNowResponse.Status != ReserveNowResponseStatus.Accepted)
            {
                using (OCPPCoreContext dbContext = new OCPPCoreContext(Configuration))
                {
                    Reservation reservation = new Reservation();
                    if (!string.IsNullOrEmpty(msgIn.ConnectorId))
                        reservation = dbContext.Reservations.Where(x => x.ChargePointId == ChargePointStatus.Id && x.ConnectorId.ToString() == msgIn.ConnectorId && x.Status == false).FirstOrDefault();
                    else
                        reservation = dbContext.Reservations.Where(x => x.ChargePointId == ChargePointStatus.Id && x.Status == false).FirstOrDefault();
                    reservation.Status = true;
                    reservation.StatusReason = "ReserveNow=>" + reserveNowResponse.Status.ToString();
                    dbContext.Update<Reservation>(reservation);
                    dbContext.SaveChanges();
                }
            }
            if (msgOut.TaskCompletionSource != null)
            {
                // Set API response as TaskCompletion-result
                string apiResult = "{\"status\": " + JsonConvert.ToString(reserveNowResponse.Status.ToString()) + "}";
                Logger.LogTrace("HandleUnlockConnector => API response: {0}", apiResult);

                msgOut.TaskCompletionSource.SetResult(apiResult);
            }

            WriteMessageLog(ChargePointStatus?.Id, null, "CP", "Response", msgOut.Action, null, errorCode);
            return errorCode;
        }
    }
}
