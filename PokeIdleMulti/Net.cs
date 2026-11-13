using System.Net;

namespace PokeIdleMulti;

/// <summary>
/// Os pedidos que o próprio deck faz (checar lives, TopIdle, lista de qualidade do vídeo, Chrome Web Store) saem pelo
/// mesmo proxy do navegador, para o IP de saída ser um só e o consumo aparecer no monitor. O DeckForm põe a porta do
/// relé aqui antes de qualquer pedido; sem proxy ligado, os pedidos saem direto como sempre.
/// </summary>
internal static class Net
{
    /// <summary>A porta local do relé (ProxyRelay); null com o proxy desligado.</summary>
    public static int? ProxyPort { get; set; }

    /// <summary>Um handler novo já apontando para o relé (se houver).</summary>
    public static HttpClientHandler Handler() => Use(new HttpClientHandler());

    public static HttpClientHandler Use(HttpClientHandler handler)
    {
        if (ProxyPort is int port)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{port}");
            handler.UseProxy = true;
        }
        return handler;
    }
}
