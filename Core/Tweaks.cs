using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShanksitoTweaker.Core;

public enum Risk { Sur, Moyen, Avance }

public readonly record struct OpResult(bool Ok, string Message);

public abstract class Tweak
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Why { get; init; } = "";
    public string Gain { get; init; } = "";
    public string Group { get; init; } = "";
    public Risk Risk { get; init; } = Risk.Sur;
    public bool Reboot { get; init; }
    public bool Recommended { get; init; }

    public abstract bool IsApplied(ISystemApi s);
    public abstract Snapshot Capture(ISystemApi s);
    public abstract void Apply(ISystemApi s, Snapshot snap);
}

public sealed record RegEdit(RegRoot Root, string Path, string Name, object Value, RegistryValueKind Kind);

public sealed class RegistryTweak : Tweak
{
    public IReadOnlyList<RegEdit> Edits { get; init; } = [];

    public override bool IsApplied(ISystemApi s) =>
        Edits.All(e => Equals(s.RegGet(e.Root, e.Path, e.Name), e.Value));

    public override Snapshot Capture(ISystemApi s) => new()
    {
        Id = Id,
        Title = Title,
        When = DateTime.Now,
        Reg = Edits.Select(e => RegSnap.Capture(s, e.Root, e.Path, e.Name)).ToList(),
    };

    public override void Apply(ISystemApi s, Snapshot snap)
    {
        foreach (var e in Edits) s.RegSet(e.Root, e.Path, e.Name, e.Value, e.Kind);
    }
}

public sealed class PowerPlanTweak : Tweak
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private static readonly Regex GuidRegex = new("[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}", RegexOptions.IgnoreCase);

    public override bool IsApplied(ISystemApi s) =>
        Regex.IsMatch(s.Run("powercfg", "/getactivescheme").Output, "performance|" + HighPerformance, RegexOptions.IgnoreCase);

    public override Snapshot Capture(ISystemApi s)
    {
        var active = GuidRegex.Match(s.Run("powercfg", "/getactivescheme").Output);
        if (!active.Success) throw new InvalidOperationException("Plan actif illisible");
        return new Snapshot { Id = Id, Title = Title, When = DateTime.Now, PowerGuid = active.Value };
    }

    public override void Apply(ISystemApi s, Snapshot snap)
    {
        if (s.Run("powercfg", $"/setactive {HighPerformance}").Code == 0) return;
        // The plan is hidden on many laptops: it has to be cloned before it can be activated.
        var clone = GuidRegex.Match(s.Run("powercfg", $"/duplicatescheme {HighPerformance}").Output);
        if (!clone.Success) throw new InvalidOperationException("Plan Performances élevées indisponible");
        snap.CreatedPowerGuid = clone.Value;
        var result = s.Run("powercfg", $"/setactive {clone.Value}");
        if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
    }
}

/// <summary>One mains-power value of the active plan, e.g. USB selective suspend.</summary>
public sealed class PowerValueTweak : Tweak
{
    public string Sub { get; init; } = "";
    public string Setting { get; init; } = "";
    public int Target { get; init; }

    public static int? ReadAc(ISystemApi s, string sub, string setting)
    {
        var hex = Regex.Matches(s.Run("powercfg", $"/q SCHEME_CURRENT {sub} {setting}").Output, "0x([0-9a-f]{8})", RegexOptions.IgnoreCase);
        // The last two values of the output are the current AC then DC indexes, whatever the language.
        return hex.Count < 2 ? null : Convert.ToInt32(hex[^2].Groups[1].Value, 16);
    }

    public override bool IsApplied(ISystemApi s) => ReadAc(s, Sub, Setting) == Target;

    public override Snapshot Capture(ISystemApi s)
    {
        var current = ReadAc(s, Sub, Setting) ?? throw new InvalidOperationException("Réglage d'alimentation illisible");
        return new Snapshot
        {
            Id = Id, Title = Title, When = DateTime.Now,
            PowerValues = [new PowerValueSnap { Sub = Sub, Setting = Setting, Ac = current }],
        };
    }

    public override void Apply(ISystemApi s, Snapshot snap) => TweakEngine.SetPowerValue(s, Sub, Setting, Target);
}

/// <summary>Windows feature driven by PowerShell: the three commands are fixed in the catalog.</summary>
public sealed class CommandTweak : Tweak
{
    public string Detect { get; init; } = "";
    public string Enable { get; init; } = "";
    public string Disable { get; init; } = "";

    public static string Ps(string command) => $"-NoProfile -Command \"{command}\"";

    public override bool IsApplied(ISystemApi s) => s.Run("powershell", Ps(Detect)).Output.Contains("YES");

