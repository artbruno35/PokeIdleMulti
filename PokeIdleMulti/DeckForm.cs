using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// A janela do deck: as 4 contas dentro dela, cada uma num perfil próprio do navegador embutido
/// (WebView2), sem barra de navegador. Dá para mover, redimensionar, maximizar e pôr em tela cheia;
/// as contas ficam em grade 2×2, em colunas ou uma ampliada. As lives da Twitch/Kick rodam escondidas
/// numa janela à parte, no perfil da conta vinculada.
/// </summary>
internal sealed class DeckForm : Form
{
    private const string GameUrl = "https://pokeidle.io/app";
    private const int Accounts = 4;
    private const int Gap = 4;

    // Sem isso o navegador desacelera as contas escondidas, minimizadas ou atrás de outras janelas;
    // e o autoplay deixa as lives da Kick tocarem (sem som) sem precisar de clique.
    private const string BrowserArguments =
        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows " +
        "--disable-renderer-backgrounding --autoplay-policy=no-user-gesture-required";

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokeIdleMulti");

    private readonly bool _selfTest;
    private readonly string _root;
    private readonly string _configPath;
    private readonly DeckConfig _config;
    private readonly ExtensionStore _store;
    private readonly AccountPane[] _panes = new AccountPane[Accounts];
    private readonly LivesWindow?[] _lives = new LivesWindow?[Accounts];
    private readonly TwitchPresence?[] _presence = new TwitchPresence?[Accounts];
    private readonly string?[] _twitchSeen = new string?[Accounts];
    private readonly Dictionary<string, bool?> _kickLive = new();
    private DateTime _kickCheckAt;
    private bool _kickChecking;
    private bool _kickWatching;
    private readonly Dictionary<(int Pane, string Extension), PopupWindow> _extensionWindows = new();
    private readonly ToolStrip _bar = new ClickThroughToolStrip();
    private readonly StatusStrip _status = new();
    private readonly Panel _grid = new() { Dock = DockStyle.Fill, BackColor = Theme.Back };
    private readonly ToolStripButton _gridButton = new("Grade 2×2");
    private readonly ToolStripButton _columnsButton = new("Colunas");
    private readonly ToolStripButton _focusButton = new("Foco");
    private readonly ToolStripButton[] _accountButtons = new ToolStripButton[Accounts];
    private readonly ToolStripSplitButton _echoButton = new("🔁 Eco");
    private bool _echo;
    private int _lastClicked;
    private readonly ToolStripDropDownButton _livesButton = new("Lives");
    private readonly ToolStripDropDownButton _kickButton = new("💰 Pontos da Kick") { Visible = false };
    private readonly ToolStripButton _extensionsButton = new("🧩 Extensões");
    private readonly ToolStripDropDownButton _sniperButton = new("🎯 Sniper") { Visible = false };
    private readonly bool[] _hasSniper = new bool[Accounts];
    private readonly ToolStripButton _fullscreenButton = new("⛶ Tela cheia");
    private readonly ToolStripButton _motionButton = new("🐢 Menos animações");
    private readonly ToolStripStatusLabel _ram = new();
    private readonly ToolStripStatusLabel _livesStatus = new();
    private readonly ToolStripStatusLabel _voteStatus = new();
    private readonly VoteState?[] _votes = new VoteState?[Accounts];
    private readonly DateTime[] _voteRefreshAt = new DateTime[Accounts];
    private readonly bool[] _voteBusy = new bool[Accounts];
    private readonly PopupWindow?[] _voteWindows = new PopupWindow?[Accounts];
    private string? _topIdleGameId;
    private DateTime _topIdleRetryAt;
    private bool _topIdleLookup;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private CoreWebView2Environment? _env;
    private DeckLayout _layoutBeforeFocus = DeckLayout.Grid;
    private bool _fullscreen;
    private FormWindowState _restoreState;
    private Rectangle _restoreBounds;
    private int _seconds;
    private bool _memoryBusy;

    public DeckForm(bool selfTest)
    {
        _selfTest = selfTest;
        // O autoteste usa perfis e páginas de teste separados, para não mexer nas suas contas.
        _root = selfTest ? Path.Combine(DataRoot, "selftest") : DataRoot;
        _configPath = Path.Combine(_root, "deck.json");
        _config = selfTest ? new DeckConfig() : DeckConfig.Load(_configPath);
        // O autoteste começa com a conta 4 desligada, como ficaria salvo do uso anterior.
        if (selfTest)
            _config.Off[3] = true;
        _store = new ExtensionStore(_root);
        if (_config.Layout != DeckLayout.Focus)
            _layoutBeforeFocus = _config.Layout;

        Text = "PokeIdle Deck";
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        KeyPreview = true;
        MinimumSize = new Size(900, 600);

        BuildBar();
        BuildStatus();
        for (int i = 0; i < Accounts; i++)
        {
            var pane = new AccountPane(i) { AccountName = _config.AccountNames[i], ReduceMotion = _config.ReduceMotion };
            pane.FocusRequested += p => ToggleFocus(p.Index);
            pane.ReloadRequested += p => p.Reload();
            pane.FullscreenRequested += _ => ToggleFullscreen();
            pane.SettingsChanged += OnPaneSettingsChanged;
            pane.Clicked += (p, click) => _ = EchoAsync(p, click);
            pane.PowerRequested += p => _ = SetPaneOffAsync(p.Index, !p.Off);
            pane.PopupRequested += (p, extension, anchor) => _ = OpenExtensionPopupAsync(p, extension, anchor);
            pane.VoteRequested += p => _ = OpenVoteAsync(p, null);
            pane.KickMenuOpening += (p, items) =>
            {
                items.Clear();
                items.AddRange(KickMenuItems(p.Index).ToArray());
            };
            pane.TopIdleLinkRequested += (p, uri) => _ = OpenVoteAsync(p, uri);
            _panes[i] = pane;
            _grid.Controls.Add(pane);
        }
        _grid.Resize += (_, _) => ArrangePanes();

        // Encaixe: o que preenche entra primeiro, as barras depois.
        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(_bar);

        RestoreWindowBounds();
        _clock.Tick += (_, _) => OnClock();
    }

