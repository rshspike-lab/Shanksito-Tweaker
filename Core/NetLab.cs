using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ShanksitoTweaker.Core;

public sealed record BloatResult(double IdleMs, double DownMs, double UpMs, double DownMbps, double UpMbps, string Grade, string Verdict);

public sealed record Hop(int Number, string Address, double Ms);

/// <summary>Network tests that need more than a ping. Run on demand only, they load the connection.</summary>
public static class NetLab
{
    private const string Target = "1.1.1.1";
    private const int Streams = 4;
    private static readonly TimeSpan LoadTime = TimeSpan.FromSeconds(6);
    private static readonly byte[] UploadChunk = new byte[1024 * 1024];

    public static string Grade(double addedMs) => addedMs switch
    {
        < 5 => "A+",
        < 30 => "A",
        < 60 => "B",
        < 200 => "C",
        < 400 => "D",
        _ => "F",
    };

    public static string BloatVerdict(string grade, double down, double up) => grade switch
    {
        "A+" or "A" => "Ta box reste réactive même chargée : un téléchargement ne gênera pas tes parties.",
        "B" => "Petit effet sous charge : évite seulement les gros envois pendant une partie.",
        _ => $"Bufferbloat : le ping monte quand la ligne est chargée. Active le SQM ou la QoS de ta box, ou limite les débits à {down * 0.9:0} / {up * 0.9:0} Mbps.",
    };

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var ordered = values.Order().ToList();
        return ordered[ordered.Count / 2];
    }

    private static async Task<List<double>> PingWhile(Func<bool> running)
    {
        var times = new List<double>();
        using var ping = new Ping();
        while (running())
        {
            try
            {
                var reply = await ping.SendPingAsync(Target, 1000);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException)
            {
                // Lost samples just mean fewer points.
            }
            await Task.Delay(200);
        }
        return times;
    }

    private static async Task<(double Ms, double Mbps)> UnderLoad(Func<HttpClient, CancellationToken, Task<long>> worker)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var limit = new CancellationTokenSource(LoadTime);
        var clock = Stopwatch.StartNew();
        var pings = PingWhile(() => !limit.IsCancellationRequested);
        var workers = Enumerable.Range(0, Streams).Select(_ => worker(client, limit.Token)).ToList();
        var bytes = (await Task.WhenAll(workers)).Sum();
        return (Median(await pings), bytes * 8 / 1e6 / clock.Elapsed.TotalSeconds);
    }

    private static async Task<long> Download(HttpClient client, CancellationToken cancel)
    {
        long total = 0;
        var buffer = new byte[81920];
        try
        {
            using var response = await client.GetAsync("https://speed.cloudflare.com/__down?bytes=500000000", HttpCompletionOption.ResponseHeadersRead, cancel);
            await using var stream = await response.Content.ReadAsStreamAsync(cancel);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancel)) > 0) total += read;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            // The time limit ends the transfer on purpose.
        }
        return total;
    }

    private static async Task<long> Upload(HttpClient client, CancellationToken cancel)
    {
        long total = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                using var content = new ByteArrayContent(UploadChunk);
                using var response = await client.PostAsync("https://speed.cloudflare.com/__up", content, cancel);
                total += UploadChunk.Length;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            // Same as download.
        }
        return total;
    }

    public static async Task<BloatResult> Bufferbloat(Action<string> progress)
    {
        progress("Mesure du ping au repos…");
        var idleEnd = DateTime.UtcNow.AddSeconds(3);
        var idle = Median(await PingWhile(() => DateTime.UtcNow < idleEnd));
        progress("Ligne chargée en réception…");
        var (down, downMbps) = await UnderLoad(Download);
        progress("Ligne chargée en envoi…");
        var (up, upMbps) = await UnderLoad(Upload);
        var grade = Grade(Math.Max(down, up) - idle);
        return new BloatResult(idle, down, up, downMbps, upMbps, grade, BloatVerdict(grade, downMbps, upMbps));
    }

    public static List<Hop> Trace(string host, int maxHops = 20)
    {
        var address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var hops = new List<Hop>();
        if (address is null) return hops;

        using var ping = new Ping();
        var silent = 0;
        for (var ttl = 1; ttl <= maxHops && silent < 3; ttl++)
        {
            var best = double.MaxValue;
            string? seen = null;
            var reached = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    var reply = ping.Send(address, 800, new byte[32], new PingOptions(ttl, true));
                    if (reply.Status is not (IPStatus.TtlExpired or IPStatus.Success)) continue;
                    seen = reply.Address?.ToString();
                    best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
                    reached |= reply.Status == IPStatus.Success;
                }
                catch (PingException)
                {
                    // Treated as no answer for this attempt.
                }
            }
            silent = seen is null ? silent + 1 : 0;
            hops.Add(new Hop(ttl, seen ?? "", seen is null ? -1 : best));
            if (reached) break;
        }
        // Trailing silent hops only mean the server ignores ping.
        while (hops.Count > 0 && hops[^1].Ms < 0) hops.RemoveAt(hops.Count - 1);
        return hops;
    }

    /// <summary>The hop where latency jumps the most: above it the problem is not on this PC.</summary>
    public static Hop? BiggestJump(List<Hop> hops)
    {
        Hop? best = null;
        double jump = 0, previous = 0;
        foreach (var hop in hops.Where(h => h.Ms >= 0))
        {
            if (hop.Ms - previous > jump)
            {
                jump = hop.Ms - previous;
                best = hop;
            }
            previous = hop.Ms;
        }
        return best;
    }
}
