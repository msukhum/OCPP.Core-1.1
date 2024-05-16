using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Models;

namespace OCPP.Core.Server.Controllers
{
    public partial class HomeController : BaseController
    {

        [Authorize]
        public IActionResult Reservations(string Id, string ConnectorId)
        {
            Logger.LogTrace("Reservations: Loading charge point Reservations...");
            Constants.RefreshTime = base.Config.GetValue<string>("RefreshTime");
            int currentConnectorId = -1;
            int.TryParse(ConnectorId, out currentConnectorId);

            ReservationListViewModel tlvm = new ReservationListViewModel();
            tlvm.CurrentChargePointId = Id;
            tlvm.CurrentConnectorId = currentConnectorId;
            tlvm.ConnectorStatuses = new List<ConnectorStatus>();
            tlvm.Reservations = new List<Reservation>();
            try
            {
                string ts = Request.Query["t"];
                int days = 30;
                if (ts == "2")
                {
                    // 90 days
                    days = 90;
                    tlvm.Timespan = 2;
                }
                else if (ts == "3")
                {
                    // 365 days
                    days = 365;
                    tlvm.Timespan = 3;
                }
                else
                {
                    // 30 days
                    days = 30;
                    tlvm.Timespan = 1;
                }

                using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
                {
                    Logger.LogTrace("Reservations: Loading charge points...");
                    tlvm.ChargePoints = dbContext.ChargePoints.ToList<ChargePoint>();

                    Logger.LogTrace("Reservations: Loading charge points connectors...");
                    tlvm.ConnectorStatuses = dbContext.ConnectorStatuses.ToList<ConnectorStatus>();

                    // Count connectors for every charge point (=> naming scheme)
                    Dictionary<string, int> dictConnectorCount = new Dictionary<string, int>();
                    foreach (ConnectorStatus cs in tlvm.ConnectorStatuses)
                    {
                        if (dictConnectorCount.ContainsKey(cs.ChargePointId))
                        {
                            // > 1 connector
                            dictConnectorCount[cs.ChargePointId] = dictConnectorCount[cs.ChargePointId] + 1;
                        }
                        else
                        {
                            // first connector
                            dictConnectorCount.Add(cs.ChargePointId, 1);
                        }
                    }


                    // load charge tags for name resolution
                    Logger.LogTrace("Reservations: Loading charge tags...");
                    List<ChargeTag> chargeTags = dbContext.ChargeTags.ToList<ChargeTag>();
                    tlvm.ChargeTags = new Dictionary<string, ChargeTag>();
                    if (chargeTags != null)
                    {
                        foreach (ChargeTag tag in chargeTags)
                        {
                            tlvm.ChargeTags.Add(tag.TagId, tag);
                        }
                    }

                    if (!string.IsNullOrEmpty(tlvm.CurrentChargePointId))
                    {
                        Logger.LogTrace("Reservations: Loading charge point Reservations...");
                        tlvm.Reservations = dbContext.Reservations
                                            .Where(t => t.ChargePointId == tlvm.CurrentChargePointId &&
                                                        t.ConnectorId == tlvm.CurrentConnectorId &&
                                                        t.ReservationTime >= DateTime.Now.AddDays(-1 * days))
                                            .OrderByDescending(t => t.ReservationID)
                                            .ToList<Reservation>();
                    }
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "Reservations: Error loading charge points from database");
            }

            return View(tlvm);
        }
    }
}
