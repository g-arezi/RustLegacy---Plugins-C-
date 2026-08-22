# RustLegacy - Plugins C#

Coleção de plugins em C# (Oxide/uMod) para servidores **Rust Legacy**. Todos usam a classe base `RustLegacyPlugin`.

---

## 📋 Índice de Plugins

| Arquivo | Nome | Função principal |
|---|---|---|
| `DiscordLogs.cs` | Discord Logs | Envia logs gerais do servidor para o Discord |
| `KillFeed.cs` | Discord KillFeed | Envia feed de mortes/PvP para o Discord |
| `KeyStock.cs` | KeyStock | Sistema de keys/chaves de VIP resgatáveis |
| `reiniciar.cs` | AutoRestart | Reinício automático periódico do servidor |

---

## 1. DiscordLogs.cs — Discord Logs

**Versão:** 1.1.2

Envia os logs gerais do servidor para o Discord através de **webhooks**, um webhook independente por categoria.

### Categorias
- **Connections** — jogador conectou / desconectou (mostra nome, SteamID e IP).
- **Chat** — mensagens do chat global (remove tags de cor `[color]` antes de enviar).
- **Moderation** — bans, kicks e unbans, capturados via hooks (`cmdBan`, `cmdKick`, `cmdUnban`) disparados por plugins como KickBan/Banip.
- **Server** — servidor iniciado / encerrando, além do comando de teste.

### Configuração (`oxide/config/DiscordLogs.json`)
| Chave | Descrição |
|---|---|
| `Settings.Enabled` | Liga/desliga o plugin inteiro |
| `Settings.ServerName` | Nome exibido no rodapé dos embeds |
| `Settings.IgnoreLocalhostIP` | Ignora log de conexão vinda de `127.0.0.1` |
| `Webhooks.<Categoria>` | URL do webhook do Discord de cada categoria (vazio = desativado) |
| `Categories.<Categoria>` | `true`/`false` para ligar/desligar cada categoria individualmente |

### Comandos
- `/dlogs` *(admin)* — envia uma mensagem de teste para o webhook da categoria "Server".

---

## 2. KillFeed.cs — Discord KillFeed

**Versão:** 1.0.1

Envia um **killfeed** para o Discord via webhook, usando a API de mortes do plugin `Death.cs` (hooks `OnPlayerDeath`, `OnPlayerSuicide`, `OnAnimalDeath`, `OnStructureDestroyed`, `OnDeployableDestroyed`).

### Categorias (todas no mesmo webhook, cada uma pode ser ligada/desligada)
- **PvP** — jogador matou jogador (arma, parte do corpo e distância).
- **EvP** — animal matou jogador.
- **PvE** — jogador matou animal (caça).
- **Suicide** — jogador morreu por conta própria (queda, sangramento, radiação, água etc.).
- **Building** — estrutura ou deployable destruído.

### Estatísticas
Se `TrackStats` estiver ativo, o plugin grava kills/mortes por jogador usando o `PlayerDatabase` (se esse plugin estiver carregado), permitindo consultar K/D.

### Configuração (`oxide/config/KillFeed.json`)
| Chave | Descrição |
|---|---|
| `Settings.Enabled` | Liga/desliga o plugin inteiro |
| `Settings.ServerName` | Nome exibido no rodapé dos embeds |
| `Settings.TrackStats` | Se `true`, guarda kills/mortes por jogador via `PlayerDatabase` |
| `Webhook.Url` | URL do webhook do Discord usado para todas as categorias |
| `Categories.<Categoria>` | `true`/`false` para ligar/desligar cada categoria |

### Comandos
- `/kd` — mostra kills, mortes e K/D do próprio jogador (requer `PlayerDatabase` carregado e `TrackStats` ativo).

---

## 3. KeyStock.cs — KeyStock

**Versão:** 1.0

Sistema de **estoque de keys (chaves) de VIP**, com hash único e não reutilizável — baseado no padrão do `KeyManager.cs`. Pensado para integrar com uma loja/site externo que consome o arquivo `KeyStock_Estoque.json` (somente leitura) para saber quais chaves ainda estão disponíveis.

### Como funciona
- Cada chave é um hash SHA-256 gerado a partir de um salt aleatório + dados criptograficamente aleatórios, formatado em blocos (`XXXXX-XXXXX-XXXXX-XXXXX`).
- Cada chave está associada a um **tier** de VIP (ex.: `pvp`, `ouro`, `prata`, `platina`, `newisland`, `diamante` — configurável) e a uma quantidade de **dias** de duração.
- Os tiers e as permissões correspondentes (registradas via EasyChat ou similar) podem ser sobrescritos em `Config["Tiers"]`.
- Uma rotina roda a cada 5 minutos (`VerificarExpiracoes`) revogando automaticamente a permissão de VIPs cujas keys expiraram.
- Dados persistidos em `oxide/data/KeyStock.json` (interno) e `oxide/data/KeyStock_Estoque.json` (somente as chaves disponíveis, para consumo externo).

### Comandos
| Comando | Acesso | Descrição |
|---|---|---|
| `/gerarkey <tier> <dias> [quantidade]` | admin / permissão `keystock.admin` | Gera 1 ou mais keys (máx. 100 por vez) de um tier, por X dias. Exibe as keys geradas no console. |
| `/ativarkey <chave>` | qualquer jogador | Resgata uma key, concedendo a permissão do tier correspondente via `oxide.grant` |
| `/revogarkey <chave>` | admin / permissão `keystock.admin` | Revoga (invalida) uma key ainda não utilizada |
| `/minhavip` | qualquer jogador | Mostra as VIPs ativas do jogador e a data de expiração |
| `/estoque` | admin / permissão `keystock.admin` | Mostra quantas keys disponíveis existem por tier |

---

## 4. reiniciar.cs — AutoRestart

**Versão:** 1.0

Reinicia o servidor automaticamente em intervalos fixos.

### Comportamento
1. A cada **24000 segundos** (~6h40min, valor fixo na variável `AutoRestart`) o plugin:
   - Avisa no chat global e via popup que o servidor vai reiniciar em 60 segundos.
   - Executa `save.all` para salvar o mundo.
   - Envia um aviso `@everyone` para um webhook do Discord.
2. Após 60 segundos, executa `quit`, encerrando o processo do servidor (deve ser reiniciado por um script/serviço externo, ex.: um restart automático do host).


### Configuração
Não possui arquivo de configuração — os valores (`AutoRestart`, `SystemName`, `CordSupport`) estão fixos no código e exigem recompilar o plugin para alterar.

---

## Requisitos gerais

- Servidor **Rust Legacy** com **Oxide/uMod**.
- `DiscordLogs.cs` e `KillFeed.cs` dependem apenas do `webrequest` nativo do Oxide (sem dependências entre si).
- `KillFeed.cs` opcionalmente integra com o plugin **PlayerDatabase** para estatísticas de K/D.
- `DiscordLogs.cs` opcionalmente integra com plugins de moderação (ex.: **KickBan**/**Banip**) via hooks `cmdBan`/`cmdKick`/`cmdUnban`.
- `KeyStock.cs` opcionalmente integra com **EasyChat** (ou equivalente) para as permissões de cada tier de VIP.
