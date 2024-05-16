using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Messages_OCPP16
{
    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.3.1.0 (Newtonsoft.Json v9.0.0.0)")]
    public partial class TriggerMessageRequest
    {
        [Newtonsoft.Json.JsonProperty("requestedMessage", Required = Newtonsoft.Json.Required.Always)]
        public RequestedMessage RequestedMessage { get; set; }

        [Newtonsoft.Json.JsonProperty("connectorId", Required = Newtonsoft.Json.Required.DisallowNull, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public int ConnectorId { get; set; }
    }

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.3.1.0 (Newtonsoft.Json v9.0.0.0)")]
    public enum RequestedMessage
    {
        [System.Runtime.Serialization.EnumMember(Value = @"BootNotification")]
        BootNotification = 0,

        [System.Runtime.Serialization.EnumMember(Value = @"DiagnosticsStatusNotification")]
        DiagnosticsStatusNotification = 1,

        [System.Runtime.Serialization.EnumMember(Value = @"FirmwareStatusNotification")]
        FirmwareStatusNotification = 2,

        [System.Runtime.Serialization.EnumMember(Value = @"Heartbeat")]
        Heartbeat = 3,

        [System.Runtime.Serialization.EnumMember(Value = @"MeterValues")]
        MeterValues = 4,

        [System.Runtime.Serialization.EnumMember(Value = @"StatusNotification")]
        StatusNotification = 5
    }
}
