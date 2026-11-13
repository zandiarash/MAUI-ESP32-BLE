using System;
using System.Net;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Normal;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;
using nanoFramework.WebServer;

namespace IotStarter.Firmware.Api
{
    /// <summary>
    /// GET /api/v1/info: identity only, no secrets, no authentication. Used by the app to confirm
    /// that the device at an address is the one it expects. Controller style from the
    /// WebServer.GpioRest sample.
    /// </summary>
    public class InfoController
    {
        [Route(HttpContract.RouteInfo)]
        [Method("GET")]
        public void GetInfo(WebServerEventArgs e)
        {
            try
            {
                string json = new JsonWriter()
                    .BeginObject()
                    .String(HttpContract.FieldId, DeviceContext.DeviceId)
                    .Number(HttpContract.FieldModel, DeviceContext.Model.Id)
                    .String(HttpContract.FieldName, DeviceContext.DisplayName)
                    .String(HttpContract.FieldFirmware, FirmwareConfig.FirmwareVersionString)
                    .Number(HttpContract.FieldApi, ProtocolConstants.ApiVersion)
                    .EndObject()
                    .ToString();
                HttpIo.WriteJson(e.Context.Response, HttpStatusCode.OK, json);
            }
            catch (Exception ex)
            {
                Log.Error("HTTP GET /info failed", ex);
                HttpIo.WriteStatus(e.Context.Response, HttpStatusCode.InternalServerError);
            }
        }
    }
}
