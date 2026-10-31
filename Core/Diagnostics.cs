using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ShanksitoTweaker.Core;

public enum Severity { Critique, Attention, Info, Ok }

public sealed record Finding(Severity Severity, string Title, string Detail, int Penalty = 0, string? Page = null, string? ActionLabel = null, string? ActionTarget = null);

public sealed class ScanData
{
    public ValoConfig Valo = new();
    public List<PerfSession> Sessions = [];
    public List<DisplayInfo> Displays = [];
    public GpuSample? Gpu;
    public string DriverVersion = "";
    public DateTime? DriverDate;
    public int DriverErrors;
    public int OldDriverErrors;
    public DateTime? LastDriverError;
    public int RamPct;
    public double RamGb;
    public double DiskFreeGb, DiskFreePct;
    public bool OnBattery;
    public bool Hvci;
    public List<Tweak> Windows = [];
    public List<Tweak> Startup = [];
    public List<Tweak> Services = [];
    public List<Tweak> Network = [];
    public List<Tweak> Tasks = [];
    public List<Finding> Findings = [];
    public NetInfo? Net;
    public NetTest? NetTest;
    public BloatResult? Bloat;
    public HealthInfo Health = new();
    public List<MatchReport> Matches = [];
    public string Cpu = "";
    public string GpuName = "";
    public string Machine = "";
    public bool IsLaptop;
    public bool IsAsus;

    public bool HasNvidia => GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<Tweak> AllTweaks => Windows.Concat(Network).Concat(Startup).Concat(Services).Concat(Tasks);

    /// <summary>Screen VALORANT is configured on, falling back to the primary one.</summary>
    public DisplayInfo? GameDisplay =>
        Displays.FirstOrDefault(d => d.Model.Length > 0 && d.Model == Valo.Monitor) ?? Displays.FirstOrDefault(d => d.Primary);

    /// <summary>Quality of the setup out of 100. It rates the configuration, not the FPS.</summary>
    public int Score => Math.Max(0, 100 - Findings.Sum(f => f.Penalty));
}

