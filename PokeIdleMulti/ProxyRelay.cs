using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PokeIdleMulti;

/// <summary>
/// Um relé local: o navegador embutido (e os pedidos do próprio deck) apontam para ele, sem senha, e ele repassa
/// para o proxy residencial com o login e a senha. O Chromium (motor do WebView2) não aceita login/senha embutidos
/// no --proxy-server, então este relé faz a autenticação. Trata CONNECT (https/wss) e pedidos http simples.
/// De quebra, conta os bytes que passam por ele (no total e por destino), para o monitor de tráfego do deck.
/// </summary>
internal sealed class ProxyRelay : IDisposable
{
    /// <summary>
    /// Porta local preferida do deck: fixa, para os argumentos do navegador serem os mesmos a cada abertura
    /// (o WebView2 reclama se o mesmo perfil abrir com argumentos diferentes de um processo que ainda não fechou).
    /// </summary>
    public const int PreferredPort = 38211;

    public const string Game = "Jogo";
    public const string Kick = "Kick";
    public const string Twitch = "Twitch";
    public const string TopIdle = "TopIdle";
    public const string Other = "Outros";
    private static readonly string[] Categories = { Game, Kick, Twitch, TopIdle, Other };

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private sealed class Counter
    {
        public long Down;
        public long Up;
    }

    private TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly string? _authHeader;
    private readonly int _preferredPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Counter> _byCategory = new();
    private long _down;
    private long _up;

