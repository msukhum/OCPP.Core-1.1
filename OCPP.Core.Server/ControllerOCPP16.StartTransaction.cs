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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;
using OCPP.Core.Server.Models;

namespace OCPP.Core.Server
{
    public partial class ControllerOCPP16
    {
        public string HandleStartTransaction(OCPPMessage msgIn, OCPPMessage msgOut)
        {
            string errorCode = null;
            StartTransactionResponse startTransactionResponse = new StartTransactionResponse();
            StartTransactionRequest startTransactionRequest = new StartTransactionRequest();
            int connectorId = -1;

            try
            {
                Logger.LogTrace("Processing startTransaction request...");
                startTransactionRequest = JsonConvert.DeserializeObject<StartTransactionRequest>(msgIn.JsonPayload);
                Logger.LogTrace("StartTransaction => Message deserialized");

                string idTag = CleanChargeTagId(startTransactionRequest.IdTag, Logger);
                connectorId = startTransactionRequest.ConnectorId;

                startTransactionResponse.IdTagInfo.ParentIdTag = string.Empty;
                startTransactionResponse.IdTagInfo.ExpiryDate = MaxExpiryDate;

                if (string.IsNullOrWhiteSpace(idTag))
                {
                    // no RFID-Tag => accept request
                    startTransactionResponse.IdTagInfo.Status = IdTagInfoStatus.Accepted;
                    Logger.LogInformation("StartTransaction => no charge tag => Status: {0}", startTransactionResponse.IdTagInfo.Status);
                }
                else
                {
                    try
                    {
                        using (OCPPCoreContext dbContext = new OCPPCoreContext(Configuration))
                        {
                            ChargeTag ct = dbContext.Find<ChargeTag>(idTag);
                            if (ct != null)
                            {
                                if (ct.ExpiryDate.HasValue) startTransactionResponse.IdTagInfo.ExpiryDate = ct.ExpiryDate.Value;
                                startTransactionResponse.IdTagInfo.ParentIdTag = ct.ParentTagId;
                                if (ct.Blocked.HasValue && ct.Blocked.Value)
                                {
                                    startTransactionResponse.IdTagInfo.Status = IdTagInfoStatus.Blocked;
                                }
                                else if (ct.ExpiryDate.HasValue && ct.ExpiryDate.Value < DateTime.Now)
                                {
                                    startTransactionResponse.IdTagInfo.Status = IdTagInfoStatus.Expired;
                                }
                                else
                                {
                                    startTransactionResponse.IdTagInfo.Status = IdTagInfoStatus.Accepted;
                                }
                            }
                            else
                            {
                                startTransactionResponse.IdTagInfo.Status = IdTagInfoStatus.Invalid;
                            }

                            Logger.LogInformation("StartTransaction => Charge tag='{0}' => Status: {1}", idTag, startTransactionResponse.IdTagInfo.Status);
                        }
                    }
                    catch (Exception exp)
                    {
                        Logger.LogError(exp, "StartTransaction => Exception reading charge tag ({0}): {1}", idTag, exp.Message);
                        startTransactionResponse.IdTagInfo.Status = IdTagInfoStatus.Invalid;
                    }
                }

                //Update Status Connector
                //if (connectorId > 0)
                //{
                //    // Update meter value in db connector status 
                //    UpdateConnectorStatus(connectorId, ConnectorStatusEnum.Charging.ToString(), startTransactionRequest.Timestamp, (double)startTransactionRequest.MeterStart / 1000, 0, 0, startTransactionRequest.Timestamp);
                //}

                if (startTransactionResponse.IdTagInfo.Status == IdTagInfoStatus.Accepted)
                {
                    try
                    {
                        using (OCPPCoreContext dbContext = new OCPPCoreContext(Configuration))
                        {
                            Transaction transaction = new Transaction();
                            transaction.ChargePointId = ChargePointStatus?.Id;
                            transaction.ConnectorId = startTransactionRequest.ConnectorId;
                            transaction.StartTagId = idTag;
                            transaction.StartTime = startTransactionRequest.Timestamp.DateTime;
                            transaction.MeterStart = (double)startTransactionRequest.MeterStart / 1000; // Meter value here is always Wh
                            transaction.StartResult = startTransactionResponse.IdTagInfo.Status.ToString();
                            dbContext.Add<Transaction>(transaction);
                            dbContext.SaveChanges();


                            if (startTransactionRequest.ReservationId > 0)
                            {
                                Reservation reservation = dbContext.Find<Reservation>(startTransactionRequest.ReservationId);
                                reservation.Status = true;
                                reservation.StatusReason = "StartTransaction";
                                dbContext.Update<Reservation>(reservation);
                                dbContext.SaveChanges();
                            }

                            // Return DB-ID as transaction ID
                            startTransactionResponse.TransactionId = transaction.TransactionId;
                        }
                    }
                    catch (Exception exp)
                    {
                        Logger.LogError(exp, "StartTransaction => Exception writing transaction: chargepoint={0} / tag={1}", ChargePointStatus?.Id, idTag);
                        errorCode = ErrorCodes.InternalError;
                    }
                }

                msgOut.JsonPayload = JsonConvert.SerializeObject(startTransactionResponse);
                Logger.LogTrace("StartTransaction => Response serialized");
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "StartTransaction => Exception: {0}", exp.Message);
                errorCode = ErrorCodes.FormationViolation;
            }
            WriteMessageLog(ChargePointStatus?.Id, connectorId, "CP", "Request", msgIn.Action, $"TagID=>{startTransactionRequest.IdTag}", errorCode);
            WriteMessageLog(ChargePointStatus?.Id, connectorId, "SV", "Response", msgIn.Action, startTransactionResponse.IdTagInfo?.Status.ToString(), errorCode);
            return errorCode;
        }
    }
}
