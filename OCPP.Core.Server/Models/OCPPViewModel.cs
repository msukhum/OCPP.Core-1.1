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

using OCPP.Core.Database;
using OCPP.Core.Server.Messages_OCPP16;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Models
{
    public class OCPPViewModel
    {
        public List<Reservation> Reservations { get; set; }
        public List<ChargePoint> ChargePoints { get; set; }
        public List<ConnectorStatus> ConnectorStatuses { get; set; }
        public List<ResetRequestType> ResetRequestTypes { get; set; }
        public List<ChangeAvailabilityRequestType> ChangeAvailabilityRequestTypes { get; set; }
        public List<ChargingProfilePurpose> ChargingProfilePurposes { get; set; }
        public List<ChargingRateUnit> ChargingRateUnits { get; set; }
        public List<UpdateType> UpdateTypes { get; set; }
    }
}
