using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ShanksitoTweaker.Core;

public static class Catalog
{
    private const string Gfx = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string Dwm = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string GpuPrefs = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private static RegEdit Dword(RegRoot root, string path, string name, int value) =>
        new(root, path, name, value, RegistryValueKind.DWord);

    public static List<Tweak> Windows(string? valorantExe)
    {
        var list = new List<Tweak>
        {
            new PowerPlanTweak
            {
                Id = "power_high", Group = "Performance", Title = "Plan Performances élevées",
                Why = "Garde le CPU et le lien PCIe du GPU réveillés, là où « Utilisation normale » les laisse s'endormir. Sur portable ça chauffe un peu plus : compare avec l'onglet Perf.",
                Gain = "+3 à 10 % en combat", Recommended = true,
            },
            new RegistryTweak
            {
                Id = "game_mode", Group = "Performance", Title = "Activer le Mode Jeu Windows",
                Why = "Donne la priorité au jeu face aux applis en fond. Utile surtout quand beaucoup de choses tournent derrière.",
                Gain = "Moins de chutes de FPS", Recommended = true,
                Edits = [Dword(RegRoot.HKCU, GameBar, "AutoGameModeEnabled", 1), Dword(RegRoot.HKCU, GameBar, "AllowAutoGameMode", 1)],
            },
            new RegistryTweak
            {
                Id = "game_dvr_off", Group = "Performance", Title = "Couper l'enregistrement Xbox (Game DVR)",
                Why = "L'enregistrement en fond consomme du GPU pendant la partie.",
                Gain = "+1 à 3 %", Recommended = true,
                Edits =
                [
                    Dword(RegRoot.HKCU, @"System\GameConfigStore", "GameDVR_Enabled", 0),
                    Dword(RegRoot.HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
                ],
            },
            new RegistryTweak
            {
                Id = "transparency_off", Group = "Performance", Title = "Couper la transparence Windows",
                Why = "Un peu de GPU et de RAM en moins pour le bureau, surtout avec deux écrans.",
                Gain = "Minime", Recommended = true,
                Edits = [Dword(RegRoot.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)],
            },
            new RegistryTweak
            {
                Id = "mpo_off", Group = "Stabilité GPU", Title = "Couper les Multi-Plane Overlays",
                Why = "Correctif documenté par NVIDIA contre les écrans noirs et scintillements sur plusieurs écrans. Utile si le Diagnostic signale des erreurs driver.",
                Gain = "Stabilité, pas de FPS", Reboot = true, Recommended = true, Risk = Risk.Moyen,
                Edits = [Dword(RegRoot.HKLM, Dwm, "OverlayTestMode", 5)],
            },
            new RegistryTweak
            {
                Id = "hags_on", Group = "Stabilité GPU", Title = "Planification GPU matérielle (HAGS)",
                Why = "Résultat variable selon le driver. À tester seul, puis à comparer dans Perf. Si des plantages apparaissent, à recouper.",
                Gain = "-2 à +3 %", Reboot = true, Risk = Risk.Moyen,
                Edits = [Dword(RegRoot.HKLM, Gfx, "HwSchMode", 2)],
            },
            new RegistryTweak
            {
                Id = "foreground_boost", Group = "Performance", Title = "Quantum CPU favorable au premier plan",
                Why = "Donne des tranches de temps CPU plus longues à la fenêtre active. Tweak très populaire, effet réel faible : à ne pas surestimer.",
                Gain = "0 à 1 %", Risk = Risk.Moyen,
                Edits = [Dword(RegRoot.HKLM, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26)],
            },
        };
        list.AddRange(
        [
            new PowerValueTweak
            {
                Id = "usb_suspend_off", Group = "Périphériques", Title = "Couper la mise en veille USB (secteur)",
                Why = "Évite les micro-coupures de souris et de casque USB. S'applique au plan d'alimentation actif : à activer après avoir choisi le plan.",
                Gain = "Latence souris plus stable", Recommended = true,
                Sub = "2a737441-1930-4402-8d77-b2bebba308a3", Setting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", Target = 0,
            },
            new PowerValueTweak
            {
                Id = "pcie_aspm_off", Group = "Performance", Title = "Couper l'économie du lien PCIe (secteur)",
                Why = "Le lien vers le GPU ne s'endort plus. Déjà inclus dans le plan Performances élevées : utile seulement si tu gardes un autre plan.",
                Gain = "Moins de micro-saccades", Risk = Risk.Moyen,
                Sub = "SUB_PCIEXPRESS", Setting = "ASPM", Target = 0,
            },
            new RegistryTweak
            {
                Id = "mouse_accel_off", Group = "Périphériques", Title = "Couper l'accélération souris Windows",
                Why = "Valorant utilise l'entrée brute, donc rien ne change en jeu. Rend seulement le bureau plus constant. Actif après reconnexion.",
                Gain = "Confort bureau", Reboot = true, Risk = Risk.Moyen,
                Edits =
                [
                    new RegEdit(RegRoot.HKCU, @"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String),
                    new RegEdit(RegRoot.HKCU, @"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String),
                    new RegEdit(RegRoot.HKCU, @"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String),
                ],
            },
            new RegistryTweak
            {
                Id = "fast_startup_off", Group = "Stabilité GPU", Title = "Couper le démarrage rapide",
                Why = "Un arrêt remet alors vraiment les pilotes à zéro. Le PC démarre un peu moins vite.",
                Gain = "Stabilité", Reboot = true, Risk = Risk.Moyen,
                Edits = [Dword(RegRoot.HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0)],
            },
        ]);
        if (valorantExe is not null)
        {
            list.Add(new RegistryTweak
            {
                Id = "fso_off_valorant", Group = "Performance", Title = "Couper les optimisations plein écran de Valorant",
                Why = "Résultat variable : parfois moins de latence, parfois rien. Aucun fichier du jeu n'est modifié. À comparer dans Perf.",
                Gain = "-1 à +2 %", Risk = Risk.Moyen,
                Edits = [new RegEdit(RegRoot.HKCU, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", valorantExe, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE", RegistryValueKind.String)],
            });
            list.Insert(1, new RegistryTweak
            {
                Id = "gpu_pref_valorant", Group = "Performance", Title = "Forcer le GPU dédié pour Valorant",
                Why = "Empêche Windows de lancer le jeu sur le GPU Intel, par exemple après une mise à jour.",
                Gain = "Assurance", Recommended = true,
                Edits = [new RegEdit(RegRoot.HKCU, GpuPrefs, valorantExe, "GpuPreference=2;", RegistryValueKind.String)],
            });
        }
        return list;
    }

    private static readonly (string Name, string Label, string Why, bool Suggest)[] ServiceCandidates =
    [
        ("HTTPDebuggerPro", "HTTP Debugger Pro", "Intercepte le trafic web en permanence. Inutile en jeu.", true),
        ("nlsvc", "NetLimiter", "Filtre tout le réseau via un pilote. À relancer seulement quand tu t'en sers.", true),
        ("Red Giant Service", "Red Giant", "Service de licence de plugins vidéo.", true),
        ("AdobeUpdateService", "Adobe Update", "Mises à jour Adobe, inutile en continu.", true),
        ("AGMService", "Adobe Genuine Monitor", "Vérification de licence Adobe.", true),
        ("Apple Mobile Device Service", "Apple Mobile Device", "Démarre à la demande quand un iPhone est branché.", true),
        ("CortexLauncherService", "Razer Cortex", "Lanceur et « booster » Razer, redondant avec cette app.", true),
        ("Razer Game Manager Service 3", "Razer Game Manager", "Détection de jeux Razer.", true),
        ("LGHUBUpdaterService", "Logitech G HUB Updater", "Mises à jour G HUB, pas le pilote de souris.", true),
        ("McAfee WebAdvisor", "McAfee WebAdvisor", "Extension préinstallée, Defender reste ta protection.", true),
        ("SysMain", "SysMain (Superfetch)", "Pré-chargement inutile sur SSD NVMe.", true),
        ("DiagTrack", "Télémétrie Windows", "Envoi de données de diagnostic en fond.", true),
        ("AnyDesk", "AnyDesk", "Accès distant. À garder en auto si tu t'en sers pour joindre ce PC à distance.", false),
        ("MullvadVPN", "Mullvad VPN", "À garder en auto si tu l'utilises tous les jours.", false),
        ("RzActionSvc", "Razer Central", "Requis par certains périphériques Razer.", false),
        ("LightingService", "ASUS Aura", "Éclairage RGB. Coupe-le si tu ne changes jamais les couleurs.", false),
        ("WSearch", "Indexation Windows", "Baisse l'activité disque, mais la recherche de fichiers devient lente.", false),
        ("Spooler", "Spouleur d'impression", "Inutile sans imprimante. Revient à la demande.", false),
    ];

    private static readonly Regex TaskSuggest = new("adobe|brave|platformexperience|googleuserpeh|megasync|firefox default|razercortex", RegexOptions.IgnoreCase);

    // Hardware control, audio and shell helpers break visibly when disabled, so they are never listed.
    private static readonly Regex TaskHidden = new(
        "asus|armoury|rtkaud|realtek|createexplorershell|riot|vanguard|nvidia|nvdriver|nvprofile|nvnode|nvbattery|nvtm|intel|amd|radeon|msi|dragon ?center|lenovo|legion|vantage|omen|\\bhp\\b|dell|alienware|acer|predator|logi|corsair|icue",
        RegexOptions.IgnoreCase);

    public static List<Tweak> Tasks(ISystemApi s)
    {
        const string query = "Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' } | Select-Object TaskPath,TaskName,@{n='State';e={[string]$_.State}} | ConvertTo-Json -Compress";
        return ParseTasks(s.Run("powershell", CommandTweak.Ps(query)).Output);
    }

    public static List<Tweak> ParseTasks(string json)
    {
        var list = new List<Tweak>();
        if (json.Trim().Length == 0) return list;
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return list;
        }
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        foreach (var item in items)
        {
            var path = item.GetProperty("TaskPath").GetString() ?? "";
            var name = item.GetProperty("TaskName").GetString() ?? "";
            if (TaskHidden.IsMatch(path + name)) continue;
            list.Add(new ScheduledTaskTweak
            {
                Id = $"task_{path}{name}", Group = "Tâches", Title = ShortTaskName(name),
                Why = "Se lance toute seule en arrière-plan. Couper ne désinstalle rien.",
                Gain = "Moins d'activité en fond", TaskPath = path, TaskName = name,
                Recommended = TaskSuggest.IsMatch(path + name),
                Disabled = item.GetProperty("State").GetString() == "Disabled",
            });
        }
        return list;
    }

    /// <summary>Drops the SID and GUID suffixes that make task names unreadable.</summary>
    private static string ShortTaskName(string name) =>
        Regex.Replace(name, @"(User)?S-1-5-[\d-]+.*$|[_ ]?\{[0-9A-Fa-f-]+\}.*$", "").Trim();

    public static List<Tweak> Network(NetInfo? net, string? valorantExe)
    {
        var list = new List<Tweak>();
        if (net is null) return list;

        AdapterTweak? Adapter(string id, string title, string why, string gain, bool recommended, Risk risk, params (string Keyword, string Value)[] wanted)
        {
            var values = wanted.Where(v => net.Properties.Contains(v.Keyword)).ToList();
            if (net.ClassKey.Length == 0 || values.Count == 0) return null;
            return new AdapterTweak
            {
                Id = id, Group = "Carte réseau", Title = title, Why = why + " Coupe le réseau 5 s : pas pendant une partie.",
                Gain = gain, Recommended = recommended, Risk = risk,
                AdapterName = net.Name, KeyPath = net.ClassKey, Values = values,
            };
        }

        AdapterTweak?[] adapter =
        [
            Adapter("net_power_save", "Couper les économies d'énergie réseau",
                "La carte peut somnoler ou réduire sa vitesse entre deux paquets (EEE, Ethernet vert, mode économie).",
                "Latence plus stable", true, Risk.Sur,
                ("*EEE", "0"), ("EnableGreenEthernet", "0"), ("PowerSavingMode", "0"), ("GigaLite", "0")),
            Adapter("net_interrupt_mod", "Couper la modération d'interruptions",
                "La carte traite chaque paquet tout de suite au lieu de les grouper. Un peu plus de CPU.",
                "Moins de 1 ms", false, Risk.Moyen, ("*InterruptModeration", "0")),
            Adapter("net_flow_control", "Couper le contrôle de flux",
                "La carte n'obéit plus aux demandes de pause de la box. Effet souvent nul.",
                "0 à 1 ms", false, Risk.Moyen, ("*FlowControl", "0")),
        ];
        list.AddRange(adapter.OfType<AdapterTweak>());

        if (valorantExe is not null)
        {
            var exe = Path.GetFileName(valorantExe);
            list.Add(new CommandTweak
            {
                Id = "net_qos_valorant", Group = "Priorité", Title = "Marquer le trafic Valorant comme prioritaire",
                Why = "Pose une étiquette DSCP sur les paquets du jeu. Ça n'aide que si ta box applique la QoS, et les opérateurs l'ignorent au-delà.",
                Gain = "Seulement si la box gère la QoS", Risk = Risk.Moyen,
                Detect = "if (Get-NetQosPolicy -Name FpsForgeValorant -ErrorAction SilentlyContinue) { 'YES' } else { 'NO' }",
                Enable = $"New-NetQosPolicy -Name FpsForgeValorant -AppPathNameMatchCondition '{exe}' -DSCPAction 46 -NetworkProfile All -ErrorAction Stop | Out-Null",
                Disable = "Remove-NetQosPolicy -Name FpsForgeValorant -Confirm:$false",
            });
        }

        if (net.Guid.Length > 0)
        {
            var path = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{net.Guid}";
            list.Add(new RegistryTweak
            {
                Id = "net_nagle_off", Group = "Priorité", Title = "Couper l'algorithme de Nagle",
                Why = "Tweak célèbre, mais il ne touche que le TCP. Valorant joue en UDP : aucun effet sur le ping en partie, seulement les menus.",
                Gain = "0 en partie", Reboot = true, Risk = Risk.Moyen,
                Edits = [Dword(RegRoot.HKLM, path, "TcpAckFrequency", 1), Dword(RegRoot.HKLM, path, "TCPNoDelay", 1)],
            });
        }
        return list;
    }

    public static List<Tweak> Services(ISystemApi s)
    {
        var list = new List<Tweak>();
        foreach (var (name, label, why, suggest) in ServiceCandidates)
        {
            var tweak = new ServiceTweak(name)
            {
                Id = "svc_" + name, Group = "Services", Title = label, Why = why,
                Gain = "RAM et CPU en fond", Recommended = suggest,
            };
            if (tweak.Exists(s)) list.Add(tweak);
        }
        return list;
    }

    private static readonly Regex SuggestDisable = new(
        "overwolf|medal|razer|cortex|epic|eadm|steam|lunar|roblox|docker|voicemod|voice\\.ai|discord|lghub|steelseries|megasync|brave|onenote|netlimiter|adobe|ccxprocess",
        RegexOptions.IgnoreCase);

    private static readonly Regex NeverDisable = new("securityhealth|vanguard|vgtray", RegexOptions.IgnoreCase);

    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKey32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public static List<Tweak> Startup(ISystemApi s)
    {
        var list = new List<Tweak>();

        void AddRun(RegRoot root, string runPath, string approvedSub)
        {
            foreach (var name in s.RegNames(root, runPath))
            {
                var command = Convert.ToString(s.RegGet(root, runPath, name)) ?? "";
                list.Add(StartupEntry(root, approvedSub, name, name, command));
            }
        }

        AddRun(RegRoot.HKCU, RunKey, "Run");
        AddRun(RegRoot.HKLM, RunKey, "Run");
        AddRun(RegRoot.HKLM, RunKey32, "Run32");
        AddFolder(list, RegRoot.HKCU, Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        AddFolder(list, RegRoot.HKLM, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        return list.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddFolder(List<Tweak> list, RegRoot root, string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(StartupEntry(root, "StartupFolder", name, Path.GetFileNameWithoutExtension(file), file));
        }
    }

    private static StartupTweak StartupEntry(RegRoot root, string approvedSub, string valueName, string title, string command)
    {
        var locked = NeverDisable.IsMatch(valueName) || NeverDisable.IsMatch(command);
        return new StartupTweak
        {
            Id = $"startup_{root}_{approvedSub}_{valueName}",
            Group = "Démarrage",
            Title = title,
            Why = locked ? "Verrouillé : sécurité Windows ou anti-triche." : command,
            ApprovedRoot = root,
            ApprovedPath = $@"{Approved}\{approvedSub}",
            ValueName = valueName,
            Command = command,
            Locked = locked,
            Recommended = !locked && (SuggestDisable.IsMatch(valueName) || SuggestDisable.IsMatch(command)),
        };
    }
}
