/*<summary> Plugin DiscordLogs:
Envia os logs gerais do servidor para o Discord atraves de webhooks, um por categoria.

Categorias:
Connections => Jogador conectou / desconectou.
Chat => Mensagens do chat global.
Moderation => Bans, kicks e unbans (capturado via hooks cmdBan/cmdKick/cmdUnban do KickBan/Banip).
Server => Servidor iniciado / encerrando + comando de teste.

Configuracao (oxide/config/DiscordLogs.json):
Settings.Enabled => Liga/desliga o plugin inteiro.
Settings.ServerName => Nome mostrado no rodape dos embeds.
Settings.IgnoreLocalhostIP => Ignora log de conexao vinda de 127.0.0.1.
Webhooks.<Categoria> => URL do webhook do Discord para cada categoria (vazio = desativado).
Categories.<Categoria> => true/false para ligar/desligar cada categoria.

Comandos:
/dlogs => (admin) Envia uma mensagem de teste para o webhook da categoria "Server".
</summary>*/

using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Discord Logs", "Gsa or oArezi", "1.1.2")]
    [Description("Envia os logs gerais do servidor (conexoes, chat e moderacao) para o Discord via webhooks.")]
    class DiscordLogs : RustLegacyPlugin
    {
        private const int ColorGreen = 3066993;
        private const int ColorRed = 15158332;
        private const int ColorOrange = 15105570;
        private const int ColorBlue = 3447003;

        private readonly Dictionary<string, string> jsonHeaders = new Dictionary<string, string> { { "Content-Type", "application/json" } };
        private static readonly Regex ColorTagRegex = new Regex(@"\[color[^\]]*\]|\[/color\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private bool enabled;
        private bool ignoreLocalhost;
        private string serverName;
        private Dictionary<string, string> webhooks;
        private Dictionary<string, bool> categories;

        protected override void LoadDefaultConfig()
        {
            Config["Settings"] = new Dictionary<string, object>
            {
                { "Enabled", true },
                { "ServerName", "New Island" },
                { "IgnoreLocalhostIP", true }
            };
            Config["Webhooks"] = new Dictionary<string, object>
            {
                { "Connections", "" },
                { "Chat", "" },
                { "Moderation", "" },
                { "Server", "" }
            };
            Config["Categories"] = new Dictionary<string, object>
            {
                { "Connections", true },
                { "Chat", true },
                { "Moderation", true },
                { "Server", true }
            };
        }

        void Loaded()
        {
            enabled = Config.Get<bool>("Settings", "Enabled");
            serverName = Config.Get<string>("Settings", "ServerName");
            ignoreLocalhost = Config.Get<bool>("Settings", "IgnoreLocalhostIP");
            webhooks = Config.Get<Dictionary<string, string>>("Webhooks");
            categories = Config.Get<Dictionary<string, bool>>("Categories");
            SaveConfig();
            Puts("[DiscordLogs] build de diagnostico v1.0.1 carregada (sem ServicePointManager).");
        }

        [ChatCommand("dlogs")]
        void cmdDiscordLogsTest(NetUser netuser, string command, string[] args)
        {
            if (!netuser.CanAdmin()) { SendReply(netuser, "Voce nao tem permissao para usar esse comando."); return; }
            SendLog("Server", "Teste de Log", string.Format("Log de teste disparado por **{0}**.", netuser.displayName), ColorBlue);
            SendReply(netuser, "Teste enviado para o webhook configurado da categoria 'Server'.");
        }

        void OnServerInitialized()
        {
            SendLog("Server", "Servidor iniciado", string.Format("O servidor **{0}** esta online.", serverName), ColorGreen);
        }

        void Unload()
        {
            SendLog("Server", "Servidor encerrando", string.Format("O servidor **{0}** esta sendo desligado ou reiniciado.", serverName), ColorOrange);
        }

        void OnPlayerConnected(NetUser netuser)
        {
            string ip = netuser.networkPlayer.externalIP;
            if (ignoreLocalhost && ip == "127.0.0.1") return;
            SendLog("Connections", "Jogador conectou", string.Format("**{0}**\nSteamID: `{1}`\nIP: `{2}`", netuser.displayName, netuser.userID, ip), ColorGreen);
        }

        void OnPlayerDisconnected(uLink.NetworkPlayer networkPlayer)
        {
            NetUser netuser = networkPlayer.GetLocalData() as NetUser;
            if (netuser == null) return;
            SendLog("Connections", "Jogador desconectou", string.Format("**{0}**\nSteamID: `{1}`", netuser.displayName, netuser.userID), ColorOrange);
        }

        void OnPlayerChat(NetUser netuser, string message)
        {
            string clean = ColorTagRegex.Replace(message, "").Trim();
            if (clean.Length == 0) return;
            SendLog("Chat", "Chat", string.Format("**{0}**: {1}", netuser.displayName, clean), ColorBlue);
        }

        // Recebido via Interface.CallHook("cmdBan", ...) disparado pelo KickBan.cs / Banip.cs
        void cmdBan(string userid, string name, string reason)
        {
            SendLog("Moderation", "Jogador banido", string.Format("**{0}**\nSteamID: `{1}`\nMotivo: {2}", name, userid, reason), ColorRed);
        }

        // Recebido via Interface.CallHook("cmdKick", ...) disparado pelo KickBan.cs / Banip.cs
        void cmdKick(string userid, string name, string reason)
        {
            SendLog("Moderation", "Jogador kickado", string.Format("**{0}**\nSteamID: `{1}`\nMotivo: {2}", name, userid, reason), ColorOrange);
        }

        // Recebido via Interface.CallHook("cmdUnban", ...) disparado pelo KickBan.cs / Banip.cs
        void cmdUnban(string userid)
        {
            SendLog("Moderation", "Jogador desbanido", string.Format("SteamID: `{0}`", userid), ColorGreen);
        }

        private void SendLog(string category, string title, string description, int color)
        {
            if (!enabled) return;
            bool categoryEnabled;
            if (!categories.TryGetValue(category, out categoryEnabled) || !categoryEnabled) return;
            string url;
            if (!webhooks.TryGetValue(category, out url) || string.IsNullOrEmpty(url)) return;

            var payload = new DiscordPayload
            {
                Embeds = new List<DiscordEmbed>
                {
                    new DiscordEmbed
                    {
                        Title = title,
                        Description = description,
                        Color = color,
                        Footer = new DiscordEmbedFooter { Text = serverName },
                        Timestamp = DateTime.UtcNow.ToString("o")
                    }
                }
            };
            string json = JsonConvert.SerializeObject(payload);
            webrequest.EnqueuePost(url, json, (code, response) =>
            {
                if (code != 200 && code != 204) Puts(string.Format("[DiscordLogs] Webhook '{0}' retornou HTTP {1}: {2}", category, code, string.IsNullOrEmpty(response) ? "(sem resposta, provavel falha de rede/SSL ou URL invalida)" : response));
            }, this, jsonHeaders);
        }

        class DiscordPayload
        {
            [JsonProperty("embeds")]
            public List<DiscordEmbed> Embeds { get; set; }
        }

        class DiscordEmbed
        {
            [JsonProperty("title")]
            public string Title { get; set; }
            [JsonProperty("description")]
            public string Description { get; set; }
            [JsonProperty("color")]
            public int Color { get; set; }
            [JsonProperty("footer")]
            public DiscordEmbedFooter Footer { get; set; }
            [JsonProperty("timestamp")]
            public string Timestamp { get; set; }
        }

        class DiscordEmbedFooter
        {
            [JsonProperty("text")]
            public string Text { get; set; }
        }
    }
}
