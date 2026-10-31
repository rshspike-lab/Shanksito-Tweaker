using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace ShanksitoTweaker.Core;

public sealed class MatchSample
{
    public double Temp { get; set; }
    public double Util { get; set; }
    public double Clock { get; set; }
    public double Power { get; set; }
    public ulong Throttle { get; set; }
}

public sealed class MatchReport
{
    public DateTime Start { get; set; }
    public double Minutes { get; set; }
    public double AvgTemp { get; set; }
    public double MaxTemp { get; set; }
    public double AvgUtil { get; set; }
    public double AvgClock { get; set; }
    public double AvgPower { get; set; }
    public double ThermalPct { get; set; }
    public double PingAvg { get; set; }
    public double PingMax { get; set; }
    public double LossPct { get; set; }
    public int Spikes { get; set; }
    public string Verdict { get; set; } = "";
}

/// <summary>While VALORANT runs: GPU and ping samples from outside. It only looks up the process name, never opens the game.</summary>
public sealed class GameWatcher(ISystemApi sys, Action<MatchReport> finished) : IDisposable
{
    private const string GameProcess = "VALORANT-Win64-Shipping";
    private readonly CancellationTokenSource _stop = new();
    private readonly List<MatchSample> _gpu = [];
    private readonly List<double> _pings = [];
    private DateTime _started;
    private bool _active;

    public string ProcessName { get; init; } = GameProcess;
    public int PollSeconds { get; init; } = 5;
    public double MinMinutes { get; init; } = 1;
    public Action? GameStarted { get; init; }

    public static string Folder => Path.Combine(BackupStore.DefaultDir, "matches");

    public static bool GameRunning() => IsRunning(GameProcess);

    private static bool IsRunning(string name)
    {
        var found = Process.GetProcessesByName(name);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    public void Start() => Task.Run(Loop);

    public void Dispose() => _stop.Cancel();

    private async Task Loop()
    {
        using var ping = new Ping();
        var tick = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            tick++;
            try
            {
                Step(tick, ping);
            }
            catch (Exception ex)
            {
                // One bad sample must not end the watch for the rest of the game.
                Journal.Add($"SURVEILLANCE {ex.GetType().Name}: {ex.Message}");
            }
        }
        // Closing the app mid-game still keeps what was measured so far.
        if (_active) Finish(false);
    }

    private void Step(int tick, Ping ping)
    {
        if (tick % PollSeconds == 0)
        {
            var running = IsRunning(ProcessName);
            if (running && !_active)
            {
                _active = true;
                _started = DateTime.Now;
                _gpu.Clear();
                _pings.Clear();
                Journal.Add("SURVEILLANCE partie détectée");
                GameStarted?.Invoke();
            }
            else if (!running && _active)
            {
                Finish(true);
            }
            if (_active && Gpu.Sample(sys) is { } s)
                _gpu.Add(new MatchSample { Temp = s.Temp, Util = s.Util, Clock = s.Clock, Power = s.Power, Throttle = s.Throttle });
        }
        if (_active) _pings.Add(PingOnce(ping));
    }

    private void Finish(bool notify)
    {
        _active = false;
        var report = Summarize(_started, _gpu, _pings);
        if (report.Minutes < MinMinutes) return;
        Save(report);
        if (notify) finished(report);
    }

    private static double PingOnce(Ping ping)
    {
        try
        {
            var reply = ping.Send("1.1.1.1", 800);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch (PingException)
        {
            return -1;
        }
    }

    public static MatchReport Summarize(DateTime start, List<MatchSample> gpu, List<double> pings)
    {
        var report = new MatchReport { Start = start, Minutes = (DateTime.Now - start).TotalMinutes };
        // Menus between rounds barely load the GPU and would hide what happens in play.
        var busy = gpu.Where(s => s.Util >= 30).ToList();
        if (busy.Count > 0)
        {
            report.AvgTemp = busy.Average(s => s.Temp);
            report.MaxTemp = busy.Max(s => s.Temp);
            report.AvgUtil = busy.Average(s => s.Util);
            report.AvgClock = busy.Average(s => s.Clock);
            report.AvgPower = busy.Average(s => s.Power);
            report.ThermalPct = 100.0 * busy.Count(s => (s.Throttle & 0x68) != 0) / busy.Count;
        }
        var good = pings.Where(p => p >= 0).ToList();
        if (pings.Count > 0) report.LossPct = 100.0 * (pings.Count - good.Count) / pings.Count;
        if (good.Count > 0)
        {
            var ordered = good.Order().ToList();
            var median = ordered[ordered.Count / 2];
            report.PingAvg = good.Average();
            report.PingMax = good.Max();
            report.Spikes = good.Count(p => p > median + 30);
        }
        report.Verdict = Judge(report);
        return report;
    }

    public static string Judge(MatchReport r)
    {
        if (r.ThermalPct >= 10 || r.MaxTemp >= 87) return $"Chaleur : le GPU a touché {r.MaxTemp:0} °C. C'est le refroidissement qui limite.";
        if (r.LossPct >= 2 || r.Spikes >= 5) return $"Réseau instable : {r.Spikes} pics de ping et {r.LossPct:0.#} % de perte pendant la partie.";
        if (r.AvgUtil >= 92) return "GPU à fond : baisse encore les graphismes pour gagner des FPS.";
        if (r.AvgUtil is > 0 and < 75) return "GPU pas saturé : si les FPS sont sous ta limite, c'est le CPU ou l'arrière-plan.";
        return "Partie saine : rien d'anormal côté GPU et réseau.";
    }

    public static void Save(MatchReport report)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, $"{report.Start:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(report));
    }

    public static List<MatchReport> LoadAll()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<MatchReport>();
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<MatchReport>(File.ReadAllText(file)) is { } report) list.Add(report);
            }
            catch (JsonException)
            {
                // A damaged report is simply skipped.
            }
        }
        return list.OrderByDescending(r => r.Start).ToList();
    }
}
