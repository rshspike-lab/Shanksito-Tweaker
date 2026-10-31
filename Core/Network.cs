using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ShanksitoTweaker.Core;

public sealed class NetInfo
{
    public string Name = "";
    public string Description = "";
    public string Guid = "";
    public bool WiFi;
    public long SpeedMbps;
    public string Gateway = "";
    public string ClassKey = "";
    public HashSet<string> Properties = new(StringComparer.OrdinalIgnoreCase);
    public string? Vpn;
}

public sealed record Probe(string Label, int Sent, int Received, double Avg, double Min, double Max)
{
    public double Jitter => Max - Min;
    public double LossPct => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    public bool Reached => Received > 0;
}

public sealed record NetTest(Probe Gateway, Probe Internet, Probe? GameRegion, string Verdict, bool Healthy);

public static class Network
{
    private const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private static readonly Regex VpnName = new("wintun|wireguard|tap-|mullvad|proton|openvpn|tailscale", RegexOptions.IgnoreCase);

    private static readonly string[] Keywords =
        ["*EEE", "EnableGreenEthernet", "PowerSavingMode", "GigaLite", "*InterruptModeration", "*FlowControl"];

    public static NetInfo? Active(ISystemApi s)
    {
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();
        var main = up.FirstOrDefault(n => !VpnName.IsMatch(n.Description) && Gateway(n) is not null);
        if (main is null) return null;

        var info = new NetInfo
        {
            Name = main.Name,
            Description = main.Description,
            Guid = main.Id,
            WiFi = main.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
            SpeedMbps = main.Speed / 1_000_000,
            Gateway = Gateway(main)!,
            Vpn = up.FirstOrDefault(n => VpnName.IsMatch(n.Description) || VpnName.IsMatch(n.Name))?.Description,
        };
        for (var i = 0; i < 64; i++)
        {
            var key = $@"{NetClass}\{i:0000}";
            if (!string.Equals(s.RegGet(RegRoot.HKLM, key, "NetCfgInstanceId") as string, main.Id, StringComparison.OrdinalIgnoreCase)) continue;
            info.ClassKey = key;
            foreach (var keyword in Keywords)
                if (s.RegGet(RegRoot.HKLM, key, keyword) is not null) info.Properties.Add(keyword);
            break;
        }
        return info;
    }

    private static string? Gateway(NetworkInterface n) => n.GetIPProperties().GatewayAddresses
        .Select(g => g.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))?.ToString();

    /// <summary>"aresriot.aws-euw3-prod.eu-gp-paris-1" is hosted in AWS eu-west-3.</summary>
    public static string? AwsRegion(string pod)
    {
        var match = Regex.Match(pod, @"aws-([a-z]{2})([ewcns])(\d)");
        if (!match.Success) return null;
        var side = match.Groups[2].Value switch
        {
            "e" => "east", "w" => "west", "c" => "central", "n" => "north", _ => "south",
        };
        return $"{match.Groups[1].Value}-{side}-{match.Groups[3].Value}";
    }

    private static Probe Summarize(string label, int sent, List<double> times) => times.Count == 0
        ? new Probe(label, sent, 0, 0, 0, 0)
        : new Probe(label, sent, times.Count, times.Average(), times.Min(), times.Max());

    private static Probe IcmpProbe(string label, string host, int count)
    {
        var times = new List<double>();
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            try
            {
                var reply = ping.Send(host, 1000);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException)
            {
                // Counted as lost.
            }
            Thread.Sleep(100);
        }
        return Summarize(label, count, times);
    }

    /// <summary>Game servers do not answer ping: a TCP handshake to the same AWS region gives the round trip.</summary>
    private static Probe TcpProbe(string label, string host, int count)
    {
        var times = new List<double>();
        IPAddress? address;
        try
        {
            address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            address = null;
        }
        if (address is null) return Summarize(label, 0, times);

        for (var i = 0; i < count; i++)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var clock = Stopwatch.StartNew();
            try
            {
                if (socket.ConnectAsync(address, 443).Wait(1000)) times.Add(clock.Elapsed.TotalMilliseconds);
            }
            catch (AggregateException)
            {
                // Counted as lost.
            }
            Thread.Sleep(100);
        }
        return Summarize(label, count, times);
    }

    public static NetTest Run(NetInfo net, string gamePod)
    {
        var gateway = IcmpProbe("Ta box", net.Gateway, 20);
        var internet = IcmpProbe("Internet (1.1.1.1)", "1.1.1.1", 20);
        var region = AwsRegion(gamePod);
        var game = region is null ? null : TcpProbe($"Serveurs de jeu ({region})", $"dynamodb.{region}.amazonaws.com", 8);
        var (verdict, healthy) = Judge(net, gateway, internet, game);
        return new NetTest(gateway, internet, game, verdict, healthy);
    }

    public static (string Verdict, bool Healthy) Judge(NetInfo net, Probe gateway, Probe internet, Probe? game)
    {
        if (net.Vpn is not null) return ($"VPN actif ({net.Vpn}) : coupe-le pour jouer, il rallonge le trajet.", false);
        if (!gateway.Reached && !internet.Reached) return ("Pas de réponse : test impossible.", false);
        if (gateway.LossPct > 0 || gateway.Avg > 5 || gateway.Jitter > 8)
            return (net.WiFi
                ? "Le problème est chez toi : Wi-Fi instable. Passe en câble Ethernet, c'est le plus gros gain possible."
                : "Le problème est chez toi : vérifie le câble et le port de la box.", false);
        if (internet.LossPct > 0 || internet.Jitter > 15)
            return ("Ta connexion locale est bonne, l'instabilité vient de la box ou de l'opérateur. Vérifie si quelqu'un télécharge ou envoie des fichiers.", false);
        if (game is { Reached: true, Avg: > 40 })
            return ($"Connexion stable mais {game.Avg:0} ms jusqu'aux serveurs : vérifie le serveur choisi dans Valorant. Windows ne peut pas raccourcir ce trajet.", false);
        return ("Réseau sain : le ping dépend maintenant de la distance jusqu'au serveur, aucun tweak Windows ne le baissera vraiment.", true);
    }
}
