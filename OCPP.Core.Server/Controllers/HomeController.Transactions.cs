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
        public IActionResult Transactions(string Id, string ConnectorId)
        {
            Logger.LogTrace("Transactions: Loading charge point transactions...");

            int currentConnectorId = -1;
            int.TryParse(ConnectorId, out currentConnectorId);

            OverviewViewModel overviewModel = new OverviewViewModel();
            overviewModel.ChargePoints = new List<ChargePointsOverviewViewModel>();
            Dictionary<string, ChargePointStatus> dictOnlineStatus = new Dictionary<string, ChargePointStatus>();
            #region Load online status from OCPP server
            string serverApiUrl = base.Config.GetValue<string>("ServerApiUrl");
            string apiKeyConfig = base.Config.GetValue<string>("ApiKey");
            if (!string.IsNullOrEmpty(serverApiUrl))
            {
                bool serverError = false;
                try
                {
                    ChargePointStatus[] onlineStatusList = null;

                    using (var httpClient = new HttpClient())
                    {
                        if (!serverApiUrl.EndsWith('/'))
                        {
                            serverApiUrl += "/";
                        }
                        Uri uri = new Uri(serverApiUrl);
                        uri = new Uri(uri, "Status");
                        httpClient.Timeout = new TimeSpan(0, 0, 4); // use short timeout

                        // API-Key authentication?
                        if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                        {
                            httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                        }
                        else
                        {
                            Logger.LogWarning("Index: No API-Key configured!");
                        }

                        HttpResponseMessage response = httpClient.GetAsync(uri).Result;
                        if (response.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            string jsonData = response.Content.ReadAsStringAsync().Result;
                            if (!string.IsNullOrEmpty(jsonData))
                            {
                                onlineStatusList = JsonConvert.DeserializeObject<ChargePointStatus[]>(jsonData);
                                overviewModel.ServerConnection = true;

                                if (onlineStatusList != null)
                                {
                                    foreach (ChargePointStatus cps in onlineStatusList)
                                    {
                                        if (!dictOnlineStatus.TryAdd(cps.Id, cps))
                                        {
                                            Logger.LogError("Index: Online charge point status (ID={0}) could not be added to dictionary", cps.Id);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Logger.LogError("Index: Result of status web request is empty");
                                serverError = true;
                            }
                        }
                        else
                        {
                            Logger.LogError("Index: Result of status web request => httpStatus={0}", response.StatusCode);
                            serverError = true;
                        }
                    }

                    Logger.LogInformation("Index: Result of status web request => Length={0}", onlineStatusList?.Length);
                }
                catch (Exception exp)
                {
                    Logger.LogError(exp, "Index: Error in status web request => {0}", exp.Message);
                    serverError = true;
                }

                if (serverError)
                {
                    ViewBag.ErrorMsg = "Error OCPPServer";
                }
            }
            #endregion

           
            TransactionListViewModel tlvm = new TransactionListViewModel();
            tlvm.CurrentChargePointId = Id;
            tlvm.CurrentConnectorId = currentConnectorId;
            tlvm.ConnectorStatuses = new List<ConnectorStatus>();
            tlvm.Transactions = new List<Transaction>();
            
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
                    Logger.LogTrace("Transactions: Loading charge points...");
                    tlvm.ChargePoints = dbContext.ChargePoints.ToList<ChargePoint>();

                    Logger.LogTrace("Transactions: Loading charge points connectors...");
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

                    // Dictionary mit ID+Connector => Name erstellen und View übergeben
                    // => Combobox damit füllen
                    // => Namen in Transaktionen auflösen




                    /*
                    // search selected charge point and connector
                    foreach (ConnectorStatus cs in tlvm.ConnectorStatuses)
                    {
                        if (cs.ChargePointId == Id && cs.ConnectorId == currentConnectorId)
                        {
                            tlvm.CurrentConnectorName = cs.ConnectorName;
                            if (string.IsNullOrEmpty(tlvm.CurrentConnectorName))
                            {
                                tlvm.CurrentConnectorName = $"{Id}:{cs.ConnectorId}";
                            }
                            break;
                        }
                    }
                    */


                    // load charge tags for name resolution
                    Logger.LogTrace("Transactions: Loading charge tags...");
                    List<ChargeTag> chargeTags = dbContext.ChargeTags.ToList<ChargeTag>();
                    tlvm.ChargeTags = new Dictionary<string, ChargeTag>();
                    if (chargeTags != null)
                    {
                        foreach(ChargeTag tag in chargeTags)
                        {
                            tlvm.ChargeTags.Add(tag.TagId, tag);
                        }
                    }

                    if (!string.IsNullOrEmpty(tlvm.CurrentChargePointId))
                    {
                        Logger.LogTrace("Transactions: Loading charge point transactions...");
                        tlvm.Transactions = dbContext.Transactions
                                            .Where(t => t.ChargePointId == tlvm.CurrentChargePointId &&
                                                        t.ConnectorId == tlvm.CurrentConnectorId &&
                                                        t.StartTime >= DateTime.Now.AddDays(-1 * days))
                                            .OrderByDescending(t => t.TransactionId)
                                            .ToList<Transaction>();
                    }
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "Transactions: Error loading charge points from database");
            }

            return View(tlvm);
        }

        [Authorize]
        public IActionResult ChargePointDataDetail(string Id, string ConnectorId)
        {
            Logger.LogTrace("Transactions: Loading charge point transactions...");
            Constants.RefreshTime = base.Config.GetValue<string>("RefreshTime");
            int currentConnectorId = -1;
            int.TryParse(ConnectorId, out currentConnectorId);

            TransactionListViewModel tlvm = new TransactionListViewModel();
            tlvm.CurrentChargePointId = Id;
            tlvm.CurrentConnectorId = currentConnectorId;
            tlvm.ConnectorStatuses = new List<ConnectorStatus>();
            tlvm.Transactions = new List<Transaction>();
            tlvm.MessageLogs = new List<MessageLog>();
            tlvm.HeartbeatTimeout = Convert.ToInt32(base.Config.GetValue<string>("HeartbeatTimeout"));
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
                    Logger.LogTrace("Transactions: Loading charge points...");
                    tlvm.ChargePoints = dbContext.ChargePoints.ToList<ChargePoint>();

                    Logger.LogTrace("Transactions: Loading charge points connectors...");
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

                    // Dictionary mit ID+Connector => Name erstellen und View übergeben
                    // => Combobox damit füllen
                    // => Namen in Transaktionen auflösen




                    /*
                    // search selected charge point and connector
                    foreach (ConnectorStatus cs in tlvm.ConnectorStatuses)
                    {
                        if (cs.ChargePointId == Id && cs.ConnectorId == currentConnectorId)
                        {
                            tlvm.CurrentConnectorName = cs.ConnectorName;
                            if (string.IsNullOrEmpty(tlvm.CurrentConnectorName))
                            {
                                tlvm.CurrentConnectorName = $"{Id}:{cs.ConnectorId}";
                            }
                            break;
                        }
                    }
                    */


                    // load charge tags for name resolution
                    Logger.LogTrace("Transactions: Loading charge tags...");
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
                        Logger.LogTrace("Transactions: Loading charge point transactions...");
                        tlvm.Transactions = dbContext.Transactions
                                            .Where(t => t.ChargePointId == tlvm.CurrentChargePointId &&
                                                        t.ConnectorId == tlvm.CurrentConnectorId &&
                                                        t.StartTime >= DateTime.Now.AddDays(-1 * days))
                                            .OrderByDescending(t => t.TransactionId)
                                            .ToList<Transaction>();
                    }

                    if (!string.IsNullOrEmpty(tlvm.CurrentChargePointId))
                    {
                        tlvm.MessageLogs = dbContext.MessageLogs.Where(t => t.ChargePointId == tlvm.CurrentChargePointId).ToList<MessageLog>().OrderByDescending(x => x.LogTime).ToList();
                    }
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "Transactions: Error loading charge points from database");
            }

            return View(tlvm);
        }
    }
}