    /// <param name="user">Vazio para proxy liberado por IP (sem login).</param>
    /// <param name="preferredPort">A porta local de preferência; 0 (ou ocupada) pega uma livre do Windows.</param>
    public ProxyRelay(string upstreamHost, int upstreamPort, string user, string password, int preferredPort = 0)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _authHeader = string.IsNullOrEmpty(user)
            ? null
            : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        _preferredPort = preferredPort;
        _listener = new TcpListener(IPAddress.Loopback, preferredPort);
        foreach (var category in Categories)
            _byCategory[category] = new Counter();
    }

    /// <summary>A porta local que o navegador usa: --proxy-server=http://127.0.0.1:{Port}. Vale depois do <see cref="Start"/>.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Bytes que o navegador baixou (do proxy residencial para o deck).</summary>
    public long DownBytes => Interlocked.Read(ref _down);

    /// <summary>Bytes que o navegador enviou (do deck para o proxy residencial).</summary>
    public long UpBytes => Interlocked.Read(ref _up);

    /// <summary>O tráfego por destino (Jogo, Kick, Twitch, TopIdle, Outros).</summary>
    public IReadOnlyList<(string Name, long Down, long Up)> Usage() =>
        Categories.Select(c => (c, Interlocked.Read(ref _byCategory[c].Down), Interlocked.Read(ref _byCategory[c].Up))).ToList();

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException) when (_preferredPort != 0)
        {
            // A porta de preferência está ocupada por outro programa: usa uma livre.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }
        _ = AcceptLoopAsync();
    }

    /// <summary>A categoria do tráfego de um host, para o monitor mostrar quem gasta o proxy.</summary>
    public static string Categorize(string host)
    {
        host = host.Trim().Trim('[', ']').ToLowerInvariant();
        bool Is(string domain) => host == domain || host.EndsWith("." + domain);
        if (Is("pokeidle.io"))
            return Game;
        if (Is("kick.com") || Is("live-video.net") || Is("kick.dev"))
            return Kick;
        if (Is("twitch.tv") || Is("ttvnw.net") || Is("jtvnw.net") || Is("twitchcdn.net") || Is("twitchapps.com"))
            return Twitch;
        if (Is("topidle.com"))
            return TopIdle;
        return Other;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var head = await ReadUntilHeaderEndAsync(stream, _cts.Token);
                if (head is null)
                    return;
                var firstLine = head.Split("\r\n", 2)[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 3)
                    return;
                if (parts[0] == "CONNECT")
                    await TunnelAsync(client, stream, parts[1]);
                else
                    await ForwardAsync(client, stream, head, parts[1]);
            }
            catch
            {
                // Conexão quebrada (aba fechada, proxy caiu): só encerra este túnel.
            }
        }
    }

    /// <summary>https/wss: CONNECT no proxy residencial e depois só repassa os bytes.</summary>
    private async Task TunnelAsync(TcpClient client, NetworkStream stream, string hostPort)
    {
        var counter = CounterFor(HostOf(hostPort));
        using var handshake = Handshake();
        using var upstream = await ConnectUpstreamAsync(handshake.Token);
        var up = upstream.GetStream();

        var connect = $"CONNECT {hostPort} HTTP/1.1\r\nHost: {hostPort}\r\n" +
                      (_authHeader is null ? "" : $"Proxy-Authorization: {_authHeader}\r\n") + "\r\n";
        var connectBytes = Encoding.ASCII.GetBytes(connect);
        await up.WriteAsync(connectBytes.AsMemory(), _cts.Token);
        Count(counter, upload: true, connectBytes.Length);

        var response = await ReadUntilHeaderEndAsync(up, handshake.Token);
        if (response is null || !response.StartsWith("HTTP/1.", StringComparison.Ordinal) || !response.Split("\r\n", 2)[0].Contains(" 200"))
        {
            // Devolve ao navegador o que o proxy disse (407 senha errada, 403, 502…), para ele mostrar o erro.
            if (response is not null)
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response).AsMemory(), _cts.Token);
            return;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n").AsMemory(), _cts.Token);
        await PipeAsync(client, upstream, counter);
    }

    /// <summary>http simples (o Chromium manda o endereço inteiro): põe o login do proxy e repassa, sem reaproveitar a conexão.</summary>
    private async Task ForwardAsync(TcpClient client, NetworkStream stream, string head, string target)
    {
        var counter = CounterFor(Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : "");
        using var handshake = Handshake();
        using var upstream = await ConnectUpstreamAsync(handshake.Token);
        var up = upstream.GetStream();

        var lines = head.Split("\r\n");
        var request = new StringBuilder(lines[0]).Append("\r\n");
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var name = line.Split(':', 2)[0].Trim();
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Append(line).Append("\r\n");
        }
        if (_authHeader is not null)
            request.Append("Proxy-Authorization: ").Append(_authHeader).Append("\r\n");
        request.Append("Connection: close\r\n\r\n");

        var bytes = Encoding.ASCII.GetBytes(request.ToString());
        await up.WriteAsync(bytes.AsMemory(), _cts.Token);
        Count(counter, upload: true, bytes.Length);
        await PipeAsync(client, upstream, counter);
    }

    private async Task<TcpClient> ConnectUpstreamAsync(CancellationToken token)
    {
        var upstream = new TcpClient { NoDelay = true };
        try
        {
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, token);
            return upstream;
        }
        catch
        {
            upstream.Dispose();
            throw;
        }
    }

    /// <summary>Um prazo para conectar e para o proxy responder ao CONNECT (o túnel em si não tem prazo).</summary>
    private CancellationTokenSource Handshake()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(ConnectTimeout);
        return timeout;
    }

    /// <summary>Os dois sentidos ao mesmo tempo. Quando um lado fecha, o outro tem uns segundos para terminar.</summary>
    private async Task PipeAsync(TcpClient client, TcpClient upstream, Counter counter)
    {
        var uploading = PumpAsync(client, upstream, counter, isUpload: true);
        var downloading = PumpAsync(upstream, client, counter, isUpload: false);
        var first = await Task.WhenAny(uploading, downloading);
        var other = first == uploading ? downloading : uploading;
        try
        {
            await Task.WhenAny(other, Task.Delay(Grace, _cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PumpAsync(TcpClient from, TcpClient to, Counter counter, bool isUpload)
    {
        var buffer = new byte[81920];
        try
        {
            var source = from.GetStream();
            var target = to.GetStream();
            while (true)
            {
                int n = await source.ReadAsync(buffer.AsMemory(), _cts.Token);
                if (n <= 0)
                    break;
                await target.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                Count(counter, isUpload, n);
            }
        }
        catch
        {
            // O outro lado fechou.
        }
        try
        {
            to.Client.Shutdown(SocketShutdown.Send); // avisa o outro lado que acabou, sem derrubar o que ainda vem de volta
        }
        catch
        {
        }
    }

    private Counter CounterFor(string host) => _byCategory[Categorize(host)];

    private static string HostOf(string hostPort)
    {
        int colon = hostPort.LastIndexOf(':');
        return colon > 0 ? hostPort[..colon] : hostPort;
    }

    private void Count(Counter counter, bool upload, long n)
    {
        if (upload)
        {
            Interlocked.Add(ref _up, n);
            Interlocked.Add(ref counter.Up, n);
        }
        else
        {
            Interlocked.Add(ref _down, n);
            Interlocked.Add(ref counter.Down, n);
        }
    }

    /// <summary>Lê até o fim dos cabeçalhos (\r\n\r\n) e devolve o texto (null se fechou antes de mandar algo).</summary>
    private static async Task<string?> ReadUntilHeaderEndAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 65536)
        {
            int n = await stream.ReadAsync(one.AsMemory(), token);
            if (n <= 0)
                return buffer.Length == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
            buffer.WriteByte(one[0]);
            var bytes = buffer.GetBuffer();
            long len = buffer.Length;
            if (len >= 4 && bytes[len - 4] == '\r' && bytes[len - 3] == '\n' && bytes[len - 2] == '\r' && bytes[len - 1] == '\n')
                return Encoding.ASCII.GetString(bytes, 0, (int)len);
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }
        _cts.Dispose();
    }
}
