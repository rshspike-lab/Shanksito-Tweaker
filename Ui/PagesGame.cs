using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ShanksitoTweaker.Core;
using ShanksitoTweaker.Ui;
using static ShanksitoTweaker.Ui.W;

namespace ShanksitoTweaker;

public sealed partial class Shell
{
    private UIElement SessionPage()
    {
        var d = _state.Data;
        var page = W.Page("Session de jeu", "Vérifie que tout est prêt, ferme ce qui gêne, lance Valorant. GPU et ping sont suivis tout seuls pendant la partie tant que Shanksito Tweaker reste ouvert.");

        page.Children.Add(Heading("Prêt à jouer ?"));
        var checks = new UniformGrid { Columns = 2 };
        void Check(string label, bool ok, string detail)
        {
            var row = new DockPanel();
            var mark = Icon(ok ? "" : "", 15, ok ? "Good" : "Warn").With(i => i.Margin = new Thickness(0, 0, 10, 0));
            row.Children.Add(mark);
            row.Children.Add(Line(label, detail));
            checks.Children.Add(Card(row).With(c => c.Margin = new Thickness(0, 0, 8, 8)));
        }

        var (ram, _) = Memory.Status();
        var screen = d.GameDisplay;
        Check("Sur secteur", !d.OnBattery, d.OnBattery ? "Branche le chargeur, le GPU est bridé" : "Puissance complète");
        Check("Écran au maximum", screen is null || screen.Current >= screen.Max, screen is null ? "Écran non détecté" : $"{screen.Current} Hz sur {screen.Max} Hz");
        Check("Pas de VPN", d.Net?.Vpn is null, d.Net?.Vpn ?? "Trajet direct");
        Check("Connexion filaire", d.Net is { WiFi: false }, d.Net is null ? "Aucune connexion" : d.Net.WiFi ? "Wi-Fi : préfère le câble" : "Ethernet");
        Check("RAM disponible", ram < 70, $"{ram} % utilisés");
        Check("GPU reposé", d.Gpu is null || d.Gpu.Temp < 80, d.Gpu is null ? "Mesure indisponible" : $"{d.Gpu.Temp:0} °C");
        page.Children.Add(checks);

        var apps = GameSession.Scan();
        var boxes = new List<(CheckBox Box, RunningApp App)>();
        var list = new StackPanel();
        foreach (var app in apps)
        {
            var box = new CheckBox { Style = (Style)Application.Current.Resources["Switch"], IsChecked = app.Default };
            boxes.Add((box, app));
            DockPanel.SetDock(box, Dock.Right);
            var row = new DockPanel();
            row.Children.Add(box);
            row.Children.Add(Line(app.Label, app.GracefulOnly ? $"{app.Megabytes:0} Mo · fermeture douce, sauvegarde ton travail avant" : $"{app.Megabytes:0} Mo"));
            list.Children.Add(Card(row));
        }

        async Task CloseChosen()
        {
            var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.App).ToList();
            if (chosen.Count == 0) return;
            var freed = chosen.Sum(a => a.Megabytes);
            SetStatus("Fermeture des applis…");
            var results = await Task.Run(() => chosen.Select(GameSession.Close).ToList());
            _state.ClosedApps.AddRange(results.Select(r => r.Path).OfType<string>());
            var left = results.Sum(r => r.Left);
            SetStatus($"Environ {freed:0} Mo libérés{(left > 0 ? $", {left} processus encore ouverts" : "")}.");
        }

        var client = GameLauncher.RiotClient();
        var launch = Btn("Préparer et lancer Valorant", async () =>
        {
            await CloseChosen();
            GameLauncher.LaunchValorant(client!);
            SetStatus("Valorant se lance. Bon jeu.");
            Show("session");
        }, true);
        launch.IsEnabled = client is not null;
        if (client is null) page.Children.Add(Text("Riot Client introuvable : lance Valorant toi-même, la surveillance s'active quand même.", 12.5, "Warn").With(t => t.Margin = new Thickness(0, 0, 0, 8)));
        var reopen = Btn($"Rouvrir mes applis ({_state.ClosedApps.Distinct().Count()})", () =>
        {
            foreach (var path in _state.ClosedApps.Distinct()) GameLauncher.OpenUnelevated(path);
            _state.ClosedApps.Clear();
            Show("session");
        });
        reopen.IsEnabled = _state.ClosedApps.Count > 0;
        page.Children.Add(Toolbar(launch, Btn("Fermer seulement", async () =>
        {
            await CloseChosen();
            Show("session");
        }), reopen, Btn("Actualiser", () => Show("session"))));

