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
   **Lives → Mostrar as lives**, entre na Twitch e na Kick uma vez. Em **Lives → Config…** ficam os canais e
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
  🛒 recompra, ⚔ fila de PvP, 🗳 voto no TopIdle e os botões das extensões. Ao lado do nome aparece há quanto tempo a conta
  está rodando e os avisos (por exemplo, "na tela de login"). Com as contas em colunas, o cabeçalho se aperta
  para nenhum botão sumir; se ainda faltar espaço, o ⚔ e o 🛒 desligados saem dele (os dois ficam no botão
  direito da conta, na barra de cima).
- **Jogo que não abre:** se a página carrega mas o jogo fica na tela de abertura (o logo com a barra de carga),
  sem conectar nem mostrar o login, em 60 s o deck recarrega a conta sozinho; se de novo não abrir, espera mais a
  cada vez (2 min, 3 min… até 5 min). Página que nem carregou (sem internet, site fora do ar) também é tentada de
  novo. Cada recarga vai para `aberturas.log`, na pasta do deck, com onde o jogo parou (a % da carga).
- **Janelas no tema do deck:** as de configuração (Lives → Config…, recompra,
  registro dos resgates, extensões, resgate em lote) e as caixas de aviso são escuras, com cantos arredondados e
  botões em português com o verbo (os que apagam ou gastam em vermelho). O registro de cada janela fica recolhido:
  abre no **▸ Registro**. As explicações ficam nas dicas (passe o mouse no ⓘ).
- **Zoom:** **− Zoom 100% +** na barra de cima muda todas as contas juntas (na setinha, de 50% a 150%).
  Ampliar uma conta põe o zoom em 100%; voltar para a grade ou as colunas devolve o zoom de antes (por
  exemplo, 50% para as 4 contas caberem na tela).
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

A cada segundo o deck confere quanto a conta tem. Quando um item fica abaixo de "quando tiver menos de", compra
o lote de "Comprar" no **Market do NPC**, uma vez, e só volta a comprar quando cair abaixo de novo (comprar 30.000
quando tiver menos de 5.000 fica com uns 35.000). Os valores de fábrica são pequenos, para quem está começando
(Poke Ball 500 abaixo de 100, revive 10 abaixo de 3, poção 100 abaixo de 30, ouro mínimo 10.000): aumente conforme
o ouro. O deck.json de antes ("Manter") passa sozinho: a recompra nunca configurada fica com os de fábrica; a
configurada compra o que levava ao "Manter" (Manter − abaixo de).

- **Pokebola:** a que você escolher (Poke Ball de fábrica).
- **Revives:** contados juntos; compra sempre o mais barato.
- **Poções:** contadas juntas; compra a de **melhor custo-benefício** de fábrica (o ouro por HP que ela cura de verdade
  no pokémon ativo: como a automação "Usar Poções" do jogo só usa a poção abaixo do limiar, 30% de fábrica, cura além
  do que falta se perde), ou a que você escolher. A recompra nunca configurada (desligada, com a Ultimate Potion, a de
  fábrica de antes) passa para o custo-benefício. A Golden Potion (cura % do HP) está na lista e conta com as poções.
- Opcional: pôr a poção escolhida em 1º na fila do "Usar Poções", para ela ser gasta primeiro.
- **Automações do jogo:** ligada, a recompra marca nas Automações do jogo o que ela compra: "Usar Poções", "Usar
  Revive" (com o revive dela entre os escolhidos) e, com VIP (o jogo só lança sozinho para VIP), "Lançar sem parar"
  com a pokebola dela em 1º. Desmarcou no jogo com a recompra ligada: ela marca de novo.
- **Ouro mínimo:** nunca compra a ponto de o ouro ficar abaixo dele. Sem ouro para o lote inteiro, espera juntar
  (não compra aos poucos); a exceção é poção ou revive zerado, que compra o que o ouro deixar. Se o ouro não der
  para tudo, primeiro poção, depois revive, depois pokebola.
- O jogo aceita até 9.999 por pedido: um lote maior sai em pedidos seguidos, um a cada 4 s, pelo mesmo pedido do
  botão Comprar do jogo. Se o jogo recusar,
  espera 30 s; 3 compras seguidas sem resposta dão 2 min de pausa.
- Cada conta tem a sua configuração. A janela mostra o estoque, o ouro e o registro das compras; o carrinho
  do cabeçalho fica verde quando está ligada e com o jogo conectado.

### ⚔ Fila de PvP

