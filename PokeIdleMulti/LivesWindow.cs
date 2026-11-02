using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeIdleMulti;

/// <summary>
/// As lives da Twitch e da Kick de uma conta, no mesmo perfil da conta (é onde ficam os logins da
/// Twitch/Kick). As páginas ficam empilhadas, todas visíveis para o navegador: página escondida (como
/// a aba de trás de um TabControl) não toca o vídeo, e a Kick só dá pontos com a live tocando. Por isso
/// a janela fica aberta fora da tela; "Mostrar" traz ela para perto do deck e fechar devolve para fora.
/// Cada live da Kick toca só o player (player.kick.com): ele avisa a Kick que a conta está assistindo, que
/// é o que rende os pontos, e gasta uns 40% menos que a página do canal. O chat e o botão dos pontos ficam na
/// página do canal, que abre por cima do player só enquanto a janela está na tela, em colunas lado a lado, e
/// fecha quando ela sai.
/// </summary>
internal sealed class LivesWindow : Form
{
    private const int WsExToolWindow = 0x80;

    private readonly ToolStrip _bar = new ClickThroughToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly Panel _stack = new() { Dock = DockStyle.Fill };
    private readonly List<Page> _pages = new();
    private readonly ToolStripButton _fullPage = new("Página inteira")
    {
        Alignment = ToolStripItemAlignment.Right, CheckOnClick = true, Visible = false,
        ToolTipText = "Mostra a página toda da live (com o vídeo), se algo do chat ficar cortado",
    };
    private readonly ToolStripButton _next = new("Próxima conta ▶") { Alignment = ToolStripItemAlignment.Right, Visible = false };
    private int _kickStart;
    private int _selected;
    private int _order;
    private int _fitHeight;
    private bool _revealed;
    private CoreWebView2Environment _env = null!;
    private string _profile = "";
    private Action<CoreWebView2>? _setup;
    private Func<string, string> _chatUrl = channel => channel;

    // A página do canal da Kick é desenhada na largura de computador, que põe o chat numa lateral de 340 px à
    // direita com o botão dos pontos embaixo (na largura de celular esse botão some). Na coluna de cada live
    // aparece só essa lateral; o resto da página, com o vídeo, fica à esquerda, fora da vista.
    private const int PageWidth = 1100;
    private const int ChatWidth = 340;

    /// <summary>Uma página da janela; uma vaga da Kick sem live no ar fica em branco (<see cref="Url"/> nulo).</summary>
    private sealed class Page
    {
        public required WebView2 View { get; init; }

        /// <summary>O pedaço da janela onde a página aparece (a da Kick passa dele para a esquerda).</summary>
        public required Panel Frame { get; init; }

        public required ToolStripButton Button { get; init; }
        public bool IsKick { get; init; }
        public string? Url { get; set; }
        public string? Kick { get; set; }
        public bool Loaded { get; set; }

        /// <summary>Onde o vídeo estava na última conferência, e quantas seguidas ele não andou.</summary>
        public double? VideoTime { get; set; }
        public int Stalls { get; set; }
        public bool? Playing { get; set; }
        public int Reloads { get; set; }

        /// <summary>Recarregadas seguidas sem o vídeo voltar a andar (cada uma espera o dobro para a próxima).</summary>
        public int Retries { get; set; }

        /// <summary>A página do canal por cima do player (o chat e os pontos), só com a janela na tela.</summary>
        public WebView2? Chat { get; set; }
        public string? ChatChannel { get; set; }
        public bool ChatLoaded { get; set; }
    }

    // Manda tocar se estiver pausado e diz onde o vídeo está ("sem" quando a página não tem vídeo).
    private const string PlayScript =
        "(() => { const v = document.querySelector('video'); if (!v) return 'sem'; " +
        "if (v.paused) v.play().catch(() => {}); return [v.paused, v.readyState, v.currentTime].join(' '); })()";

    // Página travada não responde ao script; passado isso, conta como parada.
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// O que aparece do chat na página do canal. Nas colunas dos pontos, só o rodapé (o botão dos pontos e o
    /// popup de escolher o bônus), sem as mensagens. Na página inteira, tudo. (Fora da tela a página do canal
    /// nem fica aberta.)
    /// </summary>
    private enum ChatView { Hidden, Points, Full }

