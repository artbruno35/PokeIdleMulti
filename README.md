# PokeIdle

Ferramentas para o [pokeidle.io](https://pokeidle.io/app):

- **[PokeIdleMulti](PokeIdleMulti/)** — o PokeIdle Deck, várias contas numa janela só.

## PokeIdle Deck

4 contas numa janela, cada uma num perfil próprio do navegador embutido (WebView2), com login, cookies e
extensões separados.

- Grade 2×2, colunas ou uma conta ampliada; tela cheia no F11.
- Cabeçalho de cada conta: ⏻ desligar, ⛶ ampliar, ⟳ recarregar, 🔊 som, **kick** (escolher qual live da Kick fechar, ou todas), 🛒 recompra, 🗳 voto no TopIdle e os botões das extensões.
- **🛒 Recompra** (o carrinho no cabeçalho, verde quando ligada e com o jogo conectado): a cada segundo o deck confere quantas pokebolas, revives e poções a conta tem e compra no Market do NPC o que ficou abaixo de "Recomprar abaixo de", até "Manter". Pokebola: a escolhida (Ultra Ball de fábrica). Revives: contados juntos, compra o mais barato (Revive). Poções: contadas juntas, compra a Ultimate Potion de fábrica, ou a de **melhor custo-benefício**: o ouro por HP que ela cura de verdade no pokémon ativo (a poção entra abaixo do limiar do "Usar Poções", então cura além do que falta se perde). A Golden Potion fica de fora. Opcional: pôr a poção escolhida em 1º na fila do "Usar Poções". Nunca deixa o ouro abaixo do mínimo; se não der para tudo, poção, depois revive, depois pokebola. Até 9.999 por compra, uma a cada 4 s, pelo mesmo pedido do botão Comprar do jogo; 3 compras seguidas sem resposta dão 2 min de pausa. Cada conta tem a sua configuração, e a janela mostra o estoque e o registro das compras.
- Lives para o bônus de XP: presença nos chats da Twitch e as 2 primeiras lives da Kick da lista que estão no ar (vídeo na menor qualidade, sem propaganda nem rastreadores), numa janela fora da tela. Lá fora o chat da Kick fica escondido, o que corta quase metade da CPU das lives. **💰 Pontos da Kick** → a conta mostra as 2 lives lado a lado só com o rodapé do chat: o botão dos pontos e espaço para o popup de escolher o bônus, sem as mensagens (o vídeo continua tocando, fora da vista). A janela tem ~640 px de altura; arraste a borda para mudar, e o deck guarda. "Próxima conta ▶" passa para a conta seguinte, e "Página inteira" mostra a live toda, com o chat. A cada 30 s o deck confere se o vídeo de cada live está tocando (▶ na barra de baixo) e recarrega a que travou ou caiu (⟳); sem internet, tenta de novo com intervalos cada vez maiores. A presença na Twitch reconecta sozinha quando a conexão morre (Wi-Fi trocou, o notebook dormiu).
- Barra de baixo: a RAM separada em jogo · lives · navegador, para ver o que está pesando.
- **⚡ ULTRA** (fica laranja, "ligado", como o eco): liga o **Modo ULTRA Economia** do jogo (em inglês, ULTRA Battery Saver) em todas as contas, na hora: o jogo inteiro sai da tela e fica só uma caixa com o XP, o Gold, os drops e os shinies da sessão, e a caça continua. O deck usa o interruptor do próprio jogo (Configurações → Desempenho) e o "Voltar ao jogo" da caixa; conta fora do jogo guarda a escolha e liga ao entrar, e a conta que recarrega ou é ligada volta ao ULTRA sozinha. Na setinha do botão, 🐢 Menos animações (ligado de fábrica): o jogo vê o "reduzir movimento" do sistema e ele mesmo desliga as animações que rodam sem parar, o que vale no jogo normal e na tela de login. Conta parada na tela de login ganha um aviso no cabeçalho (o captcha do login vai juntando memória).
- Sem o ULTRA, dá para gastar menos com o **Modo Economia** do jogo (Configurações → Desempenho): a cena da hunt deixa de ser desenhada.
- ⇄ Eco (um clique numa conta se repete nas outras), 🎯 liga/desliga do Rotom Sniper, 🧩 extensões por conta.
- Zoom: **− Zoom 100% +** na barra de cima muda todas as contas juntas (na setinha, os níveis de 50% a 150%), com o eco ligado ou não.
- Conta estreita (em colunas): o voto fica só com o 🗳, o "rodando há" sai e os botões se juntam, para nenhum sumir do cabeçalho.

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