    public override Snapshot Capture(ISystemApi s) =>
        new() { Id = Id, Title = Title, When = DateTime.Now, UndoArgs = Ps(Disable) };

    public override void Apply(ISystemApi s, Snapshot snap)
    {
        var result = s.Run("powershell", Ps(Enable));
        if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
    }
}

/// <summary>Driver properties of the network card, stored as strings. Needs an adapter restart.</summary>
public sealed class AdapterTweak : Tweak
{
    public string AdapterName { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public IReadOnlyList<(string Keyword, string Value)> Values { get; init; } = [];

    public static string RestartArgs(string adapter) =>
        CommandTweak.Ps($"Restart-NetAdapter -Name '{adapter.Replace("'", "''")}' -Confirm:$false");

    public override bool IsApplied(ISystemApi s) =>
        Values.All(v => s.RegGet(RegRoot.HKLM, KeyPath, v.Keyword) as string == v.Value);

    public override Snapshot Capture(ISystemApi s) => new()
    {
        Id = Id, Title = Title, When = DateTime.Now, RestartAdapter = AdapterName,
        Reg = Values.Select(v => RegSnap.Capture(s, RegRoot.HKLM, KeyPath, v.Keyword)).ToList(),
    };

    public override void Apply(ISystemApi s, Snapshot snap)
    {
        foreach (var (keyword, value) in Values) s.RegSet(RegRoot.HKLM, KeyPath, keyword, value, RegistryValueKind.String);
        var result = s.Run("powershell", RestartArgs(AdapterName));
        if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
    }
}

/// <summary>State comes from the scan: asking Windows per row would cost a second each.</summary>
public sealed class ScheduledTaskTweak : Tweak
{
    public string TaskPath { get; init; } = "";
    public string TaskName { get; init; } = "";
    public bool Disabled { get; set; }

    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
    private string Target => $"-TaskPath {Quote(TaskPath)} -TaskName {Quote(TaskName)}";

    public override bool IsApplied(ISystemApi s) => Disabled;

    public override Snapshot Capture(ISystemApi s) => new()
    {
        Id = Id, Title = Title, When = DateTime.Now,
        UndoArgs = CommandTweak.Ps($"Enable-ScheduledTask {Target} | Out-Null"),
    };

    public override void Apply(ISystemApi s, Snapshot snap)
    {
        var result = s.Run("powershell", CommandTweak.Ps($"Disable-ScheduledTask {Target} -ErrorAction Stop | Out-Null"));
        if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
        Disabled = true;
    }
}

public sealed class ServiceTweak : Tweak
{
    /// <summary>Security and anti-cheat services: never offered, whatever the catalog says.</summary>
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "MDCoreSvc", "WdNisSvc", "WdFilter", "WdBoot", "SecurityHealthService", "wscsvc",
        "mpssvc", "BFE", "vgc", "vgk", "EventLog", "RpcSs", "CryptSvc",
    };

    public string ServiceName { get; }
    private string KeyPath => $@"SYSTEM\CurrentControlSet\Services\{ServiceName}";

    public ServiceTweak(string serviceName)
    {
        if (Protected.Contains(serviceName))
            throw new ArgumentException($"Service protégé : {serviceName}");
        ServiceName = serviceName;
    }

    public bool Exists(ISystemApi s) => s.RegGet(RegRoot.HKLM, KeyPath, "Start") is not null;

    public override bool IsApplied(ISystemApi s) => s.RegGet(RegRoot.HKLM, KeyPath, "Start") is 3 or 4;

    public override Snapshot Capture(ISystemApi s) => new()
    {
        Id = Id,
        Title = Title,
        When = DateTime.Now,
        ServiceName = ServiceName,
        ServiceStart = s.RegGet(RegRoot.HKLM, KeyPath, "Start") as int? ?? 2,
        ServiceDelayed = s.RegGet(RegRoot.HKLM, KeyPath, "DelayedAutostart") is 1,
        ServiceRunning = s.Run("sc.exe", $"query \"{ServiceName}\"").Output.Contains("RUNNING"),
    };

    public override void Apply(ISystemApi s, Snapshot snap)
    {
        var result = s.Run("sc.exe", $"config \"{ServiceName}\" start= demand");
        if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
        s.Run("sc.exe", $"stop \"{ServiceName}\"");
    }
}

/// <summary>Same mechanism as the Startup tab of Task Manager, so it shows up there too.</summary>
public sealed class StartupTweak : Tweak
{
    public RegRoot ApprovedRoot { get; init; }
    public string ApprovedPath { get; init; } = "";
    public string ValueName { get; init; } = "";
    public string Command { get; init; } = "";
    public bool Locked { get; init; }

