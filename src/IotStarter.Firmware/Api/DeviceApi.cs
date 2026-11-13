using System;
using System.Text;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Normal;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;

namespace IotStarter.Firmware.Api
{
    /// <summary>
    /// The /api/v1 routes (docs/protocol.md §7). Everything except GET /info needs the ApiKey header,
    /// compared in constant time. Unknown path: 404. Known path, wrong method: 405.
    /// </summary>
    public static class DeviceApi
    {
        private const string Base = "/api/v1/";

        public static void Handle(HttpRequest request, HttpResponse response)
        {
            string path = request.Path;
            if (!path.StartsWith(Base))
            {
                NotFound(response);
                return;
            }

            string route = path.Substring(Base.Length);
            if (route == "info")
            {
                if (Allow(request, response, "GET"))
                {
                    GetInfo(response);
                }

                return;
            }

            bool known = route == "outputs" || route.StartsWith("outputs/") || route == "name"
                || route == "system/identify" || route == "system/reboot" || route == "system/factory-reset";
            if (!known)
            {
                NotFound(response);
                return;
            }

            if (!IsAuthorized(request.ApiKey))
            {
                response.Status = 401;
                response.Json = HttpServer.ErrorJson("missing or wrong ApiKey");
                return;
            }

            if (route == "outputs")
            {
                if (Allow(request, response, "GET"))
                {
                    GetOutputs(response);
                }
            }
            else if (route.StartsWith("outputs/"))
            {
                if (Allow(request, response, "PUT"))
                {
                    PutOutput(route.Substring("outputs/".Length), request, response);
                }
            }
            else if (route == "name")
            {
                if (Allow(request, response, "PUT"))
                {
                    PutName(request, response);
                }
            }
            else if (route == "system/identify")
            {
                if (Allow(request, response, "POST"))
                {
                    DeviceContext.Led.Identify();
                    response.Status = 204;
                }
            }
            else if (route == "system/reboot")
            {
                if (Allow(request, response, "POST"))
                {
                    response.Status = 202;
                    DeviceContext.ScheduleRestart();
                }
            }
            else if (Allow(request, response, "POST"))
            {
                response.Status = 202;
                DeviceContext.ScheduleFactoryReset();
            }
        }

        private static void GetInfo(HttpResponse response)
        {
            response.Json = new JsonWriter()
                .BeginObject()
                .String(HttpContract.FieldId, DeviceContext.DeviceId)
                .Number(HttpContract.FieldModel, DeviceContext.Model.Id)
                .String(HttpContract.FieldName, DeviceContext.DisplayName)
                .String(HttpContract.FieldFirmware, FirmwareConfig.FirmwareVersionString)
                .Number(HttpContract.FieldApi, ProtocolConstants.ApiVersion)
                .EndObject()
                .ToString();
        }

        private static void GetOutputs(HttpResponse response)
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

            response.Json = writer.EndArray().EndObject().ToString();
        }

        private static void PutOutput(string idText, HttpRequest request, HttpResponse response)
        {
            int outputId;
            OutputDefinition output = TryParseId(idText, out outputId) ? DeviceContext.Model.FindOutput(outputId) : null;
            if (output == null)
            {
                NotFound(response);
                return;
            }

            FlatJsonObject body = ParseBody(request, response);
            bool on;
            if (body == null)
            {
                return;
            }

            if (!body.TryGetBool(HttpContract.FieldOn, out on))
            {
                BadRequest(response, "\"on\" must be true or false");
                return;
            }

            DeviceContext.Outputs.TrySet(outputId, on);
            bool actual;
            DeviceContext.Outputs.TryGet(outputId, out actual);
            Log.Info("Output " + outputId.ToString() + " -> " + (actual ? "on" : "off"));
            response.Json = new JsonWriter()
                .BeginObject()
                .Number(HttpContract.FieldId, output.Id)
                .String(HttpContract.FieldLabel, output.Label)
                .Bool(HttpContract.FieldOn, actual)
                .EndObject()
                .ToString();
        }

        private static void PutName(HttpRequest request, HttpResponse response)
        {
            FlatJsonObject body = ParseBody(request, response);
            string name;
            if (body == null)
            {
                return;
            }

            if (!body.TryGetString(HttpContract.FieldName, out name))
            {
                BadRequest(response, "\"name\" must be a string");
                return;
            }

            name = name.Trim();
            int bytes = Encoding.UTF8.GetBytes(name).Length;
            if (bytes == 0 || bytes > HttpContract.MaxDeviceNameBytes || HasControlCharacters(name))
            {
                BadRequest(response, "name must be 1-32 bytes without control characters");
                return;
            }

            if (!DeviceContext.Settings.SetName(name))
            {
                response.Status = 500;
                return;
            }

            response.Json = new JsonWriter().BeginObject().String(HttpContract.FieldName, name).EndObject().ToString();
        }

        private static FlatJsonObject ParseBody(HttpRequest request, HttpResponse response)
        {
            if (request.Body == null || request.Body.Length == 0)
            {
                BadRequest(response, "empty body");
                return null;
            }

            FlatJsonObject json = null;
            try
            {
                json = FlatJsonObject.Parse(Encoding.UTF8.GetString(request.Body, 0, request.Body.Length));
            }
            catch (Exception)
            {
                // Not UTF-8; reported below.
            }

            if (json == null)
            {
                BadRequest(response, "body is not a flat JSON object");
            }

            return json;
        }

        private static bool IsAuthorized(string apiKey)
        {
            string token = DeviceContext.Settings.TokenHex;
            if (apiKey == null || token == null)
            {
                return false;
            }

            return ByteHelpers.ConstantTimeEquals(Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes(token));
        }

        private static bool Allow(HttpRequest request, HttpResponse response, string method)
        {
            if (request.Method == method)
            {
                return true;
            }

            response.Status = 405;
            response.Json = HttpServer.ErrorJson("method not allowed");
            return false;
        }

        private static void NotFound(HttpResponse response)
        {
            response.Status = 404;
            response.Json = HttpServer.ErrorJson("not found");
        }

        private static void BadRequest(HttpResponse response, string message)
        {
            response.Status = 400;
            response.Json = HttpServer.ErrorJson(message);
        }

        private static bool TryParseId(string text, out int id)
        {
            id = 0;
            if (text.Length == 0 || text.Length > 3)
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
