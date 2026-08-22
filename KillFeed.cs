/*<summary> Plugin KillFeed:
Envia um killfeed para o Discord via webhook, usando a API de mortes do plugin Death.cs
(hooks OnPlayerDeath, OnPlayerSuicide, OnAnimalDeath, OnStructureDestroyed, OnDeployableDestroyed).

Categorias (todas passam pelo mesmo webhook, cada uma pode ser ligada/desligada):
PvP => Jogador matou jogador.
EvP => Animal matou jogador.
PvE => Jogador matou animal.
Suicide => Jogador morreu por conta propria (queda, sangramento, radiacao, agua, etc).
Building => Estrutura ou deployable destruido.

Configuracao (oxide/config/KillFeed.json):
Settings.Enabled => Liga/desliga o plugin inteiro.
Settings.ServerName => Nome mostrado no rodape dos embeds.
Settings.TrackStats => Se true, guarda kills/mortes por jogador usando o PlayerDatabase (se estiver carregado).
Webhook.Url => URL do webhook do Discord usado para todas as categorias.
Categories.<Categoria> => true/false para ligar/desligar cada categoria.

Comandos:
/kd => Mostra kills, mortes e K/D do proprio jogador (requer PlayerDatabase carregado e TrackStats ativo).
</summary>*/

