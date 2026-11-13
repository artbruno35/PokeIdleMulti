namespace PokeIdleMulti;

/// <summary>
/// A janela do proxy residencial: liga/desliga, endereço, porta, login e senha, com um teste que roda o
/// mesmo relé local (ProxyRelay) que o deck usa, para conferir o IP de saída, e o consumo acumulado.
/// </summary>
internal sealed class ProxyDialog : Form
{
    private readonly DeckConfig _config;
    private readonly CheckBox _on = new() { Text = "Usar proxy residencial (todas as contas e lives, um IP só)", AutoSize = true };
    private readonly TextBox _host = new() { Width = 230 };
    private readonly TextBox _port = new() { Width = 80 };
    private readonly TextBox _user = new() { Width = 230 };
    private readonly TextBox _password = new() { Width = 230, UseSystemPasswordChar = true };
    private readonly Label _total = new() { AutoSize = true, ForeColor = Theme.Text, MaximumSize = new Size(390, 0) };
    private readonly Label _result = new() { AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(390, 0) };
    private bool _resetTotal;

    public ProxyDialog(DeckConfig config)
    {
        _config = config;
        Text = "Proxy residencial";
        Icon = Theme.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;

        var proxy = config.Proxy;
        _on.Checked = proxy.Enabled;
        _host.Text = proxy.Host;
        _port.Text = proxy.Port > 0 ? proxy.Port.ToString() : "";
        _user.Text = proxy.User;
        _password.Text = proxy.Password;

        var table = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 6) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));

        table.Controls.Add(_on, 0, 0);
        table.SetColumnSpan(_on, 2);

        void Add(string label, Control control, int row)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                Anchor = AnchorStyles.Left,
                AutoSize = true,
                ForeColor = Theme.Text,
                Margin = new Padding(0, 10, 14, 0),
            }, 0, row);
            table.Controls.Add(control, 1, row);
        }
        Add("Endereço (host)", _host, 1);
        Add("Porta", _port, 2);
        Add("Login (vazio se liberado por IP)", _user, 3);
        Add("Senha", _password, 4);

        var hint = new Label
        {
            Text = "Proxy HTTP/HTTPS (não SOCKS). Proxy rotativo troca de IP a cada conexão; para o jogo e as lives, " +
                   "peça ao provedor uma sessão fixa (sticky), em geral um trecho no login, como usuario-session-abc.",
            AutoSize = true,
            ForeColor = Theme.Muted,
            MaximumSize = new Size(390, 0),
            Margin = new Padding(0, 12, 0, 0),
        };
        table.Controls.Add(hint, 0, 5);
        table.SetColumnSpan(hint, 2);

        _total.Margin = new Padding(0, 10, 0, 0);
        ShowTotal();
        table.Controls.Add(_total, 0, 6);
        table.SetColumnSpan(_total, 2);

        _result.Margin = new Padding(0, 8, 0, 0);
        table.Controls.Add(_result, 0, 7);
        table.SetColumnSpan(_result, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(14, 9, 14, 9) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Salvar", DialogResult = DialogResult.OK };
        var test = new Button { Text = "Testar" };
        var reset = new Button { Text = "Zerar contador", AutoSize = true };
        test.Click += async (_, _) => await TestAsync(test);
        save.Click += (_, _) => Apply();
        reset.Click += (_, _) =>
        {
            _resetTotal = true;
            ShowTotal();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(test);
        buttons.Controls.Add(reset);

        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
        ClientSize = new Size(440, 400);
    }

    private void ShowTotal()
    {
        long total = _resetTotal ? 0 : _config.Proxy.TotalBytes;
        _total.Text = $"Consumo acumulado pelo proxy: {total / 1024d / 1024:0.0} MB ({total / 1024d / 1024 / 1024:0.00} GB)" +
                      (_resetTotal ? " — zera ao salvar" : "");
    }

    private void Apply()
    {
        _config.Proxy.Enabled = _on.Checked;
        _config.Proxy.Host = _host.Text.Trim();
        _config.Proxy.Port = int.TryParse(_port.Text.Trim(), out var p) ? p : 0;
        _config.Proxy.User = _user.Text.Trim();
        _config.Proxy.Password = _password.Text;
        if (_resetTotal)
            _config.Proxy.TotalBytes = 0;
    }

    /// <summary>Testa o proxy pelo mesmo caminho do deck: relé local + proxy residencial (com o login, se houver).</summary>
    private async Task TestAsync(Button button)
    {
        button.Enabled = false;
        _result.ForeColor = Theme.Muted;
        _result.Text = "Testando pelo relé local…";
        try
        {
            var host = _host.Text.Trim();
            var port = int.TryParse(_port.Text.Trim(), out var p) ? p : 0;
            if (host.Length == 0 || port is <= 0 or > 65535)
            {
                _result.Text = "Preencha endereço e porta.";
                return;
            }

            // Porta livre (0): o deck aberto já usa a porta fixa dele.
            using var relay = new ProxyRelay(host, port, _user.Text.Trim(), _password.Text);
            relay.Start();
            var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{relay.Port}"), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            var ip = (await http.GetStringAsync("https://api.ipify.org/?format=text")).Trim();
            // Conferir também os sites do deck: alguns provedores bloqueiam domínios.
            var sites = new List<string>();
            foreach (var (name, url) in new[] { ("pokeidle.io", "https://pokeidle.io/"), ("kick.com", "https://kick.com/"), ("twitch.tv", "https://www.twitch.tv/") })
            {
                try
                {
                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    sites.Add($"{name} {(int)response.StatusCode}");
                }
                catch (Exception ex)
                {
                    sites.Add($"{name} falhou ({(ex.InnerException ?? ex).Message})");
                }
            }
            _result.Text = $"Deu certo! IP externo via proxy: {ip}\nSites: {string.Join(" · ", sites)}";
            _result.ForeColor = Theme.Vote;
        }
        catch (Exception ex)
        {
            _result.Text = "Não passou: " + (ex.InnerException ?? ex).Message;
        }
        finally
        {
            button.Enabled = true;
        }
    }
}
