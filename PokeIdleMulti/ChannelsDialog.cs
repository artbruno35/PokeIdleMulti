namespace PokeIdleMulti;

/// <summary>Janela para editar os canais da Twitch e da Kick que ficam abertos em segundo plano.</summary>
internal sealed class ChannelsDialog : Form
{
    private readonly TextBox _twitch = NewList();
    private readonly TextBox _kick = NewList();
    private readonly ComboBox _twitchMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly NumericUpDown _kickLives = new() { Minimum = 0, Maximum = KickLive.MaxSlots, Width = 60 };
    private readonly CheckBox _kickRedeem = new() { Text = "Resgatar sozinho com os pontos da Kick:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
    private readonly ComboBox _kickReward = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ToolTip _tips = new();

    public ChannelsDialog(IEnumerable<string> twitch, IEnumerable<string> kick, TwitchMode twitchMode, int kickLives,
        bool kickRedeem = false, KickReward kickReward = KickReward.Xp)
    {
        Text = "Canais das lives";
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 546);
        Padding = new Padding(12);

        _twitch.Text = string.Join(Environment.NewLine, twitch);
        _kick.Text = string.Join(Environment.NewLine, kick);
        // Na mesma ordem do enum TwitchMode.
        _twitchMode.Items.AddRange(new object[]
        {
            "Só presença no chat (sem página, quase não gasta: recomendado)",
            "Abrir a página do chat de cada canal",
            "Abrir a live inteira de cada canal",
        });
        _twitchMode.SelectedIndex = (int)twitchMode;
        var mode = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        mode.Controls.Add(new Label { Text = "Twitch:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        mode.Controls.Add(_twitchMode);
        _kickLives.Value = Math.Clamp(kickLives, 0, KickLive.MaxSlots);
        var lives = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        lives.Controls.Add(new Label { Text = "Lives da Kick abertas em cada conta:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        lives.Controls.Add(_kickLives);
        lives.Controls.Add(new Label { Text = "(de fábrica 2; 0 = nenhuma)", AutoSize = true, Margin = new Padding(4, 7, 0, 0) });

        // Na mesma ordem do enum KickReward.
        _kickReward.Items.AddRange(new object[]
        {
            "+15% XP (300 pontos)",
            "+15% Captura (500 pontos)",
            "+15% Shiny, Secret Lure (1000 pontos)",
        });
        _kickReward.SelectedIndex = (int)kickReward;
        _kickRedeem.Checked = kickRedeem;
        _kickReward.Enabled = kickRedeem;
        _kickRedeem.CheckedChanged += (_, _) => _kickReward.Enabled = _kickRedeem.Checked;
        var redeem = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30 };
        redeem.Controls.Add(_kickRedeem);
        redeem.Controls.Add(_kickReward);
        var redeemHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Text = "A cada 5 min confere o saldo de cada live da Kick aberta e resgata 1 bônus se der (as horas " +
                   "acumulam). Vincule a Kick no jogo (Bônus na Kick) em cada conta.",
        };
        _tips.SetToolTip(_kickRedeem, "Sem a Kick vinculada no jogo os pontos voltam: o deck para de resgatar naquela conta e avisa no menu Lives");

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 100,
            Text = "Um canal por linha, como aparece no endereço (twitch.tv/rodil → rodil). Use os streamers " +
                   "oficiais do painel de bônus do jogo. Ao salvar, as lives abertas reabrem com os canais novos.\n" +
                   "Kick: o deck abre as primeiras da lista que estiverem no ar, até o número escolhido embaixo " +
                   "(a primeira tem prioridade), e não deixa live offline aberta. Cada live a mais gasta uns 260 MB de RAM.",
        };

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        columns.Controls.Add(new Label { Text = "Twitch", AutoSize = true }, 0, 0);
        columns.Controls.Add(new Label { Text = "Kick (em ordem de prioridade)", AutoSize = true }, 1, 0);
        columns.Controls.Add(_twitch, 0, 1);
        columns.Controls.Add(_kick, 1, 1);

        var ok = new Button { Text = "Salvar", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        // Os controles encaixam do último para o primeiro: texto no topo, botões embaixo, a caixa logo acima deles.
        Controls.Add(columns);
        Controls.Add(mode);
        Controls.Add(lives);
        Controls.Add(redeem);
        Controls.Add(redeemHint);
        Controls.Add(buttons);
        Controls.Add(hint);
    }

    public List<string> TwitchChannels => Parse(_twitch.Text);
    public List<string> KickChannels => Parse(_kick.Text);
    public TwitchMode TwitchMode => (TwitchMode)Math.Max(0, _twitchMode.SelectedIndex);
    public int KickLives => (int)_kickLives.Value;
    public bool KickRedeem => _kickRedeem.Checked;
    public KickReward KickReward => (KickReward)Math.Max(0, _kickReward.SelectedIndex);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();
        base.Dispose(disposing);
    }

    private static TextBox NewList() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
    };

    // Aceita também o endereço colado inteiro (https://kick.com/yetz → yetz).
    private static List<string> Parse(string text) => text
        .Split(new[] { '\r', '\n', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(c => c.Trim().TrimEnd('/'))
        .Select(c => c[(c.LastIndexOf('/') + 1)..].TrimStart('@').ToLowerInvariant())
        .Where(c => c.Length > 0)
        .Distinct()
        .ToList();
}
