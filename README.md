# PokeIdle

Ferramentas para o [pokeidle.io](https://pokeidle.io/app):

- **[PokeIdleMulti](PokeIdleMulti/)** — o PokeIdle Deck, várias contas numa janela só.

## PokeIdle Deck

4 contas numa janela, cada uma num perfil próprio do navegador embutido (WebView2), com login, cookies e
extensões separados.

- Grade 2×2, colunas ou uma conta ampliada; tela cheia no F11.
- Cabeçalho de cada conta: ⏻ desligar, ⛶ ampliar, ⟳ recarregar, 🔊 som, **kick** (escolher qual live da Kick fechar, ou todas), 🗳 voto no TopIdle e os botões das extensões.
- Lives para o bônus de XP: presença nos chats da Twitch e as 2 primeiras lives da Kick da lista que estão no ar (vídeo na menor qualidade), numa janela fora da tela.
- 🔁 Eco (um clique numa conta se repete nas outras), 🎯 liga/desliga do Rotom Sniper, 🧩 extensões por conta.

### Baixar e abrir (outro PC)

1. Em **Releases**, baixe o `PokeIdleDeck.exe` da versão mais nova.
2. Ponha numa pasta qualquer e abra. Ele não precisa de instalação nem do .NET.
   Precisa do WebView2 Runtime da Microsoft, que já vem no Windows 10/11 atualizado
   (se faltar, o deck avisa e dá o link).

Os dados do deck ficam em `%LOCALAPPDATA%\PokeIdleMulti` (configurações em `deck.json`, perfis das contas
em `WebView2`). Nada disso está no repositório: num PC novo é preciso entrar de novo no jogo, na Twitch e na Kick.

### Compilar

Precisa do .NET 8 SDK. Dentro de `PokeIdleMulti`:

```
dotnet publish PokeIdleMulti.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

O exe sai em `publish\PokeIdleMulti.exe` (renomeie para `PokeIdleDeck.exe`). Com `--self-contained false` ele
fica com ~1,5 MB, mas aí o PC precisa do .NET 8 Desktop Runtime.

Autoteste: `PokeIdleDeck.exe --self-test` abre contas de teste (em `%LOCALAPPDATA%\PokeIdleMulti\selftest`,
sem mexer nas suas), confere tudo e grava o resultado em `%TEMP%\PokeIdleDeck-selftest.txt`.