    private ChatView _chatApplied;

    private static string ChatScript(ChatView view)
    {
        string css = view switch
        {
            ChatView.Hidden => "#channel-chatroom, #chatroom { display: none !important; }",
            ChatView.Points => "#chatroom-messages, [data-testid=\"pinned-message-modal\"], [aria-label=\"Expand leaderboard\"] " +
                               "{ visibility: hidden !important; }",
            _ => "",
        };
        return "(() => { let s = document.getElementById('deck-chat'); if (!s) { s = document.createElement('style'); " +
               $"s.id = 'deck-chat'; (document.head || document.documentElement).appendChild(s); }} s.textContent = {JsonSerializer.Serialize(css)}; return 1; }})()";
    }

    private LivesWindow(string accountName)
    {
        Text = $"Lives — {accountName}";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        Size = new Size(1100, 760);
        Theme.Style(_bar);
        _bar.Items.Add(_next);
        _bar.Items.Add(_fullPage);
        _fullPage.CheckedChanged += (_, _) =>
        {
            Arrange();
            Fit();
        };
        _next.Click += (_, _) => NextRequested?.Invoke();
        _stack.Resize += (_, _) => Arrange();
        Controls.Add(_stack);
        Controls.Add(_bar);
    }

    /// <summary>Pediu para ir à conta seguinte (o deck tira esta da tela e mostra a próxima).</summary>
    public event Action? NextRequested;

    /// <summary>Mostra o botão "Próxima conta" (há mais de uma conta com live da Kick).</summary>
    public bool HasNext
    {
        get => _next.Visible;
        set => _next.Visible = value;
    }

    /// <summary>Altura da janela dos pontos, em pixels a 100% de escala (a tela baixa limita); muda arrastando a borda.</summary>
    public int PointsHeight { get; set; } = 640;

    /// <summary>A altura dos pontos mudou na mão (o deck guarda e usa nas outras contas).</summary>
    public event Action<int>? PointsHeightChanged;

    /// <summary>Fechar de verdade (ao desligar as lives ou fechar o deck); senão o X só tira da tela.</summary>
    public bool AllowClose { get; set; }

    /// <summary>A janela está fora da tela (rodando, mas sem aparecer).</summary>
    public bool Parked => !_revealed;

    public int Count => _pages.Count(p => p.Url is not null);
    public int Loaded => _pages.Count(p => p.Url is not null && p.Loaded);

    /// <summary>Os canais da Kick abertos agora.</summary>
    public IEnumerable<string> KickChannels => _pages.Skip(_kickStart).Select(p => p.Kick).OfType<string>();

    /// <summary>Cada live da Kick aberta e se o vídeo dela está tocando (null: ainda não conferiu).</summary>
    public IEnumerable<(string Channel, bool? Playing, int Reloads)> KickPlayback => _pages.Skip(_kickStart)
        .Where(p => p.Kick is not null)
        .Select(p => (p.Kick!, p.Playing, p.Reloads));

