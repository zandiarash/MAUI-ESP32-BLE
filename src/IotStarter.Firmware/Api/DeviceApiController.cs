using System;
using System.Net;
using System.Text;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Normal;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;
using nanoFramework.WebServer;

namespace IotStarter.Firmware.Api
{
    /// <summary>
    /// Token-protected routes. [Authentication("ApiKey")] makes nanoFramework.WebServer compare the
    /// "ApiKey" header with WebServer.ApiKey and answer 401 when it is missing or wrong.
    /// </summary>
    [Authentication("ApiKey")]
    public class DeviceApiController
    {
        [Route(HttpContract.RouteOutputs)]
        [Method("GET")]
        public void GetOutputs(WebServerEventArgs e)
        {
            try
            {
                ModelDefinition model = DeviceContext.Model;
                bool[] states = DeviceContext.Outputs.Snapshot();

                JsonWriter writer = new JsonWriter().BeginObject().BeginArray(HttpContract.FieldOutputs);
                for (int i = 0; i < model.Outputs.Length; i++)
                {
                    writer.BeginObject()
                        .Number(HttpContract.FieldId, model.Outputs[i].Id)
                        .String(HttpContract.FieldLabel, model.Outputs[i].Label)
                        .Bool(HttpContract.FieldOn, states[i])
                        .EndObject();
                }

                writer.EndArray().EndObject();
                HttpIo.WriteJson(e.Context.Response, HttpStatusCode.OK, writer.ToString());
            }
            catch (Exception ex)
            {
                Log.Error("HTTP GET /outputs failed", ex);
                HttpIo.WriteStatus(e.Context.Response, HttpStatusCode.InternalServerError);
            }
        }

        [Route(HttpContract.RouteOutput)]
        [Method("PUT")]
        public void PutOutput(WebServerEventArgs e)
        {
            try
            {
                int outputId;
                OutputDefinition output = null;
                if (TryParseOutputId(e.GetRouteParameter("id"), out outputId))
                {
                    output = DeviceContext.Model.FindOutput(outputId);
                }

                if (output == null)
                {
                    HttpIo.WriteError(e.Context.Response, HttpStatusCode.NotFound, "unknown output");
                    return;
                }

                FlatJsonObject body = HttpIo.ReadJsonBody(e.Context);
                if (body == null)
                {
                    return;
                }

                bool on;
                if (!body.TryGetBool(HttpContract.FieldOn, out on))
                {
                    HttpIo.WriteError(e.Context.Response, HttpStatusCode.BadRequest, "\"on\" must be true or false");
                    return;
                }

                if (!DeviceContext.Outputs.TrySet(outputId, on))
                {
                    HttpIo.WriteError(e.Context.Response, HttpStatusCode.NotFound, "unknown output");
                    return;
                }

                bool actual;
                DeviceContext.Outputs.TryGet(outputId, out actual);
                Log.Info("Output " + outputId.ToString() + " -> " + (actual ? "on" : "off"));

                string json = new JsonWriter()
                    .BeginObject()
                    .Number(HttpContract.FieldId, output.Id)
                    .String(HttpContract.FieldLabel, output.Label)
                    .Bool(HttpContract.FieldOn, actual)
                    .EndObject()
                    .ToString();
                HttpIo.WriteJson(e.Context.Response, HttpStatusCode.OK, json);
            }
            catch (Exception ex)
            {
                Log.Error("HTTP PUT /outputs/{id} failed", ex);
                HttpIo.WriteStatus(e.Context.Response, HttpStatusCode.InternalServerError);
            }
        }

        [Route(HttpContract.RouteName)]
        [Method("PUT")]
        public void PutName(WebServerEventArgs e)
        {
            try
            {
                FlatJsonObject body = HttpIo.ReadJsonBody(e.Context);
                if (body == null)
                {
                    return;
                }

                string name;
                if (!body.TryGetString(HttpContract.FieldName, out name))
                {
                    HttpIo.WriteError(e.Context.Response, HttpStatusCode.BadRequest, "\"name\" must be a string");
                    return;
                }

                name = name.Trim();
                int bytes = Encoding.UTF8.GetBytes(name).Length;
                if (bytes == 0 || bytes > HttpContract.MaxDeviceNameBytes || HasControlCharacters(name))
                {
                    HttpIo.WriteError(e.Context.Response, HttpStatusCode.BadRequest, "name must be 1-32 bytes without control characters");
                    return;
                }

                if (!DeviceContext.Settings.SetName(name))
                {
                    HttpIo.WriteError(e.Context.Response, HttpStatusCode.InternalServerError, "could not save");
                    return;
                }

                string json = new JsonWriter().BeginObject().String(HttpContract.FieldName, name).EndObject().ToString();
                HttpIo.WriteJson(e.Context.Response, HttpStatusCode.OK, json);
            }
            catch (Exception ex)
            {
                Log.Error("HTTP PUT /name failed", ex);
                HttpIo.WriteStatus(e.Context.Response, HttpStatusCode.InternalServerError);
            }
        }

        [Route(HttpContract.RouteIdentify)]
        [Method("POST")]
        public void PostIdentify(WebServerEventArgs e)
        {
            DeviceContext.Led.Identify();
            HttpIo.WriteStatus(e.Context.Response, HttpStatusCode.NoContent);
        }

        [Route(HttpContract.RouteReboot)]
        [Method("POST")]
        public void PostReboot(WebServerEventArgs e)
        {
            HttpIo.WriteStatus(e.Context.Response, HttpStatusCode.Accepted);
            DeviceContext.ScheduleRestart();
        }

        [Route(HttpContract.RouteFactoryReset)]
        [Method("POST")]
        public void PostFactoryReset(WebServerEventArgs e)
        {
            HttpIo.WriteStatus(e.Context.Response, HttpStatusCode.Accepted);
            DeviceContext.ScheduleFactoryReset();
        }

        private static bool TryParseOutputId(string text, out int id)
        {
            id = 0;
            if (text == null || text.Length == 0 || text.Length > 3)
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }

                id = (id * 10) + (c - '0');
            }

            return true;
        }

        private static bool HasControlCharacters(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < 0x20 || text[i] == 0x7F)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
