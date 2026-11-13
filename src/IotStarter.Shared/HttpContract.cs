// Shared between firmware and app. Conservative C# subset only.
#nullable disable

namespace IotStarter.Shared
{
    /// <summary>HTTP routes, header and JSON field names of the local API.</summary>
    public static class HttpContract
    {
        public const string BasePath = "/api/v1";

        /// <summary>Header carrying the API token (lowercase hex). Name fixed by nanoFramework.WebServer.</summary>
        public const string ApiKeyHeader = "ApiKey";

        // Routes as registered on the device (relative, no leading slash, lowercase).
        public const string RouteInfo = "api/v1/info";
        public const string RouteOutputs = "api/v1/outputs";
        public const string RouteOutput = "api/v1/outputs/{id}";
        public const string RouteName = "api/v1/name";
        public const string RouteIdentify = "api/v1/system/identify";
        public const string RouteReboot = "api/v1/system/reboot";
        public const string RouteFactoryReset = "api/v1/system/factory-reset";

        // Paths as used by the client.
        public const string PathInfo = "/api/v1/info";
        public const string PathOutputs = "/api/v1/outputs";
        public const string PathOutputPrefix = "/api/v1/outputs/";
        public const string PathName = "/api/v1/name";
        public const string PathIdentify = "/api/v1/system/identify";
        public const string PathReboot = "/api/v1/system/reboot";
        public const string PathFactoryReset = "/api/v1/system/factory-reset";

        // JSON field names.
        public const string FieldId = "id";
        public const string FieldModel = "model";
        public const string FieldName = "name";
        public const string FieldFirmware = "fw";
        public const string FieldApi = "api";
        public const string FieldPort = "port";
        public const string FieldOutputs = "outputs";
        public const string FieldLabel = "label";
        public const string FieldOn = "on";
        public const string FieldRssi = "rssi";
        public const string FieldError = "error";

        /// <summary>Longest device name accepted by PUT /name (UTF-8 bytes).</summary>
        public const int MaxDeviceNameBytes = 32;

        /// <summary>Largest request body the device reads.</summary>
        public const int MaxRequestBodyBytes = 256;

        // mDNS TXT keys.
        public const string TxtId = "id";
        public const string TxtModel = "model";
        public const string TxtApi = "api";
    }
}
