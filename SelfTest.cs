using System.Text;
using System.Text.RegularExpressions;
using ShanksitoTweaker.Core;
using Microsoft.Win32;

namespace ShanksitoTweaker;

/// <summary>Runs every apply/revert path against an in-memory fake: the real machine is never written to.</summary>
public static class SelfTest
{
    private sealed class FakeSystem : ISystemApi
    {
        private readonly Dictionary<string, (object Value, RegistryValueKind Kind)> _reg = [];
        public string ActivePlan = "381b4222-f694-41f0-9685-ff5bb260df2e";
        public bool HighPerfHidden;
        public bool FailWrites;
        public int PowerValue;
        public bool CommandOn;
        public readonly List<string> Commands = [];

        private static string K(RegRoot r, string p, string n) => $"{r}|{p}|{n}".ToLowerInvariant();

        public object? RegGet(RegRoot root, string path, string name) =>
            _reg.TryGetValue(K(root, path, name), out var v) ? v.Value : null;

        public RegistryValueKind RegKind(RegRoot root, string path, string name) =>
            _reg.TryGetValue(K(root, path, name), out var v) ? v.Kind : RegistryValueKind.Unknown;

        public string[] RegNames(RegRoot root, string path)
        {
            var prefix = $"{root}|{path}|".ToLowerInvariant();
            return _reg.Keys.Where(k => k.StartsWith(prefix)).Select(k => k[prefix.Length..]).ToArray();
        }

        public void RegSet(RegRoot root, string path, string name, object value, RegistryValueKind kind)
        {
            if (FailWrites) throw new UnauthorizedAccessException("denied");
            _reg[K(root, path, name)] = (value, kind);
        }

        public void RegDelete(RegRoot root, string path, string name) => _reg.Remove(K(root, path, name));

        public void RegDeleteKey(RegRoot root, string path)
        {
            var prefix = $"{root}|{path}|".ToLowerInvariant();
            foreach (var key in _reg.Keys.Where(k => k.StartsWith(prefix)).ToList()) _reg.Remove(key);
        }

        public ISystemApi? Real;

        public CommandResult Run(string exe, string args)
        {
            Commands.Add($"{exe} {args}");
            if (Real is not null) return Real.Run(exe, args);
            if (exe == "powershell")
            {
                if (args.Contains("detect")) return new CommandResult(0, CommandOn ? "YES" : "NO");
                if (args.Contains("enable")) CommandOn = true;
                if (args.Contains("disable")) CommandOn = false;
                return new CommandResult(0, "");
            }
            if (exe == "powercfg")
            {
                if (args.StartsWith("/q ")) return new CommandResult(0, $"Max: 0x00000064\nAC : 0x{PowerValue:x8}\nDC : 0x00000005");
                if (args.StartsWith("/setacvalueindex "))
                {
                    PowerValue = int.Parse(args.Split(' ')[^1]);
                    return new CommandResult(0, "");
                }
                if (args == "/getactivescheme")
                {
                    var name = ActivePlan == "381b4222-f694-41f0-9685-ff5bb260df2e" ? "Utilisation normale" : "Performances élevées";
                    return new CommandResult(0, $"GUID : {ActivePlan}  ({name})");
                }
                if (args.StartsWith("/setactive "))
                {
                    var guid = args[11..];
                    if (HighPerfHidden && guid == PowerPlanTweak.HighPerformance) return new CommandResult(1, "invalid");
                    ActivePlan = guid;
                    return new CommandResult(0, "");
                }
                if (args.StartsWith("/duplicatescheme")) return new CommandResult(0, "GUID : aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee  (Performances élevées)");
            }
            if (exe == "sc.exe" && args.StartsWith("config"))
            {
                var name = args.Split('"')[1];
                var mode = args.Split("start= ")[1];
                _reg[K(RegRoot.HKLM, $@"SYSTEM\CurrentControlSet\Services\{name}", "Start")] =
                    (mode switch { "auto" or "delayed-auto" => 2, "disabled" => 4, _ => 3 }, RegistryValueKind.DWord);
                return new CommandResult(0, "");
            }
            if (exe == "sc.exe" && args.StartsWith("query")) return new CommandResult(0, "STATE : 4 RUNNING");
            return new CommandResult(0, "");
        }
    }

    private static readonly StringBuilder Log = new();
    private static int _failures;

