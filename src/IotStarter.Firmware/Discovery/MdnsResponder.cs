// Optional: compiled only with the IOTSTARTER_MDNS symbol and the nanoFramework.Iot.Device.MulticastDns
// package (plus its dependencies) referenced. Off by default: on ESP32 boards without PSRAM the
// extra assemblies did not fit next to the BLE stack (OutOfMemory at boot). See README "Discovery".
#if IOTSTARTER_MDNS
using System;
using System.Net;
using IotStarter.Firmware.Diagnostics;
using IotStarter.Firmware.Network;
using IotStarter.Firmware.Normal;
using IotStarter.Shared;
using Iot.Device.MulticastDns;
using Iot.Device.MulticastDns.Entities;
using Iot.Device.MulticastDns.Enum;
using Iot.Device.MulticastDns.EventArgs;

namespace IotStarter.Firmware.Discovery
{
    /// <summary>
    /// DNS-SD responder on top of nanoFramework.Iot.Device.MulticastDns (pattern from its sample):
    /// PTR _iotstarter._tcp.local, SRV/TXT for the instance, A for the host name.
    /// </summary>
    public class MdnsResponder
    {
        private const string LocalSuffix = ".local";
        private const string ServicesMetaQuery = "_services._dns-sd._udp.local";
        private const int Ttl = 120;

        private readonly WifiService _wifi;
        private readonly string _serviceDomain;
        private readonly string _instanceDomain;
        private readonly string _hostDomain;
        private readonly string _txt;
        private MulticastDnsService _service;

        public MdnsResponder(WifiService wifi)
        {
            _wifi = wifi;
            _serviceDomain = ProtocolConstants.MdnsServiceType + LocalSuffix;
            _instanceDomain = DeviceContext.HostName + "." + _serviceDomain;
            _hostDomain = DeviceContext.HostName + LocalSuffix;
            _txt = BuildTxt(
                HttpContract.TxtId + "=" + DeviceContext.DeviceId,
                HttpContract.TxtModel + "=" + DeviceContext.Model.Id.ToString(),
                HttpContract.TxtApi + "=" + ProtocolConstants.ApiVersion.ToString());
        }

        public void Start()
        {
            _service = new MulticastDnsService();
            _service.MessageReceived += OnMessageReceived;
            _service.Start();
            Log.Info("mDNS: answering for " + _instanceDomain);
        }

        private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                if (e.Message == null)
                {
                    return;
                }

                byte[] ip = _wifi.CurrentIPv4();
                if (ip == null)
                {
                    return;
                }

                IPAddress address = IPAddress.Parse(ByteHelpers.FormatIPv4(ip));
                Response response = null;

                foreach (Question question in e.Message.GetQuestions())
                {
                    string domain = Normalize(question.Domain);
                    DnsResourceType type = question.QueryType;
                    bool any = type == DnsResourceType.ANY;

                    if (domain == _serviceDomain && (type == DnsResourceType.PTR || any))
                    {
                        response = response ?? new Response();
                        response.AddAnswer(new PtrRecord(_serviceDomain, _instanceDomain, Ttl));
                        AddInstanceRecords(response, address, false);
                    }
                    else if (domain == ServicesMetaQuery && (type == DnsResourceType.PTR || any))
                    {
                        response = response ?? new Response();
                        response.AddAnswer(new PtrRecord(ServicesMetaQuery, _serviceDomain, Ttl));
                    }
                    else if (domain == _instanceDomain && (type == DnsResourceType.SRV || type == DnsResourceType.TXT || any))
                    {
                        response = response ?? new Response();
                        AddInstanceRecords(response, address, true);
                    }
                    else if (domain == _hostDomain && (type == DnsResourceType.A || any))
                    {
                        response = response ?? new Response();
                        response.AddAnswer(new ARecord(_hostDomain, address, Ttl));
                    }
                }

                if (response != null)
                {
                    e.Response = response;
                }
            }
            catch (Exception ex)
            {
                // A malformed packet must never take the device down.
                Log.Error("mDNS: query handling failed", ex);
            }
        }

        private void AddInstanceRecords(Response response, IPAddress address, bool asAnswers)
        {
            SrvRecord srv = new SrvRecord(_instanceDomain, 0, 0, (ushort)ProtocolConstants.HttpPort, _hostDomain, Ttl);
            TxtRecord txt = new TxtRecord(_instanceDomain, _txt, Ttl);
            ARecord a = new ARecord(_hostDomain, address, Ttl);

            if (asAnswers)
            {
                response.AddAnswer(srv);
                response.AddAnswer(txt);
            }
            else
            {
                response.AddAdditional(srv);
                response.AddAdditional(txt);
            }

            response.AddAdditional(a);
        }

        private static string Normalize(string domain)
        {
            if (domain == null)
            {
                return string.Empty;
            }

            string lower = domain.ToLower();
            return lower.EndsWith(".") ? lower.Substring(0, lower.Length - 1) : lower;
        }

        /// <summary>
        /// TxtRecord writes its text as raw bytes with no length prefixes, so the DNS-SD
        /// "character-string" framing (length byte + text) is built here. Every entry is ASCII and
        /// shorter than 128 characters, so each length character encodes to exactly one UTF-8 byte.
        /// </summary>
        private static string BuildTxt(string entry1, string entry2, string entry3)
        {
            return ((char)entry1.Length).ToString() + entry1
                + ((char)entry2.Length).ToString() + entry2
                + ((char)entry3.Length).ToString() + entry3;
        }
    }
}
#endif