O ⚔ no cabeçalho de cada conta (e no clique direito da conta na barra) liga a fila do PvP ranqueado: a conta entra na
fila e volta depois de cada partida, esperando o tempo que o jogo pede entre elas; desligar tira da fila. Conta VIP: o
deck liga também a auto-fila do próprio jogo (que segue com a página fechada). O ⚔ fica verde na fila, amarelo
esperando e apagado desligado; a dica tem há quanto tempo está na fila, as partidas desde que ligou (vitórias e
derrotas) e, se não conectar, o motivo. Sem VIP quem volta para a fila é o deck: o jogo reserva a auto-fila para VIP.

**Botão direito no ⚔** (ou "⚔ Fila de PvP" no menu da conta): como as partidas da fila aparecem — **mostrada** (o
replay na tela), **minimizada** (a janelinha do canto) ou **escondida** (de fábrica: o jogo só avisa o resultado) — e,
para mostrada e minimizada, a **velocidade**: a do jogo (acelera só para caber até a próxima) ou 1×, 2×, 4×, 8×, 16×,
32×. Vale por conta (deck.json `PvpScreen`/`PvpSpeed`; "Usar nas outras contas" copia), com a fila ligada, a partir da
próxima partida. A tela é a "Tela da batalha" do PvP do jogo, que o deck mantém na escolhida. A velocidade: o replay do
jogo não guarda nenhuma, então o deck pega as classes dele (`/replay.mjs`, `/replay-tabuleiro.mjs`, com um `import` do
mesmo endereço que o jogo já baixou, que devolve o mesmo módulo) e, no primeiro play do replay aberto junto com uma
partida da fila, põe a velocidade; os botões de velocidade do replay seguem valendo. Replays que a pessoa abre no jogo
não mudam. O Battery Saver (Modo Economia) do jogo esconde as partidas de qualquer jeito: com a fila ligada e a luta
mostrada ou minimizada, o deck desliga ele pelo "Sair" do próprio jogo e, ao voltar para Escondida ou desligar a fila,
religa (só se foi o deck que desligou; localStorage `deckPvpSemEconomia`). A tela ULTRA também esconde: o deck só avisa.

### ⟳ Recarregar limpo

O ⟳ no cabeçalho de cada conta (e "Recarregar limpo" no clique direito dela) apaga o cache de disco do perfil da conta
e, só do site do jogo, o Cache Storage e os service workers, e recarrega sem o cache. O login (que o jogo guarda no
localStorage), os cookies da Twitch e da Kick e as extensões ficam. O vigia do jogo que não abre (a conta presa na tela
de abertura por mais de 60 s depois de carregar a página) recarrega normal na 1ª vez e limpo da 2ª em diante. Ele não
recarrega com o aviso "conta aberta em outro lugar" à vista (tomaria a sessão do outro aparelho) nem com um aviso da conta
(limite de contas no mesmo IP, sessão inválida): só anota no cabeçalho. Depois que o jogo abre, só volta a vigiar na
próxima vez que a página carregar.

### Apagar os dados da conta

Para a conta que continua travada depois da carga mesmo com o ⟳: o jogo guarda o login e o id do aparelho no
localStorage, e o recarregar limpo deixa os dois. No clique direito da conta (barra de cima), **Apagar os dados da conta
(entrar de novo)…** sai do jogo, apaga e abre o jogo na tela de login:

- **Apagar o jogo:** tudo o que o site do jogo guarda na conta (localStorage, IndexedDB, cookies, os do Cloudflare
  também, Cache Storage e service workers) e o cache de disco do perfil. A Twitch e a Kick continuam logadas.
- **Apagar tudo:** o mesmo e os cookies de todos os sites: sai também da Twitch e da Kick (as lives da conta reabrem
  pedindo o login).

As extensões, as configurações delas e as do deck (nome, recompra, fila de PvP) ficam.

### 🩺 Diagnóstico: o jogo que não carrega

Para quando o jogo não carrega num PC onde não dá para olhar. O deck anota, por conta, em
`%LOCALAPPDATA%\PokeIdleMulti\diagnostico\contaN.log`, o caminho da página até o jogo abrir: a navegação (o HTTP, o
Cloudflare, o relógio do PC comparado com o do site), os arquivos do jogo e os pedidos que falharam ou voltaram com erro,
o socket do jogo (abriu, conectou, fechou), os erros de JavaScript e do console, cada etapa da tela de abertura e os
processos do navegador que caíram. Quando o vigia recarrega, anota antes um retrato da página (o que está à vista, a
etapa da carga, os scripts, o WebGL) e salva um print. Depois que o jogo abre, só as falhas. O `deck.log` da mesma pasta
tem a abertura do deck (Windows e WebView2) e os erros que derrubaram o deck.

