using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeIdleMulti;

/// <summary>
/// As lives da Twitch e da Kick de uma conta, no mesmo perfil da conta (é onde ficam os logins da
/// Twitch/Kick). As páginas ficam empilhadas, todas visíveis para o navegador: página escondida (como
/// a aba de trás de um TabControl) não toca o vídeo, e a Kick só dá pontos com a live tocando. Por isso
/// a janela fica aberta fora da tela; "Mostrar" traz ela para perto do deck e fechar devolve para fora.
/// </summary>
internal sealed class LivesWindow : Form
{
    private const int WsExToolWindow = 0x80;

    private readonly ToolStrip _bar = new ClickThroughToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly Panel _stack = new() { Dock = DockStyle.Fill };
    private readonly List<Page> _pages = new();
    private int _kickStart;
    private bool _revealed;

    /// <summary>Uma página da janela; uma vaga da Kick sem live no ar fica em branco (<see cref="Url"/> nulo).</summary>
    private sealed class Page
    {
        public required WebView2 View { get; init; }
        public required ToolStripButton Button { get; init; }
        public string? Url { get; set; }
        public string? Kick { get; set; }
        public bool Loaded { get; set; }
    }

    private LivesWindow(string accountName)
    {
        Text = $"Lives — {accountName}";
        BackColor = Theme.Back;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        Size = new Size(1100, 760);
        Theme.Style(_bar);
        Controls.Add(_stack);
        Controls.Add(_bar);
    }

    /// <summary>Fechar de verdade (ao desligar as lives ou fechar o deck); senão o X só tira da tela.</summary>
    public bool AllowClose { get; set; }

    /// <summary>A janela está fora da tela (rodando, mas sem aparecer).</summary>
    public bool Parked => !_revealed;

    public int Count => _pages.Count(p => p.Url is not null);
    public int Loaded => _pages.Count(p => p.Url is not null && p.Loaded);

    /// <summary>Os canais da Kick abertos agora.</summary>
    public IEnumerable<string> KickChannels => _pages.Skip(_kickStart).Select(p => p.Kick).OfType<string>();

    /// <summary>As abas da Kick abertas (onde ficam o chat e o resgate dos pontos do canal).</summary>
    public IEnumerable<(int Tab, string Name)> KickTabs => _pages
        .Select((page, tab) => (Page: page, Tab: tab))
        .Where(t => t.Tab >= _kickStart && t.Page.Kick is not null)
        .Select(t => (t.Tab, "Kick · " + t.Page.Kick));

    /// <summary>
    /// Cria a janela já rodando fora da tela: uma página para cada endereço fixo (Twitch) e
    /// <paramref name="kickSlots"/> vagas da Kick, preenchidas depois por <see cref="SetKick"/>.
    /// </summary>
    public static async Task<LivesWindow> CreateAsync(CoreWebView2Environment env, string profile, string accountName,
        IEnumerable<string> urls, int kickSlots, Action<CoreWebView2>? setup)
    {
        var window = new LivesWindow(accountName);
        window.Park();
        window.Show();
        foreach (var url in urls)
            window.Navigate(await window.AddPageAsync(env, profile, setup), url, null);
        window._kickStart = window._pages.Count;
        for (int i = 0; i < kickSlots; i++)
            await window.AddPageAsync(env, profile, setup);
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
    }

    /// <summary>Traz a janela para o meio de <paramref name="area"/>, já na aba pedida.</summary>
    public void Reveal(Rectangle area, int tab = -1)
    {
        if (tab >= 0)
            Select(tab);
        else if (_pages.FirstOrDefault(p => p.Url is not null) is Page first)
            Select(_pages.IndexOf(first));
        _revealed = true;
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Location = new Point(area.X + Math.Max(0, (area.Width - Width) / 2), area.Y + Math.Max(0, (area.Height - Height) / 2));
        Activate();
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
            page.View.Dispose();
        base.OnFormClosed(e);
    }

    private void Park()
    {
        _revealed = false;
        WindowState = FormWindowState.Normal;
        var screens = SystemInformation.VirtualScreen;
        Location = new Point(screens.Right + 100, screens.Top);
    }

    private async Task<Page> AddPageAsync(CoreWebView2Environment env, string profile, Action<CoreWebView2>? setup)
    {
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Back };
        var page = new Page { View = view, Button = new ToolStripButton { Visible = false } };
        int tab = _pages.Count;
        page.Button.Click += (_, _) => Select(tab);
        _pages.Add(page);
        _bar.Items.Add(page.Button);
        _stack.Controls.Add(view);

        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = profile;
        _ = view.Handle;
        await view.EnsureCoreWebView2Async(env, options);

        var core = view.CoreWebView2;
        core.IsMuted = true;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationCompleted += (_, e) => page.Loaded = e.IsSuccess && page.Url is not null;
        core.NewWindowRequested += (_, e) => PopupWindow.HandleNewWindow(env, profile, this, e);
        // Vídeo na menor qualidade: continua contando como assistindo, gastando bem menos.
        VideoQuality.LimitToLowest(env, core);
        setup?.Invoke(core);
        return page;
    }

    private void Navigate(Page page, string? url, string? kick)
    {
        page.Url = url;
        page.Kick = kick;
        page.Loaded = false;
        page.Button.Text = url is null ? "" : TabName(url);
        page.Button.Visible = url is not null;
        page.View.CoreWebView2?.Navigate(url ?? "about:blank");
    }

    private void Select(int tab)
    {
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].Button.Checked = i == tab;
        // Só muda qual fica na frente: as de trás continuam visíveis para o navegador e tocando.
        _pages[tab].View.BringToFront();
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
