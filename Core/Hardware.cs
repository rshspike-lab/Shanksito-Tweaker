using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ShanksitoTweaker.Core;

public sealed class GpuSample
{
    public double Temp, Util, Clock, MemClock, Power, MemUsed, MemTotal;
    public string PState = "";
    public ulong Throttle;

    public string ThrottleText
    {
        get
        {
            var reasons = new List<string>();
            if ((Throttle & 0x04) != 0) reasons.Add("limite de puissance");
            if ((Throttle & 0x08) != 0) reasons.Add("ralentissement matériel");
            if ((Throttle & 0x20) != 0) reasons.Add("thermique (logiciel)");
            if ((Throttle & 0x40) != 0) reasons.Add("thermique (matériel)");
            if ((Throttle & 0x80) != 0) reasons.Add("frein d'alimentation");
            return reasons.Count == 0 ? "aucun" : string.Join(", ", reasons);
        }
    }
}

public static class Gpu
{
    private const string Query = "temperature.gpu,utilization.gpu,clocks.gr,clocks.mem,power.draw,memory.used,memory.total,pstate,clocks_throttle_reasons.active";

    public static GpuSample? Sample(ISystemApi s)
    {
        try
        {
            var result = s.Run("nvidia-smi", $"--query-gpu={Query} --format=csv,noheader,nounits");
            return result.Code == 0 ? ParseSample(result.Output) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public static GpuSample? ParseSample(string csv)
    {
        var parts = csv.Trim().Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length < 9) return null;
        double N(int i) => double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var hex = parts[8].StartsWith("0x") ? parts[8][2..] : "0";
        return new GpuSample
        {
            Temp = N(0), Util = N(1), Clock = N(2), MemClock = N(3), Power = N(4), MemUsed = N(5), MemTotal = N(6),
            PState = parts[7],
            Throttle = ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask) ? mask : 0,
        };
    }

    /// <summary>Dedicated GPU first: on laptops the Intel or AMD chip is listed too.</summary>
    public static string Name(ISystemApi s)
    {
        var classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var names = new List<string>();
        for (var i = 0; i < 6; i++)
            if (s.RegGet(RegRoot.HKLM, $@"{classKey}\{i:0000}", "DriverDesc") is string name) names.Add(name);
        return names.FirstOrDefault(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => Regex.IsMatch(n, "Radeon (RX|R9|VII|Pro)", RegexOptions.IgnoreCase))
            ?? names.FirstOrDefault()
            ?? "";
    }

    /// <summary>Works for any GPU brand: the value comes from the display driver entry.</summary>
    public static double VideoMemoryGb(ISystemApi s)
    {
        var classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        long best = 0;
        for (var i = 0; i < 6; i++)
            if (s.RegGet(RegRoot.HKLM, $@"{classKey}\{i:0000}", "HardwareInformation.qwMemorySize") is long bytes) best = Math.Max(best, bytes);
        return best / 1073741824.0;
    }