    private void BuildBar()
    {
        Theme.Style(_bar);
        _bar.Padding = new Padding(6, 3, 6, 3);
        _bar.Items.Add(new ToolStripLabel("PokeIdle Deck") { Font = new Font(_bar.Font, FontStyle.Bold), ForeColor = Theme.Accent });
        _bar.Items.Add(new ToolStripSeparator());

        _gridButton.ToolTipText = "As 4 contas em grade";
        _columnsButton.ToolTipText = "As 4 contas lado a lado";
        _focusButton.ToolTipText = "Uma conta ocupando o deck (as outras continuam rodando escondidas)";
        _gridButton.Click += (_, _) => SetLayout(DeckLayout.Grid);
        _columnsButton.Click += (_, _) => SetLayout(DeckLayout.Columns);
        _focusButton.Click += (_, _) => FocusPane(_config.Focused);
        _bar.Items.AddRange(new ToolStripItem[] { _gridButton, _columnsButton, _focusButton, new ToolStripSeparator() });

        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            var button = new ToolStripButton
            {
                ToolTipText = "Clique: ampliar esta conta (de novo: voltar). Botão direito: mais opções",
            };
            button.Click += (_, _) => ToggleFocus(index);
            button.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    ShowAccountMenu(index);
            };
            _accountButtons[i] = button;
            _bar.Items.Add(button);
        }

        _echoButton.ToolTipText = "Eco: um clique numa conta se repete nas outras (no mesmo botão). " +
                                  "A setinha tem o zoom igual em todas as contas";
        _echoButton.ButtonClick += (_, _) => SetEcho(!_echo);
        _echoButton.DropDownOpening += (_, _) => FillEchoMenu();
        _echoButton.DropDownItems.Add("…");
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(_echoButton);

        // Itens à direita entram da direita para a esquerda.
        _fullscreenButton.Alignment = _extensionsButton.Alignment = _livesButton.Alignment = ToolStripItemAlignment.Right;
        _fullscreenButton.ToolTipText = "Tela cheia (F11)";
        _fullscreenButton.Click += (_, _) => ToggleFullscreen();
        _extensionsButton.ToolTipText = "Instalar e gerenciar as extensões de cada conta";
        _extensionsButton.Click += (_, _) => OpenExtensions();
        _livesButton.ToolTipText = "Twitch e Kick abertas em segundo plano, para o bônus de XP das lives no jogo";
        _livesButton.DropDownOpening += (_, _) => FillLivesMenu();
        _livesButton.DropDownItems.Add("…");
        _kickButton.Alignment = ToolStripItemAlignment.Right;
        _kickButton.ToolTipText = "Abre os chats da Kick da conta, para resgatar os pontos do canal (300 pontos = 1 h de +15% de XP)";
        _kickButton.DropDownOpening += (_, _) => FillKickMenu();
        _kickButton.DropDownItems.Add("…");
        _sniperButton.Alignment = ToolStripItemAlignment.Right;
        _sniperButton.ToolTipText = "Liga ou desliga o Rotom Sniper (compras automáticas no mercado) em todas as contas";
        _sniperButton.DropDownOpening += (_, _) => FillSniperMenu();
        _sniperButton.DropDownItems.Add("…");
        _motionButton.Alignment = ToolStripItemAlignment.Right;
        _motionButton.ToolTipText = "Menos animações no jogo: o próprio jogo desliga as que rodam sem parar (selos pulsando, " +
                                    "halos, cartas). Gasta bem menos CPU e placa de vídeo. Desmarque para ver todas";
        _motionButton.Checked = _config.ReduceMotion;
        _motionButton.Click += async (_, _) => await SetReduceMotionAsync(!_config.ReduceMotion);
        _bar.Items.Add(_fullscreenButton);
        _bar.Items.Add(_motionButton);
        _bar.Items.Add(_extensionsButton);
        _bar.Items.Add(_sniperButton);
        _bar.Items.Add(_livesButton);
        _bar.Items.Add(_kickButton);
    }

    private void BuildStatus()
    {
        Theme.Style(_status);
        _status.SizingGrip = false;
        _status.ShowItemToolTips = true;
        _ram.ForeColor = Theme.Muted;
        _livesStatus.ForeColor = Theme.Muted;
        _status.Items.Add(_ram);
        _status.Items.Add(new ToolStripStatusLabel("·") { ForeColor = Theme.Muted });
        _status.Items.Add(_livesStatus);
        _status.Items.Add(new ToolStripStatusLabel("·") { ForeColor = Theme.Muted });
        _voteStatus.ForeColor = Theme.Muted;
        _status.Items.Add(_voteStatus);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ArrangePanes();
        UpdateBar();
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            if (_selfTest)
            {
                File.WriteAllText(SelfTestReport, "ERRO ao iniciar " + ex);
                Close();
                return;
            }
            MessageBox.Show(this, "Não consegui abrir o navegador embutido:\n\n" + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task InitializeAsync()
    {
        if (_selfTest)
            PrepareSelfTestFiles();

        // Configurações de extensão pedidas em "Trazer configurações…": só dá para gravar antes do navegador abrir.
        var imported = await Task.Run(() => StorageImport.ApplyPending(_root));

        var options = new CoreWebView2EnvironmentOptions(BrowserArguments) { AreBrowserExtensionsEnabled = true };
        _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "WebView2"), options);

        // Conta desligada nem cria o navegador: só abre quando for ligada.
        foreach (var pane in _panes.Where(p => _config.Off[p.Index]))
            pane.SetOff(true);
        await Task.WhenAll(_panes.Where(p => !p.Off).Select(p =>
            p.InitializeAsync(_env, GameUrlForDeck, _config.Zoom[p.Index], _config.Muted[p.Index], SelfTestSetup, RepairExtensions)));
        await RefreshExtensionButtonsAsync();
        foreach (int account in _config.LivesAccounts.Where(a => a is >= 1 and <= Accounts).ToList())
            await OpenLivesAsync(account - 1);

        _clock.Start();
        OnClock();
        if (_selfTest)
        {
            _ = RunSelfTestAsync();
            return;
        }
        if (imported.Count > 0)
        {
            var lines = imported.Select(r =>
                $"{_panes.FirstOrDefault(p => p.ProfileName == r.Profile)?.AccountName ?? r.Profile}: {r.Result}");
            MessageBox.Show(this, "Configurações de extensão trazidas:\n\n" + string.Join("\n", lines), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private string GameUrlForDeck => _selfTest ? "https://deck.test/game.html" : GameUrl;

    /// <summary>Dados trazidos de outro PC: reinstala da pasta daqui as extensões cujo caminho antigo sumiu.</summary>
    private Task RepairExtensions(CoreWebView2 core) => _store.RepairAsync(core.Profile);

    private Action<CoreWebView2>? SelfTestSetup => _selfTest
        ? core => core.SetVirtualHostNameToFolderMapping("deck.test", Path.Combine(_root, "site"), CoreWebView2HostResourceAccessKind.Allow)
        : null;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _clock.Stop();
        SaveWindowBounds();
        SaveConfig();
        for (int i = 0; i < Accounts; i++)
            CloseLives(i);
        base.OnFormClosing(e);
    }

    // ---------- Janela: posição, tamanho e tela cheia ----------

    private void RestoreWindowBounds()
    {
        var saved = _config.WindowBounds is { Length: 4 } b ? new Rectangle(b[0], b[1], b[2], b[3]) : Rectangle.Empty;
        if (_selfTest)
            saved = new Rectangle(80, 60, 1500, 900);
        if (saved.Width >= MinimumSize.Width && saved.Height >= MinimumSize.Height
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            var work = Screen.PrimaryScreen!.WorkingArea;
            Size = new Size(Math.Min(1600, work.Width), Math.Min(950, work.Height));
        }
        if (_config.Maximized && !_selfTest)
            WindowState = FormWindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var state = _fullscreen ? _restoreState : WindowState;
        var bounds = _fullscreen ? _restoreBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _config.WindowBounds = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
        if (state != FormWindowState.Minimized)
            _config.Maximized = state == FormWindowState.Maximized;
    }

    private void ToggleFullscreen()
    {
        SuspendLayout();
        if (!_fullscreen)
        {
            _restoreState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var screen = Screen.FromControl(this).Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen;
            _status.Visible = false;
            _fullscreen = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _restoreBounds;
            WindowState = _restoreState;
            _status.Visible = true;
            _fullscreen = false;
        }
        _fullscreenButton.Checked = _fullscreen;
        _fullscreenButton.Text = _fullscreen ? "⛶ Sair da tela cheia" : "⛶ Tela cheia";
        ResumeLayout();
    }

    // ---------- Layout das contas ----------

    private void ArrangePanes()
    {
        var area = _grid.ClientRectangle;
        if (area.Width <= 0 || area.Height <= 0)
            return;
        _grid.SuspendLayout();
        foreach (var pane in _panes)
        {
            var cell = CellFor(pane.Index, area);
            pane.Visible = cell is not null;
            if (cell is Rectangle bounds)
                pane.Bounds = bounds;
        }
        _grid.ResumeLayout();
    }

    private Rectangle? CellFor(int index, Rectangle area) => _config.Layout switch
    {
        DeckLayout.Columns => Cell(area, index, 0, Accounts, 1),
        DeckLayout.Focus => index == _config.Focused ? Cell(area, 0, 0, 1, 1) : null,
        _ => Cell(area, index % 2, index / 2, 2, 2),
    };

    private static Rectangle Cell(Rectangle area, int col, int row, int cols, int rows) => Rectangle.FromLTRB(
        area.X + Gap + col * (area.Width - Gap) / cols,
        area.Y + Gap + row * (area.Height - Gap) / rows,
        area.X + (col + 1) * (area.Width - Gap) / cols,
        area.Y + (row + 1) * (area.Height - Gap) / rows);

    private void SetLayout(DeckLayout layout)
    {
        _config.Layout = layout;
        if (layout != DeckLayout.Focus)
            _layoutBeforeFocus = layout;
        ArrangePanes();
        UpdateBar();
    }

    private void FocusPane(int index)
    {
        _config.Focused = index;
        SetLayout(DeckLayout.Focus);
        _panes[index].WebView.Focus();
    }

    private void ToggleFocus(int index)
    {
        if (_config.Layout == DeckLayout.Focus && _config.Focused == index)
            SetLayout(_layoutBeforeFocus);
        else
            FocusPane(index);
    }

    private void UpdateBar()
    {
        _gridButton.Checked = _config.Layout == DeckLayout.Grid;
        _columnsButton.Checked = _config.Layout == DeckLayout.Columns;
        _focusButton.Checked = _config.Layout == DeckLayout.Focus;
        for (int i = 0; i < Accounts; i++)
        {
            _accountButtons[i].Text = _config.AccountNames[i];
            _accountButtons[i].Checked = _config.Layout == DeckLayout.Focus && _config.Focused == i;
        }

        _livesButton.Text = _config.LivesAccounts.Count == 0 ? "Lives: desligadas" : "Lives: ligadas";
        var parts = Enumerable.Range(0, Accounts)
            .Where(i => _lives[i] is not null || _presence[i] is not null)
            .Select(i => $"{_config.AccountNames[i]}: {LivesSummary(i)}")
            .ToList();
        _livesStatus.Text = parts.Count == 0 ? "Lives desligadas" : "Lives · " + string.Join(" · ", parts);
        _kickButton.Visible = _lives.Any(l => l is not null && l.KickTabs.Any());
        RenderKickButtons();
    }

    /// <summary>O botão kick de cada conta: o que dá para fechar ou abrir de novo, ou por que não tem nada.</summary>
    private void RenderKickButtons()
    {
        for (int i = 0; i < Accounts; i++)
        {
            var pane = _panes[i];
            var open = KickOpen(i);
            var shut = KickShut(i);
            if (_lives[i] is null)
                pane.SetKick(false, false, _presence[i] is null
                    ? "As lives estão desligadas nesta conta (ligue em Lives, na barra de cima)"
                    : "Nenhum canal da Kick na lista (Lives → Canais…)");
            else if (_config.Kick[i].Limited)
                pane.SetKick(true, true, "Lives da Kick fechadas nesta conta: " +
                    (_config.Kick[i].Slots == 0 ? "todas" : shut.Count > 0 ? string.Join(", ", shut) : "a 2ª") +
                    (open.Count > 0 ? $" (abertas: {string.Join(", ", open)})" : "") + ". Clique para fechar outra ou abrir de novo");
            else if (open.Count > 0)
                pane.SetKick(true, false, $"Escolher qual live da Kick fechar nesta conta (abertas: {string.Join(", ", open)}). " +
                    "A vaga de uma live fechada não abre outra sozinha");
            else
                pane.SetKick(false, false, !_selfTest && _kickLive.Count == 0 ? "Conferindo quem está no ar na Kick…"
                    : "Nenhuma live da Kick aberta nesta conta agora");
        }
    }

    /// <summary>As lives da Kick abertas na conta, na ordem da lista.</summary>
    private List<string> KickOpen(int index) => (_lives[index]?.KickChannels ?? Enumerable.Empty<string>())
        .OrderBy(c => _config.KickChannels.IndexOf(c)).ToList();

    /// <summary>Os canais da lista que o kick fechou na conta.</summary>
    private List<string> KickShut(int index) => _config.KickChannels.Where(_config.Kick[index].Closed.Contains).ToList();

    /// <summary>O menu do kick: fechar cada live aberta ou todas; abrir de novo cada fechada ou todas.</summary>
    private List<ToolStripItem> KickMenuItems(int index)
    {
        var items = new List<ToolStripItem>();
        var open = KickOpen(index);
        var shut = KickShut(index);
        foreach (var channel in open)
            items.Add(new ToolStripMenuItem($"Fechar a live de {channel}", null, (_, _) => CloseKick(index, channel)));
        if (open.Count > 0)
            items.Add(new ToolStripMenuItem("Fechar todas (nenhuma live da Kick nesta conta)", null, (_, _) => CloseKick(index, null)));
        if (_config.Kick[index].Limited)
        {
            if (items.Count > 0)
                items.Add(new ToolStripSeparator());
            foreach (var channel in shut)
                items.Add(new ToolStripMenuItem($"Abrir de novo a live de {channel}", null, (_, _) => ReopenKick(index, channel)));
            items.Add(new ToolStripMenuItem($"Abrir todas de novo (volta às {KickLive.Slots} primeiras da lista no ar)", null,
                (_, _) => ReopenKick(index, null)));
        }
        if (items.Count == 0)
            items.Add(new ToolStripMenuItem("Nenhuma live da Kick aberta nesta conta agora") { Enabled = false });
        return items;
    }

    /// <summary>Uma linha por live da Kick de cada conta: abre a janela das lives já nela, com o chat.</summary>
    private void FillKickMenu()
    {
        var items = _kickButton.DropDownItems;
        items.Clear();
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            if (_lives[i] is LivesWindow window && window.KickTabs.Any())
                items.Add($"{_config.AccountNames[i]} · {string.Join(", ", window.KickChannels)}", null, (_, _) => ShowKick(index));
        }
        if (items.Count == 0)
            items.Add(new ToolStripMenuItem("Nenhuma live da Kick no ar agora") { Enabled = false });
    }

    /// <summary>"Twitch 14/14 chats como x, Kick no ar: biscoitao" ou "Twitch: entre na Twitch…".</summary>
    private string LivesSummary(int index)
    {
        var parts = new List<string>();
        if (_presence[index] is TwitchPresence presence)
            parts.Add(presence.Joined > 0 ? $"Twitch {presence.Joined}/{presence.Total} chats como {presence.Login}"
                : presence.NeedsLogin && _twitchSeen[index] is string seen ? $"Twitch {presence.Status} [{seen}]"
                : "Twitch " + presence.Status);
        if (_lives[index] is LivesWindow window)
        {
            var kick = window.KickChannels.ToList();
            var shut = KickShut(index);
            // ▶ tocando, ⏸ parada (o deck manda tocar e, se continuar, recarrega: ⟳ quantas vezes).
            var playback = window.KickPlayback.Select(k => k.Channel
                + (k.Playing == true ? " ▶" : k.Playing == false ? " ⏸" : "") + (k.Reloads > 0 ? $" ⟳{k.Reloads}" : ""));
            if (_config.KickChannels.Count > 0)
                parts.Add(kick.Count > 0 ? "Kick no ar: " + string.Join(", ", playback)
                    : _config.Kick[index].Slots == 0 ? "Kick: todas fechadas no kick"
                    : _kickLive.Count == 0 ? "Kick: conferindo quem está no ar…"
                    : "Kick: nenhuma live da lista no ar");
            if (_config.Kick[index] is { Limited: true, Slots: > 0 })
                parts.Add("fechadas no kick: " + (shut.Count > 0 ? string.Join(", ", shut) : "a 2ª"));
            if (window.Count > kick.Count)
                parts.Add($"{window.Count - kick.Count} páginas da Twitch");
            if (window.Loaded < window.Count)
                parts.Add("carregando…");
        }
        return string.Join(", ", parts);
    }

    private void OnClock()
    {
        foreach (var pane in _panes)
            pane.RefreshHeader();
        if (_seconds++ % 3 == 0)
            _ = UpdateMemoryAsync();
        if (_seconds % 5 == 0)
        {
            foreach (var window in _lives.OfType<LivesWindow>())
                window.KeepParked();
            UpdateBar();
        }
        if (_seconds % 30 == 0)
            foreach (var pane in _panes)
                _ = pane.CheckLoginScreenAsync();
        if (!_selfTest && !_kickChecking && DateTime.Now >= _kickCheckAt && _lives.Any(l => l is not null))
            _ = CheckKickAsync();
        if (!_selfTest && _seconds % 30 == 15)
            _ = WatchKickAsync();
        UpdateVotes();
    }

    /// <summary>
    /// A RAM do navegador embutido, separada em o jogo das contas, as lives e o resto (o próprio navegador,
    /// a placa de vídeo, as extensões). É a memória privada de cada processo, a que pesa no PC.
    /// </summary>
    private async Task UpdateMemoryAsync()
    {
        if (_env is null || _memoryBusy)
            return;
        _memoryBusy = true;
        try
        {
            long game = 0, lives = 0, other = 0;
            int count = 0;
            foreach (var info in await _env.GetProcessExtendedInfosAsync())
            {
                long bytes;
                try
                {
                    using var process = Process.GetProcessById(info.ProcessInfo.ProcessId);
                    bytes = process.PrivateMemorySize64;
                }
                catch (ArgumentException)
                {
                    // O processo acabou de fechar.
                    continue;
                }
                count++;
                // Cada página roda num processo, e um iframe conta para a página que o abriu: o captcha do
                // Cloudflare na tela de login é do jogo; o da Kick, das lives.
                var hosts = info.AssociatedFrameInfos.Select(PageHost).ToList();
                if (hosts.Any(h => h.EndsWith("pokeidle.io")))
                    game += bytes;
                else if (hosts.Any(h => h.EndsWith("kick.com") || h.EndsWith("twitch.tv")))
                    lives += bytes;
                else
                    other += bytes;
            }
            _ram.Text = $"RAM ~{Gb(game + lives + other)} GB: jogo {Gb(game)} · lives {Gb(lives)} · navegador {Gb(other)}";
            _ram.ToolTipText = $"{count} processos do navegador embutido (memória privada, a que pesa no PC).\n" +
                               "Para aliviar: feche lives da Kick no botão kick de cada conta, ou desligue no ⏻ as contas que não está usando.";
        }
        catch
        {
            // O deck está fechando ou o navegador caiu: fica o último valor.
        }
        finally
        {
            _memoryBusy = false;
        }
    }

    private static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0");

    /// <summary>O site da página onde o quadro está (sobe dos iframes até a página).</summary>
    private static string PageHost(CoreWebView2FrameInfo frame)
    {
        while (frame.ParentFrameInfo is CoreWebView2FrameInfo parent)
            frame = parent;
        return Uri.TryCreate(frame.Source, UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    /// <summary>Liga ou desliga o "menos animações" em todas as contas, na hora (sem recarregar).</summary>
    private async Task SetReduceMotionAsync(bool reduce)
    {
        _config.ReduceMotion = reduce;
        _motionButton.Checked = reduce;
        SaveConfig();
        foreach (var pane in _panes)
        {
            pane.ReduceMotion = reduce;
            await pane.ApplyReduceMotionAsync();
        }
    }

    // ---------- Ações das contas ----------

    private void ShowAccountMenu(int index)
    {
        var pane = _panes[index];
        var menu = new ContextMenuStrip();
        menu.Items.Add(_config.Layout == DeckLayout.Focus && _config.Focused == index ? "Voltar para a grade" : "Ampliar no deck",
            null, (_, _) => ToggleFocus(index));
        menu.Items.Add("Recarregar a página", null, (_, _) => pane.Reload());
        menu.Items.Add(pane.Muted ? "Ligar o som" : "Desligar o som", null, (_, _) => pane.SetMuted(!pane.Muted));
        menu.Items.Add("Renomear…", null, (_, _) => RenameAccount(index));
        menu.Items.Add(pane.Off ? "Ligar esta conta" : "Desligar esta conta (fica no deck, sem rodar nada)",
            null, async (_, _) => await SetPaneOffAsync(index, !pane.Off));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Lives da Twitch/Kick nesta conta", null, async (_, _) => await ToggleLivesAsync(index))
        {
            Checked = _config.LivesAccounts.Contains(index + 1),
        });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(Cursor.Position);
    }

    /// <summary>
    /// Desliga ou religa uma conta. Desligada, ela fica no deck, mas a página do jogo fecha (e com ela
    /// as extensões, que só rodam no jogo), e as lives, as janelas dela e a conferência do voto param.
    /// </summary>
    private async Task SetPaneOffAsync(int index, bool off)
    {
        var pane = _panes[index];
        _config.Off[index] = off;
        SaveConfig();
        if (off)
        {
            CloseLives(index);
            _voteWindows[index]?.Close();
            foreach (var (key, window) in _extensionWindows.Where(w => w.Key.Pane == index).ToList())
            {
                window.Close();
                _extensionWindows.Remove(key);
            }
            _votes[index] = null;
            pane.SetOff(true);
        }
        else
        {
            pane.SetOff(false);
            if (pane.Core is CoreWebView2 core)
                core.Navigate(GameUrlForDeck);
            else if (_env is not null)
            {
                // Estava desligada desde que o deck abriu: o navegador dela é criado agora.
                await pane.InitializeAsync(_env, GameUrlForDeck, _config.Zoom[index], _config.Muted[index], SelfTestSetup, RepairExtensions);
                await RefreshExtensionButtonsAsync();
            }
            _voteRefreshAt[index] = DateTime.Now;
            if (_config.LivesAccounts.Contains(index + 1))
                await OpenLivesAsync(index);
        }
        UpdateBar();
    }

    private void RenameAccount(int index)
    {
        var name = Prompt.Ask(this, "Renomear conta", "Nome que aparece no deck (por exemplo, o nick do jogo):", _config.AccountNames[index]);
        if (string.IsNullOrWhiteSpace(name))
            return;
        _config.AccountNames[index] = name;
        _panes[index].AccountName = name;
        _panes[index].RefreshHeader();
        SaveConfig();
        UpdateBar();
    }

    // ---------- Extensões ----------

    private void OpenExtensions()
    {
        if (_env is null)
            return;
        using var dialog = new ExtensionsDialog(_panes, _store, _env, _root, () => _ = RefreshExtensionButtonsAsync());
        dialog.ShowDialog(this);
    }

    private async Task RefreshExtensionButtonsAsync()
    {
        foreach (var pane in _panes)
        {
            if (pane.Profile is not CoreWebView2Profile profile)
                continue;
            var extensions = await profile.GetBrowserExtensionsAsync();
            _hasSniper[pane.Index] = extensions.Any(e => e.Id == RotomSniper.Id && e.IsEnabled);
            _sniperButton.Visible = _hasSniper.Any(h => h);
            pane.SetExtensions(extensions
                .Where(e => e.IsEnabled)
                .Select(e => _store.Find(e.Id))
                .OfType<ExtensionInfo>()
                .Where(info => info.Popup is not null));
        }
    }

    private async Task<PopupWindow?> OpenExtensionPopupAsync(AccountPane pane, ExtensionInfo extension, Point anchor)
    {
        if (_env is null || extension.Popup is null)
            return null;

        var key = (pane.Index, extension.Id);
        if (extension.OpensWindow && _extensionWindows.TryGetValue(key, out var open) && !open.IsDisposed)
        {
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;
            open.Activate();
            return open;
        }

        var popup = await PopupWindow.CreateAsync(_env, pane.ProfileName,
            extension.OpensWindow ? new Size(1160, 760) : new Size(420, 580), closeOnDeactivate: !extension.OpensWindow);
        if (extension.OpensWindow)
        {
            var area = Screen.FromControl(this).WorkingArea;
            popup.Location = new Point(area.X + Math.Max(0, (area.Width - popup.Width) / 2), area.Y + Math.Max(0, (area.Height - popup.Height) / 2));
            _extensionWindows[key] = popup;
        }
        else
        {
            popup.PlaceBelow(anchor);
        }
        popup.WebView.CoreWebView2.Navigate($"chrome-extension://{extension.Id}/{extension.Popup}");
        popup.Show(this);
        return popup;
    }

    // ---------- Eco ----------

    private static readonly double[] ZoomLevels = { 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25 };

    /// <summary>Ao ligar, todas as contas ficam com o zoom da última conta clicada.</summary>
    private void SetEcho(bool on)
    {
        _echo = on;
        if (on)
            SetZoomAll(_panes[_lastClicked].WebView.ZoomFactor);
        _echoButton.Text = on ? "🔁 Eco ligado" : "🔁 Eco";
        _echoButton.ForeColor = on ? Theme.Vote : Theme.Text;
    }

    private void FillEchoMenu()
    {
        var items = _echoButton.DropDownItems;
        items.Clear();
        items.Add(_echo ? "Desligar o eco" : "Ligar o eco (um clique numa conta se repete nas outras)", null, (_, _) => SetEcho(!_echo));
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Zoom igual em todas as contas:") { Enabled = false });
        foreach (var level in ZoomLevels)
        {
            var item = new ToolStripMenuItem($"{level * 100:0}%") { Checked = _panes.All(p => Math.Abs(p.WebView.ZoomFactor - level) < 0.01) };
            item.Click += (_, _) => SetZoomAll(level);
            items.Add(item);
        }
    }

    /// <summary>
    /// Zoom (Ctrl + roda) ou som de uma conta mudou. O WebView2 só avisa zoom mudado pela pessoa, nunca
    /// o que o deck põe, então o eco espalhar o zoom não volta aqui.
    /// </summary>
    private void OnPaneSettingsChanged(AccountPane pane)
    {
        _config.Zoom[pane.Index] = pane.WebView.ZoomFactor;
        _config.Muted[pane.Index] = pane.Muted;
        // Com o eco ligado, o zoom de uma conta vale para todas.
        if (_echo)
            SetZoomAll(pane.WebView.ZoomFactor);
    }

    private void SetZoomAll(double zoom)
    {
        foreach (var pane in _panes)
        {
            // Só quem está diferente: assim a mudança não fica voltando de uma conta para outra.
            if (Math.Abs(pane.WebView.ZoomFactor - zoom) > 0.001)
                pane.WebView.ZoomFactor = zoom;
            _config.Zoom[pane.Index] = zoom;
        }
    }

    /// <summary>Repete nas outras contas o clique feito numa; avisa no cabeçalho de quem não tem esse botão.</summary>
    private async Task EchoAsync(AccountPane source, Echo.Click click)
    {
        _lastClicked = source.Index;
        if (!_echo)
            return;
        var script = Echo.ReplayScript(click);
        await Task.WhenAll(_panes.Where(p => p != source && p.Core is not null && !p.Off).Select(async pane =>
        {
            string result;
            try
            {
                result = await pane.WebView.ExecuteScriptAsync(script);
            }
            catch
            {
                result = "false";
            }
            if (result != "true")
                pane.Note($"eco: \"{(click.Text.Length > 0 ? click.Text : "esse botão")}\" não está nesta tela");
        }));
    }

    // ---------- Rotom Sniper ----------

    /// <summary>Ligar/desligar em todas as contas, e uma linha por conta com o estado dela (clicar troca só ela).</summary>
    private async void FillSniperMenu()
    {
        var items = _sniperButton.DropDownItems;
        items.Clear();
        items.Add("Ligar o sniper em todas as contas", null, async (_, _) => await SetSniperAsync(null, true));
        items.Add("Desligar o sniper em todas as contas", null, async (_, _) => await SetSniperAsync(null, false));
        items.Add(new ToolStripSeparator());
        var lines = new List<(int Index, ToolStripMenuItem Item)>();
        for (int i = 0; i < Accounts; i++)
        {
            if (!_hasSniper[i])
                continue;
            if (_panes[i].Off)
            {
                items.Add(new ToolStripMenuItem($"{_config.AccountNames[i]}: conta desligada") { Enabled = false });
                continue;
            }
            var item = new ToolStripMenuItem($"{_config.AccountNames[i]}: conferindo…") { Enabled = false };
            items.Add(item);
            lines.Add((i, item));
        }
        foreach (var (index, item) in lines)
        {
            bool? on = _panes[index].Core is CoreWebView2 core ? await RotomSniper.GetEnabledAsync(core) : null;
            if (on is not bool enabled)
            {
                item.Text = $"{_config.AccountNames[index]}: o Rotom não está na página (recarregue a conta)";
                continue;
            }
            item.Text = $"{_config.AccountNames[index]}: {(enabled ? "ligado" : "desligado")}";
            item.Checked = enabled;
            item.Enabled = true;
            item.Click += async (_, _) => await SetSniperAsync(index, !enabled);
        }
    }

    /// <summary>Liga ou desliga o sniper numa conta (ou em todas, com <paramref name="only"/> nulo).</summary>
    private async Task SetSniperAsync(int? only, bool enabled)
    {
        var failed = new List<string>();
        for (int i = 0; i < Accounts; i++)
        {
            if (!_hasSniper[i] || _panes[i].Off || only is int one && one != i)
                continue;
            if (_panes[i].Core is not CoreWebView2 core || !await RotomSniper.SetEnabledAsync(core, enabled))
                failed.Add(_config.AccountNames[i]);
        }
        // Onde deu certo, o próprio Rotom avisa na página do jogo ("AUTO-SNIPE ATIVADO/PAUSADO").
        if (failed.Count > 0)
            MessageBox.Show(this,
                $"Não consegui {(enabled ? "ligar" : "desligar")} o sniper em: {string.Join(", ", failed)}.\n\n" +
                "O deck mexe no Rotom pela página do jogo: recarregue a conta (⟳ no cabeçalho) e tente de novo.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ---------- Voto no TopIdle ----------

    /// <summary>A cada segundo: atualiza os botões de voto e confere no TopIdle as contas cuja hora chegou.</summary>
    private void UpdateVotes()
    {
        // O autoteste não sai para a internet; ele confere o voto por partes.
        if (_env is null || _selfTest)
            return;
        if (_topIdleGameId is null)
        {
            if (!_topIdleLookup && DateTime.Now >= _topIdleRetryAt)
                _ = FindTopIdleGameAsync();
            return;
        }
        for (int i = 0; i < Accounts; i++)
        {
            if (!_voteBusy[i] && DateTime.Now >= _voteRefreshAt[i] && _panes[i].Core is not null && !_panes[i].Off)
                _ = RefreshVoteAsync(i);
            RenderVote(i);
        }
        RenderVoteStatus();
    }

    private async Task<string?> FindTopIdleGameAsync()
    {
        _topIdleLookup = true;
        _topIdleRetryAt = DateTime.Now.AddMinutes(10);
        try
        {
            _topIdleGameId = await TopIdle.FindGameIdAsync("pokeidle.io");
        }
        catch
        {
            // Sem internet ou TopIdle fora do ar: tenta de novo daqui a pouco.
        }
        finally
        {
            _topIdleLookup = false;
        }
        return _topIdleGameId;
    }

    private async Task RefreshVoteAsync(int index)
    {
        if (_topIdleGameId is not string gameId || _panes[index].Core is not CoreWebView2 core)
            return;
        _voteBusy[index] = true;
        try
        {
            var state = await TopIdle.GetStateAsync(core.CookieManager, gameId);
            _votes[index] = state;
            // Confere de novo a cada 30 min, ou logo depois que o próximo voto liberar
            // (já votou mas o TopIdle ainda não deu a hora do próximo: de novo em 2 min).
            var next = DateTime.Now.AddMinutes(30);
            if (state.LoggedIn && !state.CanVote)
                next = state.NextVoteAt is not DateTime at ? DateTime.Now.AddMinutes(2)
                    : at > DateTime.Now ? (at.AddSeconds(3) < next ? at.AddSeconds(3) : next) : DateTime.Now.AddMinutes(1);
            _voteRefreshAt[index] = next;
        }
        catch
        {
            _voteRefreshAt[index] = DateTime.Now.AddMinutes(5);
        }
        finally
        {
            _voteBusy[index] = false;
        }
        RenderVote(index);
        RenderVoteStatus();
    }

    private void RenderVote(int index)
    {
        var pane = _panes[index];
        if (_topIdleGameId is null)
        {
            pane.HideVote();
            return;
        }
        var state = _votes[index];
        if (state is null)
            pane.SetVote("🗳 TopIdle", Theme.Muted, false, "Conferindo o voto desta conta no TopIdle…");
        else if (!state.LoggedIn)
            pane.SetVote("🗳 Votar", Theme.Text, false,
                "Votar no pokeidle pelo TopIdle (o jogo dá recompensa por voto). Na primeira vez, entre no TopIdle " +
                "com o Discord ou o Google na janela que abre: o login fica salvo nesta conta.");
        else if (state.Ready)
            pane.SetVote("🗳 Votar", Theme.Vote, true,
                $"Voto liberado no TopIdle ({state.User}). Clique para votar: quem vota é você, na página do TopIdle.");
        else if (state.NextVoteAt is DateTime at)
            pane.SetVote("🗳 " + FormatLeft(at - DateTime.Now), Theme.Muted, false,
                $"Esta conta já votou. O próximo voto libera {FormatWhen(at)}.");
        else
            // Acabou de votar: o TopIdle ainda não disse quando libera o próximo.
            pane.SetVote("🗳 ✓ Votou", Theme.Muted, false, "Esta conta já votou. Conferindo no TopIdle quando libera o próximo voto…");
    }

    /// <summary>A janela de voto viu o voto entrar: o botão vira "votou" e o TopIdle é consultado logo em seguida.</summary>
    private void MarkVoted(int index)
    {
        _votes[index] = (_votes[index] ?? new VoteState(true, true, null, null)) with { LoggedIn = true, CanVote = false, NextVoteAt = null };
        _voteRefreshAt[index] = DateTime.Now.AddSeconds(2);
        RenderVote(index);
        RenderVoteStatus();
    }

    private void RenderVoteStatus()
    {
        if (_topIdleGameId is null)
        {
            _voteStatus.Text = "";
            return;
        }
        var on = Enumerable.Range(0, Accounts).Where(i => !_panes[i].Off).ToList();
        var ready = on.Where(i => _votes[i]?.Ready == true).Select(i => _config.AccountNames[i]).ToList();
        var next = on.Select(i => _votes[i]).Where(v => v is { LoggedIn: true, Ready: false, NextVoteAt: not null }).Select(v => v!.NextVoteAt!.Value)
            .DefaultIfEmpty().Min();
        _voteStatus.ForeColor = ready.Count > 0 ? Theme.Vote : Theme.Muted;
        _voteStatus.Text = ready.Count > 0 ? "🗳 Voto liberado: " + string.Join(", ", ready)
            : next > DateTime.Now ? "🗳 Próximo voto em " + FormatLeft(next - DateTime.Now)
            : "🗳 Vote no TopIdle pelo botão de cada conta";
    }

    private static string FormatLeft(TimeSpan left) =>
        left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";

    private static string FormatWhen(DateTime at) =>
        at.Date == DateTime.Today ? $"hoje às {at:HH:mm}" : at.Date == DateTime.Today.AddDays(1) ? $"amanhã às {at:HH:mm}" : $"em {at:dd/MM} às {at:HH:mm}";

    /// <summary>
    /// Abre a página de voto do TopIdle no navegador da conta (onde fica o login do TopIdle dela), já com
    /// o nick do personagem. Quem vota é você; o deck percebe o voto registrado e atualiza o botão.
    /// </summary>
    private async Task OpenVoteAsync(AccountPane pane, string? url)
    {
        if (_env is null)
            return;
        int index = pane.Index;
        if (_voteWindows[index] is { IsDisposed: false } open)
        {
            if (url is not null)
                open.WebView.CoreWebView2?.Navigate(url);
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }

        if ((_topIdleGameId ?? await FindTopIdleGameAsync()) is not string gameId)
        {
            MessageBox.Show(this, "Não consegui falar com o TopIdle agora. Confira a internet e tente de novo; as contas seguem rodando.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        url ??= TopIdle.VoteUrl(gameId, await pane.ReadNickAsync());

        var window = await PopupWindow.CreateAsync(_env, pane.ProfileName, new Size(1100, 820), closeOnDeactivate: false);
        var area = Screen.FromControl(this).WorkingArea;
        window.Location = new Point(area.X + Math.Max(0, (area.Width - window.Width) / 2), area.Y + Math.Max(0, (area.Height - window.Height) / 2));
        string prefix = $"Votar com {pane.AccountName} · quem vota é você, nesta página do TopIdle";
        window.Text = prefix;
        var core = window.WebView.CoreWebView2;
        core.DocumentTitleChanged += (_, _) => window.Text = prefix;
        core.WebResourceResponseReceived += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (TopIdle.IsVoteRegistered(e.Request.Method, uri, e.Response?.StatusCode ?? 0, gameId))
            {
                prefix = $"✓ Voto registrado com {pane.AccountName}! Pode fechar esta janela";
                window.Text = prefix;
                MarkVoted(index);
            }
            else if (TopIdle.IsLoginChange(uri))
            {
                _voteRefreshAt[index] = DateTime.Now.AddSeconds(2);
            }
        };
        window.FormClosed += (_, _) =>
        {
            _voteWindows[index] = null;
            _voteRefreshAt[index] = DateTime.Now.AddSeconds(1);
        };
        _voteWindows[index] = window;
        core.Navigate(url);
        window.Show(this);
    }

    // ---------- Lives ----------

    private IEnumerable<string> TwitchUrls() => _selfTest
        ? new[] { "https://deck.test/twitch.html" }
        : _config.TwitchUrls();

    private string KickUrl(string channel) => _selfTest
        ? $"https://deck.test/kick.html?canal={Uri.EscapeDataString(channel)}"
        : $"https://kick.com/{channel}";

    private async Task OpenLivesAsync(int index)
    {
        if (_env is null || _lives[index] is not null || _presence[index] is not null || _panes[index].Off)
            return;
        var urls = TwitchUrls().ToList();

        if (!_selfTest && _config.TwitchMode == TwitchMode.Presence && _config.TwitchChannels.Count > 0)
        {
            var presence = new TwitchPresence(() => ReadTwitchLoginAsync(index), _config.TwitchChannels);
            presence.Changed += () =>
            {
                if (!IsDisposed)
                    BeginInvoke(UpdateBar);
            };
            _presence[index] = presence;
            presence.Start();
            // Sem login da Twitch nesta conta ainda: uma aba para entrar (depois a presença usa esse login).
            // O que vale é o token: o cookie com o nick nem sempre existe, e a presença acha o nick pelo token.
            if ((await ReadTwitchLoginAsync(index)).Token is null)
                urls.Insert(0, "https://www.twitch.tv/login");
        }

        int kickSlots = Math.Min(KickLive.Slots, _config.KickChannels.Count);
        if (urls.Count > 0 || kickSlots > 0)
        {
            _lives[index] = await LivesWindow.CreateAsync(_env, _panes[index].ProfileName, _config.AccountNames[index], urls, kickSlots, SelfTestSetup);
            var window = _lives[index]!;
            window.NextRequested += () => ShowNextKick(index);
            window.PointsHeight = _config.KickPointsHeight;
            window.PointsHeightChanged += height =>
            {
                _config.KickPointsHeight = height;
                SaveConfig();
                foreach (var other in _lives.OfType<LivesWindow>())
                    other.PointsHeight = height;
            };
            ApplyKick();
            // Confere logo quem está no ar (o autoteste não sai para a internet: abre as primeiras da lista).
            _kickCheckAt = DateTime.Now;
        }
        UpdateBar();
    }

    /// <summary>Pergunta à Kick quais canais da lista estão no ar (a cada 2 min, enquanto houver lives abertas).</summary>
    private async Task CheckKickAsync()
    {
        _kickChecking = true;
        _kickCheckAt = DateTime.Now.AddMinutes(2);
        try
        {
            var channels = _config.KickChannels.ToList();
            var live = await Task.WhenAll(channels.Select(KickLive.IsLiveAsync));
            for (int i = 0; i < channels.Count; i++)
                _kickLive[channels[i]] = live[i];
        }
        finally
        {
            _kickChecking = false;
        }
        ApplyKick();
    }

    /// <summary>
    /// A cada 30 s, se as lives da Kick abertas estão mesmo tocando (vídeo parado não rende pontos); a que
    /// caiu e não voltou sozinha é recarregada.
    /// </summary>
    private async Task WatchKickAsync()
    {
        if (_kickWatching)
            return;
        _kickWatching = true;
        try
        {
            foreach (var window in _lives.OfType<LivesWindow>().ToList())
                if (!window.IsDisposed)
                    await window.WatchKickAsync();
        }
        finally
        {
            _kickWatching = false;
        }
        UpdateBar();
    }

    /// <summary>Deixa abertas, em cada janela de lives, só as lives da Kick que estão no ar (até duas).</summary>
    private void ApplyKick()
    {
        // Antes da primeira resposta da Kick não abre nada: a live pode estar offline.
        if (!_selfTest && _kickLive.Count == 0)
        {
            UpdateBar();
            return;
        }
        for (int i = 0; i < Accounts; i++)
        {
            // Sem as fechadas no botão kick da conta, e cada uma levou a vaga dela junto.
            var kick = _config.Kick[i];
            var channels = _config.KickChannels.Where(c => !kick.Closed.Contains(c));
            _lives[i]?.SetKick(KickLive.Pick(channels, _kickLive).Take(kick.Slots).Select(c => (c, KickUrl(c))).ToList());
        }
        UpdateBar();
    }

    /// <summary>
    /// Fecha no kick a live de <paramref name="channel"/> nesta conta (ou todas, com nulo). A vaga fica em
    /// branco: a conferência de 2 em 2 min não abre outra live nela até abrir de novo.
    /// </summary>
    private void CloseKick(int index, string? channel)
    {
        var kick = _config.Kick[index];
        foreach (var shut in channel is null ? KickOpen(index) : new List<string> { channel })
            if (!kick.Closed.Contains(shut))
                kick.Closed.Add(shut);
        kick.Slots = channel is null ? 0 : Math.Max(0, kick.Slots - 1);
        SaveConfig();
        ApplyKick();
        _panes[index].Note(channel is null ? "Kick: fechei todas as lives" : $"Kick: fechei a live de {channel}");
    }

    /// <summary>Abre de novo a live de <paramref name="channel"/> fechada no kick (ou todas, com nulo), com a vaga dela.</summary>
    private void ReopenKick(int index, string? channel)
    {
        var kick = _config.Kick[index];
        if (channel is null)
        {
            kick.Closed.Clear();
            kick.Slots = KickLive.Slots;
        }
        else if (kick.Closed.Remove(channel))
        {
            kick.Slots = Math.Min(KickLive.Slots, kick.Slots + 1);
        }
        SaveConfig();
        ApplyKick();
        _panes[index].Note(channel is null ? "Kick: as lives voltam a abrir" : $"Kick: {channel} volta a abrir (se estiver no ar)");
    }

    private void CloseLives(int index)
    {
        _presence[index]?.Dispose();
        _presence[index] = null;
        if (_lives[index] is LivesWindow window)
        {
            window.AllowClose = true;
            window.Close();
            _lives[index] = null;
        }
        UpdateBar();
    }

    /// <summary>
    /// O login da Twitch salvo no perfil da conta (o mesmo que o site da Twitch usa no chat). Pode ser
    /// chamado de qualquer thread; o navegador embutido só é usado na thread da interface.
    /// </summary>
    private Task<(string? Token, string? Login)> ReadTwitchLoginAsync(int index)
    {
        var result = new TaskCompletionSource<(string?, string?)>();
        async void Read()
        {
            try
            {
                if (_panes[index].Core is not CoreWebView2 core)
                {
                    result.TrySetResult((null, null));
                    return;
                }
                var cookies = await core.CookieManager.GetCookiesAsync("https://www.twitch.tv");
                var token = cookies.FirstOrDefault(c => c.Name == "auth-token")?.Value;
                var login = cookies.FirstOrDefault(c => c.Name == "login")?.Value;
                // O que foi visto, para o menu explicar quando o login não aparece.
                _twitchSeen[index] = $"{cookies.Count} cookies da Twitch{(string.IsNullOrEmpty(token) ? ", sem o auth-token do login" : ", com o auth-token")}";
                result.TrySetResult(string.IsNullOrEmpty(token) ? (null, null) : (token, login));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        }
        try
        {
            if (InvokeRequired)
                BeginInvoke(Read);
            else
                Read();
        }
        catch (InvalidOperationException)
        {
            // O deck está fechando.
            result.TrySetResult((null, null));
        }
        return result.Task;
    }

    private async Task ToggleLivesAsync(int index)
    {
        int account = index + 1;
        if (_config.LivesAccounts.RemoveAll(a => a == account) > 0)
        {
            CloseLives(index);
        }
        else
        {
            _config.LivesAccounts.Add(account);
            _config.LivesAccounts.Sort();
            await OpenLivesAsync(index);
        }
        SaveConfig();
        UpdateBar();
    }

    private void FillLivesMenu()
    {
        var items = _livesButton.DropDownItems;
        items.Clear();
        items.Add(new ToolStripMenuItem("Marque a conta vinculada à sua Twitch/Kick (a main):") { Enabled = false });
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            string state = _panes[i].Off ? " — conta desligada (as lives voltam ao ligar)"
                : _lives[i] is not null || _presence[i] is not null ? " — " + LivesSummary(i) : "";
            var item = new ToolStripMenuItem(_config.AccountNames[i] + state) { Checked = _config.LivesAccounts.Contains(i + 1) };
            item.Click += async (_, _) => await ToggleLivesAsync(index);
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        items.Add("Mostrar as lives (para entrar na Twitch/Kick ou resgatar pontos da Kick)", null, (_, _) => ShowLives())
            .Enabled = _lives.Any(l => l is not null);
        items.Add("Canais…", null, async (_, _) => await EditChannelsAsync());
    }

    private void ShowLives()
    {
        int order = 0;
        foreach (var window in _lives.OfType<LivesWindow>())
            window.Reveal(Screen.FromControl(this).WorkingArea, order: order++);
    }

    /// <summary>
    /// Os chats da Kick da conta, para resgatar os pontos; "Próxima conta" na janela passa para a seguinte
    /// que tem live da Kick aberta.
    /// </summary>
    private void ShowKick(int index)
    {
        var withKick = Enumerable.Range(0, Accounts).Where(i => _lives[i]?.KickTabs.Any() == true).ToList();
        if (_lives[index] is not LivesWindow window || !withKick.Contains(index))
            return;
        foreach (int i in withKick)
            _lives[i]!.HasNext = withKick.Count > 1;
        window.Reveal(Screen.FromControl(this).WorkingArea, window.KickTabs.First().Tab);
    }

    private void ShowNextKick(int index)
    {
        for (int step = 1; step < Accounts; step++)
        {
            int next = (index + step) % Accounts;
            if (_lives[next]?.KickTabs.Any() != true)
                continue;
            _lives[index]?.Park();
            ShowKick(next);
            return;
        }
    }

    private async Task EditChannelsAsync()
    {
        using var dialog = new ChannelsDialog(_config.TwitchChannels, _config.KickChannels, _config.TwitchMode);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _config.TwitchChannels = dialog.TwitchChannels;
        _config.KickChannels = dialog.KickChannels;
        _config.TwitchMode = dialog.TwitchMode;
        SaveConfig();
        _kickLive.Clear();
        for (int i = 0; i < Accounts; i++)
        {
            if (_lives[i] is null && _presence[i] is null)
                continue;
            CloseLives(i);
            await OpenLivesAsync(i);
        }
    }

    private void SaveConfig()
    {
        if (!_selfTest)
            _config.Save(_configPath);
    }

    // ---------- Autoteste ----------

    private static string SelfTestReport => Path.Combine(Path.GetTempPath(), "PokeIdleDeck-selftest.txt");

    /// <summary>Páginas de teste (servidas como https://deck.test/) e uma extensão de teste.</summary>
    private void PrepareSelfTestFiles()
    {
        var site = Path.Combine(_root, "site");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(site, "game.html"),
            "<!doctype html><title>Jogo-teste</title><body style='background:#234;color:#fff'>jogo<nav>" +
            "<button onclick='window.__mapa = (window.__mapa || 0) + 1'>Mapa</button> " +
            "<button onclick='window.__outro = (window.__outro || 0) + 1'>Outro</button></nav></body>");
        File.WriteAllText(Path.Combine(site, "twitch.html"), "<!doctype html><title>Twitch-teste</title>");
        // Live de teste com vídeo "tocando" (o tempo anda); a do yetz fica parada, sem vídeo para tocar.
        File.WriteAllText(Path.Combine(site, "kick.html"),
            "<!doctype html><title>Kick-teste</title><video muted></video><div id=channel-chatroom><div id=chatroom-messages>msgs</div>botão dos pontos</div><script>" +
            "if (!location.search.includes('yetz')) { const v = document.querySelector('video');" +
            " Object.defineProperty(v, 'paused', { get: () => false });" +
            " Object.defineProperty(v, 'currentTime', { get: () => performance.now() / 1000 }); }</script>");

        var extension = Path.Combine(_root, "ext-teste");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Combine(extension, "manifest.json"),
            """
            {
              "manifest_version": 3,
              "name": "Extensao teste",
              "version": "1.0",
              "permissions": [ "storage" ],
              "action": { "default_popup": "popup.html" },
              "content_scripts": [ { "matches": [ "https://deck.test/*" ], "js": [ "cs.js" ] } ]
            }
            """);
        File.WriteAllText(Path.Combine(extension, "cs.js"), "document.documentElement.dataset.ext = 'ok';");
        File.WriteAllText(Path.Combine(extension, "popup.html"), "<!doctype html><title>Popup-teste</title><body>popup</body>");

        // Como o Rotom Sniper: ícone sem popup, que abre uma página da própria extensão.
        var withPage = Path.Combine(_root, "ext-pagina");
        Directory.CreateDirectory(withPage);
        File.WriteAllText(Path.Combine(withPage, "manifest.json"),
            """
            { "manifest_version": 3, "name": "Extensao pagina", "version": "1.0",
              "action": { "default_title": "abrir" }, "background": { "service_worker": "bg.js" } }
            """);
        File.WriteAllText(Path.Combine(withPage, "bg.js"), "");
        File.WriteAllText(Path.Combine(withPage, "cockpit.html"), "<!doctype html><title>Cockpit-teste</title>");
    }

    /// <summary>
    /// Autoteste (<c>--self-test</c>): abre 4 contas de teste e confere sessões separadas, layouts,
    /// conta escondida rodando no ritmo normal, extensão por conta e popup dela, lives escondidas,
    /// minimizar e tela cheia. Grava o resultado em %TEMP%\PokeIdleDeck-selftest.txt e fecha.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        var log = new List<string>();
        void Check(string name, bool ok, string detail = "") => log.Add($"{(ok ? "OK  " : "FALHOU")} {name} {detail}");
        async Task<bool> WaitFor(Func<bool> condition, int seconds)
        {
            var deadline = DateTime.Now.AddSeconds(seconds);
            while (!condition() && DateTime.Now < deadline)
                await Task.Delay(250);
            return condition();
        }
        Task<string> Eval(AccountPane pane, string script) => pane.WebView.ExecuteScriptAsync(script);
        bool Shape(int cols, int rows) => _panes.All(p => p.Visible && p.WebView.Height > 100
            && p.Bounds == Cell(_grid.ClientRectangle, p.Index % cols, p.Index / cols, cols, rows));
        string Detail() => string.Join(" | ", _panes.Select(p => $"{p.Index + 1}:{(p.Visible ? p.Bounds.ToString() : "escondida")}"));

        try
        {
            bool loaded = await WaitFor(() => _panes.Take(3).All(p => p.Status == PaneStatus.Ok), 40);
            Check("conta desligada ao abrir o deck não carrega nada",
                loaded && _panes[3].Off && _panes[3].Core is null && _panes[3].StatusText == "desligada",
                $"conta4: desligada={_panes[3].Off} navegador={(_panes[3].Core is null ? "não criado" : "criado")} status='{_panes[3].StatusText}'");
            await SetPaneOffAsync(3, false);
            loaded = await WaitFor(() => _panes.All(p => p.Status == PaneStatus.Ok), 40);
            Check("4 contas abriram (a 4ª ligada no ⏻)", loaded && _panes.All(p => p.Core?.DocumentTitle == "Jogo-teste"),
                string.Join(", ", _panes.Select(p => p.Status)));

            // Menos animações: o jogo vê o "reduzir movimento" do sistema ligado; o botão desliga e religa na hora.
            const string ReducedMotion = "matchMedia('(prefers-reduced-motion: reduce)').matches";
            async Task<string> Motion() => string.Join(",", await Task.WhenAll(_panes.Select(p => Eval(p, ReducedMotion))));
            var motionStart = await Motion();
            await SetReduceMotionAsync(false);
            var motionOff = await Motion();
            await SetReduceMotionAsync(true);
            var motionOn = await Motion();
            Check("menos animações no jogo (e o botão liga e desliga na hora)",
                motionStart == "true,true,true,true" && motionOff == "false,false,false,false" && motionOn == "true,true,true,true"
                && _motionButton.Checked && _config.ReduceMotion,
                $"ao abrir={motionStart} desligado={motionOff} religado={motionOn}");

            // Conta parada na tela de login: o cabeçalho avisa (o captcha de lá vai juntando memória).
            await Eval(_panes[1], "document.body.insertAdjacentHTML('beforeend', '<div id=portal></div><div id=login></div>'); 1");
            await _panes[1].CheckLoginScreenAsync();
            string loginInfo = _panes[1].HeaderInfo;
            await Eval(_panes[1], "document.querySelector('#portal').remove(); document.querySelector('#login').remove(); 1");
            await _panes[1].CheckLoginScreenAsync();
            Check("aviso de conta parada na tela de login",
                loginInfo.Contains("tela de login") && !_panes[1].HeaderInfo.Contains("tela de login") && !_panes[0].HeaderInfo.Contains("tela de login"),
                $"com login='{loginInfo}' sem='{_panes[1].HeaderInfo}'");

            await Eval(_panes[0], "localStorage.setItem('quem', 'conta1')");
            var own = await Eval(_panes[0], "localStorage.getItem('quem')");
            var other = await Eval(_panes[1], "localStorage.getItem('quem')");
            Check("cada conta com sessão própria", own == "\"conta1\"" && other == "null", $"conta1={own} conta2={other}");

            SetLayout(DeckLayout.Grid);
            await Task.Delay(500);
            Check("grade 2x2", Shape(2, 2), Detail());

            SetLayout(DeckLayout.Columns);
            await Task.Delay(500);
            Check("colunas", Shape(4, 1), Detail());
            SetLayout(DeckLayout.Grid);

            await Eval(_panes[0], "window.__t = 0; setInterval(() => window.__t++, 100); 1");
            FocusPane(1);
            await Task.Delay(3000);
            bool focused = _panes[1].Visible && _panes[1].Bounds == Cell(_grid.ClientRectangle, 0, 0, 1, 1)
                && _panes.Where(p => p.Index != 1).All(p => !p.Visible);
            Check("ampliar conta 2", focused, Detail());
            int.TryParse(await Eval(_panes[0], "window.__t"), out int ticks);
            Check("conta escondida continua no ritmo normal", ticks >= 20, $"{ticks} ticks em 3 s (normal ~30, desacelerada ~3)");
            ToggleFocus(1);
            await Task.Delay(500);
            Check("voltar para a grade", _config.Layout == DeckLayout.Grid && Shape(2, 2), Detail());

            // Eco: um clique de verdade (pelo DevTools, como o mouse) na conta 1 se repete nas outras;
            // a conta 3 está "noutra tela" (sem o botão Mapa) e não clica nada.
            async Task RealClick(AccountPane pane, string selector)
            {
                var xy = JsonSerializer.Deserialize<double[]>(await Eval(pane,
                    $"(() => {{ const r = document.querySelector('{selector}').getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; }})()"))!;
                foreach (var type in new[] { "mousePressed", "mouseReleased" })
                    await pane.Core!.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                        JsonSerializer.Serialize(new { type, x = xy[0], y = xy[1], button = "left", clickCount = 1 }));
            }
            async Task<string> Clicks() => string.Join(",", await Task.WhenAll(_panes.Select(p => Eval(p, "window.__mapa || 0"))));
            await Eval(_panes[2], "document.querySelector('nav button').remove(); 1");
            SetEcho(true);
            await RealClick(_panes[0], "nav button");
            await Task.Delay(1500);
            var echoed = await Clicks();
            Check("eco: clique na conta 1 se repete nas outras (e não volta)",
                echoed == "1,1,0,1" && _panes[2].NoteText?.Contains("Mapa") == true && _panes[1].NoteText is null,
                $"cliques por conta={echoed} aviso conta3='{_panes[2].NoteText}'");

            // Zoom posto pelo programa não gera o aviso do WebView2 (só o Ctrl + roda da pessoa): avisa à mão.
            _panes[1].WebView.ZoomFactor = 0.8;
            OnPaneSettingsChanged(_panes[1]);
            bool zoomSynced = await WaitFor(() => _panes.All(p => Math.Abs(p.WebView.ZoomFactor - 0.8) < 0.01), 5);
            Check("eco: zoom de uma conta vale para todas", zoomSynced, string.Join(", ", _panes.Select(p => $"{p.WebView.ZoomFactor:0.00}")));
            SetZoomAll(1);

            SetEcho(false);
            await RealClick(_panes[0], "nav button");
            await Task.Delay(1000);
            var alone = await Clicks();
            Check("eco desligado: o clique fica só na conta", alone == "2,1,0,1", $"cliques por conta={alone}");

            var folder = _store.CopyUnpacked(Path.Combine(_root, "ext-teste"));
            var info = await _store.InstallAsync(_panes[0].Profile!, folder);
            await RefreshExtensionButtonsAsync();
            _panes[0].Reload();
            _panes[1].Reload();
            await Task.Delay(500);
            await WaitFor(() => _panes[0].Status == PaneStatus.Ok && _panes[1].Status == PaneStatus.Ok, 15);
            await Task.Delay(500);
            var ext0 = await Eval(_panes[0], "document.documentElement.dataset.ext || null");
            var ext1 = await Eval(_panes[1], "document.documentElement.dataset.ext || null");
            Check("extensão roda na conta 1 e só nela", ext0 == "\"ok\"" && ext1 == "null", $"conta1={ext0} conta2={ext1} id={info.Id}");
            Check("menos animações continua depois de recarregar",
                await Eval(_panes[0], ReducedMotion) == "true" && await Eval(_panes[1], ReducedMotion) == "true");
            Check("botão da extensão no cabeçalho da conta 1", _panes[0].ExtensionButtonCount == 1 && _panes[1].ExtensionButtonCount == 0);

            // O liga/desliga do sniper, com a extensão de teste no lugar do Rotom: grava no storage dela
            // de dentro da página, lê de volta, e a conta 2 (sem a extensão) responde "não carregado".
            bool? sniperBefore = await RotomSniper.GetEnabledAsync(_panes[0].Core!, info.Id);
            bool sniperOff = await RotomSniper.SetEnabledAsync(_panes[0].Core!, false, info.Id);
            bool? sniperAfterOff = await RotomSniper.GetEnabledAsync(_panes[0].Core!, info.Id);
            bool sniperOn = await RotomSniper.SetEnabledAsync(_panes[0].Core!, true, info.Id);
            bool? sniperAfterOn = await RotomSniper.GetEnabledAsync(_panes[0].Core!, info.Id);
            bool? sniperNone = await RotomSniper.GetEnabledAsync(_panes[1].Core!, info.Id);
            Check("liga/desliga do sniper pelo deck",
                sniperBefore == true && sniperOff && sniperAfterOff == false && sniperOn && sniperAfterOn == true && sniperNone is null,
                $"antes={sniperBefore} desligar={sniperOff}->{sniperAfterOff} ligar={sniperOn}->{sniperAfterOn} conta2={sniperNone?.ToString() ?? "sem extensão"}");

            var popup = await OpenExtensionPopupAsync(_panes[0], info, new Point(Right - 40, Top + 120));
            bool popupOk = popup is not null && await WaitFor(() => popup.WebView.CoreWebView2?.DocumentTitle == "Popup-teste", 10);
            Check("popup da extensão abre", popupOk, $"titulo '{popup?.WebView.CoreWebView2?.DocumentTitle}'");
            popup?.Close();

            // Trazer configurações: grava na storage da extensão na conta 1 e copia para a conta 4, como faria
            // ao abrir o deck (a conta 4 ainda não abriu o banco dessa extensão, então dá para gravar agora).
            var writer = await OpenExtensionPopupAsync(_panes[0], info, new Point(Right - 40, Top + 120));
            await WaitFor(() => writer?.WebView.CoreWebView2?.DocumentTitle == "Popup-teste", 10);
            await writer!.WebView.ExecuteScriptAsync("chrome.storage.local.set({ regra: 'da conta 1' }); 1");
            await Task.Delay(1000);
            writer.Close();
            var source = StorageImport.FindSources(_root, info.Id, new[] { (_panes[0].ProfileName, _panes[0].AccountName) })
                .FirstOrDefault(s => s.Label.StartsWith("Deck"));
            if (source is not null)
                StorageImport.Queue(_root, new PendingImport(info.Id, source.Folder, source.Label, _panes[3].ProfileName));
            var applied = StorageImport.ApplyPending(_root);
            await _store.InstallAsync(_panes[3].Profile!, folder);
            var reader = await OpenExtensionPopupAsync(_panes[3], info, new Point(Right - 40, Top + 120));
            await WaitFor(() => reader?.WebView.CoreWebView2?.DocumentTitle == "Popup-teste", 10);
            await reader!.WebView.ExecuteScriptAsync("window.__r = 'pendente'; chrome.storage.local.get('regra', v => window.__r = v.regra || 'vazio'); 1");
            string copied = "\"pendente\"";
            for (int i = 0; i < 20 && copied.Contains("pendente"); i++)
            {
                await Task.Delay(250);
                copied = await reader.WebView.ExecuteScriptAsync("window.__r");
            }
            reader.Close();
            Check("trazer configurações da extensão (conta 1 para a conta 4)", copied == "\"da conta 1\"",
                $"leu {copied}; origem {(source is null ? "não achada" : source.Label)}; {string.Join("; ", applied.Select(a => a.Result))}");

            // Dados trazidos de outro PC: o perfil ainda aponta para a pasta da extensão de lá.
            var fakeProfile = Path.Combine(_root, "perfil-de-outro-pc");
            Directory.CreateDirectory(fakeProfile);
            File.WriteAllText(Path.Combine(fakeProfile, "Secure Preferences"), JsonSerializer.Serialize(new
            {
                extensions = new
                {
                    settings = new Dictionary<string, object>
                    {
                        [info.Id] = new { location = 4, path = @"C:\Users\OutroUsuario\AppData\Local\PokeIdleMulti\extensions\" + Path.GetFileName(folder) },
                        ["daquimesmo"] = new { location = 4, path = folder },
                        ["donavegador"] = new { location = 5, path = @"C:\nao\existe" },
                    },
                },
            }));
            var moved = _store.MovedExtensions(fakeProfile);
            Check("dados de outro PC: acha a extensão que mudou de pasta",
                moved.Count == 1 && moved[0].Id == info.Id && moved[0].Folder == folder,
                string.Join(", ", moved.Select(m => $"{m.Id} -> {m.Folder}")));

            var page = ExtensionStore.Describe(Path.Combine(_root, "ext-pagina"));
            Check("extensão sem popup ganha botão que abre a página dela", page.OpensWindow && page.Popup == "cockpit.html",
                $"popup={page.Popup} janela={page.OpensWindow}");

            var before = Application.OpenForms.OfType<PopupWindow>().ToList();
            await Eval(_panes[2], "window.open('https://deck.test/kick.html', '_blank', 'width=500,height=400'); 1");
            bool opened = await WaitFor(() => Application.OpenForms.OfType<PopupWindow>().Except(before).Any(), 10);
            Check("janela aberta pelo site (login por popup) fica no deck", opened);
            foreach (var window in Application.OpenForms.OfType<PopupWindow>().Except(before).ToList())
                window.Close();

            // Twitch-teste + as 2 primeiras da Kick (o autoteste não pergunta à Kick quem está no ar).
            bool livesOk = await WaitFor(() => _lives[0] is LivesWindow w && w.Count == 3 && w.Loaded == w.Count, 20);
            var lives0 = _lives[0];
            bool parked = lives0 is not null && lives0.Visible && lives0.Parked
                && !Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(lives0.Bounds));
            Check("lives da conta 1 carregadas, rodando fora da tela",
                livesOk && parked && _lives.Skip(1).All(l => l is null),
                lives0 is null ? "sem janela" : $"{lives0.Loaded}/{lives0.Count} Kick={string.Join(",", lives0.KickChannels)} {lives0.Bounds}");
            var visibility = lives0 is null ? new List<string>() : await lives0.EvalAsync("document.visibilityState");
            Check("todas as páginas das lives visíveis para o navegador (vídeo de trás toca)",
                visibility.Count == 3 && visibility.All(v => v == "\"visible\""), string.Join(", ", visibility));

            // Fora da tela o chat da Kick fica escondido (gasta menos); nos pontos aparece só o rodapé, sem as
            // mensagens; fechar esconde de novo.
            const string ChatShown = "(() => { const c = document.querySelector('#channel-chatroom'); return c ? getComputedStyle(c).display + ' ' " +
                "+ getComputedStyle(document.querySelector('#chatroom-messages')).visibility : 'sem chat'; })()";
            async Task<string> Chat() => lives0 is null ? "" : string.Join(",", await lives0.EvalAsync(ChatShown));
            string chatParked = await Chat();
            // 💰 Pontos da Kick: só as colunas de chat lado a lado (a página fica na largura de computador,
            // encostada à direita), com a altura dos pontos (a tela baixa limita), no meio da tela.
            ShowKick(0);
            await Task.Delay(300);
            string chatShown = await Chat();
            var screenArea = lives0 is null ? Rectangle.Empty : Screen.FromControl(lives0).WorkingArea;
            var columns = lives0?.KickLayout.ToList() ?? new();
            int chatWidth = lives0?.LogicalToDeviceUnits(340) ?? 0, pageWidth = lives0?.LogicalToDeviceUnits(1100) ?? 0;
            bool fits = lives0 is not null && lives0.Bounds.Height == Math.Min(screenArea.Height, lives0.LogicalToDeviceUnits(640))
                && screenArea.Contains(lives0.Bounds) && Math.Abs(lives0.Bounds.Top - screenArea.Top - (screenArea.Bottom - lives0.Bounds.Bottom)) <= 1;
            bool sideBySide = columns.Count == 2 && columns.All(c => c.Frame.Width == chatWidth && c.View.Width == pageWidth
                && c.View.Right == c.Frame.Width && c.View.Height == c.Frame.Height) && columns[0].Frame.Right == columns[1].Frame.Left;
            string layout = lives0 is null ? "" : $"janela={lives0.Bounds} área={screenArea} colunas={string.Join(" ", columns.Select(c => $"{c.Frame}/{c.View}"))}";
            lives0?.Close();
            await Task.Delay(300);
            string chatAgain = await Chat();
            Activate();
            Check("chat da Kick escondido fora da tela, e nos pontos só o rodapé (sem as mensagens)",
                chatParked == "\"sem chat\",\"none visible\",\"none visible\"" && chatShown == "\"sem chat\",\"block hidden\",\"block hidden\""
                && chatAgain == chatParked && lives0 is { Parked: true, Visible: true },
                $"fora={chatParked} mostrando={chatShown} fechou={chatAgain}");
            Check("pontos da Kick: os 2 chats lado a lado, com a altura dos pontos", fits && sideBySide, layout);

            // Propaganda cortada nas lives: o pedido a um site de anúncio volta 403 do próprio deck.
            if (lives0 is not null)
                await lives0.EvalAsync("window.__ads = 'pendente'; fetch('https://pagead2.googlesyndication.com/deck-teste')" +
                                       ".then(r => window.__ads = r.status, () => window.__ads = 'erro'); 1");
            await Task.Delay(1500);
            var ads = lives0 is null ? new List<string>() : await lives0.EvalAsync("window.__ads");
            Check("lives sem propaganda (anúncio bloqueado; vídeo, Kick e Cloudflare passam)",
                ads.Count == 3 && ads.All(a => a == "403")
                && AdBlock.Blocked("https://pagead2.googlesyndication.com/x") && AdBlock.Blocked("https://js.stripe.com/v3")
                && !AdBlock.Blocked("https://kick.com/biscoitao") && !AdBlock.Blocked("https://abc.playback.live-video.net/api/video/v1/x.m3u8")
                && !AdBlock.Blocked("https://challenges.cloudflare.com/x") && !AdBlock.Blocked("https://notgooglesyndication.com/"),
                string.Join(",", ads));

            Check("Kick: abre só as 2 primeiras da lista que estão no ar",
                KickLive.Pick(new[] { "biscoitao", "axt", "clovao", "outro" },
                    new Dictionary<string, bool?> { ["biscoitao"] = false, ["axt"] = true, ["clovao"] = true, ["outro"] = true })
                    .SequenceEqual(new[] { "axt", "clovao" })
                && KickLive.Pick(new[] { "biscoitao", "axt", "clovao" },
                    new Dictionary<string, bool?> { ["biscoitao"] = true, ["axt"] = false, ["clovao"] = true })
                    .SequenceEqual(new[] { "biscoitao", "clovao" })
                && KickLive.Pick(new[] { "biscoitao", "axt", "clovao" }, new Dictionary<string, bool?>())
                    .SequenceEqual(new[] { "biscoitao", "axt" }));
            Check("Kick: resposta da API (no ar / offline)",
                KickLive.ParseLive("{\"livestream\":null}") == false && KickLive.ParseLive("{\"livestream\":{\"is_live\":true}}") == true
                && KickLive.ParseLive("{}") is null && KickLive.ParseLive("<html>") is null);

            // A 2ª da lista sai do ar: a vaga dela vai para a próxima no ar, e a 1ª fica sem recarregar.
            if (lives0 is not null)
                await lives0.EvalAsync("window.__marca = 'mesma'; 1");
            _kickLive["biscoitao"] = true;
            _kickLive["yetz"] = false;
            _kickLive["surskity"] = true;
            ApplyKick();
            bool swapped = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count
                && lives0.KickChannels.SequenceEqual(new[] { "biscoitao", "surskity" }), 15);
            var kept = lives0 is null ? new List<string>() : await lives0.EvalAsync("window.__marca || 'recarregou'");
            Check("Kick: live que sai do ar fecha e a próxima no ar entra",
                swapped && kept.Count == 3 && kept[1] == "\"mesma\"" && kept[2] == "\"recarregou\"",
                lives0 is null ? "sem janela" : $"Kick={string.Join(",", lives0.KickChannels)} marca={string.Join(",", kept)}");

            // Botão kick na conta 1: escolhe qual fechar, até a 1ª da lista (biscoitao); a outra fica sem
            // recarregar, e a conferência de 2 em 2 min não põe outra na vaga, nem com mais uma no ar.
            string KickNow() => string.Join(",", lives0?.KickChannels ?? Enumerable.Empty<string>());
            if (lives0 is not null)
                await lives0.EvalAsync("window.__marca = 'mesma'; 1");
            string kickBefore = string.Join(",", _panes.Select(p => p.KickState));
            var menuBefore = KickMenuItems(0).Select(i => i.Text).ToList();
            CloseKick(0, "biscoitao");
            bool chosen = await WaitFor(() => lives0 is not null && lives0.Count == 2 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.SequenceEqual(new[] { "surskity" }), 10);
            var stayed = lives0 is null ? new List<string>() : await lives0.EvalAsync("window.__marca || 'recarregou'");
            var menuAfter = KickMenuItems(0).Select(i => i.Text).ToList();
            // Todas no ar: a vaga do biscoitao não abre outra (fica 1 live, a mais de cima da lista sem as fechadas).
            _kickLive["yetz"] = true;
            ApplyKick();
            bool noRefill = await WaitFor(() => lives0 is not null && lives0.Count == 2 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.SequenceEqual(new[] { "yetz" }), 10);
            Check("kick: escolhe qual live fechar (até a 1ª da lista) e a vaga não abre outra",
                kickBefore == "ligado,apagado,apagado,apagado"
                && menuBefore.Take(3).SequenceEqual(new[] { "Fechar a live de biscoitao", "Fechar a live de surskity", "Fechar todas (nenhuma live da Kick nesta conta)" })
                && chosen && stayed.Count == 2 && stayed[1] == "\"mesma\"" && noRefill && _panes[0].KickState == "fechada"
                && menuAfter.Contains("Fechar a live de surskity") && menuAfter.Contains("Abrir de novo a live de biscoitao"),
                $"botões antes={kickBefore} menu antes={string.Join("|", menuBefore)} marca={string.Join(",", stayed)} sem repor={noRefill} " +
                $"Kick={KickNow()} botão={_panes[0].KickState} menu depois={string.Join("|", menuAfter)}");
            CloseKick(0, null);
            ApplyKick();
            bool none = await WaitFor(() => lives0 is not null && lives0.Count == 1 && !lives0.KickChannels.Any(), 10);
            Check("kick: fechar todas", none && _config.Kick[0].Slots == 0 && _panes[0].KickState == "fechada", $"Kick={KickNow()}");
            ReopenKick(0, null);
            bool reopened = await WaitFor(() => lives0 is not null && lives0.Count == 3 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.Order().SequenceEqual(new[] { "biscoitao", "yetz" }), 15);
            Check("kick: abrir todas de novo volta às 2 primeiras da lista no ar",
                reopened && _panes[0].KickState == "ligado" && !_config.Kick[0].Limited,
                $"Kick={KickNow()} botão={_panes[0].KickState}");

            // Conferência das lives tocando: a do biscoitao anda (▶); a do yetz fica parada (a 1ª conferência
            // só anota onde o vídeo está) e, depois de duas paradas seguidas, é recarregada (⟳1).
            if (lives0 is not null)
                for (int i = 0; i < 3; i++)
                    await lives0.WatchKickAsync();
            var playback = lives0?.KickPlayback.ToDictionary(k => k.Channel) ?? new();
            string summary = LivesSummary(0);
            Check("lives da Kick: confere se o vídeo toca e recarrega a que travou",
                playback.TryGetValue("biscoitao", out var bisc) && bisc.Playing == true && bisc.Reloads == 0
                && playback.TryGetValue("yetz", out var yetz) && yetz.Reloads == 1
                && summary.Contains("biscoitao ▶") && summary.Contains("yetz ⟳1"),
                summary);

            // A página da live morre (como por falta de memória no notebook) e abre de novo na hora. Sem
            // internet ela nem carrega: a conferência tenta de novo depois de duas paradas, e o vídeo volta.
            var kickFile = Path.Combine(_root, "site", "kick.html");
            File.Move(kickFile, kickFile + ".fora");
            int killed = 0;
            if (_env is not null)
                foreach (var renderer in await _env.GetProcessExtendedInfosAsync())
                    if (renderer.AssociatedFrameInfos.Any(f => f.Source.Contains("kick.html?canal=biscoitao")))
                    {
                        using var process = Process.GetProcessById(renderer.ProcessInfo.ProcessId);
                        process.Kill();
                        killed++;
                    }
            (bool? Playing, int Reloads) Biscoitao() => lives0?.KickPlayback.Where(k => k.Channel == "biscoitao")
                .Select(k => (k.Playing, k.Reloads)).FirstOrDefault() ?? default;
            bool reopenedNow = await WaitFor(() => Biscoitao().Reloads == 1 && lives0!.Loaded < lives0.Count, 15);
            File.Move(kickFile + ".fora", kickFile);
            if (lives0 is not null)
                for (int i = 0; i < 2; i++)
                    await lives0.WatchKickAsync();
            bool loadedAgain = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count, 15);
            if (lives0 is not null)
                await lives0.WatchKickAsync();
            // As lives também ficam sem animações, e continuam assim na página que abriu de novo.
            var livesMotion = lives0 is null ? new List<string>() : await lives0.EvalAsync(ReducedMotion);
            Check("lives da Kick: página que morreu abre de novo, e a que não carregou é recarregada",
                killed > 0 && reopenedNow && loadedAgain && Biscoitao() is (true, 2) && livesMotion.Count == 3 && livesMotion.All(m => m == "true"),
                $"processos fechados={killed} reabriu na hora={reopenedNow} carregou depois={loadedAgain} sem animações={string.Join(",", livesMotion)} {LivesSummary(0)}");
            await WaitFor(() => _panes.All(p => p.Status == PaneStatus.Ok), 20);
            _kickLive.Clear();

            // deck.json da versão em que o kick só fechava a 2ª: vira 1 vaga, e o campo velho some ao salvar.
            var oldConfig = Path.Combine(_root, "deck-antigo.json");
            File.WriteAllText(oldConfig, "{ \"KickSingle\": [false, false, false, true] }");
            var migrated = DeckConfig.Load(oldConfig);
            migrated.Save(oldConfig);
            var saved = File.ReadAllText(oldConfig);
            Check("kick: deck.json antigo (só a 2ª fechada) continua com a 2ª fechada",
                migrated.Kick[3].Slots == 1 && migrated.Kick.Take(3).All(k => !k.Limited)
                && !saved.Contains("KickSingle") && !saved.Contains("Limited") && saved.Contains("\"Slots\": 1"),
                string.Join(",", migrated.Kick.Select(k => k.Slots)));

            // ⏻ na conta 1 (que tem lives): o jogo vira página em branco e as lives fecham; ligar traz tudo de volta.
            await SetPaneOffAsync(0, true);
            bool closed = await WaitFor(() => _panes[0].Core?.Source == "about:blank", 10);
            Check("desligar a conta 1: o jogo fecha, as lives param e o cabeçalho mostra 'desligada'",
                closed && _panes[0].Off && _lives[0] is null && _presence[0] is null && _panes[0].StatusText == "desligada"
                && _panes[0].VoteText is null && _config.LivesAccounts.Contains(1),
                $"página={_panes[0].Core?.Source} lives={(_lives[0] is null ? "fechadas" : "abertas")} status='{_panes[0].StatusText}'");
            await SetPaneOffAsync(0, false);
            bool back = await WaitFor(() => _panes[0].Status == PaneStatus.Ok && _panes[0].Core?.DocumentTitle == "Jogo-teste"
                && _lives[0] is LivesWindow w && w.Count > 0 && w.Loaded == w.Count, 20);
            Check("ligar a conta 1 de novo: o jogo e as lives voltam", back,
                $"status={_panes[0].Status} título='{_panes[0].Core?.DocumentTitle}' lives={(_lives[0] is LivesWindow lw ? $"{lw.Loaded}/{lw.Count}" : "fechadas")}");
            Check("menos animações continua depois de desligar e ligar a conta", await Eval(_panes[0], ReducedMotion) == "true");

            WindowState = FormWindowState.Minimized;
            await Task.Delay(1500);
            var alive = await Eval(_panes[2], "1 + 1");
            WindowState = FormWindowState.Normal;
            await Task.Delay(1000);
            Check("minimizar e restaurar com as contas vivas", alive == "2" && Shape(2, 2), Detail());

            // Voto no TopIdle, por partes (o autoteste não sai para a internet).
            Check("link de voto com o nick do personagem",
                TopIdle.VoteUrl("pokeidle-io-5f9969", "fulano") == "https://topidle.com/jogo/pokeidle-io-5f9969?votar=1&playerIdentifier=fulano"
                && TopIdle.VoteUrl("pokeidle-io-5f9969", "nick com espaço") == "https://topidle.com/jogo/pokeidle-io-5f9969?votar=1");
            Check("nick lido do jogo", TopIdle.ParseNick("\"fulano\"") == "fulano"
                && TopIdle.ParseNick("\"\\\"ciclano\\\"\"") == "ciclano" && TopIdle.ParseNick("null") is null);
            Check("voto registrado é percebido",
                TopIdle.IsVoteRegistered("POST", "https://topidle.com/api/games/pokeidle-io-5f9969/vote", 200, "pokeidle-io-5f9969")
                && !TopIdle.IsVoteRegistered("POST", "https://topidle.com/api/games/pokeidle-io-5f9969/vote", 429, "pokeidle-io-5f9969")
                && !TopIdle.IsVoteRegistered("GET", "https://topidle.com/api/games/pokeidle-io-5f9969/vote", 200, "pokeidle-io-5f9969")
                && !TopIdle.IsVoteRegistered("POST", "https://outro.com/api/games/pokeidle-io-5f9969/vote", 200, "pokeidle-io-5f9969"));
            _topIdleGameId = "pokeidle-io-5f9969";
            _votes[0] = new VoteState(true, false, DateTime.Now.AddHours(2).AddMinutes(5).AddSeconds(30), "teste");
            _votes[1] = new VoteState(true, true, null, "teste");
            _votes[2] = new VoteState(false, true, null, null);
            for (int i = 0; i < Accounts; i++)
                RenderVote(i);
            RenderVoteStatus();
            Check("botões de voto nos cabeçalhos",
                _panes[0].VoteText == "🗳 2 h 05 min" && _panes[1].VoteText == "🗳 Votar" && _panes[2].VoteText == "🗳 Votar"
                && _panes[3].VoteText == "🗳 TopIdle" && _voteStatus.Text == "🗳 Voto liberado: Conta 2",
                string.Join(" | ", _panes.Select(p => p.VoteText)) + " | " + _voteStatus.Text);
            MarkVoted(1);
            Check("depois de votar o botão vira 'votou' (sem erro)", _panes[1].VoteText == "🗳 ✓ Votou" && _voteStatus.Text?.StartsWith("🗳 Próximo voto em") == true,
                _panes[1].VoteText + " | " + _voteStatus.Text);
            try
            {
                Check("TopIdle acha o pokeidle (precisa de internet)",
                    await TopIdle.FindGameIdAsync("pokeidle.io") == "pokeidle-io-5f9969");
                var anonymous = await TopIdle.GetStateAsync(_panes[3].Core!.CookieManager, "pokeidle-io-5f9969");
                Check("conta sem login no TopIdle aparece como 'Votar' (precisa de internet)", !anonymous.LoggedIn);
                Check("Kick responde se a live está no ar (precisa de internet)", await KickLive.IsLiveAsync("biscoitao") is not null);
            }
            catch (Exception ex)
            {
                Check("TopIdle respondeu (precisa de internet)", false, ex.Message);
            }

            const string master =
                "#EXTM3U\n#EXT-X-TWITCH-INFO:NODE=\"x\"\n#EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID=\"720p60\",NAME=\"720p60\"\n" +
                "#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=100,BANDWIDTH=3000000,RESOLUTION=1280x720,VIDEO=\"720p60\"\nhttps://x/720.m3u8\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=230000,RESOLUTION=284x160,VIDEO=\"160p30\"\nhttps://x/160.m3u8\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=160000,CODECS=\"mp4a.40.2\",VIDEO=\"audio_only\"\nhttps://x/audio.m3u8\n";
            var lowestOnly = VideoQuality.LowestOnly(master) ?? "";
            Check("lives na menor qualidade de vídeo (lista com só a 160p)",
                lowestOnly.Contains("160.m3u8") && !lowestOnly.Contains("720.m3u8") && !lowestOnly.Contains("audio.m3u8")
                && lowestOnly.Contains("#EXT-X-MEDIA") && VideoQuality.LowestOnly("#EXTM3U\n#EXTINF:2,\nseg.ts\n") is null);

            // Presença no chat da Twitch contra o servidor de verdade, anônima (anônimo não vale para o
            // bônus; aqui só confere que a conexão entra e fica nos canais).
            using (var presence = new TwitchPresence(
                       () => Task.FromResult<(string?, string?)>((null, "justinfan" + Random.Shared.Next(10000, 99999))),
                       new[] { "rodil", "tftoddy", "patopapao" }))
            {
                presence.Start();
                bool joined = await WaitFor(() => presence.Joined == 3, 25);
                Check("presença no chat da Twitch entra nos canais (precisa de internet)", joined, presence.Status);
            }

            var windowed = Bounds;
            ToggleFullscreen();
            await Task.Delay(800);
            var screen = Screen.FromControl(this).Bounds;
            Check("tela cheia", Bounds == screen && FormBorderStyle == FormBorderStyle.None && Shape(2, 2), $"{Bounds} tela {screen}");
            ToggleFullscreen();
            await Task.Delay(800);
            Check("sair da tela cheia volta como estava", Bounds == windowed && FormBorderStyle == FormBorderStyle.Sizable, $"{Bounds} antes {windowed}");
            Check("RAM na barra separada em jogo, lives e navegador",
                _ram.Text?.StartsWith("RAM ~") == true && _ram.Text.Contains(" jogo ") && _ram.Text.Contains(" lives "), _ram.Text ?? "");
        }
        catch (Exception ex)
        {
            log.Add("ERRO " + ex);
        }
        finally
        {
            File.WriteAllText(SelfTestReport, string.Join(Environment.NewLine, log));
            Close();
        }
    }
}
