using OCPP.Core.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Models
{
    public class ReservationListViewModel
    {
        public List<ChargePoint> ChargePoints { get; set; }

        public List<ConnectorStatus> ConnectorStatuses { get; set; }

        public Dictionary<string, ChargeTag> ChargeTags { get; set; }

        public string CurrentChargePointId { get; set; }

        public int CurrentConnectorId { get; set; }

        public List<Reservation> Reservations { get; set; }

        public int Timespan { get; set; }
    }
}
