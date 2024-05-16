using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Messages_OCPP16
{
    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.3.1.0 (Newtonsoft.Json v9.0.0.0)")]
    public partial class DiagnosticsStatusNotificationRequest
    {
        [Newtonsoft.Json.JsonProperty("status", Required = Newtonsoft.Json.Required.Always)]
        public DiagnosticsStatusNotificationRequestStatus Status { get; set; }
    }

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.3.1.0 (Newtonsoft.Json v9.0.0.0)")]
    public enum DiagnosticsStatusNotificationRequestStatus
    {
        [System.Runtime.Serialization.EnumMember(Value = @"Idle")]
        Idle = 0,

        [System.Runtime.Serialization.EnumMember(Value = @"Uploaded")]
        Uploaded = 1,

        [System.Runtime.Serialization.EnumMember(Value = @"UploadFailed")]
        UploadFailed = 2,

        [System.Runtime.Serialization.EnumMember(Value = @"Uploading")]
        Uploading = 3
    }
}
