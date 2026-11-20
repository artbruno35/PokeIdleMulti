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
    // --process-per-site: as páginas do mesmo site num perfil dividem um processo. As lives da Kick de uma
    // conta ficam num só, sem carregar o site inteiro uma vez por live: medido, 3 lives caem de ~655 MB
    // para ~560 MB. Cada conta é um perfil, e perfis nunca dividem processo: o jogo fica separado.
    internal const string BrowserArguments =
        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows " +
        "--disable-renderer-backgrounding --autoplay-policy=no-user-gesture-required --process-per-site";

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokeIdleMulti");

    private readonly bool _selfTest;
    private readonly string _root;
    private readonly string _configPath;
    private readonly DeckConfig _config;

    // No autoteste: a Kick leve veio ligada de fábrica (o autoteste desliga antes de abrir as lives).
    private readonly bool _kickLightByDefault;
    private readonly ExtensionStore _store;
    private readonly AccountPane[] _panes = new AccountPane[Accounts];
    private readonly LivesWindow?[] _lives = new LivesWindow?[Accounts];
    private readonly string?[] _twitchSeen = new string?[Accounts];

    /// <summary>A conta tem o login da Twitch no perfil (null: ainda não conferiu). Sem ele os players nem abrem.</summary>
    private readonly bool?[] _twitchLogin = new bool?[Accounts];

    private readonly Dictionary<string, bool?> _kickLive = new();
    private readonly Dictionary<string, bool?> _twitchLive = new();
    private DateTime _liveCheckAt;
    private bool _liveChecking;
    private bool _watching;
    private readonly PointsRedeemer[] _redeemKick = Enumerable.Range(0, Accounts).Select(_ => new PointsRedeemer(LiveSite.Kick)).ToArray();
    private readonly PointsRedeemer[] _redeemTwitch = Enumerable.Range(0, Accounts).Select(_ => new PointsRedeemer(LiveSite.Twitch)).ToArray();
    private DateTime _twitchPointsAt;
    private bool _twitchPointsBusy;
    private readonly RedeemLog _redeemLog;
    private RedeemLogWindow? _redeemLogWindow;
    private BulkRedeemDialog? _bulkWindow;
    private DateTime _kickRedeemAt;
    private bool _kickRedeeming;
    private readonly Dictionary<(int Pane, string Extension), PopupWindow> _extensionWindows = new();
    private readonly ToolStrip _bar = new ClickThroughToolStrip();
    private readonly StatusStrip _status = new();
    private readonly Panel _grid = new() { Dock = DockStyle.Fill, BackColor = Theme.Back };
    private readonly ToolStripButton _gridButton = new("Grade 2×2");
    private readonly ToolStripButton _columnsButton = new("Colunas");
    private readonly ToolStripButton _focusButton = new("Foco");
    private readonly ToolStripButton[] _accountButtons = new ToolStripButton[Accounts];
    // ⇄ e ⚡, e não 🔁 e 🔋: na barra o Windows desenha esses dois emojis como quadrados.
    private readonly ToolStripButton _echoButton = new("⇄ Eco");
    private bool _echo;
    private readonly ToolStripButton _zoomOutButton = new("−");
    private readonly ToolStripDropDownButton _zoomButton = new("Zoom 100%");
    private readonly ToolStripButton _zoomInButton = new("+");
    private readonly ToolStripDropDownButton _livesButton = new("Lives");
    private readonly ToolStripDropDownButton _kickButton = new("💰 Pontos da Kick") { Visible = false };
    private readonly ToolStripDropDownButton _twitchButton = new("💰 Pontos da Twitch") { Visible = false };
    private readonly ToolStripButton _extensionsButton = new("🧩 Extensões");
    private readonly ToolStripDropDownButton _sniperButton = new("🎯 Sniper") { Visible = false };
    private readonly bool[] _hasSniper = new bool[Accounts];
    private readonly ToolStripButton _fullscreenButton = new("⛶ Tela cheia");
    private readonly ToolStripSplitButton _ultraButton = new("⚡ ULTRA");
    private readonly ToolStripStatusLabel _ram = new();
    private readonly ToolStripStatusLabel _voteStatus = new();
    private readonly VoteState?[] _votes = new VoteState?[Accounts];
    private readonly DateTime[] _voteRefreshAt = new DateTime[Accounts];
    private readonly bool[] _voteBusy = new bool[Accounts];
    private readonly PopupWindow?[] _voteWindows = new PopupWindow?[Accounts];
    private readonly DateTime[] _votedAt = new DateTime[Accounts];
    private readonly RestockWindow?[] _restockWindows = new RestockWindow?[Accounts];
    private readonly bool[] _restockBusy = new bool[Accounts];
    private readonly bool[] _pvpBusy = new bool[Accounts];
    private readonly BonusGuard _guard = new();
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
        LivesWindow.NoFocus = selfTest;
        // O autoteste usa perfis e páginas de teste separados, para não mexer nas suas contas.
        _root = selfTest ? Path.Combine(DataRoot, "selftest") : DataRoot;
        _configPath = Path.Combine(_root, "deck.json");
        _config = selfTest ? new DeckConfig() : DeckConfig.Load(_configPath);
        // O consumo.log só começa 10 min depois de abrir, quando tudo já carregou (no autoteste, na hora).
        _usageLogAt = selfTest ? DateTime.MinValue : DateTime.Now.AddMinutes(10);
        // O autoteste começa com a conta 4 desligada, como ficaria salvo do uso anterior, e com 1 live da Twitch
        // (mais as 2 da Kick, com o player: a Kick leve, ligada de fábrica, tem o teste dela).
        if (selfTest)
        {
            _kickLightByDefault = _config.KickLight.All(l => l);
            _config.KickLight = new bool[Accounts];
            _config.Off[3] = true;
            _config.TwitchLives = 1;
        }
        // O registro dos resgates, um arquivo por conta (o do autoteste começa vazio a cada vez).
        if (selfTest && Directory.Exists(_root))
            foreach (var old in Directory.GetFiles(_root, "resgates*.log"))
                File.Delete(old);
        _redeemLog = new RedeemLog(_root, _config.AccountNames);
        // O diagnóstico da carga do jogo (o do autoteste também começa vazio).
        Diag.Folder = Path.Combine(_root, "diagnostico");
        if (selfTest && Directory.Exists(Diag.Folder))
            Directory.Delete(Diag.Folder, true);
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            _redeemKick[i].Log = text => _redeemLog.Add(index, _config.AccountNames[index], LiveSite.Kick, text);
            _redeemTwitch[i].Log = text => _redeemLog.Add(index, _config.AccountNames[index], LiveSite.Twitch, text);
        }
        _store = new ExtensionStore(_root);
        if (_config.Layout != DeckLayout.Focus)
            _layoutBeforeFocus = _config.Layout;
        // O zoom é um só para todas as contas. Do tempo em que cada uma tinha o seu, fica o da primeira ligada.
        var zoom = _config.Zoom[Enumerable.Range(0, Accounts).FirstOrDefault(i => !_config.Off[i])];
        Array.Fill(_config.Zoom, zoom);

        Text = "PokeIdle Deck";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        KeyPreview = true;
        MinimumSize = new Size(900, 600);

        BuildBar();
        BuildStatus();
        for (int i = 0; i < Accounts; i++)
        {
            var pane = new AccountPane(i)
            {
                AccountName = _config.AccountNames[i], ReduceMotion = _config.ReduceMotion, Ultra = _config.Ultra, GameFps = _config.GameFps, SpriteMs = _config.SpriteMs,
                RestockConfig = _config.Restock[i], PvpOn = _config.Pvp[i],
                PvpScreen = _config.PvpScreen[i], PvpSpeed = _config.PvpSpeed[i],
                // O jogo-teste não conecta: o autoteste liga o vigia só no teste dele.
                LoadTimeout = selfTest ? null : TimeSpan.FromSeconds(60),
            };
            pane.Diag = new PaneDiag(Diag.Folder, pane.ProfileName, () => pane.AccountName);
            pane.GameStuck += (p, text) => LogOpening(p, text);
            pane.ShowRestock(null);
            pane.RestockRequested += p => ShowRestockWindow(p.Index);
            pane.PvpRequested += p => _ = SetPvpAsync(p.Index, !_config.Pvp[p.Index]);
            pane.PvpMenuRequested += p => ShowPvpMenu(p.Index);
            pane.FocusRequested += p => ToggleFocus(p.Index);
            pane.ReloadRequested += p => _ = p.ReloadCleanAsync();
            pane.FullscreenRequested += _ => ToggleFullscreen();
            pane.SettingsChanged += OnPaneSettingsChanged;
            pane.Clicked += (p, click) => _ = EchoAsync(p, click);
            pane.PowerRequested += p => _ = SetPaneOffAsync(p.Index, !p.Off);
            pane.PopupRequested += (p, extension, anchor) => _ = OpenExtensionPopupAsync(p, extension, anchor);
            pane.VoteRequested += p => _ = OpenVoteAsync(p, null);
            // O botão lives do cabeçalho: as lives da Kick e as da Twitch da conta, para fechar ou abrir de novo.
            pane.LivesMenuOpening += (p, items) =>
            {
                items.Clear();
                foreach (var site in new[] { LiveSite.Kick, LiveSite.Twitch })
                {
                    if (items.Count > 0)
                        items.Add(new ToolStripSeparator());
                    items.Add(new ToolStripMenuItem($"Lives da {site}:") { Enabled = false });
                    items.AddRange(LiveMenuItems(p.Index, site).ToArray());
                }
            };
            pane.VoteLinkRequested += (p, uri) => _ = OpenVoteAsync(p, uri);
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

        _echoButton.ToolTipText = "Eco: um clique numa conta se repete nas outras (no mesmo botão)";
        _echoButton.Click += (_, _) => SetEcho(!_echo);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(_echoButton);

        _zoomOutButton.ToolTipText = "Diminuir o zoom de todas as contas";
        _zoomInButton.ToolTipText = "Aumentar o zoom de todas as contas";
        _zoomButton.ToolTipText = "Zoom de todas as contas";
        _zoomOutButton.Font = _zoomInButton.Font = new Font(_bar.Font.FontFamily, 11f, FontStyle.Bold);
        _zoomOutButton.Click += (_, _) => StepZoom(-1);
        _zoomInButton.Click += (_, _) => StepZoom(1);
        _zoomButton.DropDownOpening += (_, _) => FillZoomMenu();
        _zoomButton.DropDownItems.Add("…");
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.AddRange(new ToolStripItem[] { _zoomOutButton, _zoomButton, _zoomInButton });
        ShowZoom();

        // Itens à direita entram da direita para a esquerda.
        _fullscreenButton.Alignment = _extensionsButton.Alignment = _livesButton.Alignment = ToolStripItemAlignment.Right;
        _fullscreenButton.ToolTipText = "Tela cheia (F11)";
        _fullscreenButton.Click += (_, _) => ToggleFullscreen();
        _extensionsButton.ToolTipText = "Instalar e gerenciar as extensões de cada conta";
        _extensionsButton.Click += (_, _) => OpenExtensions();
        _livesButton.ToolTipText = Theme.Wrap("Lives da Twitch e da Kick tocando em segundo plano, juntando os pontos dos canais (o bônus das lives no jogo)");
        _livesButton.DropDownOpening += (_, _) => FillLivesMenu();
        _livesButton.DropDownItems.Add("…");
        _kickButton.Alignment = ToolStripItemAlignment.Right;
        _kickButton.ToolTipText = Theme.Wrap("Abre os chats da Kick da conta, para resgatar os pontos do canal (300 pontos = 1 h de +15% de XP)");
        _kickButton.DropDownOpening += (_, _) => FillPointsMenu(LiveSite.Kick);
        _kickButton.DropDownItems.Add("…");
        _twitchButton.Alignment = ToolStripItemAlignment.Right;
        _twitchButton.ToolTipText = Theme.Wrap("Abre os chats da Twitch da conta, com o botão dos pontos e o baú, para resgatar na mão " +
                                               "(300 pontos = 1 h de +15% de XP)");
        _twitchButton.DropDownOpening += (_, _) => FillPointsMenu(LiveSite.Twitch);
        _twitchButton.DropDownItems.Add("…");
        _sniperButton.Alignment = ToolStripItemAlignment.Right;
        _sniperButton.ToolTipText = "Liga ou desliga o Rotom Sniper (compras automáticas no mercado) em todas as contas";
        _sniperButton.DropDownOpening += (_, _) => FillSniperMenu();
        _sniperButton.DropDownItems.Add("…");
        _ultraButton.Alignment = ToolStripItemAlignment.Right;
        _ultraButton.ToolTipText = "ULTRA em todas as contas: o Modo ULTRA Economia do jogo (ULTRA Battery Saver). O jogo inteiro " +
                                   "sai da tela e fica só uma caixa com o XP, o Gold, os drops e os shinies da sessão; a caça continua. " +
                                   "Na setinha, o menos animações";
        _ultraButton.ButtonClick += async (_, _) => await SetUltraAsync(!_config.Ultra);
        _ultraButton.DropDownOpening += (_, _) => FillUltraMenu();
        ShowUltra();
        _ultraButton.DropDownItems.Add("…");
        _bar.Items.Add(_fullscreenButton);
        _bar.Items.Add(_ultraButton);
        _bar.Items.Add(_extensionsButton);
        _bar.Items.Add(_sniperButton);
        _bar.Items.Add(_livesButton);
        _bar.Items.Add(_kickButton);
        _bar.Items.Add(_twitchButton);
    }

    private void BuildStatus()
    {
        Theme.Style(_status);
        _status.SizingGrip = false;
        _status.ShowItemToolTips = true;
        _ram.ForeColor = Theme.Muted;
        _status.Items.Add(_ram);
        _status.Items.Add(new ToolStripStatusLabel("·") { ForeColor = Theme.Muted });
        _voteStatus.ForeColor = Theme.Muted;
        _status.Items.Add(_voteStatus);
        _status.Items.Add(new ToolStripStatusLabel { Spring = true });
        _diagStatus.ForeColor = Theme.Muted;
        _diagStatus.ToolTipText = Theme.Wrap("O jogo não carrega? Gera na Área de trabalho um .zip com o log da carga de cada conta, prints e as " +
                                             "informações do PC e da rede (sem senha, token nem cookie), para mandar a quem cuida do deck.");
        _diagStatus.Click += async (_, _) => await CreateDiagnosticAsync();
        _status.Items.Add(_diagStatus);
        // A assinatura, no fim da barra de baixo.
        _status.Items.Add(new ToolStripStatusLabel("·") { ForeColor = Theme.Muted });
        _status.Items.Add(new ToolStripStatusLabel("by: artbruno") { ForeColor = Theme.Muted });
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
            Diag.Write("ERRO ao abrir o navegador embutido: " + ex);
            DeckBox.Show(this, "Não consegui abrir o navegador embutido.\n\n" + ex.Message, Text, DeckBoxIcon.Error);
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
        Diag.Write(Diag.Summary(_env) + $" · ligadas: {string.Join(", ", _panes.Where(p => !_config.Off[p.Index]).Select(p => p.ProfileName))}");
        _env.BrowserProcessExited += (_, e) => Diag.Write($"o navegador interno FECHOU: {e.BrowserProcessExitKind} (processo {e.BrowserProcessId})");

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
            DeckBox.Show(this, "Configurações de extensão trazidas.\n\n" + string.Join("\n", lines), Text);
        }
    }

    private string GameUrlForDeck => _selfTest ? "https://deck.test/game.html" : GameUrl;

    /// <summary>Dados trazidos de outro PC: reinstala da pasta daqui as extensões cujo caminho antigo sumiu.</summary>
    private Task RepairExtensions(CoreWebView2 core) => _store.RepairAsync(core.Profile);

    // As lives do autoteste vêm de outros sites (lives.test e kick.test), como a Kick e o jogo são sites diferentes:
    // assim dividem processo entre elas, mas não com o jogo. A Kick de teste (o player e a API) quem responde é o
    // próprio deck, porque o navegador não deixa interceptar os pedidos de um site mapeado para uma pasta.
    private Action<CoreWebView2>? SelfTestSetup => _selfTest
        ? core =>
        {
            core.SetVirtualHostNameToFolderMapping("deck.test", Path.Combine(_root, "site"), CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("lives.test", Path.Combine(_root, "site"), CoreWebView2HostResourceAccessKind.Allow);
            core.AddWebResourceRequestedFilter("https://kick.test/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.AddWebResourceRequestedFilter("https://twitch.test/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                FakeKick(e);
                FakeTwitch(e);
            };
        }
        : null;

    // A Kick de mentira do autoteste: o saldo de cada canal e cada resgate pedido ("canal bônus login corpo").
    private readonly Dictionary<string, int> _fakePoints = new();
    private readonly List<string> _fakeRedeems = new();

    // Os bônus pausados no canal, na Kick e na Twitch de mentira (pelo id).
    private readonly HashSet<string> _fakePausedRewards = new();

    // Quem está logado na Kick de mentira: no canal com o mesmo nome é o dono (pontos infinitos).
    private string _fakeKickMe = "fulano_kick";

    // O id de cada usuário na Kick de mentira.
    private static int FakeKickId(string name) => name.Sum(c => (int)c);

    /// <summary>
    /// A Kick do autoteste (kick.test): as páginas da pasta do site de teste (o player é o kick.html; sem o arquivo,
    /// fica sem resposta, como sem internet) e a API, com quem está logado, o canal (e o usuário dele), o saldo, os
    /// bônus do canal e o resgate. O /me do canal ela não tem (404): o dono do canal sai pelo id do usuário.
    /// </summary>
    private void FakeKick(CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        if (_env is null || uri.Host != "kick.test")
            return;
        if (!uri.AbsolutePath.StartsWith("/api/"))
        {
            var file = Path.Combine(_root, "site", uri.AbsolutePath.TrimStart('/'));
            if (File.Exists(file))
                e.Response = _env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK", "Content-Type: text/html");
            return;
        }
        // api/v1/user (quem está logado), api/v2/channels/{canal}, …/me, …/points, …/rewards e …/rewards/{id}/redeem
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool logged = e.Request.Headers.Contains("Authorization");
        void Reply(int code, string body) => e.Response = _env.CreateWebResourceResponse(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), code, code == 200 ? "OK" : "Error", "Content-Type: application/json");
        if (path is [_, "v1", "user"])
        {
            Reply(logged ? 200 : 401, logged ? JsonSerializer.Serialize(new { id = FakeKickId(_fakeKickMe), username = _fakeKickMe }) : "{\"message\":\"Unauthenticated.\"}");
            return;
        }
        if (path.Length < 4 || path[2] != "channels")
            return;
        string channel = path[3];
        bool owner = channel == _fakeKickMe;
        if (path.Length == 4)
        {
            Reply(200, JsonSerializer.Serialize(new
            {
                id = 1000 + FakeKickId(channel), user_id = FakeKickId(channel), slug = channel, user = new { id = FakeKickId(channel), username = channel },
            }));
            return;
        }
        if (path[4] == "me")
        {
            Reply(404, "{\"message\":\"Not Found\"}");
            return;
        }
        var rewards = FakeKickRewards;
        int status = 200;
        string json;
        if (path.Length == 5 && FakeKickApi(channel, path[4]) is string read)
        {
            json = read;
        }
        else if (path.Length == 7 && path[6] == "redeem" && e.Request.Method == "POST" && rewards.Any(r => r.Id == path[5]))
        {
            string body = e.Request.Content is null ? "" : new StreamReader(e.Request.Content).ReadToEnd();
            string auth = e.Request.Headers.Contains("Authorization") ? e.Request.Headers.GetHeader("Authorization") : "sem login";
            _fakeRedeems.Add($"{channel} {path[5]} {auth} {body}");
            // O dono do canal (pontos infinitos) resgata sem gastar.
            int cost = rewards.First(r => r.Id == path[5]).Cost;
            if (owner)
                status = 200;
            else if (_fakePoints.GetValueOrDefault(channel) >= cost)
                _fakePoints[channel] -= cost;
            else
                status = 400;
            json = status == 200 ? "{\"message\":\"OK\"}" : "{\"message\":\"Not enough points\"}";
        }
        else
        {
            return;
        }
        e.Response = _env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)), status,
            status == 200 ? "OK" : "Bad Request", "Content-Type: application/json");
    }

    // Os 3 do PokeIdle, os de 3 e 10 h antes deles (o deck fica com o de 1 h, o mais barato) e um "XP" de outro tipo, que
    // não é o do jogo.
    private static readonly (string Id, string Title, int Cost, string Description)[] FakeKickRewards =
    {
        ("xp3", "Bônus +15% XP PokeIdle (3h)", 900, "3 horas de +15% de XP no PokeIdle."),
        ("lure10", "Bônus de +15% Secret Lure (10h)", 10000, "10 horas de +15% na chance de shiny no PokeIdle."),
        ("cap3", "Bônus de +15% Capture Boost (3h)", 1500, "3 horas de +15% na chance de captura no PokeIdle."),
        ("xp", "Bônus +15% XP PokeIdle", 300, "1 hora de +15% de XP no PokeIdle."),
        ("cap", "Bônus de +15% Capture Boost", 500, "1 hora de +15% na chance de captura no PokeIdle."),
        ("lure", "Bônus de +15% Secret Lure", 1000, "1 hora de +15% na chance de shiny no PokeIdle."),
        ("chat", "Dobro de XP no chat", 100, ""),
    };

    /// <summary>O saldo ("points") e os bônus ("rewards") de um canal na Kick de mentira (null: outro pedido).</summary>
    private string? FakeKickApi(string channel, string what) => what switch
    {
        "points" => $"{{\"data\":{{\"points\":{_fakePoints.GetValueOrDefault(channel)}}},\"message\":\"OK\"}}",
        "rewards" => JsonSerializer.Serialize(new
        {
            data = FakeKickRewards.Select(r => new
            {
                id = r.Id, title = r.Title, cost = r.Cost, description = r.Description, is_enabled = true, is_paused = _fakePausedRewards.Contains(r.Id),
            }),
        }),
        _ => null,
    };

    // A Twitch de mentira do autoteste: o saldo de cada canal, os canais com baú esperando e cada pedido feito.
    private readonly Dictionary<string, int> _fakeTwitchPoints = new();
    private readonly HashSet<string> _fakeTwitchClaims = new();
    private readonly List<string> _fakeTwitch = new();

    // Roda depois de cada resgate que passou (o autoteste gasta os pontos por fora, ou cancela, no meio do lote).
    private Action<string>? _fakeTwitchAfterRedeem;

    // Quem está logado na Twitch de mentira: no canal com o mesmo nome é o dono (pontos infinitos; o saldo nem vem).
    private string _fakeTwitchMe = "fulano_twitch";

    /// <summary>
    /// A Twitch do autoteste (twitch.test): as páginas da pasta do site de teste (o player é o twitch.html), a API
    /// (/gql: o saldo com os bônus do canal, o baú, que só passa com o token da checagem anti-robô, e o resgate) e o
    /// token da checagem (/integrity).
    /// </summary>
    private void FakeTwitch(CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        if (_env is null || uri.Host != "twitch.test")
            return;
        if (uri.AbsolutePath is not ("/gql" or "/integrity"))
        {
            var file = Path.Combine(_root, "site", uri.AbsolutePath.TrimStart('/'));
            if (File.Exists(file))
                e.Response = _env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK", "Content-Type: text/html");
            return;
        }
        string Header(string name) => e.Request.Headers.Contains(name) ? e.Request.Headers.GetHeader(name) : "-";
        var rewards = FakeTwitchRewards;
        string json;
        if (uri.AbsolutePath == "/integrity")
        {
            _fakeTwitch.Add($"checagem {Header("Authorization")} {Header("X-Device-Id")}");
            json = "{\"token\":\"tok-checagem\",\"expiration\":0}";
        }
        else
        {
            using var doc = JsonDocument.Parse(e.Request.Content is null ? "{}" : new StreamReader(e.Request.Content).ReadToEnd());
            // As operações salvas da Twitch (a da raid) vêm sem a query, só com o nome.
            string query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? ""
                : doc.RootElement.TryGetProperty("operationName", out var op) ? op.GetString() ?? "" : "";
            var variables = doc.RootElement.GetProperty("variables");
            if (query == "JoinRaid")
            {
                string raid = variables.GetProperty("input").GetProperty("raidID").GetString() ?? "";
                _fakeTwitch.Add($"raid {raid} {Header("Authorization")}");
                json = JsonSerializer.Serialize(new { data = new { joinRaid = new { raidID = raid } } });
            }
            else if (query.Contains("claimCommunityMoment"))
            {
                string moment = variables.GetProperty("i").GetProperty("momentID").GetString() ?? "";
                _fakeTwitch.Add($"momento {moment} {Header("Authorization")}");
                json = JsonSerializer.Serialize(new { data = new { claimCommunityMoment = new { moment = new { id = moment } } } });
            }
            else if (query.Contains("claimCommunityPoints"))
            {
                string claim = variables.GetProperty("i").GetProperty("claimID").GetString() ?? "";
                string channel = claim.Replace("bau-", "");
                if (Header("Client-Integrity") != "tok-checagem")
                {
                    _fakeTwitch.Add("baú sem a checagem");
                    json = "{\"errors\":[{\"message\":\"failed integrity check\",\"extensions\":{\"code\":\"IntegrityCheckFailed\"}}]," +
                           "\"data\":{\"claimCommunityPoints\":null},\"extensions\":{\"challenge\":{\"type\":\"integrity\"}}}";
                }
                else if (claim == "bau-pego")
                {
                    // O baú que outro lugar com a mesma conta já pegou.
                    _fakeTwitch.Add($"baú {claim} {Header("Authorization")}");
                    json = "{\"data\":{\"claimCommunityPoints\":{\"claim\":null,\"currentPoints\":null,\"error\":{\"code\":\"NOT_FOUND\"}}}}";
                }
                else
                {
                    _fakeTwitch.Add($"baú {claim} {Header("Authorization")}");
                    _fakeTwitchClaims.Remove(channel);
                    _fakeTwitchPoints[channel] = _fakeTwitchPoints.GetValueOrDefault(channel) + 50;
                    json = $"{{\"data\":{{\"claimCommunityPoints\":{{\"claim\":{{\"id\":\"{claim}\"}},\"currentPoints\":{_fakeTwitchPoints[channel]},\"error\":null}}}}}}";
                }
            }
            else if (query.Contains("redeemCommunityPointsCustomReward"))
            {
                var input = variables.GetProperty("i");
                string channel = input.GetProperty("channelID").GetString()!.Replace("id-", "");
                int cost = input.GetProperty("cost").GetInt32();
                _fakeTwitch.Add($"resgate {channel} {input.GetProperty("rewardID").GetString()} {cost} {Header("Authorization")} " +
                                input.GetProperty("transactionID").GetString());
                // Como a Twitch: o custo, o título e a descrição (prompt) têm de ser os do bônus no canal.
                var reward = rewards.FirstOrDefault(r => r.id == input.GetProperty("rewardID").GetString());
                string? prompt = input.TryGetProperty("prompt", out var sent) && sent.ValueKind == JsonValueKind.String ? sent.GetString() : null;
                bool matches = reward is not null && reward.cost == cost && reward.title == input.GetProperty("title").GetString()
                               && reward.prompt == (prompt ?? "");
                // O dono do canal (pontos infinitos) resgata sem gastar.
                bool owner = channel == _fakeTwitchMe;
                bool enough = owner || _fakeTwitchPoints.GetValueOrDefault(channel) >= cost;
                if (matches && enough)
                {
                    if (!owner)
                        _fakeTwitchPoints[channel] -= cost;
                    _fakeTwitchAfterRedeem?.Invoke(channel);
                }
                json = "{\"data\":{\"redeemCommunityPointsCustomReward\":" +
                       (!matches ? "{\"redemption\":null,\"error\":{\"code\":\"PROPERTIES_MISMATCH\"}}"
                           : enough ? "{\"redemption\":{\"id\":\"r1\"},\"error\":null}"
                           : "{\"redemption\":null,\"error\":{\"code\":\"NOT_ENOUGH_POINTS\"}}") + "}}";
            }
            else
            {
                json = FakeTwitchBalance(variables.GetProperty("login").GetString() ?? "", "saldo", Header("Authorization"), Header("X-Device-Id"));
            }
        }
        e.Response = _env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)), 200, "OK",
            "Content-Type: application/json");
    }

    /// <summary>
    /// O saldo de um canal na Twitch de mentira, com os bônus e o baú se tiver. Vale para o player (twitch.test, anota
    /// "saldo …") e para a leitura de fora da página na Twitch leve (o servidor em localhost, anota "leitura …").
    /// </summary>
    private string FakeTwitchBalance(string channel, string kind, string authorization, string device)
    {
        _fakeTwitch.Add($"{kind} {channel} {authorization} {device}");
        object? communityPoints = channel == _fakeTwitchMe ? null : new
        {
            balance = _fakeTwitchPoints.GetValueOrDefault(channel),
            availableClaim = _fakeTwitchClaims.Contains(channel) ? new { id = "bau-" + channel } : null,
        };
        return JsonSerializer.Serialize(new
        {
            data = new
            {
                currentUser = new { id = "id-" + _fakeTwitchMe, login = _fakeTwitchMe },
                user = new
                {
                    id = "id-" + channel,
                    channel = new
                    {
                        self = new { communityPoints },
                        communityPointsSettings = new
                        {
                            customRewards = FakeTwitchRewards.Select(r => new
                            {
                                r.id, r.title, r.prompt, r.cost, isEnabled = true, isPaused = _fakePausedRewards.Contains(r.id), isInStock = true,
                                cooldownExpiresAt = (string?)null,
                            }),
                        },
                    },
                },
            },
        });
    }

    // Os bônus dos canais da Twitch de mentira (os 3 do PokeIdle e um de outro tipo).
    private sealed record FakeReward(string id, string title, string prompt, int cost);

    // Como na Twitch: fora de ordem, com os de 3 e 10 h antes do de 1 h (o deck fica com o mais barato).
    private static readonly FakeReward[] FakeTwitchRewards =
    {
        new("xp3", "Bônus +15% XP PokeIdle (3h)", "3 horas de +15% de XP no PokeIdle.", 900),
        new("lure10", "Bônus de +15% Secret Lure (10h)", "10 horas de +15% na chance de shiny no PokeIdle.", 10000),
        new("cap3", "Bônus de +15% Capture Boost (3h)", "3 horas de +15% na chance de captura no PokeIdle.", 1500),
        new("xp", "Bônus +15% XP PokeIdle", "1 hora de +15% de XP no PokeIdle.", 300),
        new("cap", "Bônus de +15% Capture Boost", "1 hora de +15% na chance de captura no PokeIdle.", 500),
        new("lure", "Bônus de +15% Secret Lure", "1 hora de +15% na chance de shiny no PokeIdle.", 1000),
        new("chat", "Capture o streamer", "", 100),
    };

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
        _fakePubSub?.Dispose();
        foreach (var pane in _panes)
            pane.Diag?.Flush();
        base.OnFormClosing(e);
    }

    // ---------- Janela: posição, tamanho e tela cheia ----------

    private void RestoreWindowBounds()
    {
        var saved = _config.WindowBounds is { Length: 4 } b ? new Rectangle(b[0], b[1], b[2], b[3]) : Rectangle.Empty;
        // O autoteste abre na Área de trabalho 2 do Windows (TestDesktop leva para lá cada janela que o deck mostra); sem
        // ela, num monitor que não é o principal (onde o usuário joga), quando tem mais de um. As outras janelas do deck
        // (lives, popups, registro) abrem na tela da janela principal e vão junto.
        if (_selfTest)
        {
            var area = (Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen!).WorkingArea;
            saved = new Rectangle(area.X + 80, area.Y + 60, 1500, 900);
        }
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

    // O autoteste não rouba o foco de quem está usando o PC (jogando no outro monitor): abre sem ativar.
    protected override bool ShowWithoutActivation => _selfTest;

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
        bool entering = layout == DeckLayout.Focus && _config.Layout != DeckLayout.Focus;
        bool leaving = layout != DeckLayout.Focus && _config.Layout == DeckLayout.Focus;
        _config.Layout = layout;
        if (layout != DeckLayout.Focus)
            _layoutBeforeFocus = layout;
        // A conta ampliada fica em 100%; voltar para a grade ou as colunas devolve o zoom de antes (o que deixa
        // as 4 contas cabendo na tela, como 50%).
        if (entering)
        {
            _config.ZoomOutsideFocus = _config.Zoom[0];
            SetZoomAll(1);
        }
        else if (leaving && _config.ZoomOutsideFocus is double zoom)
        {
            SetZoomAll(zoom);
        }
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
        _kickButton.Visible = _lives.Any(l => l is not null && l.Tabs(LiveSite.Kick).Any());
        _twitchButton.Visible = _lives.Any(l => l is not null && l.Tabs(LiveSite.Twitch).Any());
        RenderLiveButtons();
    }

    /// <summary>Os botões kick e twitch de cada conta: o que dá para fechar ou abrir de novo, ou por que não tem nada.</summary>
    private void RenderLiveButtons()
    {
        for (int i = 0; i < Accounts; i++)
        {
            var (enabled, closed, tip) = LiveButton(i, LiveSite.Kick);
            _panes[i].SetKick(enabled, closed, tip);
            (enabled, closed, tip) = LiveButton(i, LiveSite.Twitch);
            _panes[i].SetTwitch(enabled, closed, tip);
        }
    }

    private (bool Enabled, bool Closed, string Tooltip) LiveButton(int index, LiveSite site)
    {
        var open = LivesOpen(index, site);
        var limit = _config.Limits(site)[index];
        if (_lives[index] is null)
            return (false, false, !_config.LivesAccounts.Contains(index + 1)
                ? "As lives estão desligadas nesta conta (ligue em Lives, na barra de cima)"
                : $"Nenhuma live da {site} para abrir (Lives → Config…)");
        if (limit.Limited)
            return (true, true, $"Lives da {site} fechadas nesta conta: " + (limit.Slots == 0 ? "todas" : ShutText(index, site)) +
                (open.Count > 0 ? $" (abertas: {string.Join(", ", open)})" : "") + ". Clique para fechar outra ou abrir de novo");
        if (open.Count > 0)
            return (true, false, $"Escolher qual live da {site} fechar nesta conta (abertas: {string.Join(", ", open)}). " +
                "A vaga de uma live fechada não abre outra sozinha");
        bool checking = !_selfTest && (site == LiveSite.Kick ? _kickLive : _twitchLive).Count == 0;
        return (false, false, site == LiveSite.Twitch && _twitchLogin[index] == false ? "Entre na Twitch nesta conta (Lives → Mostrar)"
            : checking ? $"Conferindo quem está no ar na {site}…" : $"Nenhuma live da {site} aberta nesta conta agora");
    }

    /// <summary>As lives de <paramref name="site"/> abertas na conta, na ordem da lista.</summary>
    private List<string> LivesOpen(int index, LiveSite site) => (_lives[index]?.Channels(site) ?? Enumerable.Empty<string>())
        .OrderBy(c => _config.Channels(site).IndexOf(c)).ToList();

    /// <summary>Os canais da lista que o botão do site fechou na conta.</summary>
    private List<string> LivesShut(int index, LiveSite site) => _config.Channels(site).Where(_config.Limits(site)[index].Closed.Contains).ToList();

    /// <summary>As fechadas pelo nome, ou quantas vagas a conta perdeu (do tempo em que o kick só fechava a 2ª).</summary>
    private string ShutText(int index, LiveSite site)
    {
        var shut = LivesShut(index, site);
        int max = _config.Lives(site);
        int missing = max - _config.Limits(site)[index].SlotsOf(max);
        return shut.Count > 0 ? string.Join(", ", shut) : missing == 1 ? "1 vaga" : $"{missing} vagas";
    }

    /// <summary>O menu do kick ou do twitch: fechar cada live aberta ou todas; abrir de novo cada fechada ou todas.</summary>
    private List<ToolStripItem> LiveMenuItems(int index, LiveSite site)
    {
        var items = new List<ToolStripItem>();
        var open = LivesOpen(index, site);
        var shut = LivesShut(index, site);
        foreach (var channel in open)
            items.Add(new ToolStripMenuItem($"Fechar a live de {channel}", null, (_, _) => CloseLive(index, site, channel)));
        if (open.Count > 0)
            items.Add(new ToolStripMenuItem($"Fechar todas (nenhuma live da {site} nesta conta)", null, (_, _) => CloseLive(index, site, null)));
        if (_config.Limits(site)[index].Limited)
        {
            if (items.Count > 0)
                items.Add(new ToolStripSeparator());
            foreach (var channel in shut)
                items.Add(new ToolStripMenuItem($"Abrir de novo a live de {channel}", null, (_, _) => ReopenLive(index, site, channel)));
            items.Add(new ToolStripMenuItem($"Abrir todas de novo (volta às {_config.Lives(site)} primeiras da lista no ar)", null,
                (_, _) => ReopenLive(index, site, null)));
        }
        if (items.Count == 0)
            items.Add(new ToolStripMenuItem($"Nenhuma live da {site} aberta nesta conta agora") { Enabled = false });
        return items;
    }

    /// <summary>Uma linha por conta com live do site: abre a janela das lives já nela, com os chats (e o botão dos pontos).</summary>
    private void FillPointsMenu(LiveSite site)
    {
        var items = (site == LiveSite.Kick ? _kickButton : _twitchButton).DropDownItems;
        items.Clear();
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            if (_lives[i] is LivesWindow window && window.Tabs(site).Any())
                items.Add($"{_config.AccountNames[i]} · {string.Join(", ", window.Channels(site))}", null, (_, _) => ShowPoints(index, site));
        }
        if (items.Count == 0)
            items.Add(new ToolStripMenuItem($"Nenhuma live da {site} no ar agora") { Enabled = false });
    }

    /// <summary>
    /// O resumo curto do menu Lives: só as lives abertas e o saldo ("Twitch: tftoddy 1200 · Kick: biscoitao 230/300, yetz"),
    /// com ⚠ quando pede uma ação. O completo (<see cref="LivesSummary"/>) fica em Lives → Detalhes….
    /// </summary>
    private string LivesShort(int index)
    {
        if (_lives[index] is not LivesWindow window)
            return "";
        var parts = new List<string>();
        string Open(LiveSite site, PointsRedeemer redeemer) => string.Join(", ", LivesOpen(index, site)
            .Select(c => redeemer.Balance(c) is { Length: > 0 } balance ? $"{c} {balance}" : c));
        if (_twitchLogin[index] == false)
            parts.Add("⚠ entre na Twitch (Lives → Mostrar)");
        else if (window.TwitchChannels.Any())
            parts.Add("Twitch: " + Open(LiveSite.Twitch, _redeemTwitch[index]));
        if (window.KickChannels.Any())
            parts.Add("Kick: " + Open(LiveSite.Kick, _redeemKick[index]));
        if (_redeemTwitch[index].Stopped is not null || _redeemKick[index].Stopped is not null)
            parts.Add("⚠ resgate parado (Detalhes…)");
        else if ((_redeemKick[index].NoLogin && window.KickChannels.Any()) || (_redeemTwitch[index].NoLogin && window.TwitchChannels.Any()))
            parts.Add("⚠ entre na Twitch/Kick nas lives");
        return parts.Count == 0 ? "nenhuma live no ar" : string.Join(" · ", parts);
    }

    /// <summary>"Twitch no ar: cogu ▶, Kick no ar: biscoitao ▶" ou "Twitch: entre na Twitch…".</summary>
    private string LivesSummary(int index) => string.Join(", ", LivesParts(index));

    /// <summary>
    /// Lives → Detalhes…: uma linha por live aberta (conta, site, canal, se está tocando, a leve e o saldo dos pontos) e,
    /// embaixo, os avisos de cada conta (o resto do <see cref="LivesParts"/>: entrar na Twitch, resgate parado…).
    /// </summary>
    private void ShowLivesDetails()
    {
        var (rows, notes) = LivesDetails();
        DeckBox.ShowTable(this, "Detalhes das lives",
            new[] { ("Conta", 104), ("Site", 74), ("Canal", 136), ("Tocando", 104), ("Leve", 150), ("Pontos", 0) },
            rows, notes, "Nenhuma conta com lives.");
    }

    private (List<ListViewItem> Rows, List<string> Notes) LivesDetails()
    {
        var rows = new List<ListViewItem>();
        var notes = new List<string>();
        for (int i = 0; i < Accounts; i++)
        {
            if (_lives[i] is not LivesWindow window)
                continue;
            string name = _config.AccountNames[i];
            var kickRedeem = _config.RedeemKick[i] is not null ? _redeemKick[i] : null;
            var twitchRedeem = _config.RedeemTwitch[i] is not null || _config.ClaimTwitchBonus ? _redeemTwitch[i] : null;
            foreach (var site in new[] { LiveSite.Twitch, LiveSite.Kick })
                foreach (var (channel, playing, reloads) in window.Playback(site))
                {
                    var item = new ListViewItem(name) { UseItemStyleForSubItems = false };
                    item.SubItems.Add(site.ToString()).Tag = site == LiveSite.Kick ? UiTone.Kick : UiTone.Twitch;
                    item.SubItems.Add(channel);
                    string state = (playing == true ? "▶ tocando" : playing == false ? "‖ parada" : "—") + (reloads > 0 ? $"  ⟳{reloads}" : "");
                    var color = playing == true ? Color.FromArgb(126, 226, 168) : playing == false ? Color.FromArgb(243, 214, 107) : Theme.Muted;
                    item.SubItems.Add(state, color, Color.Empty, Ui.Font);
                    string light = (site == LiveSite.Twitch ? _watchers[i]?.Status(channel) : _kickWatchers[i]?.Status(channel)) ?? "";
                    item.SubItems.Add(light, Theme.Muted, Color.Empty, Ui.Font);
                    item.SubItems.Add((site == LiveSite.Kick ? kickRedeem : twitchRedeem)?.Text(channel) ?? "");
                    rows.Add(item);
                }
            foreach (var part in LivesParts(i).Where(p => !p.StartsWith("Twitch no ar:") && !p.StartsWith("Kick no ar:")))
                notes.Add($"{name}: {part}");
        }
        return (rows, notes);
    }

    private List<string> LivesParts(int index)
    {
        var parts = new List<string>();
        if (_lives[index] is LivesWindow window)
        {
            var kick = window.KickChannels.ToList();
            // ▶ tocando, ‖ parada (o deck manda tocar e, se continuar, recarrega: ⟳ quantas vezes). O ‖ e não o
            // ⏸: esse só existe nas fontes de emoji, que o desenham como um botão quadrado.
            // Com o resgate ligado, o saldo de cada uma entre parênteses: (230/300 pontos, resgatou 14:05).
            var redeem = _config.RedeemKick[index] is not null ? _redeemKick[index] : null;
            // Na Twitch o saldo aparece também só com o baú ligado.
            var twitchRedeem = _config.RedeemTwitch[index] is not null || _config.ClaimTwitchBonus ? _redeemTwitch[index] : null;
            // Na Twitch leve, no lugar do ▶: se a Twitch está aceitando o "assistindo" do deck ("leve ✓ 14:05").
            var watcher = _watchers[index];
            IEnumerable<string> Played(LiveSite site) => window.Playback(site).Select(k => k.Channel
                + (k.Playing == true ? " ▶" : k.Playing == false ? " ‖" : "") + (k.Reloads > 0 ? $" ⟳{k.Reloads}" : "")
                + (site == LiveSite.Twitch && watcher?.Status(k.Channel) is { Length: > 0 } light ? " " + light : "")
                + (site == LiveSite.Kick && _kickWatchers[index]?.Status(k.Channel) is { Length: > 0 } kickLight ? " " + kickLight : "")
                + ((site == LiveSite.Kick ? redeem : twitchRedeem)?.Text(k.Channel) is { Length: > 0 } points ? $" ({points})" : ""));
            if (_config.TwitchChannels.Count > 0 && _config.TwitchLives > 0)
                parts.Add(_twitchLogin[index] == false ? "Twitch: entre na Twitch (Lives → Mostrar)"
                        + (_twitchSeen[index] is string seen ? $" [{seen}]" : "")
                    : window.TwitchChannels.Any() ? "Twitch no ar: " + string.Join(", ", Played(LiveSite.Twitch))
                    : _config.Twitch[index].Slots == 0 ? "Twitch: todas fechadas no botão lives"
                    : _twitchLive.Count == 0 ? "Twitch: conferindo quem está no ar…"
                    : "Twitch: nenhuma live da lista no ar");
            // Na Twitch leve, quantas vezes o player parado abriu para uma ação (o baú, ~4 por hora em cada live; muito
            // mais que isso é a leitura do saldo de fora da página falhando, e a conferência abrindo o player).
            if (window.Wakes > 0)
                parts.Add($"player leve abriu {window.Wakes}× desde {window.CreatedAt:HH:mm} (só para o baú e os resgates)");
            if (_config.Twitch[index].Limited && _config.Twitch[index].SlotsOf(_config.TwitchLives) > 0)
                parts.Add("Twitch fechadas no botão lives: " + ShutText(index, LiveSite.Twitch));
            // Os avisos da Twitch (o websocket) fora do ar: o porquê (o login recusado para de vez; o resto tenta de novo).
            if (_pubsub[index] is { Connected: false, Error: string pubsubError })
                parts.Add("avisos da Twitch: " + pubsubError);
            if (twitchRedeem?.Stopped is string twitchStopped)
                parts.Add("resgate da Twitch parado: " + twitchStopped);
            else if (twitchRedeem is { NoLogin: true } && window.TwitchChannels.Any())
                parts.Add("pontos da Twitch: entre na Twitch nas lives desta conta");
            var playback = Played(LiveSite.Kick);
            if (_config.KickChannels.Count > 0 && _config.KickLives > 0)
                parts.Add(kick.Count > 0 ? "Kick no ar: " + string.Join(", ", playback)
                    : _config.Kick[index].Slots == 0 ? "Kick: todas fechadas no botão lives"
                    : _kickLive.Count == 0 ? "Kick: conferindo quem está no ar…"
                    : "Kick: nenhuma live da lista no ar");
            if (redeem?.Stopped is string stopped)
                parts.Add("resgate parado: " + stopped);
            else if (redeem is { NoLogin: true } && kick.Count > 0)
                parts.Add("resgate: entre na Kick nas lives desta conta");
            if (_config.Kick[index].Limited && _config.Kick[index].SlotsOf(_config.KickLives) > 0)
                parts.Add("Kick fechadas no botão lives: " + ShutText(index, LiveSite.Kick));
            if (window.Loaded < window.Count)
                parts.Add("carregando…");
        }
        return parts;
    }

    private void OnClock()
    {
        foreach (var pane in _panes)
            pane.RefreshHeader();
        // Medir lista todos os processos do navegador e abre cada um: a cada 10 s basta (e a CPU sai mais estável).
        if (_seconds++ % 10 == 0)
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
        // O jogo que não abre (preso na tela de abertura, ou a página que não carregou): recarrega a conta.
        if (_seconds % 5 == 2)
            foreach (var pane in _panes)
                _ = pane.CheckGameOpenAsync();
        if (_seconds % 2 == 0)
            foreach (var pane in _panes.Where(p => p.RestockConfig.On || _restockWindows[p.Index] is not null))
                _ = RefreshRestockAsync(pane.Index);
        // A fila de PvP: o ⚔ a cada 5 s com ela ligada.
        if (_seconds % 5 == 3)
            foreach (var pane in _panes.Where(p => p.PvpOn))
                _ = RefreshPvpAsync(pane.Index);
        // O bônus das lives no jogo (a dica do cabeçalho e a guarda do bônus): a cada 30 s.
        if (!_selfTest && _seconds % 30 == 10)
            foreach (var pane in _panes.Where(p => !p.Off))
                _ = RefreshBonusAsync(pane.Index);
        if (!_selfTest && !_liveChecking && DateTime.Now >= _liveCheckAt && _lives.Any(l => l is not null))
            _ = CheckLivesAsync();
        if (!_selfTest && _seconds % 30 == 15)
        {
            _ = WatchLivesAsync();
            _ = UpdateKickWatchersAsync();
        }
        if (!_selfTest && !_kickRedeeming && DateTime.Now >= _kickRedeemAt
            && Enumerable.Range(0, Accounts).Any(i => _config.RedeemKick[i] is not null && _lives[i] is not null))
            _ = RedeemKickAsync();
        if (!_selfTest && !_twitchPointsBusy && DateTime.Now >= _twitchPointsAt
            && Enumerable.Range(0, Accounts).Any(i => (_config.RedeemTwitch[i] is not null || _config.ClaimTwitchBonus || _config.TwitchLight[i])
                                                     && _lives[i]?.TwitchChannels.Any() == true))
            _ = TwitchPointsAsync();
        UpdateVotes();
    }

    /// <summary>Uma parte do consumo do deck: a memória privada e a CPU (em núcleos) dos processos dela.</summary>
    public sealed record UsagePart(string Name, long Bytes, double Cores, int Processes);

    /// <summary>As partes da última medição, na ordem da barra (para o autoteste).</summary>
    public IReadOnlyList<UsagePart> Usage { get; private set; } = Array.Empty<UsagePart>();

    /// <summary>A CPU já gasta por cada processo na medição anterior: a diferença dá a CPU do intervalo.</summary>
    private readonly Dictionary<int, TimeSpan> _cpuBefore = new();
    private readonly Stopwatch _cpuClock = new();
    /// <summary>A próxima linha do consumo.log: 10 min depois de abrir (o começo é pesado) e depois a cada 10 min.</summary>
    private DateTime _usageLogAt;

    private string UsageLog => Path.Combine(_root, "consumo.log");

    /// <summary>
    /// O consumo do deck, separado em partes: o jogo das contas, as lives da Kick e da Twitch, a placa de vídeo
    /// (que desenha o jogo e os vídeos), o resto do navegador (rede, áudio, extensões, DRM) e o próprio deck.
    /// A memória é a privada de cada processo, a que pesa no PC; a CPU é a do intervalo desde a medição anterior.
    /// </summary>
    private async Task UpdateMemoryAsync()
    {
        if (_env is null || _memoryBusy)
            return;
        _memoryBusy = true;
        try
        {
            var infos = await _env.GetProcessExtendedInfosAsync();
            double elapsed = _cpuClock.IsRunning ? _cpuClock.Elapsed.TotalMilliseconds : 0;
            _cpuClock.Restart();
            var parts = new[] { "jogo", "Kick", "Twitch", "placa de vídeo", "navegador e resto", "deck" }
                .ToDictionary(n => n, _ => (Bytes: 0L, Cores: 0d, Processes: 0));
            var seen = new HashSet<int>();

            void Add(string part, Process process)
            {
                long bytes = process.PrivateMemorySize64;
                var cpu = process.TotalProcessorTime;
                // Processo novo (sem leitura anterior) entra só na próxima: senão conta a CPU dele desde que abriu.
                double cores = elapsed > 0 && _cpuBefore.TryGetValue(process.Id, out var was)
                    ? Math.Max(0, (cpu - was).TotalMilliseconds / elapsed) : 0;
                _cpuBefore[process.Id] = cpu;
                seen.Add(process.Id);
                var p = parts[part];
                parts[part] = (p.Bytes + bytes, p.Cores + cores, p.Processes + 1);
            }

            foreach (var info in infos)
            {
                try
                {
                    using var process = Process.GetProcessById(info.ProcessInfo.ProcessId);
                    Add(PartOf(info), process);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // O processo acabou de fechar.
                }
            }
            using (var self = Process.GetCurrentProcess())
                Add("deck", self);
            foreach (int gone in _cpuBefore.Keys.Where(k => !seen.Contains(k)).ToList())
                _cpuBefore.Remove(gone);

            Usage = parts.Select(p => new UsagePart(p.Key, p.Value.Bytes, p.Value.Cores, p.Value.Processes)).ToList();
            long total = Usage.Sum(p => p.Bytes);
            double cores = Usage.Sum(p => p.Cores);
            // CPU em % do processador inteiro, como no Gerenciador de Tarefas.
            static string Pct(double used)
            {
                double pct = used * 100 / Environment.ProcessorCount;
                return (pct < 10 ? pct.ToString("0.#") : pct.ToString("0")) + "%";
            }
            string cpu = elapsed > 0 ? $"CPU {Pct(cores)}" : "CPU medindo…";
            // Na barra só o total, as lives e o próprio deck; o resto (jogo, placa de vídeo…) fica na dica.
            string Part(string label, string name)
            {
                var part = Usage.First(p => p.Name == name);
                return $"{label} {Gb(part.Bytes)} GB" + (elapsed > 0 ? $" {Pct(part.Cores)}" : "");
            }
            _ram.Text = $"RAM ~{Gb(total)} GB · {cpu} · {Part("Kick", "Kick")} · {Part("Twitch", "Twitch")} · {Part("Deck", "deck")}";
            _ram.ToolTipText =
                string.Join("\n", Usage.Select(p => $"{p.Name}: {p.Bytes / 1024 / 1024} MB, " +
                                                    (elapsed > 0 ? $"{Pct(p.Cores)} da CPU" : "CPU medindo…") +
                                                    $" ({p.Processes} {(p.Processes == 1 ? "processo" : "processos")})")) +
                $"\ntotal: {Gb(total)} GB, {(elapsed > 0 ? $"{Pct(cores)} da CPU ({Environment.ProcessorCount} núcleos)" : "CPU medindo…")}\n" +
                Theme.Wrap("Memória privada (a que pesa no PC); CPU dos últimos 10 s. A cada 10 min vai uma linha para o " +
                           "consumo.log, na pasta do deck. Para aliviar: feche lives no botão lives do cabeçalho, abra menos " +
                           "lives (Lives → Config…) ou desligue as contas que não está usando.", 60, 6);

            if (elapsed > 0 && DateTime.Now >= _usageLogAt)
            {
                _usageLogAt = DateTime.Now.AddMinutes(10);
                LogUsage(total, cores);
                _ = LogGameAsync();
            }
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

    /// <summary>
    /// A parte de um processo do navegador. Cada site roda no seu processo, e um iframe conta para a página que o
    /// abriu: o captcha do Cloudflare na tela de login é do jogo; o da Kick, das lives da Kick.
    /// </summary>
    private static string PartOf(CoreWebView2ProcessExtendedInfo info)
    {
        if (info.ProcessInfo.Kind == CoreWebView2ProcessKind.Gpu)
            return "placa de vídeo";
        var hosts = info.AssociatedFrameInfos.Select(PageHost).ToList();
        // deck.test, kick.test e twitch.test são os sites de mentira do autoteste.
        if (hosts.Any(h => h.EndsWith("pokeidle.io") || h == "deck.test"))
            return "jogo";
        if (hosts.Any(h => h.EndsWith("kick.com") || h == "kick.test"))
            return "Kick";
        if (hosts.Any(h => h.EndsWith("twitch.tv") || h == "twitch.test"))
            return "Twitch";
        return "navegador e resto";
    }

    /// <summary>
    /// Uma linha no consumo.log (pasta do deck), para comparar horas inteiras antes e depois de uma mudança.
    /// Passando de 2 MB (uns 2 meses), o antigo vira consumo.old.log.
    /// </summary>
    private void LogUsage(long total, double cores)
    {
        var lines = new List<string>
        {
            $"{DateTime.Now:dd/MM/yy HH:mm}  RAM {Gb(total)} GB, CPU {cores:0.00} núcleos | " +
            string.Join(" | ", Usage.Select(p => $"{p.Name} {p.Bytes / 1024 / 1024} MB {p.Cores:0.00}")),
        };
        // Na Twitch leve, quantas vezes o player abriu para o baú e o resgate (~4 por hora por live) e quantas não deram.
        var wakes = Enumerable.Range(0, Accounts).Where(i => _lives[i] is { Wakes: > 0 })
            .Select(i => $"{_config.AccountNames[i]} {_lives[i]!.Wakes}× ({_lives[i]!.WakeFailures} sem abrir)").ToList();
        if (wakes.Count > 0)
            lines.Add($"{DateTime.Now:dd/MM/yy HH:mm}  player leve abriu: {string.Join(", ", wakes)}");
        AppendUsage(lines);
    }

    private void AppendUsage(IReadOnlyCollection<string> lines)
    {
        if (lines.Count == 0)
            return;
        try
        {
            var file = new FileInfo(UsageLog);
            if (file.Exists && file.Length > 2 * 1024 * 1024)
                File.Move(file.FullName, Path.ChangeExtension(file.FullName, ".old.log"), true);
            File.AppendAllLines(UsageLog, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Arquivo aberto em outro programa: vai na próxima.
        }
    }

    /// <summary>A leitura anterior das métricas do jogo de cada conta (o navegador dá o tempo acumulado; o intervalo é a diferença).</summary>
    private readonly Dictionary<int, (DateTime At, Dictionary<string, double> Metrics)> _gameBefore = new();

    // Quantos canvas a página do jogo tem, quantas animações (CSS ou de script) estão rodando agora e quantos quadros
    // (requestAnimationFrame) por segundo o jogo teve desde a leitura anterior, com o limite do deck.
    private const string GamePageScript =
        "(() => { const n = window.__deckFrames ?? 0, k = window.__deckSpriteTicks ?? 0, at = performance.now(), was = window.__deckFramesAt;" +
        " window.__deckFramesAt = [n, at, k];" +
        " const rate = (now, i) => was && at > was[1] && now >= (was[i] ?? 0) ? ((now - (was[i] ?? 0)) * 1000 / (at - was[1])).toFixed(1) + '/s' : '?';" +
        " return `canvas ${document.querySelectorAll('canvas').length}, animações rodando ${document.getAnimations ? " +
        "document.getAnimations().filter(a => a.playState === 'running').length : '?'}, quadros ${rate(n, 0)} (limite ${window.__deckFps ?? '-'})," +
        " sprites ${rate(k, 2)} (a cada ${window.__deckSpriteMs ?? '-'} ms)`; })()";

    /// <summary>
    /// Onde o jogo de cada conta gasta CPU, para saber se limitar os quadros ajuda (item 7 do plano de consumo): o tempo da
    /// linha principal da página em script, layout, estilo e o resto (pintura, eventos), e a CPU do processo inteiro, em
    /// núcleos, desde a leitura anterior. Também o tamanho da página (nós, memória do JavaScript, ouvintes), que mostra se
    /// ela cresce com as horas. Vai para o consumo.log junto com a linha do consumo; a primeira leitura só marca o começo.
    /// </summary>
    private async Task LogGameAsync()
    {
        if (_gameBusy)
            return;
        _gameBusy = true;
        var lines = new List<string>();
        for (int i = 0; i < _panes.Length; i++)
        {
            try
            {
                if (_panes[i]?.Core is not CoreWebView2 core)
                {
                    _gameBefore.Remove(i);
                    continue;
                }
                // Ligar de novo não zera os contadores; a página que recarregou volta a contar do zero.
                await core.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");
                using var doc = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}"));
                var now = new Dictionary<string, double>();
                foreach (var metric in doc.RootElement.GetProperty("metrics").EnumerateArray())
                    now[metric.GetProperty("name").GetString() ?? ""] = metric.GetProperty("value").GetDouble();
                string page = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(GamePageScript)) ?? "";
                var at = DateTime.Now;
                if (_gameBefore.TryGetValue(i, out var before) && GameLine(before.Metrics, now, (at - before.At).TotalSeconds) is string cpu)
                    lines.Add($"{at:dd/MM/yy HH:mm}  jogo de {_config.AccountNames[i]}: {cpu} | {page}");
                _gameBefore[i] = (at, now);
            }
            catch
            {
                // A conta desligou ou a página está recarregando: começa de novo na próxima.
                _gameBefore.Remove(i);
            }
        }
        _gameBusy = false;
        AppendUsage(lines);
    }

    private bool _gameBusy;

    /// <summary>A CPU do jogo no intervalo, em núcleos, por tipo de trabalho (null: a página recarregou no meio).</summary>
    private static string? GameLine(Dictionary<string, double> was, Dictionary<string, double> now, double seconds)
    {
        double Delta(string name) => now.GetValueOrDefault(name) - was.GetValueOrDefault(name);
        double task = Delta("TaskDuration"), script = Delta("ScriptDuration"), layout = Delta("LayoutDuration"), style = Delta("RecalcStyleDuration");
        if (seconds < 1 || task < 0 || script < 0 || Delta("LayoutCount") < 0)
            return null;
        string Cores(double value) => (value / seconds).ToString("0.000");
        string process = now.ContainsKey("ProcessTime") ? $", processo inteiro {Cores(Delta("ProcessTime"))}" : "";
        return $"linha principal {Cores(task)} núcleo (script {Cores(script)}, layout {Cores(layout)} " +
               $"{Delta("LayoutCount") / seconds:0.0}/s, estilo {Cores(style)} {Delta("RecalcStyleCount") / seconds:0.0}/s, " +
               $"resto {Cores(Math.Max(0, task - script - layout - style))}){process} | nós {now.GetValueOrDefault("Nodes"):0}, " +
               $"JS {now.GetValueOrDefault("JSHeapUsedSize") / 1024 / 1024:0} MB, ouvintes {now.GetValueOrDefault("JSEventListeners"):0}";
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
        SaveConfig();
        foreach (var pane in _panes)
        {
            pane.ReduceMotion = reduce;
            await pane.ApplyReduceMotionAsync();
        }
    }

    /// <summary>O ⚡ ULTRA na barra: laranja com "ligado", como o eco.</summary>
    private void ShowUltra()
    {
        _ultraButton.Text = _config.Ultra ? "⚡ ULTRA ligado" : "⚡ ULTRA";
        _ultraButton.ForeColor = _config.Ultra ? Theme.Vote : Theme.Text;
    }

    /// <summary>O ULTRA (o mesmo do botão) e, embaixo, o menos animações.</summary>
    private void FillUltraMenu()
    {
        var items = _ultraButton.DropDownItems;
        items.Clear();
        items.Add(_config.Ultra ? "Desligar o ULTRA" : "Ligar o ULTRA (em todas as contas)", null,
            async (_, _) => await SetUltraAsync(!_config.Ultra));
        items.Add(new ToolStripSeparator());
        var motion = new ToolStripMenuItem("🐢 Menos animações (no jogo normal e na tela de login)")
        {
            Checked = _config.ReduceMotion,
            ToolTipText = "O próprio jogo desliga as animações que rodam sem parar (selos pulsando, halos, cartas). " +
                          "Gasta bem menos CPU e placa de vídeo. Desmarque para ver todas",
        };
        motion.Click += async (_, _) => await SetReduceMotionAsync(!_config.ReduceMotion);
        items.Add(motion);
        static string FpsText(int fps) => fps >= 60 ? "60 por segundo (sem limite)" : $"{fps} por segundo";
        var frames = new ToolStripMenuItem($"🎞 Quadros do mapa: {FpsText(_config.GameFps)}")
        {
            ToolTipText = "Quantas vezes por segundo o mapa da caça é desenhado. A caça roda no servidor: menos quadros só " +
                          "deixam o mapa menos fluido, e gastam bem menos CPU e placa de vídeo",
        };
        foreach (int fps in new[] { 60, 30, 15, 10 })
        {
            var choice = new ToolStripMenuItem(FpsText(fps)) { Checked = _config.GameFps == fps };
            choice.Click += async (_, _) => await SetGameFpsAsync(fps);
            frames.DropDownItems.Add(choice);
        }
        items.Add(frames);
        static string SpriteText(int ms) => ms switch
        {
            <= 220 => "normal (220 ms, como o jogo)",
            < 1000 => $"lento ({ms} ms)",
            < 3000 => $"bem lento ({ms / 1000.0:0.#} s)",
            _ => $"quase parados ({ms / 1000.0:0.#} s)",
        };
        var sprites = new ToolStripMenuItem($"🐾 Sprites animados: {SpriteText(_config.SpriteMs)}")
        {
            ToolTipText = "De quanto em quanto tempo os sprites da equipe, das listas e do mercado trocam de quadro. Cada conta " +
                          "tem 120–190 deles: mais devagar gasta bem menos CPU e placa de vídeo. Mais devagar vale na hora; mais " +
                          "rápido pega nos sprites novos ou ao recarregar a conta",
        };
        foreach (int ms in new[] { 220, 660, 1500, 5000 })
        {
            var choice = new ToolStripMenuItem(SpriteText(ms)) { Checked = _config.SpriteMs == ms };
            choice.Click += async (_, _) => await SetSpriteMsAsync(ms);
            sprites.DropDownItems.Add(choice);
        }
        items.Add(sprites);
    }

    /// <summary>Troca o ritmo dos sprites animados em todas as contas (mais devagar vale na hora).</summary>
    private async Task SetSpriteMsAsync(int ms)
    {
        _config.SpriteMs = ms;
        SaveConfig();
        foreach (var pane in _panes)
        {
            pane.SpriteMs = ms;
            await pane.ApplyGameFpsAsync();
        }
    }

    /// <summary>Troca o limite de quadros do jogo em todas as contas, na hora (sem recarregar).</summary>
    private async Task SetGameFpsAsync(int fps)
    {
        _config.GameFps = fps;
        SaveConfig();
        foreach (var pane in _panes)
        {
            pane.GameFps = fps;
            await pane.ApplyGameFpsAsync();
        }
    }

    /// <summary>
    /// Liga ou desliga o ULTRA em todas as contas, na hora. Ligado, vale também para a conta que recarrega ou
    /// é ligada depois (ela volta ao ULTRA sozinha).
    /// </summary>
    private async Task SetUltraAsync(bool on)
    {
        _config.Ultra = on;
        ShowUltra();
        SaveConfig();
        var failed = new List<string>();
        foreach (var pane in _panes)
        {
            pane.Ultra = on;
            if (!pane.Off && !await pane.SetUltraAsync(on))
                failed.Add(pane.AccountName);
        }
        if (failed.Count > 0)
            DeckBox.Show(this,
                $"Não consegui {(on ? "ligar" : "desligar")} o ULTRA em: {string.Join(", ", failed)}.\n\n" +
                "O deck usa o interruptor do próprio jogo (Configurações → Desempenho → Modo ULTRA Economia): " +
                "recarregue a conta (⟳ no cabeçalho) e tente de novo, ou ligue por lá.",
                Text, DeckBoxIcon.Warn);
    }

    // ---------- Ações das contas ----------

    private void ShowAccountMenu(int index)
    {
        var pane = _panes[index];
        var menu = new ContextMenuStrip();
        menu.Items.Add(_config.Layout == DeckLayout.Focus && _config.Focused == index ? "Voltar para a grade" : "Ampliar no deck",
            null, (_, _) => ToggleFocus(index));
        menu.Items.Add("Recarregar limpo (sem o cache do jogo)", null, (_, _) => _ = pane.ReloadCleanAsync());
        menu.Items.Add(pane.Muted ? "Ligar o som" : "Desligar o som", null, (_, _) => pane.SetMuted(!pane.Muted));
        menu.Items.Add("Renomear…", null, (_, _) => RenameAccount(index));
        menu.Items.Add(new ToolStripMenuItem("Apagar os dados da conta (entrar de novo)…", null, async (_, _) => await WipeAccountAsync(index))
        {
            Enabled = !pane.Off && pane.Core is not null,
        });
        menu.Items.Add(pane.Off ? "Ligar esta conta" : "Desligar esta conta (fica no deck, sem rodar nada)",
            null, async (_, _) => await SetPaneOffAsync(index, !pane.Off));
        menu.Items.Add("🩺 Gerar o diagnóstico (o arquivo para mandar quando o jogo não carrega)…", null, async (_, _) => await CreateDiagnosticAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Lives da Twitch/Kick nesta conta", null, async (_, _) => await ToggleLivesAsync(index))
        {
            Checked = _config.LivesAccounts.Contains(index + 1),
        });
        // O carrinho e o ⚔ também aqui: em colunas, desligados, eles saem do cabeçalho para os outros botões caberem.
        if (!pane.Off)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Recompra…", null, (_, _) => ShowRestockWindow(index));
            var pvpMenu = new ToolStripMenuItem("⚔ Fila de PvP") { Checked = _config.Pvp[index] };
            FillPvpMenu(index, pvpMenu.DropDownItems);
            menu.Items.Add(pvpMenu);
        }
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
            _restockWindows[index]?.Close();
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

    // ---------- Recompra ----------

    /// <summary>A janela da recompra da conta (o carrinho no cabeçalho); aberta de novo, só vem para a frente.</summary>
    private void ShowRestockWindow(int index)
    {
        if (_restockWindows[index] is RestockWindow open)
        {
            open.Activate();
            return;
        }
        var pane = _panes[index];
        var window = new RestockWindow(pane.AccountName, pane.RestockConfig);
        window.Changed += () =>
        {
            SaveConfig();
            _ = ApplyRestockAsync(index);
        };
        window.FormClosed += (_, _) => _restockWindows[index] = null;
        _restockWindows[index] = window;
        window.Show(this);
        _ = RefreshRestockAsync(index);
    }

    /// <summary>A configuração nova vale na hora: vai para a página e o carrinho e a janela mostram o resultado.</summary>
    private async Task ApplyRestockAsync(int index)
    {
        await _panes[index].PushRestockAsync();
        await RefreshRestockAsync(index);
    }

    /// <summary>Lê da página o estoque e o registro, para o carrinho do cabeçalho e a janela aberta.</summary>
    private async Task RefreshRestockAsync(int index)
    {
        if (_restockBusy[index])
            return;
        _restockBusy[index] = true;
        try
        {
            var status = await _panes[index].ReadRestockAsync();
            _panes[index].ShowRestock(status);
            _restockWindows[index]?.ShowStatus(status);
        }
        finally
        {
            _restockBusy[index] = false;
        }
    }

    // ---------- Fila de PvP ----------

    /// <summary>Liga ou desliga a fila de PvP da conta (o ⚔ do cabeçalho e o menu da conta); vale na hora.</summary>
    private async Task SetPvpAsync(int index, bool on)
    {
        _config.Pvp[index] = on;
        _panes[index].PvpOn = on;
        SaveConfig();
        await _panes[index].PushPvpAsync();
        await RefreshPvpAsync(index);
    }

    /// <summary>
    /// Como as partidas da fila aparecem na conta (completa, mini ou off) e a velocidade delas (0 = a do jogo); vale na
    /// próxima partida, com a fila ligada.
    /// </summary>
    private async Task SetPvpViewAsync(int index, string screen, double speed)
    {
        _config.PvpScreen[index] = _panes[index].PvpScreen = PvpScreens.Normalized(screen);
        _config.PvpSpeed[index] = _panes[index].PvpSpeed = PvpScreens.NormalizedSpeed(speed);
        SaveConfig();
        await _panes[index].PushPvpAsync();
        await RefreshPvpAsync(index);
    }

    /// <summary>Botão direito no ⚔ do cabeçalho: ligar a fila, a tela das partidas e a velocidade.</summary>
    private void ShowPvpMenu(int index)
    {
        var menu = new ContextMenuStrip();
        FillPvpMenu(index, menu.Items);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_panes[index].PvpMenuPoint, ToolStripDropDownDirection.BelowLeft);
    }

    /// <summary>O menu da fila de PvP (o botão direito no ⚔ e o "⚔ Fila de PvP" do menu da conta).</summary>
    private void FillPvpMenu(int index, ToolStripItemCollection items)
    {
        string screen = PvpScreens.Normalized(_config.PvpScreen[index]);
        double speed = PvpScreens.NormalizedSpeed(_config.PvpSpeed[index]);
        items.Add(new ToolStripMenuItem("Fila ligada (entra de novo depois de cada partida)", null,
            (_, _) => _ = SetPvpAsync(index, !_config.Pvp[index])) { Checked = _config.Pvp[index] });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Partidas da fila:") { Enabled = false });
        // Economia ou ULTRA: o jogo esconde as partidas de qualquer jeito; o menu avisa antes da escolha.
        if (PvpScreens.NoSceneNote(_panes[index].PvpNoScene) is string noScene)
            items.Add(new ToolStripMenuItem("⚠ " + (_panes[index].PvpNoScene == "economia"
                ? "Battery Saver ligado: mostrar ou minimizar desliga ele com a fila ligada (o deck religa ao esconder)"
                : "Tela ULTRA ligada: o jogo esconde as partidas assim mesmo"))
            {
                Enabled = false, ToolTipText = noScene,
            });
        foreach (var (value, text) in new[]
                 {
                     (PvpScreens.Full, "Mostrar a luta (o replay na tela)"),
                     (PvpScreens.Mini, "Minimizada (a janelinha do canto)"),
                     (PvpScreens.Off, "Escondida (só o aviso do resultado)"),
                 })
            items.Add(new ToolStripMenuItem(text, null, (_, _) => _ = SetPvpViewAsync(index, value, _config.PvpSpeed[index]))
            {
                Checked = screen == value,
            });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Velocidade da luta (mostrada ou minimizada):") { Enabled = false });
        items.Add(new ToolStripMenuItem("A do jogo (acelera só para caber até a próxima)", null,
            (_, _) => _ = SetPvpViewAsync(index, _config.PvpScreen[index], 0)) { Checked = speed == 0, Enabled = screen != PvpScreens.Off });
        foreach (double s in PvpScreens.Speeds)
            items.Add(new ToolStripMenuItem(PvpScreens.SpeedLabel(s), null, (_, _) => _ = SetPvpViewAsync(index, _config.PvpScreen[index], s))
            {
                Checked = speed == s, Enabled = screen != PvpScreens.Off,
            });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem($"Usar nas outras contas (partidas {PvpScreens.Label(screen)}s" +
                                        (screen == PvpScreens.Off ? "" : $", {PvpScreens.SpeedLabel(speed)}") + ")", null, async (_, _) =>
        {
            for (int i = 0; i < Accounts; i++)
                if (i != index)
                    await SetPvpViewAsync(i, screen, speed);
        }));
    }

    /// <summary>Lê da página a fila de PvP para o ⚔ do cabeçalho.</summary>
    private async Task RefreshPvpAsync(int index)
    {
        if (_pvpBusy[index])
            return;
        _pvpBusy[index] = true;
        try
        {
            _panes[index].ShowPvp(await _panes[index].ReadPvpAsync());
        }
        finally
        {
            _pvpBusy[index] = false;
        }
    }

    // ---------- O jogo que não abre ----------

    /// <summary>Anota em aberturas.log (pasta do deck) cada vez que uma conta recarregou porque o jogo não abriu.</summary>
    private void LogOpening(AccountPane pane, string text)
    {
        try
        {
            var path = Path.Combine(_root, "aberturas.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                File.WriteAllLines(path, File.ReadAllLines(path).TakeLast(500));
            File.AppendAllText(path, $"{DateTime.Now:dd/MM/yy HH:mm:ss}  {pane.AccountName} · {text}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Fica para a próxima.
        }
    }

    // ---------- Guarda do bônus: o bônus escolhido não vence com pontos parados em outros canais ----------

    private readonly bool[] _bonusBusy = new bool[Accounts];

    /// <summary>
    /// Lê o bônus das lives no jogo (o bonus.js da página): o tempo que falta vai para a dica do cabeçalho, e a guarda do
    /// bônus age se ele está vencendo.
    /// </summary>
    private async Task RefreshBonusAsync(int index)
    {
        if (_bonusBusy[index])
            return;
        _bonusBusy[index] = true;
        try
        {
            var pane = _panes[index];
            if (Bonus.Parse(await pane.ReadBonusAsync()) is not BonusSummary summary)
            {
                pane.ShowInfoTip(null);
                return;
            }
            pane.ShowInfoTip(BonusLine(index, summary));
            await GuardBonusAsync(index, summary);
        }
        finally
        {
            _bonusBusy[index] = false;
        }
    }

    /// <summary>O tempo que falta do bônus escolhido em cada site, para a dica do cabeçalho; null sem bônus escolhido.</summary>
    private string? BonusLine(int index, BonusSummary summary)
    {
        var parts = new List<string>();
        foreach (var (site, info, reward) in new[]
                 {
                     (LiveSite.Twitch, summary.Twitch, _config.RedeemTwitch[index]), (LiveSite.Kick, summary.Kick, _config.RedeemKick[index]),
                 })
        {
            if (reward is not KickReward r || info is null)
                continue;
            int left = info.Left.GetValueOrDefault(BonusGuard.Kind(r));
            parts.Add($"{site} {BonusGuard.KindName(r)}: " + (!info.Linked ? "não vinculada no jogo" : left > 0 ? BonusGuard.Time(left) : "vencido"));
        }
        return parts.Count == 0 ? null : "Bônus das lives: " + string.Join(" · ", parts) + (_config.BonusGuard[index] ? " (guarda do bônus ligada)" : "");
    }

    /// <summary>
    /// A guarda do bônus da conta (<see cref="BonusGuard"/>), a cada leitura do bônus: em cada site com bônus escolhido,
    /// vinculado no jogo e vencendo em menos de 30 min, lê o saldo dos canais fora das lives abertas e resgata 1 no
    /// escolhido, pelo mesmo script do resgate automático, numa página escondida do canal. Depois confere no jogo se o
    /// tempo do bônus subiu; se não subir, para a guarda naquele site por 1 h e diz no registro.
    /// </summary>
    private async Task GuardBonusAsync(int index, BonusSummary summary)
    {
        if (_env is null || !_config.BonusGuard[index] || _panes[index].Off || !summary.Connected)
            return;
        foreach (var site in new[] { LiveSite.Twitch, LiveSite.Kick })
        {
            var reward = site == LiveSite.Kick ? _config.RedeemKick[index] : _config.RedeemTwitch[index];
            var info = site == LiveSite.Kick ? summary.Kick : summary.Twitch;
            var redeemer = site == LiveSite.Kick ? _redeemKick[index] : _redeemTwitch[index];
            if (reward is not KickReward r || info is not { Linked: true } || redeemer.Stopped is not null)
                continue;
            var slot = _guard.SlotOf(index, site);
            if (slot.Busy || DateTime.Now < slot.HoldUntil)
                continue;
            string kind = BonusGuard.KindName(r);
            int left = info.Left.GetValueOrDefault(BonusGuard.Kind(r));
            void Log(string text) => _redeemLog.Add(index, _config.AccountNames[index], site, text);
            // O último resgate da guarda: o jogo tem de mostrar o tempo a mais (1 h ou mais).
            if (slot.RedeemedAt is DateTime at)
            {
                double since = (DateTime.Now - at).TotalSeconds;
                if (left > slot.LeftBefore - since + 1800)
                    slot.RedeemedAt = null;
                else if (DateTime.Now - at > _guard.ConfirmWithin)
                {
                    slot.RedeemedAt = null;
                    slot.HoldUntil = DateTime.Now.AddHours(1);
                    Log($"guarda do bônus: o resgate das {at:HH:mm} não somou no bônus de {kind} do jogo ({site} vinculada?); paro a guarda aqui por 1 h");
                    continue;
                }
                else
                    continue;
            }
            if (left > _guard.Threshold.TotalSeconds)
            {
                slot.NoneNoted = false;
                continue;
            }
            slot.Busy = true;
            try
            {
                string? token, device = null;
                if (site == LiveSite.Kick)
                    token = await ReadKickTokenAsync(index);
                else
                    (token, device) = await ReadTwitchLoginAsync(index);
                if (token is null)
                    continue;
                var candidates = BonusGuard.Candidates(_config.Channels(site), info, _lives[index]?.Channels(site) ?? Enumerable.Empty<string>());
                foreach (var channel in candidates.Where(c => _guard.Stale(slot, c)))
                    BonusGuard.Remember(slot, channel, site == LiveSite.Kick
                        ? await PointsRedeemer.KickReadAsync(KickReadApi, channel, token, r)
                        : await PointsRedeemer.TwitchReadAsync(TwitchReadApi, channel, token, device, r));
                string when = left > 0 ? $"vence em {BonusGuard.Time(left)}" : "venceu";
                if (BonusGuard.Pick(slot, candidates, info) is not { } pick)
                {
                    if (!slot.NoneNoted)
                    {
                        slot.NoneNoted = true;
                        Log($"guarda do bônus: o de {kind} {when}, e nenhum canal fora das lives abertas tem os pontos ({candidates.Count} conferidos)");
                    }
                    slot.HoldUntil = DateTime.Now.AddMinutes(10);
                    continue;
                }
                var (chosen, points, _, live) = pick;
                Log($"{chosen}: guarda do bônus: o de {kind} {when}; resgato aqui ({points} pontos" + (live ? ")" : ", canal offline)"));
                string script = site == LiveSite.Kick
                    ? redeemer.KickScript(KickApi, chosen, token, r)
                    : redeemer.TwitchScript(TwitchApi, chosen, token, device, r, claim: false);
                string result = await RunInChannelAsync(index, site, chosen, script);
                if (redeemer.Apply(chosen, result) is string note)
                    _panes[index].Note(note);
                slot.Reads.Remove(chosen);
                if (result.Contains("\"redeemed\":true"))
                {
                    slot.RedeemedAt = DateTime.Now;
                    slot.LeftBefore = left;
                    slot.NoneNoted = false;
                    slot.HoldUntil = DateTime.Now.AddMinutes(1);
                }
                else
                {
                    Log($"{chosen}: guarda do bônus: o resgate não passou ({(result.Length == 0 ? "a página do canal não respondeu" : result)}); tento de novo em 10 min");
                    slot.HoldUntil = DateTime.Now.AddMinutes(10);
                }
            }
            finally
            {
                slot.Busy = false;
            }
        }
    }

    /// <summary>
    /// Roda o script num canal que não está aberto na conta: a página do canal (o player parado, sem vídeo; abre mesmo
    /// offline) escondida, no perfil da conta, só pelo tempo do script. Devolve o texto que ele entrega ("" sem resposta).
    /// </summary>
    private async Task<string> RunInChannelAsync(int index, LiveSite site, string channel, string script)
    {
        if (_env is null)
            return "";
        string url = site == LiveSite.Kick
            ? KickUrl(channel) + (KickUrl(channel).Contains('?') ? "&" : "?") + TwitchLive.Still
            : TwitchUrl(channel) + "&" + TwitchLive.Still;
        var view = new Microsoft.Web.WebView2.WinForms.WebView2 { Visible = false, DefaultBackgroundColor = Theme.Back };
        Controls.Add(view);
        try
        {
            var options = _env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = _panes[index].ProfileName;
            _ = view.Handle;
            await view.EnsureCoreWebView2Async(_env, options);
            var core = view.CoreWebView2;
            core.IsMuted = true;
            SelfTestSetup?.Invoke(core);
            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate(url);
            if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30))) != loaded.Task || !loaded.Task.Result)
                return "";
            var call = core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }));
            if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(45))) != call)
                return "";
            using var doc = JsonDocument.Parse(await call);
            return doc.RootElement.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var text)
                   && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
        }
        catch
        {
            // A página fechou no meio, ou o deck está fechando.
            return "";
        }
        finally
        {
            Controls.Remove(view);
            view.Dispose();
        }
    }

    private void RenameAccount(int index)
    {
        var name = Prompt.Ask(this, "Renomear conta", "Nome que aparece no deck (por exemplo, o nick do jogo):", _config.AccountNames[index], "Salvar");
        if (string.IsNullOrWhiteSpace(name))
            return;
        _config.AccountNames[index] = name;
        _panes[index].AccountName = name;
        _panes[index].RefreshHeader();
        SaveConfig();
        UpdateBar();
    }

    /// <summary>
    /// Apaga os dados da conta para ela entrar de novo (a conta que trava depois da carga mesmo recarregando limpo): só os do
    /// jogo, ou tudo (sai da Twitch e da Kick também). As lives abertas da conta fecham no "tudo" e abrem de novo sem login.
    /// </summary>
    private async Task WipeAccountAsync(int index)
    {
        var pane = _panes[index];
        int choice = DeckBox.Choose(this,
            $"Apagar os dados de {pane.AccountName}?\n\n" +
            "Apagar o jogo: sai da conta do jogo e apaga tudo o que o site guarda nesta conta do deck (login, id do aparelho, " +
            "cookies, cache e service workers). O jogo abre na tela de login: entre de novo com o e-mail e a senha.\n\n" +
            "Apagar tudo: o mesmo e os cookies de todos os sites, então sai também da Twitch e da Kick (o resgate dos pontos " +
            "e as lives desta conta pedem o login de novo).\n\n" +
            "As extensões, as configurações delas e as do deck (nome, recompra, fila de PvP) ficam.",
            "Apagar os dados da conta", "Apagar o jogo", "Apagar tudo", danger: true);
        if (choice == 0)
            return;
        bool everything = choice == 2;
        if (everything)
            CloseLives(index);
        bool ok = await pane.WipeAsync(everything);
        pane.Note(ok ? "dados apagados: entre de novo na conta" : "não consegui apagar tudo: recarregando assim mesmo");
        if (everything && _config.LivesAccounts.Contains(index + 1))
            await OpenLivesAsync(index);
    }

    // ---------- 🩺 Diagnóstico: o jogo que não carrega ----------

    private readonly ToolStripStatusLabel _diagStatus = new("🩺 Gerar diagnóstico");
    private bool _diagBusy;

    /// <summary>O deck agora, para o relatório do diagnóstico: as contas, as opções que mexem no jogo, as lives e o consumo.</summary>
    private IEnumerable<string> DeckLines()
    {
        yield return "contas: " + string.Join("; ", _panes.Select(p => $"{p.ProfileName} {p.AccountName} {(_config.Off[p.Index] ? "desligada" : "ligada")}"));
        yield return $"zoom {_config.Zoom[0]:0.##}; menos animações {_config.ReduceMotion}; quadros {_config.GameFps}/s; sprites {_config.SpriteMs} ms; " +
                     $"ULTRA {_config.Ultra}; layout {_config.Layout}";
        yield return "lives nas contas: " + string.Join(", ", _config.LivesAccounts) + $"; Twitch {_config.TwitchLives}, Kick {_config.KickLives}";
        yield return "automações ligadas: " + string.Join("; ", _panes.Select(p =>
            $"{p.ProfileName} recompra={_config.Restock[p.Index].On} pvp={_config.Pvp[p.Index]} guarda do bônus={_config.BonusGuard[p.Index]}"));
        yield return Diag.Cut($"consumo: {_ram.Text} {_ram.ToolTipText}", 600);
    }

    /// <summary>
    /// 🩺 Gerar diagnóstico (a barra de baixo e o menu da conta): o .zip na Área de trabalho, para a pessoa do PC onde o jogo
    /// não carrega mandar, e abre o Explorer nele.
    /// </summary>
    private async Task CreateDiagnosticAsync()
    {
        if (_diagBusy)
            return;
        _diagBusy = true;
        _diagStatus.Text = "🩺 gerando o diagnóstico…";
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string folder = Directory.Exists(desktop) ? desktop : _root;
            string zip = await Diag.CreateReportAsync(folder, _env, _panes, DeckLines().ToList(), _root, outside: true);
            DeckBox.Show(this,
                $"Pronto: {Path.GetFileName(zip)}, na {(folder == desktop ? "Área de trabalho" : folder)}.\n\n" +
                "Mande este arquivo para quem cuida do deck. Ele tem o log da carga do jogo de cada conta, prints das contas e as " +
                "informações do PC e da rede; não tem senha, token nem cookie.", Text);
            try
            {
                Process.Start("explorer.exe", $"/select,\"{zip}\"");
            }
            catch
            {
                // Sem o Explorer, o caminho está na caixa.
            }
        }
        catch (Exception ex)
        {
            Diag.Write("ERRO ao gerar o diagnóstico: " + ex);
            DeckBox.Show(this, "Não consegui gerar o diagnóstico.\n\n" + ex.Message, Text, DeckBoxIcon.Error);
        }
        finally
        {
            _diagStatus.Text = "🩺 Gerar diagnóstico";
            _diagBusy = false;
        }
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

    // ---------- Eco e zoom ----------

    private static readonly double[] ZoomLevels = { 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5 };

    /// <summary>O zoom (o mesmo em todas as contas) no botão; − e + apagam nas pontas.</summary>
    private void ShowZoom()
    {
        double zoom = _config.Zoom[0];
        _zoomButton.Text = $"Zoom {zoom * 100:0}%";
        _zoomOutButton.Enabled = zoom > ZoomLevels[0] + 0.001;
        _zoomInButton.Enabled = zoom < ZoomLevels[^1] - 0.001;
    }

    /// <summary>O − e o +: o nível de zoom anterior ou o seguinte da lista.</summary>
    private void StepZoom(int direction)
    {
        double zoom = _config.Zoom[0];
        SetZoomAll(direction < 0
            ? ZoomLevels.LastOrDefault(level => level < zoom - 0.001, ZoomLevels[0])
            : ZoomLevels.FirstOrDefault(level => level > zoom + 0.001, ZoomLevels[^1]));
    }

    private void FillZoomMenu()
    {
        var items = _zoomButton.DropDownItems;
        items.Clear();
        foreach (var level in ZoomLevels)
        {
            var item = new ToolStripMenuItem($"{level * 100:0}%") { Checked = Math.Abs(_config.Zoom[0] - level) < 0.01 };
            item.Click += (_, _) => SetZoomAll(level);
            items.Add(item);
        }
    }

    /// <summary>O eco só repete cliques; o zoom vale para todas as contas com ele ligado ou não.</summary>
    private void SetEcho(bool on)
    {
        _echo = on;
        _echoButton.Text = on ? "⇄ Eco ligado" : "⇄ Eco";
        _echoButton.ForeColor = on ? Theme.Vote : Theme.Text;
    }

    /// <summary>
    /// Zoom (Ctrl + roda, Ctrl + 0) ou som de uma conta mudou. O zoom vale sempre para todas as contas. O WebView2
    /// só avisa zoom mudado pela pessoa, nunca o que o deck põe, então espalhar o zoom não volta aqui.
    /// </summary>
    private void OnPaneSettingsChanged(AccountPane pane)
    {
        _config.Muted[pane.Index] = pane.Muted;
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
        ShowZoom();
    }

    /// <summary>Repete nas outras contas o clique feito numa; avisa no cabeçalho de quem não tem esse botão.</summary>
    private async Task EchoAsync(AccountPane source, Echo.Click click)
    {
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
            DeckBox.Show(this,
                $"Não consegui {(enabled ? "ligar" : "desligar")} o sniper em: {string.Join(", ", failed)}.\n\n" +
                "O deck mexe no Rotom pela página do jogo: recarregue a conta (⟳ no cabeçalho) e tente de novo.",
                Text, DeckBoxIcon.Warn);
    }

    // ---------- Voto no IdleHub ----------

    /// <summary>A cada segundo: atualiza os botões de voto e pergunta ao jogo das contas cuja hora chegou.</summary>
    private void UpdateVotes()
    {
        // O autoteste não sai para a internet; ele confere o voto por partes.
        if (_env is null || _selfTest)
            return;
        for (int i = 0; i < Accounts; i++)
        {
            if (!_voteBusy[i] && DateTime.Now >= _voteRefreshAt[i] && _panes[i].Core is not null && !_panes[i].Off)
                _ = RefreshVoteAsync(i);
            RenderVote(i);
        }
        RenderVoteStatus();
    }

    // Acabou de votar: o aviso do IdleHub leva uns segundos para chegar ao jogo.
    private bool JustVoted(int index) => DateTime.Now - _votedAt[index] < TimeSpan.FromMinutes(3);

    private async Task RefreshVoteAsync(int index)
    {
        if (_panes[index].Core is not CoreWebView2 core)
            return;
        _voteBusy[index] = true;
        try
        {
            var ask = IdleHub.GetStateAsync(core);
            if (await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(30))) != ask)
                throw new TimeoutException();
            var state = await ask;
            var now = DateTime.Now;
            if (state is null)
            {
                // A página não está no jogo (carregando, recarregando) ou o jogo não respondeu: de novo logo.
                _voteRefreshAt[index] = now.AddMinutes(1);
                return;
            }
            if (state.Ready && JustVoted(index))
            {
                // O jogo ainda não recebeu o voto: segura o "votou" e pergunta de novo.
                _voteRefreshAt[index] = now.AddSeconds(20);
                return;
            }
            _votes[index] = state;
            // De novo a cada 30 min, ou logo depois que o próximo voto liberar; sem login no jogo, em 2 min.
            var next = now.AddMinutes(30);
            if (!state.LoggedIn)
                next = now.AddMinutes(2);
            else if (state.NextVoteAt is DateTime at && at > now && at.AddSeconds(3) < next)
                next = at.AddSeconds(3);
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
        var state = _votes[index];
        // Sem login no jogo ou com o Vote & Ganhe desligado no servidor: o voto não daria nada.
        if (state is { LoggedIn: false } or { Active: false })
            pane.HideVote();
        else if (JustVoted(index) && (state is null || state.Ready))
            pane.SetVote("🗳 ✓ Votou", Theme.Muted, false, "Voto registrado no IdleHub.io. Conferindo no jogo quando libera o próximo…");
        else if (state is null)
            pane.SetVote("🗳 IdleHub", Theme.Muted, false, "Conferindo no jogo o voto desta conta no IdleHub.io…");
        else if (state.Ready)
            pane.SetVote("🗳 Votar", Theme.Vote, true,
                $"Voto liberado no IdleHub.io{(state.Nick is null ? "" : $" para {state.Nick}")}. Clique para votar: quem vota é você, " +
                "na página do IdleHub (na primeira vez, entre com o Google ou o Discord: o login fica salvo nesta conta)." + VoteProgress(state));
        else
            pane.SetVote("🗳 " + FormatLeft(state.NextVoteAt!.Value - DateTime.Now), Theme.Muted, false,
                $"Esta conta já votou. O próximo voto libera {FormatWhen(state.NextVoteAt.Value)}." + VoteProgress(state));
    }

    /// <summary>O total de votos e quanto falta para o próximo diamante, como o painel do jogo mostra.</summary>
    private static string VoteProgress(VoteState state)
    {
        if (state.VotesPerReward <= 0)
            return "";
        int left = Math.Max(1, state.VotesPerReward - state.InPair);
        return $"\n{state.Votes} votos · {(left == 1 ? "falta 1 voto" : $"faltam {left} votos")} para o próximo 💎 " +
               $"({state.PerPair} 💎 a cada {state.VotesPerReward} votos)";
    }

    /// <summary>
    /// A janela de voto viu o voto entrar: o botão vira "votou" (ou já conta o tempo, se o IdleHub disse quando libera)
    /// e o jogo é consultado logo em seguida.
    /// </summary>
    private void MarkVoted(int index, DateTime? next)
    {
        _votedAt[index] = DateTime.Now;
        if (_votes[index] is VoteState state && next is DateTime at && at > DateTime.Now)
            _votes[index] = state with { NextVoteAt = at };
        _voteRefreshAt[index] = DateTime.Now.AddSeconds(3);
        RenderVote(index);
        RenderVoteStatus();
    }

    private void RenderVoteStatus()
    {
        var on = Enumerable.Range(0, Accounts).Where(i => !_panes[i].Off && _panes[i].VoteText is not null).ToList();
        if (on.Count == 0)
        {
            _voteStatus.Text = "";
            return;
        }
        var ready = on.Where(i => _votes[i]?.Ready == true && !JustVoted(i)).Select(i => _config.AccountNames[i]).ToList();
        var next = on.Select(i => _votes[i]).Where(v => v is { LoggedIn: true, Active: true, Ready: false, NextVoteAt: not null })
            .Select(v => v!.NextVoteAt!.Value).DefaultIfEmpty().Min();
        _voteStatus.ForeColor = ready.Count > 0 ? Theme.Vote : Theme.Muted;
        _voteStatus.Text = ready.Count > 0 ? "🗳 Voto liberado: " + string.Join(", ", ready)
            : next > DateTime.Now ? "🗳 Próximo voto em " + FormatLeft(next - DateTime.Now)
            : "🗳 Vote no IdleHub pelo botão de cada conta";
    }

    private static string FormatLeft(TimeSpan left) =>
        left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";

    private static string FormatWhen(DateTime at) =>
        at.Date == DateTime.Today ? $"hoje às {at:HH:mm}" : at.Date == DateTime.Today.AddDays(1) ? $"amanhã às {at:HH:mm}" : $"em {at:dd/MM} às {at:HH:mm}";

    /// <summary>
    /// Abre a página de voto do IdleHub no navegador da conta (onde fica o login do IdleHub dela), já com o nick do
    /// personagem. Quem vota é você; o deck lê a resposta do voto e atualiza o botão.
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
        // O link que o jogo deu (com o nick dele); sem ele, o nick salvo pelo jogo.
        url ??= _votes[index]?.Url ?? IdleHub.VoteUrl(await pane.ReadNickAsync());

        var window = await PopupWindow.CreateAsync(_env, pane.ProfileName, new Size(1100, 820), closeOnDeactivate: false);
        var area = Screen.FromControl(this).WorkingArea;
        window.Location = new Point(area.X + Math.Max(0, (area.Width - window.Width) / 2), area.Y + Math.Max(0, (area.Height - window.Height) / 2));
        string prefix = $"Votar com {pane.AccountName} · quem vota é você, nesta página do IdleHub.io";
        window.Text = prefix;
        var core = window.WebView.CoreWebView2;
        core.DocumentTitleChanged += (_, _) => window.Text = prefix;
        core.WebResourceResponseReceived += async (_, e) =>
        {
            if (e.Response is null || !IdleHub.IsVoteResponse(e.Request.Method, e.Request.Uri, e.Response.StatusCode))
                return;
            string body;
            try
            {
                using var stream = await e.Response.GetContentAsync();
                if (stream is null)
                    return;
                using var reader = new StreamReader(stream);
                body = await reader.ReadToEndAsync();
            }
            catch
            {
                return;
            }
            if (IdleHub.TryParseVoted(body, out var next) && !window.IsDisposed)
            {
                prefix = $"✓ Voto registrado com {pane.AccountName}! Pode fechar esta janela";
                window.Text = prefix;
                MarkVoted(index, next);
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

    // A live da Twitch toca só o player (o "player em janela" do site), como na Kick.
    private string TwitchUrl(string channel) => _selfTest
        ? $"https://twitch.test/twitch.html?canal={Uri.EscapeDataString(channel)}"
        : TwitchLive.PlayerUrl(channel);

    // A live da Twitch da conta: na Twitch leve, o player parado (sem vídeo); quem assiste é o deck (TwitchWatcher).
    private string TwitchLiveUrl(int index, string channel) =>
        _config.TwitchLight[index] ? TwitchUrl(channel) + "&" + TwitchLive.Still : TwitchUrl(channel);

    // A live da Kick toca só o player: ele avisa a Kick que a conta está assistindo, como a página do canal
    // (testado: os pontos sobem igual), gastando uns 40% menos. A página do canal só abre para o chat e os
    // pontos, com a janela das lives na tela.
    private string KickUrl(string channel) => _selfTest
        ? $"https://kick.test/kick.html?canal={Uri.EscapeDataString(channel)}"
        : $"https://player.kick.com/{channel}";

    // A live da Kick da conta: na Kick leve, em branco (abre só para resgatar); quem assiste é o deck (KickWatcher).
    private string KickLiveUrl(int index, string channel) =>
        _config.KickLight[index] ? KickUrl(channel) + (KickUrl(channel).Contains('?') ? "&" : "?") + TwitchLive.Still : KickUrl(channel);

    // A página do chat, com o botão dos pontos, que abre por cima do player só no 💰: na Kick a do canal, na Twitch o
    // chat em janela (bem mais leve que a página do canal, e com o botão dos pontos e o baú). Na Twitch, com "Página
    // inteira" (full), o site do canal, com o menu da conta (sair, tela cheia…), como a da Kick.
    private string ChatUrl(LiveSite site, string channel, bool full) => (site, _selfTest) switch
    {
        (LiveSite.Kick, true) => $"https://kick.test/kick.html?canal={Uri.EscapeDataString(channel)}&chat=1",
        (LiveSite.Kick, false) => $"https://kick.com/{channel}",
        (_, true) => $"https://twitch.test/twitch.html?canal={Uri.EscapeDataString(channel)}" + (full ? "&inteira=1" : "&chat=1"),
        _ => full ? $"https://www.twitch.tv/{Uri.EscapeDataString(channel)}"
            : $"https://www.twitch.tv/popout/{Uri.EscapeDataString(channel)}/chat?popout=",
    };

    private async Task OpenLivesAsync(int index)
    {
        if (_env is null || _lives[index] is not null || _panes[index].Off)
            return;
        var urls = new List<string>();
        int twitchSlots = Math.Min(_config.TwitchLives, _config.TwitchChannels.Count);
        if (twitchSlots > 0)
        {
            // Sem login da Twitch nesta conta ainda: uma aba para entrar, e os players só abrem depois (sem login não
            // rendem pontos). O que vale é o token: o cookie com o nick nem sempre existe.
            _twitchLogin[index] = _selfTest || (await ReadTwitchLoginAsync(index)).Token is not null;
            if (_twitchLogin[index] == false)
                urls.Add("https://www.twitch.tv/login");
        }

        int kickSlots = Math.Min(_config.KickLives, _config.KickChannels.Count);
        if (urls.Count > 0 || twitchSlots > 0 || kickSlots > 0)
        {
            _lives[index] = await LivesWindow.CreateAsync(_env, _panes[index].ProfileName, _config.AccountNames[index], urls,
                twitchSlots, kickSlots, ChatUrl, SelfTestSetup);
            var window = _lives[index]!;
            if (_selfTest)
            {
                // O player da Twitch leve: no autoteste abre na hora e fecha logo depois da ação.
                window.IdleClose = TimeSpan.FromSeconds(1);
                window.WakeSettle = TimeSpan.Zero;
            }
            window.NextRequested += site => ShowNextPoints(index, site);
            window.PointsHeight = _config.KickPointsHeight;
            window.PointsHeightChanged += height =>
            {
                _config.KickPointsHeight = height;
                SaveConfig();
                foreach (var other in _lives.OfType<LivesWindow>())
                    other.PointsHeight = height;
            };
            ApplyLives();
            // Confere logo quem está no ar (o autoteste não sai para a internet: abre as primeiras da lista), e
            // o saldo dos pontos 1 min depois, com os players já carregados.
            _liveCheckAt = DateTime.Now;
            _kickRedeemAt = _twitchPointsAt = DateTime.Now.AddMinutes(1);
        }
        UpdateBar();
    }

    /// <summary>
    /// Pergunta à Twitch e à Kick quais canais da lista estão no ar (a cada 2 min, enquanto houver lives abertas).
    /// A conta que entrou na Twitch desde a última conferência fecha a página de entrar e passa a abrir os players.
    /// </summary>
    private async Task CheckLivesAsync()
    {
        _liveChecking = true;
        _liveCheckAt = DateTime.Now.AddMinutes(2);
        try
        {
            var kick = _config.KickLives > 0 ? _config.KickChannels.ToList() : new List<string>();
            var twitch = _config.TwitchLives > 0 ? _config.TwitchChannels.ToList() : new List<string>();
            var kickLive = Task.WhenAll(kick.Select(KickLive.IsLiveAsync));
            var twitchLive = TwitchLive.LiveAsync(twitch);
            var kickNow = await kickLive;
            for (int i = 0; i < kick.Count; i++)
                _kickLive[kick[i]] = kickNow[i];
            foreach (var (channel, live) in await twitchLive)
                _twitchLive[channel] = live;
            for (int i = 0; i < Accounts; i++)
                if (_lives[i] is LivesWindow window && _twitchLogin[i] == false && (await ReadTwitchLoginAsync(i)).Token is not null)
                {
                    _twitchLogin[i] = true;
                    window.CloseFixed();
                }
        }
        finally
        {
            _liveChecking = false;
        }
        ApplyLives();
    }

    /// <summary>
    /// A cada 30 s, se as lives abertas estão mesmo tocando (vídeo parado não rende pontos); a que caiu e não
    /// voltou sozinha é recarregada.
    /// </summary>
    private async Task WatchLivesAsync()
    {
        if (_watching)
            return;
        _watching = true;
        try
        {
            foreach (var window in _lives.OfType<LivesWindow>().ToList())
                if (!window.IsDisposed)
                    await window.WatchAsync();
        }
        finally
        {
            _watching = false;
        }
        UpdateBar();
    }

    // A API da Kick (no autoteste, a de mentira em kick.test).
    private string KickApi => _selfTest ? "https://kick.test" : "https://kick.com";

    /// <summary>
    /// A cada 5 min, nas contas com o resgate marcado em Lives → Config…: o saldo de cada live da Kick aberta na conta
    /// e, se der, 1 resgate do bônus escolhido para ela em cada uma (as horas acumulam no jogo).
    /// </summary>
    private async Task RedeemKickAsync()
    {
        _kickRedeeming = true;
        _kickRedeemAt = DateTime.Now + PointsRedeemer.KickInterval;
        try
        {
            for (int i = 0; i < Accounts; i++)
            {
                if (_config.RedeemKick[i] is not KickReward reward || _lives[i] is not LivesWindow window || !window.KickChannels.Any())
                    continue;
                var redeemer = _redeemKick[i];
                var token = await ReadKickTokenAsync(i);
                redeemer.NoLogin = token is null;
                if (token is null)
                    continue;
                if (_config.KickLight[i])
                {
                    // Kick leve: lê o saldo sem a página; o player em branco só abre para resgatar.
                    foreach (var channel in window.KickChannels.ToList())
                    {
                        string read = await PointsRedeemer.KickReadAsync(KickReadApi, channel, token, reward);
                        LogKickLight(i, channel, read, true);
                        if (!redeemer.NeedsPage(channel, read, claim: false))
                        {
                            if (redeemer.Apply(channel, read) is string note)
                                _panes[i].Note(note);
                            continue;
                        }
                        foreach (var (live, result) in await window.EvalLivesAsync(LiveSite.Kick, c => redeemer.KickScript(KickApi, c, token, reward), channel))
                            if (redeemer.Apply(live, result) is string note)
                                _panes[i].Note(note);
                    }
                    continue;
                }
                foreach (var (channel, result) in await window.EvalLivesAsync(LiveSite.Kick, c => redeemer.KickScript(KickApi, c, token, reward)))
                {
                    LogKickLight(i, channel, result, false);
                    if (redeemer.Apply(channel, result) is string note)
                        _panes[i].Note(note);
                }
            }
        }
        finally
        {
            _kickRedeeming = false;
        }
        UpdateBar();
    }

    // A API da Kick de fora do navegador (a leitura do saldo na Kick leve). No autoteste, o servidor em localhost responde
    // com o saldo da Kick de mentira do player.
    private string KickReadApi
    {
        get
        {
            if (!_selfTest)
                return "https://kick.com";
            var fake = _fakePubSub ??= new FakePubSub();
            fake.KickApi ??= (channel, what, _) => (string?)Invoke(() => FakeKickApi(channel, what));
            return fake.WebUrl;
        }
    }

    /// <summary>
    /// Enquanto alguma conta está na Kick leve (em teste): o saldo de cada live da Kick em cada conferência, das contas
    /// leves e das com o player, no kick-leve.log, para comparar os pontos por hora. Nas leves, também o que o deck mandou.
    /// </summary>
    private void LogKickLight(int index, string channel, string json, bool light)
    {
        if (!_config.KickLight.Any(l => l))
            return;
        string points = "?";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("points", out var p))
                points = p.GetRawText();
            else if (doc.RootElement.TryGetProperty("error", out var e))
                points = "? (" + e.GetString() + ")";
        }
        catch (JsonException)
        {
            points = "? (resposta estranha)";
        }
        string sent = light && _kickWatchers[index] is KickWatcher watcher
            ? $" · {watcher.Status(channel)} · mandou {watcher.Counts(channel).Events} tempos e {watcher.Counts(channel).Handshakes} handshakes, " +
              $"recebeu {watcher.Counts(channel).Received}"
            : "";
        string line = $"{DateTime.Now:dd/MM/yy HH:mm}  {_config.AccountNames[index]} · {channel} · {(light ? "leve" : "player")} · saldo {points}{sent}";
        try
        {
            var path = Path.Combine(_root, "kick-leve.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                File.WriteAllLines(path, File.ReadAllLines(path).TakeLast(2000));
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // Fica para a próxima.
        }
    }

    // A API da Twitch (no autoteste, a de mentira em twitch.test).
    private string TwitchApi => _selfTest ? "https://twitch.test" : "https://gql.twitch.tv";

    // A mesma API, de fora do navegador (a leitura do saldo na Twitch leve). No autoteste, o servidor em localhost
    // responde com o saldo da Twitch de mentira do player.
    private string TwitchReadApi
    {
        get
        {
            if (!_selfTest)
                return "https://gql.twitch.tv";
            var fake = _fakePubSub ??= new FakePubSub();
            fake.Points ??= (login, auth, device) => (string)Invoke(() => FakeTwitchBalance(login, "leitura", auth, device));
            return fake.WebUrl;
        }
    }

    /// <summary>
    /// A cada 2 min, nas contas com live da Twitch aberta: o saldo de cada uma, o baú de bônus se tiver (com o baú ligado
    /// em Lives → Config…) e, nas contas com o resgate marcado, 1 resgate do bônus escolhido em cada live, se der.
    /// </summary>
    private async Task TwitchPointsAsync()
    {
        _twitchPointsBusy = true;
        _twitchPointsAt = DateTime.Now + PointsRedeemer.TwitchInterval;
        try
        {
            for (int i = 0; i < Accounts; i++)
            {
                var reward = _config.RedeemTwitch[i];
                bool claim = _config.ClaimTwitchBonus;
                // Na Twitch leve a conferência roda mesmo sem baú nem resgate: ela dá o id da conta para o deck assistir.
                if ((reward is null && !claim && !_config.TwitchLight[i]) || _lives[i] is not LivesWindow window || !window.TwitchChannels.Any())
                {
                    StopPubSub(i);
                    StopWatcher(i);
                    continue;
                }
                var redeemer = _redeemTwitch[i];
                var (token, device) = await ReadTwitchLoginAsync(i);
                redeemer.NoLogin = token is null;
                if (token is null)
                {
                    StopPubSub(i);
                    StopWatcher(i);
                    continue;
                }
                if (_config.TwitchLight[i])
                {
                    // Twitch leve: lê o saldo sem a página; o player parado só abre para pegar o baú ou resgatar.
                    foreach (var channel in window.TwitchChannels.ToList())
                    {
                        string read = await PointsRedeemer.TwitchReadAsync(TwitchReadApi, channel, token, device, reward);
                        if (!redeemer.NeedsPage(channel, read, claim))
                        {
                            if (redeemer.Apply(channel, read) is string note)
                                _panes[i].Note(note);
                            continue;
                        }
                        if (FailedRead(read) is string readFailed)
                            LogTwitchProblem(i, channel, $"a leitura do saldo sem a página não passou ({readFailed}); abri o player");
                        bool chestWaiting = read.Contains("\"claimId\":\"");
                        var results = await window.EvalLivesAsync(LiveSite.Twitch,
                            c => redeemer.TwitchScript(TwitchApi, c, token, device, reward, claim), channel);
                        if (results.Count == 0)
                            LogTwitchProblem(i, channel, "o player não abriu" + (chestWaiting ? " (com o baú esperando)" : ""));
                        foreach (var (live, result) in results)
                        {
                            if (chestWaiting && PointsRedeemer.ClaimProblem(result) is string problem)
                                LogTwitchProblem(i, live, $"o baú estava esperando, mas {problem}");
                            if (redeemer.Apply(live, result) is string note)
                                _panes[i].Note(note);
                        }
                    }
                }
                else
                {
                    foreach (var (channel, result) in await window.EvalLivesAsync(LiveSite.Twitch,
                                 c => redeemer.TwitchScript(TwitchApi, c, token, device, reward, claim)))
                        if (redeemer.Apply(channel, result) is string note)
                            _panes[i].Note(note);
                }
                UpdatePubSub(i, token);
                UpdateWatcher(i, token, device);
                foreach (var quiet in redeemer.NewQuiet(window.TwitchChannels))
                    _panes[i].Note($"Twitch: {quiet} está aberta há {redeemer.QuietLimit.TotalMinutes:0} min sem os pontos de assistir");
            }
        }
        finally
        {
            _twitchPointsBusy = false;
        }
        UpdateBar();
    }

    /// <summary>O motivo de uma leitura do saldo sem a página que não passou (null: passou).</summary>
    private static string? FailedRead(string read)
    {
        try
        {
            using var doc = JsonDocument.Parse(read);
            return doc.RootElement.TryGetProperty("failed", out var why) ? why.GetString() : null;
        }
        catch (JsonException)
        {
            return "resposta estranha";
        }
    }

    // A última vez que cada problema da Twitch leve foi ao registro (conta, canal e o começo do texto).
    private readonly Dictionary<string, DateTime> _twitchProblemAt = new();

    /// <summary>
    /// Um problema da Twitch leve no registro dos resgates (o baú que não veio, o player que não abriu), no máximo um
    /// igual a cada 10 min por canal, para dar para ver por que os baús param sem lotar o registro.
    /// </summary>
    private void LogTwitchProblem(int index, string channel, string text)
    {
        string key = $"{index} {channel} {text[..Math.Min(24, text.Length)]}";
        if (_twitchProblemAt.TryGetValue(key, out var last) && DateTime.Now - last < TimeSpan.FromMinutes(10))
            return;
        _twitchProblemAt[key] = DateTime.Now;
        _redeemLog.Add(index, _config.AccountNames[index], LiveSite.Twitch, $"{channel}: {text}");
    }

    // Os avisos da Twitch de cada conta (o websocket), e as raids e os momentos que cada uma já pegou.
    private readonly TwitchPubSub?[] _pubsub = new TwitchPubSub?[Accounts];
    private readonly HashSet<string>[] _twitchTaken = Enumerable.Range(0, Accounts).Select(_ => new HashSet<string>()).ToArray();

    // O websocket de avisos do autoteste (em localhost), aberto na primeira vez que o deck pede.
    private FakePubSub? _fakePubSub;

    private string PubSubUrl => _selfTest ? (_fakePubSub ??= new FakePubSub()).Url : TwitchPubSub.TwitchUrl;

    /// <summary>
    /// Liga (ou mantém) os avisos da Twitch da conta, com o login dela, ouvindo a raid e o momento das lives da Twitch
    /// abertas. O login mudou: conecta de novo com o novo.
    /// </summary>
    private void UpdatePubSub(int index, string token)
    {
        var redeemer = _redeemTwitch[index];
        var open = _lives[index]?.TwitchChannels.ToList() ?? new List<string>();
        if (redeemer.UserId is not string me || open.Count == 0)
        {
            StopPubSub(index);
            return;
        }
        if (_pubsub[index] is TwitchPubSub current && (current.Token != token || current.UserId != me))
            StopPubSub(index);
        var pubsub = _pubsub[index] ??= new TwitchPubSub(PubSubUrl, token, me, notice => _ = OnTwitchNoticeAsync(index, notice));
        pubsub.SetChannels(open.Select(redeemer.IdOf).OfType<string>());
        redeemer.ListeningSince = pubsub.Connected ? pubsub.ConnectedAt : null;
    }

    private void StopPubSub(int index)
    {
        _pubsub[index]?.Dispose();
        _pubsub[index] = null;
        _redeemTwitch[index].ListeningSince = null;
    }

    // Quem assiste às lives da Twitch sem player, em cada conta com a Twitch leve.
    private readonly TwitchWatcher?[] _watchers = new TwitchWatcher?[Accounts];
    private readonly KickWatcher?[] _kickWatchers = new KickWatcher?[Accounts];

    // No autoteste, a Kick de fora do navegador é a de mentira (o mesmo servidor do websocket, em localhost).
    private KickWatcher.Endpoints KickWatcherEndpoints => _selfTest
        ? new KickWatcher.Endpoints((_fakePubSub ??= new FakePubSub()).WebUrl, _fakePubSub.WebUrl, _fakePubSub.Url + "viewer/v1/connect")
        : KickWatcher.Kick;

    /// <summary>
    /// Na Kick leve, liga (ou mantém) quem assiste às lives da Kick abertas em cada conta, sem player, com o login dela;
    /// fora dela, sem login ou sem live aberta, desliga. O login mudou: começa de novo com o novo.
    /// </summary>
    private async Task UpdateKickWatchersAsync()
    {
        for (int i = 0; i < Accounts; i++)
        {
            var open = _lives[i]?.KickChannels.ToList() ?? new List<string>();
            if (!_config.KickLight[i] || open.Count == 0 || await ReadKickTokenAsync(i) is not string token)
            {
                StopKickWatcher(i);
                continue;
            }
            if (_kickWatchers[i] is KickWatcher current && current.Token != token)
                StopKickWatcher(i);
            var watcher = _kickWatchers[i] ??= NewKickWatcher(token);
            watcher.SetChannels(open);
        }
    }

    /// <summary>Quem assiste na Kick leve (o autoteste encurta os tempos).</summary>
    private KickWatcher NewKickWatcher(string token)
    {
        var watcher = new KickWatcher(KickWatcherEndpoints, token);
        if (_selfTest)
        {
            watcher.Tick = TimeSpan.FromMilliseconds(100);
            watcher.HandshakeEvery = watcher.PingEvery = TimeSpan.FromSeconds(1);
            watcher.WatchEvery = TimeSpan.FromSeconds(1);
        }
        return watcher;
    }

    private void StopKickWatcher(int index)
    {
        _kickWatchers[index]?.Dispose();
        _kickWatchers[index] = null;
    }

    // No autoteste, a Twitch de fora do navegador é a de mentira (o mesmo servidor do websocket, em localhost).
    private TwitchWatcher.Endpoints WatcherEndpoints => _selfTest
        ? new TwitchWatcher.Endpoints((_fakePubSub ??= new FakePubSub()).WebUrl, _fakePubSub.WebUrl + "/gql", _fakePubSub.WebUrl + "/usher")
        : TwitchWatcher.Twitch;

    /// <summary>
    /// Na Twitch leve, liga (ou mantém) quem assiste às lives da Twitch abertas na conta, sem player, com o login e o id
    /// dela; fora dela, ou sem o id da conta ainda, desliga. O login mudou: começa de novo com o novo.
    /// </summary>
    private void UpdateWatcher(int index, string token, string? device)
    {
        var open = _lives[index]?.TwitchChannels.ToList() ?? new List<string>();
        if (!_config.TwitchLight[index] || _redeemTwitch[index].UserId is not string me || open.Count == 0)
        {
            StopWatcher(index);
            return;
        }
        if (_watchers[index] is TwitchWatcher current && (current.Token != token || current.UserId != me))
            StopWatcher(index);
        var watcher = _watchers[index] ??= NewWatcher(index, me, token, device);
        watcher.SetChannels(open);
    }

    /// <summary>Quem assiste na Twitch leve (o autoteste encurta os tempos).</summary>
    private TwitchWatcher NewWatcher(int index, string userId, string token, string? device)
    {
        var watcher = new TwitchWatcher(WatcherEndpoints, userId, token, device);
        if (_selfTest)
        {
            watcher.Tick = TimeSpan.FromMilliseconds(300);
            watcher.MinuteEvery = TimeSpan.FromSeconds(1);
            watcher.PlaylistEvery = TimeSpan.FromSeconds(1);
        }
        return watcher;
    }

    private void StopWatcher(int index)
    {
        _watchers[index]?.Dispose();
        _watchers[index] = null;
    }

    /// <summary>
    /// Um aviso da Twitch na conta: o baú apareceu (pega na hora, na live dele), pontos entraram (o rendimento e o
    /// saldo; a sequência de lives e a raid vão para o registro), o canal aberto fez raid (entra nela) ou abriu um
    /// momento (pega).
    /// </summary>
    private async Task OnTwitchNoticeAsync(int index, TwitchPubSub.Notice notice)
    {
        try
        {
            var redeemer = _redeemTwitch[index];
            redeemer.ListeningSince = _pubsub[index] is { Connected: true } pubsub ? pubsub.ConnectedAt : null;
            if (notice.ChannelId is not string id || redeemer.ChannelOf(id) is not string channel
                || _lives[index] is not LivesWindow window || !window.TwitchChannels.Contains(channel))
                return;
            var message = notice.Message;
            switch (notice.Topic, notice.Type)
            {
                case ("community-points-user-v1", "points-earned"):
                {
                    var data = message.GetProperty("data");
                    var gain = data.GetProperty("point_gain");
                    int? balance = data.TryGetProperty("balance", out var b) && b.TryGetProperty("balance", out var bv) && bv.TryGetInt32(out int bi) ? bi : null;
                    if (redeemer.Earned(channel, gain.GetProperty("reason_code").GetString() ?? "", gain.GetProperty("total_points").GetInt32(), balance) is string note)
                        _panes[index].Note(note);
                    break;
                }
                case ("community-points-user-v1", "claim-available") when _config.ClaimTwitchBonus:
                {
                    var (token, device) = await ReadTwitchLoginAsync(index);
                    if (token is null)
                        return;
                    var reward = _config.RedeemTwitch[index];
                    // O id do baú vem no aviso: a consulta de dentro da página nem sempre mostra o baú.
                    string? claimId = message.TryGetProperty("data", out var d) && d.TryGetProperty("claim", out var c)
                                      && c.TryGetProperty("id", out var cid) ? cid.GetString() : null;
                    var results = await window.EvalLivesAsync(LiveSite.Twitch,
                        ch => redeemer.TwitchScript(TwitchApi, ch, token, device, reward, claim: true, spend: false, claimId: claimId), channel);
                    if (results.Count == 0)
                        LogTwitchProblem(index, channel, "o baú apareceu, mas o player não abriu");
                    foreach (var (live, result) in results)
                    {
                        if (PointsRedeemer.ClaimProblem(result) is string problem)
                            LogTwitchProblem(index, live, $"o baú apareceu, mas {problem}");
                        if (redeemer.Apply(live, result) is string note)
                            _panes[index].Note(note);
                    }
                    break;
                }
                case ("raid", "raid_update_v2"):
                {
                    var raid = message.GetProperty("raid");
                    string raidId = raid.GetProperty("id").GetString() ?? "";
                    string target = raid.TryGetProperty("target_login", out var t) ? t.GetString() ?? "?" : "?";
                    // A Twitch repete o aviso enquanto a raid não sai: entra uma vez só.
                    if (!_twitchTaken[index].Add("raid " + raidId))
                        return;
                    var (token, device) = await ReadTwitchLoginAsync(index);
                    if (token is null)
                        return;
                    var result = (await window.EvalLivesAsync(LiveSite.Twitch, _ => PointsRedeemer.TwitchRaidScript(TwitchApi, token, device, raidId), channel))
                        .Select(r => r.Result).FirstOrDefault() ?? "";
                    string? error = TwitchActionError(result, "joined");
                    _redeemLog.Add(index, _config.AccountNames[index], LiveSite.Twitch, error is null
                        ? $"{channel}: entrou na raid (para {target})"
                        : $"{channel}: a raid (para {target}) não passou ({error})");
                    if (error is null)
                        _panes[index].Note($"Twitch: entrei na raid de {channel}");
                    break;
                }
                case ("community-moments-channel-v1", "active"):
                {
                    string momentId = message.GetProperty("data").GetProperty("moment_id").GetString() ?? "";
                    if (!_twitchTaken[index].Add("momento " + momentId))
                        return;
                    var (token, device) = await ReadTwitchLoginAsync(index);
                    if (token is null)
                        return;
                    var result = (await window.EvalLivesAsync(LiveSite.Twitch, _ => PointsRedeemer.TwitchMomentScript(TwitchApi, token, device, momentId), channel))
                        .Select(r => r.Result).FirstOrDefault() ?? "";
                    string? error = TwitchActionError(result, "claimed");
                    _redeemLog.Add(index, _config.AccountNames[index], LiveSite.Twitch, error is null
                        ? $"{channel}: pegou o momento"
                        : $"{channel}: o momento não passou ({error})");
                    break;
                }
            }
            UpdateBar();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ObjectDisposedException)
        {
            // Aviso fora do formato, ou a janela fechou no meio: ignora esse.
        }
    }

    /// <summary>O erro do script de uma ação da Twitch (raid, momento), ou null se deu certo (<paramref name="ok"/> veio true).</summary>
    private static string? TwitchActionError(string json, string ok)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(ok, out var done) && done.ValueKind == JsonValueKind.True)
                return null;
            return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "sem resposta";
        }
        catch (JsonException)
        {
            return "a live não respondeu";
        }
    }

    /// <summary>O login da Kick salvo no perfil da conta: o cookie session_token, que o site manda como "Bearer".</summary>
    private async Task<string?> ReadKickTokenAsync(int index)
    {
        if (_panes[index].Core is not CoreWebView2 core)
            return null;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("https://kick.com");
            var token = cookies.FirstOrDefault(c => c.Name == "session_token")?.Value;
            return string.IsNullOrEmpty(token) ? null : Uri.UnescapeDataString(token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deixa abertas, em cada janela de lives, só as lives da Twitch e da Kick que estão no ar (até o limite do deck).</summary>
    private void ApplyLives()
    {
        for (int i = 0; i < Accounts; i++)
        {
            if (_lives[i] is not LivesWindow window)
                continue;
            // Antes da primeira resposta de cada site não abre nada: a live pode estar offline. Na Twitch, sem o login
            // na conta também não: o player sem login não rende pontos.
            if (_selfTest || _twitchLive.Count > 0 || _twitchLogin[i] == false)
            {
                // Sem as fechadas no botão twitch da conta, e cada uma levou a vaga dela junto.
                var twitch = _config.Twitch[i];
                var open = _config.TwitchChannels.Where(c => !twitch.Closed.Contains(c));
                window.SetLives(LiveSite.Twitch, _twitchLogin[i] == false ? new()
                    : KickLive.Pick(open, _twitchLive, twitch.SlotsOf(_config.TwitchLives)).Select(c => (c, TwitchLiveUrl(i, c))).ToList());
            }
            if (_selfTest || _kickLive.Count > 0)
            {
                // Sem as fechadas no botão lives da conta, e cada uma levou a vaga dela junto.
                var kick = _config.Kick[i];
                var channels = _config.KickChannels.Where(c => !kick.Closed.Contains(c));
                window.SetLives(LiveSite.Kick, KickLive.Pick(channels, _kickLive, kick.SlotsOf(_config.KickLives)).Select(c => (c, KickLiveUrl(i, c))).ToList());
            }
        }
        UpdateBar();
    }

    /// <summary>
    /// Fecha no botão do site (kick ou twitch) a live de <paramref name="channel"/> nesta conta (ou todas, com nulo). A
    /// vaga fica em branco: a conferência de 2 em 2 min não abre outra live nela até abrir de novo.
    /// </summary>
    private void CloseLive(int index, LiveSite site, string? channel)
    {
        var limit = _config.Limits(site)[index];
        foreach (var shut in channel is null ? LivesOpen(index, site) : new List<string> { channel })
            if (!limit.Closed.Contains(shut))
                limit.Closed.Add(shut);
        limit.Slots = channel is null ? 0 : Math.Max(0, limit.SlotsOf(_config.Lives(site)) - 1);
        SaveConfig();
        ApplyLives();
        _panes[index].Note(channel is null ? $"{site}: fechei todas as lives" : $"{site}: fechei a live de {channel}");
    }

    /// <summary>Abre de novo a live de <paramref name="channel"/> fechada no botão do site (ou todas, com nulo), com a vaga dela.</summary>
    private void ReopenLive(int index, LiveSite site, string? channel)
    {
        var limit = _config.Limits(site)[index];
        if (channel is null)
        {
            limit.Closed.Clear();
            limit.Slots = null;
        }
        else if (limit.Closed.Remove(channel))
        {
            limit.Slots = limit.SlotsOf(_config.Lives(site)) + 1;
            limit.Fit(_config.Lives(site));
        }
        SaveConfig();
        ApplyLives();
        _panes[index].Note(channel is null ? $"{site}: as lives voltam a abrir" : $"{site}: {channel} volta a abrir (se estiver no ar)");
    }

    private void CloseLives(int index)
    {
        StopPubSub(index);
        StopWatcher(index);
        StopKickWatcher(index);
        _twitchLogin[index] = null;
        if (_lives[index] is LivesWindow window)
        {
            window.AllowClose = true;
            window.Close();
            _lives[index] = null;
        }
        UpdateBar();
    }

    /// <summary>
    /// O login da Twitch salvo no perfil da conta (o cookie auth-token, que o site manda como "OAuth") e o aparelho (o
    /// cookie unique_id, que o site manda como X-Device-Id). Pode ser chamado de qualquer thread; o navegador embutido
    /// só é usado na thread da interface.
    /// </summary>
    private Task<(string? Token, string? Device)> ReadTwitchLoginAsync(int index)
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
                var device = cookies.FirstOrDefault(c => c.Name == "unique_id")?.Value;
                // O que foi visto, para o menu explicar quando o login não aparece.
                _twitchSeen[index] = $"{cookies.Count} cookies da Twitch{(string.IsNullOrEmpty(token) ? ", sem o auth-token do login" : ", com o auth-token")}";
                result.TrySetResult(string.IsNullOrEmpty(token) ? (null, null) : (token, string.IsNullOrEmpty(device) ? null : device));
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

    /// <summary>O menu Lives, com o resgate em lote sempre à vista (com o aviso: ele gasta os pontos de uma vez).</summary>
    private void FillLivesMenu()
    {
        var items = _livesButton.DropDownItems;
        items.Clear();
        items.Add(new ToolStripMenuItem("Marque a conta vinculada à sua Twitch/Kick (a main):") { Enabled = false });
        for (int i = 0; i < Accounts; i++)
        {
            int index = i;
            string state = _panes[i].Off ? " — conta desligada (as lives voltam ao ligar)"
                : _lives[i] is not null ? " — " + LivesShort(i) : "";
            var item = new ToolStripMenuItem(_config.AccountNames[i] + state) { Checked = _config.LivesAccounts.Contains(i + 1) };
            item.Click += async (_, _) => await ToggleLivesAsync(index);
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        items.Add("Mostrar as lives (para entrar na Twitch/Kick ou resgatar pontos)", null, (_, _) => ShowLives())
            .Enabled = _lives.Any(l => l is not null);
        items.Add("Detalhes… (tocando, Twitch leve, baús, pontos por hora)", null, (_, _) => ShowLivesDetails())
            .Enabled = _lives.Any(l => l is not null);
        int today = _redeemLog.Lines.Count(l => l.StartsWith(DateTime.Now.ToString("dd/MM/yy")));
        items.Add($"Registro dos resgates… ({today} hoje)", null, (_, _) => ShowRedeemLog());
        var bulkItem = new ToolStripMenuItem("Resgate em lote… (CUIDADO)", null, (_, _) => ShowBulkRedeem())
        {
            ToolTipText = "Resgata vários bônus de uma vez com os pontos da Twitch ou da Kick de uma conta. Gasta os pontos na hora " +
                          "e não tem volta: confira a conta, o canal e as quantidades antes de começar",
        };
        items.Add(bulkItem);
        items.Add("Config…", null, async (_, _) => await EditChannelsAsync());
    }

    /// <summary>
    /// O resgate em lote (uma janela só; clicar de novo traz ela para a frente). Vem escolhida a 1ª conta com lives, e
    /// os pedidos rodam num player do site (Twitch ou Kick) escondido no perfil da conta (no autoteste, o de mentira).
    /// </summary>
    private void ShowBulkRedeem()
    {
        if (_env is null)
            return;
        if (_bulkWindow is { IsDisposed: false })
        {
            _bulkWindow.Activate();
            return;
        }
        int account = _config.LivesAccounts.Where(a => a >= 1 && a <= Accounts).Select(a => a - 1).DefaultIfEmpty(0).First();
        var accounts = Enumerable.Range(0, Accounts)
            .Select(i => (_config.AccountNames[i] + (_panes[i].Off ? " (desligada)" : ""), _panes[i].ProfileName)).ToList();
        _bulkWindow = new BulkRedeemDialog(_env, accounts, account,
            site => site == LiveSite.Kick ? KickApi : TwitchApi,
            (site, channel) => site == LiveSite.Kick ? KickUrl(channel) : TwitchUrl(channel), SelfTestSetup,
            (i, site, text) =>
            {
                _redeemLog.Add(i, _config.AccountNames[i], site, text);
                // O lote gasta por fora do resgate automático: a próxima conferência do canal começa do saldo novo.
                int colon = text.IndexOf(':');
                if (colon > 0)
                    (site == LiveSite.Kick ? _redeemKick : _redeemTwitch)[i].Forget(text[..colon]);
            });
        _bulkWindow.Show(this);
    }

    /// <summary>A janela do registro dos resgates (uma só; clicar de novo traz ela para a frente).</summary>
    private void ShowRedeemLog()
    {
        if (_redeemLogWindow is { IsDisposed: false })
        {
            _redeemLogWindow.Activate();
            return;
        }
        _redeemLogWindow = new RedeemLogWindow(_redeemLog, _config.AccountNames);
        _redeemLogWindow.Show(this);
    }

    private void ShowLives()
    {
        int order = 0;
        foreach (var window in _lives.OfType<LivesWindow>())
            window.Reveal(Screen.FromControl(this).WorkingArea, order: order++);
    }

    /// <summary>
    /// Os chats de <paramref name="site"/> da conta, com o botão dos pontos, para resgatar; "Próxima conta" na janela
    /// passa para a seguinte que tem live daquele site aberta.
    /// </summary>
    private void ShowPoints(int index, LiveSite site)
    {
        var with = Enumerable.Range(0, Accounts).Where(i => _lives[i]?.Tabs(site).Any() == true).ToList();
        if (_lives[index] is not LivesWindow window || !with.Contains(index))
            return;
        foreach (int i in with)
            _lives[i]!.HasNext = with.Count > 1;
        window.Reveal(Screen.FromControl(this).WorkingArea, window.Tabs(site).First().Tab);
    }

    private void ShowNextPoints(int index, LiveSite site)
    {
        for (int step = 1; step < Accounts; step++)
        {
            int next = (index + step) % Accounts;
            if (_lives[next]?.Tabs(site).Any() != true)
                continue;
            _lives[index]?.Park();
            ShowPoints(next, site);
            return;
        }
    }

    private async Task EditChannelsAsync()
    {
        using var dialog = new ChannelsDialog(_config.TwitchChannels, _config.KickChannels, _config.TwitchLives, _config.KickLives,
            _config.AccountNames, _config.RedeemKick, _config.RedeemTwitch, _config.ClaimTwitchBonus, _config.BonusGuard);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _config.BonusGuard = dialog.BonusGuard;
        _guard.Restart();
        _config.TwitchChannels = dialog.TwitchChannels;
        _config.KickChannels = dialog.KickChannels;
        _config.TwitchLives = Math.Clamp(dialog.TwitchLives, 0, TwitchLive.MaxSlots);
        foreach (var twitch in _config.Twitch)
            twitch.Fit(_config.TwitchLives);
        SetKickLives(dialog.KickLives);
        // Salvar de novo também volta a resgatar na conta que parou (depois de vincular a Kick ou a Twitch no jogo).
        _config.RedeemKick = dialog.RedeemKick;
        _config.RedeemTwitch = dialog.RedeemTwitch;
        _config.ClaimTwitchBonus = dialog.ClaimTwitchBonus;
        foreach (var redeemer in _redeemKick.Concat(_redeemTwitch))
            redeemer.Restart();
        SaveConfig();
        _kickLive.Clear();
        _twitchLive.Clear();
        // Reabre as lives de cada conta marcada (também a que estava sem nenhuma, se agora tem o que abrir).
        for (int i = 0; i < Accounts; i++)
        {
            if (!_config.LivesAccounts.Contains(i + 1) || _panes[i].Off)
                continue;
            CloseLives(i);
            await OpenLivesAsync(i);
        }
    }

    /// <summary>O limite de lives da Kick por conta; vale quando as lives reabrem (salvar os canais já reabre).</summary>
    private void SetKickLives(int lives)
    {
        _config.KickLives = Math.Clamp(lives, 0, KickLive.MaxSlots);
        foreach (var kick in _config.Kick)
            kick.Fit(_config.KickLives);
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
            "<button onclick='window.__outro = (window.__outro || 0) + 1'>Outro</button></nav>" +
            "<div id='chat-msgs'></div><script>document.getElementById('chat-msgs').addEventListener('click', e => {" +
            " const l = e.target.closest('.m-pk-link'); if (l) window.__ficha = l.dataset.nick + ':' + l.dataset.pk; });</script></body>");
        // O player da Twitch de teste, com o vídeo "tocando" (o tempo anda).
        File.WriteAllText(Path.Combine(site, "twitch.html"),
            "<!doctype html><title>Twitch-teste</title><video muted></video><script>const v = document.querySelector('video');" +
            " Object.defineProperty(v, 'paused', { get: () => false });" +
            " Object.defineProperty(v, 'currentTime', { get: () => performance.now() / 1000 });</script>");
        // O replay das partidas de PvP, como os do jogo (/replay.mjs e /replay-tabuleiro.mjs): a velocidade é por replay.
        File.WriteAllText(Path.Combine(site, "replay.mjs"), """
            export const VELOCIDADES = [1, 2, 4, 8];
            export class ReplayGuerra {
              constructor(canvas, rep, { mini = false } = {}) { this.vel = 1; this.mini = !!mini; this.tocando = false; }
              iniciar() { return Promise.resolve(this); }
              tocar() { this.tocando = true; return this; }
              velocidade(v, { livre = false } = {}) {
                const n = Number(v);
                this.vel = livre && n > 0 ? Math.min(32, Math.max(0.25, n)) : VELOCIDADES.includes(n) ? n : 1;
                return this.vel;
              }
            }
            """);
        File.WriteAllText(Path.Combine(site, "replay-tabuleiro.mjs"), """
            import { ReplayGuerra } from './replay.mjs';
            export class ReplayTabuleiro extends ReplayGuerra {
              iniciar() { return super.iniciar(); }
              tocar() { this.tocando = true; return this; }
            }
            """);
        // As poções e os revives do espelho de itens do jogo (/assets/items.json), que a recompra lê.
        Directory.CreateDirectory(Path.Combine(site, "assets"));
        File.WriteAllText(Path.Combine(site, "assets", "items.json"), """
            { "items": [
              { "id": 200, "name": "Small Potion", "category": "heal", "npcPrice": 50, "healAmount": 60 },
              { "id": 203, "name": "Hyper Potion", "category": "heal", "npcPrice": 800, "healAmount": 1000 },
              { "id": 204, "name": "Ultimate Potion", "category": "heal", "npcPrice": 1500, "healAmount": 3000 },
              { "id": 205, "name": "Revive", "category": "revive", "npcPrice": 600, "revivePct": 0.5 },
              { "id": 206, "name": "Max Revive", "category": "revive", "npcPrice": 2500, "revivePct": 1 },
              { "id": 207, "name": "Medicine", "category": "heal", "npcPrice": 0, "healAmount": 20 },
              { "id": 1, "name": "Pedra", "category": "stone", "npcPrice": 5000 }
            ] }
            """);
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
        string Detail() => string.Join(" | ", _panes.Select(p => $"{p.Index + 1}:{(p.Visible ? p.Bounds.ToString() : "escondida")}"))
                           + $" | janela {WindowState} grade {_grid.ClientRectangle}";

        try
        {
            // A Twitch leve e a Kick leve vêm ligadas de fábrica; os testes dos players são com eles tocando, e cada uma
            // tem o teste dela (que liga na conta 1).
            bool lightByDefault = _config.TwitchLight.All(l => l);
            _config.TwitchLight = new bool[Accounts];
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
                && _config.ReduceMotion,
                $"ao abrir={motionStart} desligado={motionOff} religado={motionOn}");

            // Quadros do mapa: 15 por segundo de fábrica, muda na hora (60 = sem limite), o cancelar continua valendo e o
            // limite volta sozinho na página recarregada. Um laço de requestAnimationFrame conta os quadros em 2 s.
            async Task<int> FramesIn2s(AccountPane pane)
            {
                await Eval(pane, "window.__gen = (window.__gen || 0) + 1; window.__n = 0;" +
                                 " (g => { const f = () => { if (window.__gen !== g) return; window.__n++; requestAnimationFrame(f); }; f(); })(window.__gen); 1");
                await Task.Delay(2000);
                return int.TryParse(await Eval(pane, "window.__gen++; window.__n"), out int n) ? n : -1;
            }
            int framesDefault = await FramesIn2s(_panes[0]);
            await SetGameFpsAsync(60);
            int framesFree = await FramesIn2s(_panes[0]);
            await SetGameFpsAsync(10);
            int framesTen = await FramesIn2s(_panes[0]);
            await Eval(_panes[0], "window.__c = 0; cancelAnimationFrame(requestAnimationFrame(() => window.__c++)); 1");
            await Task.Delay(300);
            string cancelled = await Eval(_panes[0], "window.__c");
            _panes[0].Reload();
            await Task.Delay(500);
            await WaitFor(() => _panes[0].Status == PaneStatus.Ok, 15);
            await Task.Delay(500);
            int framesReloaded = await FramesIn2s(_panes[0]);
            await SetGameFpsAsync(15);
            Check("quadros do mapa: 15/s de fábrica, sem limite e 10/s na hora, o cancelar vale e o limite volta na página recarregada",
                framesDefault is >= 22 and <= 38 && framesFree >= 70 && framesTen is >= 14 and <= 26 && cancelled == "0"
                && framesReloaded is >= 14 and <= 26 && _config.GameFps == 15,
                $"15/s={framesDefault} sem limite={framesFree} 10/s={framesTen} cancelado={cancelled} recarregada a 10/s={framesReloaded}");

            // Sprites: um setInterval de 220 ms (o dos sprites do jogo) vira 660 ms de fábrica; em 220 volta ao do jogo nos
            // novos; subir para 1500 vale na hora no que já existe; um setInterval de outro tempo não muda.
            async Task<int> Ticks(AccountPane pane, int ms, int wait, bool fresh = true)
            {
                if (fresh)
                    await Eval(pane, $"clearInterval(window.__iv); window.__k = 0; window.__iv = setInterval(() => window.__k++, {ms}); 1");
                else
                    await Eval(pane, "window.__k = 0; 1");
                await Task.Delay(wait);
                return int.TryParse(await Eval(pane, "window.__k"), out int n) ? n : -1;
            }
            int spriteDefault = await Ticks(_panes[0], 220, 2000);
            await SetSpriteMsAsync(220);
            int spriteGame = await Ticks(_panes[0], 220, 2000);
            await SetSpriteMsAsync(1500);
            int spriteSlower = await Ticks(_panes[0], 220, 3100, fresh: false);
            int otherTimer = await Ticks(_panes[0], 500, 2000);
            await Eval(_panes[0], "clearInterval(window.__iv); 1");
            await SetSpriteMsAsync(660);
            Check("sprites animados: 660 ms de fábrica, 220 volta ao do jogo, mais devagar vale na hora, e os outros relógios não mudam",
                spriteDefault is >= 2 and <= 4 && spriteGame is >= 7 and <= 10 && spriteSlower is >= 1 and <= 3 && otherTimer is >= 3 and <= 5
                && _config.SpriteMs == 660,
                $"660 ms={spriteDefault} em 2 s, 220 ms={spriteGame} em 2 s, 1500 ms no já criado={spriteSlower} em 3,1 s, outro de 500 ms={otherTimer} em 2 s");

            // ULTRA: o botão liga em todas pelo interruptor das Configurações do jogo e desliga pelo "Voltar ao
            // jogo" da caixa, na hora; fora do jogo (a 4ª, no portal) só guarda a escolha. Ligado, a conta que
            // recarrega volta a ele mesmo se tinha saído. O jogo-teste ganha as peças do jogo.
            const string FakeUltra = """
                (() => {
                  localStorage.removeItem('cfg-ultra');
                  const ultra = on => {
                    localStorage.setItem('cfg-ultra', on ? '1' : '0');
                    document.documentElement.classList.toggle('modo-ultra', on);
                    document.querySelector('#ultra').innerHTML = on ? '<button id=ultra-sair>Voltar ao jogo</button>' : '';
                    if (on) document.querySelector('#ultra-sair').onclick = () => ultra(false);
                  };
                  document.body.insertAdjacentHTML('beforeend', '<div id=app></div><button id=btn-config>Config</button><div id=modal></div><div id=ultra></div>');
                  document.querySelector('#btn-config').onclick = () => {
                    document.querySelector('#modal').innerHTML = '<button id=cfg-ultra>ULTRA</button>';
                    document.querySelector('#cfg-ultra').onclick = () => {
                      ultra(localStorage.getItem('cfg-ultra') !== '1');
                      document.querySelector('#modal').innerHTML = '';
                    };
                  };
                  return 1;
                })()
                """;
            foreach (var pane in _panes)
                await Eval(pane, FakeUltra);
            await Eval(_panes[3], "document.querySelector('#app').classList.add('hidden'); 1");
            async Task<string> Ultra() => string.Join(",", await Task.WhenAll(_panes.Select(p => p.GetUltraAsync())));
            var ultraStart = await Ultra();
            await SetUltraAsync(true);
            var ultraOn = await Ultra();
            string buttonOn = _ultraButton.Text;
            await Eval(_panes[2], "localStorage.setItem('cfg-ultra', '0'); 1");
            _panes[2].Reload();
            await Task.Delay(500);
            await WaitFor(() => _panes[2].Status == PaneStatus.Ok, 15);
            string? reloaded = null;
            for (int i = 0; i < 20 && reloaded != "escolhido"; i++)
            {
                await Task.Delay(250);
                reloaded = await _panes[2].GetUltraAsync();
            }
            await SetUltraAsync(false);
            var ultraOff = await Ultra();
            Check("ULTRA do jogo liga e desliga em todas na hora, e a conta que recarrega volta a ele",
                ultraStart == "normal,normal,normal,fora" && ultraOn == "ligado,ligado,ligado,escolhido" && reloaded == "escolhido"
                && ultraOff == "normal,normal,fora,fora" && buttonOn == "⚡ ULTRA ligado" && _ultraButton.Text == "⚡ ULTRA" && !_config.Ultra,
                $"antes={ultraStart} ligado={ultraOn} 3ª recarregada={reloaded} desligado={ultraOff} botão='{buttonOn}'→'{_ultraButton.Text}'");
            foreach (var pane in _panes)
                await Eval(pane, "document.querySelectorAll('#app, #btn-config, #modal, #ultra').forEach(e => e.remove()); 1");

            // Conta parada na tela de login: o cabeçalho avisa (o captcha de lá vai juntando memória).
            await Eval(_panes[1], "document.body.insertAdjacentHTML('beforeend', '<div id=portal></div><div id=login></div>'); 1");
            await _panes[1].CheckLoginScreenAsync();
            string loginInfo = _panes[1].HeaderInfo;
            await Eval(_panes[1], "document.querySelector('#portal').remove(); document.querySelector('#login').remove(); 1");
            await _panes[1].CheckLoginScreenAsync();
            Check("aviso de conta parada na tela de login",
                loginInfo.Contains("tela de login") && !_panes[1].HeaderInfo.Contains("tela de login") && !_panes[0].HeaderInfo.Contains("tela de login"),
                $"com login='{loginInfo}' sem='{_panes[1].HeaderInfo}'");

            // O jogo que não abre: a página carregou mas ficou na tela de abertura (o jogo-teste não conecta nem mostra o
            // login) além do tempo; a conta recarrega sozinha e anota em aberturas.log. Com o login à vista, o jogo abriu:
            // para de vigiar e zera a contagem.
            var stuckPane = _panes[1];
            var openingLog = Path.Combine(_root, "aberturas.log");
            stuckPane.LoadTimeout = TimeSpan.FromSeconds(1);
            int loadsBefore = stuckPane.Loads;
            stuckPane.Reload();
            await WaitFor(() => stuckPane.Loads > loadsBefore && stuckPane.Status == PaneStatus.Ok, 15);
            await Task.Delay(1200);
            await stuckPane.CheckGameOpenAsync();
            int stuckReloads = stuckPane.StuckReloads;
            bool stuckReloaded = await WaitFor(() => stuckPane.Loads > loadsBefore + 1 && stuckPane.Status == PaneStatus.Ok, 15);
            await Eval(stuckPane, "document.body.insertAdjacentHTML('beforeend', '<div id=login>login</div>'); 1");
            await stuckPane.CheckGameOpenAsync();
            int stuckAfterLogin = stuckPane.StuckReloads;
            await Task.Delay(2500);
            await stuckPane.CheckGameOpenAsync();
            int stuckLoads = stuckPane.Loads - loadsBefore;
            await Eval(stuckPane, "document.querySelector('#login').remove(); 1");
            // Com o aviso "conta aberta em outro lugar" por cima da tela de abertura, não recarrega (tomaria a sessão do
            // outro aparelho): para de vigiar e avisa no cabeçalho.
            int loadsTaken = stuckPane.Loads;
            stuckPane.Reload();
            await WaitFor(() => stuckPane.Loads > loadsTaken && stuckPane.Status == PaneStatus.Ok, 15);
            await Eval(stuckPane, "document.body.insertAdjacentHTML('beforeend', '<div id=sessao-tomada class=modal>Sua conta foi aberta em outro lugar</div>'); 1");
            await Task.Delay(1200);
            await stuckPane.CheckGameOpenAsync();
            string takenNote = stuckPane.NoteText ?? "";
            await Task.Delay(1500);
            await stuckPane.CheckGameOpenAsync();
            bool takenStays = stuckPane.StuckReloads == 0 && stuckPane.Loads == loadsTaken + 1;
            await Eval(stuckPane, "document.querySelector('#sessao-tomada').remove(); 1");
            stuckPane.LoadTimeout = null;
            string openingText = File.Exists(openingLog) ? File.ReadAllText(openingLog) : "";
            Check("jogo que não abre: preso na tela de abertura além do tempo, a conta recarrega sozinha; com o login à vista, para",
                stuckReloads == 1 && stuckReloaded && stuckAfterLogin == 0 && stuckLoads == 2
                && openingText.Contains("o jogo não abriu em 1 s (carga ?%): recarregando (1ª vez)")
                && takenStays && takenNote.Contains("aberta em outro lugar: o deck não recarrega"),
                $"recargas={stuckReloads} recarregou={stuckReloaded} depois do login={stuckAfterLogin} cargas={stuckLoads} " +
                $"outro lugar={takenStays}/'{takenNote}' log='{openingText.Trim()}'");

            // 🩺 Diagnóstico: o log da conta 2 tem o caminho da carga (a tela de abertura, o vigia com o retrato e o print) e os
            // erros da página (JavaScript, console, o arquivo que não existe); o .zip leva tudo, com os nomes das chaves do
            // localStorage mas sem os valores.
            await Eval(stuckPane, "localStorage.setItem('chave-diag', 'segredo-diag'); setTimeout(() => { throw new Error('erro-de-teste-diag'); }); " +
                                  "console.error('console-de-teste-diag'); fetch('https://deck.test/nao-existe-diag.js').catch(() => 0); 1");
            await Task.Delay(1500);
            string diagZip = await Diag.CreateReportAsync(_root, _env, _panes, DeckLines().ToList(), _root, outside: false);
            var zipped = new System.Text.StringBuilder();
            using (var archive = System.IO.Compression.ZipFile.OpenRead(diagZip))
                foreach (var entry in archive.Entries.Where(e => !e.Name.EndsWith(".jpg")))
                {
                    using var entryReader = new StreamReader(entry.Open());
                    zipped.Append($"[{entry.FullName}]\n").Append(entryReader.ReadToEnd());
                }
            File.Delete(diagZip);
            await Eval(stuckPane, "localStorage.removeItem('chave-diag'); 1");
            string diagText = zipped.ToString();
            int diagShots = Directory.GetFiles(Diag.Folder, "conta2-*.jpg").Length;
            var diagMissing = new[]
                {
                    "[relatorio.txt]", "[diagnostico/conta2.log]", "[diagnostico/deck.log]", "[aberturas.log]", "WebView2: em uso", "== conta2:",
                    "tela: carga ?%", "retrato (o vigia vai recarregar)", "vigia: o jogo não abriu em 1 s", "tela: login",
                    "erro-de-teste-diag", "console-de-teste-diag", "nao-existe-diag.js", "chave-diag",
                }
                .Where(t => !diagText.Contains(t)).ToList();
            Check("diagnóstico: o log da conta tem a carga, o vigia com retrato e print e os erros da página; o .zip leva tudo sem os valores guardados",
                diagMissing.Count == 0 && !diagText.Contains("segredo-diag") && diagShots >= 2,
                $"faltou=[{string.Join(" | ", diagMissing)}] valor vazou={diagText.Contains("segredo-diag")} prints={diagShots}");

            // Recompra: o script do deck pega o socket que o jogo abre para o próprio site. No jogo-teste ele não
            // conecta, então o socket finge estar aberto, guarda o que seria mandado e recebe o welcome à mão.
            // Ligada na conta 1 (Ultimate Potion 4.000 abaixo de 2.000, revive 450 abaixo de 100, Ultra Ball 12.000
            // abaixo de 5.000), ela arruma a fila do "Usar Poções", liga o "Usar Revive" nas Automações do jogo e compra
            // um lote de cada, uma compra por vez (o lote de 12.000 em 9.999 + 2.001); com o lote comprado, o estoque
            // gasto um pouco não compra de novo. Sem ouro para o lote, espera; poção zerada compra o que o ouro deixa.
            // A conta 2, desligada, não compra nada. A Golden Potion entra na lista e conta com as poções.
            const string FakeSocket = """
                (() => {
                  const ws = new WebSocket('wss://deck.test/');
                  Object.defineProperty(ws, 'readyState', { get: () => 1 });
                  window.__enviados = [];
                  ws.send = d => window.__enviados.push(JSON.parse(d));
                  window.__msg = o => ws.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(o) }));
                  window.__msg({ t: 'welcome',
                    catalogoBolas: [{ id: 1, nome: 'Poke Ball', priceGold: 20, compravel: true }, { id: 4, nome: 'Ultra Ball', priceGold: 300, compravel: true }],
                    itensNossos: [{ id: 70070, name: 'Golden Potion', category: 'heal', npcPrice: 5000, healPct: 0.5 }],
                    estado: { cheio: true, nick: 'teste', gold: 50000000, balls: { 4: 4000 }, items: { 204: 1000, 205: 50, 70070: 500 },
                      activeId: 7, automation: { autoPotion: true, potionIds: [200, 204], hpLimiar: 0.3 }, pokemons: [{ id: 7, maxHp: 2000 }] } });
                  return 1;
                })()
                """;
            const string SentScript = "JSON.stringify(window.__enviados.map(m => m.t === 'shop.buy' ? `${m.kind}:${m.id}x${m.qty}` : `fila:${m.potionIds}`))";
            // O ExecuteScript devolve o texto em JSON: o JSON.stringify da página chega entre aspas.
            async Task<string> Sent(AccountPane pane) => JsonSerializer.Deserialize<string>(await Eval(pane, SentScript)) ?? "";
            await Eval(_panes[0], FakeSocket);
            await Eval(_panes[1], FakeSocket);
            // De fábrica: pouco (para quem está começando) e a poção de melhor custo-benefício. O deck.json de antes
            // ("manter"): nunca configurada fica com os de agora; configurada compra o que levava ao "manter".
            var oldUntouched = new RestockConfig
            {
                Balls = new() { On = true, Id = 4, Keep = 30000, Below = 5000 }, Revives = new() { On = true, Keep = 500, Below = 100 },
                Potions = new() { On = true, Id = Restock.UltimatePotion, Keep = 5000, Below = 2000 }, GoldFloor = 1_000_000,
            }.Normalized();
            var oldUsed = new RestockConfig
            {
                On = true, Balls = new() { On = true, Id = 4, Keep = 30000, Below = 5000 },
                Potions = new() { On = true, Id = Restock.UltimatePotion, Keep = 5000, Below = 2000 }, GoldFloor = 1_000_000,
            }.Normalized();
            var fresh = new RestockConfig();
            bool potionDefault = fresh.Potions is { Id: 0, Buy: 100, Below: 30 } && fresh.Balls is { Id: 1, Buy: 500, Below: 100 }
                && fresh.Revives is { Buy: 10, Below: 3 } && fresh.GoldFloor == 10_000
                && oldUntouched.Balls is { Id: 1, Buy: 500, Below: 100, Keep: null } && oldUntouched.Potions is { Id: 0, Buy: 100 }
                && oldUntouched.Revives.Buy == 10 && oldUntouched.GoldFloor == 10_000
                && oldUsed.Balls is { Id: 4, Buy: 25000, Below: 5000, Keep: null } && oldUsed.Potions is { Id: Restock.UltimatePotion, Buy: 3000 }
                && oldUsed.GoldFloor == 1_000_000 && !JsonSerializer.Serialize(oldUsed).Contains("Keep");
            var restock0 = _config.Restock[0];
            restock0.Potions = new() { On = true, Id = Restock.UltimatePotion, Buy = 4000, Below = 2000 };
            restock0.Revives = new() { On = true, Buy = 450, Below = 100 };
            restock0.Balls = new() { On = true, Id = 4, Buy = 12000, Below = 5000 };
            restock0.GoldFloor = 1_000_000;
            restock0.On = true;
            await ApplyRestockAsync(0);
            await Task.Delay(2500);
            var firstBuy = await Sent(_panes[0]);
            var goldenListed = await Eval(_panes[0], "window.__deckRecompra.summary().potions.some(p => p.id === 70070 && p.healPct === 0.5)");
            // O jogo confirma (o aviso da compra, o estoque novo e as Automações como ela pediu): 4 s depois vêm os
            // revives, depois as Ultra Balls.
            await Eval(_panes[0], "__msg({ t: 'batalha', ev: [{ k: 'compra', nome: 'Ultimate Potion', qtd: 4000, gasto: 6000000 }] });" +
                                  " __msg({ t: 'estado', estado: { gold: 44000000, items: { 204: 5000, 205: 50, 70070: 500 }," +
                                  " automation: { autoPotion: true, autoRevive: true, potionIds: [204, 200], hpLimiar: 0.3 } } }); 1");
            await Task.Delay(4500);
            await Eval(_panes[0], "__msg({ t: 'estado', estado: { items: { 204: 5000, 205: 500, 70070: 500 } } }); 1");
            await Task.Delay(4500);
            // A 1ª parte das Ultra Balls chega (e o jogo já gastou umas): vem o resto do lote, e só.
            await Eval(_panes[0], "__msg({ t: 'batalha', ev: [{ k: 'compra', nome: 'Ultra Ball', qtd: 9999, gasto: 2999700 }] });" +
                                  " __msg({ t: 'estado', estado: { gold: 40000000, balls: { 4: 13990 } } }); 1");
            await Task.Delay(4500);
            await Eval(_panes[0], "__msg({ t: 'batalha', ev: [{ k: 'compra', nome: 'Ultra Ball', qtd: 2001, gasto: 600300 }] });" +
                                  " __msg({ t: 'estado', estado: { balls: { 4: 15980 } } }); 1");
            await Task.Delay(4500);
            var allBuys = await Sent(_panes[0]);
            // Ouro para 200 mil acima do mínimo: as Ultra Balls abaixo de 5.000 esperam (o lote custa 3,6 mi); as poções
            // zeradas compram o que o ouro deixa (200.000 / 1.500 = 133).
            await Eval(_panes[0], "__msg({ t: 'estado', estado: { gold: 1200000, balls: { 4: 100 } } }); 1");
            await Task.Delay(2000);
            var waitedGold = await Sent(_panes[0]);
            await Eval(_panes[0], "__msg({ t: 'estado', estado: { items: { 204: 0, 205: 500, 70070: 0 } } }); 1");
            await Task.Delay(4500);
            var rescueBuys = await Sent(_panes[0]);
            var restockLog = await Eval(_panes[0], "window.__deckRecompra.summary().log.join('\\n')");
            var notBought = await Sent(_panes[1]);
            await RefreshRestockAsync(0);
            await RefreshRestockAsync(1);
            // As Automações do jogo: sem VIP, "Usar Revive" (e a fila das poções); com VIP, "Lançar sem parar" (no lugar do
            // "até capturar") com a Ultra Ball, que ela compra, em 1º.
            const string AutoSetScript = "JSON.stringify(window.__enviados.filter(m => m.t === 'auto.set').map(m => " +
                                         "[m.potionIds, m.autoPotion, m.autoRevive, m.autoBallSemParar, m.autoBallAteCapturar, m.ballIds].join('/')))";
            async Task<string> AutoSets() => JsonSerializer.Deserialize<string>(await Eval(_panes[0], AutoSetScript)) ?? "";
            string autoNoVip = await AutoSets();
            await Eval(_panes[0], "__msg({ t: 'estado', estado: { loja: { vip: true }, automation: { autoPotion: true, autoRevive: true, " +
                                  "potionIds: [204, 200], hpLimiar: 0.3, autoBallAteCapturar: true, ballIds: [1] } } }); 1");
            string autoVip = "";
            for (int tries = 0; tries < 20 && !(autoVip = await AutoSets()).Contains("4,1"); tries++)
                await Task.Delay(200);
            const string LotBuys = "\"fila:204,200\",\"item:204x4000\",\"item:205x450\",\"ball:4x9999\",\"ball:4x2001\"";
            Check("recompra: arruma a fila das poções e compra um lote de poção, revive e pokebola, uma por vez (desligada não compra)",
                firstBuy == "[\"fila:204,200\",\"item:204x4000\"]" && allBuys == $"[{LotBuys}]"
                && notBought == "[]" && _panes[0].RestockState == "ligada" && _panes[1].RestockState == "desligada",
                $"conta1={allBuys} (1ª={firstBuy}) conta2={notBought} carrinhos={_panes[0].RestockState},{_panes[1].RestockState}");
            Check("recompra: sem ouro para o lote espera (sem comprar aos poucos); poção zerada compra o que o ouro deixa; Golden Potion na lista",
                waitedGold == $"[{LotBuys}]" && rescueBuys == $"[{LotBuys},\"item:204x133\"]" && restockLog.Contains("esperando")
                && goldenListed == "true",
                $"esperando={waitedGold} zerada={rescueBuys} golden={goldenListed} registro={restockLog.Replace("\\n", " | ")}");
            Check("recompra: liga nas Automações do jogo o que compra (revive; com VIP, lançar sem parar com a bola dela em 1º); poção de fábrica = custo-benefício",
                potionDefault && autoNoVip == "[\"204,200/true/true///\"]"
                && autoVip == "[\"204,200/true/true///\",\"204,200/true/true/true/false/4,1\"]",
                $"fábrica={potionDefault} sem VIP={autoNoVip} com VIP={autoVip}");

            // A janela do carrinho: mostra a conta e a poção escolhida; desligar nela vale na hora, e a página que
            // recarrega recebe a configuração sozinha.
            ShowRestockWindow(0);
            var restockWindow = _restockWindows[0]!;
            await WaitFor(() => restockWindow.StateText.Contains("na conta"), 5);
            string windowState = restockWindow.StateText, windowPotion = restockWindow.PotionText;
            var picture = Path.Combine(Path.GetTempPath(), "PokeIdleDeck-recompra.png");
            using (var bitmap = new Bitmap(restockWindow.Width, restockWindow.Height))
            {
                restockWindow.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(picture);
            }
            var typed = restockWindow.TypeBallQty("12.345");
            var typedComma = restockWindow.TypeBallQty("30,000");
            Check("recompra: campo de quantidade aceita 12.345 e 30,000 e escreve como o registro",
                typed == (12345m, "12.345") && typedComma == (30000m, "30.000") && _config.Restock[0].Balls.Buy == 30000,
                $"12.345 → {typed} · 30,000 → {typedComma} · config={_config.Restock[0].Balls.Buy}");
            restockWindow.SetOn(false);
            await Task.Delay(500);
            var pageOff = await Eval(_panes[0], "window.__deckRecompra.summary().on");
            _panes[0].Reload();
            await Task.Delay(500);
            await WaitFor(() => _panes[0].Status == PaneStatus.Ok, 15);
            await Task.Delay(500);
            var afterReload = await Eval(_panes[0], "[window.__deckRecompra.summary().configured, window.__deckRecompra.summary().on]");
            restockWindow.Close();
            Check("recompra: janela do carrinho mostra a conta e desliga na hora; recarregada, a página recebe a configuração",
                windowState.Contains("na conta teste") && windowPotion.StartsWith("Ultimate Potion") && pageOff == "false" && !_config.Restock[0].On
                && afterReload == "[true,false]" && _panes[0].RestockState == "desligada" && _restockWindows[0] is null,
                $"estado='{windowState}' poção='{windowPotion}' página desligada={pageOff} recarregada={afterReload} carrinho={_panes[0].RestockState} (imagem em {picture})");

            await Eval(_panes[0], "localStorage.setItem('quem', 'conta1')");
            var own = await Eval(_panes[0], "localStorage.getItem('quem')");
            var other = await Eval(_panes[1], "localStorage.getItem('quem')");
            Check("cada conta com sessão própria", own == "\"conta1\"" && other == "null", $"conta1={own} conta2={other}");

            // Apagar os dados da conta: "o jogo" leva o login do localStorage e os cookies do site do jogo, e a Twitch/Kick
            // (kick.test) fica; "tudo" leva os cookies dos outros sites também. A outra conta não perde nada.
            var wipePane = _panes[1];
            var cookies = wipePane.Core!.CookieManager;
            async Task<string> WipeState() =>
                await Eval(wipePane, "localStorage.getItem('sessao')") + " deck=" + (await cookies.GetCookiesAsync("https://deck.test")).Count
                + " kick=" + (await cookies.GetCookiesAsync("https://kick.test")).Count;
            async Task<bool> WipeAndWait(bool everything)
            {
                int loads = wipePane.Loads;
                bool done = await wipePane.WipeAsync(everything);
                return done && await WaitFor(() => wipePane.Loads > loads && wipePane.Status == PaneStatus.Ok, 15);
            }
            await Eval(wipePane, "localStorage.setItem('sessao', '{\"nick\":\"teste\",\"token\":\"x\"}'); 1");
            cookies.AddOrUpdateCookie(cookies.CreateCookie("cf_clearance", "1", "deck.test", "/"));
            cookies.AddOrUpdateCookie(cookies.CreateCookie("session_token", "1", "kick.test", "/"));
            string wipeBefore = await WipeState();
            bool wipedGame = await WipeAndWait(false);
            string wipeGame = await WipeState();
            cookies.AddOrUpdateCookie(cookies.CreateCookie("cf_clearance", "1", "deck.test", "/"));
            bool wipedAll = await WipeAndWait(true);
            string wipeAll = await WipeState();
            string wipeOther = await Eval(_panes[0], "localStorage.getItem('quem')");
            Check("apagar os dados da conta: o jogo sai do login e dos cookies do site, a Kick fica; tudo leva a Kick; a outra conta fica",
                wipeBefore == "\"{\\\"nick\\\":\\\"teste\\\",\\\"token\\\":\\\"x\\\"}\" deck=1 kick=1" && wipedGame && wipeGame == "null deck=0 kick=1"
                && wipedAll && wipeAll == "null deck=0 kick=0" && wipeOther == "\"conta1\"" && wipePane.Wipes == 2,
                $"antes={wipeBefore} jogo={wipedGame}/{wipeGame} tudo={wipedAll}/{wipeAll} conta1={wipeOther}");

            // O socket fingido do jogo na conta 2 (o jogo-teste não conecta): o que a página manda vai para __pvpEnviados, e
            // __pvpJogo entrega uma mensagem do jogo, como se viesse do servidor.
            const string GameSocket = """
                (() => {
                  const ws = new WebSocket('wss://deck.test/pvp');
                  Object.defineProperty(ws, 'readyState', { get: () => 1 });
                  window.__pvpEnviados = [];
                  ws.send = d => window.__pvpEnviados.push(JSON.parse(d));
                  window.__pvpJogo = o => ws.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(o) }));
                  window.__pvpJogo({ t: 'welcome', estado: { cheio: true, nick: 'teste', level: 45, xp: 1000000, gold: 500000 } });
                  return 1;
                })()
                """;
            var pvpPane = _panes[1];
            await Eval(pvpPane, GameSocket);
            const string PvpSentScript = "JSON.stringify(window.__pvpEnviados.map(m => m.t + (m.slug ? ':' + m.slug : '')))";
            async Task<string> PvpSent() => JsonSerializer.Deserialize<string>(await Eval(pvpPane, PvpSentScript)) ?? "";
            async Task<bool> PvpSentSoon(string what, int seconds)
            {
                var deadline = DateTime.Now.AddSeconds(seconds);
                while (!(await PvpSent()).Contains(what) && DateTime.Now < deadline)
                    await Task.Delay(150);
                return (await PvpSent()).Contains(what);
            }

            // A fila de PvP (⚔): ligada, pergunta ao jogo se a conta está na fila e entra; depois de uma partida (e da espera
            // que o jogo pede) entra de novo; desligada na fila, sai. Conta VIP: liga a auto-fila do jogo (e desliga junto).
            await Eval(pvpPane, "window.__pvpEnviados.length = 0; localStorage.setItem('pvpTelaBatalha', 'completa'); 1");
            await SetPvpAsync(1, true);
            bool pvpEntered = await PvpSentSoon("pvp.fila.entrar", 5);
            string pvpScreen = await Eval(pvpPane, "localStorage.getItem('pvpTelaBatalha')");
            string pvpFirst = await PvpSent();
            await Eval(pvpPane, "__pvpJogo({ t: 'pvp', fila: { na: true, desde: Date.now(), tamanho: 3 } }); 1");
            await RefreshPvpAsync(1);
            string pvpInQueue = pvpPane.PvpState;
            await Eval(pvpPane, "window.__pvpEnviados.length = 0;" +
                                 " __pvpJogo({ t: 'pvp', partida: { id: 1, venci: true, oponente: { nick: 'Rival', time: [1, 2] }, delta: 12, pontos: 1012 }, fila: { cooldownMs: 0 } }); 1");
            bool pvpAgain = await PvpSentSoon("pvp.fila.entrar", 5);
            await Eval(pvpPane, "__pvpJogo({ t: 'pvp', fila: { na: true, desde: Date.now(), tamanho: 2 } }); window.__pvpEnviados.length = 0; 1");
            var pvpStatus = await pvpPane.ReadPvpAsync();
            await SetPvpAsync(1, false);
            bool pvpLeft = await PvpSentSoon("pvp.fila.sair", 3);
            string pvpOff = pvpPane.PvpState;
            await Eval(pvpPane, "window.__pvpEnviados.length = 0;" +
                                 " __pvpJogo({ t: 'estado', estado: { loja: { vip: true }, automation: { hpLimiar: 0.3, pvpAutoFila: false } } }); 1");
            await SetPvpAsync(1, true);
            await PvpSentSoon("auto.set", 3);
            await SetPvpAsync(1, false);
            await Task.Delay(200);
            string pvpAuto = JsonSerializer.Deserialize<string>(await Eval(pvpPane,
                "JSON.stringify(window.__pvpEnviados.filter(m => m.t === 'auto.set').map(m => m.pvpAutoFila + '/' + m.hpLimiar))")) ?? "";
            Check("pvp: a fila liga, entra de novo depois de cada partida e sai ao desligar; na conta VIP liga a auto-fila do jogo; as partidas não abrem na tela",
                pvpEntered && pvpFirst == "[\"pvp.info\",\"pvp.fila.entrar\"]" && pvpInQueue == "na fila" && pvpAgain
                && pvpStatus is { On: true, Na: true, Wins: 1, Losses: 0, Tamanho: 2, Hidden: true, LastMatch.Rival: "Rival" } && pvpLeft && pvpOff == "desligada" && !_config.Pvp[1]
                && pvpAuto == "[\"true/0.3\",\"false/0.3\"]" && pvpScreen == "\"off\"",
                $"1ª={pvpFirst} estado={pvpInQueue} de novo={pvpAgain} status={(pvpStatus is null ? "nada" : JsonSerializer.Serialize(pvpStatus))} " +
                $"saiu={pvpLeft}/{pvpOff} vip={pvpAuto} tela da batalha={pvpScreen}");

            // A tela e a velocidade das partidas (botão direito no ⚔): mostrada a 4× (o botão 4× do replay acende),
            // minimizada a 16× escolhida no menu, um replay que não é de partida fica como o jogo abriu, e escondida não
            // mexe na velocidade. A Tela da batalha do jogo segue a escolha.
            async Task<bool> PvpUntil(string js, int seconds)
            {
                var deadline = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < deadline)
                {
                    if (await Eval(pvpPane, js) == "true")
                        return true;
                    await Task.Delay(100);
                }
                return false;
            }
            async Task<string> PvpReplay(bool match, bool mini)
            {
                await Eval(pvpPane, (match ? "__pvpJogo({ t: 'pvp', partida: { id: 9, venci: false, oponente: { nick: 'Outro' }, delta: -8 } });" : "") +
                                     $" window.__rp = new window.__RT(null, {{}}, {{ mini: {(mini ? "true" : "false")} }}); __rp.iniciar().then(() => __rp.tocar()); 1");
                await PvpUntil("window.__rp.tocando === true", 3);
                return JsonSerializer.Deserialize<string>(await Eval(pvpPane,
                    "__rp.vel + '/' + (document.querySelector('#gwr-vels .on')?.dataset.vel ?? '-')")) ?? "";
            }
            await Eval(pvpPane, "import('/replay-tabuleiro.mjs').then(m => { window.__RT = m.ReplayTabuleiro; });" +
                                 " document.body.insertAdjacentHTML('beforeend', \"<div id='gwr-vels'><button class='gwr-vel on' data-vel='1'></button>" +
                                 "<button class='gwr-vel' data-vel='4'></button></div>\"); 1");
            await SetPvpViewAsync(1, PvpScreens.Full, 4);
            await SetPvpAsync(1, true);
            bool viewReady = await PvpUntil("!!window.__RT && __deckPvp.status().velPronta", 5);
            bool viewFullScreen = await PvpUntil("localStorage.getItem('pvpTelaBatalha') === 'completa'", 3);
            string viewFull = await PvpReplay(true, false);
            using (var pvpMenu = new ContextMenuStrip())
            {
                FillPvpMenu(1, pvpMenu.Items);
                pvpMenu.Items.OfType<ToolStripMenuItem>().First(m => m.Text!.StartsWith("Minimizada")).PerformClick();
            }
            await PvpUntil("__deckPvp.status().querTela === 'mini'", 3);
            using (var pvpMenu = new ContextMenuStrip())
            {
                FillPvpMenu(1, pvpMenu.Items);
                pvpMenu.Items.OfType<ToolStripMenuItem>().First(m => m.Text == "16×").PerformClick();
            }
            await PvpUntil("__deckPvp.status().querVel === 16", 3);
            bool viewMiniScreen = await PvpUntil("localStorage.getItem('pvpTelaBatalha') === 'mini'", 3);
            string viewMini = await PvpReplay(true, true);
            await Task.Delay(500);
            string viewOther = await PvpReplay(false, true);
            await SetPvpViewAsync(1, PvpScreens.Off, 16);
            bool viewOffScreen = await PvpUntil("localStorage.getItem('pvpTelaBatalha') === 'off'", 3);
            string viewOff = await PvpReplay(true, true);
            var viewStatus = await pvpPane.ReadPvpAsync();
            string viewSaved = $"{_config.PvpScreen[1]}/{_config.PvpSpeed[1]}";
            // Battery Saver (Modo Economia) ligado: o jogo esconde as partidas assim mesmo. Escondida, o menu avisa; mostrar ou
            // minimizar desliga ele pelo botão do jogo (#eco-sair), e voltar para Escondida ou desligar a fila religa (#hunt-eco).
            await Eval(pvpPane, "document.body.insertAdjacentHTML('beforeend', \"<button id='eco-sair'></button><button id='hunt-eco'></button>\");" +
                                 " document.getElementById('eco-sair').onclick = () => localStorage.setItem('cfg-economia', '0');" +
                                 " document.getElementById('hunt-eco').onclick = () => localStorage.setItem('cfg-economia', '1');" +
                                 " localStorage.setItem('cfg-economia', '1'); 1");
            pvpPane.ShowPvp(await pvpPane.ReadPvpAsync());
            string viewEco = pvpPane.PvpNoScene ?? "-";
            bool viewEcoMenu;
            using (var pvpMenu = new ContextMenuStrip())
            {
                FillPvpMenu(1, pvpMenu.Items);
                viewEcoMenu = pvpMenu.Items.OfType<ToolStripMenuItem>().Any(m => m.Text!.StartsWith("⚠ Battery Saver"));
            }
            const string EcoOff = "localStorage.getItem('cfg-economia') === '0' && localStorage.getItem('deckPvpSemEconomia') === '1'";
            const string EcoOn = "localStorage.getItem('cfg-economia') === '1' && localStorage.getItem('deckPvpSemEconomia') === null";
            await SetPvpViewAsync(1, PvpScreens.Mini, 0);
            bool ecoOff = await PvpUntil(EcoOff, 3);
            var ecoStatus = await pvpPane.ReadPvpAsync();
            await SetPvpViewAsync(1, PvpScreens.Off, 0);
            bool ecoBack = await PvpUntil(EcoOn, 3);
            await SetPvpViewAsync(1, PvpScreens.Full, 0);
            bool ecoOffAgain = await PvpUntil(EcoOff, 3);
            await SetPvpAsync(1, false);
            bool ecoBackOff = await PvpUntil(EcoOn, 3);
            string ecoSeen = $"{ecoOff}/{ecoStatus?.EcoByDeck}/{ecoBack}/{ecoOffAgain}/{ecoBackOff}";
            await Eval(pvpPane, "localStorage.removeItem('cfg-economia'); 1");
            await SetPvpViewAsync(1, PvpScreens.Off, 0);
            Check("pvp: a tela das partidas (mostrada, minimizada ou escondida) e a velocidade do botão direito no ⚔ valem na próxima partida da fila",
                viewReady && viewFullScreen && viewFull == "4/4" && viewMiniScreen && viewMini == "16/4" && viewOther == "1/4"
                && viewOffScreen && viewOff == "1/4" && viewSaved == "off/16" && viewEco == "economia" && viewEcoMenu && ecoSeen == "True/True/True/True/True"
                && viewStatus is { Screen: "off", WantScreen: "off", WantSpeed: 16, SpeedReady: true, LastSpeed: 16 },
                $"pronto={viewReady} mostrada={viewFullScreen}/{viewFull} minimizada={viewMiniScreen}/{viewMini} outro replay={viewOther} " +
                $"escondida={viewOffScreen}/{viewOff} salvo={viewSaved} economia={viewEco}/{viewEcoMenu} battery saver={ecoSeen} status={(viewStatus is null ? "nada" : JsonSerializer.Serialize(viewStatus))}");

            // ⟳ recarregar limpo: o Cache Storage do site some, o login (localStorage) fica, e a página carrega de novo.
            var cleanPane = _panes[2];
            await Eval(cleanPane, "localStorage.setItem('sessao-teste', 'fica'); caches.open('deck-teste').then(c => c.put('/x', new Response('a'))); 1");
            await Task.Delay(500);
            string cachesBefore = await Eval(cleanPane, "window.__cachesAntes = null; caches.keys().then(k => window.__cachesAntes = k.join(',')); 1");
            await Task.Delay(300);
            cachesBefore = await Eval(cleanPane, "window.__cachesAntes");
            int cleanLoadsBefore = cleanPane.Loads;
            await cleanPane.ReloadCleanAsync();
            bool reloadedClean = await WaitFor(() => cleanPane.Loads > cleanLoadsBefore, 10);
            await Task.Delay(500);
            await Eval(cleanPane, "window.__cachesDepois = null; caches.keys().then(k => window.__cachesDepois = k.join(',')); 1");
            await Task.Delay(300);
            string cachesAfter = await Eval(cleanPane, "window.__cachesDepois");
            string sessionAfter = await Eval(cleanPane, "localStorage.getItem('sessao-teste')");
            await Eval(cleanPane, "localStorage.removeItem('sessao-teste'); 1");
            Check("⟳ recarregar limpo: apaga o Cache Storage do site e recarrega, e o login (localStorage) fica",
                cachesBefore.Contains("deck-teste") && reloadedClean && cachesAfter == "\"\"" && sessionAfter == "\"fica\"" && cleanPane.CleanReloads == 1,
                $"antes={cachesBefore} recarregou={reloadedClean} depois={cachesAfter} login={sessionAfter} limpos={cleanPane.CleanReloads}");

            SetLayout(DeckLayout.Grid);
            await Task.Delay(500);
            Check("grade 2x2", Shape(2, 2), Detail());

            SetLayout(DeckLayout.Columns);
            await Task.Delay(500);
            Check("colunas", Shape(4, 1), Detail());
            SetLayout(DeckLayout.Grid);

            await Eval(_panes[0], "window.__t = 0; setInterval(() => window.__t++, 100); 1");
            double zoomBefore = _config.Zoom[0];
            bool AtZoom(double zoom) => _panes.All(p => Math.Abs(p.WebView.ZoomFactor - zoom) < 0.01);
            SetZoomAll(0.5);
            FocusPane(1);
            bool zoomFocused = AtZoom(1) && _zoomButton.Text == "Zoom 100%";
            await Task.Delay(3000);
            bool focused = _panes[1].Visible && _panes[1].Bounds == Cell(_grid.ClientRectangle, 0, 0, 1, 1)
                && _panes.Where(p => p.Index != 1).All(p => !p.Visible);
            Check("ampliar conta 2", focused, Detail());
            int.TryParse(await Eval(_panes[0], "window.__t"), out int ticks);
            Check("conta escondida continua no ritmo normal", ticks >= 20, $"{ticks} ticks em 3 s (normal ~30, desacelerada ~3)");
            ToggleFocus(1);
            await Task.Delay(500);
            Check("voltar para a grade", _config.Layout == DeckLayout.Grid && Shape(2, 2), Detail());
            // Ampliar põe o zoom em 100%; voltar pelo mesmo botão ou pela grade devolve o de antes (50%).
            bool zoomBack = AtZoom(0.5) && _zoomButton.Text == "Zoom 50%";
            FocusPane(2);
            bool zoomAgain = AtZoom(1);
            SetLayout(DeckLayout.Grid);
            bool zoomGrid = AtZoom(0.5) && _config.ZoomOutsideFocus is 0.5;
            Check("ampliar põe o zoom em 100%, e voltar (pelo botão ou pela grade) devolve o de antes",
                zoomFocused && zoomBack && zoomAgain && zoomGrid,
                $"ampliou={zoomFocused} voltou={zoomBack} ampliou de novo={zoomAgain} pela grade={zoomGrid} ({_zoomButton.Text})");
            SetZoomAll(zoomBefore);

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

            SetEcho(false);
            await RealClick(_panes[0], "nav button");
            await Task.Delay(1000);
            var alone = await Clicks();
            Check("eco desligado: o clique fica só na conta", alone == "2,1,0,1", $"cliques por conta={alone}");

            // O zoom não depende do eco (desligado aqui). Zoom posto pelo programa não gera o aviso do WebView2
            // (só o Ctrl + roda da pessoa): avisa à mão.
            _panes[1].WebView.ZoomFactor = 0.8;
            OnPaneSettingsChanged(_panes[1]);
            bool zoomSynced = await WaitFor(() => _panes.All(p => Math.Abs(p.WebView.ZoomFactor - 0.8) < 0.01), 5);
            Check("zoom de uma conta vale para todas, com o eco desligado", zoomSynced && !_echo && _config.Zoom.All(z => Math.Abs(z - 0.8) < 0.01),
                string.Join(", ", _panes.Select(p => $"{p.WebView.ZoomFactor:0.00}")));

            // Os botões de zoom da barra: + e − andam um nível em todas as contas, e o botão do meio mostra o zoom.
            bool AllAt(double zoom) => _panes.All(p => Math.Abs(p.WebView.ZoomFactor - zoom) < 0.01);
            SetZoomAll(1);
            StepZoom(1);
            bool zoomedIn = AllAt(1.1) && _zoomButton.Text == "Zoom 110%";
            StepZoom(-1);
            StepZoom(-1);
            bool zoomedOut = AllAt(0.9) && _zoomButton.Text == "Zoom 90%";
            SetZoomAll(1.5);
            bool top = !_zoomInButton.Enabled && _zoomOutButton.Enabled;
            Check("botões de zoom: + e − mudam todas as contas", zoomedIn && zoomedOut && top,
                $"+ {zoomedIn} · −− {zoomedOut} · no máximo o + apaga {top} · {_zoomButton.Text}");
            SetZoomAll(1);

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
            // O player parado da Twitch leve (de fábrica) dorme sem navegador: só as páginas com navegador respondem.
            int withBrowser = lives0 is null ? -1 : lives0.Browsers(LiveSite.Kick) + lives0.Browsers(LiveSite.Twitch);
            var visibility = lives0 is null ? new List<string>() : await lives0.EvalAsync("document.visibilityState");
            Check("todas as páginas das lives visíveis para o navegador (vídeo de trás toca)",
                lives0?.Browsers(LiveSite.Kick) == 2 && visibility.Count == withBrowser && visibility.All(v => v == "\"visible\""),
                $"{string.Join(", ", visibility)} (com navegador: {withBrowser}, Twitch {string.Join(",", lives0?.Sources(LiveSite.Twitch) ?? Array.Empty<string>())})");

            // Fora da tela só tocam os players, sem página do chat. Nos pontos abre por cima de cada player a
            // página do canal, só com o rodapé do chat (sem as mensagens); fechar fecha essas páginas.
            const string ChatShown = "(() => { const c = document.querySelector('#channel-chatroom'); return c ? getComputedStyle(c).display + ' ' " +
                "+ getComputedStyle(document.querySelector('#chatroom-messages')).visibility : 'sem chat'; })()";
            int chatsParked = lives0?.ChatsOpen ?? -1;
            // 💰 Pontos da Kick: só as colunas de chat lado a lado (a página do canal fica na largura de
            // computador, encostada à direita), com a altura dos pontos (a tela baixa limita), no meio da tela.
            ShowPoints(0, LiveSite.Kick);
            bool chatsUp = await WaitFor(() => lives0 is not null && lives0.ChatsLoaded == 2, 15);
            await Task.Delay(300);
            string chatShown = lives0 is null ? "" : string.Join(",", await lives0.EvalChatsAsync(ChatShown));
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
            int chatsAfter = lives0?.ChatsOpen ?? -1;
            Check("chat da Kick só abre com os pontos na tela (só o rodapé, sem as mensagens), e fecha ao sair",
                chatsParked == 0 && chatsUp && chatShown == "\"block hidden\",\"block hidden\"" && chatsAfter == 0
                && lives0 is { Parked: true, Visible: true },
                $"fora={chatsParked} páginas de chat; mostrando={chatShown}; depois de fechar={chatsAfter}");
            Check("pontos da Kick: os 2 chats lado a lado, com a altura dos pontos", fits && sideBySide, layout);

            // 💰 Pontos da Twitch: o chat em janela de cada live da Twitch da conta (com o botão dos pontos), só com o
            // 💰 aberto, numa coluna do tamanho dela; fechar fecha essa página.
            bool twitchPointsButton = _twitchButton.Available;
            ShowPoints(0, LiveSite.Twitch);
            bool twitchChatUp = await WaitFor(() => lives0 is not null && lives0.ChatsLoaded == 1, 15);
            await Task.Delay(300);
            var twitchChats = lives0 is null ? new List<string>() : await lives0.EvalChatsAsync("location.search");
            var twitchColumn = lives0?.ColumnsOf(LiveSite.Twitch).ToList() ?? new();
            // "Página inteira" na Twitch: o chat vira o site inteiro do canal (com o menu da conta); desligar volta ao
            // chat em janela.
            var twitchFull = new List<string>();
            if (lives0 is not null)
            {
                lives0.FullPage = true;
                await WaitFor(() => lives0.ChatsLoaded == 1, 15);
                twitchFull.AddRange(await lives0.EvalChatsAsync("location.search"));
                lives0.FullPage = false;
                await WaitFor(() => lives0.ChatsLoaded == 1, 15);
                twitchFull.AddRange(await lives0.EvalChatsAsync("location.search"));
            }
            Check("Twitch: \"Página inteira\" abre o site do canal no lugar do chat em janela, e desligar volta",
                twitchFull.SequenceEqual(new[] { "\"?canal=tftoddy&inteira=1\"", "\"?canal=tftoddy&chat=1\"" }), string.Join(",", twitchFull));
            lives0?.Close();
            await Task.Delay(300);
            Check("💰 Pontos da Twitch: abre o chat em janela de cada live da Twitch da conta, e fecha ao sair",
                twitchPointsButton && twitchChatUp && twitchChats.SequenceEqual(new[] { "\"?canal=tftoddy&chat=1\"" })
                && twitchColumn.Count == 1 && twitchColumn[0].View.Size == twitchColumn[0].Frame.Size && lives0?.ChatsOpen == 0,
                $"botão={twitchPointsButton} chats={string.Join(",", twitchChats)} coluna={string.Join(" ", twitchColumn)} depois={lives0?.ChatsOpen}");

            // Propaganda cortada nas lives: o pedido a um site de anúncio volta 403 do próprio deck.
            if (lives0 is not null)
                await lives0.EvalAsync("window.__ads = 'pendente'; fetch('https://pagead2.googlesyndication.com/deck-teste')" +
                                       ".then(r => window.__ads = r.status, () => window.__ads = 'erro'); 1");
            await Task.Delay(1500);
            var ads = lives0 is null ? new List<string>() : await lives0.EvalAsync("window.__ads");
            Check("lives sem propaganda (anúncio bloqueado; vídeo, Kick e Cloudflare passam)",
                ads.Count >= 2 && ads.All(a => a == "403")
                && AdBlock.Blocked("https://pagead2.googlesyndication.com/x") && AdBlock.Blocked("https://js.stripe.com/v3")
                && !AdBlock.Blocked("https://kick.com/biscoitao") && !AdBlock.Blocked("https://abc.playback.live-video.net/api/video/v1/x.m3u8")
                && !AdBlock.Blocked("https://challenges.cloudflare.com/x") && !AdBlock.Blocked("https://notgooglesyndication.com/"),
                string.Join(",", ads));

            // Sem DRM nas lives: o player que testa o vídeo protegido ouve "não dá", sem o Edge abrir o processo do DRM
            // (~68 MB por conta). A consulta de capacidade com sistema de chave também diz que não.
            const string DrmProbe = "navigator.requestMediaKeySystemAccess('com.widevine.alpha', [{ initDataTypes: ['cenc'], " +
                "videoCapabilities: [{ contentType: 'video/mp4; codecs=\"avc1.42E01E\"' }] }]).then(() => 'aceitou', e => e.name + ': ' + e.message)";
            const string CapsProbe = "navigator.mediaCapabilities.decodingInfo({ type: 'media-source', video: { contentType: " +
                "'video/mp4; codecs=\"avc1.42E01E\"', width: 640, height: 360, bitrate: 1000000, framerate: 30 }, " +
                "keySystemConfiguration: { keySystem: 'com.widevine.alpha' } }).then(r => 'supported=' + r.supported, e => e.name)";
            var drm = new List<(string Channel, string Result)>();
            var drmCaps = new List<(string Channel, string Result)>();
            if (lives0 is not null)
            {
                drm.AddRange(await lives0.EvalLivesAsync(LiveSite.Kick, _ => DrmProbe));
                drm.AddRange(await lives0.EvalLivesAsync(LiveSite.Twitch, _ => DrmProbe));
                drmCaps.AddRange(await lives0.EvalLivesAsync(LiveSite.Kick, _ => CapsProbe));
            }
            Check("lives sem DRM (o player ouve que não dá, e o Edge não abre o processo do DRM)",
                drm.Count == 3 && drm.All(d => d.Result == "NotSupportedError: sem DRM nas lives")
                && drmCaps.Count == 2 && drmCaps.All(d => d.Result == "supported=false"),
                string.Join(", ", drm.Concat(drmCaps).Select(d => $"{d.Channel}={d.Result}")));

            // Fora da tela, a janela das lives fica pequena (a placa de vídeo desenha menos), com as lives empilhadas do
            // tamanho dela (sem as colunas dos pontos, que deixariam a última estreita) e continuando a tocar.
            var parkedFrames = lives0?.ColumnsOf(LiveSite.Kick).Concat(lives0.ColumnsOf(LiveSite.Twitch)).ToList() ?? new();
            if (lives0 is not null)
            {
                await lives0.WatchAsync();
                await Task.Delay(1500);
                await lives0.WatchAsync();
            }
            var parkedPlay = lives0?.KickPlayback.ToDictionary(k => k.Channel) ?? new();
            bool smallParked = lives0 is { Parked: true } && lives0.Size == lives0.ParkedSize
                && parkedFrames.Count == 3 && parkedFrames.All(f => f.Frame == parkedFrames[0].Frame && f.View.Size == f.Frame.Size)
                && parkedFrames[0].Frame.Width >= lives0.LogicalToDeviceUnits(300) && parkedFrames[0].Frame.Height >= lives0.LogicalToDeviceUnits(100)
                && parkedPlay.TryGetValue("biscoitao", out var parkedBisc) && parkedBisc.Playing == true;
            Check("lives fora da tela: janela pequena, lives empilhadas do tamanho dela e tocando (na tela, as colunas voltam)",
                smallParked && sideBySide && fits,
                $"janela={lives0?.Size} esperado={lives0?.ParkedSize} páginas={string.Join(" ", parkedFrames.Select(f => $"{f.Frame}/{f.View}"))} " +
                $"biscoitao={(parkedPlay.TryGetValue("biscoitao", out var biscParked) ? biscParked.Playing?.ToString() ?? "?" : "fechada")}");

            Check("Kick: abre só as primeiras da lista que estão no ar, até o limite (2 de fábrica, ou o escolhido)",
                KickLive.Pick(new[] { "biscoitao", "axt", "clovao", "outro" },
                    new Dictionary<string, bool?> { ["biscoitao"] = false, ["axt"] = true, ["clovao"] = true, ["outro"] = true }, 2)
                    .SequenceEqual(new[] { "axt", "clovao" })
                && KickLive.Pick(new[] { "biscoitao", "axt", "clovao" },
                    new Dictionary<string, bool?> { ["biscoitao"] = true, ["axt"] = false, ["clovao"] = true }, 2)
                    .SequenceEqual(new[] { "biscoitao", "clovao" })
                && KickLive.Pick(new[] { "biscoitao", "axt", "clovao" }, new Dictionary<string, bool?>(), 2)
                    .SequenceEqual(new[] { "biscoitao", "axt" })
                && KickLive.Pick(new[] { "biscoitao", "axt", "clovao", "outro" },
                    new Dictionary<string, bool?> { ["axt"] = false }, 3)
                    .SequenceEqual(new[] { "biscoitao", "clovao", "outro" })
                && new DeckConfig().KickLives == 2);
            Check("Kick: resposta da API (no ar / offline)",
                KickLive.ParseLive("{\"livestream\":null}") == false && KickLive.ParseLive("{\"livestream\":{\"is_live\":true}}") == true
                && KickLive.ParseLive("{}") is null && KickLive.ParseLive("<html>") is null);
            var twitchParsed = TwitchLive.ParseLive(new[] { "cogu", "rodil", "nao" },
                "{\"data\":{\"users\":[{\"login\":\"cogu\",\"stream\":{\"id\":\"1\"}},{\"login\":\"rodil\",\"stream\":null},null]}}");
            Check("Twitch: resposta da API (no ar / offline / não existe) e o endereço do player",
                twitchParsed["cogu"] == true && twitchParsed["rodil"] == false && twitchParsed["nao"] == false
                && TwitchLive.ParseLive(new[] { "x" }, "<html>")["x"] is null
                && TwitchLive.PlayerUrl("cogu") == "https://player.twitch.tv/?channel=cogu&parent=twitch.tv&player=popout"
                && new DeckConfig().TwitchLives == 2,
                string.Join(", ", twitchParsed.Select(t => $"{t.Key}={t.Value?.ToString() ?? "?"}")));

            // Twitch (1 vaga no autoteste): abre o player da 1ª da lista no ar, troca a que sai do ar pela próxima, e sem o
            // login da Twitch na conta não abre nenhuma (o player sem login não rende pontos).
            string TwitchNow() => string.Join(",", lives0?.TwitchChannels ?? Enumerable.Empty<string>());
            string twitchFirst = TwitchNow();
            _twitchLive["tftoddy"] = false;
            _twitchLive["clovaobengala"] = true;
            ApplyLives();
            bool twitchSwapped = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count && TwitchNow() == "clovaobengala", 10);
            _twitchLogin[0] = false;
            ApplyLives();
            bool twitchClosed = await WaitFor(() => TwitchNow() == "", 5);
            string noTwitchLogin = LivesSummary(0);
            _twitchLogin[0] = true;
            _twitchLive.Clear();
            ApplyLives();
            bool twitchBack = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count && TwitchNow() == "tftoddy", 10);
            if (lives0 is not null)
                await lives0.WatchAsync();
            string twitchSummary = LivesSummary(0);
            Check("Twitch: abre o player da 1ª no ar, troca a que sai do ar, e sem login na Twitch não abre",
                twitchFirst == "tftoddy" && twitchSwapped && twitchClosed && noTwitchLogin.Contains("Twitch: entre na Twitch")
                && twitchBack && twitchSummary.Contains("Twitch no ar: tftoddy ▶"),
                $"antes={twitchFirst} trocou={twitchSwapped} sem login={twitchClosed} ({noTwitchLogin}) voltou={twitchBack} ({twitchSummary})");

            // A 2ª da lista sai do ar: a vaga dela vai para a próxima no ar, e a 1ª fica sem recarregar.
            if (lives0 is not null)
                await lives0.EvalAsync("window.__marca = 'mesma'; 1");
            _kickLive["biscoitao"] = true;
            _kickLive["yetz"] = false;
            _kickLive["surskity"] = true;
            ApplyLives();
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
            var menuBefore = LiveMenuItems(0, LiveSite.Kick).Select(i => i.Text).ToList();
            CloseLive(0, LiveSite.Kick, "biscoitao");
            bool chosen = await WaitFor(() => lives0 is not null && lives0.Count == 2 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.SequenceEqual(new[] { "surskity" }), 10);
            var stayed = lives0 is null ? new List<string>() : await lives0.EvalAsync("window.__marca || 'recarregou'");
            var menuAfter = LiveMenuItems(0, LiveSite.Kick).Select(i => i.Text).ToList();
            // Todas no ar: a vaga do biscoitao não abre outra (fica 1 live, a mais de cima da lista sem as fechadas).
            _kickLive["yetz"] = true;
            ApplyLives();
            bool noRefill = await WaitFor(() => lives0 is not null && lives0.Count == 2 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.SequenceEqual(new[] { "yetz" }), 10);
            Check("kick: escolhe qual live fechar (até a 1ª da lista) e a vaga não abre outra",
                kickBefore == "ligado,apagado,apagado,apagado"
                && menuBefore.Take(3).SequenceEqual(new[] { "Fechar a live de biscoitao", "Fechar a live de surskity", "Fechar todas (nenhuma live da Kick nesta conta)" })
                && chosen && stayed.Count == 2 && stayed[1] == "\"mesma\"" && noRefill && _panes[0].KickState == "fechada"
                && menuAfter.Contains("Fechar a live de surskity") && menuAfter.Contains("Abrir de novo a live de biscoitao"),
                $"botões antes={kickBefore} menu antes={string.Join("|", menuBefore)} marca={string.Join(",", stayed)} sem repor={noRefill} " +
                $"Kick={KickNow()} botão={_panes[0].KickState} menu depois={string.Join("|", menuAfter)}");
            CloseLive(0, LiveSite.Kick, null);
            ApplyLives();
            bool none = await WaitFor(() => lives0 is not null && lives0.Count == 1 && !lives0.KickChannels.Any(), 10);
            int kickBrowsersNone = lives0?.Browsers(LiveSite.Kick) ?? -1;
            Check("kick: fechar todas", none && _config.Kick[0].Slots == 0 && _panes[0].KickState == "fechada", $"Kick={KickNow()}");
            ReopenLive(0, LiveSite.Kick, null);
            bool reopened = await WaitFor(() => lives0 is not null && lives0.Count == 3 && lives0.Loaded == lives0.Count
                && lives0.KickChannels.Order().SequenceEqual(new[] { "biscoitao", "yetz" }), 15);
            int kickBrowsersBack = lives0?.Browsers(LiveSite.Kick) ?? -1;
            Check("kick: abrir todas de novo volta às 2 primeiras da lista no ar",
                reopened && _panes[0].KickState == "ligado" && !_config.Kick[0].Limited,
                $"Kick={KickNow()} botão={_panes[0].KickState}");

            // Botão twitch na conta 1: o mesmo do kick, para a live da Twitch (1 vaga no autoteste). Fechada, a vaga não
            // abre outra da lista; abrir de novo volta.
            string twitchBefore = _panes[0].TwitchState;
            var twitchMenuBefore = LiveMenuItems(0, LiveSite.Twitch).Select(i => i.Text).ToList();
            CloseLive(0, LiveSite.Twitch, "tftoddy");
            bool twitchShut = await WaitFor(() => lives0 is not null && !lives0.TwitchChannels.Any(), 10);
            int twitchBrowsersShut = lives0?.Browsers(LiveSite.Twitch) ?? -1;
            UpdateBar();
            string twitchShutState = _panes[0].TwitchState;
            var twitchMenuAfter = LiveMenuItems(0, LiveSite.Twitch).Select(i => i.Text).ToList();
            string twitchShutSummary = LivesSummary(0);
            ReopenLive(0, LiveSite.Twitch, null);
            bool twitchReopened = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count
                && lives0.TwitchChannels.SequenceEqual(new[] { "tftoddy" }), 15);
            UpdateBar();
            Check("twitch: o botão da conta fecha a live da Twitch (a vaga não abre outra) e abre de novo",
                twitchBefore == "ligado" && twitchMenuBefore.Take(2).SequenceEqual(new[] { "Fechar a live de tftoddy", "Fechar todas (nenhuma live da Twitch nesta conta)" })
                && twitchShut && twitchShutState == "fechada" && twitchMenuAfter.Contains("Abrir de novo a live de tftoddy")
                && twitchShutSummary.Contains("Twitch: todas fechadas no botão lives") && twitchReopened && _panes[0].TwitchState == "ligado"
                && !_config.Twitch[0].Limited,
                $"antes={twitchBefore} menu={string.Join("|", twitchMenuBefore)} fechou={twitchShut} botão={twitchShutState} " +
                $"menu depois={string.Join("|", twitchMenuAfter)} ({twitchShutSummary}) voltou={twitchReopened}");

            // Vaga sem live não tem navegador (em about:blank cada uma ainda gastava um processo de ~20–25 MB): a live
            // que entra cria o dela, e a que sai fecha.
            int twitchBrowsersBack = lives0?.Browsers(LiveSite.Twitch) ?? -1;
            Check("vagas vazias sem navegador: a live que entra cria o dela, e a que sai fecha",
                kickBrowsersNone == 0 && kickBrowsersBack == 2 && twitchBrowsersShut == 0 && twitchBrowsersBack == 1,
                $"Kick fechadas={kickBrowsersNone} reabertas={kickBrowsersBack}; Twitch fechada={twitchBrowsersShut} reaberta={twitchBrowsersBack}");

            // A dica do botão lives tem limite: linhas de até 60 letras (sem isso ela cresce pela tela toda).
            string livesTip = _panes[0].LivesTip;
            var tipLines = livesTip.Split('\n');
            string wrapped = Theme.Wrap(string.Join(" ", Enumerable.Repeat("palavra", 200)));
            Check("botão lives: a dica quebra em linhas de até 60 letras e corta em 12",
                livesTip.Contains("tftoddy") && tipLines.Length > 2 && tipLines.All(l => l.Length <= 60)
                && wrapped.Split('\n').Length == 12 && wrapped.EndsWith("\n…") && wrapped.Split('\n').All(l => l.Length <= 60),
                livesTip.Replace("\n", " ⏎ "));

            // As lives da Kick da conta dividem um processo (--process-per-site), e o jogo fica em outro.
            var liveProcesses = _env is null ? new List<CoreWebView2ProcessExtendedInfo>()
                : (await _env.GetProcessExtendedInfosAsync())
                    .Where(p => p.AssociatedFrameInfos.Any(f => f.Source.Contains("/kick.html?canal="))).ToList();
            var livePages = liveProcesses.SelectMany(p => p.AssociatedFrameInfos).Select(f => f.Source).ToList();
            Check("lives da Kick da conta num processo só, separado do jogo",
                liveProcesses.Count == 1 && livePages.Count(s => s.Contains("/kick.html?canal=") && !s.Contains("chat=1")) == 2
                && !livePages.Any(s => s.Contains("game.html")),
                $"processos com live={liveProcesses.Count} páginas={string.Join(", ", livePages)}");

            // A régua do consumo: cada processo do navegador numa parte (jogo, Kick, Twitch, placa de vídeo, resto),
            // com a CPU do intervalo; a soma bate com a memória de todos os processos, e vai uma linha ao consumo.log.
            await WaitFor(() => !_memoryBusy, 5);
            await UpdateMemoryAsync();
            await Task.Delay(1500);
            await WaitFor(() => !_memoryBusy, 5);
            await UpdateMemoryAsync();
            await WaitFor(() => Usage.Sum(p => p.Cores) > 0, 25);
            long counted = 0;
            if (_env is not null)
                foreach (var browserProcess in await _env.GetProcessExtendedInfosAsync())
                    try
                    {
                        using var process = Process.GetProcessById(browserProcess.ProcessInfo.ProcessId);
                        counted += process.PrivateMemorySize64;
                    }
                    catch (ArgumentException)
                    {
                    }
            using (var me = Process.GetCurrentProcess())
                counted += me.PrivateMemorySize64;
            var usage = Usage.ToDictionary(p => p.Name);
            long usageTotal = Usage.Sum(p => p.Bytes);
            string usageLog = File.Exists(UsageLog) ? File.ReadAllText(UsageLog) : "";
            Check("consumo por parte: jogo, Kick, Twitch e placa de vídeo separados, com CPU, e a soma bate com o total",
                usage.Count == 6 && usage["jogo"].Processes >= 1 && usage["Kick"].Processes >= 1 && usage["Twitch"].Processes >= 1
                && usage["placa de vídeo"].Processes >= 1 && usage["deck"].Processes == 1
                && Usage.Sum(p => p.Cores) > 0 && Math.Abs(usageTotal - counted) < counted * 0.1
                && _ram.Text.Contains(" Kick ") && _ram.Text.Contains(" Twitch ") && _ram.ToolTipText.Contains("% da CPU")
                && usageLog.Contains(" | Kick ") && usageLog.Contains(" | Twitch "),
                string.Join(", ", Usage.Select(p => $"{p.Name} {p.Bytes / 1024 / 1024} MB {p.Cores:0.00} ({p.Processes})")) +
                $" | contado {counted / 1024 / 1024} MB | barra: {_ram.Text} | log: {usageLog.Trim()}");

            // Onde o jogo gasta: a cada linha do consumo.log vai uma por conta com a CPU da página do jogo separada em
            // script, layout, estilo e o resto, desde a leitura anterior (a primeira só marca o começo).
            await WaitFor(() => !_gameBusy, 10);
            await LogGameAsync();
            await Task.Delay(1500);
            await WaitFor(() => !_gameBusy, 10);
            await LogGameAsync();
            var gameLines = (File.Exists(UsageLog) ? File.ReadAllLines(UsageLog) : Array.Empty<string>())
                .Where(l => l.Contains("  jogo de ")).ToList();
            var activeNames = Enumerable.Range(0, Accounts).Where(i => _panes[i]?.Core is not null).Select(i => _config.AccountNames[i]).ToList();
            Check("consumo.log: onde o jogo de cada conta gasta (script, layout, estilo e o resto), para decidir o limite de quadros",
                activeNames.Count > 0 && activeNames.All(n => gameLines.Any(l => l.Contains($"  jogo de {n}: linha principal ")))
                && gameLines.All(l => l.Contains(" (script ") && l.Contains(", layout ") && l.Contains(", estilo ") && l.Contains(" | nós ")
                                      && l.Contains(" | canvas ")),
                $"contas={string.Join(",", activeNames)} | {string.Join(" || ", gameLines.TakeLast(Math.Max(1, activeNames.Count)))}");

            // Conferência das lives tocando: a do biscoitao anda (▶); a do yetz fica parada (a 1ª conferência
            // só anota onde o vídeo está) e, depois de duas paradas seguidas, é recarregada (⟳1).
            if (lives0 is not null)
                for (int i = 0; i < 3; i++)
                    await lives0.WatchAsync();
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
                    await lives0.WatchAsync();
            bool loadedAgain = await WaitFor(() => lives0 is not null && lives0.Loaded == lives0.Count, 15);
            if (lives0 is not null)
                await lives0.WatchAsync();
            // As lives também ficam sem animações, e continuam assim na página que abriu de novo.
            var livesMotion = lives0 is null ? new List<string>() : await lives0.EvalAsync(ReducedMotion);
            Check("lives da Kick: página que morreu abre de novo, e a que não carregou é recarregada",
                killed > 0 && reopenedNow && loadedAgain && Biscoitao() is (true, 2) && livesMotion.Count == 3 && livesMotion.All(m => m == "true"),
                $"processos fechados={killed} reabriu na hora={reopenedNow} carregou depois={loadedAgain} sem animações={string.Join(",", livesMotion)} {LivesSummary(0)}");

            // Resgate dos pontos (Lives → Config…, cada conta com o seu): a conta 1 resgata Captura (500), com o login
            // dela: o cookie session_token vai como Bearer. O biscoitao tem 1210: 1 resgate por conferência, até não
            // dar mais; o yetz, com 120, não resgata.
            var kickCookies = _panes[0].Core!.CookieManager;
            kickCookies.AddOrUpdateCookie(kickCookies.CreateCookie("session_token", "123%7Cabc", "kick.com", "/"));
            using (var redeemDialog = new ChannelsDialog(_config.TwitchChannels, _config.KickChannels, _config.TwitchLives, 2,
                _config.AccountNames, new KickReward?[] { KickReward.Capture, null, KickReward.Lure, null },
                new KickReward?[] { KickReward.Capture, KickReward.Xp, null, null }, true))
            {
                _config.RedeemKick = redeemDialog.RedeemKick;
                _config.RedeemTwitch = redeemDialog.RedeemTwitch;
                _config.ClaimTwitchBonus = redeemDialog.ClaimTwitchBonus;
                // Como a janela fica, para conferir de olho (aparece um instante).
                redeemDialog.Show(this);
                await Task.Delay(500);
                using var dialogPicture = new Bitmap(redeemDialog.Width, redeemDialog.Height);
                redeemDialog.DrawToBitmap(dialogPicture, new Rectangle(Point.Empty, dialogPicture.Size));
                dialogPicture.Save(Path.Combine(Path.GetTempPath(), "PokeIdleDeck-canais.png"));
            }
            _fakePoints["biscoitao"] = 1210;
            _fakePoints["yetz"] = 120;
            await RedeemKickAsync();
            var firstRedeem = _fakeRedeems.ToList();
            await RedeemKickAsync();
            await RedeemKickAsync();
            string redeemSummary = LivesSummary(0);
            bool ulid = firstRedeem.Count == 1 && System.Text.RegularExpressions.Regex.IsMatch(firstRedeem[0],
                "^biscoitao cap Bearer 123\\|abc \\{\"transaction_id\":\"[0-9A-HJKMNP-TV-Z]{26}\",\"message\":\"\"\\}$");
            Check("pontos da Kick: resgata o bônus escolhido na conta, com o login dela, 1 por canal a cada conferência, só com saldo",
                _config.RedeemKick.SequenceEqual(new KickReward?[] { KickReward.Capture, null, KickReward.Lure, null })
                && ulid && _fakeRedeems.Count == 2 && _fakePoints["biscoitao"] == 210 && _fakePoints["yetz"] == 120
                && redeemSummary.Contains("(210/500 pontos, resgatou") && redeemSummary.Contains("(120/500 pontos)"),
                $"pedidos={string.Join(" | ", _fakeRedeems)} saldo={_fakePoints["biscoitao"]}/{_fakePoints["yetz"]} {redeemSummary}");

            // Sem a Kick vinculada no jogo o PokeIdle devolve os pontos: o deck não resgata de novo e para nesta conta.
            // Salvar Lives → Config… volta a resgatar; sem o login da Kick no perfil, avisa no menu.
            _fakePoints["biscoitao"] += 500;
            await RedeemKickAsync();
            await RedeemKickAsync();
            string stoppedSummary = LivesSummary(0);
            bool stopped = _redeemKick[0].Stopped is not null && _fakeRedeems.Count == 2;
            for (int i = 0; i < 20; i++)
            {
                var login = (await kickCookies.GetCookiesAsync("https://kick.com")).Where(c => c.Name == "session_token").ToList();
                if (login.Count == 0)
                    break;
                login.ForEach(kickCookies.DeleteCookie);
                await Task.Delay(250);
            }
            _redeemKick[0].Restart();
            await RedeemKickAsync();
            string noLoginSummary = LivesSummary(0);
            Check("pontos da Kick: os pontos voltaram (sem vínculo no jogo) para na conta; sem login avisa",
                stopped && stoppedSummary.Contains("resgate parado: os pontos voltaram") && _fakeRedeems.Count == 2
                && _redeemKick[0].NoLogin && noLoginSummary.Contains("resgate: entre na Kick"),
                $"parou={stopped} pedidos={_fakeRedeems.Count} | {stoppedSummary} | {noLoginSummary}");

            // Conta desmarcada não resgata nem confere o saldo, mesmo logada e com pontos.
            kickCookies.AddOrUpdateCookie(kickCookies.CreateCookie("session_token", "123%7Cabc", "kick.com", "/"));
            _config.RedeemKick[0] = null;
            _redeemKick[0].Restart();
            await RedeemKickAsync();
            string offSummary = LivesSummary(0);
            Check("pontos da Kick: conta desmarcada não resgata nem confere o saldo",
                _fakeRedeems.Count == 2 && _redeemKick[0].Text("biscoitao") == "" && !offSummary.Contains("pontos"), offSummary);
            _config.RedeemKick = new KickReward?[Accounts];

            // Pontos da Twitch: a conta 1 resgata Captura (500) com o login dela (o cookie auth-token vai como OAuth e o
            // unique_id como X-Device-Id). O tftoddy tem 460 e um baú esperando: o baú é desafiado pela checagem anti-robô,
            // o deck busca o token dela e manda de novo (510), e aí resgata a Captura (10). Na conferência seguinte, sem
            // baú e sem saldo, só lê o saldo.
            var twitchCookies = _panes[0].Core!.CookieManager;
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("auth-token", "tok123", ".twitch.tv", "/"));
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("unique_id", "dev123", ".twitch.tv", "/"));
            _fakeTwitchPoints["tftoddy"] = 460;
            _fakeTwitchClaims.Add("tftoddy");
            await TwitchPointsAsync();
            var twitchFirstRun = _fakeTwitch.ToList();
            await TwitchPointsAsync();
            string twitchPointsSummary = LivesSummary(0);
            bool twitchFlow = twitchFirstRun.Count == 5
                && twitchFirstRun[0] == "saldo tftoddy OAuth tok123 dev123" && twitchFirstRun[1] == "baú sem a checagem"
                && twitchFirstRun[2] == "checagem OAuth tok123 dev123" && twitchFirstRun[3] == "baú bau-tftoddy OAuth tok123"
                && System.Text.RegularExpressions.Regex.IsMatch(twitchFirstRun[4], "^resgate tftoddy cap 500 OAuth tok123 [0-9a-f]{32}$");
            Check("pontos da Twitch: pega o baú (passando pela checagem anti-robô) e resgata o bônus da conta, com o login dela",
                twitchFlow && _fakeTwitch.Count == 6 && _fakeTwitch[5].StartsWith("saldo tftoddy") && _fakeTwitchPoints["tftoddy"] == 10
                && twitchPointsSummary.Contains("tftoddy ▶ (10/500 pontos, resgatou") && twitchPointsSummary.Contains(", baú "),
                $"pedidos={string.Join(" | ", _fakeTwitch)} saldo={_fakeTwitchPoints["tftoddy"]} {twitchPointsSummary}");

            // Sem bônus marcado na conta, só o baú: confere o saldo e pega o baú, sem resgatar; sem o login, avisa.
            _config.RedeemTwitch[0] = null;
            _redeemTwitch[0].Restart();
            _fakeTwitchClaims.Add("tftoddy");
            _fakeTwitch.Clear();
            await TwitchPointsAsync();
            string claimOnly = LivesSummary(0);
            bool claimOnlyOk = _fakeTwitchPoints["tftoddy"] == 60 && !_fakeTwitch.Any(r => r.StartsWith("resgate")) && claimOnly.Contains("(60 pontos, baú ");
            for (int i = 0; i < 20; i++)
            {
                var login = (await twitchCookies.GetCookiesAsync("https://www.twitch.tv")).Where(c => c.Name == "auth-token").ToList();
                if (login.Count == 0)
                    break;
                login.ForEach(twitchCookies.DeleteCookie);
                await Task.Delay(250);
            }
            await TwitchPointsAsync();
            string twitchNoLogin = LivesSummary(0);
            Check("pontos da Twitch: só o baú, sem resgatar; sem o login da Twitch, avisa",
                claimOnlyOk && _redeemTwitch[0].NoLogin && twitchNoLogin.Contains("pontos da Twitch: entre na Twitch"),
                $"pedidos={string.Join(" | ", _fakeTwitch)} | {claimOnly} | {twitchNoLogin}");
            _config.RedeemTwitch = new KickReward?[Accounts];
            _redeemTwitch[0].Restart();

            // O bônus de 1 h pausado no canal: vai o mais barato liberado (o de 3 h, o mesmo preço por hora), e o registro
            // diz por quê. Todas as durações pausadas: não resgata e avisa uma vez (no registro, e "pausado" no menu). O
            // saldo que cai sem resgate do deck (pontos gastos em outro lugar com a conta) vai para o registro.
            string kickBlocked = $"  {_config.AccountNames[0]} · Kick · biscoitao: 400 pontos, mas o bônus está pausado no canal (todas as durações); resgato quando liberar";
            _config.RedeemKick[0] = KickReward.Xp;
            _redeemKick[0].Restart();
            _fakeRedeems.Clear();
            _fakePausedRewards.Add("xp");
            _fakePoints["biscoitao"] = 950;
            _fakePoints["yetz"] = 0;
            await RedeemKickAsync();
            var fallbackRedeems = _fakeRedeems.ToList();
            bool fallbackLogged = _redeemLog.LinesOf(0).Any(l => l.EndsWith(
                "Kick · biscoitao: resgatou Bônus +15% XP PokeIdle (3h) (900 pontos; sobraram 50; o de 300 pontos está pausado no canal)"));
            _fakePausedRewards.Add("xp3");
            _fakePoints["biscoitao"] = 400;
            await RedeemKickAsync();
            await RedeemKickAsync();
            string pausedSummary = LivesSummary(0);
            int pausedLogs = _redeemLog.LinesOf(0).Count(l => l.EndsWith(kickBlocked));
            _fakePoints["biscoitao"] = 100;
            await RedeemKickAsync();
            bool droppedLogged = _redeemLog.LinesOf(0).Any(l => l.EndsWith(
                "Kick · biscoitao: o saldo caiu de 400 para 100 sem resgate do deck (300 pontos gastos em outro lugar com esta conta?)"));
            _fakePausedRewards.Clear();
            _config.RedeemKick = new KickReward?[Accounts];
            _redeemKick[0].Restart();
            Check("pontos da Kick: com o de 1 h pausado vai o de 3 h; tudo pausado avisa uma vez; saldo que cai sem o deck vai ao registro",
                fallbackRedeems.Count == 1 && fallbackRedeems[0].StartsWith("biscoitao xp3 ") && fallbackLogged && _fakeRedeems.Count == 1
                && pausedLogs == 1 && pausedSummary.Contains("400/300 pontos") && pausedSummary.Contains("bônus pausado no canal") && droppedLogged,
                $"pedidos={string.Join(" | ", _fakeRedeems)} registrou={fallbackLogged} avisos={pausedLogs} caiu={droppedLogged} {pausedSummary}");

            // Guarda do bônus: o XP da Kick da conta 1 vence em 10 min e as lives abertas (biscoitao, yetz) estão sem pontos.
            // O surskity (da lista, fora das lives, offline) tem 1000 e o faturetosl (oficial do jogo) 50: resgata 1 XP no
            // surskity, numa página escondida, com o login da conta, e o jogo mostra o tempo a mais. Sem canal com os pontos,
            // diz uma vez no registro.
            const string GuardSocket = """
                (() => {
                  const ws = new WebSocket('wss://deck.test/guarda');
                  Object.defineProperty(ws, 'readyState', { get: () => 1 });
                  ws.send = () => {};
                  window.__guarda = o => ws.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(o) }));
                  window.__guarda({ t: 'welcome', hunts: [], estado: { cheio: true, nick: 'teste', level: 10, xp: 0, gold: 0 } });
                  return 1;
                })()
                """;
            string GuardState(int minutes) =>
                "__guarda({ t: 'estado', estado: { servidorAgora: Date.now(), kick: { vinculado: true, bonus: { xp: Date.now() + " + minutes +
                " * 60000 }, oficiais: [{ login: 'faturetosl', aoVivo: false, ativo: true }] } } }); 1";
            string account0 = _config.AccountNames[0];
            _config.RedeemKick[0] = KickReward.Xp;
            _redeemKick[0].Restart();
            _guard.Restart();
            _fakeRedeems.Clear();
            _fakePoints["biscoitao"] = 0;
            _fakePoints["yetz"] = 0;
            _fakePoints["surskity"] = 1000;
            _fakePoints["faturetosl"] = 50;
            await Eval(_panes[0], GuardSocket);
            await Eval(_panes[0], GuardState(10));
            await RefreshBonusAsync(0);
            var guardRedeems = _fakeRedeems.ToList();
            bool guardLogged = _redeemLog.LinesOf(0).Any(l => l.EndsWith(
                $"  {account0} · Kick · surskity: guarda do bônus: o de XP vence em 10 min; resgato aqui (1000 pontos, canal offline)"));
            bool guardRedeemLogged = _redeemLog.LinesOf(0).Any(l => l.EndsWith("Kick · surskity: resgatou Bônus +15% XP PokeIdle (300 pontos; sobraram 700)"));
            var guardSlot = _guard.SlotOf(0, LiveSite.Kick);
            bool waiting = guardSlot.RedeemedAt is not null;
            // O jogo soma 1 h: a guarda vê o tempo a mais e não resgata de novo.
            await Eval(_panes[0], GuardState(70));
            guardSlot.HoldUntil = DateTime.MinValue;
            await RefreshBonusAsync(0);
            bool confirmed = guardSlot.RedeemedAt is null && _fakeRedeems.Count == 1;
            string guardTip = Bonus.Parse(await _panes[0].ReadBonusAsync()) is BonusSummary guardSummary ? BonusLine(0, guardSummary) ?? "" : "";
            // De novo perto de vencer, e ninguém com 300: avisa uma vez.
            _fakePoints["surskity"] = 100;
            await Eval(_panes[0], GuardState(5));
            await RefreshBonusAsync(0);
            guardSlot.HoldUntil = DateTime.MinValue;
            await RefreshBonusAsync(0);
            int noneLogs = _redeemLog.LinesOf(0).Count(l => l.Contains("Kick · guarda do bônus: o de XP vence em ") && l.Contains("nenhum canal fora das lives abertas tem os pontos"));
            _config.RedeemKick = new KickReward?[Accounts];
            _redeemKick[0].Restart();
            _guard.Restart();
            Check("guarda do bônus: com o bônus vencendo, resgata 1 num canal fora das lives (offline primeiro) e confere que o tempo subiu",
                guardRedeems.Count == 1 && guardRedeems[0].StartsWith("surskity xp Bearer 123|abc ") && guardLogged && guardRedeemLogged && waiting
                && confirmed && guardTip.Contains("Kick XP: 1 h ") && guardTip.EndsWith("(guarda do bônus ligada)") && _fakeRedeems.Count == 1 && noneLogs == 1,
                $"pedidos={string.Join(" | ", guardRedeems)} registrou={guardLogged}/{guardRedeemLogged} esperando={waiting} subiu={confirmed} " +
                $"dica='{guardTip}' sem pontos={noneLogs}");

            // Na Twitch também: com a Captura de 1 h pausada, vai a de 3 h (1500).
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("auth-token", "tok123", ".twitch.tv", "/"));
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("unique_id", "dev123", ".twitch.tv", "/"));
            _config.RedeemTwitch[0] = KickReward.Capture;
            _redeemTwitch[0].Restart();
            _fakePausedRewards.Add("cap");
            _fakeTwitchClaims.Remove("tftoddy");
            _fakeTwitchPoints["tftoddy"] = 1600;
            _fakeTwitch.Clear();
            await TwitchPointsAsync();
            bool twitchFallback = _fakeTwitch.Any(r => r.StartsWith("resgate tftoddy cap3 1500 ")) && _fakeTwitchPoints["tftoddy"] == 100
                && _redeemLog.LinesOf(0).Any(l => l.EndsWith("Twitch · tftoddy: resgatou Bônus de +15% Capture Boost (3h) (1500 pontos; sobraram 100; o de 500 pontos está pausado no canal)"));
            _fakePausedRewards.Clear();
            _config.RedeemTwitch = new KickReward?[Accounts];
            _redeemTwitch[0].Restart();
            Check("pontos da Twitch: com o de 1 h pausado vai o de 3 h", twitchFallback,
                $"pedidos={string.Join(" | ", _fakeTwitch)} saldo={_fakeTwitchPoints["tftoddy"]}");

            // Registro dos resgates: cada resgate, baú e pontos que voltaram, com a conta, o site e o canal, no
            // resgates.log; a janela mostra o mais novo em cima.
            var logLines = _redeemLog.Lines.ToList();
            string account1 = _config.AccountNames[0];
            bool Logged(string text) => logLines.Any(l => l.Contains($"  {account1} · {text}"));
            ShowRedeemLog();
            await Task.Delay(300);
            string shownLog = _redeemLogWindow?.ShownText ?? "";
            _redeemLogWindow?.Close();
            Check("registro dos resgates: grava resgates, baús e pontos que voltaram no resgates.log, e mostra o mais novo em cima",
                Logged("Kick · biscoitao: resgatou Bônus de +15% Capture Boost (500 pontos; sobraram 710)")
                && Logged("Kick · biscoitao: resgatou Bônus de +15% Capture Boost (500 pontos; sobraram 210)")
                && Logged("Kick · biscoitao: os pontos do último resgate voltaram")
                && Logged("Twitch · tftoddy: pegou o baú (+50, saldo 510)")
                && Logged("Twitch · tftoddy: resgatou Bônus de +15% Capture Boost (500 pontos; sobraram 10)")
                && Logged("Twitch · tftoddy: pegou o baú (+50, saldo 60)")
                && Enumerable.Range(0, Accounts).All(i => (File.Exists(_redeemLog.FileOf(i)) ? File.ReadAllLines(_redeemLog.FileOf(i))
                    : Array.Empty<string>()).SequenceEqual(_redeemLog.LinesOf(i)))
                && logLines.Where(l => l.Contains($"  {account1} · ")).All(_redeemLog.LinesOf(0).Contains)
                && logLines.Count > 0 && shownLog.StartsWith(logLines[^1]),
                string.Join(" | ", logLines));

            // Um arquivo por conta: o resgates.log antigo (todas as contas) é separado pelo nome da conta e vai para a
            // Lixeira; ao abrir, cada conta fica só com os últimos 7 dias; a janela mostra uma conta ou todas; Limpar apaga
            // só a conta escolhida.
            var splitFolder = Path.Combine(_root, "resgates-teste");
            Directory.CreateDirectory(splitFolder);
            foreach (var leftover in Directory.GetFiles(splitFolder))
                File.Delete(leftover);
            string LogStamp(DateTime at) => at.ToString("dd/MM/yy HH:mm");
            File.WriteAllLines(Path.Combine(splitFolder, "resgates.log"), new[]
            {
                $"{LogStamp(DateTime.Now.AddDays(-10))}  Ana · Kick · velho: some",
                $"{LogStamp(DateTime.Now.AddMinutes(-5))}  Ana · Kick · canal: resgatou",
                $"{LogStamp(DateTime.Now.AddMinutes(-4))}  Bia · Twitch · canal: pegou o baú",
                $"{LogStamp(DateTime.Now.AddMinutes(-3))}  Ana · Twitch · canal: pegou o baú",
            });
            var split = new RedeemLog(splitFolder, new[] { "Ana", "Bia", "Cid", "Dani" });
            bool splitOk = !File.Exists(Path.Combine(splitFolder, "resgates.log"))
                && split.LinesOf(0).Count == 2 && split.LinesOf(0).All(l => l.Contains("  Ana · ")) && File.ReadAllLines(split.FileOf(0)).Length == 2
                && split.LinesOf(1).Count == 1 && split.LinesOf(1)[0].Contains("  Bia · ") && !File.Exists(split.FileOf(2))
                && split.Lines.Count == 3 && split.Lines[^1].EndsWith("Ana · Twitch · canal: pegou o baú");
            split.Add(1, "Bia", LiveSite.Kick, "canal: resgatou");
            var splitWindow = new RedeemLogWindow(split, new[] { "Ana", "Bia", "Cid", "Dani" });
            splitWindow.ShowAccount(1);
            string biaShown = splitWindow.ShownText;
            splitWindow.Dispose();
            split.Clear(0);
            bool cleared = split.LinesOf(0).Count == 0 && !File.Exists(split.FileOf(0)) && split.LinesOf(1).Count == 2
                && File.ReadAllLines(split.FileOf(1)).Length == 2 && split.Lines.Count == 2;
            Check("registro dos resgates por conta: separa o antigo, guarda 7 dias, mostra uma conta e limpa só ela",
                splitOk && biaShown.Split(Environment.NewLine).Length == 2 && biaShown.StartsWith(LogStamp(DateTime.Now)[..8])
                && biaShown.Contains("Bia · Kick · canal: resgatou") && !biaShown.Contains("Ana") && cleared,
                $"separou={splitOk} conta1={string.Join(" | ", split.LinesOf(0))} conta2={string.Join(" | ", split.LinesOf(1))} " +
                $"janela da Bia={biaShown.Replace(Environment.NewLine, " / ")} limpou={cleared}");

            // Resgate em lote: o item fica sempre no menu Lives, com o (CUIDADO). Com a conta 1 (o login dela de novo) no
            // biscoitao, 2 XP + 1 Captura com 1500 pontos são 3 resgates (XP, Captura, XP), cada um com a descrição do
            // bônus (senão a Twitch de mentira recusa) e um transactionID novo; saem 1100 e o saldo do fim é lido de novo.
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("auth-token", "tok123", ".twitch.tv", "/"));
            twitchCookies.AddOrUpdateCookie(twitchCookies.CreateCookie("unique_id", "dev123", ".twitch.tv", "/"));
            _fakeTwitchPoints["biscoitao"] = 1500;
            bool HasBulk() => _livesButton.DropDownItems.Cast<ToolStripItem>().Any(x => x.Text?.StartsWith("Resgate em lote") == true);
            FillLivesMenu();
            bool bulkShown = HasBulk() && _livesButton.DropDownItems.Cast<ToolStripItem>().Any(x => x.Text == "Resgate em lote… (CUIDADO)");
            _livesButton.DropDownItems.Cast<ToolStripItem>().FirstOrDefault(x => x.Text?.StartsWith("Resgate em lote") == true)?.PerformClick();
            FillLivesMenu();
            var bulk = _bulkWindow;
            string bulkBefore = "", bulkEnd = "";
            bool bulkRead = false, bulkReady = false;
            var bulkRedeems = new List<string>();
            if (bulk is not null)
            {
                bulk.PauseMs = (100, 200);
                bulk.Account = 0;
                bulk.Channel = "biscoitao";
                bulk.SetCounts(2, 1, 0);
                bulkRead = await bulk.CheckAsync();
                bulkBefore = bulk.StatusText;
                bulkReady = bulk.CanRedeem;
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                bulkRedeems = _fakeTwitch.Where(r => r.StartsWith("resgate")).ToList();
                bulkEnd = string.Join(" / ", bulk.ProgressLines);            }
            bool bulkOrder = bulkRedeems.Count == 3
                && System.Text.RegularExpressions.Regex.IsMatch(bulkRedeems[0], "^resgate biscoitao xp 300 OAuth tok123 [0-9a-f]{32}$")
                && System.Text.RegularExpressions.Regex.IsMatch(bulkRedeems[1], "^resgate biscoitao cap 500 OAuth tok123 [0-9a-f]{32}$")
                && System.Text.RegularExpressions.Regex.IsMatch(bulkRedeems[2], "^resgate biscoitao xp 300 OAuth tok123 [0-9a-f]{32}$")
                && bulkRedeems.Select(r => r.Split(' ')[^1]).Distinct().Count() == 3;
            Check("resgate em lote: sempre no menu Lives, com o (CUIDADO); 2 XP + 1 Captura são 3 resgates com a descrição do bônus, e lê o saldo no fim",
                bulkShown && bulk is not null && bulkRead && bulkReady && bulkOrder
                && bulkBefore.Contains("fulano_twitch tem 1.500 pontos em biscoitao") && bulkBefore.Contains("Total: 1.100 pontos (sobram 400)")
                && bulk.Done == 3 && bulk.Stop is null && _fakeTwitchPoints["biscoitao"] == 400 && _fakeTwitch[^1].StartsWith("saldo biscoitao")
                && bulkEnd.EndsWith("Fim: 3 de 3. Saldo agora: 400."),
                $"menu={bulkShown} leu={bulkRead} pronto={bulkReady} | {bulkBefore.Replace(Environment.NewLine, " ")} | " +
                $"pedidos={string.Join(" | ", _fakeTwitch)} | {bulkEnd} saldo={_fakeTwitchPoints["biscoitao"]}");

            // Sem saldo para o lote, não começa (nenhum resgate). Com saldo, mas os pontos gastos por fora depois do 1º
            // (outro resgate no meio), para no NOT_ENOUGH_POINTS da Twitch e mostra o saldo do fim; o Cancelar para depois
            // do resgate da vez. Tudo no registro dos resgates.
            string bulkShort = "", bulkStopped = "", bulkCancelled = "";
            bool bulkRefused = false, bulkNotEnough = false, bulkCancel = false;
            if (bulk is not null)
            {
                bulk.SetCounts(2, 0, 0);
                await bulk.CheckAsync();
                bulkShort = bulk.StatusText;
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                bulkRefused = !bulk.CanRedeem && bulkShort.Contains("Saldo insuficiente: precisa de 600 pontos e a conta tem 400.")
                              && bulk.Done == 0 && bulk.Stop?.StartsWith("Saldo insuficiente") == true && !_fakeTwitch.Any(r => r.StartsWith("resgate"));

                _fakeTwitchPoints["biscoitao"] = 1000;
                bulk.SetCounts(3, 0, 0);
                _fakeTwitchAfterRedeem = channel => _fakeTwitchPoints[channel] = 100;
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                bulkStopped = string.Join(" / ", bulk.ProgressLines);
                bulkNotEnough = bulk.Done == 1 && bulk.Stop == "erro NOT_ENOUGH_POINTS" && _fakeTwitch.Count(r => r.StartsWith("resgate")) == 2
                                && bulkStopped.Contains("2/3 +15% XP: recusado, erro NOT_ENOUGH_POINTS. Parei aqui.")
                                && bulkStopped.EndsWith("Fim: 1 de 3 (erro NOT_ENOUGH_POINTS). Saldo agora: 100.");

                _fakeTwitchPoints["biscoitao"] = 1000;
                _fakeTwitchAfterRedeem = _ => bulk.Cancel();
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                _fakeTwitchAfterRedeem = null;
                bulkCancelled = string.Join(" / ", bulk.ProgressLines);
                bulkCancel = bulk.Done == 1 && bulk.Stop == "cancelado" && _fakeTwitch.Count(r => r.StartsWith("resgate")) == 1
                             && _fakeTwitchPoints["biscoitao"] == 700 && bulkCancelled.EndsWith("Fim: 1 de 3 (cancelado). Saldo agora: 700.");
            }
            var bulkLog = _redeemLog.Lines.Where(l => l.Contains($"  {account1} · Twitch · biscoitao: ")).ToList();
            bool BulkLogged(string text) => bulkLog.Any(l => l.EndsWith($"  {account1} · Twitch · biscoitao: {text}"));
            Check("resgate em lote: sem saldo não começa; para no 1º erro da Twitch (NOT_ENOUGH_POINTS) e no Cancelar; tudo no registro",
                bulkRefused && bulkNotEnough && bulkCancel
                && BulkLogged("resgate em lote de 2 XP + 1 Captura (1100 pontos; saldo 1500)")
                && BulkLogged("resgatou Bônus +15% XP PokeIdle (em lote, 1 de 3; 300 pontos; sobraram 1200)")
                && BulkLogged("resgatou Bônus de +15% Capture Boost (em lote, 2 de 3; 500 pontos; sobraram 700)")
                && BulkLogged("resgate em lote terminou: 3 de 3; saldo 400")
                && BulkLogged("o resgate em lote de Bônus +15% XP PokeIdle foi recusado (erro NOT_ENOUGH_POINTS; saldo 700); parei no 2 de 3")
                && BulkLogged("resgate em lote terminou: 1 de 3 (erro NOT_ENOUGH_POINTS); saldo 100")
                && BulkLogged("resgate em lote terminou: 1 de 3 (cancelado); saldo 700"),
                $"{bulkShort.Replace(Environment.NewLine, " ")} | {bulkStopped} | {bulkCancelled} | {string.Join(" | ", bulkLog)}");

            // Resgate em lote na Kick, com o login da conta 1 (o session_token vai como Bearer): 1 XP + 1 Captura com 1000
            // pontos são 2 resgates (XP, Captura), cada um com um transaction_id novo; sobram 200, lidos de novo no fim.
            string kickBulkBefore = "", kickEnd = "";
            bool kickRead = false, kickReady = false;
            int kickFrom = _fakeRedeems.Count;
            if (bulk is not null)
            {
                _fakePoints["biscoitao"] = 1000;
                bulk.SetCounts(1, 1, 0);
                bulk.Platform = LiveSite.Kick;
                kickRead = await bulk.CheckAsync();
                kickBulkBefore = bulk.StatusText;
                kickReady = bulk.CanRedeem;
                await bulk.RunAsync();
                kickEnd = string.Join(" / ", bulk.ProgressLines);
            }
            var kickBulk = _fakeRedeems.Skip(kickFrom).ToList();
            const string kickBody = " \\{\"transaction_id\":\"[0-9A-HJKMNP-TV-Z]{26}\",\"message\":\"\"\\}$";
            bool kickOrder = kickBulk.Count == 2
                && System.Text.RegularExpressions.Regex.IsMatch(kickBulk[0], "^biscoitao xp Bearer 123\\|abc" + kickBody)
                && System.Text.RegularExpressions.Regex.IsMatch(kickBulk[1], "^biscoitao cap Bearer 123\\|abc" + kickBody);
            Check("resgate em lote na Kick: com o login da conta, 1 XP + 1 Captura são 2 resgates, e lê o saldo no fim",
                kickRead && kickReady && kickOrder && kickBulkBefore.Contains("fulano_kick tem 1.000 pontos em biscoitao na Kick")
                && kickBulkBefore.Contains("Total: 800 pontos (sobram 200)") && bulk?.Done == 2 && bulk.Stop is null
                && _fakePoints["biscoitao"] == 200 && kickEnd.EndsWith("Fim: 2 de 2. Saldo agora: 200."),
                $"leu={kickRead} pronto={kickReady} | {kickBulkBefore.Replace(Environment.NewLine, " ")} | pedidos={string.Join(" | ", kickBulk)} | " +
                $"{kickEnd} saldo={_fakePoints["biscoitao"]}");

            // O dono do canal tem pontos infinitos: na Twitch o saldo nem vem, na Kick vem 0. O lote reconhece o dono (a
            // conta logada é o usuário do canal; a Kick de mentira nem tem o /me do canal), mostra ∞, não exige saldo e
            // resgata 2 Shiny (2000 pontos) nos dois sites, sem gastar nada. Tudo no registro.
            string ownerTwitch = "", ownerKick = "", ownerTwitchEnd = "", ownerKickEnd = "";
            bool ownerTwitchReady = false, ownerKickReady = false;
            int ownerTwitchDone = 0, ownerKickDone = 0, ownerKickFrom = _fakeRedeems.Count;
            if (bulk is not null)
            {
                _fakeTwitchMe = _fakeKickMe = "biscoitao";
                _fakeTwitchPoints["biscoitao"] = 0;
                _fakePoints["biscoitao"] = 0;
                bulk.SetCounts(0, 0, 2);
                bulk.Platform = LiveSite.Twitch;
                await bulk.CheckAsync();
                ownerTwitch = bulk.StatusText;
                ownerTwitchReady = bulk.CanRedeem;
                _fakeTwitch.Clear();
                await bulk.RunAsync();
                ownerTwitchDone = bulk.Done;
                ownerTwitchEnd = string.Join(" / ", bulk.ProgressLines);
                bulk.Platform = LiveSite.Kick;
                await bulk.CheckAsync();
                ownerKick = bulk.StatusText;
                ownerKickReady = bulk.CanRedeem;
                await bulk.RunAsync();
                ownerKickDone = bulk.Done;
                ownerKickEnd = string.Join(" / ", bulk.ProgressLines);
                _fakeTwitchMe = "fulano_twitch";
                _fakeKickMe = "fulano_kick";
                bulk.Close();
            }
            int ownerTwitchRedeems = _fakeTwitch.Count(r => r.StartsWith("resgate biscoitao lure 1000 OAuth tok123 "));
            int ownerKickRedeems = _fakeRedeems.Skip(ownerKickFrom).Count(r => r.StartsWith("biscoitao lure Bearer 123|abc "));
            var ownerLog = _redeemLog.Lines.Where(l => l.Contains($"  {account1} · ") && l.Contains(" · biscoitao: ")).ToList();
            bool OwnerLogged(LiveSite site, string text) => ownerLog.Any(l => l.EndsWith($"  {account1} · {site} · biscoitao: {text}"));
            bool ownerLogs = new[] { LiveSite.Twitch, LiveSite.Kick }.All(site =>
                OwnerLogged(site, "resgate em lote de 2 Shiny (2000 pontos; dono do canal, pontos infinitos)")
                && OwnerLogged(site, "resgatou Bônus de +15% Secret Lure (em lote, 2 de 2; 1000 pontos; dono do canal)")
                && OwnerLogged(site, "resgate em lote terminou: 2 de 2; saldo ∞ (dono do canal)"))
                && OwnerLogged(LiveSite.Kick, "resgatou Bônus de +15% Capture Boost (em lote, 2 de 2; 500 pontos; sobraram 200)");
            Check("resgate em lote: o dono do canal (pontos infinitos) resgata sem saldo, na Twitch e na Kick; tudo no registro",
                ownerTwitchReady && ownerKickReady
                && ownerTwitch.Contains("biscoitao é o dono do canal biscoitao na Twitch: pontos infinitos (∞).")
                && ownerTwitch.Contains("Total: 2.000 pontos (dono do canal: não precisa de saldo)")
                && ownerKick.Contains("biscoitao é o dono do canal biscoitao na Kick: pontos infinitos (∞).")
                && ownerTwitchDone == 2 && ownerKickDone == 2 && ownerTwitchRedeems == 2 && ownerKickRedeems == 2
                && ownerTwitchEnd.EndsWith("Fim: 2 de 2. Saldo agora: ∞ (dono do canal).")
                && ownerKickEnd.EndsWith("Fim: 2 de 2. Saldo agora: ∞ (dono do canal).")
                && _fakeTwitchPoints["biscoitao"] == 0 && _fakePoints["biscoitao"] == 0 && ownerLogs && bulk?.IsDisposed == true,
                $"{ownerTwitch.Replace(Environment.NewLine, " ")} | {ownerTwitchEnd} | {ownerKick.Replace(Environment.NewLine, " ")} | {ownerKickEnd} | " +
                $"twitch={ownerTwitchRedeems} kick={ownerKickRedeems} | {string.Join(" | ", ownerLog.TakeLast(8))}");

            // Avisos da Twitch (o websocket da conta; o de mentira, em localhost): a conferência liga com o login da conta
            // e ouve os pontos da conta e a raid e o momento da live aberta. O baú que aparece é pego na hora; a
            // sequência de lives vai para o registro e para o rendimento (+450/h); a raid avisada duas vezes é uma entrada
            // só; o momento é pego. Live aberta sem ponto de assistir avisa, e o próximo ponto de assistir tira o aviso.
            _redeemTwitch[0].Restart();
            _fakeTwitchPoints["tftoddy"] = 100;
            await TwitchPointsAsync();
            var fakePubSub = _fakePubSub;
            const string userTopic = "community-points-user-v1.id-fulano_twitch";
            bool listening = fakePubSub is not null && await WaitFor(() => fakePubSub.Listening(userTopic)
                && fakePubSub.Listening("raid.id-tftoddy") && fakePubSub.Listening("community-moments-channel-v1.id-tftoddy"), 15);
            bool withLogin = fakePubSub?.Listens.Contains($"{userTopic} tok123") == true;
            bool claimedNow = false, gone = false, streak = false, raided = false, moment = false, quietShown = false, quietGone = false, badAuth = false;
            string earnedSummary = "", quietSummary = "";
            if (fakePubSub is not null && listening)
            {
                static string Stamp() => DateTime.UtcNow.ToString("o");
                object Earn(int points, string reason, int balance) => new
                {
                    type = "points-earned",
                    data = new
                    {
                        timestamp = Stamp(), channel_id = "id-tftoddy",
                        point_gain = new { channel_id = "id-tftoddy", total_points = points, reason_code = reason },
                        balance = new { channel_id = "id-tftoddy", balance },
                    },
                };
                // Como na Twitch de verdade: a consulta não mostra o baú que o aviso deu, e ele vai com o id do aviso.
                _fakeTwitchClaims.Remove("tftoddy");
                _fakeTwitch.Clear();
                // Um baú que outro lugar já pegou (NOT_FOUND) vai ao registro e não segura o próximo por 30 min.
                await fakePubSub.SendAsync(userTopic, new { type = "claim-available", data = new { timestamp = Stamp(), claim = new { id = "bau-pego", channel_id = "id-tftoddy" } } });
                gone = await WaitFor(() => _redeemLog.Lines.Any(l => l.EndsWith($"  {account1} · Twitch · tftoddy: o baú já tinha sido pego (a conta está aberta em outro lugar?)")), 15)
                       && !LivesSummary(0).Contains("NOT_FOUND");
                await fakePubSub.SendAsync(userTopic, new { type = "claim-available", data = new { timestamp = Stamp(), claim = new { id = "bau-tftoddy", channel_id = "id-tftoddy" } } });
                claimedNow = await WaitFor(() => _fakeTwitch.Contains("baú bau-tftoddy OAuth tok123"), 15);
                await fakePubSub.SendAsync(userTopic, Earn(450, "WATCH_STREAK", 600));
                streak = await WaitFor(() => _redeemLog.Lines.Any(l => l.EndsWith($"  {account1} · Twitch · tftoddy: ganhou +450 da sequência de lives (saldo 600)")), 10);
                earnedSummary = LivesSummary(0);
                var raidNotice = new { type = "raid_update_v2", raid = new { id = "raid1", target_login = "outro" } };
                await fakePubSub.SendAsync("raid.id-tftoddy", raidNotice);
                await fakePubSub.SendAsync("raid.id-tftoddy", raidNotice);
                raided = await WaitFor(() => _redeemLog.Lines.Any(l => l.EndsWith($"  {account1} · Twitch · tftoddy: entrou na raid (para outro)")), 10);
                await Task.Delay(500);
                raided = raided && _fakeTwitch.Count(l => l == "raid raid1 OAuth tok123") == 1;
                await fakePubSub.SendAsync("community-moments-channel-v1.id-tftoddy", new { type = "active", data = new { moment_id = "m1" } });
                moment = await WaitFor(() => _fakeTwitch.Contains("momento m1 OAuth tok123")
                                             && _redeemLog.Lines.Any(l => l.EndsWith($"  {account1} · Twitch · tftoddy: pegou o momento")), 10);
                _redeemTwitch[0].QuietLimit = TimeSpan.FromSeconds(3);
                await Task.Delay(3500);
                await TwitchPointsAsync();
                quietSummary = LivesSummary(0);
                quietShown = quietSummary.Contains("sem pontos de assistir há")
                             && _redeemLog.Lines.Any(l => l.EndsWith($"  {account1} · Twitch · tftoddy: a live está aberta há 0 min sem os pontos de assistir"));
                await fakePubSub.SendAsync(userTopic, Earn(10, "WATCH", 610));
                quietGone = await WaitFor(() => !LivesSummary(0).Contains("sem pontos de assistir") && LivesSummary(0).Contains("+460/h"), 2);
                _redeemTwitch[0].QuietLimit = TimeSpan.FromMinutes(20);
                // O login recusado (ERR_BADAUTH) para de vez e diz o porquê.
                using var refused = new TwitchPubSub(fakePubSub.Url, "recusado", "id-x", _ => { });
                badAuth = await WaitFor(() => refused.Error == "a Twitch recusou o login da conta nos avisos" && !refused.Connected, 10);
            }
            Check("avisos da Twitch: o baú na hora, a sequência de lives e o rendimento, a raid (uma vez) e o momento; live sem render avisa",
                listening && withLogin && gone && claimedNow && streak && earnedSummary.Contains("+450/h") && raided && moment && quietShown && quietGone
                && badAuth,
                $"ouvindo={listening} login={withLogin} já pego={gone} baú={claimedNow} sequência={streak} raid={raided} momento={moment} recusado={badAuth} " +
                $"sem render={quietShown}/{quietGone} | {earnedSummary} | {quietSummary} | pedidos={string.Join(" | ", _fakeTwitch)} | " +
                $"tópicos={string.Join(", ", fakePubSub?.Listens ?? new List<string>())}");

            // Twitch leve (Lives → Config…, por conta): a live da Twitch da conta 1 abre parada (sem vídeo; o deck não manda
            // tocar nem recarrega), e quem assiste é o deck, sem navegador: o aviso de "assistindo" (no endereço tirado da
            // config da página, com o id da conta, o do canal e o da transmissão) e só o cabeçalho dos pedaços do vídeo na
            // menor qualidade, com o token do vídeo pedido com o login da conta. Desligar volta o player e para o deck.
            _config.TwitchLight[0] = true;
            ApplyLives();
            var lightLives = _lives[0];
            bool still = await WaitFor(() => lightLives is not null && lightLives.Loaded == lightLives.Count
                && lightLives.Urls(LiveSite.Twitch).Any(u => u.EndsWith("&" + TwitchLive.Still)), 15);
            await TwitchPointsAsync();
            bool watched = await WaitFor(() => _watchers[0]?.Counts("tftoddy") is ( >= 2, >= 3), 20);
            if (lightLives is not null)
                for (int i = 0; i < 3; i++)
                    await lightLives.WatchAsync();
            bool notForced = lightLives?.Playback(LiveSite.Twitch).All(p => p.Playing is null && p.Reloads == 0) == true;
            var webLog = _fakePubSub?.Web ?? new List<string>();
            string spade = webLog.FirstOrDefault(l => l.StartsWith("spade ")) ?? "";
            bool payload = spade.Contains("\"event\":\"minute-watched\"") && spade.Contains("\"channel_id\":\"id-tftoddy\"")
                && spade.Contains("\"broadcast_id\":\"b-tftoddy\"") && spade.Contains("\"user_id\":\"id-fulano_twitch\"")
                && spade.Contains("\"channel\":\"tftoddy\"") && spade.Contains("\"player\":\"site\"") && spade.Contains("\"live\":true")
                && spade.Contains("\"game_id\":\"g1\"");
            bool fromPage = webLog.Contains("página tftoddy");
            bool tokenWithLogin = webLog.Contains("token tftoddy OAuth tok123") && webLog.Contains("usher tftoddy sig=sig1");
            bool lowest = webLog.Contains("lista tftoddy 160p") && !webLog.Any(l => l.StartsWith("lista tftoddy 720p") || l.StartsWith("lista tftoddy audio"));
            bool headOnly = webLog.Any(l => l.StartsWith("HEAD /seg/tftoddy/")) && !webLog.Any(l => l.StartsWith("GET /seg/"));
            string lightSummary = LivesSummary(0);

            // Twitch leve sob demanda: o player parado fica em branco (sem o processo da Twitch). A conferência de 2 em 2 min
            // lê o saldo de fora da página e não abre o player à toa; o aviso do baú abre, pega e, sem ação, volta para o
            // branco; a conferência abre para o baú que ficou esperando e para o resgate que dá.
            bool blankIdle = false, readOutside = false, notWoken = false, claimWoke = false, sleptAgain = false, checkClaim = false, redeemWoke = false;
            int wakesBefore = 0;
            if (lightLives is not null && _fakePubSub is FakePubSub lightPubSub)
            {
                bool Blank() => lightLives.TwitchAwake == 0 && lightLives.Urls(LiveSite.Twitch).Any()
                                && lightLives.Browsers(LiveSite.Twitch) == 0;
                blankIdle = await WaitFor(Blank, 10);
                _fakeTwitch.Clear();
                wakesBefore = lightLives.Wakes;
                await TwitchPointsAsync();
                readOutside = _fakeTwitch.Contains("leitura tftoddy OAuth tok123 dev123") && !_fakeTwitch.Any(l => l.StartsWith("saldo "));
                notWoken = lightLives.Wakes == wakesBefore && Blank();

                _fakeTwitchClaims.Add("tftoddy");
                _fakeTwitch.Clear();
                await lightPubSub.SendAsync(userTopic, new
                {
                    type = "claim-available",
                    data = new { timestamp = DateTime.UtcNow.ToString("o"), claim = new { id = "bau-tftoddy", channel_id = "id-tftoddy" } },
                });
                claimWoke = await WaitFor(() => _fakeTwitch.Contains("baú bau-tftoddy OAuth tok123"), 15) && lightLives.Wakes == wakesBefore + 1;
                sleptAgain = await WaitFor(Blank, 10);

                _fakeTwitchClaims.Add("tftoddy");
                _fakeTwitch.Clear();
                await TwitchPointsAsync();
                checkClaim = _fakeTwitch.Contains("leitura tftoddy OAuth tok123 dev123") && _fakeTwitch.Contains("baú bau-tftoddy OAuth tok123")
                             && lightLives.Wakes == wakesBefore + 2;
                await WaitFor(Blank, 10);

                var redeemBefore = _config.RedeemTwitch[0];
                _config.RedeemTwitch[0] = KickReward.Xp;
                _fakeTwitchPoints["tftoddy"] = 400;
                _fakeTwitch.Clear();
                await TwitchPointsAsync();
                redeemWoke = _fakeTwitch.Any(l => l.StartsWith("resgate tftoddy xp 300 OAuth tok123 ")) && lightLives.Wakes == wakesBefore + 3
                             && _fakeTwitchPoints["tftoddy"] == 100;
                _config.RedeemTwitch[0] = redeemBefore;
                await WaitFor(Blank, 10);
            }
            string wakesSummary = LivesSummary(0);
            Check("Twitch leve sob demanda: o player fica em branco, o saldo é lido sem ele, e ele só abre para o baú e o resgate",
                blankIdle && readOutside && notWoken && claimWoke && sleptAgain && checkClaim && redeemWoke
                && wakesSummary.Contains($"player leve abriu {lightLives?.Wakes}× desde "),
                $"em branco={blankIdle} leu fora={readOutside} não abriu={notWoken} baú pelo aviso={claimWoke} voltou ao branco={sleptAgain} " +
                $"baú pela conferência={checkClaim} resgate={redeemWoke} aberturas={lightLives?.Wakes - wakesBefore} " +
                $"páginas={string.Join(",", lightLives?.Sources(LiveSite.Twitch) ?? Array.Empty<string>())} | {wakesSummary} | " +
                $"pedidos={string.Join(" | ", _fakeTwitch)}");

            _config.TwitchLight[0] = false;
            ApplyLives();
            await TwitchPointsAsync();
            bool lightBack = await WaitFor(() => lightLives is not null && lightLives.Loaded == lightLives.Count && _watchers[0] is null
                && lightLives.Urls(LiveSite.Twitch).Any() && lightLives.Urls(LiveSite.Twitch).All(u => !u.Contains(TwitchLive.Still)), 15);
            Check("Twitch leve: o player abre parado e o deck assiste sem navegador (o aviso de assistindo e só o cabeçalho do vídeo); desligar volta o player",
                still && watched && notForced && payload && fromPage && tokenWithLogin && lowest && headOnly && lightSummary.Contains("tftoddy leve ✓") && lightBack,
                $"parado={still} assistiu={watched} ({_watchers[0]?.Counts("tftoddy")}) sem forçar={notForced} aviso={payload} config={fromPage} " +
                $"token={tokenWithLogin} menor={lowest} só cabeçalho={headOnly} voltou={lightBack} | {lightSummary} | {spade} | " +
                string.Join(" | ", webLog.Where(l => !l.StartsWith("HEAD ") && !l.StartsWith("spade ")).Distinct().Take(12)));

            // Kick leve (Lives → Config…, por conta): as lives da Kick da conta 1 ficam em branco (sem navegador), e quem
            // assiste é o deck, como o player por baixo: o token de espectador com o login da conta (e o token do site), o
            // websocket com o handshake do canal, o ping e o tempo assistido (o id do canal, o da live e o do VOD, tirados
            // da info do canal). A conferência lê o saldo sem a página e só abre o player para resgatar. Desligar volta.
            _config.KickLight[0] = true;
            ApplyLives();
            var kickLightLives = _lives[0];
            bool KickBlank() => kickLightLives is not null && kickLightLives.Urls(LiveSite.Kick).Any()
                                && kickLightLives.Urls(LiveSite.Kick).All(u => u.EndsWith("&" + TwitchLive.Still))
                                && kickLightLives.Browsers(LiveSite.Kick) == 0;
            bool kickBlank = await WaitFor(KickBlank, 15);
            await UpdateKickWatchersAsync();
            string kickChannel = kickLightLives?.KickChannels.FirstOrDefault() ?? "";
            bool kickWatched = await WaitFor(() => _kickWatchers[0]?.Counts(kickChannel) is ( >= 2, >= 2, _), 15);
            var kickWeb = _fakePubSub?.Web ?? new List<string>();
            int kickId = FakePubSub.KickChannelId(kickChannel), kickLiveId = FakePubSub.KickLivestreamId(kickChannel);
            bool kickToken = kickWeb.Contains("kick token Bearer 123|abc site ok") && kickWeb.Contains("kick conectou vt-123|abc");
            bool kickInfo = kickWeb.Contains($"kick info {kickChannel} Bearer 123|abc");
            bool kickHandshake = kickWeb.Contains($"kick handshake {kickId}") && kickWeb.Contains("kick ping");
            bool kickPayload = kickWeb.Contains($"kick assistiu tracking.user.watch.livestream {kickId} {kickLiveId} \"vod-{kickChannel}\"");
            string kickLightSummary = LivesSummary(0);

            bool kickReadOutside = false, kickNotWoken = false, kickRedeemWoke = false, kickSlept = false;
            int kickWakesBefore = 0;
            if (kickLightLives is not null)
            {
                var kickRedeemBefore = _config.RedeemKick[0];
                _config.RedeemKick[0] = KickReward.Capture;
                _redeemKick[0].Restart();
                _fakePoints[kickChannel] = 100;
                _fakeRedeems.Clear();
                kickWakesBefore = kickLightLives.Wakes;
                await RedeemKickAsync();
                var readWeb = _fakePubSub?.Web ?? new List<string>();
                kickReadOutside = readWeb.Contains($"kick points {kickChannel} Bearer 123|abc") && readWeb.Contains($"kick rewards {kickChannel} Bearer 123|abc");
                kickNotWoken = kickLightLives.Wakes == kickWakesBefore && _fakeRedeems.Count == 0 && KickBlank();
                _fakePoints[kickChannel] = 600;
                await RedeemKickAsync();
                kickRedeemWoke = _fakeRedeems.Count(l => l.StartsWith($"{kickChannel} cap Bearer 123|abc ")) == 1 && kickLightLives.Wakes == kickWakesBefore + 1
                                 && _fakePoints[kickChannel] == 100;
                kickSlept = await WaitFor(KickBlank, 10);
                _config.RedeemKick[0] = kickRedeemBefore;
                _redeemKick[0].Restart();
            }

            _config.KickLight[0] = false;
            ApplyLives();
            await UpdateKickWatchersAsync();
            bool kickBack = await WaitFor(() => kickLightLives is not null && _kickWatchers[0] is null && kickLightLives.Loaded == kickLightLives.Count
                && kickLightLives.Urls(LiveSite.Kick).Any() && kickLightLives.Urls(LiveSite.Kick).All(u => !u.Contains(TwitchLive.Still)), 15);
            bool kickSaid = await WaitFor(() => (_fakePubSub?.Web ?? new List<string>()).Contains($"kick saiu {kickId}"), 5);
            Check("Kick leve: as lives ficam em branco e o deck assiste como o player (token, handshake, ping e tempo assistido); o saldo é lido sem a página, que só abre para resgatar; desligar volta",
                kickBlank && kickWatched && kickToken && kickInfo && kickHandshake && kickPayload && kickLightSummary.Contains($"{kickChannel} leve ✓")
                && kickReadOutside && kickNotWoken && kickRedeemWoke && kickSlept && kickBack && kickSaid,
                $"em branco={kickBlank} assistiu={kickWatched} ({_kickWatchers[0]?.Counts(kickChannel)}) token={kickToken} info={kickInfo} " +
                $"handshake={kickHandshake} tempo={kickPayload} leu fora={kickReadOutside} não abriu={kickNotWoken} resgate={kickRedeemWoke} " +
                $"voltou ao branco={kickSlept} desligou={kickBack} saiu={kickSaid} | {kickLightSummary} | " +
                string.Join(" | ", kickWeb.Where(l => l.StartsWith("kick ")).Distinct().Take(10)));

            // deck.json: o de antes (um resgate para todas as contas) vira o mesmo bônus em cada conta; um número fora
            // da lista vira o XP; de fábrica nenhuma conta resgata.
            var redeemPath = Path.Combine(_root, "deck-resgate.json");
            File.WriteAllText(redeemPath, "{ \"KickRedeem\": true, \"KickReward\": \"Lure\" }");
            var redeemConfig = DeckConfig.Load(redeemPath);
            redeemConfig.Save(redeemPath);
            string redeemSaved = File.ReadAllText(redeemPath);
            File.WriteAllText(redeemPath, "{ \"RedeemKick\": [ \"Capture\", null, 7 ] }");
            var perAccount = DeckConfig.Load(redeemPath);
            Check("pontos da Kick: o resgate de cada conta fica no deck.json (nenhuma de fábrica; o de antes vale para todas)",
                redeemConfig.RedeemKick.All(r => r == KickReward.Lure) && !redeemSaved.Contains("\"KickRedeem\"") && !redeemSaved.Contains("\"KickReward\"")
                && perAccount.RedeemKick.SequenceEqual(new KickReward?[] { KickReward.Capture, null, KickReward.Xp, null })
                && new DeckConfig().RedeemKick.All(r => r is null),
                $"antes→{string.Join(",", redeemConfig.RedeemKick)} por conta→{string.Join(",", perAccount.RedeemKick.Select(r => r?.ToString() ?? "não"))}");

            // A Twitch e a Kick leves vêm ligadas em todas as contas, também no deck.json de antes (sem o campo) e no que
            // tinha uma conta desligada (desde 12/11 não desligam mais).
            File.WriteAllText(redeemPath, "{ \"TwitchLives\": 2 }");
            var lightOld = DeckConfig.Load(redeemPath);
            File.WriteAllText(redeemPath, "{ \"TwitchLight\": [ false, true ] }");
            var lightSaved = DeckConfig.Load(redeemPath);
            Check("Twitch leve: sempre ligada em todas as contas (no deck.json de antes e no que tinha uma desligada também)",
                lightByDefault && new DeckConfig().TwitchLight.All(l => l) && lightOld.TwitchLight.All(l => l)
                && lightSaved.TwitchLight.SequenceEqual(new[] { true, true, true, true }),
                $"autoteste={lightByDefault} de antes={string.Join(",", lightOld.TwitchLight)} salva={string.Join(",", lightSaved.TwitchLight)}");
            File.WriteAllText(redeemPath, "{ \"KickLight\": [ true, false ] }");
            var kickLightSaved = DeckConfig.Load(redeemPath);
            Check("Kick leve: sempre ligada em todas as contas (no deck.json de antes e no que tinha uma desligada também)",
                _kickLightByDefault && new DeckConfig().KickLight.All(l => l) && lightOld.KickLight.All(l => l)
                && kickLightSaved.KickLight.SequenceEqual(new[] { true, true, true, true }),
                $"autoteste={_kickLightByDefault} de antes={string.Join(",", lightOld.KickLight)} salva={string.Join(",", kickLightSaved.KickLight)}");
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

            // deck.json de antes do limite: o 2 fixo escrito em cada conta vira "todas"; com o limite em 3, uma conta
            // que fechou uma live fica com 2, e um limite fora da faixa é corrigido.
            File.WriteAllText(oldConfig, "{ \"Kick\": [ { \"Slots\": 2, \"Closed\": [] }, { \"Slots\": 1, \"Closed\": [\"yetz\"] } ] }");
            var legacy = DeckConfig.Load(oldConfig);
            File.WriteAllText(oldConfig, "{ \"KickLives\": 3, \"Kick\": [ { \"Slots\": 2, \"Closed\": [\"yetz\"] }, { \"Slots\": 3, \"Closed\": [] } ] }");
            var three = DeckConfig.Load(oldConfig);
            File.WriteAllText(oldConfig, "{ \"KickLives\": 99 }");
            var tooMany = DeckConfig.Load(oldConfig);
            Check("kick: limite de lives no deck.json (o de antes vira todas; fora da faixa é corrigido)",
                legacy.KickLives == 2 && legacy.Kick[0].Slots is null && !legacy.Kick[0].Limited && legacy.Kick[1].Slots == 1 && legacy.Kick[1].Limited
                && three.KickLives == 3 && three.Kick[0].SlotsOf(3) == 2 && three.Kick[0].Limited && three.Kick[1].Slots is null && three.Kick[2].SlotsOf(3) == 3
                && tooMany.KickLives == KickLive.MaxSlots,
                $"antes={legacy.KickLives}:{string.Join(",", legacy.Kick.Select(k => k.Slots?.ToString() ?? "todas"))} " +
                $"com 3={string.Join(",", three.Kick.Select(k => k.SlotsOf(3)))} 99→{tooMany.KickLives}");

            // deck.json do tempo da presença no chat: o modo da Twitch some ao salvar, e o número de lives fica na faixa.
            File.WriteAllText(oldConfig, "{ \"TwitchMode\": \"Presence\", \"TwitchLives\": 99 }");
            var presenceConfig = DeckConfig.Load(oldConfig);
            presenceConfig.Save(oldConfig);
            var presenceSaved = File.ReadAllText(oldConfig);
            File.WriteAllText(oldConfig, "{ \"TwitchMode\": \"Live\" }");
            Check("Twitch: deck.json antigo (presença no chat) vira 2 lives, e o número fica na faixa",
                presenceConfig.TwitchLives == TwitchLive.MaxSlots && !presenceSaved.Contains("TwitchMode")
                && DeckConfig.Load(oldConfig).TwitchLives == TwitchLive.DefaultSlots,
                $"99→{presenceConfig.TwitchLives}");

            // O campo em Lives → Config…: com 3, a conta abre as 3 primeiras da lista (o autoteste não pergunta à Kick
            // quem está no ar, então conta todas como no ar), e a janela dos pontos põe as 3 colunas lado a lado.
            // O da Twitch, em 2: abre as 2 primeiras da lista dela.
            using (var channelsDialog = new ChannelsDialog(_config.TwitchChannels, _config.KickChannels, 2, 3,
                _config.AccountNames, _config.RedeemKick, _config.RedeemTwitch, _config.ClaimTwitchBonus))
            {
                SetKickLives(channelsDialog.KickLives);
                _config.TwitchLives = channelsDialog.TwitchLives;
            }
            CloseLives(0);
            await OpenLivesAsync(0);
            var lives3 = _lives[0];
            bool opened3 = await WaitFor(() => lives3 is not null && lives3.Loaded == lives3.Count
                && lives3.KickChannels.SequenceEqual(new[] { "biscoitao", "yetz", "surskity" })
                && lives3.TwitchChannels.SequenceEqual(new[] { "tftoddy", "clovaobengala" }), 20);
            _config.TwitchLives = 1;
            ShowPoints(0, LiveSite.Kick);
            await Task.Delay(300);
            // Lado a lado: cada coluna começa num lugar diferente da janela.
            int columns3 = lives3?.KickLayout.Select(c => c.Frame.Left).Distinct().Count() ?? 0;
            lives3?.Close();
            SetKickLives(2);
            CloseLives(0);
            await OpenLivesAsync(0);
            bool back2 = await WaitFor(() => _lives[0] is LivesWindow w && w.Loaded == w.Count && w.KickChannels.Count() == 2, 20);
            Check("kick: com o limite em 3 a conta abre 3 lives (3 colunas nos pontos), e volta a 2; Twitch em 2 abre 2",
                opened3 && columns3 == 3 && back2,
                $"Kick={string.Join(",", lives3?.KickChannels ?? Enumerable.Empty<string>())} " +
                $"Twitch={string.Join(",", lives3?.TwitchChannels ?? Enumerable.Empty<string>())} colunas={columns3} " +
                $"de volta a 2={back2} ({string.Join(",", _lives[0]?.KickChannels ?? Enumerable.Empty<string>())})");

            // ⏻ na conta 1 (que tem lives): o jogo vira página em branco e as lives fecham; ligar traz tudo de volta.
            await SetPaneOffAsync(0, true);
            bool closed = await WaitFor(() => _panes[0].Core?.Source == "about:blank", 10);
            Check("desligar a conta 1: o jogo fecha, as lives param e o cabeçalho mostra 'desligada'",
                closed && _panes[0].Off && _lives[0] is null && _panes[0].StatusText == "desligada"
                && _panes[0].VoteText is null && _config.LivesAccounts.Contains(1),
                $"página={_panes[0].Core?.Source} lives={(_lives[0] is null ? "fechadas" : "abertas")} status='{_panes[0].StatusText}'");
            await SetPaneOffAsync(0, false);
            bool back = await WaitFor(() => _panes[0].Status == PaneStatus.Ok && _panes[0].Core?.DocumentTitle == "Jogo-teste"
                && _lives[0] is LivesWindow w && w.Count > 0 && w.Loaded == w.Count, 20);
            Check("ligar a conta 1 de novo: o jogo e as lives voltam", back,
                $"status={_panes[0].Status} título='{_panes[0].Core?.DocumentTitle}' lives={(_lives[0] is LivesWindow lw ? $"{lw.Loaded}/{lw.Count}" : "fechadas")}");
            Check("menos animações continua depois de desligar e ligar a conta", await Eval(_panes[0], ReducedMotion) == "true");

            // Sem ativar a janela: ativada, ela traria a Área de trabalho 2 para a tela (ou tiraria o foco do jogo).
            TestDesktop.SetMinimized(this, true);
            await Task.Delay(1500);
            var alive = await Eval(_panes[2], "1 + 1");
            TestDesktop.SetMinimized(this, false);
            await Task.Delay(1000);
            Check("minimizar e restaurar com as contas vivas", alive == "2" && Shape(2, 2), Detail());

            // Voto no IdleHub, por partes (o autoteste não sai para a internet).
            Check("link de voto com o nick do personagem",
                IdleHub.VoteUrl("fulano") == "https://idlehub.io/votar/pokeidle-io?jogador=fulano"
                && IdleHub.VoteUrl("nick com espaço") == "https://idlehub.io/votar/pokeidle-io" && IdleHub.VoteUrl(null) == "https://idlehub.io/votar/pokeidle-io");
            Check("nick lido do jogo", IdleHub.ParseNick("\"fulano\"") == "fulano"
                && IdleHub.ParseNick("\"\\\"ciclano\\\"\"") == "ciclano" && IdleHub.ParseNick("null") is null);
            VoteState? ParseVote(string json)
            {
                using var doc = JsonDocument.Parse(json);
                return IdleHub.ParseState(doc.RootElement);
            }
            var voteWaiting = ParseVote("""{"status":200,"nick":"fulano","dados":{"ativo":true,"esperaMs":7500000,"url":"https://idlehub.io/votar/pokeidle-io?jogador=fulano","identificador":"fulano","votos":7,"noPar":1,"porPar":1,"votosPorDiamante":2}}""");
            var voteFree = ParseVote("""{"status":200,"nick":"fulano","dados":{"ativo":true,"esperaMs":0,"url":"https://outro.com/x","identificador":"beltrano","votos":0,"noPar":0,"porPar":1,"votosPorDiamante":2}}""");
            Check("estado do voto lido do jogo (/idlehub/me)",
                voteWaiting is { LoggedIn: true, Active: true, Ready: false, Votes: 7, InPair: 1, VotesPerReward: 2, Nick: "fulano" }
                && voteWaiting.NextVoteAt > DateTime.Now.AddHours(2) && voteWaiting.Url == "https://idlehub.io/votar/pokeidle-io?jogador=fulano"
                && voteFree is { Ready: true, Url: "https://idlehub.io/votar/pokeidle-io?jogador=beltrano" }
                && ParseVote("""{"erro":"login"}""") is { LoggedIn: false } && ParseVote("""{"status":401,"dados":{"erro":"auth.precisaLogin"}}""") is { LoggedIn: false }
                && ParseVote("""{"erro":"pagina"}""") is null && ParseVote("""{"status":500,"dados":null}""") is null
                && ParseVote("""{"status":200,"dados":{"ativo":false,"esperaMs":0}}""") is { Active: false, Ready: false });
            Check("voto registrado é percebido",
                IdleHub.IsVoteResponse("POST", "https://idlehub.io/jogo/pokeidle-io/votar", 200)
                && !IdleHub.IsVoteResponse("POST", "https://idlehub.io/api/auth/sair", 200)
                && !IdleHub.IsVoteResponse("GET", "https://idlehub.io/jogo/pokeidle-io", 200)
                && !IdleHub.IsVoteResponse("POST", "https://outro.com/jogo/pokeidle-io/votar", 200)
                && IdleHub.TryParseVoted("""{"ok":true,"votosMes":10,"proximoEm":1795274052000}""", out var votedNext) && votedNext?.Year == 2026
                && !IdleHub.TryParseVoted("""{"ok":false,"erro":"x"}""", out _) && !IdleHub.TryParseVoted("<html>", out _));
            _votes[0] = new VoteState(true, true, DateTime.Now.AddHours(2).AddMinutes(5).AddSeconds(30), null, "teste", 7, 1, 1, 2);
            _votes[1] = new VoteState(true, true, null, null, "teste");
            _votes[2] = new VoteState(false, false, null, null, null);
            for (int i = 0; i < Accounts; i++)
                RenderVote(i);
            RenderVoteStatus();
            Check("botões de voto nos cabeçalhos",
                _panes[0].VoteText == "🗳 2 h 05 min" && _panes[1].VoteText == "🗳 Votar" && _panes[2].VoteText is null
                && _panes[3].VoteText == "🗳 IdleHub" && _voteStatus.Text == "🗳 Voto liberado: Conta 2",
                string.Join(" | ", _panes.Select(p => p.VoteText)) + " | " + _voteStatus.Text);
            MarkVoted(1, null);
            Check("depois de votar o botão vira 'votou' (sem erro)", _panes[1].VoteText == "🗳 ✓ Votou" && _voteStatus.Text?.StartsWith("🗳 Próximo voto em") == true,
                _panes[1].VoteText + " | " + _voteStatus.Text);
            MarkVoted(1, DateTime.Now.AddHours(23).AddMinutes(59).AddSeconds(30));
            Check("com a hora do IdleHub o 'votou' já conta o tempo", _panes[1].VoteText == "🗳 23 h 59 min", _panes[1].VoteText);
            _votedAt[1] = default;

            // O cabeçalho da conta 1 (com extensão, kick, carrinho e voto) na grade e em colunas, a mais apertada:
            // a imagem para conferir o tamanho dos botões, e o que não coube.
            var headerPicture = Path.Combine(Path.GetTempPath(), "PokeIdleDeck-cabecalho.png");
            var shots = new List<Bitmap>();
            var cut = new Dictionary<DeckLayout, string>();
            foreach (var shape in new[] { DeckLayout.Grid, DeckLayout.Columns })
            {
                SetLayout(shape);
                await Task.Delay(500);
                var header = _panes[0].Controls.OfType<ToolStrip>().First();
                var shot = new Bitmap(header.Width, header.Height);
                header.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                shots.Add(shot);
                cut[shape] = string.Join(" ", header.Items.Cast<ToolStripItem>()
                    .Where(i => i.Available && i.Placement == ToolStripItemPlacement.None).Select(i => i.Text ?? i.ToolTipText));
            }
            SetLayout(DeckLayout.Grid);
            using (var both = new Bitmap(shots.Max(b => b.Width), shots.Sum(b => b.Height) + 8))
            {
                using (var g = Graphics.FromImage(both))
                {
                    g.Clear(Color.Black);
                    g.DrawImage(shots[0], 0, 0);
                    g.DrawImage(shots[1], 0, shots[0].Height + 8);
                }
                both.Save(headerPicture);
            }
            shots.ForEach(b => b.Dispose());
            Check("cabeçalho: botões maiores, e nenhum some (em colunas sai o \"rodando há\" e o voto fica só 🗳)",
                cut[DeckLayout.Grid].Length == 0 && cut[DeckLayout.Columns].Length == 0 && _panes[0].VoteText == "🗳 2 h 05 min",
                $"cortados na grade='{cut[DeckLayout.Grid]}' em colunas='{cut[DeckLayout.Columns]}' (imagem em {headerPicture})");
            try
            {
                Check("Kick responde se a live está no ar (precisa de internet)", await KickLive.IsLiveAsync("biscoitao") is not null);
            }
            catch (Exception ex)
            {
                Check("Kick respondeu (precisa de internet)", false, ex.Message);
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

            // A Twitch de verdade responde quem está no ar (canal que não existe vem como offline).
            var twitchNow = await TwitchLive.LiveAsync(new[] { "rodil", "tftoddy", "naoexiste_deck_teste_0" });
            Check("Twitch responde se a live está no ar (precisa de internet)",
                twitchNow["rodil"] is not null && twitchNow["tftoddy"] is not null && twitchNow["naoexiste_deck_teste_0"] == false,
                string.Join(", ", twitchNow.Select(t => $"{t.Key}={t.Value?.ToString() ?? "?"}")));

            var windowed = Bounds;
            ToggleFullscreen();
            await Task.Delay(800);
            var screen = Screen.FromControl(this).Bounds;
            Check("tela cheia", Bounds == screen && FormBorderStyle == FormBorderStyle.None && Shape(2, 2), $"{Bounds} tela {screen}");
            ToggleFullscreen();
            await Task.Delay(800);
            Check("sair da tela cheia volta como estava", Bounds == windowed && FormBorderStyle == FormBorderStyle.Sizable, $"{Bounds} antes {windowed}");
            // Na barra só o total, a Kick, a Twitch e o deck (sem conta nenhuma); o jogo e o resto ficam na dica.
            Check("RAM e CPU na barra: o total, a Kick, a Twitch e o deck, sem as contas",
                _ram.Text?.StartsWith("RAM ~") == true && _ram.Text.Contains(" CPU ") && _ram.Text.Contains(" · Kick ")
                && _ram.Text.Contains(" · Twitch ") && _ram.Text.Contains(" · Deck ") && !_ram.Text.Contains("Conta")
                && _ram.ToolTipText?.Contains("jogo: ") == true, _ram.Text ?? "");
        }
        catch (Exception ex)
        {
            log.Add("ERRO " + ex);
        }
        finally
        {
            log.Add($"INFO {TestDesktop.Summary}" + (TestDesktop.Active ? $" (deck {(TestDesktop.IsOnTarget(Handle) ? "nela" : "FORA dela")})" : ""));
            File.WriteAllText(SelfTestReport, string.Join(Environment.NewLine, log));
            Close();
        }
    }
}
