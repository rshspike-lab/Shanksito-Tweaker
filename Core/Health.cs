using System.Diagnostics.Eventing.Reader;
using System.Text.Json;

namespace ShanksitoTweaker.Core;

public sealed class HealthInfo
{
    public double UptimeHours;
    public bool PendingReboot;
    public double BatteryWearPct = -1;
    public int DiskTemp = -1;
    public int DiskWear = -1;
    public long DiskHours = -1;
    public string DiskHealth = "";
    public int RamSticks;
    public int RamChannels;
    public int RamSpeed;
    public string BiosVersion = "";
    public int WheaErrors;
    public int HardResets;
    public int GameCrashes;
    public SortedDictionary<DateTime, int> GpuErrorsByDay = [];
}

public static class Health
{
    private const string Script =
        "$o=Get-CimInstance Win32_OperatingSystem;$r=Get-CimInstance Win32_PhysicalMemory;$d=Get-PhysicalDisk|Select-Object -First 1;" +
        "$c=$d|Get-StorageReliabilityCounter -ErrorAction SilentlyContinue;" +
        "$f=(Get-CimInstance -Namespace root\\wmi -ClassName BatteryFullChargedCapacity -ErrorAction SilentlyContinue).FullChargedCapacity;" +
        "$b=(Get-CimInstance -Namespace root\\wmi -ClassName BatteryStaticData -ErrorAction SilentlyContinue).DesignedCapacity;" +
        "[pscustomobject]@{Boot=$o.LastBootUpTime.ToString('o');Sticks=@($r).Count;Channels=@($r|Select-Object -ExpandProperty DeviceLocator|ForEach-Object{($_ -split '-')[0]}|Sort-Object -Unique).Count;" +
        "Speed=@($r)[0].ConfiguredClockSpeed;Bios=(Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion;DiskHealth=[string]$d.HealthStatus;" +
        "DiskTemp=$c.Temperature;DiskWear=$c.Wear;DiskHours=$c.PowerOnHours;Full=$f;Design=$b}|ConvertTo-Json -Compress";

    public static HealthInfo Read(ISystemApi s)
    {
        var info = Parse(s.Run("powershell", CommandTweak.Ps(Script)).Output);
        info.PendingReboot = s.RegGet(RegRoot.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update", "RebootRequired") is not null;
        info.WheaErrors = Count("*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[timediff(@SystemTime) <= 1209600000]]]", "System");
        info.HardResets = Count("*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[timediff(@SystemTime) <= 1209600000]]]", "System");
        info.GameCrashes = Count("*[System[Provider[@Name='Application Error'] and EventID=1000 and TimeCreated[timediff(@SystemTime) <= 1209600000]]]", "Application", "VALORANT");
        info.GpuErrorsByDay = GpuErrors(14);
        return info;
    }

    public static HealthInfo Parse(string json)
    {
        var info = new HealthInfo();
        if (json.Trim().Length == 0) return info;
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return info;
        }

        int Int(string name, int fallback = -1) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
        string Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        if (DateTime.TryParse(Str("Boot"), out var boot)) info.UptimeHours = (DateTime.Now - boot).TotalHours;
        info.RamSticks = Int("Sticks", 0);
        info.RamChannels = Int("Channels", 0);
        info.RamSpeed = Int("Speed", 0);
        info.BiosVersion = Str("Bios");
        info.DiskHealth = Str("DiskHealth");
        info.DiskTemp = Int("DiskTemp");
        info.DiskWear = Int("DiskWear");
        info.DiskHours = Int("DiskHours");
        int full = Int("Full"), design = Int("Design");
        if (full > 0 && design > 0) info.BatteryWearPct = Math.Max(0, 100.0 * (design - full) / design);
        return info;
    }

    private static int Count(string xpath, string log, string? contains = null)
    {
        var total = 0;
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath));
            while (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    if (contains is null || record.FormatDescription()?.Contains(contains, StringComparison.OrdinalIgnoreCase) == true) total++;
                }
            }
        }
        catch (EventLogException)
        {
            return 0;
        }
        return total;
    }

    private static SortedDictionary<DateTime, int> GpuErrors(int days)
    {
        var map = new SortedDictionary<DateTime, int>();
        var ms = days * 86_400_000L;
        try
        {
            var xpath = $"*[System[(Provider[@Name='nvlddmkm'] or (Provider[@Name='Display'] and EventID=4101)) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
            while (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    if (record.TimeCreated is not { } when) continue;
                    map[when.Date] = map.GetValueOrDefault(when.Date) + 1;
                }
            }
        }
        catch (EventLogException)
        {
            return [];
        }
        return map;
    }
}

