using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ShanksitoTweaker.Core;
using ShanksitoTweaker.Ui;
using static ShanksitoTweaker.Ui.W;

namespace ShanksitoTweaker;

/// <summary>Window content: navigation and shared rows. Pages live in the Ui folder and are rebuilt from the latest scan.</summary>
public sealed partial class Shell : Grid
{
    public static bool Animate { get; set; } = true;

    public static readonly (string Key, string Label, string Icon, string Group)[] Pages =
    [
        ("home", "Tableau de bord", "", ""),
        ("ai", "Shanksito AI", "", ""),
        ("session", "Session de jeu", "", "JEU"),
        ("valorant", "Réglages Valorant", "", "JEU"),
        ("perf", "Performances", "", "JEU"),
        ("windows", "Windows", "", "OPTIMISER"),
        ("display", "Affichage", "", "OPTIMISER"),
        ("network", "Réseau et ping", "", "OPTIMISER"),
        ("startup", "Démarrage", "", "ALLÉGER"),
        ("services", "Services", "", "ALLÉGER"),
        ("tasks", "Tâches planifiées", "", "ALLÉGER"),
        ("clean", "Nettoyage", "", "ALLÉGER"),
        ("health", "Santé du PC", "", "SYSTÈME"),
        ("undo", "Historique", "", "SYSTÈME"),
    ];

    private readonly AppState _state;
    private readonly ContentControl _host = new();
    private readonly TextBlock _status;
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, RadioButton> _nav = [];
    private readonly TextBox _search = new() { Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
    private string _current = "home";
    private DispatcherTimer? _monitor;
    private List<GpuSample>? _recording;

    public Shell(AppState state)
    {
        _state = state;
        Background = B("Bg");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nav = new StackPanel { Margin = new Thickness(12, 18, 12, 12) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 2) };
        brand.Children.Add(Icon("", 20, "Accent").With(i => i.Margin = new Thickness(0, 2, 8, 0)));
        brand.Children.Add(Text("Shanksito", 21, "Text", true));
        nav.Children.Add(brand);
        nav.Children.Add(Text("Tweaker · Valorant", 11.5, "Muted").With(t => t.Margin = new Thickness(12, 0, 0, 14)));

        var group = "";
        foreach (var (key, label, icon, section) in Pages)
        {
            if (section != group)
            {
                group = section;
                nav.Children.Add(Text(section, 10.5, "Muted", true).With(t => t.Margin = new Thickness(12, 12, 0, 4)));
            }
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Icon(icon, 15).With(i => i.Margin = new Thickness(0, 0, 11, 0)));
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var button = new RadioButton { Content = content, GroupName = "nav", Style = (Style)Application.Current.Resources["Nav"] };
            button.Click += (_, _) =>
            {
                _search.Text = "";
                Show(key);
            };
            _nav[key] = button;
            nav.Children.Add(button);
        }
        var side = new Border { Background = B("Panel"), Child = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        SetRowSpan(side, 3);
        Children.Add(side);

        var searchBox = new Grid { Margin = new Thickness(28, 14, 28, 0), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
        _search.Width = 420;
        _search.Padding = new Thickness(32, 6, 8, 6);
        _search.ToolTip = "Rechercher un réglage";
        var hint = Text("Rechercher un réglage…", 13, "Muted").With(t =>
        {
            t.IsHitTestVisible = false;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(34, 0, 0, 0);
        });
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_search.Text.Trim().Length >= 2) ShowSearch(_search.Text.Trim());
            else if (_search.Text.Length == 0 && _host.Content is not null && _state.Data.Windows.Count > 0) Show(_current);
        };
        searchBox.Children.Add(_search);
        searchBox.Children.Add(hint);
        searchBox.Children.Add(Icon("", 13).With(i =>
        {
            i.HorizontalAlignment = HorizontalAlignment.Left;
            i.Margin = new Thickness(11, 0, 0, 0);
            i.IsHitTestVisible = false;
        }));
        SetColumn(searchBox, 1);
        Children.Add(searchBox);

