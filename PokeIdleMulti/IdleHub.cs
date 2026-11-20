using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>
/// Situação do voto de uma conta, como o jogo conta no painel "Vote &amp; Ganhe". LoggedIn: a página da conta tem
/// sessão no jogo. Active: o Vote &amp; Ganhe está ligado no servidor. Votes/InPair/PerPair/VotesPerReward: o total de
/// votos e o caminho até o próximo diamante (PerPair 💎 a cada VotesPerReward votos).
/// </summary>
internal sealed record VoteState(bool LoggedIn, bool Active, DateTime? NextVoteAt, string? Url, string? Nick,
    int Votes = 0, int InPair = 0, int PerPair = 0, int VotesPerReward = 0)
{
    public bool Ready => LoggedIn && Active && (NextVoteAt is not DateTime at || at <= DateTime.Now);
}

/// <summary>
/// Voto no IdleHub.io (https://idlehub.io), o ranking de jogos idle que substituiu o TopIdle no pokeidle (v1.458,
/// 11/2026). Cada voto vira um aviso do IdleHub ao servidor do jogo, que dá diamantes ao nick (um voto por dia vale
/// prêmio). Quem diz quando a conta pode votar é o próprio jogo: o deck faz, na página do jogo da conta, o mesmo
/// pedido do botão da urna (POST /idlehub/me com o token da sessão). Quem vota é você, na página do IdleHub aberta
/// no navegador da conta (é lá que fica o login Google/Discord do IdleHub dela).
/// </summary>
internal static class IdleHub
{
    public const string Base = "https://idlehub.io";
    public const string Game = "pokeidle-io";

    // O nick que vai no link (o jogo aceita só estes; o IdleHub recebe ele codificado).
    private static readonly Regex PlayerPattern = new("^[A-Za-z0-9_-]{1,64}$");

    // Roda na página do jogo: o mesmo pedido do botão da urna, com a sessão salva pelo jogo.
    private const string StateScript = """
        (async () => {
          if (location.hostname !== 'pokeidle.io') return { erro: 'pagina' };
          let s = null;
          try { s = JSON.parse(localStorage.getItem('sessao') ?? 'null'); } catch {}
          if (!s?.token) return { erro: 'login' };
          const r = await fetch('/idlehub/me', {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ token: s.token }),
          });
          return { status: r.status, nick: s.nick ?? null, dados: await r.json().catch(() => null) };
        })()
        """;

    /// <summary>
    /// Pergunta ao jogo, na página da conta, como está o voto dela. Null quando a página não está no jogo
    /// (carregando, recarregando, caiu) ou o jogo não respondeu: vale tentar de novo.
    /// </summary>
    public static async Task<VoteState?> GetStateAsync(CoreWebView2 core)
    {
        var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression = StateScript, awaitPromise = true, returnByValue = true }));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value)
            ? ParseState(value) : null;
    }

    /// <summary>A resposta do script da página (separada para o autoteste).</summary>
    public static VoteState? ParseState(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        var error = Text(value, "erro");
        int status = value.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
        if (error == "login" || status == 401)
            return new VoteState(false, false, null, null, null);
        if (error is not null || status is < 200 or >= 300
            || !value.TryGetProperty("dados", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        var nick = Text(data, "identificador") ?? Text(value, "nick");
        var url = Text(data, "url");
        double wait = Number(data, "esperaMs");
        return new VoteState(
            LoggedIn: true,
            Active: data.TryGetProperty("ativo", out var on) && on.ValueKind == JsonValueKind.True,
            NextVoteAt: wait > 0 ? DateTime.Now.AddMilliseconds(wait) : null,
            Url: url is not null && IsIdleHubLink(url) ? url : VoteUrl(nick),
            Nick: nick,
            Votes: (int)Number(data, "votos"),
            InPair: (int)Number(data, "noPar"),
            PerPair: (int)Number(data, "porPar"),
            VotesPerReward: (int)Number(data, "votosPorDiamante"));
    }

    /// <summary>A página de voto do pokeidle no IdleHub, já com o nick que recebe o prêmio.</summary>
    public static string VoteUrl(string? player) =>
        $"{Base}/votar/{Game}"
        + (player is not null && PlayerPattern.IsMatch(player) ? $"?jogador={Uri.EscapeDataString(player)}" : "");

    /// <summary>Um POST da página do IdleHub que pode ser o voto (o formulário de voto responde JSON).</summary>
    public static bool IsVoteResponse(string method, string uri, int status) =>
        method == "POST" && status is >= 200 and < 300
        && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsIdleHubHost(parsed.Host)
        && !parsed.AbsolutePath.StartsWith("/api/auth/") && !parsed.AbsolutePath.StartsWith("/api/painel/");

    /// <summary>
    /// A resposta do voto disse que ele entrou ({ ok: true, proximoEm: ms }): next é quando o IdleHub libera o próximo.
    /// </summary>
    public static bool TryParseVoted(string body, out DateTime? next)
    {
        next = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                return false;
            double at = Number(root, "proximoEm");
            if (at > 0)
                next = DateTimeOffset.FromUnixTimeMilliseconds((long)at).LocalDateTime;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsIdleHubLink(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && IsIdleHubHost(parsed.Host);

    /// <summary>O nick que o jogo guarda no localStorage ("nick"), a partir do resultado de ExecuteScriptAsync.</summary>
    public static string? ParseNick(string scriptResult)
    {
        try
        {
            var value = JsonSerializer.Deserialize<string?>(scriptResult);
            // O jogo pode guardar o nick já em JSON ("\"fulano\"").
            if (value is not null && value.StartsWith('"'))
                value = JsonSerializer.Deserialize<string?>(value);
            value = value?.Trim();
            return value is not null && PlayerPattern.IsMatch(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsIdleHubHost(string host) => host is "idlehub.io" or "www.idlehub.io";

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}