    private static void Check(string name, bool ok)
    {
        Log.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}");
        if (!ok) _failures++;
    }

    private static (FakeSystem Sys, TweakEngine Engine) Fresh()
    {
        var sys = new FakeSystem();
        return (sys, new TweakEngine(sys, new BackupStore(null)));
    }

    private static RegistryTweak Sample => new()
    {
        Id = "t", Title = "T",
        Edits =
        [
            new RegEdit(RegRoot.HKCU, "A", "existing", 1, RegistryValueKind.DWord),
            new RegEdit(RegRoot.HKCU, "A", "absent", 1, RegistryValueKind.DWord),
        ],
    };

    private static void Registry()
    {
        var (sys, engine) = Fresh();
        sys.RegSet(RegRoot.HKCU, "A", "existing", 0, RegistryValueKind.DWord);
        Check("registry: applied", engine.Apply(Sample).Ok && Sample.IsApplied(sys));
        Check("registry: second apply keeps original backup", engine.Apply(Sample).Ok && engine.Store.Items["t"].Reg[0].Value == "0");
        Check("registry: revert ok", engine.Revert("t").Ok);
        Check("registry: existing value restored", sys.RegGet(RegRoot.HKCU, "A", "existing") is 0);
        Check("registry: absent value deleted again", sys.RegGet(RegRoot.HKCU, "A", "absent") is null);
        Check("registry: backup cleared", !engine.CanRevert("t"));
        Check("registry: revert without backup refused", !engine.Revert("t").Ok);
    }

    private static void Rollback()
    {
        var (sys, engine) = Fresh();
        sys.RegSet(RegRoot.HKCU, "A", "existing", 0, RegistryValueKind.DWord);
        sys.FailWrites = true;
        var result = engine.Apply(Sample);
        Check("rollback: failure reported", !result.Ok);
        Check("rollback: snapshot kept while restore is impossible", engine.CanRevert("t"));
        sys.FailWrites = false;
        Check("rollback: value untouched", sys.RegGet(RegRoot.HKCU, "A", "existing") is 0);
        Check("rollback: revert later works", engine.Revert("t").Ok && !engine.CanRevert("t"));

        var (sys2, engine2) = Fresh();
        var locked = new StartupTweak { Id = "l", Title = "L", ApprovedRoot = RegRoot.HKCU, ApprovedPath = "X", ValueName = "v", Locked = true };
        Check("rollback: clean failure leaves no backup", !engine2.Apply(locked).Ok && !engine2.CanRevert("l"));
        Check("rollback: clean failure wrote nothing", sys2.RegGet(RegRoot.HKCU, "X", "v") is null);
    }

    private static void Power()
    {
        var tweak = new PowerPlanTweak { Id = "p", Title = "P" };

        var (sys, engine) = Fresh();
        var original = sys.ActivePlan;
        Check("power: applied", engine.Apply(tweak).Ok && sys.ActivePlan == PowerPlanTweak.HighPerformance);
        Check("power: revert", engine.Revert("p").Ok && sys.ActivePlan == original);

        (sys, engine) = Fresh();
        sys.HighPerfHidden = true;
        Check("power: hidden plan cloned", engine.Apply(tweak).Ok && sys.ActivePlan.StartsWith("aaaaaaaa"));
        Check("power: revert removes the clone", engine.Revert("p").Ok && sys.ActivePlan == original
            && sys.Commands.Contains("powercfg /delete aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    }

    private static void Services()
    {
        var (sys, engine) = Fresh();
        var key = @"SYSTEM\CurrentControlSet\Services\Red Giant Service";
        sys.RegSet(RegRoot.HKLM, key, "Start", 2, RegistryValueKind.DWord);
        var tweak = new ServiceTweak("Red Giant Service") { Id = "s", Title = "S" };
        Check("service: manual", engine.Apply(tweak).Ok && sys.RegGet(RegRoot.HKLM, key, "Start") is 3);
        Check("service: stopped", sys.Commands.Contains("sc.exe stop \"Red Giant Service\""));
        Check("service: revert to auto and restart", engine.Revert("s").Ok && sys.RegGet(RegRoot.HKLM, key, "Start") is 2
            && sys.Commands.Contains("sc.exe start \"Red Giant Service\""));

        foreach (var name in new[] { "WinDefend", "vgc", "vgk", "mpssvc", "MDCoreSvc" })
        {
            var refused = false;
            try { _ = new ServiceTweak(name); } catch (ArgumentException) { refused = true; }
            Check($"service: {name} refused", refused);
        }
    }

    private static void Startup()
    {
        var (sys, engine) = Fresh();
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        sys.RegSet(RegRoot.HKCU, run, "overwolf", "C:\\ow.exe", RegistryValueKind.String);
        sys.RegSet(RegRoot.HKLM, run, "riot vanguard", "C:\\Program Files\\Riot Vanguard\\vgtray.exe", RegistryValueKind.String);
        sys.RegSet(RegRoot.HKLM, run, "securityhealth", "C:\\sec.exe", RegistryValueKind.String);
        var list = Catalog.Startup(sys).Cast<StartupTweak>().Where(t => t.ApprovedPath.EndsWith(@"\Run")).ToList();
        var overwolf = list.First(t => t.ValueName == "overwolf");
        Check("startup: suggested", overwolf.Recommended);
        Check("startup: vanguard locked", list.First(t => t.ValueName == "riot vanguard").Locked);
        Check("startup: defender tray locked", list.First(t => t.ValueName == "securityhealth").Locked);
        Check("startup: locked refused", !engine.Apply(list.First(t => t.Locked)).Ok);
        Check("startup: disabled", engine.Apply(overwolf).Ok && overwolf.IsApplied(sys));
        Check("startup: flag byte", sys.RegGet(RegRoot.HKCU, approved, "overwolf") is byte[] { Length: 12 } b && b[0] == 3);
        Check("startup: revert", engine.Revert(overwolf.Id).Ok && !overwolf.IsApplied(sys));
        Check("startup: disabled flags read", IsDisabled(sys, new byte[] { 7, 0 }) && !IsDisabled(sys, new byte[] { 2, 0 }) && !IsDisabled(sys, new byte[] { 6, 0 }));
    }

    private static bool IsDisabled(FakeSystem sys, byte[] flags)
    {
        sys.RegSet(RegRoot.HKCU, "X", "v", flags, RegistryValueKind.Binary);
        return new StartupTweak { ApprovedRoot = RegRoot.HKCU, ApprovedPath = "X", ValueName = "v" }.IsApplied(sys);
    }

    private static void NewTweakTypes()
    {
        var (sys, engine) = Fresh();
        var usb = new PowerValueTweak { Id = "pv", Title = "PV", Sub = "SUB", Setting = "SET", Target = 0 };
        sys.PowerValue = 1;
        Check("power value: applied", engine.Apply(usb).Ok && sys.PowerValue == 0);
        Check("power value: plan reloaded", sys.Commands.Contains("powercfg /setactive SCHEME_CURRENT"));
        Check("power value: restored", engine.Revert("pv").Ok && sys.PowerValue == 1);

        (sys, engine) = Fresh();
        var qos = new CommandTweak { Id = "c", Title = "C", Detect = "detect", Enable = "enable", Disable = "disable" };
        Check("command: applied", engine.Apply(qos).Ok && sys.CommandOn);
        Check("command: undone", engine.Revert("c").Ok && !sys.CommandOn);

        (sys, engine) = Fresh();
        sys.RegSet(RegRoot.HKLM, "NIC", "*EEE", "1", RegistryValueKind.String);
        var nic = new AdapterTweak { Id = "n", Title = "N", AdapterName = "L'Ethernet", KeyPath = "NIC", Values = [("*EEE", "0")] };
        Check("adapter: applied", engine.Apply(nic).Ok && sys.RegGet(RegRoot.HKLM, "NIC", "*EEE") as string == "0");
        var restarts = sys.Commands.Count(c => c.Contains("Restart-NetAdapter -Name 'L''Ethernet'"));
        Check("adapter: restarted once, quote escaped", restarts == 1);
        Check("adapter: restored and restarted", engine.Revert("n").Ok && sys.RegGet(RegRoot.HKLM, "NIC", "*EEE") as string == "1"
            && sys.Commands.Count(c => c.Contains("Restart-NetAdapter")) == 2);

        var net = new NetInfo { Name = "E", ClassKey = "K", Guid = "{G}", Properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*EEE" } };
        var tweaks = Catalog.Network(net, @"C:\x\VALORANT-Win64-Shipping.exe");
        var save = tweaks.OfType<AdapterTweak>().Single();
        Check("network: absent card properties filtered", save.Id == "net_power_save" && save.Values.Count == 1);
        Check("network: no card, no tweaks", Catalog.Network(null, null).Count == 0);
    }

    private static void NetworkJudge()
    {
        var good = new Probe("g", 20, 20, 1, 1, 2);
        var wifi = new NetInfo { WiFi = true };
        var cable = new NetInfo();
        Check("net: healthy", Network.Judge(cable, good, new Probe("i", 20, 20, 6, 5, 7), new Probe("r", 8, 8, 12, 10, 15)).Healthy);
        Check("net: wifi blamed", Network.Judge(wifi, new Probe("g", 20, 19, 12, 2, 80), good, null).Verdict.Contains("Wi-Fi"));
        Check("net: isp blamed", Network.Judge(cable, good, new Probe("i", 20, 18, 9, 5, 40), null).Verdict.Contains("opérateur"));
        Check("net: far server", !Network.Judge(cable, good, good, new Probe("r", 8, 8, 70, 68, 72)).Healthy);
        Check("net: vpn first", Network.Judge(new NetInfo { Vpn = "Mullvad" }, good, good, null).Verdict.Contains("VPN"));
        Check("net: pod to aws region", Network.AwsRegion("aresriot.aws-euw3-prod.eu-gp-paris-1") == "eu-west-3");
        Check("net: pod frankfurt", Network.AwsRegion("aresriot.aws-euc1-prod.eu-gp-frankfurt-1") == "eu-central-1");
        Check("net: unknown pod", Network.AwsRegion("") is null);
    }

    private static void LocalAi()
    {
        var allowed = new HashSet<string> { "game_mode", "mpo_off" };
        var json = """
            {"reply":"Salut","actions":[
              {"tweak_id":"mpo_off","why":"b"},
              {"tweak_id":"mpo_off","why":"dup"},
              {"tweak_id":"disable_defender","why":"x"}],
              "manual":[{"title":"T","how":"H"}],"pages":["perf","hack"],"tester_ping":true}
            """;
        var reply = ShanksitoAi.Parse(json, allowed);
        Check("ai: unknown tweak dropped", reply.Actions.Count == 1 && reply.Actions[0].TweakId == "mpo_off");
        Check("ai: unknown page dropped", reply.Pages.SequenceEqual(["perf"]));
        Check("ai: fields read", reply.Text == "Salut" && reply.TestPing && reply.Manual.Single().How == "H");
        var cut = ShanksitoAi.Parse("{\"reply\":\"Phrase coup", allowed);
        Check("ai: cut answer keeps the text, no actions", cut.Text == "Phrase coup" && cut.Actions.Count == 0);

        Check("ai: partial empty", ShanksitoAi.PartialReply("{\"rep") == "");
        Check("ai: partial escapes", ShanksitoAi.PartialReply("{\"reply\": \"a\\nb \\\"c\\\" \\u00e9") == "a\nb \"c\" é");
        Check("ai: partial stops at end of field", ShanksitoAi.PartialReply("{\"reply\":\"fini\",\"actions\":[]}") == "fini");

        var body = ShanksitoAi.RequestBody("S", [new ChatLine("user", "Q")], allowed);
        Check("ai: output limited to allowed ids", body.Contains("\"enum\":[\"game_mode\",\"mpo_off\"]"));
        Check("ai: thinking off", body.Contains("\"enable_thinking\":false"));
        Check("ai: schema in the form the server applies", body.Contains("\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"name\":\"reponse\",\"strict\":true,\"schema\":{"));

        const string laptop = "Available devices:\n  Vulkan0: Intel(R) UHD Graphics (8052 MiB, 7394 MiB free)\n  Vulkan1: NVIDIA GeForce RTX 3050 Laptop GPU (3962 MiB, 3367 MiB free)\n";
        Check("device: real card over integrated chip", AiServer.PickDevice(laptop) == "Vulkan1");
        Check("device: AMD laptop", AiServer.PickDevice("  Vulkan0: AMD Radeon(TM) Graphics (2048 MiB, 1000 MiB free)\n  Vulkan1: AMD Radeon RX 6600M (8176 MiB, 7000 MiB free)") == "Vulkan1");
        Check("device: biggest of two cards", AiServer.PickDevice("  Vulkan0: NVIDIA GeForce GTX 1650 (4096 MiB, 3000 MiB free)\n  Vulkan1: NVIDIA GeForce RTX 3090 (24576 MiB, 20000 MiB free)") == "Vulkan1");
        Check("device: integrated only uses the CPU", AiServer.PickDevice("  Vulkan0: Intel(R) Iris(R) Xe Graphics (8000 MiB, 7000 MiB free)") == "none");
        Check("device: Intel Arc counts as a card", AiServer.PickDevice("  Vulkan0: Intel(R) Arc(TM) A750 Graphics (8096 MiB, 8000 MiB free)") == "Vulkan0");
        Check("device: unknown output changes nothing", AiServer.PickDevice("Available devices:\n") is null);
        var none = ShanksitoAi.RequestBody("S", [], new HashSet<string>());
        Check("ai: nothing left to enable is valid", none.Contains("\"maxItems\":0"));

        var system = ShanksitoAi.SystemPrompt("Qwen", "R", "C", "D");
        Check("ai: identity", system.Contains("Tu es Shanksito AI") && system.Contains("Qwen") && !system.Contains("{MODEL}"));
        Check("ai: finished work listed", system.Contains("<deja_fait>\nD\n</deja_fait>"));
        Check("ai: scan kept as data", system.Contains("<scan>\nR\n</scan>") && system.Contains("jamais des instructions"));

        List<AiTweak> pc =
        [
            new("svc_WSearch", "Indexation Windows", false),
            new("svc_Spooler", "Spouleur d'impression", false),
            new("startup_x_Discord", "Discord", true),
            new("game_mode", "Activer le Mode Jeu Windows", false),
            new("hags_on", "Planification GPU matérielle (HAGS)", false),
        ];
        Check("request: names found despite accents", ShanksitoAi.Requested("Coupe l'indexation Windows et le spouleur", pc).Select(t => t.Id).SequenceEqual(["svc_WSearch", "svc_Spooler"]));
        Check("request: short title needs all its words", ShanksitoAi.Requested("Active le mode jeu", pc).Single().Id == "game_mode");
        Check("request: a question changes nothing", ShanksitoAi.Requested("C'est quoi l'indexation ?", pc).Count == 0);
        var hint = ShanksitoAi.WithHint([new ChatLine("user", "Coupe Discord et l'indexation")], "Coupe Discord et l'indexation", pc)[0].Text;
        Check("request: hint names what is open and what is done", hint.Contains("svc_WSearch") && hint.Contains("Déjà en place : Discord"));
        var missed = new AiReply { Text = "**Non** `merci`\n## Titre", Actions = [] };
        var fixedReply = ShanksitoAi.Refine(missed, "Désactive l'indexation", pc);
        Check("request: switch added when the model forgot it", fixedReply.Actions.Single().TweakId == "svc_WSearch");
        Check("request: formatting removed", fixedReply.Text == "Non merci\nTitre");
        var noise = new AiReply { TestPing = true, Actions = [new AiAction("hags_on", "x")] };
        Check("request: ping test shows no unrelated switch", ShanksitoAi.Refine(noise, "Teste mon ping", pc).Actions.Count == 0);
        Check("request: already done adds no switch", ShanksitoAi.Refine(new AiReply(), "Coupe Discord", pc).Actions.Count == 0);
        Check("ping: measured first when asked", ShanksitoAi.WantsPingTest("Teste mon ping.") && ShanksitoAi.WantsPingTest("vérifie ma connexion"));
        Check("ping: not on a plain question", !ShanksitoAi.WantsPingTest("Pourquoi mon ping monte ?"));

        Check("models: all pinned", AiCatalog.Models.All(m => m.Commit.Length == 40 && m.Sha256.Length == 64 && m.Bytes > 0) && AiCatalog.RuntimeSha.Length == 64);
        Check("models: unique ids", AiCatalog.Models.Select(m => m.Id).Distinct().Count() == AiCatalog.Models.Length);
        Check("models: runtime pinned to one build", AiCatalog.RuntimeUrl.Contains("/download/" + AiCatalog.RuntimeBuild + "/"));
        Check("models: pick for 16 GB + 4 GB VRAM", AiCatalog.Recommend(16, 4).Id == "qwen35-4b");
        Check("models: pick for 16 GB + 8 GB VRAM", AiCatalog.Recommend(16, 8).Id == "qwen35-9b");
        Check("models: pick for 32 GB + 24 GB VRAM", AiCatalog.Recommend(32, 24).Id == "qwen36-27b");
        Check("models: pick without GPU stays light", AiCatalog.Recommend(16, 0).SizeGb <= 3);
        Check("models: pick for weak PC", AiCatalog.Recommend(4, 0).Id == "qwen35-2b");

        var file = Path.Combine(Path.GetTempPath(), $"shanksito-{Guid.NewGuid():N}.bin");
        File.WriteAllText(file, "abc");
        Check("hash: accepted", Task.Run(() => Downloader.Matches(file, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")).GetAwaiter().GetResult());
        Check("hash: refused", !Task.Run(() => Downloader.Matches(file, new string('0', 64))).GetAwaiter().GetResult());
        File.Delete(file);
    }

    private static void Regressions()
    {
        // Real failure on 2026-10-31: two switches flipped fast raced on backup.json.tmp.
        var file = Path.Combine(Path.GetTempPath(), $"shanksito-{Guid.NewGuid():N}.json");
        var store = new BackupStore(file);
        var sys = new FakeSystem();
        var engine = new TweakEngine(sys, store);
        var tweaks = Enumerable.Range(0, 24).Select(i => new RegistryTweak
        {
            Id = $"r{i}", Title = $"R{i}",
            Edits = [new RegEdit(RegRoot.HKCU, "P", $"v{i}", 1, RegistryValueKind.DWord)],
        }).ToList();
        var results = new OpResult[tweaks.Count];
        var failedThread = false;
        try
        {
            Parallel.For(0, tweaks.Count, i => results[i] = engine.Apply(tweaks[i]));
        }
        catch (AggregateException)
        {
            failedThread = true;
        }
        Check("backup: parallel applies all succeed", !failedThread && results.All(r => r.Ok));
        Check("backup: file complete and readable", new BackupStore(file).Items.Count == tweaks.Count);
        Check("backup: no stray tmp left", Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + ".*.tmp").Length == 0);
        File.Delete(file);
    }

    private static void NewFeatures()
    {
        var (sys, engine) = Fresh();
        var task = new ScheduledTaskTweak { Id = "task", Title = "T", TaskPath = "\\", TaskName = "It's" };
        Check("task: applied", engine.Apply(task).Ok && task.Disabled);
        Check("task: quote escaped", sys.Commands.Any(c => c.Contains("Disable-ScheduledTask -TaskPath '\\' -TaskName 'It''s'")));
        Check("task: undo uses enable", engine.Store.Items["task"].UndoArgs!.Contains("Enable-ScheduledTask"));

        var tasks = Catalog.ParseTasks("""
            [{"TaskPath":"\\","TaskName":"AdobeGCInvoker-1.0","State":"Ready"},
             {"TaskPath":"\\ASUS\\","TaskName":"ArmourySocketServer","State":"Ready"},
             {"TaskPath":"\\","TaskName":"NvDriverUpdateCheckDaily_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}","State":"Ready"},
             {"TaskPath":"\\","TaskName":"BraveSoftwareUpdateTaskUserS-1-5-21-1-2-3-1001Core{1C81D362-0AA9-4C83-9F43-37AAAAAAAAAA}","State":"Disabled"},
             {"TaskPath":"\\","TaskName":"MyOwnBackup","State":"Ready"}]
            """);
        Check("tasks: hardware and drivers hidden", tasks.Count == 3 && tasks.All(t => !t.Title.Contains("Armoury") && !t.Title.Contains("NvDriver")));
        Check("tasks: suggested only when known", tasks.Single(t => t.Title == "MyOwnBackup").Recommended == false && tasks.First().Recommended);
        Check("tasks: readable name, disabled state", tasks.OfType<ScheduledTaskTweak>().Single(t => t.Disabled).Title == "BraveSoftwareUpdateTask");
        Check("tasks: garbage tolerated", Catalog.ParseTasks("oops").Count == 0 && Catalog.ParseTasks("").Count == 0);
        Check("tasks: single object tolerated", Catalog.ParseTasks("""{"TaskPath":"\\","TaskName":"X","State":"Ready"}""").Count == 1);

        var dir = Directory.CreateTempSubdirectory("shanksito-clean").FullName;
        var old = Path.Combine(dir, "old.tmp");
        var young = Path.Combine(dir, "young.tmp");
        File.WriteAllBytes(old, new byte[2048]);
        File.WriteAllBytes(young, new byte[1024]);
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-10));
        var target = new CleanTarget("t", "T", "W", dir, 3, true, false);
        var measured = Cleaner.Measure(target);
        Check("clean: only old files counted", measured is { Files: 1, Bytes: 2048 });
        var (freed, skipped) = Cleaner.Clean(target);
        Check("clean: old gone, recent kept", freed == 2048 && skipped == 0 && !File.Exists(old) && File.Exists(young));
        Check("clean: missing folder tolerated", Cleaner.Measure(target with { Folder = dir + "-none" }).Files == 0);
        Directory.Delete(dir, true);
        Check("clean: targets stay in local data", Cleaner.Targets().All(t => t.Folder.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)));

        Check("bloat: grades", NetLab.Grade(2) == "A+" && NetLab.Grade(20) == "A" && NetLab.Grade(50) == "B" && NetLab.Grade(150) == "C" && NetLab.Grade(900) == "F");
        Check("bloat: bad grade gives a fix", NetLab.BloatVerdict("C", 100, 20).Contains("90"));
        var hops = new List<Hop> { new(1, "a", 1), new(2, "", -1), new(3, "b", 3), new(4, "c", 19) };
        Check("trace: biggest jump skips silent hops", NetLab.BiggestJump(hops)?.Number == 4);

        var gpu = new List<MatchSample>
        {
            new() { Temp = 50, Util = 5 },
            new() { Temp = 80, Util = 90, Clock = 1600, Power = 60 },
            new() { Temp = 88, Util = 90, Clock = 1500, Power = 60, Throttle = 0x20 },
        };
        var report = GameWatcher.Summarize(DateTime.Now.AddMinutes(-3), gpu, [8, 8, 8, 8, 60, -1]);
        Check("match: menus ignored", report.AvgTemp == 84 && report.AvgUtil == 90);
        Check("match: heat blamed", report.Verdict.Contains("Chaleur") && report.ThermalPct == 50);
        Check("match: ping stats", report.Spikes == 1 && report.PingMax == 60 && Math.Abs(report.LossPct - 100.0 / 6) < 0.01);
        Check("match: network blamed", GameWatcher.Judge(new MatchReport { Spikes = 9, AvgUtil = 85 }).Contains("Réseau"));
        Check("match: empty is safe", GameWatcher.Summarize(DateTime.Now, [], []).Spikes == 0);

        var lines = new[] { "2026-10-31 11:36:45 APPLIQUE game_mode", "2026-10-31 12:04:01 ECHEC net: x y", "junk" };
        var entries = Journal.Parse(lines);
        Check("journal: parsed", entries.Count == 2 && entries[0].Detail == "game_mode" && entries[1].Action == "ECHEC");
        var sessions = new List<PerfSession>
        {
            new() { Start = new DateTime(2026, 10, 30, 22, 0, 0) },
            new() { Start = new DateTime(2026, 10, 31, 9, 0, 0) },
            new() { Start = new DateTime(2026, 10, 31, 13, 0, 0) },
        };
        var marks = Journal.ChangedSessions(sessions, entries);
        Check("journal: only the session after a change is marked", marks.SetEquals([2]));

        Check("launcher: script keeps quotes", GameLauncher.LaunchScript(@"C:\Riot Games\Riot Client\RiotClientServices.exe").Contains("\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --launch-product=valorant"));

        var health = Health.Parse("""{"Boot":"2026-10-31T12:38:43","Sticks":2,"Channels":2,"Speed":3200,"Bios":"313","DiskHealth":"Healthy","DiskTemp":41,"DiskWear":6,"DiskHours":900,"Full":64963,"Design":90000}""");
        Check("health: parsed", health is { RamChannels: 2, DiskTemp: 41, BiosVersion: "313" } && Math.Abs(health.BatteryWearPct - 27.8) < 0.1);
        Check("health: unelevated nulls tolerated", Health.Parse("""{"Sticks":2,"DiskTemp":null,"Full":100,"Design":null}""") is { DiskTemp: -1, BatteryWearPct: -1 });
        Check("health: garbage tolerated", Health.Parse("nope").RamSticks == 0);

        var data = new ScanData { Valo = new ValoConfig { LimitAlways = true, MaxAlways = 200 }, DiskFreePct = 50 };
        var mild = Diagnostics.BuildFindings(data, sys);
        Check("score: high cap is not critical", mild.All(f => f.Severity != Severity.Critique));
        data.Valo.MaxAlways = 144;
        data.Findings = Diagnostics.BuildFindings(data, sys);
        Check("score: low cap is critical and costs points", data.Findings.Any(f => f.Severity == Severity.Critique) && data.Score == 75);
        data.Findings = Enumerable.Range(0, 30).Select(_ => new Finding(Severity.Critique, "x", "y", 20)).ToList();
        Check("score: never negative", data.Score == 0);

        var fresh = new ScanData { DiskFreePct = 50, OldDriverErrors = 186 };
        var afterUpdate = Diagnostics.BuildFindings(fresh, sys);
        Check("driver: old errors are not critical", afterUpdate.All(f => f.Severity != Severity.Critique && f.Penalty == 0) && afterUpdate.Any(f => f.Title.Contains("nouveau driver")));
        fresh.DriverErrors = 3;
        Check("driver: new errors stay critical", Diagnostics.BuildFindings(fresh, sys).Any(f => f.Severity == Severity.Critique));
        var hot = new ScanData { DiskFreePct = 50, Gpu = new GpuSample { Clock = 1500, Util = 0, Temp = 60 } };
        Check("gpu: stuck clocks flagged", Diagnostics.BuildFindings(hot, sys).Any(f => f.Title.Contains("1500 MHz")));
    }

    private static void OtherMachines()
    {
        var sys = new FakeSystem();
        var desktop = new ScanData { DiskFreePct = 50, Gpu = new GpuSample { Temp = 75 } };
        Check("other pc: desktop gets no laptop advice", Diagnostics.BuildFindings(desktop, sys).Single(f => f.Title.Contains("repos")).Detail.Contains("boîtier"));
        var acer = new ScanData { DiskFreePct = 50, IsLaptop = true, Gpu = new GpuSample { Temp = 75 } };
        Check("other pc: no ASUS advice elsewhere", !Diagnostics.BuildFindings(acer, sys).Any(f => f.Detail.Contains("Armoury")));
        var rog = new ScanData { DiskFreePct = 50, IsLaptop = true, IsAsus = true, Gpu = new GpuSample { Temp = 75 } };
        Check("other pc: ASUS advice on ASUS", Diagnostics.BuildFindings(rog, sys).Any(f => f.Detail.Contains("Armoury")));

        var amd = new ScanData { DiskFreePct = 50, GpuName = "AMD Radeon RX 6600" };
        Check("other pc: AMD is not NVIDIA", !amd.HasNvidia);
        Check("other pc: missing game explained", Diagnostics.BuildFindings(amd, sys).Any(f => f.Title == "Valorant introuvable"));
        sys.RegSet(RegRoot.HKLM, @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", "DriverDesc", "AMD Radeon(TM) Graphics", RegistryValueKind.String);
        sys.RegSet(RegRoot.HKLM, @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0001", "DriverDesc", "AMD Radeon RX 6600", RegistryValueKind.String);
        Check("other pc: dedicated AMD preferred", Gpu.Name(sys) == "AMD Radeon RX 6600");

        Check("other pc: vendor tasks hidden", Catalog.ParseTasks("""[{"TaskPath":"\\Lenovo\\","TaskName":"Vantage","State":"Ready"},{"TaskPath":"\\","TaskName":"Intel Update","State":"Ready"}]""").Count == 0);

        Check("setup: detected from file name", Installer.IsSetup(@"C:\Dl\ShanksitoTweaker-Setup (1).exe") && !Installer.IsSetup(@"C:\Program Files\Shanksito Tweaker\ShanksitoTweaker.exe"));
        Check("setup: notice names no affiliation", Installer.Notice.Contains("non affilié"));
    }

    /// <summary>Copies this exe into a scratch folder and removes it again. Registry entries stay in memory.</summary>
    public static string InstallRoundTrip(string dir)
    {
        Log.Clear();
        var sys = new FakeSystem { Real = new RealSystem() };
        var options = new InstallOptions(Path.Combine(dir, "Program Files", "Shanksito Tweaker"), Path.Combine(dir, "Menu", "Shanksito Tweaker"), Path.Combine(dir, "Bureau"));
        Directory.CreateDirectory(options.DesktopFolder!);
        var source = Path.Combine(dir, "downloaded.exe");
        File.Copy(Environment.ProcessPath!, source, true);
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3");
        Check("install: test file carries the download mark", File.Exists(source + ":Zone.Identifier"));
        var target = Installer.Install(sys, source, options);
        Check("install: download mark not kept", !File.Exists(target + ":Zone.Identifier"));
        Check("install: exe copied whole", new FileInfo(target).Length == new FileInfo(source).Length);
        Check("install: start menu shortcut", File.Exists(Path.Combine(options.StartMenuFolder, "Shanksito Tweaker.lnk")));
        Check("install: desktop shortcut", File.Exists(Path.Combine(options.DesktopFolder!, "Shanksito Tweaker.lnk")));

        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ShanksitoTweaker";
        Check("install: uninstall entry", sys.RegGet(RegRoot.HKLM, key, "DisplayName") as string == "Shanksito Tweaker");
        Check("install: uninstall command", (sys.RegGet(RegRoot.HKLM, key, "UninstallString") as string ?? "").EndsWith("\" --uninstall"));

        var folder = Installer.Unregister(sys);
        Check("uninstall: shortcuts gone", !File.Exists(Path.Combine(options.DesktopFolder!, "Shanksito Tweaker.lnk")) && !Directory.Exists(options.StartMenuFolder));
        Check("uninstall: entry gone", sys.RegGet(RegRoot.HKLM, key, "DisplayName") is null);
        Check("uninstall: folder returned", folder == options.Folder);
        File.WriteAllText(Path.Combine(folder!, "keep-me.txt"), "x");
        Installer.ScheduleRemoval(folder!, false);
        return Log.ToString();
    }

    private static void Migration()
    {
        var root = Directory.CreateTempSubdirectory("shanksito-migrate").FullName;
        var (from, to) = (Path.Combine(root, "old"), Path.Combine(root, "new"));
        Directory.CreateDirectory(Path.Combine(from, "matches"));
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "backup.json"), "real backup");
        File.WriteAllText(Path.Combine(from, "matches", "m.json"), "match");
        File.WriteAllText(Path.Combine(to, "backup.json"), "empty first run");
        BackupStore.MoveData(from, to);
        Check("migrate: old backup wins over a first run", File.ReadAllText(Path.Combine(to, "backup.json")) == "real backup");
        Check("migrate: subfolders follow", File.Exists(Path.Combine(to, "matches", "m.json")));
        Check("migrate: old folder removed", !Directory.Exists(from));
        BackupStore.MoveData(from, to);
        Check("migrate: nothing to do is safe", File.Exists(Path.Combine(to, "backup.json")));
        Directory.Delete(root, true);
    }

    private static void CatalogSafety()
    {
        var all = Catalog.Windows(@"C:\game.exe").OfType<RegistryTweak>().SelectMany(t => t.Edits).ToList();
        Check("catalog: nothing under DeviceGuard", all.All(e => !e.Path.Contains("DeviceGuard")));
        Check("catalog: nothing under Defender", all.All(e => !e.Path.Contains("Defender", StringComparison.OrdinalIgnoreCase)));
        Check("catalog: nothing under Policies", all.All(e => !e.Path.Contains("Policies")));

        var sys = new FakeSystem();
        var missing = Catalog.Services(sys);
        Check("catalog: absent services hidden", missing.Count == 0);
    }

    private static void Parsers()
    {
        var sample = """
            [2026.10.31-09.31.01:650][706]LogClientPerRoundTelemetryComponent: End of Round Perf statistics
            	Round 19 Performance:
            		Avg.Frametime (ms): 8.00
            		Total Frames: 1000
            		Game Thread Frames Under 1 fps: 0
            		Game Thread Frames Over 60 fps: 100
            		Game Thread Frames Over 120 fps: 200
            		Game Thread Frames Over 144 fps: 600
            		Game Thread Frames Over 240 fps: 100
            [2026.10.31-09.31.01:650][706]LogClientPerRoundTelemetryComponent:
            		Avg. Work (ms): 6.50
            [2026.10.31-09.31.01:650][706]LogClientPerRoundTelemetryComponent:
            		Total Time(secs): 60.00
            		System CPU Util(mean): 20.00
            		GPU Time(mean): 5.00
            		System Memory Util(mean): 80.00
            		RHI: D3D11
            [2026.10.31-09.32.01:650][706]LogClientPerRoundTelemetryComponent: End of Round Perf statistics
            		Avg.Frametime (ms): 1.00
            		Total Frames: 50
            		Total Time(secs): 2.00
            		RHI: D3D11
            """.Replace("            ", "");
        var s = PerfLogs.Parse(new StringReader(sample), "x");
        Check("logs: session found", s is not null);
        Check("logs: short round ignored", s?.Rounds == 1 && s.Frames == 1000);
        Check("logs: fps", Math.Abs((s?.AvgFps ?? 0) - 125) < 0.01);
        Check("logs: histogram", s?.PctUnder120 == 10 && s.PctUnder144 == 30 && s.PctOver240 == 10);
        Check("logs: gpu and ram", s?.GpuMs == 5 && s.MemoryPct == 80 && s.WorkMs == 6.5);
        Check("logs: empty is null", PerfLogs.Parse(new StringReader("nothing"), "x") is null);

        var gpu = Gpu.ParseSample("77, 3, 210, 405, 8.15, 12, 4096, P8, 0x0000000000000060");
        Check("gpu: csv", gpu is { Temp: 77, Clock: 210, PState: "P8" } && gpu.ThrottleText.Contains("thermique"));
        Check("gpu: n/a tolerated", Gpu.ParseSample("77, 3, 210, 405, [N/A], 12, 4096, P8, [N/A]") is { Power: 0, Throttle: 0 });
        Check("gpu: nvidia version", Gpu.ToNvidiaVersion("32.0.15.7283") == "572.83");

        var dir = Directory.CreateTempSubdirectory("shanksito").FullName;
        var id = "11111111-2222-3333-4444-555555555555";
        Directory.CreateDirectory(Path.Combine(dir, "WindowsClient"));
        Directory.CreateDirectory(Path.Combine(dir, id + "-eu", "Windows"));
        Directory.CreateDirectory(Path.Combine(dir, id + "-eu", "WindowsClient"));
        File.WriteAllText(Path.Combine(dir, "WindowsClient", "RiotLocalMachine.ini"), $"[UserInfo]\nLastKnownUser={id}\n");
        File.WriteAllText(Path.Combine(dir, id + "-eu", "Windows", "RiotUserSettings.ini"),
            "EAresFloatSettingName::MaxFramerateAlways=144\nEAresBoolSettingName::LimitFramerateAlways=True\n");
        File.WriteAllText(Path.Combine(dir, id + "-eu", "WindowsClient", "GameUserSettings.ini"),
            "bUseVSync=False\nPreferredFullscreenMode=0\nResolutionSizeX=1920\nResolutionSizeY=1080\nDefaultMonitorDeviceID=\"MONITOR\\\\AOC2702\\\\{x}\\\\0002\"\n");
        var config = ValorantFiles.Load(dir);
        Check("ini: cap", config is { Found: true, LimitAlways: true, MaxAlways: 144 });
        Check("ini: game settings", config is { VSync: false, FullscreenMode: 0, Resolution: "1920x1080", Monitor: "AOC2702" });
        Directory.Delete(dir, true);
    }

    public static int Run(string outputFile)
    {
        Registry();
        Rollback();
        Power();
        Services();
        Startup();
        NewTweakTypes();
        NetworkJudge();
        LocalAi();
        Regressions();
        NewFeatures();
        OtherMachines();
        Migration();
        CatalogSafety();
        Parsers();
        Log.AppendLine($"{(_failures == 0 ? "ALL PASSED" : _failures + " FAILED")}");
        File.WriteAllText(outputFile, Log.ToString());
        return _failures == 0 ? 0 : 1;
    }
}
