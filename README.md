# PokeIdle Deck

Várias contas do [pokeidle.io](https://pokeidle.io/app) numa janela só, gastando pouco do PC.

O deck é um programa para Windows que abre 4 contas do jogo lado a lado. Cada uma roda num perfil próprio
do navegador embutido (Microsoft Edge WebView2), com login, cookies e extensões separados, como se fossem 4
navegadores diferentes. Por cima disso ele traz o que ajuda a caçar com várias contas: as lives da
Twitch/Kick para o bônus de XP, a recompra automática de pokebolas, revives e poções, o voto no TopIdle, o
eco de cliques e o Modo ULTRA Economia em todas as contas de uma vez.

> Projeto de fã, não oficial, sem ligação com o pokeidle.io. As automações usam os mesmos pedidos dos
> botões do próprio jogo, mas use por sua conta e respeite as regras do jogo.

## Baixar e abrir

1. Em [**Releases**](https://github.com/artbruno35/PokeIdleMulti/releases/latest), baixe o `PokeIdleDeck.exe` da versão mais nova.
2. Ponha numa pasta qualquer e abra. Não precisa instalar nem do .NET: o exe já leva tudo.
3. Precisa do **WebView2 Runtime** da Microsoft, que já vem no Windows 10/11 atualizado. Se faltar, o deck
   avisa e dá o link para baixar.

O exe não é assinado, então na primeira vez o Windows pode mostrar "O Windows protegeu o computador"
(SmartScreen): clique em **Mais informações → Executar assim mesmo**. Se preferir, compile você mesmo (veja
[Compilar](#compilar)).

**Trocar de versão:** feche o deck, troque o `PokeIdleDeck.exe` pelo novo e abra de novo. Os logins e as
configurações continuam, porque não ficam no exe.

## Primeiros passos

1. **Entre no jogo em cada conta.** As 4 abrem na tela de login do pokeidle.io; cada uma guarda o próprio
   login.
2. **Dê nome às contas.** Na barra de cima, clique com o botão direito no botão da conta → **Renomear…**
   (o nick do jogo, por exemplo).
3. **Desligue o que não usa.** O ⏻ no canto direito do cabeçalho de cada conta desliga a conta inteira (jogo,
   extensões, lives e voto), e ela não gasta nada até ligar de novo. Uma conta parada na tela de login gasta
   memória à toa: entre nela ou desligue.
4. **Lives (opcional).** Em **Lives**, marque a conta vinculada à sua Twitch/Kick (normalmente a main). Em
   **Lives → Mostrar as lives**, entre na Twitch e na Kick uma vez. Em **Lives → Canais…** ficam os canais e
   quantas lives da Kick abrir.
5. **Recompra (opcional).** O 🛒 no cabeçalho de cada conta abre a recompra dela. Confira os valores antes de
   ligar.
6. **Extensões (opcional).** Em **🧩 Extensões**, instale as extensões de cada conta (o Rotom Sniper, por
   exemplo).

## Funcionalidades

### Janela e contas

- **Layouts:** grade 2×2, colunas ou uma conta ampliada (as outras continuam rodando escondidas, no ritmo
  normal). **F11** é tela cheia; duplo clique no cabeçalho amplia ou volta.
- **Cabeçalho de cada conta:** ⏻ desligar, ampliar, ⟳ recarregar, som, **kick** (lives da Kick da conta),
  🛒 recompra, 🗳 voto no TopIdle e os botões das extensões. Ao lado do nome aparece há quanto tempo a conta
  está rodando e os avisos (por exemplo, "na tela de login"). Com as contas em colunas, o cabeçalho se aperta
  para nenhum botão sumir.
- **Zoom:** **− Zoom 100% +** na barra de cima muda todas as contas juntas (na setinha, de 50% a 150%).
- **⇄ Eco:** ligado, um clique numa conta se repete nas outras, no mesmo botão (pelo caminho e pelo texto
  dele, não pela posição na tela). Se a outra conta está numa tela sem esse botão, nada é clicado nela e o
  cabeçalho avisa. O que você digita nunca é repetido, para o chat do jogo não receber a mesma mensagem 4 vezes.

### ⚡ ULTRA e menos CPU

- **⚡ ULTRA** liga o **Modo ULTRA Economia** do próprio jogo em todas as contas, na hora: o jogo inteiro sai
  da tela e fica só uma caixa com o XP, o Gold, os drops e os shinies da sessão, e a caça continua. Ligado, a
  conta que recarrega ou é ligada volta ao ULTRA sozinha.
- **🐢 Menos animações** (na setinha do ULTRA, ligado de fábrica): o jogo passa a ver o "reduzir movimento" do
  sistema e ele mesmo desliga as animações que rodam sem parar.
- Sem o ULTRA, o **Modo Economia** do jogo (Configurações → Desempenho) também ajuda: a cena da hunt deixa de
  ser desenhada.
- A barra de baixo mostra a **RAM** separada em jogo · lives · navegador, para ver o que está pesando.

### 🛒 Recompra de pokebolas, revives e poções

A cada segundo o deck confere quanto a conta tem e compra no **Market do NPC** o que ficou abaixo de
"Recomprar abaixo de", até chegar em "Manter".

- **Pokebola:** a que você escolher (Ultra Ball de fábrica).
- **Revives:** contados juntos; compra sempre o mais barato.
- **Poções:** contadas juntas; compra a **Ultimate Potion** de fábrica, ou a de **melhor custo-benefício**: o
  ouro por HP que ela cura de verdade no pokémon ativo. Como a automação "Usar Poções" do jogo só usa a poção
  abaixo do limiar (30% de fábrica), cura além do que falta se perde. A Golden Potion fica de fora.
- Opcional: pôr a poção escolhida em 1º na fila do "Usar Poções", para ela ser gasta primeiro.
- **Ouro mínimo:** nunca compra a ponto de o ouro ficar abaixo dele. Se o ouro não der para tudo, compra
  primeiro poção, depois revive, depois pokebola.
- Até 9.999 por compra, uma compra a cada 4 s, pelo mesmo pedido do botão Comprar do jogo. Se o jogo recusar,
  espera 30 s; 3 compras seguidas sem resposta dão 2 min de pausa.
- Cada conta tem a sua configuração. A janela mostra o estoque, o ouro e o registro das compras; o carrinho
  do cabeçalho fica verde quando está ligada e com o jogo conectado.

### Lives para o bônus de XP

- **Twitch:** de fábrica, só a **presença nos chats** (uma conexão com o chat de cada canal, sem página
  aberta), que é o que o jogo confere e quase não gasta nada. Usa o login da Twitch da janela de lives da
  conta e reconecta sozinha quando a internet cai ou o PC dorme. Em Lives → Canais… dá para trocar por abrir a
  página do chat ou a live inteira.
- **Kick:** abre as primeiras lives da lista que estiverem no ar, até o número escolhido em Lives → Canais…
  (2 de fábrica, as que a Kick conta para os pontos). Live offline não fica aberta. O vídeo vem na menor
  qualidade, sem propaganda nem rastreadores, numa janela fora da tela com o chat escondido (quase metade da
  CPU a menos). A cada 30 s o deck confere se cada live está tocando e recarrega a que travou ou caiu.
- **Botão kick** de cada conta: escolher qual live da Kick fechar nela (ou todas), e abrir de novo.
- **💰 Pontos da Kick:** mostra as lives da conta lado a lado, só com o rodapé do chat: o botão dos pontos e o
  espaço do popup de escolher o bônus. A altura da janela muda arrastando a borda, e o deck guarda. "Próxima
  conta ▶" passa para a conta seguinte; "Página inteira" mostra a live toda.

### 🗳 Voto no TopIdle

O botão 🗳 de cada conta mostra quando o voto no TopIdle (que dá recompensa no jogo) está liberado e quanto
falta para o próximo. Clicar abre a página de voto já com o nick da conta, no perfil dela. Você vota na
página; o deck percebe quando o voto entra. A barra de baixo diz quais contas podem votar.

### 🧩 Extensões

- Cada conta tem as próprias extensões: **Importar do meu Chrome…**, **Da Chrome Web Store…** (cole o link)
  ou **Pasta descompactada…**, numa conta ou em todas.
- **Trazer configurações…** copia as configurações de uma extensão de outra conta do deck, do seu Chrome ou
  do IdleDeck. A cópia entra quando o deck abre de novo.
- Cada extensão com popup ganha um botão no cabeçalho da conta. Extensão nova só entra na página depois de
  recarregar a conta (⟳).
- **🎯 Sniper:** com o Rotom Sniper instalado, liga e desliga ele em todas as contas (ou uma por uma) pela
  barra de cima.

## Configurar

Quase tudo se configura pelas janelas do deck, e fica salvo sozinho em
`%LOCALAPPDATA%\PokeIdleMulti\deck.json`:

| Onde | O quê |
|---|---|
| Botão direito na conta, na barra de cima | Renomear, recarregar, som, desligar, lives nesta conta |
| **Lives** | Quais contas abrem lives; mostrar a janela das lives |
| **Lives → Canais…** | Canais da Twitch e da Kick (em ordem de prioridade), modo da Twitch e quantas lives da Kick abrir por conta |
| **🛒** no cabeçalho | Recompra da conta: pokebola, revives, poções, ouro mínimo |
| **⚡ ULTRA** (setinha) | ULTRA em todas as contas e o menos animações |
| **− Zoom +** | Zoom de todas as contas |
| **🧩 Extensões** | Extensões de cada conta |

A lista de fábrica da Twitch são os streamers oficiais do painel de bônus do jogo (outubro de 2026); confira
no jogo se mudou.

Os dados ficam em `%LOCALAPPDATA%\PokeIdleMulti`: o `deck.json` e os perfis das contas em `WebView2`. Para
levar o deck para outro PC, copie essa pasta inteira (com o deck fechado) ou entre de novo nas contas.

## Pontos de atenção

- **Não compartilhe a pasta `%LOCALAPPDATA%\PokeIdleMulti`**: ela tem os logins das suas contas (do jogo, da
  Twitch e da Kick).
- **Uma conta do jogo em um lugar só.** Abrir a mesma conta no deck e no navegador derruba uma das duas.
- **Conta parada na tela de login** vai juntando memória (o captcha do login se refaz sozinho). O cabeçalho
  avisa: entre na conta ou desligue no ⏻.
- **RAM:** cada live da Kick gasta uns 260 MB, e a barra de baixo mostra quanto o jogo, as lives e o navegador
  estão usando. Num notebook com 8 GB, abra poucas lives, use o ULTRA e desligue as contas que não usa.
- **O bônus das lives** vale para a conta do jogo vinculada à Twitch/Kick logada nas lives daquela conta.
  Marque em Lives só a conta vinculada.
- **Kick:** os pontos só contam com o vídeo tocando; o deck confere e recarrega, mas acompanhe o ▶ na barra de
  baixo.
- **Recompra gasta ouro de verdade.** Confira "Manter", "Recomprar abaixo de" e o ouro mínimo antes de ligar, e
  comece por uma conta só, olhando o registro. Ela só compra com a conta ligada no deck e dentro do jogo.
- **Login com o Google** pode não funcionar dentro do deck (o Google bloqueia alguns navegadores embutidos).
  Entre com e-mail e senha.
- **Antivírus/SmartScreen** podem estranhar um exe novo e sem assinatura. O código está todo aqui, e dá para
  compilar você mesmo.

## Compilar

Precisa do [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Dentro de `PokeIdleMulti`:

```
dotnet publish PokeIdleMulti.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

O exe sai em `publish\PokeIdleMulti.exe` (renomeie para `PokeIdleDeck.exe`). Com `--self-contained false` ele
fica com ~1,5 MB, mas aí o PC precisa do .NET 8 Desktop Runtime.

**Autoteste:** `PokeIdleDeck.exe --self-test` abre contas de teste (em `%LOCALAPPDATA%\PokeIdleMulti\selftest`,
sem mexer nas suas), confere tudo e grava o resultado em `%TEMP%\PokeIdleDeck-selftest.txt`. A janela dele
abre no monitor principal. Apague a pasta `selftest` antes de cada rodada.

### Onde está cada coisa

| Arquivo | O quê |
|---|---|
| `DeckForm.cs` | A janela, a barra de cima e a de baixo, os layouts, lives, voto e o autoteste |
| `AccountPane.cs` | Uma conta: o cabeçalho e a página do jogo |
| `DeckConfig.cs` | O `deck.json` |
| `LivesWindow.cs`, `TwitchPresence.cs`, `KickLive.cs`, `VideoQuality.cs`, `AdBlock.cs` | As lives da Twitch e da Kick |
| `Restock.cs`, `RestockWindow.cs`, `recompra.js` | A recompra (o script roda na página do jogo) |
| `Echo.cs` | O eco de cliques |
| `TopIdle.cs` | O voto no TopIdle |
| `ExtensionStore.cs`, `ExtensionsDialog.cs`, `StorageImport.cs`, `RotomSniper.cs` | As extensões |
| `Theme.cs` | Cores e ícones |

## Licença

[MIT](LICENSE).