**🩺 Gerar diagnóstico** (no canto direito da barra de baixo, ou no clique direito da conta) junta tudo num .zip na Área
de trabalho, com um relatório do PC (Windows, memória, placa de vídeo, proxy, arquivo hosts, antivírus, WebView2), da
rede fora do navegador (o DNS e o site do jogo) e de cada conta agora (status, extensões, um retrato e um print). É
esse arquivo que a pessoa manda. Nada de senha, token, cookie ou valor do localStorage (só os nomes das chaves), e os
endereços vão sem o `?…`.

### Lives para o bônus de XP

- **Twitch:** desde 04/11/2026 o bônus da Twitch sai dos pontos do canal (300 XP, 500 Captura, 1000 Shiny),
  que só vêm com a live tocando; estar no chat não conta mais. O deck abre o player das primeiras lives da
  lista que estiverem no ar, até o número escolhido em Lives → Config… (2 de fábrica), e confere a cada 2 min
  quem está no ar. Cada live toca só o player da Twitch (o "player em janela" do site), com os mesmos cortes da
  Kick. Sem o login da Twitch na conta, abre só a página de entrar; os players vêm depois que a conta entra.
- **Baú de bônus da Twitch** (ligado de fábrica): a cada 2 min o deck pega o baú (+50 a cada ~15 min) de cada
  live da Twitch aberta, o que quase triplica os pontos. Pegar o baú passa pela checagem anti-robô da Twitch,
  que o deck faz de dentro do player; se ela barrar, o menu Lives avisa ao lado da live.
- **Kick:** abre as primeiras lives da lista que estiverem no ar, até o número escolhido em Lives → Config…
  (2 de fábrica: testado, a Kick só dá pontos em 2 lives ao mesmo tempo por conta). Live offline não fica
  aberta. Cada live toca só o player da Kick (player.kick.com), que rende os pontos igual à página do canal e
  gasta uns 40% menos; o vídeo vem na menor qualidade, sem propaganda nem rastreadores, numa janela fora da
  tela. A cada 30 s o deck confere se cada live está tocando e recarrega a que travou ou caiu.
- **Lives leves (sempre, desde 12/11/2026):** o player da Twitch abre parado e o da Kick em branco (abrem só para o baú
  e os resgates), e quem assiste é o deck, sem navegador. Rende igual e gasta quase nada. Saiu a opção de cada conta
  em Lives → Config…; o player com vídeo segue no código, para o caso de precisar de novo.
- **Botão lives** de cada conta (no cabeçalho): escolher qual live da Kick ou da Twitch fechar nela (ou todas de
  um site), e abrir de novo. A vaga de uma live fechada não abre outra sozinha. O botão fica marcado enquanto
  houver live fechada.
- **💰 Pontos da Kick:** abre a página de cada canal da conta, lado a lado, só com o rodapé do chat: o botão
  dos pontos e o espaço do popup de escolher o bônus. Essas páginas só existem enquanto o 💰 está aberto;
  fechar devolve a memória, e o player continua rendendo. A altura da janela muda arrastando a borda, e o deck
  guarda. "Próxima conta ▶" passa para a conta seguinte; "Página inteira" mostra a página do canal toda.
- **💰 Pontos da Twitch:** o mesmo, com o chat em janela de cada live da Twitch da conta (o botão dos pontos e o
  baú ficam embaixo, ao lado do campo de mensagem). As mensagens ficam escondidas; "Página inteira" troca o
  chat pelo site inteiro do canal, com o menu da conta (sair da Twitch, tela cheia…), como na Kick. Desligar
  volta ao chat em janela.
- **Registro dos resgates** (Lives → Registro dos resgates…): cada resgate, cada baú, cada recusa e cada vez que
  os pontos voltaram, com a hora, a conta, o site e o canal, o mais novo em cima. Fica em
  `%LOCALAPPDATA%\PokeIdleMulti\resgates.log`, de uma vez para outra.
- **Resgate automático** (Lives → Config…, uma linha por conta, com uma escolha para a Kick e outra para a
  Twitch, nenhuma de fábrica): o bônus do PokeIdle que cada conta resgata em cada site (+15% XP por 300 pontos,
  Captura por 500 ou Shiny por 1000). O deck confere o saldo de cada live aberta nessas contas (Kick a cada
  5 min, Twitch a cada 2), pelo player e com o login da conta, e resgata 1 bônus naquele canal se der. As horas
  acumulam no jogo. O saldo e o último resgate aparecem no menu Lives, ao lado de cada live. Resgate recusado:
  tenta de novo naquele canal em 30 min. Se os pontos voltarem (site não vinculado no jogo), o deck para de
  resgatar naquela conta e avisa; salvar Lives → Config… de novo volta a resgatar.
