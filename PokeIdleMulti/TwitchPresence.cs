using System.Net.WebSockets;
using System.Text;

namespace PokeIdleMulti;

/// <summary>
/// Presença nos chats da Twitch sem abrir página nenhuma: uma conexão com o servidor de chat da Twitch
/// (o mesmo que o chat do site usa por baixo), entrando em todos os canais oficiais com o login da
/// Twitch salvo no perfil da conta. O jogo confere quem está no chat; página e vídeo não fazem
/// diferença, e assim quase não gasta memória nem CPU.
/// </summary>
internal sealed class TwitchPresence : IDisposable
{
    private const string Server = "wss://irc-ws.chat.twitch.tv:443";

    // A Twitch aceita 20 entradas em canais a cada 10 s.
    private const int JoinBatch = 15;

    // A Twitch manda PING a cada ~5 min. Conexão que morreu calada (o Wi-Fi trocou, o notebook dormiu)
    // não avisa nada: tanto tempo sem chegar coisa nenhuma é conexão morta, e aí conecta de novo.
    private static readonly TimeSpan Silence = TimeSpan.FromMinutes(7);

    private readonly Func<Task<(string? Token, string? Login)>> _readLogin;
    private readonly List<string> _channels;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly HashSet<string> _joined = new();
    private readonly object _lock = new();

    public TwitchPresence(Func<Task<(string? Token, string? Login)>> readLogin, IEnumerable<string> channels)
    {
        _readLogin = readLogin;
        _channels = channels.Select(c => c.Trim().TrimStart('#').ToLowerInvariant()).Where(c => c.Length > 0).Distinct().ToList();
    }

    /// <summary>Mudou o status (chamado fora da thread da interface).</summary>
    public event Action? Changed;

    public string Status { get; private set; } = "conectando…";
    public bool NeedsLogin { get; private set; }
    public string? Login { get; private set; }
    public int Total => _channels.Count;

    public int Joined
    {
        get
        {
            lock (_lock)
                return _joined.Count;
        }
    }

    public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));

    public void Dispose()
    {
        _stop.Cancel();
        Changed = null;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var (token, login) = await _readLogin();
                // Anônimo (justinfan) só no autoteste: não conta como presença no chat.
                bool anonymous = string.IsNullOrEmpty(token) && login?.StartsWith("justinfan") == true;
                if (!anonymous)
                    login = string.IsNullOrEmpty(token) ? null : await ValidateAsync(token!, stop) ?? login;
                if (string.IsNullOrEmpty(login))
                {
                    SetStatus("entre na Twitch (Lives → Mostrar)", needsLogin: true);
                    await Task.Delay(TimeSpan.FromSeconds(15), stop);
                    continue;
                }
                Login = login;
                await SessionAsync(token, login, stop);
                wait = TimeSpan.FromSeconds(5);
                SetStatus("caiu, voltando…");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                SetStatus("o login da Twitch expirou: entre de novo (Lives → Mostrar)", needsLogin: true);
                wait = TimeSpan.FromMinutes(1);
            }
            catch
            {
                SetStatus("sem conexão com a Twitch, tentando de novo…");
            }

            lock (_lock)
                _joined.Clear();
            try
            {
                await Task.Delay(wait, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            wait = TimeSpan.FromSeconds(Math.Min(120, wait.TotalSeconds * 2));
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Confere o login na Twitch (endpoint oficial de validação) e devolve o nome de usuário dele.
    /// Login vencido vira <see cref="UnauthorizedAccessException"/>; sem resposta, null.
    /// </summary>
    private static async Task<string?> ValidateAsync(string token, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
        using var response = await Http.SendAsync(request, stop);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();
        if (!response.IsSuccessStatusCode)
            return null;
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop));
        return json.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }

    /// <summary>Uma conexão: entra nos canais e fica respondendo o PING até cair.</summary>
    private async Task SessionAsync(string? token, string login, CancellationToken stop)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(Server), stop);
        SetStatus("conectando…");
        // Sem token entra anônimo (só o autoteste usa: anônimo não conta como presença).
        if (!string.IsNullOrEmpty(token))
            await SendAsync(socket, $"PASS oauth:{token}", stop);
        await SendAsync(socket, $"NICK {login}", stop);

        var buffer = new byte[16 * 1024];
        var pending = new StringBuilder();
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(stop);
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            silence.CancelAfter(Silence);
            try
            {
                result = await socket.ReceiveAsync(buffer, silence.Token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                return;
            }
            if (result.MessageType == WebSocketMessageType.Close)
                return;
            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            var text = pending.ToString();
            int end = text.LastIndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0)
                continue;
            pending.Clear().Append(text[(end + 2)..]);
            foreach (var line in text[..end].Split("\r\n"))
            {
                if (!await HandleAsync(socket, line, login, stop))
                    return;
            }
        }
    }

    /// <summary>Trata uma linha do servidor. Devolve false quando é para reconectar.</summary>
    private async Task<bool> HandleAsync(ClientWebSocket socket, string line, string login, CancellationToken stop)
    {
        if (line.StartsWith("PING", StringComparison.Ordinal))
        {
            await SendAsync(socket, "PONG" + line[4..], stop);
            return true;
        }
        if (line.Contains(" NOTICE * :Login authentication failed") || line.Contains(" NOTICE * :Improperly formatted auth"))
            throw new UnauthorizedAccessException();
        if (line.EndsWith(" RECONNECT", StringComparison.Ordinal))
            return false;

        // Mensagens de chat dos canais chegam aos montes: só interessam as nossas entradas e saídas.
        var parts = line.Split(' ', 4);
        if (parts.Length >= 2 && parts[1] == "001")
        {
            SetStatus("entrando nos chats…");
            _ = JoinAllAsync(socket, stop);
        }
        else if (parts.Length >= 3 && parts[1] is "JOIN" or "PART"
                 && parts[0].StartsWith($":{login}!", StringComparison.OrdinalIgnoreCase))
        {
            var channel = parts[2].TrimStart('#');
            lock (_lock)
            {
                if (parts[1] == "JOIN")
                    _joined.Add(channel);
                else
                    _joined.Remove(channel);
            }
            SetStatus($"presente em {Joined} de {Total} chats como {login}");
        }
        return true;
    }

    private async Task JoinAllAsync(ClientWebSocket socket, CancellationToken stop)
    {
        try
        {
            for (int i = 0; i < _channels.Count; i += JoinBatch)
            {
                if (i > 0)
                    await Task.Delay(TimeSpan.FromSeconds(11), stop);
                var batch = _channels.Skip(i).Take(JoinBatch).Select(c => "#" + c);
                await SendAsync(socket, "JOIN " + string.Join(",", batch), stop);
            }
        }
        catch
        {
            // A conexão caiu no meio; a próxima entra em todos de novo.
        }
    }

    private async Task SendAsync(ClientWebSocket socket, string line, CancellationToken stop)
    {
        await _sending.WaitAsync(stop);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(line + "\r\n"), WebSocketMessageType.Text, true, stop);
        }
        finally
        {
            _sending.Release();
        }
    }

    private void SetStatus(string status, bool needsLogin = false)
    {
        Status = status;
        NeedsLogin = needsLogin;
        Changed?.Invoke();
    }
}