    /// <summary>
    /// Confere se o vídeo de cada live da Kick está andando, que é o que rende os pontos. Pausado: manda
    /// tocar. Parado em duas conferências seguidas (travou, a live caiu e voltou, a página não carregou por
    /// falta de internet, ou ficou presa na checagem do Cloudflare, que ainda vai comendo memória):
    /// recarrega só aquela página. Se continuar sem voltar, espera o dobro antes de cada nova tentativa
    /// (1, 2 e depois 4 min).
    /// </summary>
    public async Task WatchKickAsync()
    {
        foreach (var page in _pages.Skip(_kickStart).Where(p => p.Kick is not null).ToList())
        {
            // A conta pode ter sido desligada (e a janela fechada) no meio da conferência.
            if (IsDisposed)
                return;
            // "pausado prontidão tempo": andando = não pausado e o tempo passou desde a última conferência.
            var parts = (page.Loaded ? await PlaybackAsync(page) : "carregando").Split(' ');
            bool moving = false;
            if (parts.Length == 3 && parts[0] == "false"
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double time))
            {
                moving = page.VideoTime is not double last || time > last;
                page.VideoTime = time;
            }
            if (moving)
            {
                page.Stalls = 0;
                page.Retries = 0;
                page.Playing = true;
                continue;
            }
            page.Playing = false;
            if (++page.Stalls >= 2 << Math.Min(page.Retries, 2))
            {
                page.Reloads++;
                page.Retries++;
                Navigate(page, page.Url, page.Kick);
            }
        }
    }

    private static async Task<string> PlaybackAsync(Page page)
    {
        try
        {
            var script = page.View.ExecuteScriptAsync(PlayScript);
            if (await Task.WhenAny(script, Task.Delay(ScriptTimeout)) != script)
                return "travada";
            return JsonSerializer.Deserialize<string>(await script) ?? "sem";
        }
        catch
        {
            return "sem";
        }
    }

    /// <summary>
    /// Roda no player de cada live da Kick aberta e carregada o script que <paramref name="script"/> monta para o
    /// canal dela, esperando a promessa dele acabar (o resgate dos pontos espera a Kick), e devolve o texto que ele
    /// entrega, por canal ("" quando a página não respondeu).
    /// </summary>
    public async Task<List<(string Channel, string Result)>> EvalKickAsync(Func<string, string> script)
    {
        var results = new List<(string, string)>();
        foreach (var page in _pages.Skip(_kickStart).Where(p => p.Kick is not null && p.Loaded).ToList())
        {
            if (IsDisposed || page.Kick is not string channel || page.View.CoreWebView2 is not CoreWebView2 core)
                continue;
            string result = "";
            try
            {
                var call = core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    JsonSerializer.Serialize(new { expression = script(channel), awaitPromise = true, returnByValue = true }));
                if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))) == call)
                {
                    using var doc = JsonDocument.Parse(await call);
                    if (doc.RootElement.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var text)
                        && text.ValueKind == JsonValueKind.String)
                        result = text.GetString()!;
                }
            }
            catch
            {
                // A página recarregou ou fechou no meio: fica para a próxima conferência.
            }
            results.Add((channel, result));
        }
        return results;
    }

    /// <summary>As abas da Kick abertas (onde ficam o chat e o resgate dos pontos do canal).</summary>
    public IEnumerable<(int Tab, string Name)> KickTabs => _pages
        .Select((page, tab) => (Page: page, Tab: tab))
        .Where(t => t.Tab >= _kickStart && t.Page.Kick is not null)
        .Select(t => (t.Tab, "Kick · " + t.Page.Kick));

    /// <summary>
    /// Cria a janela já rodando fora da tela: uma página para cada endereço fixo (Twitch) e
    /// <paramref name="kickSlots"/> vagas da Kick, preenchidas depois por <see cref="SetKick"/> com o player.
    /// <paramref name="kickChatUrl"/> dá a página do canal (a do chat) de cada canal.
    /// </summary>
    public static async Task<LivesWindow> CreateAsync(CoreWebView2Environment env, string profile, string accountName,
        IEnumerable<string> urls, int kickSlots, Func<string, string> kickChatUrl, Action<CoreWebView2>? setup)
    {
        var window = new LivesWindow(accountName) { _env = env, _profile = profile, _setup = setup, _chatUrl = kickChatUrl };
        window.Park();
        window.Show();
        foreach (var url in urls)
            window.Navigate(await window.AddPageAsync(kick: false), url, null);
        window._kickStart = window._pages.Count;
        for (int i = 0; i < kickSlots; i++)
            await window.AddPageAsync(kick: true);
        if (window._pages.Count > 0)
            window.Select(0);
        return window;
    }

    /// <summary>
    /// Abre nas vagas da Kick só estas lives (as que estão no ar); vaga que sobra fica em branco, sem gastar.
    /// Uma live que continua no ar fica na vaga dela, sem recarregar.
    /// </summary>
    public void SetKick(IReadOnlyList<(string Channel, string Url)> lives)
    {
        var slots = _pages.Skip(_kickStart).ToList();
        var wanted = lives.Take(slots.Count).ToList();
        foreach (var slot in slots.Where(s => s.Kick is not null && !wanted.Any(w => w.Channel == s.Kick)))
            Navigate(slot, null, null);
        foreach (var (channel, url) in wanted.Where(w => !slots.Any(s => s.Kick == w.Channel)))
            Navigate(slots.First(s => s.Kick is null), url, channel);
        Arrange();
        Fit();
    }

    /// <summary>
    /// Traz a janela para <paramref name="area"/>, da altura dela e já na aba pedida (numa da Kick: as
    /// colunas de chat). <paramref name="order"/> desloca cada conta um pouco, quando várias aparecem juntas.
    /// </summary>
    public void Reveal(Rectangle area, int tab = -1, int order = 0)
    {
        _revealed = true;
        _order = order;
        _fullPage.Checked = false;
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        // Primeiro para a tela certa (lá a escala pode ser outra), depois do tamanho dela.
        Location = area.Location;
        Select(tab >= 0 ? tab : Math.Max(0, _pages.FindIndex(p => p.Url is not null)));
        Activate();
    }

    /// <summary>Tira da tela (os players continuam tocando; as páginas do chat fecham).</summary>
    public void Park()
    {
        _revealed = false;
        foreach (var page in _pages)
            CloseChat(page);
        WindowState = FormWindowState.Normal;
        var screens = SystemInformation.VirtualScreen;
        Location = new Point(screens.Right + 100, screens.Top);
    }

    /// <summary>Onde está cada coluna da Kick na janela, e a página que aparece nela (autoteste).</summary>
    public IEnumerable<(Rectangle Frame, Rectangle View)> KickLayout => _pages
        .Where(p => p.IsKick && p.Url is not null)
        .Select(p => (p.Frame.Bounds, (p.Chat ?? p.View).Bounds));

    /// <summary>Páginas do chat abertas agora, e quantas já carregaram (autoteste).</summary>
    public int ChatsOpen => _pages.Count(p => p.Chat is not null);
    public int ChatsLoaded => _pages.Count(p => p.Chat is not null && p.ChatLoaded);

    /// <summary>Roda um script em cada página do chat aberta (autoteste).</summary>
    public async Task<List<string>> EvalChatsAsync(string script)
    {
        var results = new List<string>();
        foreach (var page in _pages.Where(p => p.Chat?.CoreWebView2 is not null))
            results.Add(await page.Chat!.ExecuteScriptAsync(script));
        return results;
    }

    /// <summary>O Windows às vezes traz de volta para a tela uma janela que ficou fora dela (ao mudar de monitor).</summary>
    public void KeepParked()
    {
        if (!_revealed && (WindowState != FormWindowState.Normal || Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(Bounds))))
            Park();
    }

    /// <summary>Roda um script em cada página aberta (o autoteste confere se elas estão visíveis).</summary>
    public async Task<List<string>> EvalAsync(string script)
    {
        var results = new List<string>();
        foreach (var page in _pages.Where(p => p.Url is not null))
            results.Add(await page.View.ExecuteScriptAsync(script));
        return results;
    }

    // Fora da Alt+Tab e da barra de tarefas: é uma janela de trabalho do deck.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;
            return cp;
        }
    }

    // Abrir (fora da tela) não pode roubar o foco do deck.
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Park();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (var page in _pages)
        {
            page.Chat?.Dispose();
            page.View.Dispose();
        }
        base.OnFormClosed(e);
    }

    private async Task<Page> AddPageAsync(bool kick)
    {
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Back };
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back };
        frame.Controls.Add(view);
        var page = new Page { View = view, Frame = frame, IsKick = kick, Button = new ToolStripButton { Visible = false } };
        int tab = _pages.Count;
        page.Button.Click += (_, _) => Select(tab);
        if (kick)
            frame.Resize += (_, _) => PlaceChat(page);
        _pages.Add(page);
        _bar.Items.Insert(_bar.Items.IndexOf(_next), page.Button);
        _stack.Controls.Add(frame);

        var core = await StartAsync(view);
        core.NavigationCompleted += (_, e) => page.Loaded = e.IsSuccess && page.Url is not null;
        // A página morreu (no notebook, por falta de memória): abre de novo na hora, sem esperar a conferência.
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited && page.Url is not null && !IsDisposed)
            {
                page.Reloads++;
                Navigate(page, page.Url, page.Kick);
            }
        };
        return page;
    }

    /// <summary>Liga uma página no perfil da conta, mudo, com o vídeo na menor qualidade e sem propaganda.</summary>
    private async Task<CoreWebView2> StartAsync(WebView2 view)
    {
        var options = _env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = _profile;
        _ = view.Handle;
        await view.EnsureCoreWebView2Async(_env, options);

        var core = view.CoreWebView2;
        core.IsMuted = true;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, e) => PopupWindow.HandleNewWindow(_env, _profile, this, e);
        // Vídeo na menor qualidade: continua contando como assistindo, gastando bem menos.
        VideoQuality.LimitToLowest(_env, core);
        AdBlock.Apply(_env, core);
        // Ninguém fica olhando as lives: com o "reduzir movimento" do sistema, a Kick corta as animações dela.
        await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia",
            "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]}");
        _setup?.Invoke(core);
        return core;
    }

    /// <summary>Abre por cima do player de cada live da Kick a página do canal (o chat e o botão dos pontos).</summary>
    private void ShowChats()
    {
        foreach (var page in OpenKick.Where(p => p.Chat is null))
            _ = OpenChatAsync(page);
    }

    private async Task OpenChatAsync(Page page)
    {
        var chat = new WebView2 { DefaultBackgroundColor = Theme.Back };
        page.Chat = chat;
        page.ChatLoaded = false;
        page.Frame.Controls.Add(chat);
        chat.BringToFront();
        PlaceChat(page);
        CoreWebView2 core;
        try
        {
            core = await StartAsync(chat);
        }
        catch
        {
            // A janela saiu da tela enquanto a página abria (ou o navegador falhou): fica sem, e a próxima
            // vez que a janela aparecer tenta de novo.
            if (page.Chat == chat)
                CloseChat(page);
            return;
        }
        if (page.Chat != chat)
            return;
        core.NavigationCompleted += (_, e) => page.ChatLoaded = page.Chat == chat && e.IsSuccess;
        core.DOMContentLoaded += (_, _) => _ = core.ExecuteScriptAsync(ChatScript(CurrentChat));
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited && page.Chat == chat && !IsDisposed)
                NavigateChat(page);
        };
        NavigateChat(page);
    }

    private void NavigateChat(Page page)
    {
        page.ChatChannel = page.Kick;
        page.ChatLoaded = false;
        page.Chat?.CoreWebView2?.Navigate(page.Kick is null ? "about:blank" : _chatUrl(page.Kick));
    }

    private static void CloseChat(Page page)
    {
        var chat = page.Chat;
        page.Chat = null;
        page.ChatChannel = null;
        page.ChatLoaded = false;
        chat?.Dispose();
    }

    private void Navigate(Page page, string? url, string? kick)
    {
        // Outra live na vaga começa a conferência do zero (recarregar a mesma mantém a contagem).
        if (kick != page.Kick)
            page.Reloads = page.Retries = 0;
        page.Url = url;
        page.Kick = kick;
        page.Loaded = false;
        page.VideoTime = null;
        page.Stalls = 0;
        page.Playing = null;
        page.Button.Text = url is null ? "" : TabName(url);
        page.Button.Visible = url is not null;
        page.View.CoreWebView2?.Navigate(url ?? "about:blank");
        // A página do chat segue a vaga: fecha se ela ficou em branco, troca se outra live entrou.
        if (page.Chat is not null && kick is null)
            CloseChat(page);
        else if (page.Chat is not null && page.ChatChannel != kick)
            NavigateChat(page);
    }

    private ChatView CurrentChat => !_revealed ? ChatView.Hidden : Columns ? ChatView.Points : ChatView.Full;

    private void ApplyChat()
    {
        _chatApplied = CurrentChat;
        foreach (var page in _pages.Where(p => p.Chat?.CoreWebView2 is not null && p.ChatLoaded))
            _ = page.Chat!.ExecuteScriptAsync(ChatScript(_chatApplied));
    }

    private void Select(int tab)
    {
        _selected = tab;
        Arrange();
        Fit();
    }

    private List<Page> OpenKick => _pages.Where(p => p.IsKick && p.Url is not null).ToList();

    // Numa aba da Kick, as lives dela lado a lado (a não ser com "Página inteira").
    private bool Columns => !_fullPage.Checked && _selected < _pages.Count && _pages[_selected].IsKick && OpenKick.Count > 0;

    /// <summary>
    /// Quem fica na frente, e as colunas da Kick lado a lado. Só muda a ordem e o tamanho: as páginas de
    /// trás continuam visíveis para o navegador e tocando.
    /// </summary>
    private void Arrange()
    {
        if (_pages.Count == 0)
            return;
        var kick = OpenKick;
        // A aba escolhida pode ter ficado em branco (a live saiu do ar): vai para outra aberta.
        if (_selected >= _pages.Count || _pages[_selected].Url is null)
            _selected = Math.Max(0, kick.Count > 0 ? _pages.IndexOf(kick[0]) : _pages.FindIndex(p => p.Url is not null));
        bool columns = Columns;
        _stack.SuspendLayout();
        foreach (var page in _pages)
            page.Frame.Dock = DockStyle.Fill;
        if (columns)
        {
            // A última coluna fica com o resto da largura (com uma live só, mostra um pouco do vídeo também).
            for (int i = 0; i < kick.Count; i++)
            {
                if (i < kick.Count - 1)
                {
                    kick[i].Frame.Dock = DockStyle.Left;
                    kick[i].Frame.Width = LogicalToDeviceUnits(ChatWidth);
                }
                kick[i].Frame.BringToFront();
            }
        }
        else
        {
            _pages[_selected].Frame.BringToFront();
        }
        _stack.ResumeLayout(true);
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].Button.Checked = columns ? kick.Contains(_pages[i]) : i == _selected;
        _fullPage.Visible = kick.Count > 0;
        // Na tela, numa aba da Kick: abre as páginas do chat que faltam.
        if (_revealed && (columns || _pages[_selected].IsKick))
            ShowChats();
        if (CurrentChat != _chatApplied)
            ApplyChat();
    }

    /// <summary>
    /// Na tela: nas colunas dos pontos, da largura delas e da altura escolhida (a tela baixa limita); na
    /// página inteira, da altura da área de trabalho.
    /// </summary>
    private void Fit()
    {
        if (!_revealed)
            return;
        var area = Screen.FromControl(this).WorkingArea;
        bool columns = Columns;
        int content = columns ? Math.Max(2, OpenKick.Count) * LogicalToDeviceUnits(ChatWidth) : LogicalToDeviceUnits(PageWidth);
        int width = Math.Min(area.Width, content + Width - _stack.ClientSize.Width);
        int height = columns ? Math.Min(area.Height, LogicalToDeviceUnits(PointsHeight)) : area.Height;
        int x = area.X + (area.Width - width) / 2 + _order * LogicalToDeviceUnits(40);
        Bounds = new Rectangle(Math.Clamp(x, area.X, area.Right - width), area.Y + (area.Height - height) / 2, width, height);
        _fitHeight = height;
    }

    // Arrastou a borda das colunas dos pontos: essa passa a ser a altura delas.
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!_revealed || !Columns || Height == _fitHeight)
            return;
        _fitHeight = Height;
        PointsHeight = (int)Math.Round(Height * 96.0 / DeviceDpi);
        PointsHeightChanged?.Invoke(PointsHeight);
    }

    /// <summary>A página do canal na largura de computador, encostada à direita da coluna: o chat aparece inteiro.</summary>
    private void PlaceChat(Page page)
    {
        if (page.Chat is null)
            return;
        var size = page.Frame.ClientSize;
        int width = Math.Max(size.Width, LogicalToDeviceUnits(PageWidth));
        page.Chat.Bounds = new Rectangle(size.Width - width, 0, width, size.Height);
    }

    private static string TabName(string url)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // twitch.tv/popout/<canal>/chat e kick.com/<canal>
        var channel = segments.Length > 1 && segments[0] == "popout" ? segments[1] : segments.FirstOrDefault() ?? uri.Host;
        var site = uri.Host.Contains("twitch") ? "Twitch" : uri.Host.Contains("kick") ? "Kick" : uri.Host;
        return $"{site} · {channel}";
    }
}
