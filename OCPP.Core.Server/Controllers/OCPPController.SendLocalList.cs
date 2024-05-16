using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OCPP.Core.Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OCPP.Core.Database;
using Newtonsoft.Json;
using OCPP.Core.Server.Messages_OCPP16;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net;

namespace OCPP.Core.Server.Controllers
{
    public partial class OCPPController : BaseController
    {
        [Authorize]
        public IActionResult SendLocalList()
        {
            OCPPViewModel model = new OCPPViewModel();

            using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
            {
                model.ChargePoints = dbContext.ChargePoints.ToList<ChargePoint>();
            }
            model.UpdateTypes = Enum.GetValues(typeof(UpdateType)).Cast<UpdateType>().ToList();

            return View(model);
        }

        public IActionResult GetSendLocalListJson(string[] IdTags , int version , string updatetype, string status)
        {
            SendLocalListRequest request = new SendLocalListRequest();

            Enum.TryParse(updatetype, out UpdateType myStatus);
            request.UpdateType = updatetype;
            request.ListVersion = version;
            request.LocalAuthorizationList = new List<LocalAuthorizationList>();
            foreach (string IdTag in IdTags)
            {
                LocalAuthorizationList list = new LocalAuthorizationList();
                list.IdTag = IdTag;
                list.IdTagInfo = new IdTagInfo();

                using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
                {
                    ChargeTag ChargeTag = dbContext.ChargeTags.Where(x => x.TagId == IdTag).FirstOrDefault();
                    list.IdTagInfo.ExpiryDate = ChargeTag.ExpiryDate.Value;
                    //list.IdTagInfo.Status = DateTime.Now > ChargeTag.ExpiryDate.Value ? IdTagInfoStatus.Expired : IdTagInfoStatus.Accepted;
                }
                list.IdTagInfo.Status = status == "Expired" ? IdTagInfoStatus.Expired : IdTagInfoStatus.Accepted;
                request.LocalAuthorizationList.Add(list);
            }

            string json = JsonConvert.SerializeObject(request);

            return new JsonResult(json);
        }

        public async Task<IActionResult> SendLocalList2ChargePoint(string id, string[] IdTags, int version, string updatetype, string status)
        {
            string jsonResult = null;
            Logger.LogTrace("SendLocalList: Request to restart chargepoint '{0}'", id);
            SendLocalListRequest request = new SendLocalListRequest();

            //Enum.TryParse(updatetype, out UpdateType myStatus);
            request.UpdateType = updatetype;
            request.ListVersion = version;
            request.LocalAuthorizationList = new List<LocalAuthorizationList>();
            foreach (string IdTag in IdTags)
            {
                LocalAuthorizationList list = new LocalAuthorizationList();
                list.IdTag = IdTag;
                list.IdTagInfo = new IdTagInfo();

                using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
                {
                    ChargeTag ChargeTag = dbContext.ChargeTags.Where(x => x.TagId == IdTag).FirstOrDefault();
                    list.IdTagInfo.ExpiryDate = ChargeTag.ExpiryDate.Value;
                    //list.IdTagInfo.Status = DateTime.Now > ChargeTag.ExpiryDate.Value ? IdTagInfoStatus.Expired : IdTagInfoStatus.Accepted;
                }
                list.IdTagInfo.Status = status == "Expired" ? IdTagInfoStatus.Expired : IdTagInfoStatus.Accepted;
                request.LocalAuthorizationList.Add(list);
            }

            try
            {
                string serverApiUrl = base.Config.GetValue<string>("ServerApiUrl");
                string apiKeyConfig = base.Config.GetValue<string>("ApiKey");
                using (var httpClient = new HttpClient())
                {
                    if (!serverApiUrl.EndsWith('/'))
                    {
                        serverApiUrl += "/";
                    }
                    Uri uri = new Uri(serverApiUrl);
                    uri = new Uri(uri, $"SendLocalList/{Uri.EscapeUriString(id)}");
                    httpClient.Timeout = new TimeSpan(0, 0, 30); // use short timeout

                    // API-Key authentication?
                    if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                    {
                        httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                    }
                    else
                    {
                        Logger.LogWarning("SendLocalList: No API-Key configured!");
                    }

                    //HttpResponseMessage response = await httpClient.GetAsync(uri,);
                    HttpResponseMessage response = await httpClient.PostAsync(uri, request.AsJson());
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        jsonResult = await response.Content.ReadAsStringAsync();
                        
                    }
                    else if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Chargepoint offline
                        // httpStatuscode = (int)HttpStatusCode.OK;
                        //resultContent = "The charging station is offline and cannot be restarted.";
                    }
                    else
                    {
                        Logger.LogError("SendLocalList: Result of API  request => httpStatus={0}", response.StatusCode);
                        //httpStatuscode = (int)HttpStatusCode.OK;
                        // resultContent = "An error has occurred.";
                    }
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "SendLocalList: Error in API request => {0}", exp.Message);
                //httpStatuscode = (int)HttpStatusCode.OK;
                //resultContent = "An error has occurred.";
            }

            return new JsonResult(jsonResult);
        }

    }
}
