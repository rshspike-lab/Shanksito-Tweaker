using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShanksitoTweaker.Core;
using ShanksitoTweaker.Ui;
using static ShanksitoTweaker.Ui.W;

namespace ShanksitoTweaker;

public sealed class ChatEntry
{
    public string Role = "";
    public string Text = "";
    public AiReply? Reply;
    public bool Live;
}

public sealed partial class Shell
{
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(5);

    private bool _aiModels;
    private TextBlock? _aiBubble;
    private CancellationTokenSource? _aiCancel;
    private DispatcherTimer? _aiIdle;

    private UIElement AiPage()
    {
        var page = W.Page("Shanksito AI", "Ton assistant, directement sur ton PC. Rien ne part sur internet. Il analyse, répond et propose : c'est toi qui confirmes chaque changement.");
        var current = _state.Ai.Current;
        if (current is null || _aiModels)
        {
            AddModels(page, current);
            return page;
        }

        var analyse = Btn("Analyser mon PC", () => SendAi(ShanksitoAi.AnalysePrompt), true);
        var reset = Btn("Nouvelle conversation", () =>
        {
            _state.Chat.Clear();
            Show("ai");
        });
        var models = Btn("Changer de modèle", () =>
        {
            _aiModels = true;
            Show("ai");
        });
        var busy = _aiCancel is not null;
        analyse.IsEnabled = !busy;
        reset.IsEnabled = !busy;
        models.IsEnabled = !busy;
        page.Children.Add(Toolbar(analyse, reset, models));
        page.Children.Add(Text($"Modèle : {current.Name} ({current.Tag})", 12.5, "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 10)));

        if (_state.Chat.Count == 0)
            page.Children.Add(Card(Text("Salut, je suis Shanksito AI. Demande-moi d'analyser ton PC, ou dis-moi ce que tu veux changer : « coupe Discord au démarrage », « pourquoi j'ai des chutes de FPS ? », « teste mon ping ».", 13.5), "Info"));
        foreach (var entry in _state.Chat) page.Children.Add(Bubble(entry));

        var question = new TextBox { Height = 38, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = !busy };
        var send = Btn("Envoyer", () => { });
        send.Margin = new Thickness(8, 0, 0, 0);
        send.IsEnabled = !busy;
        void Submit()
        {
            if (question.Text.Trim().Length > 0) SendAi(question.Text.Trim());
        }
        send.Click += (_, _) => Submit();
        question.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Submit();
        };
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        row.Children.Add(send);
        if (busy)
        {
            var stop = Btn("Arrêter", () => _aiCancel?.Cancel());
            stop.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(stop, Dock.Right);
            row.Children.Add(stop);
        }
        row.Children.Add(question);
        page.Children.Add(row);
        return page;
    }

    private void AddModels(StackPanel page, AiModel? current)
    {
        var data = _state.Data;
        var vram = Gpu.VideoMemoryGb(_state.Sys);
        var best = AiCatalog.Recommend(data.RamGb, vram);
        page.Children.Add(Text($"Ton PC : {data.RamGb:0} Go de RAM, {vram:0} Go de mémoire vidéo. Plus un modèle est gros, plus il est capable et lent. Il se décharge tout seul quand tu joues.", 13, "Muted").With(t => t.Margin = new Thickness(0, 0, 0, 12)));
        if (current is not null)
            page.Children.Add(Toolbar(Btn("Retour au chat", () =>
            {
                _aiModels = false;
                Show("ai");
            })));

        foreach (var model in AiCatalog.Models)
        {
            var installed = _state.Ai.Installed(model);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (installed)
            {
                var use = Btn(model == current ? "Utilisé" : "Utiliser", () =>
                {
                    _state.Ai.Current = model;
                    _state.Server.Stop();
                    _aiModels = false;
                    Show("ai");
                }, model != current);
                use.IsEnabled = model != current;
                use.Margin = new Thickness(0);
                var remove = Btn("Supprimer", () =>
                {
                    if (MessageBox.Show($"Supprimer {model.Name} ({model.SizeGb:0.0} Go) ?", Installer.Product, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    _state.Server.Stop();
                    _state.Ai.Remove(model);
                    Show("ai");
                });
                remove.Margin = new Thickness(8, 0, 0, 0);
                actions.Children.Add(use);
                actions.Children.Add(remove);
            }
            else
            {
                var install = Btn("Installer", () => InstallModel(model), model == best);
                install.Margin = new Thickness(0);
                install.IsEnabled = _aiCancel is null;
                actions.Children.Add(install);
            }
            DockPanel.SetDock(actions, Dock.Right);
            var row = new DockPanel();
            row.Children.Add(actions);
            var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var title = new WrapPanel();
            title.Children.Add(Text(model.Name, 14, "Text", true));
            title.Children.Add(Pill(model.Tag, "Info"));
            if (model == best) title.Children.Add(Pill("conseillé pour ton PC", "Good"));
            if (!AiCatalog.RunsWell(model, data.RamGb)) title.Children.Add(Pill("trop lourd ici", "Warn"));
            stack.Children.Add(title);
            stack.Children.Add(Text($"{model.SizeGb:0.0} Go · {model.MinRamGb} Go de RAM minimum · {model.Note}", 12.5, "Muted"));
            row.Children.Add(stack);
            page.Children.Add(Card(row));
        }
        if (_aiCancel is not null)
            page.Children.Add(Toolbar(Btn("Annuler le téléchargement", () => _aiCancel?.Cancel())));
        page.Children.Add(Text("Modèles ouverts sous licence Apache 2.0, téléchargés depuis Hugging Face. Moteur llama.cpp. Chaque fichier est vérifié avant usage.", 12, "Muted").With(t => t.Margin = new Thickness(0, 6, 0, 0)));
    }

    private async void InstallModel(AiModel model)
    {
        if (MessageBox.Show($"Télécharger {model.Name} ({model.SizeGb:0.0} Go) et le moteur IA ?", Installer.Product, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        using var cancel = new CancellationTokenSource();
        _aiCancel = cancel;
        Show("ai");
        try
        {
            await Task.Run(() => _state.Ai.Install(model, text => Dispatcher.Invoke(() => SetStatus(text)), cancel.Token));
            _aiModels = false;
            SetStatus($"{model.Name} installé.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(ex is OperationCanceledException ? "Téléchargement en pause : relance-le pour reprendre." : ShanksitoAi.ErrorMessage(ex));
        }
        _aiCancel = null;
        if (_current == "ai") Show("ai");
    }

    private Border Bubble(ChatEntry entry)
    {
        if (entry.Role == "note") return Card(Text(entry.Text, 13, "Muted"));
        var mine = entry.Role == "user";
        var stack = new StackPanel();
        stack.Children.Add(Text(mine ? "Toi" : "Shanksito AI", 12, mine ? "Muted" : "Accent", true).With(t => t.Margin = new Thickness(0, 0, 0, 3)));
        var body = Text(entry.Text, 13.5);
        if (entry.Live) _aiBubble = body;
        stack.Children.Add(body);

        if (entry.Reply is { } reply)
        {
            var known = _state.Data.AllTweaks.ToDictionary(t => t.Id);
            var actions = reply.Actions.Where(a => known.ContainsKey(a.TweakId)).ToList();
            if (actions.Count > 0)
            {
                stack.Children.Add(Text("Je propose, tu confirmes :", 12.5, "Muted", true).With(t => t.Margin = new Thickness(0, 10, 0, 6)));
                foreach (var action in actions)
                {
                    stack.Children.Add(Text(action.Why, 12.5).With(t => t.Margin = new Thickness(2, 2, 0, 4)));
                    stack.Children.Add(TweakRow(known[action.TweakId]));
                }
                if (actions.Count > 1)
                    stack.Children.Add(Toolbar(Btn("Tout activer", () => ApplyAll(actions.Select(a => known[a.TweakId]), "ai", false)).With(b => b.IsEnabled = _state.Admin)));
            }
            if (reply.Manual.Count > 0)
            {
                stack.Children.Add(Text("À faire à la main :", 12.5, "Muted", true).With(t => t.Margin = new Thickness(0, 10, 0, 6)));
                foreach (var step in reply.Manual) stack.Children.Add(Line(step.Title, step.How).With(s => s.Margin = new Thickness(0, 0, 0, 6)));
            }
            var pages = reply.Pages.Select(key => Pages.FirstOrDefault(p => p.Key == key)).Where(p => p.Key is not null).ToList();
            if (pages.Count > 0)
            {
                var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var target in pages) bar.Children.Add(Btn(target.Label, () => Show(target.Key)));
                stack.Children.Add(bar);
            }
            if (reply.WordsPerSecond > 0)
                stack.Children.Add(Text($"{reply.WordsPerSecond:0} tokens/s", 11, "Muted").With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        }
        return Card(stack, mine ? null : "Accent");
    }

    private async void SendAi(string question)
    {
        if (_aiCancel is not null) return;
        if (GameWatcher.GameRunning())
        {
            SetStatus("Pas pendant une partie : le modèle prendrait du GPU au jeu.");
            return;
        }
        var model = _state.Ai.Current;
        if (model is null) return;

        var history = _state.Chat.Where(c => c.Role is "user" or "ai" && c.Text.Length > 0).TakeLast(8).Select(c => new ChatLine(c.Role == "ai" ? "assistant" : "user", c.Text)).ToList();
        history.Add(new ChatLine("user", question));
        var answer = new ChatEntry { Role = "ai", Live = true };
        _state.Chat.Add(new ChatEntry { Role = "user", Text = question });
        _state.Chat.Add(answer);
        using var cancel = new CancellationTokenSource();
        _aiCancel = cancel;
        _aiIdle?.Stop();
        Show("ai");
        _scroll.ScrollToEnd();

        try
        {
            var measured = ShanksitoAi.WantsPingTest(question);
            if (measured || (question == ShanksitoAi.AnalysePrompt && _state.Data.NetTest is null))
            {
                SetStatus("Mesure du réseau…");
                await Task.Run(_state.TestNetwork, cancel.Token);
            }
            if (!_state.Server.Running(model))
            {
                SetStatus($"Chargement de {model.Name}…");
                await _state.Server.Start(_state.Ai, model, cancel.Token);
            }
            SetStatus("Shanksito AI écrit…");
            var context = await Task.Run(() => _state.AiContext(model.Name), cancel.Token);
            var body = ShanksitoAi.RequestBody(context.System, ShanksitoAi.WithHint(history, question, context.Tweaks), context.Allowed);
            var raw = await Task.Run(() => ShanksitoAi.Ask(_state.Server, body, context.Allowed, text => Dispatcher.Invoke(() =>
            {
                answer.Text = ShanksitoAi.Clean(text);
                if (_aiBubble is not null) _aiBubble.Text = answer.Text;
                if (_current == "ai") _scroll.ScrollToEnd();
            }), cancel.Token), cancel.Token);
            var reply = ShanksitoAi.Refine(raw, question, context.Tweaks);
            answer.Text = reply.Text;
            answer.Reply = reply;
            answer.Live = false;
            if (reply.TestPing || measured)
            {
                if (!measured)
                {
                    SetStatus("Test du ping…");
                    await Task.Run(_state.TestNetwork, cancel.Token);
                }
                if (_state.Data.NetTest is { } test) _state.Chat.Add(new ChatEntry { Role = "note", Text = $"Test du ping : box {test.Gateway.Avg:0.#} ms, internet {test.Internet.Avg:0.#} ms. {test.Verdict}" });
            }
            SetStatus("Rien n'a été changé. C'est toi qui confirmes.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            answer.Live = false;
            if (answer.Text.Length == 0) _state.Chat.Remove(answer);
            SetStatus(ShanksitoAi.ErrorMessage(ex));
        }
        _aiCancel = null;
        UnloadWhenIdle();
        if (_current == "ai")
        {
            Show("ai");
            _scroll.ScrollToEnd();
        }
    }

    private void UnloadWhenIdle()
    {
        _aiIdle ??= new DispatcherTimer { Interval = IdleUnload };
        _aiIdle.Tick -= OnAiIdle;
        _aiIdle.Tick += OnAiIdle;
        _aiIdle.Stop();
        _aiIdle.Start();
    }

    private void OnAiIdle(object? sender, EventArgs e)
    {
        _aiIdle?.Stop();
        _state.Server.Stop();
    }
}
