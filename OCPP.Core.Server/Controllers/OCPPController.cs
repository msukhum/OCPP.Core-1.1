using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Controllers
{
    public partial class OCPPController : BaseController
    {
        public OCPPController(UserManager userManager, ILoggerFactory loggerFactory, IConfiguration config) : base(userManager, loggerFactory, config)
        {
            Logger = loggerFactory.CreateLogger<HomeController>();

            Constants.RefreshTime = "-1";
        }


        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View();
        }

        public IActionResult GetChargeTags()
        {
            List<ChargeTag> data = new List<ChargeTag>();

            using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
            {
                data = dbContext.ChargeTags.ToList<ChargeTag>();
            }

            string json = JsonConvert.SerializeObject(data);

            return new JsonResult(json);
        }

        public IActionResult GetConnectors(string id)
        {
            List<ConnectorStatus> data = new List<ConnectorStatus>();

            using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
            {
                data = dbContext.ConnectorStatuses.Where(x => x.ChargePointId == id).ToList<ConnectorStatus>();
            }

            string json = JsonConvert.SerializeObject(data);

            return new JsonResult(json);
        }

        public IActionResult GetReservations(string id, int connectorid)
        {
            List<Reservation> data = new List<Reservation>();

            using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
            {
                data = dbContext.Reservations.Where(x => x.ChargePointId == id && x.ConnectorId == connectorid && x.Status == false).ToList<Reservation>();
            }

            string json = JsonConvert.SerializeObject(data);

            return new JsonResult(json);
        }

        public IActionResult GetTransactions(string id, int connectorid)
        {
            List<Transaction> data = new List<Transaction>();

            using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
            {
                data = dbContext.Transactions.Where(x => x.ChargePointId == id && x.ConnectorId == connectorid && x.StopTime == null).ToList<Transaction>();
            }

            string json = JsonConvert.SerializeObject(data);

            return new JsonResult(json);
        }
    }
}