using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Discord KillFeed", "Gsa or oArezi", "1.0.1")]
    [Description("Envia um killfeed (PvP, mortes por animais, suicidios e destruicao de construcoes) para o Discord via webhook.")]
    class KillFeed : RustLegacyPlugin
    {
        [PluginReference]
        Plugin PlayerDatabase;

        private const int ColorRed = 15158332;
        private const int ColorOrange = 15105570;
        private const int ColorYellow = 15844367;
        private const int ColorGrey = 9807270;
        private const int ColorGreen = 3066993;

        private readonly Dictionary<string, string> jsonHeaders = new Dictionary<string, string> { { "Content-Type", "application/json" } };

        private bool enabled;
        private bool trackStats;
        private string serverName;
        private string webhookUrl;
        private Dictionary<string, bool> categories;

        protected override void LoadDefaultConfig()
        {
            Config["Settings"] = new Dictionary<string, object>
            {
                { "Enabled", true },
                { "ServerName", "New Island" },
                { "TrackStats", true }
            };
            Config["Webhook"] = new Dictionary<string, object>
            {
                { "Url", "" }
            };
            Config["Categories"] = new Dictionary<string, object>
            {
                { "PvP", true },
                { "EvP", true },
                { "PvE", false },
                { "Suicide", false },
                { "Building", false }
            };
        }

        void Loaded()
        {
            enabled = Config.Get<bool>("Settings", "Enabled");
            serverName = Config.Get<string>("Settings", "ServerName");
            trackStats = Config.Get<bool>("Settings", "TrackStats");
            webhookUrl = Config.Get<string>("Webhook", "Url");
            categories = Config.Get<Dictionary<string, bool>>("Categories");
            SaveConfig();
        }

        [ChatCommand("kd")]
        void cmdKd(NetUser netuser, string command, string[] args)
        {
            if (PlayerDatabase == null) { SendReply(netuser, "Estatisticas indisponiveis (PlayerDatabase nao esta carregado)."); return; }
            string userid = netuser.userID.ToString();
            int kills = GetStat(userid, "kf_kills");
            int deaths = GetStat(userid, "kf_deaths");
            double ratio = deaths == 0 ? kills : Math.Round(kills / (double)deaths, 2);
            SendReply(netuser, string.Format("Kills: {0} | Mortes: {1} | K/D: {2}", kills, deaths, ratio));
        }

        void OnPlayerDeath(TakeDamage takedamage, DamageEvent damage, object tags)
        {
            string deathtype = tags.GetProperty("deathType").ToString();
            string killer = tags.GetProperty("killer").ToString();
            string killerId = tags.GetProperty("killerId").ToString();
            string killed = tags.GetProperty("killed").ToString();
            string killedId = tags.GetProperty("killedId").ToString();
            string weapon = tags.GetProperty("weapon").ToString();
            string bodypart = tags.GetProperty("bodypart").ToString();
            double distance = Convert.ToDouble(tags.GetProperty("distance"));

            if (deathtype == "human")
            {
                if (trackStats && killerId != killedId)
                {
                    IncrementStat(killerId, "kf_kills");
                    IncrementStat(killedId, "kf_deaths");
                }
                SendLog("PvP", "PvP", string.Format("**{0}** matou **{1}**\nArma: {2}\nParte do corpo: {3}\nDistancia: {4}m", killer, killed, weapon, bodypart, distance.ToString("0.#")), ColorRed);
            }
            else
            {
                if (trackStats) IncrementStat(killedId, "kf_deaths");
                SendLog("EvP", "Morte por animal", string.Format("**{0}** matou **{1}**\nDistancia: {2}m", killer, killed, distance.ToString("0.#")), ColorOrange);
            }
        }

        void OnPlayerSuicide(TakeDamage takedamage, DamageEvent damage, object tags)
        {
            string killed = tags.GetProperty("killed").ToString();
            string killedId = tags.GetProperty("killedId").ToString();
            string weapon = tags.GetProperty("weapon").ToString();
            if (trackStats) IncrementStat(killedId, "kf_deaths");
            SendLog("Suicide", "Suicidio", string.Format("**{0}** morreu ({1})", killed, weapon), ColorGrey);
        }

        void OnAnimalDeath(TakeDamage takedamage, DamageEvent damage, object tags)
        {
            string killer = tags.GetProperty("killer").ToString();
            string killerId = tags.GetProperty("killerId").ToString();
            string killed = tags.GetProperty("killed").ToString();
            double distance = Convert.ToDouble(tags.GetProperty("distance"));
            if (trackStats && killer != "Unknown") IncrementStat(killerId, "kf_animal_kills");
            SendLog("PvE", "Caca", string.Format("**{0}** abateu **{1}**\nDistancia: {2}m", killer, killed, distance.ToString("0.#")), ColorGreen);
        }

        void OnStructureDestroyed(TakeDamage takedamage, DamageEvent damage, object tags)
        {
            LogBuildingDestroyed("Estrutura destruida", tags);
        }

        void OnDeployableDestroyed(TakeDamage takedamage, DamageEvent damage, object tags)
        {
            LogBuildingDestroyed("Deployable destruido", tags);
        }

        private void LogBuildingDestroyed(string title, object tags)
        {
            string killed = tags.GetProperty("killed").ToString();
            string killedId = tags.GetProperty("killedId").ToString();
            string weapon = tags.GetProperty("weapon").ToString();
            SendLog("Building", title, string.Format("**{0}**\nDono: `{1}`\nCausa: {2}", killed, killedId, weapon), ColorYellow);
        }

        private int GetStat(string userid, string key)
        {
            if (PlayerDatabase == null) return 0;
            object raw = PlayerDatabase.Call("GetPlayerData", userid, key);
            int value;
            return raw != null && int.TryParse(raw.ToString(), out value) ? value : 0;
        }

        private void IncrementStat(string userid, string key)
        {
            if (PlayerDatabase == null) return;
            int current = GetStat(userid, key);
            PlayerDatabase.Call("SetPlayerData", userid, key, current + 1);
        }

        private void SendLog(string category, string title, string description, int color)
        {
            if (!enabled || string.IsNullOrEmpty(webhookUrl)) return;
            bool categoryEnabled;
            if (!categories.TryGetValue(category, out categoryEnabled) || !categoryEnabled) return;

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
            webrequest.EnqueuePost(webhookUrl, json, (code, response) =>
            {
                if (code != 200 && code != 204) Puts(string.Format("[KillFeed] Webhook retornou HTTP {0}: {1}", code, string.IsNullOrEmpty(response) ? "(sem resposta, provavel falha de rede/SSL ou URL invalida)" : response));
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