        _scroll = new ScrollViewer { Content = _host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 16, 22, 22) };
        SetColumn(_scroll, 1);
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        _status = Text(_state.Admin ? "Prêt. Chaque réglage est sauvegardé avant d'être changé." : "Sans droits admin : réglages système en lecture seule. Relance et accepte l'UAC.", 12, "Muted");
        var bar = new Border { Background = B("Panel"), Padding = new Thickness(28, 8, 20, 8), Child = _status };
        SetColumn(bar, 1);
        SetRow(bar, 2);
        Children.Add(bar);
    }

    public void ShowLoading() => _host.Content = Text("Analyse du PC…", 18, "Muted");

    public void StopMonitor()
    {
        _monitor?.Stop();
        _monitor = null;
    }

    public void Show(string key)
    {
        StopMonitor();
        _current = key;
        _nav[key].IsChecked = true;
        _host.Content = key switch
        {
            "home" => HomePage(),
            "ai" => AiPage(),
            "session" => SessionPage(),
            "valorant" => ValorantPage(),
            "perf" => PerfPage(),
            "windows" => WindowsPage(),
            "display" => DisplayPage(),
            "network" => NetworkPage(),
            "startup" => TweakListPage("Démarrage", "Interrupteur à droite : l'appli ne se lance plus avec Windows. Même mécanisme que le Gestionnaire des tâches. Rien n'est désinstallé.", _state.Data.Startup, "Couper les conseillés", "startup", true),
            "services" => TweakListPage("Services", "Passe des services tiers en démarrage manuel et les arrête. Defender, pare-feu et Vanguard ne sont jamais proposés.", _state.Data.Services, "Passer les conseillés en manuel", "services", false),
            "tasks" => TweakListPage("Tâches planifiées", "Tâches lancées toutes seules par les applis installées. Celles de Windows, du matériel, des pilotes et de Riot ne sont pas listées.", _state.Data.Tasks, "Couper les conseillées", "tasks", false),
            "clean" => CleanPage(),
            "health" => HealthPage(),
            _ => UndoPage(),
        };
        _scroll.ScrollToTop();
        if (Animate) _host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
    }

    private void ShowSearch(string query)
    {
        StopMonitor();
        var page = W.Page("Recherche", $"Réglages contenant « {query} ».");
        var hits = _state.Data.AllTweaks
            .Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Why.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(30).ToList();
        if (hits.Count == 0) page.Children.Add(Text("Aucun réglage trouvé.", 13, "Muted"));
        foreach (var tweak in hits) page.Children.Add(TweakRow(tweak));
        _host.Content = page;
        _scroll.ScrollToTop();
    }

    private void SetStatus(string message) => _status.Text = message;

    public void OnMatchFinished(MatchReport report)
    {
        _state.Data.Matches.Insert(0, report);
        SetStatus($"Partie terminée : {report.Verdict}");
        if (_current is "home" or "session") Show(_current);
    }

    private void Rescan(string page)
    {
        ShowLoading();
        Task.Run(_state.Scan).ContinueWith(_ => Dispatcher.Invoke(() => Show(page)));
    }

    private FrameworkElement TweakRow(Tweak tweak)
    {
        var applied = tweak.IsApplied(_state.Sys);
        var hasBackup = _state.Engine.CanRevert(tweak.Id);
        var locked = tweak is StartupTweak { Locked: true };
        var userScope = tweak is StartupTweak { ApprovedRoot: RegRoot.HKCU }
            || (tweak is RegistryTweak registry && registry.Edits.All(x => x.Root == RegRoot.HKCU));
        var needsAdmin = !_state.Admin && !userScope;

        var toggle = new CheckBox { Style = (Style)Application.Current.Resources["Switch"], IsChecked = applied };
        // Already in that state without a snapshot: there is no original value to go back to.
        toggle.IsEnabled = !locked && !needsAdmin && (!applied || hasBackup);
        toggle.Click += async (_, _) =>
        {
            toggle.IsEnabled = false;
            var turnOn = toggle.IsChecked == true;
            var result = await Task.Run(() => turnOn ? _state.Engine.Apply(tweak) : _state.Engine.Revert(tweak.Id));
            SetStatus(result.Message);
            if (!turnOn && result.Ok && tweak is ScheduledTaskTweak task) task.Disabled = false;
            toggle.IsChecked = tweak.IsApplied(_state.Sys);
            toggle.IsEnabled = toggle.IsChecked != true || _state.Engine.CanRevert(tweak.Id);
        };
        DockPanel.SetDock(toggle, Dock.Right);

        var row = new DockPanel();
        row.Children.Add(toggle);
        var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var title = new WrapPanel();
        title.Children.Add(Text(tweak.Title, 14, "Text", true));
        if (tweak.Recommended) title.Children.Add(Pill("conseillé", "Good"));
        if (tweak.Risk != Risk.Sur) title.Children.Add(Pill("à tester", "Warn"));
        if (tweak.Reboot) title.Children.Add(Pill("redémarrage", "Info"));
        if (applied && !hasBackup) title.Children.Add(Pill(tweak is StartupTweak or ScheduledTaskTweak ? "déjà coupé" : "déjà actif", "Muted"));
        stack.Children.Add(title);
        stack.Children.Add(Text(tweak.Why, 12.5, "Muted").With(t =>
        {
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.MaxHeight = 54;
        }));
        if (tweak.Gain.Length > 0 && tweak is not StartupTweak)
            stack.Children.Add(Text("Effet : " + tweak.Gain, 12, "Info"));
        row.Children.Add(stack);
        return Card(row);
    }

    private async void ApplyAll(IEnumerable<Tweak> tweaks, string reloadPage, bool onlyRecommended = true)
    {
        var todo = tweaks.Where(t => (!onlyRecommended || t.Recommended) && !t.IsApplied(_state.Sys)).ToList();
        if (todo.Count == 0)
        {
            SetStatus("Rien à appliquer.");
            return;
        }
        var summary = string.Join("\n", todo.Take(25).Select(t => "• " + t.Title));
        if (MessageBox.Show($"Appliquer {todo.Count} réglages ?\n\n{summary}\n\nTous sauvegardés, annulables dans l'onglet Historique.", "Shanksito Tweaker", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        var results = await Task.Run(() => todo.Select(_state.Engine.Apply).ToList());
        var failed = results.Where(r => !r.Ok).ToList();
        Show(reloadPage);
        SetStatus(failed.Count == 0 ? $"{results.Count} réglages appliqués." : $"{results.Count - failed.Count} appliqués, {failed.Count} en échec : {failed[0].Message}");
    }
}