        page.Children.Add(Heading("Applis à fermer"));
        page.Children.Add(Text("Riot, Vanguard et Windows ne sont jamais touchés.", 12.5, "Muted").With(t => t.Margin = new Thickness(0, -4, 0, 8)));
        if (apps.Count == 0) page.Children.Add(Text("Aucune appli connue en fond.", 13, "Muted"));
        page.Children.Add(list);

        if (d.Matches.Count > 0) page.Children.Add(Heading("Dernières parties"));
        foreach (var match in d.Matches.Take(5)) page.Children.Add(MatchCard(match));
        return page;
    }

    private static Border MatchCard(MatchReport m)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text($"{m.Start:dd/MM à HH:mm} · {m.Minutes:0} min", 14, "Text", true));
        stack.Children.Add(Text($"GPU {m.AvgTemp:0} °C (max {m.MaxTemp:0}) · charge {m.AvgUtil:0} % · {m.AvgClock:0} MHz · {m.AvgPower:0} W", 12.5, "Muted"));
        stack.Children.Add(Text($"Ping internet {m.PingAvg:0} ms (max {m.PingMax:0}) · {m.Spikes} pics · perte {m.LossPct:0.#} %", 12.5, "Muted"));
        stack.Children.Add(Text(m.Verdict, 13, "Text").With(t => t.Margin = new Thickness(0, 4, 0, 0)));
        return Card(stack);
    }

    private UIElement ValorantPage()
    {
        var v = _state.Data.Valo;
        var page = W.Page("Réglages Valorant", "Ces réglages sont synchronisés par Riot : ils se changent dans le jeu, jamais dans un fichier. Règle, quitte le jeu, puis clique Analyser.");
        page.Children.Add(Toolbar(Btn("Analyser", () => Rescan("valorant"), true)));

        void Check(string label, string want, string? seen, bool? ok)
        {
            var row = new DockPanel();
            var (mark, color) = ok switch { true => ("OK", "Good"), false => ("À changer", "Accent"), _ => ("À vérifier", "Muted") };
            var tag = Text(mark, 12.5, color, true).With(t =>
            {
                t.Margin = new Thickness(16, 0, 0, 0);
                t.VerticalAlignment = VerticalAlignment.Center;
                t.TextWrapping = TextWrapping.NoWrap;
            });
            DockPanel.SetDock(tag, Dock.Right);
            row.Children.Add(tag);
            row.Children.Add(Line(label, seen is null ? want : $"{want}  ·  détecté : {seen}"));
            page.Children.Add(Card(row));
        }

        var limit = v.LimitAlways is null ? null : v.LimitAlways == true ? $"activée, {v.MaxAlways:0} FPS" : "désactivée";
        page.Children.Add(Heading("Vidéo > Général"));
        Check("Limiter les FPS en permanence", "Désactivé, ou au-dessus de ta fréquence d'écran", limit, v.LimitAlways is null ? null : v.LimitAlways == false || v.MaxAlways > 144);
        Check("Mode d'affichage", "Plein écran", v.FullscreenMode is null ? null : v.FullscreenMode == 0 ? "plein écran" : "fenêtré", v.FullscreenMode is null ? null : v.FullscreenMode == 0);
        var native = _state.Data.GameDisplay is { } display ? $"{display.Width}x{display.Height}" : "";
        Check("Résolution", native.Length == 0 ? "Résolution native de ton écran" : native, v.Resolution, v.Resolution == native && native.Length > 0 ? true : null);
        Check("V-Sync", "Désactivé", v.VSync is null ? null : v.VSync == true ? "activé" : "désactivé", v.VSync is null ? null : v.VSync == false);
        page.Children.Add(Heading("Vidéo > Qualité des graphismes"));
        Check("Rendu multithread", "Activé (gros gain à partir de 4 cœurs)", null, null);
        Check("Matériaux, Textures, Détails, Interface", "Bas", null, null);
        Check("Vignette, Anticrénelage (MSAA)", "Désactivé / Aucun", null, null);
        Check("Filtrage anisotrope", "1x", null, null);
        Check("Améliorer la clarté, Bloom, Distorsion, Ombres", "Désactivé", null, null);
        if (_state.Data.HasNvidia)
        {
            Check("NVIDIA Reflex", _state.Data.IsLaptop ? "Activé (sans Boost : le portable chauffe moins)" : "Activé", null, null);
            page.Children.Add(Heading("Panneau de configuration NVIDIA"));
            Check("Taux de rafraîchissement maximal", "Désactivé (peut aussi brider les FPS)", null, null);
            Check("Mode de gestion de l'alimentation", "Performances maximales pour Valorant seulement (Paramètres de programme), pas en global : sinon le GPU chauffe au repos", null, null);
            Check("Synchronisation verticale", "Désactivée", null, null);
        }
        page.Children.Add(Heading("Général > Réseau"));
        Check("Mise en mémoire tampon réseau", "Minimum (à monter d'un cran seulement si les tirs saccadent)", null, null);
        Check("Graphiques des stats réseau", "Affiche Ping réseau et Perte de paquets pour voir le vrai ping", null, null);
        return page;
    }

    private UIElement PerfPage()
    {
        var data = _state.Data;
        var page = W.Page("Performances", "Mesuré par Valorant lui-même à chaque round. Les traits pointillés marquent une session où tes réglages ont changé.");
        page.Children.Add(Toolbar(Btn("Analyser les logs", () => Rescan("perf"), true)));

        var sessions = data.Sessions.TakeLast(14).ToList();
        if (sessions.Count == 0)
        {
            page.Children.Add(Text("Aucune session trouvée.", 13, "Muted"));
        }
        else
        {
            var journal = Journal.Read();
            var chart = new StackPanel();
            chart.Children.Add(Text("FPS moyens par session", 14, "Text", true));
            chart.Children.Add(new LineChart
            {
                Height = 170,
                Values = sessions.Select(s => s.AvgFps).ToList(),
                Labels = sessions.Select(s => s.Start.ToString("dd/MM")).ToList(),
                Marks = Journal.ChangedSessions(sessions, journal),
            });
            page.Children.Add(Card(chart));
            AddComparison(page, data.Sessions, journal);

            page.Children.Add(Heading("Détail par session"));
            page.Children.Add(Card(SessionTable(sessions.AsEnumerable().Reverse().Take(10).ToList())));
        }

        if (!data.HasNvidia)
        {
            page.Children.Add(Text("La mesure GPU en direct n'existe que sur carte NVIDIA.", 12.5, "Muted").With(t => t.Margin = new Thickness(0, 8, 0, 0)));
            return page;
        }

        page.Children.Add(Heading("GPU en direct"));
        page.Children.Add(Text("Enregistre pendant une partie pour savoir si ça bloque côté GPU, CPU ou chaleur. Passe par nvidia-smi, rien n'approche le jeu.", 12.5, "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 10)));
        var live = new UniformGrid { Columns = 5 };
        var temp = Text("-", 22, "Text", true);
        var util = Text("-", 22, "Text", true);
        var clock = Text("-", 22, "Text", true);
        var power = Text("-", 22, "Text", true);
        var vram = Text("-", 22, "Text", true);
        foreach (var (label, value) in new[] { ("Température", temp), ("Charge", util), ("Fréquence", clock), ("Puissance", power), ("VRAM", vram) })
        {
            var stack = new StackPanel();
            stack.Children.Add(Text(label, 12, "Muted"));
            stack.Children.Add(value);
            live.Children.Add(Card(stack).With(c => c.Margin = new Thickness(0, 0, 8, 8)));
        }
        page.Children.Add(live);
        var limiter = Text("Bridage actif : -", 12.5, "Muted");
        page.Children.Add(limiter);
        var verdict = Text("", 13.5, "Text", true).With(t => t.Margin = new Thickness(0, 10, 0, 0));
        var record = new Button { Content = "Enregistrer", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        record.Click += (_, _) =>
        {
            if (_recording is null)
            {
                _recording = [];
                record.Content = "Arrêter";
                verdict.Text = "Enregistrement…";
                return;
            }
            verdict.Text = GpuVerdict(_recording);
            _recording = null;
            record.Content = "Enregistrer";
        };
        page.Children.Add(record);
        page.Children.Add(verdict);

        void Tick()
        {
            var sys = _state.Sys;
            Task.Run(() => Gpu.Sample(sys)).ContinueWith(t => Dispatcher.Invoke(() =>
            {
                if (t.Result is not { } s) return;
                temp.Text = $"{s.Temp:0} °C";
                util.Text = $"{s.Util:0} %";
                clock.Text = $"{s.Clock:0} MHz";
                power.Text = $"{s.Power:0} W";
                vram.Text = $"{s.MemUsed / 1024:0.0} / {s.MemTotal / 1024:0.0} Go";
                limiter.Text = "Bridage actif : " + s.ThrottleText;
                _recording?.Add(s);
            }));
        }
        // Every 2 s: spawning nvidia-smi faster can itself cause small hitches in game.
        _monitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitor.Tick += (_, _) => Tick();
        _monitor.Start();
        return page;
    }

    /// <summary>Compares what was measured before Shanksito Tweaker touched anything with the latest session.</summary>
    private void AddComparison(StackPanel page, List<PerfSession> sessions, List<JournalEntry> journal)
    {
        if (journal.Count == 0) return;
        var first = journal.Min(e => e.When);
        var before = sessions.Where(s => s.Start < first).ToList();
        var after = sessions.LastOrDefault(s => s.Start > first);
        if (before.Count == 0) return;
        if (after is null)
        {
            page.Children.Add(Text("Joue une partie : la comparaison avant / après apparaîtra ici.", 13, "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 8)));
            return;
        }

        double Avg(Func<PerfSession, double> f) => before.Sum(s => f(s) * s.Frames) / before.Sum(s => s.Frames);
        page.Children.Add(Heading("Avant Shanksito Tweaker / dernière session"));
        var row = new UniformGrid { Columns = 4 };
        void Delta(string label, double was, double now, string unit, bool higherIsBetter)
        {
            var better = higherIsBetter ? now > was : now < was;
            row.Children.Add(Tile(label, $"{now:0}{unit}", now.Equals(was) ? "Text" : better ? "Good" : "Accent", $"avant {was:0}{unit}"));
        }
        Delta("FPS moyens", Avg(s => s.AvgFps), after.AvgFps, "", true);
        Delta("Images sous 120 FPS", Avg(s => s.PctUnder120), after.PctUnder120, " %", false);
        Delta("CPU en fond", Avg(s => s.SystemCpu), after.SystemCpu, " %", false);
        Delta("RAM utilisée", Avg(s => s.MemoryPct), after.MemoryPct, " %", false);
        page.Children.Add(row);

        var bars = new StackPanel();
        bars.Children.Add(Text("Répartition des images", 14, "Text", true));
        void Bar(string label, double under120, double to144, double over240)
        {
            bars.Children.Add(Text(label, 12, "Muted").With(t => t.Margin = new Thickness(0, 8, 0, 3)));
            bars.Children.Add(StackBar((under120, "Accent"), (to144, "Warn"), (100 - under120 - to144 - over240, "Info"), (over240, "Good")));
        }
        Bar("Avant", Avg(s => s.PctUnder120), Avg(s => s.PctUnder144 - s.PctUnder120), Avg(s => s.PctOver240));
        Bar("Dernière session", after.PctUnder120, after.PctUnder144 - after.PctUnder120, after.PctOver240);
        var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (text, color) in new[] { ("sous 120", "Accent"), ("120-144", "Warn"), ("144-240", "Info"), ("plus de 240", "Good") })
        {
            legend.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = B(color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(Text(text, 11.5, "Muted").With(t => t.Margin = new Thickness(0, 0, 14, 0)));
        }
        bars.Children.Add(legend);
        page.Children.Add(Card(bars));
    }

    private static string GpuVerdict(List<GpuSample> samples)
    {
        var busy = samples.Where(s => s.Util >= 30).ToList();
        if (busy.Count < 5) return "Pas assez de charge enregistrée : lance une partie pendant l'enregistrement.";
        var util = busy.Average(s => s.Util);
        var temp = busy.Max(s => s.Temp);
        var clock = busy.Average(s => s.Clock);
        var power = busy.Average(s => s.Power);
        var thermal = busy.Count(s => (s.Throttle & 0x68) != 0) * 100.0 / busy.Count;
        var summary = $"{busy.Count * 2} s : charge {util:0} %, {clock:0} MHz, {power:0} W, max {temp:0} °C. ";
        if (thermal >= 10 || temp >= 87) return summary + "Verdict : CHALEUR. Le GPU se bride, c'est le cooling qui limite.";
        if (util >= 92) return summary + "Verdict : GPU à fond. Baisse les réglages graphiques ou vérifie le mode Turbo.";
        if (util < 80) return summary + "Verdict : GPU pas saturé. Si les FPS restent bas sans limite, c'est le CPU ou l'arrière-plan.";
        return summary + "Verdict : équilibré entre CPU et GPU.";
    }

    private static Grid SessionTable(List<PerfSession> sessions)
    {
        string[] heads = ["Date", "Rounds", "FPS moy.", "< 120 FPS", "GPU ms", "CPU fond", "RAM"];
        var grid = new Grid();
        foreach (var _ in heads) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Cell(int row, int col, string text, string color = "Text", bool bold = false)
        {
            var cell = Text(text, 12.5, color, bold).With(t => t.Margin = new Thickness(0, 3, 0, 3));
            SetRow(cell, row);
            SetColumn(cell, col);
            grid.Children.Add(cell);
        }

        for (var c = 0; c < heads.Length; c++) Cell(0, c, heads[c], "Muted", true);
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(i + 1, 0, s.Start.ToString("dd/MM HH:mm"));
            Cell(i + 1, 1, s.Rounds.ToString());
            Cell(i + 1, 2, s.AvgFps.ToString("0"), "Text", true);
            Cell(i + 1, 3, $"{s.PctUnder120:0} %");
            Cell(i + 1, 4, s.GpuMs.ToString("0.0"));
            Cell(i + 1, 5, $"{s.SystemCpu:0} %");
            Cell(i + 1, 6, $"{s.MemoryPct:0} %", s.MemoryPct >= 85 ? "Warn" : "Text");
        }
        return grid;
    }
}
