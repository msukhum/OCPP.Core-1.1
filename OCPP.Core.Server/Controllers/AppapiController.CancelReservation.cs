using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;

namespace OCPP.Core.Server.Controllers
{
    public partial class AppapiController : BaseController
    {
        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> CancelReservation(string Id, string connectorID)
        {
            //if (User != null && !User.IsInRole(Constants.AdminRoleName))
            //{
            //    Logger.LogWarning("CancelReservation: Request by non-administrator: {0}", User?.Identity?.Name);
            //    return StatusCode((int)HttpStatusCode.Unauthorized);
            //}

            int httpStatuscode = (int)HttpStatusCode.OK;
            string resultContent = string.Empty;

            Logger.LogTrace("CancelReservation: Request to restart chargepoint '{0}'", Id);
            if (!string.IsNullOrEmpty(Id))
            {
                try
                {
                    using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
                    {
                        ChargePoint chargePoint = dbContext.ChargePoints.Find(Id);
                        if (chargePoint != null)
                        {
                            string serverApiUrl = base.Config.GetValue<string>("ServerApiUrl");
                            string apiKeyConfig = base.Config.GetValue<string>("ApiKey");
                            if (!string.IsNullOrEmpty(serverApiUrl))
                            {
                                try
                                {
                                    using (var httpClient = new HttpClient())
                                    {
                                        if (!serverApiUrl.EndsWith('/'))
                                        {
                                            serverApiUrl += "/";
                                        }
                                        Uri uri = new Uri(serverApiUrl);
                                        uri = new Uri(uri, $"CancelReservation/{Uri.EscapeUriString(Id)}/{connectorID}");
                                        httpClient.Timeout = new TimeSpan(0, 0, 4); // use short timeout

                                        // API-Key authentication?
                                        if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                        {
                                            httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                        }
                                        else
                                        {
                                            Logger.LogWarning("CancelReservation: No API-Key configured!");
                                        }

                                        HttpResponseMessage response = await httpClient.GetAsync(uri);
                                        if (response.StatusCode == HttpStatusCode.OK)
                                        {
                                            string jsonResult = await response.Content.ReadAsStringAsync();
                                            if (!string.IsNullOrEmpty(jsonResult))
                                            {
                                                try
                                                {
                                                    dynamic jsonObject = JsonConvert.DeserializeObject(jsonResult);
                                                    Logger.LogInformation("CancelReservation: Result of API request is '{0}'", jsonResult);
                                                    string status = jsonObject.status;
                                                    switch (status)
                                                    {
                                                        case "Accepted":
                                                            resultContent = "The charging station is being restarted.";
                                                            break;
                                                        case "Rejected":
                                                            resultContent = "The charging station has rejected the request.";
                                                            break;
                                                        default:
                                                            resultContent = string.Format("The charging station returned an unexpected result: '{0}'", status);
                                                            break;
                                                    }
                                                }
                                                catch (Exception exp)
                                                {
                                                    Logger.LogError(exp, "CancelReservation: Error in JSON result => {0}", exp.Message);
                                                    httpStatuscode = (int)HttpStatusCode.OK;
                                                    resultContent = "An error has occurred.";
                                                }
                                            }
                                            else
                                            {
                                                Logger.LogError("CancelReservation: Result of API request is empty");
                                                httpStatuscode = (int)HttpStatusCode.OK;
                                                resultContent = "An error has occurred.";
                                            }
                                        }
                                        else if (response.StatusCode == HttpStatusCode.NotFound)
                                        {
                                            // Chargepoint offline
                                            httpStatuscode = (int)HttpStatusCode.OK;
                                            resultContent = "The charging station is offline and cannot be restarted.";
                                        }
                                        else
                                        {
                                            Logger.LogError("CancelReservation: Result of API  request => httpStatus={0}", response.StatusCode);
                                            httpStatuscode = (int)HttpStatusCode.OK;
                                            resultContent = "An error has occurred.";
                                        }
                                    }
                                }
                                catch (Exception exp)
                                {
                                    Logger.LogError(exp, "CancelReservation: Error in API request => {0}", exp.Message);
                                    httpStatuscode = (int)HttpStatusCode.OK;
                                    resultContent = "An error has occurred.";
                                }
                            }
                        }
                        else
                        {
                            Logger.LogWarning("CancelReservation: Error loading charge point '{0}' from database", Id);
                            httpStatuscode = (int)HttpStatusCode.OK;
                            resultContent = "The charging station was not found.";
                        }
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "CancelReservation: Error loading charge point from database");
                    httpStatuscode = (int)HttpStatusCode.OK;
                    resultContent = "An error has occurred.";
                }
            }

            return base.StatusCode(httpStatuscode, resultContent);
        }
    }
}
