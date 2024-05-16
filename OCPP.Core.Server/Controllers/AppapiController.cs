using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace OCPP.Core.Server.Controllers
{
    public partial class AppapiController : BaseController
    {
        protected UserManager UserManager { get; private set; }

        protected ILogger Logger { get; set; }

        protected IConfiguration Config { get; private set; }

        public AppapiController(UserManager userManager, ILoggerFactory loggerFactory, IConfiguration config) : base(userManager, loggerFactory, config)
        {
            Logger = loggerFactory.CreateLogger<AppapiController>();
            Config = config;
            UserManager = userManager;
        }
    }

    public static class Extensions
    {
        public static StringContent AsJson(this object o)
            => new StringContent(JsonConvert.SerializeObject(o), Encoding.UTF8, "application/json");
    }
}