    public static (string Version, DateTime? Date) Driver(ISystemApi s)
    {
        var classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        for (var i = 0; i < 6; i++)
        {
            var key = $@"{classKey}\{i:0000}";
            if (s.RegGet(RegRoot.HKLM, key, "ProviderName") is not string provider || !provider.Contains("NVIDIA")) continue;
            var raw = Convert.ToString(s.RegGet(RegRoot.HKLM, key, "DriverVersion")) ?? "";
            var date = Convert.ToString(s.RegGet(RegRoot.HKLM, key, "DriverDate"));
            DateTime? parsed = DateTime.TryParseExact(date, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            return (ToNvidiaVersion(raw), parsed);
        }
        return ("", null);
    }

    /// <summary>32.0.15.7283 is what NVIDIA sells as 572.83.</summary>
    public static string ToNvidiaVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length != 4 || parts[2].Length < 1 || parts[3].Length != 4) return windowsVersion;
        var joined = parts[2][^1..] + parts[3];
        return $"{joined[..3]}.{joined[3..]}";
    }

    /// <summary>The driver store folder is created at install, unlike DriverDate which is the build date.</summary>
    public static DateTime? DriverInstalled(ISystemApi s)
    {
        if (s.RegGet(RegRoot.HKLM, @"SYSTEM\CurrentControlSet\Services\nvlddmkm", "ImagePath") is not string image) return null;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var relative = image.TrimStart('\\');
        relative = relative.StartsWith("SystemRoot", StringComparison.OrdinalIgnoreCase) ? relative["SystemRoot".Length..].TrimStart('\\') : relative;
        var folder = Path.GetDirectoryName(Path.Combine(windows, relative));
        return folder is not null && Directory.Exists(folder) ? Directory.GetCreationTime(folder) : null;
    }

    public static (int Total, DateTime? Last) DriverErrors(int days, DateTime? since = null)
    {
        var ms = days * 86_400_000L;
        var query = new EventLogQuery("System", PathType.LogName,
            $"*[System[(Provider[@Name='nvlddmkm'] or (Provider[@Name='Display'] and EventID=4101)) and TimeCreated[timediff(@SystemTime) <= {ms}]]]")
        {
            ReverseDirection = true,
        };
        var total = 0;
        DateTime? last = null;
        try
        {
            using var reader = new EventLogReader(query);
            while (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    if (since is not null && record.TimeCreated < since) continue;
                    last ??= record.TimeCreated;
                    total++;
                }
            }
        }
        catch (EventLogException)
        {
            return (0, null);
        }
        return (total, last);
    }
}

public sealed class DisplayInfo
{
    public string Device { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Primary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Current { get; set; }
    public List<int> Rates { get; set; } = [];
    public int Max => Rates.Count == 0 ? Current : Rates.Max();
}

public static class Displays
{
    private const int EnumCurrent = -1;
    private const uint DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 1;
    private const uint AttachedToDesktop = 1;
    private const uint PrimaryDevice = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, Width, Height, Flags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint State;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dev);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DevMode dev, IntPtr hwnd, uint flags, IntPtr param);

    public static List<DisplayInfo> List()
    {
        var list = new List<DisplayInfo>();
        for (uint i = 0; ; i++)
        {
            var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.State & AttachedToDesktop) == 0) continue;

            var monitor = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            var model = EnumDisplayDevices(adapter.Name, 0, ref monitor, 0)
                ? Regex.Match(monitor.Id ?? "", @"MONITOR\\([^\\]+)").Groups[1].Value
                : "";

            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(adapter.Name, EnumCurrent, ref mode)) continue;
            var info = new DisplayInfo
            {
                Device = adapter.Name, Model = model, Primary = (adapter.State & PrimaryDevice) != 0,
                Width = (int)mode.Width, Height = (int)mode.Height, Current = (int)mode.Frequency,
            };
            var probe = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            var rates = new SortedSet<int>();
            for (var m = 0; EnumDisplaySettings(adapter.Name, m, ref probe); m++)
                if (probe.Width == mode.Width && probe.Height == mode.Height) rates.Add((int)probe.Frequency);
            info.Rates = rates.ToList();
            list.Add(info);
        }
        return list;
    }

    public static bool SetRefresh(string device, int hz)
    {
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(device, EnumCurrent, ref mode)) return false;
        mode.Fields = DmDisplayFrequency;
        mode.Frequency = (uint)hz;
        return ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == 0;
    }
}

public static class Memory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtended;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    public static (int LoadPct, double TotalGb) Status()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((int)status.MemoryLoad, status.TotalPhys / 1073741824.0) : (0, 0);
    }
}

public sealed record RunningApp(string Label, string[] ProcessNames, double Megabytes, bool Default, bool GracefulOnly);

