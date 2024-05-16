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
        public string HandleCancelReservation(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            string errorCode = null;

            Logger.LogTrace("Processing reserveNow Response...");
            CancelReservationResponse cancelReservationResponse = JsonConvert.DeserializeObject<CancelReservationResponse>(msgIn.JsonPayload);
            Logger.LogTrace("reserveNow => Response serialized");

            if (cancelReservationResponse.Status == CancelReservationResponseStatus.Accepted)
            {
                using (OCPPCoreContext dbContext = new OCPPCoreContext(Configuration))
                {
                    //Reservation reservation = dbContext.Reservations.Where(x => x.ChargePointId == ChargePointStatus.Id && x.ConnectorId.ToString() == msgIn.ConnectorId && x.Status == false).FirstOrDefault();
                    Reservation reservation = new Reservation();
                    if (!string.IsNullOrEmpty(msgIn.ConnectorId))
                        reservation = dbContext.Reservations.Where(x => x.ChargePointId == ChargePointStatus.Id && x.ConnectorId.ToString() == msgIn.ConnectorId && x.Status == false).FirstOrDefault();
                    else
                        reservation = dbContext.Reservations.Where(x => x.ChargePointId == ChargePointStatus.Id && x.Status == false).FirstOrDefault();

                    reservation.Status = true;
                    reservation.StatusReason = "CancelReservation=>" + cancelReservationResponse.Status.ToString();
                    dbContext.Update<Reservation>(reservation);
                    dbContext.SaveChanges();
                }

                UpdateConnectorStatus(Convert.ToInt32(msgIn.ConnectorId), StatusNotificationRequestStatus.Available.ToString(), DateTimeOffset.Now, null, null, null, null);
            }

            if (msgOut.TaskCompletionSource != null)
            {
                // Set API response as TaskCompletion-result
                string apiResult = "{\"status\": " + JsonConvert.ToString(cancelReservationResponse.Status.ToString()) + "}";
                Logger.LogTrace("HandleUnlockConnector => API response: {0}", apiResult);

                msgOut.TaskCompletionSource.SetResult(apiResult);
            }

            WriteMessageLog(ChargePointStatus?.Id, null, "CP", "Response", msgOut.Action, null, errorCode);
            return errorCode;
        }
    }
}
