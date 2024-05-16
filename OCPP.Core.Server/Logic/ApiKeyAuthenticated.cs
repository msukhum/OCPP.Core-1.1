using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OCPP.Core.Server.Logic
{
    public class ApiKeyAuthenticated
    {
        public static bool GetApiKeyAuthenticated(IHeaderDictionary headers, IConfiguration Config)
        {
            bool isAuth = false;
            string token = string.Empty;
            string resultContent = string.Empty;

            if (headers.ContainsKey("X-API-Key"))
            {
                token = headers["X-API-Key"];
            }

            if (!string.IsNullOrEmpty(token))
            {
                string apiKeyConfig = Config.GetValue<string>("ApiKey");
                // API-Key authentication?
                if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                {
                    if (token == apiKeyConfig)
                    {
                        isAuth = true;
                    }
                }
            }

            return isAuth;
        }
    }
}
