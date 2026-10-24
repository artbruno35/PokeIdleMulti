using System.Net;
using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// Quem está ao vivo na Kick, pela mesma API que o site usa (kick.com/api/v2/channels/{canal}). A Kick
/// só rende pontos em duas lives ao mesmo tempo, então o deck abre só as duas primeiras da lista que
/// estão no ar; live offline não fica aberta gastando.
/// </summary>
internal static class KickLive
{
    /// <summary>Quantas lives da Kick rendem pontos ao mesmo tempo.</summary>
    public const int Slots = 2;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>true = no ar, false = offline, null = não deu para saber agora.</summary>
    public static async Task<bool?> IsLiveAsync(string channel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return false;
            return response.IsSuccessStatusCode ? ParseLive(await response.Content.ReadAsStringAsync()) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A resposta da API: "livestream" é null com o canal offline.</summary>
    public static bool? ParseLive(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("livestream", out var live))
                return null;
            return live.ValueKind == JsonValueKind.Object
                && !(live.TryGetProperty("is_live", out var isLive) && isLive.ValueKind == JsonValueKind.False);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// As lives para abrir: as primeiras da lista que estão no ar, até <see cref="Slots"/> (a primeira da
    /// lista tem prioridade). Canal que não deu para conferir conta como no ar, para não perder pontos.
    /// </summary>
    public static List<string> Pick(IEnumerable<string> channels, IReadOnlyDictionary<string, bool?> live) =>
        channels.Where(c => live.GetValueOrDefault(c) != false).Take(Slots).ToList();
}