- **Guarda do bônus** (Lives → Config…, coluna "Guarda", ligada de fábrica em todas as contas): o resgate automático
  só usa os pontos das lives abertas, e quando os streamers delas saem do ar o bônus vence com pontos parados em outros
  canais. Quando o bônus escolhido da conta num site vence em menos de 30 min (ou venceu), o deck lê o saldo dos canais da
  lista e dos oficiais do jogo que não estão abertos na conta, escolhe um com os pontos (offline primeiro; depois o de
  mais pontos) e resgata 1 ali, numa página escondida do canal. Depois confere no jogo se o tempo do bônus subiu; se não
  subir (site não vinculado), para a guarda naquele site por 1 h e diz no registro. Sem canal com os pontos, avisa uma vez.
  O tempo que falta de cada bônus fica na dica do cabeçalho da conta.
- **Bônus pausado no canal:** cada bônus vem em 4 durações (1, 3, 5 e 10 h, com o preço proporcional). O deck
  resgata o mais barato **liberado**: com o de 1 h pausado pelo canal, vai o de 3, 5 ou 10 h (o mesmo por hora).
  Com todas pausadas, não resgata, mostra "pausado" ao lado do saldo no menu Lives e diz no registro, uma vez,
  por que o saldo não está sendo usado.
- **Pontos gastos fora do deck:** se o saldo de um canal cai sem resgate do deck (a conta aberta em outro
  aparelho ou programa, ou um resgate na mão), o registro diz quanto caiu. Se foi um resgate do deck cuja
  resposta se perdeu (o player não respondeu), o registro diz que ele passou.

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
| Botão direito na conta, na barra de cima | Renomear, apagar os dados (entrar de novo), recarregar, som, desligar, lives nesta conta, diagnóstico, recompra, fila de PvP |
| **Lives** | Quais contas abrem lives; mostrar a janela das lives |
| **Lives → Config…** | Canais da Twitch e da Kick (em ordem de prioridade), quantas lives da Twitch e da Kick abrir por conta, o resgate automático dos pontos (qual bônus cada conta resgata em cada site) e o baú da Twitch |
| **🛒** no cabeçalho | Recompra da conta: pokebola, revives, poções, ouro mínimo |
| **⚔** no cabeçalho | Fila de PvP da conta (clique); botão direito: como as partidas aparecem e a velocidade |
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
- **RAM e CPU:** medido em 04/11/2026 (`PokeIdleMulti.exe --consumo`), cada player da Twitch ou da Kick gasta
  ~9% de um núcleo; a 1ª live de cada site numa conta ~280 MB, e cada uma a mais ~115 MB. As lives de um site
  numa conta dividem um processo. Com o 💰 aberto, a página de cada canal soma mais uns 200 MB até fechar. A
  barra de baixo mostra quanto o jogo, as lives e o navegador estão usando. Num notebook com 8 GB, abra poucas lives, use o ULTRA e desligue as contas que não usa.
- **O bônus das lives** vale para a conta do jogo vinculada à Twitch/Kick logada nas lives daquela conta.
  Marque em Lives só a conta vinculada.
- **Kick:** os pontos só contam com o vídeo tocando; o deck confere e recarrega, mas acompanhe o ▶ na barra de
  baixo.
- **Recompra gasta ouro de verdade.** Confira "Comprar", "quando tiver menos de" e o ouro mínimo antes de ligar, e
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
| `LivesWindow.cs`, `TwitchLive.cs`, `KickLive.cs`, `VideoQuality.cs`, `AdBlock.cs` | As lives da Twitch e da Kick |
| `ConsumptionTest.cs` | O teste de consumo das lives (`PokeIdleMulti.exe --consumo`) |
| `Restock.cs`, `RestockWindow.cs`, `recompra.js` | A recompra (o script roda na página do jogo) |
| `Pvp.cs`, `pvp.js` | A fila de PvP, a tela e a velocidade das partidas (o script roda na página do jogo) |
| `BonusGuard.cs`, `bonus.js` | A guarda do bônus das lives (o bonus.js lê no jogo o tempo que falta do bônus) |
| `Diag.cs` | O 🩺 diagnóstico do jogo que não carrega (os logs por conta e o .zip para mandar) |
| `Echo.cs` | O eco de cliques |
| `TopIdle.cs` | O voto no TopIdle |
| `ExtensionStore.cs`, `ExtensionsDialog.cs`, `StorageImport.cs`, `RotomSniper.cs` | As extensões |
| `Theme.cs` | Cores e ícones |

## Licença

[MIT](LICENSE).
