using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// O resgate automático do bônus do PokeIdle com os pontos de cada canal da Kick, numa conta. Usa os mesmos pedidos
/// que o site faz ao clicar em Resgatar: o saldo (/points), a lista de bônus do canal (/rewards) e o resgate
/// (/rewards/{id}/redeem), com o login da conta. Roda dentro do player de cada live aberta, que já usa esse login.
/// A cada conferência, no máximo 1 resgate por canal. Sem a Kick vinculada no jogo o PokeIdle devolve os pontos:
/// o deck vê o saldo voltar e para de resgatar nesta conta.
/// </summary>
internal sealed class KickRedeemer
{
    /// <summary>De quanto em quanto tempo confere o saldo (assistindo, a Kick dá 10 pontos a cada 5 min).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Resgate recusado pela Kick: espera antes de tentar de novo naquele canal.
    private static readonly TimeSpan RetryWait = TimeSpan.FromMinutes(30);

    // Saldo que sobe até esse tempo depois de um resgate, bem mais do que assistir dá, é o bônus devolvido.
    private static readonly TimeSpan RefundWindow = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, Channel> _channels = new();

    private sealed class Channel
    {
        /// <summary>O saldo na última conferência, já sem o que foi resgatado nela.</summary>
        public int? Points { get; set; }
        public int? Cost { get; set; }
        public DateTime? RedeemedAt { get; set; }
        public DateTime RetryAt { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>Por que parou de resgatar nesta conta (null: resgatando).</summary>
    public string? Stopped { get; private set; }

    /// <summary>Sem o login da Kick no perfil da conta (as lives tocam, mas não dá para resgatar).</summary>
    public bool NoLogin { get; set; }

    /// <summary>Volta a resgatar do zero (ao salvar Lives → Canais…, depois de vincular a Kick no jogo).</summary>
    public void Restart()
    {
        Stopped = null;
        NoLogin = false;
        _channels.Clear();
    }

    /// <summary>O saldo do canal e o último resgate, para o menu Lives ("230/300 pontos, resgatou 14:05").</summary>
    public string Text(string channel) => _channels.TryGetValue(channel, out var c) ? c.Text : "";

    // A partir desse saldo os pontos do último resgate voltaram (metade do custo: assistir dá 10 a cada 5 min).
    private static int? RefundAt(Channel c) =>
        c.Points is int points && c.Cost is int cost && c.RedeemedAt is DateTime at && DateTime.Now - at < RefundWindow
            ? points + cost / 2
            : null;

    /// <summary>
    /// O script que roda no player da live de <paramref name="channel"/>: lê o saldo e o bônus escolhido e, se der e
    /// estiver liberado, resgata 1. Devolve (quando a promessa acaba) o JSON que <see cref="Apply"/> lê.
    /// </summary>
    public string Script(string api, string channel, string token, KickReward reward)
    {
        _channels.TryGetValue(channel, out var c);
        bool spend = Stopped is null && (c is null || DateTime.Now >= c.RetryAt);
        int? refund = c is null ? null : RefundAt(c);
        // O nome do bônus em cada canal: "Bônus +15% XP PokeIdle", "Bônus de +15% Capture Boost", "… Secret Lure".
        string word = reward switch
        {
            KickReward.Capture => "captur",
            KickReward.Lure => "lure|shiny",
            _ => "\\bxp\\b",
        };
        var args = JsonSerializer.Serialize(new { api, channel, token, word, spend, refund });
        return "(async a => {" +
               " const call = (path, init) => fetch(a.api + '/api/v2/channels/' + encodeURIComponent(a.channel) + path," +
               "  { cache: 'no-cache', credentials: 'include', ...init, headers: { Accept: 'application/json', 'x-app-platform': 'web'," +
               "   Authorization: 'Bearer ' + a.token, ...(init && init.body ? { 'Content-Type': 'application/json' } : {}) } });" +
               " try {" +
               "  let r = await call('/points');" +
               "  if (!r.ok) return { error: 'saldo: erro ' + r.status };" +
               "  const points = (await r.json())?.data?.points;" +
               "  if (typeof points !== 'number') return { error: 'saldo: sem os pontos na resposta' };" +
               "  r = await call('/rewards');" +
               "  if (!r.ok) return { points, error: 'bônus: erro ' + r.status };" +
               "  const word = new RegExp(a.word, 'i');" +
               "  const reward = ((await r.json())?.data || []).find(x => x.is_enabled && word.test(x.title || '')" +
               "   && /pokeidle/i.test((x.title || '') + ' ' + (x.description || '')));" +
               "  if (!reward) return { points, error: 'canal sem o bônus' };" +
               "  const out = { points, cost: reward.cost, title: reward.title, paused: !!reward.is_paused };" +
               "  if (!a.spend || reward.is_paused || points < reward.cost || (a.refund !== null && points >= a.refund)) return out;" +
               // O transaction_id é um ULID novo, como o site gera: o tempo em base 32 e mais 16 letras ao acaso.
               "  const abc = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'; let t = Date.now(), id = '';" +
               "  for (let i = 0; i < 10; i++) { id = abc[t % 32] + id; t = Math.floor(t / 32); }" +
               "  for (let i = 0; i < 16; i++) id += abc[Math.floor(Math.random() * 32)];" +
               "  r = await call('/rewards/' + reward.id + '/redeem', { method: 'POST', body: JSON.stringify({ transaction_id: id, message: '' }) });" +
               "  return { ...out, redeemed: r.ok, status: r.status };" +
               " } catch (e) { return { error: 'sem resposta da Kick: ' + (e && e.message || e) }; }" +
               $"}})({args}).then(o => JSON.stringify(o))";
    }

    /// <summary>
    /// Anota o que o script de um canal devolveu. Devolve o aviso para o cabeçalho da conta quando resgatou, quando
    /// a Kick recusou ou quando parou por causa dos pontos que voltaram.
    /// </summary>
    public string? Apply(string channel, string json)
    {
        if (!_channels.TryGetValue(channel, out var c))
            _channels[channel] = c = new Channel();
        int? points = null, cost = null, status = null;
        string? error = null, title = null;
        bool redeemed = false, paused = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            points = root.TryGetProperty("points", out var p) && p.TryGetInt32(out int pv) ? pv : null;
            cost = root.TryGetProperty("cost", out var k) && k.TryGetInt32(out int kv) ? kv : null;
            status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out int sv) ? sv : null;
            error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            redeemed = root.TryGetProperty("redeemed", out var r) && r.ValueKind == JsonValueKind.True;
            paused = root.TryGetProperty("paused", out var z) && z.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            error = "a live não respondeu";
        }
        if (points is not int balance || cost is not int price)
        {
            c.Text = error ?? "a live não respondeu";
            return null;
        }

        string? note = null;
        if (RefundAt(c) is int limit && balance >= limit && Stopped is null)
        {
            Stopped = "os pontos voltaram (vincule a Kick no jogo, em Bônus na Kick, e salve Lives → Canais… de novo)";
            note = "Kick: os pontos do resgate voltaram (Kick não vinculada no jogo?); parei de resgatar nesta conta";
        }
        c.Cost = price;
        c.Points = balance;
        if (redeemed)
        {
            c.Points = balance - price;
            c.RedeemedAt = DateTime.Now;
            note = $"Kick: resgatei {title} em {channel}";
        }
        else if (status is int refused)
        {
            c.RetryAt = DateTime.Now + RetryWait;
            note = $"Kick: o resgate em {channel} foi recusado (erro {refused}); tento de novo em 30 min";
        }
        c.Text = $"{c.Points}/{price} pontos"
                 + (c.RedeemedAt is DateTime at ? $", resgatou {at:HH:mm}" : "")
                 + (status is int code && !redeemed ? $", resgate recusado ({code})" : "")
                 + (paused ? ", bônus pausado no canal" : "");
        return note;
    }
}
