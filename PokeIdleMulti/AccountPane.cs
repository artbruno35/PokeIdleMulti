using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeIdleMulti;

internal enum PaneStatus { Starting, Loading, Ok, Failed }

/// <summary>
/// Uma conta dentro do deck: um cabeçalho fino (status, tempo rodando, extensões, botões) e a página
/// do jogo ocupando o resto, sem barra de navegador. Cada conta tem um perfil próprio do WebView2,
/// com cookies, login e extensões separados.
/// </summary>
internal sealed class AccountPane : UserControl
{
    // F11 dentro da página não chega na janela do deck; o script avisa por mensagem.
    private const string KeyScript =
        "addEventListener('keydown', e => { if (e.key === 'F11') { e.preventDefault(); chrome.webview.postMessage('F11'); } }, true);";

    private readonly ToolStrip _header = new ClickThroughToolStrip();
    private readonly ToolStripLabel _dot = new("●");
    private readonly ToolStripLabel _name = new();
    private readonly ToolStripLabel _info = new();
    private readonly ToolStripButton _focus = new("⛶");
    private readonly ToolStripButton _reload = new("⟳");
    private readonly ToolStripButton _mute = new("🔊");
    private readonly ToolStripButton _vote = new("🗳 Votar") { Visible = false };
    private readonly ToolStripDropDownButton _kick = new("kick") { Enabled = false };
    private readonly ToolStripButton _power = new(Theme.PowerGlyph) { Font = Theme.Symbols };
    private readonly Label _offNote = new()
    {
        Dock = DockStyle.Fill,
        Visible = false,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Theme.Back,
        ForeColor = Theme.Muted,
    };
    private readonly Font _voteBold;
    private readonly List<ToolStripItem> _extensionItems = new();
    private PaneStatus _status = PaneStatus.Starting;
    private string? _statusText = "abrindo…";
    private string? _note;
    private DateTime _noteUntil;
    private bool _kickClosed;
    private bool _loginScreen;

    // A tela de login do jogo está à vista (a mesma conta que o jogo faz para desenhar o captcha).
    private const string LoginScript =
        "(() => { const p = document.querySelector('#portal'), l = document.querySelector('#login');" +
        " return !!p && !!l && !p.classList.contains('hidden') && !l.classList.contains('hidden'); })()";

    public AccountPane(int index)
    {
        Index = index;
        BackColor = Theme.Border;
        Padding = new Padding(1);

        Theme.Style(_header);
        _header.Dock = DockStyle.Top;
        _header.Padding = new Padding(6, 1, 4, 1);
        _header.CanOverflow = false;
        _dot.ForeColor = Theme.Muted;
        _name.Font = new Font(_header.Font, FontStyle.Bold);
        _info.ForeColor = Theme.Muted;

        // Itens à direita entram da direita para a esquerda.
        _power.Alignment = _focus.Alignment = _reload.Alignment = _mute.Alignment = _kick.Alignment = _vote.Alignment = ToolStripItemAlignment.Right;
        _power.ToolTipText = "Desligar esta conta: o jogo, as extensões, as lives e o voto dela param (ela continua aqui no deck)";
        _power.Click += (_, _) => PowerRequested?.Invoke(this);
        _focus.ToolTipText = "Ampliar esta conta / voltar (duplo clique no cabeçalho faz o mesmo)";
        _reload.ToolTipText = "Recarregar a página";
        _mute.ToolTipText = "Ligar / desligar o som desta conta";
        _voteBold = new Font(_header.Font, FontStyle.Bold);
        _focus.Click += (_, _) => FocusRequested?.Invoke(this);
        _reload.Click += (_, _) => ReloadRequested?.Invoke(this);
        _mute.Click += (_, _) => SetMuted(!Muted);
        _vote.Click += (_, _) => VoteRequested?.Invoke(this);
        _kick.DropDownOpening += (_, _) => KickMenuOpening?.Invoke(this, _kick.DropDownItems);
        _kick.DropDownItems.Add("…");
        _header.DoubleClick += (_, _) => FocusRequested?.Invoke(this);
        _header.Items.AddRange(new ToolStripItem[] { _dot, _name, _info, _power, _focus, _reload, _mute, _kick, _vote });

        _offNote.Font = new Font(Font.FontFamily, 11f);
        _offNote.DoubleClick += (_, _) => FocusRequested?.Invoke(this);
        Controls.Add(WebView);
        Controls.Add(_offNote);
        Controls.Add(_header);
    }

