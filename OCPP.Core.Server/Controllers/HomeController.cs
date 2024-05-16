using OCPP.Core.Server.Models;
using Microsoft.AspNetCore.Mvc;

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace OCPP.Core.Server.Controllers
{
    public partial class HomeController : BaseController
    {
        public HomeController(UserManager userManager, ILoggerFactory loggerFactory, IConfiguration config) : base(userManager, loggerFactory, config)
        {
            Logger = loggerFactory.CreateLogger<HomeController>();

            Constants.RefreshTime = "60";
        }
    }
}
