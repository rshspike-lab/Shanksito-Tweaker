using System.Globalization;
using System.Text.RegularExpressions;

namespace ShanksitoTweaker.Core;

public sealed class ValoConfig
{
    public bool Found { get; set; }
    public string ProfileId { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public bool? LimitAlways { get; set; }
    public double? MaxAlways { get; set; }
    public bool? VSync { get; set; }
    public int? FullscreenMode { get; set; }
    public string Resolution { get; set; } = "";
    public string Monitor { get; set; } = "";
    public string GamePod { get; set; } = "";
}

public static class ValorantFiles
{
    private static string ConfigRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Config");

    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs");

    public static string? GameExe()
    {
        const string relative = @"live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe";
        var settings = @"C:\ProgramData\Riot Games\Metadata\valorant.live\valorant.live.product_settings.yaml";
        if (File.Exists(settings))
        {
            var match = Regex.Match(File.ReadAllText(settings), "product_install_full_path:\\s*\"?([^\"\\r\\n]+)");
            if (match.Success)
            {
                var path = Path.Combine(match.Groups[1].Value.Replace('/', '\\'), @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe");
                if (File.Exists(path)) return path;
            }
        }
        foreach (var drive in new[] { "C", "D", "E" })
        {
            var path = $@"{drive}:\Riot Games\VALORANT\{relative}";
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static ValoConfig Load() => Load(ConfigRoot);

    public static ValoConfig Load(string root)
    {
        var config = new ValoConfig();
        var machine = Path.Combine(root, "WindowsClient", "RiotLocalMachine.ini");
        if (!File.Exists(machine)) return config;
        var user = Regex.Match(File.ReadAllText(machine), "LastKnownUser=([0-9a-f-]+)").Groups[1].Value;
        if (user.Length == 0) return config;
        var profile = Directory.GetDirectories(root, user + "-*").FirstOrDefault();
        if (profile is null) return config;

        config.Found = true;
        config.ProfileId = user;
        var riot = Path.Combine(profile, "Windows", "RiotUserSettings.ini");
        if (File.Exists(riot))
        {
            config.SavedAt = File.GetLastWriteTime(riot);
            var text = File.ReadAllText(riot);
            config.LimitAlways = Regex.Match(text, @"LimitFramerateAlways=(\w+)") is { Success: true } m
                ? m.Groups[1].Value.Equals("True", StringComparison.OrdinalIgnoreCase)
                : false;
            if (Regex.Match(text, @"MaxFramerateAlways=([\d.]+)") is { Success: true } max)
                config.MaxAlways = double.Parse(max.Groups[1].Value, CultureInfo.InvariantCulture);
            config.GamePod = Regex.Match(text, @"PreferredGamePods=(\S+)").Groups[1].Value;
        }
        var game = Path.Combine(profile, "WindowsClient", "GameUserSettings.ini");
        if (File.Exists(game))
        {
            var text = File.ReadAllText(game);
            config.VSync = Regex.Match(text, @"bUseVSync=(\w+)") is { Success: true } v
                ? v.Groups[1].Value.Equals("True", StringComparison.OrdinalIgnoreCase)
                : null;
            if (Regex.Match(text, @"PreferredFullscreenMode=(\d)") is { Success: true } f)
                config.FullscreenMode = int.Parse(f.Groups[1].Value);
            var x = Regex.Match(text, @"ResolutionSizeX=(\d+)").Groups[1].Value;
            var y = Regex.Match(text, @"ResolutionSizeY=(\d+)").Groups[1].Value;
            config.Resolution = $"{x}x{y}";
            config.Monitor = Regex.Match(text, @"DefaultMonitorDeviceID=""MONITOR\\\\([^\\]+)").Groups[1].Value;
        }
        return config;
    }
}

public sealed class PerfSession
{
    public string File { get; set; } = "";
    public DateTime Start { get; set; }
    public int Rounds { get; set; }
    public long Frames { get; set; }
    public double AvgFps { get; set; }
    public double PctUnder120 { get; set; }
    public double PctUnder144 { get; set; }
    public double PctOver240 { get; set; }
    public double WorkMs { get; set; }
    public double GpuMs { get; set; }
    public double SystemCpu { get; set; }
    public double MemoryPct { get; set; }
}

/// <summary>Reads the per-round stats VALORANT writes itself, so a before/after needs no overlay.</summary>
public static class PerfLogs
{
    private sealed class Round
    {
        public DateTime When;
        public double FrameMs, Seconds, Work, Gpu, SysCpu, Mem;
        public long Frames, Under120, From120, From144, From240;
    }

    private static readonly Regex Stamp = new(@"^\[(\d{4})\.(\d\d)\.(\d\d)-(\d\d)\.(\d\d)\.(\d\d):\d+\]\[.*End of Round Perf statistics");
    private static readonly Regex Hist = new(@"Game Thread Frames (Under|Over) (\d+) fps: (\d+)");

    private static double Num(string line) =>
        double.Parse(line[(line.LastIndexOf(':') + 1)..].Trim(), CultureInfo.InvariantCulture);

    public static List<PerfSession> Load() => Load(ValorantFiles.LogDir);

    public static List<PerfSession> Load(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var sessions = new List<PerfSession>();
        foreach (var file in Directory.GetFiles(dir, "ShooterGame*.log"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var session = Parse(reader, Path.GetFileName(file));
            if (session is not null) sessions.Add(session);
        }
        return sessions.OrderBy(s => s.Start).ToList();
    }

    public static PerfSession? Parse(TextReader reader, string name)
    {
        var rounds = new List<Round>();
        Round? current = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                var m = Stamp.Match(line);
                if (m.Success)
                {
                    int G(int i) => int.Parse(m.Groups[i].Value);
                    current = new Round { When = new DateTime(G(1), G(2), G(3), G(4), G(5), G(6), DateTimeKind.Utc).ToLocalTime() };
                }
                continue;
            }
            if (current is null || line[0] != '\t') continue;
            var text = line.TrimStart();
            if (text.StartsWith("Avg.Frametime")) current.FrameMs = Num(text);
            else if (text.StartsWith("Total Frames:")) current.Frames = (long)Num(text);
            else if (text.StartsWith("Avg. Work")) current.Work = Num(text);
            else if (text.StartsWith("Total Time(secs)")) current.Seconds = Num(text);
            else if (text.StartsWith("GPU Time(mean)")) current.Gpu = Num(text);
            else if (text.StartsWith("System CPU Util")) current.SysCpu = Num(text);
            else if (text.StartsWith("System Memory Util")) current.Mem = Num(text);
            else if (text.StartsWith("Game Thread Frames"))
            {
                var h = Hist.Match(text);
                if (!h.Success) continue;
                var bucket = int.Parse(h.Groups[2].Value);
                var count = long.Parse(h.Groups[3].Value);
                if (h.Groups[1].Value == "Under" || bucket < 120) current.Under120 += count;
                else if (bucket == 120) current.From120 += count;
                else if (bucket == 144) current.From144 += count;
                else current.From240 += count;
            }
            else if (text.StartsWith("RHI:"))
            {
                // Lobby and very short rounds would skew the average.
                if (current.Seconds >= 20 && current.Frames > 0 && current.FrameMs > 0) rounds.Add(current);
                current = null;
            }
        }
        if (rounds.Count == 0) return null;

        double frames = rounds.Sum(r => r.Frames);
        double Avg(Func<Round, double> f) => rounds.Sum(r => f(r) * r.Frames) / frames;
        return new PerfSession
        {
            File = name,
            Start = rounds[0].When,
            Rounds = rounds.Count,
            Frames = (long)frames,
            AvgFps = 1000 / Avg(r => r.FrameMs),
            PctUnder120 = 100 * rounds.Sum(r => r.Under120) / frames,
            PctUnder144 = 100 * rounds.Sum(r => r.Under120 + r.From120) / frames,
            PctOver240 = 100 * rounds.Sum(r => r.From240) / frames,
            WorkMs = Avg(r => r.Work),
            GpuMs = Avg(r => r.Gpu),
            SystemCpu = Avg(r => r.SysCpu),
            MemoryPct = Avg(r => r.Mem),
        };
    }
}
