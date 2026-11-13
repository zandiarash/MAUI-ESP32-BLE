using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Normal;
using IotStarter.Firmware.Storage;
using IotStarter.Shared;

namespace IotStarter.Firmware.Discovery
{
    /// <summary>
    /// Answers the app's broadcast probe ("IOTS?" + version byte on UDP 48731) with a unicast JSON
    /// reply to the sender: {"id","model","port","api"}. Anything else is ignored.
    /// </summary>
    public class UdpDiscoveryResponder
    {
        private const int ReceiveBufferSize = 64;
        private Socket _socket;

        public void Start()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, ProtocolConstants.DiscoveryPort));

            Thread thread = new Thread(Run);
            thread.Start();
            Log.Info("UDP discovery listening on port " + ProtocolConstants.DiscoveryPort.ToString());
        }

        private void Run()
        {
            byte[] buffer = new byte[ReceiveBufferSize];
            byte[] magic = Encoding.UTF8.GetBytes(ProtocolConstants.DiscoveryProbeMagic);

            while (true)
            {
                try
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int length = _socket.ReceiveFrom(buffer, ref remote);
                    if (!IsProbe(buffer, length, magic))
                    {
                        continue;
                    }

                    string json = new JsonWriter()
                        .BeginObject()
                        .String(HttpContract.FieldId, DeviceContext.DeviceId)
                        .Number(HttpContract.FieldModel, DeviceContext.Model.Id)
                        .Number(HttpContract.FieldPort, ProtocolConstants.HttpPort)
                        .Number(HttpContract.FieldApi, ProtocolConstants.ApiVersion)
                        .EndObject()
                        .ToString();
                    byte[] reply = Encoding.UTF8.GetBytes(json);
                    _socket.SendTo(reply, remote);
                }
                catch (SocketException ex)
                {
                    // Typically the network went down; the supervisor reconnects. Back off briefly.
                    Log.Error("UDP discovery socket error", ex);
                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    DeviceRuntime.Fatal("UdpDiscoveryResponder", ex);
                    return;
                }
            }
        }

        private static bool IsProbe(byte[] buffer, int length, byte[] magic)
        {
            if (length != magic.Length + 1)
            {
                return false;
            }

            for (int i = 0; i < magic.Length; i++)
            {
                if (buffer[i] != magic[i])
                {
                    return false;
                }
            }

            // Answer probes of our protocol version or newer; the reply carries the API version.
            return buffer[magic.Length] >= 1;
        }
    }
}