    public event Action<AccountPane>? FocusRequested;
    public event Action<AccountPane>? ReloadRequested;
    public event Action<AccountPane>? FullscreenRequested;
    public event Action<AccountPane>? SettingsChanged;
    public event Action<AccountPane, ExtensionInfo, Point>? PopupRequested;
    public event Action<AccountPane>? VoteRequested;

    /// <summary>Um clique de verdade na página (para o eco repetir nas outras contas).</summary>
    public event Action<AccountPane, Echo.Click>? Clicked;

    /// <summary>O botão ⏻: ligar ou desligar a conta.</summary>
    public event Action<AccountPane>? PowerRequested;

    /// <summary>O menu do botão kick abrindo: o deck põe nele as lives da Kick para fechar ou abrir de novo.</summary>
    public event Action<AccountPane, ToolStripItemCollection>? KickMenuOpening;

    /// <summary>Desligada: fica no deck, mas sem o jogo (nem as extensões, que só rodam nele), lives ou voto.</summary>
    public bool Off { get; private set; }

    /// <summary>A página do jogo abriu um link do TopIdle (o "votar" de dentro do jogo).</summary>
    public event Action<AccountPane, string>? TopIdleLinkRequested;

    public int Index { get; }
    public string ProfileName => $"conta{Index + 1}";
    public string AccountName { get; set; } = "";
    public WebView2 WebView { get; } = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Back };
    public CoreWebView2? Core => WebView.CoreWebView2;
    public CoreWebView2Profile? Profile => WebView.CoreWebView2?.Profile;
    public PaneStatus Status => _status;
    public DateTime? StartedAt { get; private set; }
    public bool Muted => Core?.IsMuted ?? false;
    public int ExtensionButtonCount => _extensionItems.Count;
    public string? VoteText => _vote.Visible ? _vote.Text : null;

    /// <summary>Cria o navegador da conta e abre o jogo; <paramref name="prepare"/> roda antes de abrir (o conserto das extensões).</summary>
    public async Task InitializeAsync(CoreWebView2Environment env, string url, double zoom, bool muted, Action<CoreWebView2>? setup,
        Func<CoreWebView2, Task>? prepare = null)
    {
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        _ = WebView.Handle;
        await WebView.EnsureCoreWebView2Async(env, options);

        var core = WebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.IsMuted = muted;
        UpdateMuteButton();
        WebView.ZoomFactor = zoom;
        WebView.ZoomFactorChanged += (_, _) => SettingsChanged?.Invoke(this);

        core.NavigationStarting += (_, e) =>
        {
            if (e.IsRedirected || Off)
                return;
            SetStatus(PaneStatus.Loading, "carregando…");
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (Off)
                return;
            if (e.IsSuccess)
            {
                StartedAt ??= DateTime.Now;
                SetStatus(PaneStatus.Ok, null);
            }
            else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                SetStatus(PaneStatus.Failed, "não carregou: sem internet ou o site caiu");
            }
        };
        core.ProcessFailed += (_, e) => OnProcessFailed(e);
        core.NewWindowRequested += (_, e) =>
        {
            if (TopIdleLinkRequested is not null && TopIdle.IsTopIdleLink(e.Uri))
            {
                // O voto de dentro do jogo abre na janela de voto do deck, que percebe quando o voto entra.
                e.Handled = true;
                TopIdleLinkRequested(this, e.Uri);
                return;
            }
            PopupWindow.HandleNewWindow(env, ProfileName, FindForm(), e);
        };
        core.WebMessageReceived += (_, e) =>
        {
            string? message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }
            if (message == "F11")
                FullscreenRequested?.Invoke(this);
            else if (message is not null && Echo.Parse(message) is Echo.Click click)
                Clicked?.Invoke(this, click);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Echo.CaptureScript);
        await ApplyReduceMotionAsync();

        setup?.Invoke(core);
        if (prepare is not null)
            await prepare(core);
        core.Navigate(url);
    }

    public void Reload()
    {
        StartedAt = null;
        Core?.Reload();
    }

    /// <summary>Menos animações (lido ao criar o navegador da conta; mudou depois, chame <see cref="ApplyReduceMotionAsync"/>).</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// A página passa a ver o "reduzir movimento" do sistema ligado (ou desligado), e o próprio jogo
    /// desliga ~100 grupos de animação que rodam sem parar: selos pulsando, halos, cartas, o portal do
    /// login. Medido na tela de login: metade da CPU da página e da placa de vídeo. Vale até fechar o deck,
    /// inclusive depois de recarregar.
    /// </summary>
    public async Task ApplyReduceMotionAsync()
    {
        if (Core is null)
            return;
        var features = ReduceMotion ? "[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]" : "[]";
        try
        {
            await Core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", $"{{\"features\":{features}}}");
        }
        catch
        {
            // A página está fechando: na próxima abertura ela volta a ler a escolha.
        }
    }

    // Modo ULTRA Economia do jogo ("ULTRA Battery Saver"): a escolha fica no localStorage ("cfg-ultra"),
    // e o jogo acende a classe "modo-ultra" no <html> quando entra com ela ligada.
    // ligado: na tela; escolhido: liga quando entrar no jogo (ou quando a conta tiver equipe); normal; fora.
    private const string UltraStateScript =
        "(() => document.documentElement.classList.contains('modo-ultra') ? 'ligado'" +
        " : localStorage.getItem('cfg-ultra') === '1' ? 'escolhido'" +
        " : document.querySelector('#app:not(.hidden)') ? 'normal' : 'fora')()";

    // Liga pelo mesmo caminho do jogador (Configurações → interruptor do ULTRA, que fecha a janela sozinho)
    // e desliga pelo "Voltar ao jogo" da caixa. Fora do jogo, ou com a conta ainda sem equipe, só fica a
    // escolha: o próprio jogo liga ao entrar.
    private const string SetUltraScript =
        "(on => { const ultra = () => document.documentElement.classList.contains('modo-ultra');" +
        " if (!on) { document.querySelector('#ultra-sair')?.click(); localStorage.setItem('cfg-ultra', '0'); return !ultra(); }" +
        " if (ultra()) return true;" +
        " const app = document.querySelector('#app');" +
        " if (!app || app.classList.contains('hidden') || localStorage.getItem('cfg-ultra') === '1') { localStorage.setItem('cfg-ultra', '1'); return true; }" +
        " document.querySelector('#btn-config')?.click(); document.querySelector('#cfg-ultra')?.click(); return ultra(); })";

    /// <summary>A página é o jogo (deck.test: o jogo do autoteste).</summary>
    private bool OnGame => Core is not null && Uri.TryCreate(Core.Source, UriKind.Absolute, out var uri)
        && uri.Host is "pokeidle.io" or "deck.test";

    /// <summary>"ligado", "escolhido", "normal" ou "fora"; null se a página não é o jogo ou não respondeu.</summary>
    public async Task<string?> GetUltraAsync()
    {
        if (Off || !OnGame)
            return null;
        try
        {
            return JsonSerializer.Deserialize<string>(await WebView.ExecuteScriptAsync(UltraStateScript));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Liga ou desliga o Modo ULTRA Economia do jogo nesta conta, na hora (sem recarregar).</summary>
    public async Task<bool> SetUltraAsync(bool on)
    {
        if (Off || !OnGame)
            return false;
        try
        {
            return await WebView.ExecuteScriptAsync($"{SetUltraScript}({(on ? "true" : "false")})") == "true";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Confere se a conta está parada na tela de login. Ali o jogo mantém o captcha do Cloudflare, que
    /// refaz o desafio sozinho e vai juntando memória (medido: +60 MB a cada 30 s no começo).
    /// </summary>
    public async Task CheckLoginScreenAsync()
    {
        bool login = false;
        if (Core is not null && !Off && _status == PaneStatus.Ok)
        {
            try
            {
                login = await WebView.ExecuteScriptAsync(LoginScript) == "true";
            }
            catch
            {
                return;
            }
        }
        if (login == _loginScreen)
            return;
        _loginScreen = login;
        _info.ToolTipText = login
            ? "Na tela de login o jogo mantém o captcha do Cloudflare, que refaz o desafio sozinho e vai juntando " +
              "memória (centenas de MB em poucas horas). Entre na conta ou desligue-a no botão de desligar, no canto direito do cabeçalho."
            : null;
        RefreshHeader();
    }

    /// <summary>O texto ao lado do nome no cabeçalho, para o autoteste.</summary>
    public string HeaderInfo => _info.Text ?? "";

    public void SetMuted(bool muted)
    {
        if (Core is null)
            return;
        Core.IsMuted = muted;
        UpdateMuteButton();
        SettingsChanged?.Invoke(this);
    }

    /// <summary>O nick salvo pelo jogo nesta conta, para a recompensa do voto ir para o personagem certo.</summary>
    public async Task<string?> ReadNickAsync()
    {
        if (Core is null || !Core.Source.StartsWith("https://pokeidle.io", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return TopIdle.ParseNick(await WebView.ExecuteScriptAsync("localStorage.getItem('nick')"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Botão de voto do cabeçalho: laranja quando o voto está liberado, com o tempo que falta quando não.</summary>
    public void SetVote(string text, Color color, bool highlight, string tooltip)
    {
        if (Off)
        {
            HideVote();
            return;
        }
        _vote.Text = text;
        _vote.ForeColor = color;
        _vote.Font = highlight ? _voteBold : _header.Font;
        _vote.ToolTipText = tooltip;
        _vote.Visible = true;
    }

    public void HideVote() => _vote.Visible = false;

    /// <summary>
    /// Botão kick do cabeçalho: apagado quando não tem o que fechar (o motivo fica na dica), marcado
    /// quando tem live da Kick fechada nele.
    /// </summary>
    public void SetKick(bool enabled, bool closed, string tooltip)
    {
        _kick.Enabled = enabled;
        _kickClosed = closed;
        _kick.ForeColor = closed ? Theme.Accent : Theme.Text;
        _kick.ToolTipText = tooltip;
    }

    /// <summary>O estado do botão kick ("ligado", "apagado" ou "fechada"), para o autoteste.</summary>
    public string KickState => _kickClosed ? "fechada" : _kick.Enabled ? "ligado" : "apagado";

    public string? NoteText => DateTime.Now < _noteUntil ? _note : null;

    /// <summary>
    /// Desliga (a página do jogo vira em branco e o aviso toma o lugar dela) ou religa a conta. Religar só
    /// arruma o painel; quem abre o jogo de novo é o deck.
    /// </summary>
    public void SetOff(bool off)
    {
        Off = off;
        _offNote.Text = $"{AccountName} desligada\n\nNada roda nela: jogo, extensões, lives e voto ficam parados.\n" +
                        "Clique no botão de ligar, no canto direito do cabeçalho, para ligar.";
        _offNote.Visible = off;
        WebView.Visible = !off;
        _reload.Visible = _mute.Visible = _kick.Visible = !off;
        foreach (var item in _extensionItems)
            item.Visible = !off;
        _power.ToolTipText = off
            ? "Ligar esta conta: o jogo abre de novo, com as extensões e as lives dela"
            : "Desligar esta conta: o jogo, as extensões, as lives e o voto dela param (ela continua aqui no deck)";
        _power.ForeColor = off ? Theme.Accent : Theme.Text;
        if (off)
        {
            HideVote();
            StartedAt = null;
            SetStatus(PaneStatus.Starting, "desligada");
            Core?.Navigate("about:blank");
        }
        else
        {
            SetStatus(PaneStatus.Starting, "ligando…");
        }
    }

    /// <summary>O texto de status do cabeçalho ("desligada", "carregando…"), para o autoteste.</summary>
    public string? StatusText => _statusText;

    /// <summary>Um aviso rápido no cabeçalho (some sozinho em alguns segundos).</summary>
    public void Note(string text)
    {
        _note = text;
        _noteUntil = DateTime.Now.AddSeconds(4);
        RefreshHeader();
    }

    /// <summary>Um botão no cabeçalho para cada extensão desta conta que tem popup.</summary>
    public void SetExtensions(IEnumerable<ExtensionInfo> extensions)
    {
        foreach (var item in _extensionItems)
        {
            _header.Items.Remove(item);
            item.Dispose();
        }
        _extensionItems.Clear();

        foreach (var extension in extensions)
        {
            var button = new ToolStripButton
            {
                Alignment = ToolStripItemAlignment.Right,
                ToolTipText = extension.Name,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Visible = !Off,
            };
            var icon = LoadIcon(extension);
            if (icon is null)
            {
                button.DisplayStyle = ToolStripItemDisplayStyle.Text;
                button.Text = "🧩";
            }
            else
            {
                button.Image = icon;
            }
            button.Click += (_, _) =>
            {
                var bounds = button.Bounds;
                PopupRequested?.Invoke(this, extension, _header.PointToScreen(new Point(bounds.Right, bounds.Bottom)));
            };
            _extensionItems.Add(button);
            _header.Items.Add(button);
        }
    }

    /// <summary>Atualiza nome, bolinha de status e o tempo rodando (chamado a cada segundo).</summary>
    public void RefreshHeader()
    {
        _name.Text = AccountName;
        _dot.ForeColor = _status switch
        {
            PaneStatus.Ok => Theme.Ok,
            PaneStatus.Loading => Theme.Warn,
            PaneStatus.Failed => Theme.Bad,
            _ => Theme.Muted,
        };
        _info.Text = DateTime.Now < _noteUntil ? "· " + _note
            : _statusText is not null ? "· " + _statusText
            : _loginScreen ? "· na tela de login: entre ou desligue a conta"
            : StartedAt is DateTime started ? "· rodando há " + FormatUptime(DateTime.Now - started)
            : "";
    }

    private void SetStatus(PaneStatus status, string? text)
    {
        _status = status;
        _statusText = text;
        RefreshHeader();
    }

    private void OnProcessFailed(CoreWebView2ProcessFailedEventArgs e)
    {
        if (Off)
            return;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                // A aba morreu de qualquer jeito; recarregar já põe a conta de volta no jogo.
                SetStatus(PaneStatus.Failed, "a página caiu, recarregando…");
                Reload();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                SetStatus(PaneStatus.Loading, "a página travou, esperando ela voltar…");
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                SetStatus(PaneStatus.Failed, "o navegador interno fechou: feche e abra o deck");
                break;
        }
    }

    private void UpdateMuteButton() => _mute.Text = Muted ? "🔇" : "🔊";

    private static string FormatUptime(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes:00} min" : $"{time.Minutes} min";

    private static Image? LoadIcon(ExtensionInfo extension)
    {
        if (extension.Icon is null)
            return null;
        try
        {
            using var stream = File.OpenRead(Path.Combine(extension.Folder, extension.Icon));
            using var image = Image.FromStream(stream);
            return new Bitmap(image, new Size(16, 16));
        }
        catch
        {
            // Ícone em SVG ou faltando: o botão fica com a peça de quebra-cabeça.
            return null;
        }
    }
}