/// <summary>Curated on purpose: anything not listed (Riot, Vanguard, system, dev tools) is never closed.</summary>
public static class GameSession
{
    private static readonly (string Pattern, string Label, bool Default, bool Graceful)[] Known =
    [
        ("^Discord(Canary|PTB)?$", "Discord", false, false),
        ("^ChatGPT$", "ChatGPT", true, true),
        ("^(firefox|chrome|msedge|brave|opera)$", "Navigateurs", false, true),
        ("^(Creative Cloud.*|CCXProcess|CCLibrary|Adobe Desktop Service|AdobeIPCBroker|CoreSync)$", "Adobe Creative Cloud", true, false),
        ("^(steam|steamwebhelper)$", "Steam", true, false),
        ("^(EpicGamesLauncher|EpicWebHelper)$", "Epic Games", true, false),
        ("^(EADesktop|EABackgroundService|EALink)$", "EA App", true, false),
        ("^(overwolf|OverwolfBrowser|OverwolfHelper.*)$", "Overwolf", true, false),
        ("^(Medal|MedalEncoder)$", "Medal", true, false),
        ("^(RazerCortex.*|RazerAppEngine)$", "Razer Cortex / App", true, false),
        ("^(lghub|lghub_agent|lghub_system_tray)$", "Logitech G HUB", false, false),
        ("^(SteelSeriesGG.*|SteelSeriesEngine.*)$", "SteelSeries GG", false, false),
        ("^(MEGAsync)$", "MEGAsync", true, false),
        ("^(OneDrive)$", "OneDrive", true, true),
        ("^(Spotify)$", "Spotify", false, true),
        ("^(Docker Desktop|com\\.docker.*)$", "Docker Desktop", true, false),
        ("^(Voicemod.*|VoiceAI.*|Voice\\.ai.*)$", "Voice changers", true, false),
        ("^(NetLimiter|NLClientApp)$", "NetLimiter", true, false),
    ];

    public static List<RunningApp> Scan()
    {
        var totals = new Dictionary<string, (HashSet<string> Names, double Mb, bool Default, bool Graceful)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                foreach (var (pattern, label, isDefault, graceful) in Known)
                {
                    if (!Regex.IsMatch(process.ProcessName, pattern, RegexOptions.IgnoreCase)) continue;
                    if (!totals.TryGetValue(label, out var entry)) entry = ([], 0, isDefault, graceful);
                    entry.Names.Add(process.ProcessName);
                    entry.Mb += process.WorkingSet64 / 1048576.0;
                    totals[label] = entry;
                    break;
                }
            }
        }
        return totals.Select(p => new RunningApp(p.Key, p.Value.Names.ToArray(), p.Value.Mb, p.Value.Default, p.Value.Graceful))
            .OrderByDescending(a => a.Megabytes).ToList();
    }

    public static (int Closed, int Left, string? Path) Close(RunningApp app)
    {
        var processes = app.ProcessNames.SelectMany(Process.GetProcessesByName).ToList();
        string? path = null;
        foreach (var p in processes)
        {
            // Only ever read on apps the user chose to close, never on the game.
            try { path ??= p.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            p.CloseMainWindow();
        }
        Thread.Sleep(2000);
        int closed = 0, left = 0;
        foreach (var p in processes)
        {
            using (p)
            {
                if (p.HasExited) { closed++; continue; }
                // Never hard-kill apps that may hold unsaved work.
                if (app.GracefulOnly) { left++; continue; }
                try { p.Kill(); closed++; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { left++; }
            }
        }
        return (closed, left, path);
    }
}

/// <summary>This app is elevated: going through Explorer keeps the game and reopened apps unelevated.</summary>
public static class GameLauncher
{
    private const string Installs = @"C:\ProgramData\Riot Games\RiotClientInstalls.json";

    public static string? RiotClient()
    {
        if (!File.Exists(Installs)) return null;
        var match = Regex.Match(File.ReadAllText(Installs), "\"rc_default\"\\s*:\\s*\"([^\"]+)\"");
        var path = match.Groups[1].Value.Replace('/', '\\');
        return match.Success && File.Exists(path) ? path : null;
    }

    public static string LaunchScript(string client) =>
        $"@echo off\r\nstart \"\" \"{client}\" --launch-product=valorant --launch-patchline=live\r\n";

    public static void LaunchValorant(string client)
    {
        Directory.CreateDirectory(BackupStore.DefaultDir);
        var script = Path.Combine(BackupStore.DefaultDir, "launch-valorant.cmd");
        File.WriteAllText(script, LaunchScript(client));
        OpenUnelevated(script);
    }

    public static void OpenUnelevated(string target) => Process.Start("explorer.exe", $"\"{target}\"");
}
