using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OCPP.Core.Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OCPP.Core.Database;
using Newtonsoft.Json;
using OCPP.Core.Server.Messages_OCPP16;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net;

namespace OCPP.Core.Server.Controllers
{
    public partial class OCPPController : BaseController
    {
        [Authorize]
        public IActionResult GetCompositeSchedule()
        {
            OCPPViewModel model = new OCPPViewModel();

            using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
            {
                model.ChargePoints = dbContext.ChargePoints.ToList<ChargePoint>();
            }
            model.ChargingRateUnits = Enum.GetValues(typeof(ChargingRateUnit)).Cast<ChargingRateUnit>().ToList();

            return View(model);
        }

        public IActionResult GetCompositeScheduleJson(int connectorid, string chargingrateunit, int duration)
        {
            GetCompositeScheduleRequest request = new GetCompositeScheduleRequest();

            Enum.TryParse(chargingrateunit, out ChargingRateUnit myStatus);
            request.Status = myStatus;
            request.ConnectorId = connectorid;
            request.Duration = duration;

            string json = JsonConvert.SerializeObject(request);

            return new JsonResult(json);
        }

        public async Task<IActionResult> GetCompositeSchedule2ChargePoint(string id, int connectorid, string chargingrateunit, int duration)
        {
            dynamic jsonObject = null;
            string jsonResult = null;
            Logger.LogTrace("GetCompositeSchedule: Request to restart chargepoint '{0}'", id);
            GetCompositeScheduleRequest request = new GetCompositeScheduleRequest();

            Enum.TryParse(chargingrateunit, out ChargingRateUnit myStatus);
            request.Status = myStatus;
            request.ConnectorId = connectorid;
            request.Duration = duration;

            try
            {
                string serverApiUrl = base.Config.GetValue<string>("ServerApiUrl");
                string apiKeyConfig = base.Config.GetValue<string>("ApiKey");
                using (var httpClient = new HttpClient())
                {
                    if (!serverApiUrl.EndsWith('/'))
                    {
                        serverApiUrl += "/";
                    }
                    Uri uri = new Uri(serverApiUrl);
                    uri = new Uri(uri, $"GetCompositeSchedule/{Uri.EscapeUriString(id)}/{connectorid}");
                    httpClient.Timeout = new TimeSpan(0, 0, 30); // use short timeout

                    // API-Key authentication?
                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                    {
                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                    }
                    else
                    {
                        Logger.LogWarning("GetCompositeSchedule: No API-Key configured!");
                    }

                    //HttpResponseMessage response = await httpClient.GetAsync(uri,);
                    HttpResponseMessage response = await httpClient.PostAsync(uri, request.AsJson());
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        jsonResult = await response.Content.ReadAsStringAsync();
                       
                    }
                    else if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Chargepoint offline
                        // httpStatuscode = (int)HttpStatusCode.OK;
                        //resultContent = "The charging station is offline and cannot be restarted.";
                    }
                    else
                    {
                        Logger.LogError("GetCompositeSchedule: Result of API  request => httpStatus={0}", response.StatusCode);
                        //httpStatuscode = (int)HttpStatusCode.OK;
                        // resultContent = "An error has occurred.";
                    }
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "GetCompositeSchedule: Error in API request => {0}", exp.Message);
                //httpStatuscode = (int)HttpStatusCode.OK;
                //resultContent = "An error has occurred.";
            }

            return new JsonResult(jsonResult);
        }

    }
}
