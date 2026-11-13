using System;
using System.IO;
using System.Net;
using System.Text;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;
using nanoFramework.WebServer;

namespace IotStarter.Firmware.Api
{
    /// <summary>Request-body reading and JSON responses with explicit status codes.</summary>
    public static class HttpIo
    {
        /// <summary>
        /// Reads and parses a flat JSON body of at most 256 bytes. On failure writes 400 (or 413) and
        /// returns null.
        /// </summary>
        public static FlatJsonObject ReadJsonBody(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            long length = request.ContentLength64;
            if (length <= 0)
            {
                WriteError(context.Response, HttpStatusCode.BadRequest, "empty body");
                return null;
            }

            if (length > HttpContract.MaxRequestBodyBytes)
            {
                WriteError(context.Response, HttpStatusCode.RequestEntityTooLarge, "body too large");
                return null;
            }

            byte[] body = new byte[(int)length];
            int read = 0;
            try
            {
                Stream input = request.InputStream;
                while (read < body.Length)
                {
                    int n = input.Read(body, read, body.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }
            }
            catch (Exception ex)
            {
                Log.Error("HTTP: reading the body failed", ex);
                WriteError(context.Response, HttpStatusCode.BadRequest, "unreadable body");
                return null;
            }

            if (read != body.Length)
            {
                WriteError(context.Response, HttpStatusCode.BadRequest, "truncated body");
                return null;
            }

            string text;
            try
            {
                text = Encoding.UTF8.GetString(body, 0, body.Length);
            }
            catch (Exception)
            {
                WriteError(context.Response, HttpStatusCode.BadRequest, "body is not UTF-8");
                return null;
            }

            FlatJsonObject json = FlatJsonObject.Parse(text);
            if (json == null)
            {
                WriteError(context.Response, HttpStatusCode.BadRequest, "body is not a flat JSON object");
            }

            return json;
        }

        public static void WriteJson(HttpListenerResponse response, HttpStatusCode status, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = (int)status;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        public static void WriteError(HttpListenerResponse response, HttpStatusCode status, string message)
        {
            WriteJson(response, status, new JsonWriter().BeginObject().String(HttpContract.FieldError, message).EndObject().ToString());
        }

        public static void WriteStatus(HttpListenerResponse response, HttpStatusCode status)
        {
            WebServer.OutputHttpCode(response, status);
        }
    }
}
