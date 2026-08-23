using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Player Verify", "oArezi", "1.1.0")]
    [Description("Exige verificação por código antes de liberar o jogador. Hoje só via Discord (por polling REST, sem WebSocket); a estrutura já reserva um segundo método por telefone/SMS para quando um gateway for escolhido.")]
    public class PlayerVerify : RustPlugin
    {
        #region Config

        private PluginConfig config;

        private class PluginConfig
        {
            public DiscordSection Discord = new DiscordSection();
            public PhoneSection Phone = new PhoneSection();
            public int ActivationSeconds = 120;

            public class DiscordSection
            {
                public bool Enabled = true;
                public string ApiToken = "";
                public string ChannelId = "0";
                public int MinAccountAgeDays = 30;
                public string CommandTrigger = "!verificar";
                public float PollIntervalSeconds = 3f;
            }

            // Reservado para quando um gateway de SMS for escolhido (Twilio, Zenvia,
            // TotalVoice, AWS SNS, etc). Enquanto Enabled = false, nenhum código é
            // gerado por telefone e o jogador só vê a instrução do Discord.
            // Para implementar: preencher SendSmsCode() na região "Phone verification"
            // abaixo com a chamada HTTP do provedor escolhido (mesmo padrão do
            // PollDiscordMessages/webrequest usado pro Discord).
            public class PhoneSection
            {
                public bool Enabled = false;
                public string Provider = "";
                public string ApiKey = "";
                public string SenderId = "";
            }
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private void LoadConfigValues()
        {
            config = Config.ReadObject<PluginConfig>();
        }

        #endregion

        #region Lang

        private const string PermBypass = "playerverify.bypass";
        private const string PermAdmin = "playerverify.admin";
        // Concedida ao jogador (por SteamID) assim que ele completa a verificação -
        // outros plugins/kits/grupos podem usar essa permissão como gate pra "só
        // jogador verificado" (ex: oxide.grant não é preciso rodar manualmente, o
        // plugin já cuida disso em CompleteVerification/ao carregar dados salvos).
        private const string PermVerified = "playerverify.verified";

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["InstructionsDiscord"] =
                    "<color=#7289DA>[Verificação]</color> Entre no nosso Discord (discord.gg/2Q3D5pruSM) e use o comando <color=#7289DA>{0} {1}</color> em até <color=#ff5555>{2} segundos</color>. Você não pode se mover até confirmar.",
                ["KickTimeout"] = "Verificação não concluída a tempo.",
                ["VerifySuccess"] = "<color=#7289DA>[Verificação]</color> Verificação concluída! Você já pode se mover.",
                ["InteractionAccountTooNew"] = "Sua conta do Discord precisa ter pelo menos {0} dia(s) de uso para verificar.",
                ["InteractionInvalidCode"] = "Código inválido ou expirado. Conecte-se novamente ao servidor para gerar um novo código.",
                ["InteractionSuccess"] = "Verificação concluída! Volte ao jogo, você já pode se mover.",
                ["ResetSuccess"] = "Verificação de {0} foi resetada.",
                ["NoMethodConfigured"] = "Nenhum método de verificação está configurado (Discord/Telefone). Avise um administrador.",
            }, this);
        }

        private string Lang(string key, string playerId = null, params object[] args) =>
            string.Format(lang.GetMessage(key, this, playerId), args);

        #endregion

        #region Data

        private class VerifiedRecord
        {
            public string Method; // "discord" | "phone"
            public string DiscordId;
            public string PhoneNumber;
            public long VerifiedAt;
        }

        private Dictionary<string, VerifiedRecord> verified = new Dictionary<string, VerifiedRecord>();

        // Nome de arquivo próprio (não usar `Name` puro) porque `Name` também é o
        // nome usado pelo config em oxide/config/PlayerVerify.json - se alguém colar
        // esse mesmo json na pasta oxide/data por engano (já aconteceu), o plugin
        // tentava desserializar o config inteiro como se fosse a lista de jogadores
        // verificados e derrubava o Init(). Prefixar evita a colisão de nome, e o
        // try/catch garante que um data file corrompido não trave o plugin de novo -
        // ele só reseta os dados salvos e avisa no console.
        private const string DataFileName = "PlayerVerify_verified";

        private void LoadData()
        {
            try
            {
                verified = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, VerifiedRecord>>(DataFileName)
                           ?? new Dictionary<string, VerifiedRecord>();
            }
            catch (Exception ex)
            {
                PrintError($"Falha ao carregar {DataFileName}.json, iniciando com dados vazios: {ex.Message}");
                verified = new Dictionary<string, VerifiedRecord>();
            }
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(DataFileName, verified);

        private bool IsVerified(string steamId) => verified.ContainsKey(steamId);

        #endregion

        #region State

        private class PendingVerification
        {
            public string Code;
            public Timer KickTimer;
        }

        private readonly Dictionary<ulong, PendingVerification> pending = new Dictionary<ulong, PendingVerification>();
        private readonly HashSet<ulong> frozen = new HashSet<ulong>();
        // Posição de quando o freeze começou - além de engolir os botões de
        // movimento (o que barra andar/pular/agachar normalmente), teleporta o
        // jogador de volta se a posição dele se afastar disso, cobrindo empurrão por
        // outro jogador, queda, ragdoll ou qualquer outro jeito de se deslocar que não
        // passe pelos botões de input.
        private readonly Dictionary<ulong, Vector3> frozenPosition = new Dictionary<ulong, Vector3>();

        // Maior id de mensagem já processado no canal, pra não reprocessar a mesma
        // mensagem em todo poll (ids do Discord são snowflakes, crescem no tempo,
        // então dá pra comparar como número mesmo vindo como string).
        private ulong lastSeenMessageId = 0;
        private Timer pollTimer;
        private Timer freezeEnforceTimer;

        private bool DiscordConfigured =>
            config.Discord.Enabled && !string.IsNullOrEmpty(config.Discord.ApiToken) && config.Discord.ChannelId != "0";

        private bool PhoneConfigured => config.Phone.Enabled && !string.IsNullOrEmpty(config.Phone.ApiKey);

        private bool AnyMethodConfigured => DiscordConfigured || PhoneConfigured;

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermBypass, this);
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermVerified, this);
            LoadConfigValues();
            LoadData();
            SyncVerifiedPermissions();
        }

        // Garante que todo mundo já verificado (persistido em oxide/data) tenha a
        // permissão concedida, mesmo se oxide.users.data tiver sido zerado/editado à
        // mão separadamente dos dados de verificação.
        private void SyncVerifiedPermissions()
        {
            foreach (string steamId in verified.Keys)
            {
                if (!permission.UserHasPermission(steamId, PermVerified))
                    permission.GrantUserPermission(steamId, PermVerified, this);
            }
        }

        private void OnServerInitialized()
        {
            if (!AnyMethodConfigured)
            {
                PrintWarning("Nenhum método de verificação configurado em oxide/config/PlayerVerify.json (Discord.ApiToken/ChannelId ou Phone.ApiKey). O plugin não vai travar ninguém até isso ser configurado.");
                return;
            }

            if (DiscordConfigured)
            {
                pollTimer = timer.Every(Math.Max(1f, config.Discord.PollIntervalSeconds), PollDiscordMessages);
            }

            freezeEnforceTimer = timer.Every(0.1f, EnforceFreezePositions);

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsSleeping()) continue;
                if (permission.UserHasPermission(player.UserIDString, PermBypass)) continue;
                if (IsVerified(player.UserIDString)) continue;
                StartVerification(player);
            }
        }

        private void Unload()
        {
            foreach (var entry in pending.Values)
                entry.KickTimer?.Destroy();

            pollTimer?.Destroy();
            freezeEnforceTimer?.Destroy();
        }

        #endregion

        #region Discord (REST polling - sem WebSocket/gateway)

        // Por que polling REST em vez do gateway (WebSocket) do Oxide.Ext.Discord:
        // nesse servidor o Mono/UnityTls embarcado no RustDedicated (build de 2021) não
        // consegue completar o handshake TLS do WebSocket do Discord (fecha com close
        // code 1015 / UNITYTLS_X509VERIFY_NOT_DONE), mesmo o Windows validando o mesmo
        // certificado sem problema fora do Mono. A chamada REST comum, porém, funciona
        // (testado: GET /api/v9/gateway responde normalmente). Então em vez de manter
        // uma conexão de gateway em tempo real, o plugin consulta periodicamente as
        // últimas mensagens do canal configurado via REST simples (webrequest, a
        // biblioteca HTTP nativa do Oxide) e procura por "!verificar <código>" nelas.
        // Isso também elimina a dependência da extensão Oxide.Ext.Discord inteira.
        private const string ApiBase = "https://discord.com/api/v10";

        private class DiscordApiUser
        {
            public string id;
            public string username;
            public bool bot;
        }

        private class DiscordApiMessage
        {
            public string id;
            public string content;
            public DiscordApiUser author;
        }

        private Dictionary<string, string> DiscordHeaders => new Dictionary<string, string>
        {
            ["Authorization"] = $"Bot {config.Discord.ApiToken}",
            ["Content-Type"] = "application/json",
        };

        private bool pollInFlight;

        private void PollDiscordMessages()
        {
            if (pollInFlight) return; // não empilha requisição se a anterior ainda não voltou
            if (pending.Count == 0) return; // sem ninguém esperando, não gasta chamada de API

            pollInFlight = true;

            string url = $"{ApiBase}/channels/{config.Discord.ChannelId}/messages?limit=20";
            webrequest.Enqueue(url, null, OnMessagesFetched, this, RequestMethod.GET, DiscordHeaders);
        }

        private void OnMessagesFetched(int code, string response)
        {
            pollInFlight = false;

            if (code != 200 || string.IsNullOrEmpty(response))
            {
                if (code == 401 || code == 403)
                    PrintError($"Discord REST retornou {code} ao ler mensagens - confira Discord.ApiToken e se o bot tem acesso ao canal {config.Discord.ChannelId}.");
                return;
            }

            List<DiscordApiMessage> messages;
            try
            {
                messages = JsonConvert.DeserializeObject<List<DiscordApiMessage>>(response);
            }
            catch (Exception ex)
            {
                PrintError($"Falha ao interpretar resposta do Discord: {ex.Message}");
                return;
            }

            if (messages == null || messages.Count == 0) return;

            // A API devolve as mensagens mais novas primeiro; processa em ordem
            // cronológica e só considera mensagens depois da última já vista.
            messages.Reverse();

            ulong highestSeen = lastSeenMessageId;
            foreach (var message in messages)
            {
                ulong messageId;
                if (!ulong.TryParse(message.id, out messageId)) continue;
                if (messageId <= lastSeenMessageId) continue;
                if (messageId > highestSeen) highestSeen = messageId;

                HandleMessage(message, messageId);
            }

            lastSeenMessageId = highestSeen;
        }

        private void HandleMessage(DiscordApiMessage message, ulong messageId)
        {
            if (message?.author == null || message.author.bot) return;

            string content = message.content?.Trim();
            if (string.IsNullOrEmpty(content) || !content.StartsWith(config.Discord.CommandTrigger, StringComparison.OrdinalIgnoreCase))
                return;

            string code = content.Substring(config.Discord.CommandTrigger.Length).Trim();

            ulong authorSnowflake;
            if (!ulong.TryParse(message.author.id, out authorSnowflake)) return;

            TimeSpan accountAge = DateTimeOffset.UtcNow - SnowflakeToTimestamp(authorSnowflake);
            if (accountAge.TotalDays < config.Discord.MinAccountAgeDays)
            {
                ReplyToChannel(messageId, Lang("InteractionAccountTooNew", null, config.Discord.MinAccountAgeDays));
                return;
            }

            ulong steamId = 0;
            if (!string.IsNullOrEmpty(code))
            {
                foreach (var entry in pending)
                {
                    if (entry.Value.Code == code)
                    {
                        steamId = entry.Key;
                        break;
                    }
                }
            }

            if (steamId == 0)
            {
                ReplyToChannel(messageId, Lang("InteractionInvalidCode"));
                return;
            }

            CompleteVerification(steamId, "discord", message.author.id, null);
            ReplyToChannel(messageId, Lang("InteractionSuccess"));
        }

        // Epoch do Discord (2015-01-01T00:00:00Z) somado ao timestamp embutido nos
        // primeiros 42 bits do snowflake - fórmula oficial da documentação do Discord.
        private static DateTimeOffset SnowflakeToTimestamp(ulong snowflake)
        {
            long unixMs = (long)(snowflake >> 22) + 1420070400000L;
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }

        private void ReplyToChannel(ulong replyToMessageId, string text)
        {
            string url = $"{ApiBase}/channels/{config.Discord.ChannelId}/messages";
            string body = JsonConvert.SerializeObject(new
            {
                content = text,
                message_reference = new { message_id = replyToMessageId.ToString() }
            });

            webrequest.Enqueue(url, body, (code, response) =>
            {
                if (code != 200 && code != 201)
                    PrintError($"Falha ao responder no Discord (HTTP {code}): {response}");
            }, this, RequestMethod.POST, DiscordHeaders);
        }

        #endregion

        #region Phone verification (TODO: plugar gateway de SMS)

        // Quando um provedor for escolhido, chamar isto a partir de StartVerification()
        // (ou de um comando/UI que peça o telefone do jogador) para disparar o SMS com o
        // mesmo `code` já gerado para o steamId. Use webrequest.Enqueue igual
        // PollDiscordMessages/ReplyToChannel fazem pro Discord e, na resposta de
        // sucesso, não precisa fazer nada além de aguardar o jogador confirmar o
        // código (por comando de chat, por exemplo). A confirmação deve terminar
        // chamando CompleteVerification(steamId, "phone", null, numeroDoTelefone).
        private void SendSmsCode(ulong steamId, string phoneNumber, string code)
        {
            PrintWarning($"Verificação por telefone ainda não está implementada (Phone.Enabled={config.Phone.Enabled}). Configure um provedor em oxide/config/PlayerVerify.json e implemente SendSmsCode().");
        }

        #endregion

        #region Verification flow

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null) return;
            if (!AnyMethodConfigured) return;
            if (pending.ContainsKey(player.userID)) return;
            if (permission.UserHasPermission(player.UserIDString, PermBypass)) return;
            if (IsVerified(player.UserIDString)) return;

            StartVerification(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            CancelPending(player.userID);
        }

        private void StartVerification(BasePlayer player)
        {
            string code = GenerateCode();
            pending[player.userID] = new PendingVerification
            {
                Code = code,
                KickTimer = timer.Once(config.ActivationSeconds, () => OnVerificationTimeout(player.userID))
            };
            frozen.Add(player.userID);
            frozenPosition[player.userID] = player.transform.position;

            if (DiscordConfigured)
            {
                Player.Message(player, Lang("InstructionsDiscord", player.UserIDString,
                    config.Discord.CommandTrigger, code, config.ActivationSeconds));
            }
            else
            {
                Player.Message(player, Lang("NoMethodConfigured", player.UserIDString));
            }

            // Quando o telefone estiver implementado: se PhoneConfigured e o jogador já
            // tiver um número associado (ex: vindo de um comando anterior), chamar
            // SendSmsCode(player.userID, numero, code) aqui também, como segunda opção
            // ao lado da instrução do Discord.
        }

        private void OnVerificationTimeout(ulong steamId)
        {
            if (!pending.ContainsKey(steamId)) return;

            pending.Remove(steamId);
            frozen.Remove(steamId);
            frozenPosition.Remove(steamId);

            BasePlayer player = BasePlayer.FindByID(steamId);
            if (player != null && player.IsConnected)
                player.Kick(Lang("KickTimeout", player.UserIDString));
        }

        private void CancelPending(ulong steamId)
        {
            PendingVerification entry;
            if (pending.TryGetValue(steamId, out entry))
            {
                entry.KickTimer?.Destroy();
                pending.Remove(steamId);
            }
            frozen.Remove(steamId);
            frozenPosition.Remove(steamId);
        }

        private void CompleteVerification(ulong steamId, string method, string discordId, string phoneNumber)
        {
            CancelPending(steamId);

            string steamIdString = steamId.ToString();

            verified[steamIdString] = new VerifiedRecord
            {
                Method = method,
                DiscordId = discordId,
                PhoneNumber = phoneNumber,
                VerifiedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            SaveData();

            if (!permission.UserHasPermission(steamIdString, PermVerified))
                permission.GrantUserPermission(steamIdString, PermVerified, this);

            BasePlayer player = BasePlayer.FindByID(steamId);
            if (player != null)
                Player.Message(player, Lang("VerifySuccess", player.UserIDString));
        }

        private string GenerateCode()
        {
            string code;
            do
            {
                code = UnityEngine.Random.Range(100000, 999999).ToString();
            } while (pending.Values.Any(p => p.Code == code));

            return code;
        }

        #endregion

        #region Movement lock

        private object OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input?.current == null) return null;
            if (!frozen.Contains(player.userID)) return null;

            // SwallowButton sozinho não é suficiente nesse build (jogador ainda
            // conseguia andar) - zera o bitmask de botões inteiro pra garantir que
            // nenhum comando de movimento/pulo/agachar passe, além dos SwallowButton
            // individuais (cobre outros hooks que só olham SwallowButton).
            input.current.buttons = 0;
            input.SwallowButton(BUTTON.FORWARD);
            input.SwallowButton(BUTTON.BACKWARD);
            input.SwallowButton(BUTTON.LEFT);
            input.SwallowButton(BUTTON.RIGHT);
            input.SwallowButton(BUTTON.JUMP);
            input.SwallowButton(BUTTON.DUCK);
            input.SwallowButton(BUTTON.SPRINT);

            // Trava de posição: cobre deslocamento que não vem dos botões (empurrão,
            // queda, ragdoll etc). Só reposiciona se realmente saiu do lugar, pra não
            // ficar chamando Teleport toda hora à toa.
            Vector3 anchor;
            if (frozenPosition.TryGetValue(player.userID, out anchor) &&
                (player.transform.position - anchor).sqrMagnitude > 0.01f)
            {
                player.Teleport(anchor);
            }

            return null;
        }

        // Checagem redundante por timer (roda a cada 100ms independente do hook de
        // input) - se por qualquer motivo OnPlayerInput não pegar um deslocamento a
        // tempo (timing do hook nesse build, física, etc), isso reposiciona de
        // qualquer forma. Só custa algo quando tem gente congelada (frozen.Count).
        private void EnforceFreezePositions()
        {
            if (frozen.Count == 0) return;

            foreach (ulong steamId in frozen)
            {
                BasePlayer player = BasePlayer.FindByID(steamId);
                if (player == null || !player.IsConnected) continue;

                Vector3 anchor;
                if (!frozenPosition.TryGetValue(steamId, out anchor)) continue;

                if ((player.transform.position - anchor).sqrMagnitude > 0.01f)
                    player.Teleport(anchor);
            }
        }

        #endregion

        #region Admin commands

        [ConsoleCommand("playerverify.reset")]
        private void ConsoleResetVerification(ConsoleSystem.Arg arg)
        {
            if (!arg.IsAdmin) return;

            string steamId = arg.GetString(0);
            if (string.IsNullOrEmpty(steamId)) return;

            if (verified.Remove(steamId))
            {
                SaveData();
                permission.RevokeUserPermission(steamId, PermVerified);
                arg.ReplyWith(Lang("ResetSuccess", null, steamId));
            }
        }

        #endregion
    }
}
