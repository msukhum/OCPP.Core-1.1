﻿/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2021 dallmann consulting GmbH.
 * All Rights Reserved.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

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
using OCPP.Core.Server.Models;

namespace OCPP.Core.Server.Controllers
{
    public partial class AppapiController : BaseController
    {
        [Authorize]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> UnlockConnector(string Id)
        {
            if (User != null && !User.IsInRole(Constants.AdminRoleName))
            {
                Logger.LogWarning("UnlockConnector: Request by non-administrator: {0}", User?.Identity?.Name);
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            int httpStatuscode = (int)HttpStatusCode.OK;
            string resultContent = string.Empty;

            Logger.LogTrace("UnlockConnector: Request to unlock chargepoint '{0}'", Id);
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
                                        uri = new Uri(uri, $"UnlockConnector/{Uri.EscapeUriString(Id)}");
                                        httpClient.Timeout = new TimeSpan(0, 0, 4); // use short timeout

                                        // API-Key authentication?
                                        if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                                        {
                                            httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                                        }
                                        else
                                        {
                                            Logger.LogWarning("UnlockConnector: No API-Key configured!");
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
                                                    Logger.LogInformation("UnlockConnector: Result of API request is '{0}'", jsonResult);
                                                    string status = jsonObject.status;
                                                    switch (status)
                                                    {
                                                        case "Unlocked":
                                                            resultContent = "The charging station has been unlocked.";
                                                            break;
                                                        case "UnlockFailed":
                                                        case "OngoingAuthorizedTransaction":
                                                        case "UnknownConnector":
                                                            resultContent = "The charging station could NOT be unlocked.";
                                                            break;
                                                        case "NotSupported":
                                                            resultContent = "The charging station does not support unlocking.";
                                                            break;
                                                        default:
                                                            resultContent = string.Format("The charging station returned an unexpected result: '{0}'", status);
                                                            break;
                                                    }
                                                }
                                                catch (Exception exp)
                                                {
                                                    Logger.LogError(exp, "UnlockConnector: Error in JSON result => {0}", exp.Message);
                                                    httpStatuscode = (int)HttpStatusCode.OK;
                                                    resultContent = "An error has occurred.";
                                                }
                                            }
                                            else
                                            {
                                                Logger.LogError("UnlockConnector: Result of API request is empty");
                                                httpStatuscode = (int)HttpStatusCode.OK;
                                                resultContent = "An error has occurred.";
                                            }
                                        }
                                        else if (response.StatusCode == HttpStatusCode.NotFound)
                                        {
                                            // Chargepoint offline
                                            httpStatuscode = (int)HttpStatusCode.OK;
                                            resultContent = "The charging station is offline and cannot be unlocked.";
                                        }
                                        else
                                        {
                                            Logger.LogError("UnlockConnector: Result of API  request => httpStatus={0}", response.StatusCode);
                                            httpStatuscode = (int)HttpStatusCode.OK;
                                            resultContent = "An error has occurred.";
                                        }
                                    }
                                }
                                catch (Exception exp)
                                {
                                    Logger.LogError(exp, "UnlockConnector: Error in API request => {0}", exp.Message);
                                    httpStatuscode = (int)HttpStatusCode.OK;
                                    resultContent = "An error has occurred.";
                                }
                            }
                        }
                        else
                        {
                            Logger.LogWarning("UnlockConnector: Error loading charge point '{0}' from database", Id);
                            httpStatuscode = (int)HttpStatusCode.OK;
                            resultContent = "The charging station was not found.";
                        }
                    }
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "UnlockConnector: Error loading charge point from database");
                    httpStatuscode = (int)HttpStatusCode.OK;
                    resultContent = "An error has occurred.";
                }
            }

            return StatusCode(httpStatuscode, resultContent);
        }
    }
}
