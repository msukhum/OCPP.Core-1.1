using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Messages_OCPP16
{
    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.3.1.0 (Newtonsoft.Json v9.0.0.0)")]
    public partial class SendLocalListRequest
    {
        [Newtonsoft.Json.JsonProperty("listVersion", Required = Newtonsoft.Json.Required.Always)]
        public int ListVersion { get; set; }

        [Newtonsoft.Json.JsonProperty("localAuthorizationList", Required = Newtonsoft.Json.Required.Always)]
        public List<LocalAuthorizationList> LocalAuthorizationList { get; set; }

        [Newtonsoft.Json.JsonProperty("updateType", Required = Newtonsoft.Json.Required.Always)]
        public string UpdateType { get; set; }
    }

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.3.1.0 (Newtonsoft.Json v9.0.0.0)")]
    public partial class LocalAuthorizationList
    {
        [Newtonsoft.Json.JsonProperty("idTag", Required = Newtonsoft.Json.Required.Always)]
        [System.ComponentModel.DataAnnotations.StringLength(20)]
        public string IdTag { get; set; }

        [Newtonsoft.Json.JsonProperty("idTagInfo", Required = Newtonsoft.Json.Required.DisallowNull, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public IdTagInfo IdTagInfo { get; set; }
    }

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.3.1.0 (Newtonsoft.Json v9.0.0.0)")]
    public enum UpdateType
    {
        [System.Runtime.Serialization.EnumMember(Value = @"Differential")]
        Differential = 0,

        [System.Runtime.Serialization.EnumMember(Value = @"Full")]
        Full = 1
    }
}
