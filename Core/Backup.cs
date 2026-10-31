using System.Text.Json;
using Microsoft.Win32;

namespace ShanksitoTweaker.Core;

/// <summary>A registry value as it was before a tweak touched it (or the fact it did not exist).</summary>
public sealed class RegSnap
{
    public RegRoot Root { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Exists { get; set; }
    public RegistryValueKind Kind { get; set; }
    public string Value { get; set; } = "";

    public static RegSnap Capture(ISystemApi s, RegRoot root, string path, string name)
    {
        var value = s.RegGet(root, path, name);
        var snap = new RegSnap { Root = root, Path = path, Name = name, Exists = value is not null };
        if (value is null) return snap;
        snap.Kind = s.RegKind(root, path, name);
        snap.Value = value is byte[] bytes ? Convert.ToBase64String(bytes) : Convert.ToString(value) ?? "";
        return snap;
    }

    public void Restore(ISystemApi s)
    {
        if (!Exists)
        {
            s.RegDelete(Root, Path, Name);
            return;
        }
        object value = Kind switch
        {
            RegistryValueKind.DWord => int.Parse(Value),
            RegistryValueKind.QWord => long.Parse(Value),
            RegistryValueKind.Binary => Convert.FromBase64String(Value),
            _ => Value,
        };
        s.RegSet(Root, Path, Name, value, Kind);
    }
}

public sealed class Snapshot
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime When { get; set; }
    public List<RegSnap> Reg { get; set; } = [];
    public string? PowerGuid { get; set; }
    public string? CreatedPowerGuid { get; set; }
    public string? ServiceName { get; set; }
    public int ServiceStart { get; set; }
    public bool ServiceDelayed { get; set; }
    public bool ServiceRunning { get; set; }
    public List<PowerValueSnap> PowerValues { get; set; } = [];
    public string? UndoArgs { get; set; }
    public string? RestartAdapter { get; set; }
}

public sealed class PowerValueSnap
{
    public string Sub { get; set; } = "";
    public string Setting { get; set; } = "";
    public int Ac { get; set; }
}

public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string? _file;
    private readonly object _gate = new();

    public Dictionary<string, Snapshot> Items { get; private set; } = [];

    public BackupStore(string? file)
    {
        _file = file;
        if (file is null || !File.Exists(file)) return;
        Items = JsonSerializer.Deserialize<Dictionary<string, Snapshot>>(File.ReadAllText(file)) ?? [];
    }

    private static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DefaultDir => System.IO.Path.Combine(LocalData, "ShanksitoTweaker");

    /// <summary>Keeps the backups made under the app's previous name, otherwise they could no longer be undone.</summary>
    public static void MigrateOldData() => MoveData(System.IO.Path.Combine(LocalData, "FpsForge"), DefaultDir);

    /// <summary>Old files win: they hold the real undo data, whatever a first run wrote in the new folder.</summary>
    public static void MoveData(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Move(file, target, true);
        }
        Directory.Delete(from, true);
    }

    public void Save()
    {
        if (_file is null) return;
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            var json = JsonSerializer.Serialize(Items, Options);
            // Defender can hold a fresh file for a moment: a failed save must not undo a good tweak.
            for (var attempt = 1; ; attempt++)
            {
                var tmp = $"{_file}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _file, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 6)
                {
                    TryDelete(tmp);
                    Thread.Sleep(150 * attempt);
                }
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stray tmp file, harmless.
        }
    }
}
