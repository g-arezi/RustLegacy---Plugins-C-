using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("AutoRestart", "Rust Legacy", "1.0")]
    class reiniciar : RustLegacyPlugin
    {
        int AutoRestart = 24000;
        string SystemName = "NewIsLand server";

       void Loaded()
        {
                timer.Repeat(AutoRestart, 0, () =>
                {
                        rust.BroadcastChat(SystemName, "[color orange]ATENÇÃO! O SERVIDOR VAI REINICIAR EM 60 SEGUNDOS, SALVE SEU PROGRESSO. | SERVER RESTARTING IN 60 SECONDS!");
                    rust.RunServerCommand("notice.popupall \"ATENÇÃO! O SERVIDOR VAI REINICIAR EM 60 SEGUNDOS, SALVE SEU PROGRESSO.\"");
                    rust.RunServerCommand("save.all");
                    EnviarDiscord();
                    timer.Once(60f, () =>
                            {
                                Puts("Reiniciando servidor automatico.");
                            rust.RunServerCommand("quit");
                            });
                });

        }
        void EnviarDiscord()
        {
            string payloadJson = JsonConvert.SerializeObject(new DisoverPls()
            {
                MessageText = string.Format("@everyone `Servidor reiniciando em` **60 segundos.**")
            }); ;
            Dictionary<string, string> plicksl = new Dictionary<string, string>();
            plicksl.Add("Content-Type", "application/json");
            webrequest.EnqueuePost(CordSupport, payloadJson, (code, response) => NotStuctBack(code, response), this, plicksl);
        }
        public void NotStuctBack(int code, string responde) { }
        class DisoverPls
        {
            [JsonProperty("content")]
            public string MessageText { get; set; }
        }
        string CordSupport = "Your Webhook Discord";
    }
}