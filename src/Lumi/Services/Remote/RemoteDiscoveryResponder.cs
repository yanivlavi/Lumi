using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Remote.Protocol;

namespace Lumi.Services.Remote;

/// <summary>
/// Answers LAN discovery probes so a phone can find this Lumi without the user typing an IP.
/// </summary>
/// <remarks>
/// A tiny request/response beacon over UDP is used instead of mDNS/Bonjour: it needs no extra
/// dependency, no platform service, and works identically on Windows, Linux, macOS and Android.
/// The desktop only ever replies to a probe — it never broadcasts unsolicited — so an idle Lumi is
/// invisible on the network.
/// </remarks>
internal sealed class RemoteDiscoveryResponder : IDisposable
{
    private readonly string _instanceId;
    private readonly IPAddress _localAddress;
    private readonly Func<int> _portProvider;
    private readonly Func<string> _userNameProvider;
    private readonly int _discoveryPort;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private Task? _loop;

    public RemoteDiscoveryResponder(
        string instanceId,
        IPAddress localAddress,
        Func<int> portProvider,
        Func<string> userNameProvider,
        int discoveryPort = RemoteProtocol.DiscoveryPort)
    {
        _instanceId = instanceId;
        _localAddress = localAddress;
        _portProvider = portProvider;
        _userNameProvider = userNameProvider;
        _discoveryPort = discoveryPort;
    }

    public void Start()
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            udp.EnableBroadcast = true;
            _udp = udp;
            _loop = Task.Run(() => ListenAsync(udp, _cts.Token));
        }
        catch (SocketException ex)
        {
            // Another Lumi window already owns the discovery port, or the OS blocked the bind.
            // Manual address entry still works, so this is not fatal.
            Trace.TraceInformation($"[Remote] Discovery responder unavailable: {ex.Message}");
        }
    }

    private async Task ListenAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            try
            {
                var probe = Encoding.UTF8.GetString(result.Buffer);
                if (!probe.StartsWith(RemoteProtocol.DiscoveryProbe, StringComparison.Ordinal))
                    continue;

                if (!LumiRemoteServer.IsPrivateCaller(result.RemoteEndPoint))
                    continue;

                var payload = BuildBeacon();
                await udp.SendAsync(payload, payload.Length, result.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Transient network hiccup; keep listening.
            }
        }
    }

    private byte[] BuildBeacon()
    {
        var beacon = new RemoteBeacon
        {
            InstanceId = _instanceId,
            HostName = Environment.MachineName,
            UserName = _userNameProvider(),
            Address = _localAddress.ToString(),
            Port = _portProvider()
        };

        var json = JsonSerializer.Serialize(beacon, RemoteJsonContext.Default.RemoteBeacon);
        return Encoding.UTF8.GetBytes(RemoteProtocol.DiscoveryBeacon + json);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
