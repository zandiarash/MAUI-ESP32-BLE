using System;
using System.IO;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Shared;

namespace IotStarter.Firmware.Storage
{
    /// <summary>
    /// The small settings record kept in the internal flash file system (I:\). The Wi-Fi
    /// credentials are NOT stored here; they live in nanoFramework's Wi-Fi configuration store.
    /// Thread-safe. Writes go to a temporary file first and then replace the record.
    /// </summary>
    public class SettingsStore
    {
        private const string KeyToken = "token";
        private const string KeyName = "name";
        private const string KeyPendingVerification = "pendingVerification";
        private const string KeyRecoveryWindow = "recoveryWindow";
        private const string KeyLastError = "lastError";

        private readonly object _lock = new object();
        private string _tokenHex;
        private string _name;
        private bool _pendingVerification;
        private bool _recoveryWindow;
        private ErrorCode _lastError = ErrorCode.None;

        /// <summary>API token as 64 lowercase hex characters, or null if none is stored.</summary>
        public string TokenHex
        {
            get
            {
                lock (_lock)
                {
                    return _tokenHex;
                }
            }
        }

        public bool HasValidToken
        {
            get
            {
                string token = TokenHex;
                return token != null && token.Length == ProtocolConstants.TokenHexLength && ByteHelpers.FromHex(token) != null;
            }
        }

        /// <summary>User-visible device name (may be null: the app then shows the model name).</summary>
        public string Name
        {
            get
            {
                lock (_lock)
                {
                    return _name;
                }
            }
        }

        /// <summary>Fallback strategy: credentials were stored but not yet verified.</summary>
        public bool PendingVerification
        {
            get
            {
                lock (_lock)
                {
                    return _pendingVerification;
                }
            }
        }

        /// <summary>Next boot opens the token-protected recovery provisioning window.</summary>
        public bool RecoveryWindow
        {
            get
            {
                lock (_lock)
                {
                    return _recoveryWindow;
                }
            }
        }

        /// <summary>Reason the last provisioning attempt failed (readable over BLE).</summary>
        public ErrorCode LastError
        {
            get
            {
                lock (_lock)
                {
                    return _lastError;
                }
            }
        }

        /// <summary>Loads the record. A missing or corrupt file yields defaults.</summary>
        public void Load()
        {
            lock (_lock)
            {
                ResetFields();
                try
                {
                    if (!File.Exists(FirmwareConfig.SettingsFile))
                    {
                        Log.Info("Settings: none stored");
                        return;
                    }

                    string json = File.ReadAllText(FirmwareConfig.SettingsFile);
                    FlatJsonObject obj = FlatJsonObject.Parse(json);
                    if (obj == null)
                    {
                        Log.Warn("Settings: file is corrupt, using defaults");
                        return;
                    }

                    string text;
                    if (obj.TryGetString(KeyToken, out text))
                    {
                        _tokenHex = text;
                    }

                    if (obj.TryGetString(KeyName, out text))
                    {
                        _name = text;
                    }

                    bool flag;
                    if (obj.TryGetBool(KeyPendingVerification, out flag))
                    {
                        _pendingVerification = flag;
                    }

                    if (obj.TryGetBool(KeyRecoveryWindow, out flag))
                    {
                        _recoveryWindow = flag;
                    }

                    int code;
                    if (obj.TryGetInt(KeyLastError, out code) && code <= 255)
                    {
                        _lastError = (ErrorCode)code;
                    }

                    Log.Info("Settings: loaded (token " + (_tokenHex == null ? "absent" : "present")
                        + ", pending=" + (_pendingVerification ? "yes" : "no")
                        + ", recovery=" + (_recoveryWindow ? "yes" : "no")
                        + ", lastError=" + ((int)_lastError).ToString() + ")");
                }
                catch (Exception ex)
                {
                    Log.Error("Settings: load failed, using defaults", ex);
                    ResetFields();
                }
            }
        }

        public bool SetToken(byte[] token)
        {
            lock (_lock)
            {
                _tokenHex = token == null ? null : ByteHelpers.ToHexLower(token, 0, token.Length);
                return SaveLocked();
            }
        }

        public bool SetName(string name)
        {
            lock (_lock)
            {
                _name = name;
                return SaveLocked();
            }
        }

        public bool SetPendingVerification(bool pending)
        {
            lock (_lock)
            {
                _pendingVerification = pending;
                return SaveLocked();
            }
        }

        public bool SetRecoveryWindow(bool open)
        {
            lock (_lock)
            {
                _recoveryWindow = open;
                return SaveLocked();
            }
        }

        public bool SetLastError(ErrorCode error)
        {
            lock (_lock)
            {
                _lastError = error;
                return SaveLocked();
            }
        }

        /// <summary>
        /// Stores a freshly provisioned token and clears the provisioning flags in one write.
        /// </summary>
        public bool CompleteProvisioning(byte[] token, bool pendingVerification)
        {
            lock (_lock)
            {
                _tokenHex = ByteHelpers.ToHexLower(token, 0, token.Length);
                _pendingVerification = pendingVerification;
                _recoveryWindow = false;
                _lastError = ErrorCode.None;
                return SaveLocked();
            }
        }

        /// <summary>Discards an unverified provisioning (fallback strategy failure).</summary>
        public bool DiscardProvisioning(ErrorCode reason)
        {
            lock (_lock)
            {
                _tokenHex = null;
                _pendingVerification = false;
                _recoveryWindow = false;
                _lastError = reason;
                return SaveLocked();
            }
        }

        /// <summary>Factory reset: deletes the record entirely.</summary>
        public void Erase()
        {
            lock (_lock)
            {
                ResetFields();
                try
                {
                    if (File.Exists(FirmwareConfig.SettingsTempFile))
                    {
                        File.Delete(FirmwareConfig.SettingsTempFile);
                    }

                    if (File.Exists(FirmwareConfig.SettingsFile))
                    {
                        File.Delete(FirmwareConfig.SettingsFile);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Settings: erase failed", ex);
                }
            }
        }

        private void ResetFields()
        {
            _tokenHex = null;
            _name = null;
            _pendingVerification = false;
            _recoveryWindow = false;
            _lastError = ErrorCode.None;
        }

        private bool SaveLocked()
        {
            try
            {
                if (!Directory.Exists(FirmwareConfig.SettingsDirectory))
                {
                    Directory.CreateDirectory(FirmwareConfig.SettingsDirectory);
                }

                string json = new JsonWriter()
                    .BeginObject()
                    .String(KeyToken, _tokenHex)
                    .String(KeyName, _name)
                    .Bool(KeyPendingVerification, _pendingVerification)
                    .Bool(KeyRecoveryWindow, _recoveryWindow)
                    .Number(KeyLastError, (int)_lastError)
                    .EndObject()
                    .ToString();

                File.WriteAllText(FirmwareConfig.SettingsTempFile, json);
                if (File.Exists(FirmwareConfig.SettingsFile))
                {
                    File.Delete(FirmwareConfig.SettingsFile);
                }

                File.Move(FirmwareConfig.SettingsTempFile, FirmwareConfig.SettingsFile);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Settings: save failed", ex);
                return false;
            }
        }
    }
}