public sealed record CleanTarget(string Id, string Title, string Why, string Folder, int MinAgeDays, bool Default, bool BlockedWhilePlaying);

public sealed record CleanMeasure(CleanTarget Target, long Bytes, int Files);

/// <summary>Only known throwaway folders, only on an explicit click, and links are never followed.</summary>
public static class Cleaner
{
    private static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static List<CleanTarget> Targets() =>
    [
        new("temp", "Fichiers temporaires", "Fichiers de plus de 3 jours. Ceux utilisés sont ignorés.", Path.GetTempPath(), 3, true, false),
        new("dumps", "Rapports de plantage d'applis", "Vidages mémoire laissés par les applis plantées.", Path.Combine(LocalData, "CrashDumps"), 0, true, false),
        new("valo_crashes", "Rapports de plantage Valorant", "Ne sert qu'à Riot pour analyser un crash passé.", Path.Combine(LocalData, "VALORANT", "Saved", "Crashes"), 0, true, true),
        new("nv_cache", "Cache de shaders NVIDIA", "À vider seulement après une mise à jour du driver. Sinon, saccades le temps que le cache se refasse.", Path.Combine(LocalData, "NVIDIA", "DXCache"), 0, false, true),
    ];

    private static IEnumerable<FileInfo> Files(CleanTarget target)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            IgnoreInaccessible = true,
        };
        var limit = DateTime.Now.AddDays(-target.MinAgeDays);
        // Temp\.net holds this app's own unpacked runtime files.
        return new DirectoryInfo(target.Folder).EnumerateFiles("*", options)
            .Where(f => f.LastWriteTime <= limit && !f.FullName.Contains(@"\.net\", StringComparison.OrdinalIgnoreCase));
    }

    public static CleanMeasure Measure(CleanTarget target)
    {
        if (!Directory.Exists(target.Folder)) return new CleanMeasure(target, 0, 0);
        long bytes = 0;
        var count = 0;
        foreach (var file in Files(target))
        {
            bytes += file.Length;
            count++;
        }
        return new CleanMeasure(target, bytes, count);
    }

    public static (long Freed, int Skipped) Clean(CleanTarget target)
    {
        if (!Directory.Exists(target.Folder)) return (0, 0);
        if (target.BlockedWhilePlaying && GameWatcher.GameRunning()) return (0, 0);
        long freed = 0;
        var skipped = 0;
        foreach (var file in Files(target).ToList())
        {
            try
            {
                var size = file.Length;
                file.Delete();
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        return (freed, skipped);
    }
}

public sealed record JournalEntry(DateTime When, string Action, string Detail);

public static class Journal
{
    public static string File_ => Path.Combine(BackupStore.DefaultDir, "journal.log");

    public static void Add(string line)
    {
        try
        {
            Directory.CreateDirectory(BackupStore.DefaultDir);
            File.AppendAllText(File_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
        catch (IOException)
        {
            // A missing history line must never undo a tweak that worked.
        }
    }

    public static List<JournalEntry> Read() => File.Exists(File_) ? Parse(File.ReadAllLines(File_)) : [];

    public static List<JournalEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<JournalEntry>();
        foreach (var line in lines)
        {
            if (line.Length < 21 || !DateTime.TryParse(line[..19], out var when)) continue;
            var rest = line[20..];
            var space = rest.IndexOf(' ');
            list.Add(space < 0 ? new JournalEntry(when, rest, "") : new JournalEntry(when, rest[..space], rest[(space + 1)..]));
        }
        return list;
    }

    /// <summary>A session is flagged when a tweak changed since the one before it, to spot what moved the FPS.</summary>
    public static HashSet<int> ChangedSessions(IReadOnlyList<PerfSession> sessions, IEnumerable<JournalEntry> entries)
    {
        var applied = entries.Where(e => e.Action is "APPLIQUE" or "ANNULE").Select(e => e.When).ToList();
        var marks = new HashSet<int>();
        for (var i = 1; i < sessions.Count; i++)
            if (applied.Any(t => t > sessions[i - 1].Start && t <= sessions[i].Start)) marks.Add(i);
        return marks;
    }
}