    public override bool IsApplied(ISystemApi s) =>
        s.RegGet(ApprovedRoot, ApprovedPath, ValueName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;

    public override Snapshot Capture(ISystemApi s) => new()
    {
        Id = Id,
        Title = Title,
        When = DateTime.Now,
        Reg = [RegSnap.Capture(s, ApprovedRoot, ApprovedPath, ValueName)],
    };

    public override void Apply(ISystemApi s, Snapshot snap)
    {
        if (Locked) throw new InvalidOperationException("Entrée verrouillée");
        var value = new byte[12];
        value[0] = 3;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
        s.RegSet(ApprovedRoot, ApprovedPath, ValueName, value, RegistryValueKind.Binary);
    }
}

public sealed class TweakEngine(ISystemApi sys, BackupStore store, Action<string>? log = null)
{
    private readonly object _gate = new();

    public BackupStore Store => store;

    /// <summary>One change at a time: two switches flipped quickly must not race on the backup.</summary>
    public OpResult Apply(Tweak tweak)
    {
        lock (_gate) return ApplyCore(tweak);
    }

    public OpResult Revert(string id)
    {
        lock (_gate) return RevertCore(id);
    }

    private OpResult ApplyCore(Tweak tweak)
    {
        if (tweak.IsApplied(sys)) return new OpResult(true, $"{tweak.Title} : déjà actif");
        var snap = tweak.Capture(sys);
        // Saved first: a crash mid-apply must still leave a way back.
        store.Items[tweak.Id] = snap;
        store.Save();
        try
        {
            tweak.Apply(sys, snap);
            store.Save();
            if (!tweak.IsApplied(sys)) throw new InvalidOperationException("non confirmé après écriture");
        }
        catch (Exception ex)
        {
            log?.Invoke($"ECHEC {tweak.Id}: {ex.Message}");
            try
            {
                Restore(snap);
            }
            catch (Exception restoreError)
            {
                // Keep the snapshot: the Annuler page is the way back once the cause is gone.
                log?.Invoke($"ECHEC rollback {tweak.Id}: {restoreError.Message}");
                return new OpResult(false, $"{tweak.Title} : échec partiel, à annuler dans l'onglet Annuler ({ex.Message})");
            }
            store.Items.Remove(tweak.Id);
            store.Save();
            return new OpResult(false, $"{tweak.Title} : échec, rien n'a changé ({ex.Message})");
        }
        log?.Invoke($"APPLIQUE {tweak.Id}");
        return new OpResult(true, tweak.Reboot ? $"{tweak.Title} : actif après redémarrage" : $"{tweak.Title} : actif");
    }

    public bool CanRevert(string id) => store.Items.ContainsKey(id);

    private OpResult RevertCore(string id)
    {
        if (!store.Items.TryGetValue(id, out var snap))
            return new OpResult(false, "Aucune sauvegarde pour ce réglage");
        try
        {
            Restore(snap);
        }
        catch (Exception ex)
        {
            log?.Invoke($"ECHEC annulation {id}: {ex.Message}");
            return new OpResult(false, $"{snap.Title} : annulation échouée ({ex.Message})");
        }
        store.Items.Remove(id);
        store.Save();
        log?.Invoke($"ANNULE {id}");
        return new OpResult(true, $"{snap.Title} : état d'origine restauré");
    }

    public static void SetPowerValue(ISystemApi s, string sub, string setting, int value)
    {
        var result = s.Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {sub} {setting} {value}");
        if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
        s.Run("powercfg", "/setactive SCHEME_CURRENT");
    }

    private void Restore(Snapshot snap)
    {
        foreach (var reg in snap.Reg) reg.Restore(sys);
        foreach (var power in snap.PowerValues) SetPowerValue(sys, power.Sub, power.Setting, power.Ac);
        if (snap.UndoArgs is not null) sys.Run("powershell", snap.UndoArgs);
        if (snap.RestartAdapter is not null) sys.Run("powershell", AdapterTweak.RestartArgs(snap.RestartAdapter));
        if (snap.PowerGuid is not null)
        {
            var result = sys.Run("powercfg", $"/setactive {snap.PowerGuid}");
            if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
            if (snap.CreatedPowerGuid is not null) sys.Run("powercfg", $"/delete {snap.CreatedPowerGuid}");
        }
        if (snap.ServiceName is not null)
        {
            var mode = snap.ServiceStart switch
            {
                2 when snap.ServiceDelayed => "delayed-auto",
                2 => "auto",
                4 => "disabled",
                _ => "demand",
            };
            var result = sys.Run("sc.exe", $"config \"{snap.ServiceName}\" start= {mode}");
            if (result.Code != 0) throw new InvalidOperationException(result.Output.Trim());
            if (snap.ServiceRunning) sys.Run("sc.exe", $"start \"{snap.ServiceName}\"");
        }
    }
}
