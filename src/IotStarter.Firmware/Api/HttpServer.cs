using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Shared;

namespace IotStarter.Firmware.Api
{
    /// <summary>A parsed HTTP request (small, flat: method, path, ApiKey header, body).</summary>
    public class HttpRequest
    {
        public string Method;
        public string Path;
        public string ApiKey;
        public byte[] Body;

        /// <summary>True when the declared body exceeded <see cref="HttpContract.MaxRequestBodyBytes"/>.</summary>
        public bool BodyTooLarge;
    }

    /// <summary>The response a handler fills in; written by <see cref="HttpServer"/>.</summary>
    public class HttpResponse
    {
        public int Status = 200;
        public string Json;
    }

    /// <summary>Handles one request.</summary>
    public delegate void HttpHandler(HttpRequest request, HttpResponse response);

    /// <summary>
    /// Minimal HTTP/1.1 server on a plain socket. It replaces nanoFramework.WebServer and
    /// System.Net.Http (about 48 KB of deployed code), which did not fit in the managed heap next to
    /// the BLE stack on ESP32 boards without PSRAM. One connection at a time, "Connection: close",
    /// request head up to 1 KB, body up to 256 bytes. Requests are handled one after another, which
    /// also serialises access to the outputs and settings.
    /// </summary>
    public class HttpServer
    {
        private const int MaxHeadBytes = 1024;
        private const int SocketTimeoutMs = 5000;

        private readonly HttpHandler _handler;
        private Socket _listener;

        public HttpServer(HttpHandler handler)
        {
            _handler = handler;
        }

        public void Start(int port)
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Any, port));
            _listener.Listen(2);

            Thread thread = new Thread(AcceptLoop);
            thread.Start();
            Log.Info("HTTP API listening on port " + port.ToString());
        }

        private void AcceptLoop()
        {
            while (true)
            {
                Socket client = null;
                try
                {
                    client = _listener.Accept();
                    client.ReceiveTimeout = SocketTimeoutMs;
                    client.SendTimeout = SocketTimeoutMs;
                    Serve(client);
                }
                catch (SocketException ex)
                {
                    // Client went away or the network dropped; keep listening.
                    Log.Warn("HTTP: socket error " + ex.ErrorCode.ToString());
                }
                catch (Exception ex)
                {
                    Log.Error("HTTP: request failed", ex);
                }
                finally
                {
                    if (client != null)
                    {
                        try
                        {
                            client.Close();
                        }
                        catch (Exception)
                        {
                            // Already closed.
                        }
                    }
                }
            }
        }

        private void Serve(Socket client)
        {
            HttpRequest request = ReadRequest(client);
            HttpResponse response = new HttpResponse();
            if (request == null)
            {
                response.Status = 400;
                response.Json = ErrorJson("malformed request");
            }
            else if (request.BodyTooLarge)
            {
                response.Status = 413;
                response.Json = ErrorJson("body too large");
            }
            else
            {
                try
                {
                    _handler(request, response);
                }
                catch (Exception ex)
                {
                    Log.Error("HTTP: handler failed", ex);
                    response.Status = 500;
                    response.Json = null;
                }
            }

            WriteResponse(client, response);
        }

        /// <summary>Reads the request head and body. Returns null for anything malformed.</summary>
        private static HttpRequest ReadRequest(Socket client)
        {
            byte[] head = new byte[MaxHeadBytes];
            int length = 0;
            int headEnd = -1;
            while (headEnd < 0)
            {
                if (length == head.Length)
                {
                    return null;
                }

                int n = client.Receive(head, length, head.Length - length, SocketFlags.None);
                if (n <= 0)
                {
                    return null;
                }

                length += n;
                headEnd = FindHeadEnd(head, length);
            }

            string text = Encoding.UTF8.GetString(head, 0, headEnd);
            string[] lines = text.Split('\n');
            string[] requestLine = lines[0].Trim().Split(' ');
            if (requestLine.Length < 2)
            {
                return null;
            }

            HttpRequest request = new HttpRequest();
            request.Method = requestLine[0];
            string target = requestLine[1];
            int query = target.IndexOf('?');
            request.Path = (query >= 0 ? target.Substring(0, query) : target).ToLower();

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, colon).Trim().ToLower();
                string value = line.Substring(colon + 1).Trim();
                if (name == "content-length")
                {
                    if (!TryParseInt(value, out contentLength))
                    {
                        return null;
                    }
                }
                else if (name == "apikey")
                {
                    request.ApiKey = value;
                }
            }

            if (contentLength > HttpContract.MaxRequestBodyBytes)
            {
                request.BodyTooLarge = true;
                return request;
            }

            byte[] body = new byte[contentLength];
            int bodyStart = headEnd + 4;
            int already = length - bodyStart;
            if (already > contentLength)
            {
                already = contentLength;
            }

            Array.Copy(head, bodyStart, body, 0, already);
            int read = already;
            while (read < contentLength)
            {
                int n = client.Receive(body, read, contentLength - read, SocketFlags.None);
                if (n <= 0)
                {
                    return null;
                }

                read += n;
            }

            request.Body = body;
            return request;
        }

        private static int FindHeadEnd(byte[] data, int length)
        {
            for (int i = 3; i < length; i++)
            {
                if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private static bool TryParseInt(string text, out int value)
        {
            value = 0;
            if (text.Length == 0 || text.Length > 6)
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

                value = (value * 10) + (c - '0');
            }

            return true;
        }

        private static void WriteResponse(Socket client, HttpResponse response)
        {
            byte[] body = response.Json == null ? new byte[0] : Encoding.UTF8.GetBytes(response.Json);
            string head = "HTTP/1.1 " + response.Status.ToString() + " " + Reason(response.Status) + "\r\n"
                + (body.Length > 0 ? "Content-Type: application/json\r\n" : string.Empty)
                + "Content-Length: " + body.Length.ToString() + "\r\n"
                + "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.UTF8.GetBytes(head);
            client.Send(headBytes, 0, headBytes.Length, SocketFlags.None);
            if (body.Length > 0)
            {
                client.Send(body, 0, body.Length, SocketFlags.None);
            }
        }

        public static string ErrorJson(string message)
        {
            return new Storage.JsonWriter().BeginObject().String(HttpContract.FieldError, message).EndObject().ToString();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200:
                    return "OK";
                case 202:
                    return "Accepted";
                case 204:
                    return "No Content";
                case 400:
                    return "Bad Request";
                case 401:
                    return "Unauthorized";
                case 404:
                    return "Not Found";
                case 405:
                    return "Method Not Allowed";
                case 413:
                    return "Payload Too Large";
                default:
                    return "Internal Server Error";
            }
        }
    }
}