public static class Diagnostics
{
    private const string NvidiaDrivers = "https://www.nvidia.com/fr-fr/drivers/";
    private const byte NoBattery = 128;
    private const byte UnknownBattery = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    public static ScanData Scan(ISystemApi s)
    {
        var data = new ScanData
        {
            Valo = ValorantFiles.Load(),
            Sessions = PerfLogs.Load(),
            Displays = Displays.List(),
            Gpu = Gpu.Sample(s),
            Matches = GameWatcher.LoadAll(),
            Health = Health.Read(s),
        };
        (data.DriverVersion, data.DriverDate) = Gpu.Driver(s);
        // Crashes from a driver that is gone say nothing about the one installed now.
        var installed = Gpu.DriverInstalled(s);
        (data.DriverErrors, data.LastDriverError) = Gpu.DriverErrors(7, installed);
        data.OldDriverErrors = Gpu.DriverErrors(7).Total - data.DriverErrors;
        (data.RamPct, data.RamGb) = Memory.Status();
        var disk = new DriveInfo("C");
        data.DiskFreeGb = disk.AvailableFreeSpace / 1073741824.0;
        data.DiskFreePct = 100.0 * disk.AvailableFreeSpace / disk.TotalSize;
        var hasPower = GetSystemPowerStatus(out var power);
        data.OnBattery = hasPower && power.ACLineStatus == 0;
        data.IsLaptop = hasPower && power.BatteryFlag is not (NoBattery or UnknownBattery);
        data.Hvci = s.RegGet(RegRoot.HKLM,
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") is 1;
        var exe = ValorantFiles.GameExe();
        data.Net = Network.Active(s);
        data.Windows = Catalog.Windows(exe);
        data.Network = Catalog.Network(data.Net, exe);
        data.Startup = Catalog.Startup(s);
        data.Services = Catalog.Services(s);
        data.Tasks = Catalog.Tasks(s);
        data.Cpu = Convert.ToString(s.RegGet(RegRoot.HKLM, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString"))?.Trim() ?? "";
        data.Machine = Convert.ToString(s.RegGet(RegRoot.HKLM, @"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName")) ?? "";
        data.IsAsus = data.Machine.Contains("ASUS", StringComparison.OrdinalIgnoreCase) || data.Machine.Contains("ROG", StringComparison.OrdinalIgnoreCase);
        data.GpuName = Gpu.Name(s);
        data.Findings = BuildFindings(data, s);
        return data;
    }

    public static List<Finding> BuildFindings(ScanData d, ISystemApi s)
    {
        var f = new List<Finding>();
        var last = d.Sessions.LastOrDefault();

        if (!d.Valo.Found)
            f.Add(new Finding(Severity.Info, "Valorant introuvable", "Lance le jeu une fois : ses réglages et ses mesures apparaîtront ici."));

        if (d.Valo.LimitAlways == true && d.Valo.MaxAlways is { } cap)
        {
            var low = cap <= 144;
            var screen = d.GameDisplay?.Current ?? 0;
            f.Add(new Finding(low ? Severity.Critique : Severity.Attention, $"Valorant limité à {cap:0} FPS",
                screen > cap
                    ? $"Ton écran peut afficher {screen} images par seconde. La limite vaut le coup si elle garde le PC plus frais, sinon retire-la dans le jeu."
                    : "Limite adaptée à ton écran. Tu peux la retirer pour un peu moins de latence, mais le PC chauffera plus.",
                low ? 25 : 5, "valorant"));
        }

        if (d.GameDisplay is { } display && display.Max > display.Current)
            f.Add(new Finding(Severity.Critique, $"Écran de jeu ({display.Model}) réglé à {display.Current} Hz sur {display.Max} Hz possibles",
                $"Même à {display.Max} FPS, l'écran n'afficherait que {display.Current} images par seconde.", 20, "display"));

        if (d.OnBattery)
            f.Add(new Finding(Severity.Critique, "PC sur batterie", "Le GPU est fortement bridé hors secteur.", 20));

        if (d.DriverErrors > 0)
            f.Add(new Finding(Severity.Critique, $"{d.DriverErrors} erreurs du driver GPU sur 7 jours",
                $"Dernière le {d.LastDriverError:dd/MM à HH:mm}. Plantages et freezes du driver NVIDIA. Mets à jour le driver, puis surveille l'onglet Santé.",
                15, "health", "Page drivers NVIDIA", NvidiaDrivers));
        else if (d.OldDriverErrors > 0)
            f.Add(new Finding(Severity.Info, "Aucune erreur GPU depuis le nouveau driver",
                $"{d.OldDriverErrors} erreurs avant la mise à jour. Si elles ne reviennent pas après quelques parties, c'était bien le driver.", 0, "health"));

        if (d.Gpu is { } gpu && gpu.Util < 15 && gpu.Temp >= 70)
        {
            var cooling = !d.IsLaptop
                ? "Vérifie les ventilateurs et dépoussière le boîtier."
                : d.IsAsus
                    ? "Passe en Turbo dans Armoury Crate, surélève l'arrière et dépoussière les aérations."
                    : "Passe en mode performance dans le logiciel du fabricant, surélève l'arrière et dépoussière les aérations.";
            f.Add(new Finding(Severity.Attention, $"GPU à {gpu.Temp:0} °C au repos",
                $"Très chaud sans charge. Le PC peut baisser ses fréquences en partie. {cooling}", 10));
        }

        if (d.Gpu is { } idle && idle.Util < 5 && idle.Clock >= 1000)
            f.Add(new Finding(Severity.Attention, $"GPU à {idle.Clock:0} MHz sans rien faire",
                $"Il consomme {idle.Power:0} W et chauffe pour rien. Dans le panneau NVIDIA, remets la gestion de l'alimentation sur Optimale en global, et mets Performances maximales seulement pour Valorant.", 5, "valorant"));

        if (d.DriverDate is { } date && (DateTime.Now - date).TotalDays > 180)
            f.Add(new Finding(Severity.Attention, $"Driver NVIDIA {d.DriverVersion} daté du {date:dd/MM/yyyy}",
                "Ancien. Les versions récentes corrigent stabilité et performances.", 5, null, "Page drivers NVIDIA", NvidiaDrivers));

        var memPct = last?.MemoryPct ?? d.RamPct;
        if (memPct >= 75)
            f.Add(new Finding(Severity.Attention, $"RAM à {memPct:0} % pendant le jeu",
                $"{d.RamGb:0} Go, c'est juste avec autant d'applis ouvertes. Ferme-les avant de jouer.", memPct >= 85 ? 10 : 5, "session"));

        var enabled = d.Startup.Count(t => !t.IsApplied(s));
        var suggested = d.Startup.Count(t => t.Recommended && !t.IsApplied(s));
        if (suggested > 0)
            f.Add(new Finding(Severity.Attention, $"{enabled} applis au démarrage, {suggested} dispensable{(suggested > 1 ? "s" : "")}", "Moins d'applis lancées, plus de RAM libre.", Math.Min(10, suggested * 2), "startup"));

        if (d.Net is { WiFi: true })
            f.Add(new Finding(Severity.Attention, "Tu joues en Wi-Fi", "Un câble Ethernet réduit le ping et surtout ses variations. C'est le plus gros gain réseau possible.", 10, "network"));

        if (d.Net?.Vpn is { } vpn)
            f.Add(new Finding(Severity.Attention, "VPN actif", $"{vpn} rallonge le trajet jusqu'aux serveurs. Coupe-le pour jouer.", 5, "network"));

        if (d.Health.PendingReboot)
            f.Add(new Finding(Severity.Attention, "Redémarrage en attente", "Une mise à jour Windows attend. Redémarre avant de jouer.", 3));

        if (d.DiskFreePct < 10)
            f.Add(new Finding(Severity.Info, $"SSD plein à {100 - d.DiskFreePct:0} %",
                $"{d.DiskFreeGb:0} Go libres. Sous 10 %, un SSD ralentit.", 3, "clean"));

        if (d.Hvci)
            f.Add(new Finding(Severity.Info, "Intégrité de la mémoire activée",
                "Protection Windows qui coûte quelques FPS. Shanksito Tweaker ne la coupe pas : c'est une sécurité, et Vanguard peut l'exiger. Ton choix, dans Sécurité Windows.",
                0, null, "Sécurité Windows", "windowsdefender://coreisolation"));

        return f.OrderBy(x => x.Severity).ToList();
    }

    /// <summary>Models tend to average every row they see: this states plainly what is true today.</summary>
    private static void AddSummary(StringBuilder b, ScanData d)
    {
        b.AppendLine("== RÉSUMÉ : chiffres actuels, à utiliser en priorité");
        var cap = d.Valo.LimitAlways == true ? d.Valo.MaxAlways : null;
        if (d.Sessions.LastOrDefault() is { } last)
        {
            b.AppendLine($"Dernière partie ({last.Start:dd/MM HH:mm}) : {last.AvgFps:0} FPS en moyenne, {last.PctUnder120:0} % des images sous 120 FPS, RAM {last.MemoryPct:0} %, CPU en fond {last.SystemCpu:0} %, GPU {last.GpuMs:0.0} ms par image");
            var older = d.Sessions.SkipLast(1).TakeLast(5).ToList();
            if (older.Count > 0) b.AppendLine($"Parties d'avant : {older.Average(s => s.AvgFps):0} FPS en moyenne. Ces anciens chiffres ne décrivent plus le PC.");
            if (cap is { } limit)
                b.AppendLine(last.AvgFps >= limit * 0.93
                    ? $"Limite de FPS du jeu : {limit:0}. La moyenne la touche : c'est elle qui plafonne."
                    : $"Limite de FPS du jeu : {limit:0}. La moyenne reste en dessous : ce n'est pas le premier frein.");
        }
        else
        {
            b.AppendLine("Aucune partie mesurée pour l'instant.");
        }
        if (cap is null) b.AppendLine("Limite de FPS du jeu : aucune.");
        if (d.GameDisplay is { } screen) b.AppendLine($"Écran de jeu : {screen.Current} Hz sur {screen.Max} Hz possibles.");
        if (d.Matches.FirstOrDefault() is { } match) b.AppendLine($"GPU en partie : {match.AvgTemp:0} °C, max {match.MaxTemp:0} °C, bridage thermique {match.ThermalPct:0} % du temps.");
    }

    public static string Report(ScanData d, ISystemApi s, int sessions = 8)
    {
        var b = new StringBuilder();
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        AddSummary(b, d);
        b.AppendLine("== MACHINE");
        b.AppendLine($"{d.Machine} | {d.Cpu} | {d.GpuName} | RAM {d.RamGb:0} Go | driver {d.DriverVersion} | SSD libre {d.DiskFreeGb:0} Go | batterie={d.OnBattery}");
        if (d.Gpu is { } g) b.AppendLine($"GPU maintenant : {g.Temp:0} °C, charge {g.Util:0} %, {g.Clock:0} MHz, {g.Power:0} W");
        b.AppendLine($"Score configuration : {d.Score}/100");
        b.AppendLine("== HEALTH");
        var h = d.Health;
        b.AppendLine($"uptime {h.UptimeHours:0} h | RAM {h.RamSticks} barrettes {h.RamChannels} canaux {h.RamSpeed} MHz | SSD {h.DiskHealth} {h.DiskTemp} °C usure {h.DiskWear} % | batterie usée {h.BatteryWearPct:0} %");
        b.AppendLine($"14 jours : WHEA {h.WheaErrors}, arrêts brutaux {h.HardResets}, crashs Valorant {h.GameCrashes}, erreurs GPU {h.GpuErrorsByDay.Values.Sum()}");
        b.AppendLine("== NETWORK");
        if (d.Net is { } n) b.AppendLine($"{(n.WiFi ? "Wi-Fi" : "Ethernet")} {n.SpeedMbps} Mbps | {n.Description} | vpn={n.Vpn ?? "aucun"}");
        if (d.NetTest is { } test)
        {
            foreach (var p in new[] { test.Gateway, test.Internet, test.GameRegion }.OfType<Probe>())
                b.AppendLine($"{p.Label}: moy {p.Avg:0.0} ms, min {p.Min:0.0}, max {p.Max:0.0}, perte {p.LossPct:0} %");
            b.AppendLine("Verdict test : " + test.Verdict);
        }
        if (d.Bloat is { } bloat)
            b.AppendLine($"Bufferbloat {bloat.Grade}: repos {bloat.IdleMs:0} ms, réception {bloat.DownMs:0} ms, envoi {bloat.UpMs:0} ms, {bloat.DownMbps:0}/{bloat.UpMbps:0} Mbps");
        b.AppendLine("== MATCHES");
        foreach (var m in d.Matches.Take(5))
            b.AppendLine($"{m.Start:dd/MM HH:mm} {m.Minutes:0} min, GPU {m.AvgTemp:0}-{m.MaxTemp:0} °C, charge {m.AvgUtil:0} %, {m.AvgClock:0} MHz, {m.AvgPower:0} W, thermique {m.ThermalPct:0} %, ping {m.PingAvg:0}/{m.PingMax:0} ms, perte {m.LossPct:0.#} %, pics {m.Spikes}");
        b.AppendLine("== FINDINGS");
        foreach (var f in d.Findings) b.AppendLine($"[{f.Severity}] {f.Title} :: {f.Detail}");
        b.AppendLine("== DISPLAYS");
        foreach (var x in d.Displays)
            b.AppendLine($"{x.Device} {x.Model} primary={x.Primary} {x.Width}x{x.Height}@{x.Current} rates={string.Join(",", x.Rates)}");
        b.AppendLine($"game display={d.GameDisplay?.Model} valo monitor={d.Valo.Monitor} res={d.Valo.Resolution} vsync={d.Valo.VSync} fs={d.Valo.FullscreenMode} cap={(d.Valo.LimitAlways == true ? d.Valo.MaxAlways : null)}");
        b.AppendLine("== SESSIONS");
        foreach (var x in d.Sessions.TakeLast(sessions))
            b.AppendLine(string.Format(fr, "{0:dd/MM HH:mm} rounds={1} fps={2:0} <120={3:0}% <144={4:0}% >240={5:0.0}% work={6:0.0}ms gpu={7:0.0}ms syscpu={8:0}% ram={9:0}%",
                x.Start, x.Rounds, x.AvgFps, x.PctUnder120, x.PctUnder144, x.PctOver240, x.WorkMs, x.GpuMs, x.SystemCpu, x.MemoryPct));
        b.AppendLine("== BACKGROUND APPS");
        foreach (var a in GameSession.Scan()) b.AppendLine($"{a.Label}: {a.Megabytes:0} MB");
        return b.ToString();
    }

    /// <summary>Only what can still be switched on: these ids are also the only ones the model is able to output.</summary>
    public static string TweakCatalog(ScanData d, ISystemApi s)
    {
        var b = new StringBuilder();
        foreach (var t in d.AllTweaks)
        {
            if (t is StartupTweak { Locked: true } || t.IsApplied(s)) continue;
            b.AppendLine($"{t.Id} | {t.Group} | {t.Title} | effet: {t.Gain} | risque: {t.Risk}");
        }
        return b.ToString();
    }
}
