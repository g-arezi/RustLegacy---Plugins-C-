using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Oxide.Core;

// Baseado no padrão do KeyManager.cs (serverdata/oxide/plugins).
// Estoque de keys de VIP com hash único e não reutilizável.

namespace Oxide.Plugins
{
    [Info("KeyStock", "Gsa or oArezi", 1.0)]
    class KeyStock : RustLegacyPlugin
    {
        static string Prefixo = "estoque";
        static string PermissaoAdmin = "keystock.admin";

        // Tiers permitidos e a permissão correspondente registrada pelo EasyChat.json
        static Dictionary<string, string> TierPermissao = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
       /* {
            { "pvp",       "pvp_chat" },
            { "ouro",      "ouro_chat" },
            { "prata",     "prata_chat" },
            { "platina",   "platina_chat" },        //substitua por permissões do easychat do seu servidor!
            { "newisland", "nwvip_chat" },
            { "diamante",  "diamante_chat" },
        };*/ 

        class ChaveData
        {
            public string Tier;
            public string Permissao;
            public int Dias;
            public string CriadaEm;
            public string CriadaPor;
            public bool Usada;
            public bool Revogada;
            public string UsadaPorId;
            public string UsadaPorNome;
            public string UsadaEm;
            public string ExpiraEm;
        }

        class StoredData
        {
            public string Salt = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            public Dictionary<string, ChaveData> Chaves = new Dictionary<string, ChaveData>();
        }

        StoredData storedData;
        static Timer timerExpiracao;

        static IFormatProvider Cultura = new CultureInfo("pt-BR");
        const string FormatoData = "dd/MM/yyyy HH:mm:ss";

        void LoadDefaultConfig() { }

        void Loaded()
        {
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>("KeyStock");
            if (storedData == null) storedData = new StoredData();
            if (string.IsNullOrEmpty(storedData.Salt)) storedData.Salt = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

            // Diagnóstico: confirma no console quantas chaves foram realmente lidas do arquivo de dados.
            // Se aparecer 0, o arquivo oxide/data/KeyStock.json nao foi encontrado (nome/caminho errado,
            // maiusculas/minusculas na VPS Linux, ou o arquivo nunca foi enviado para essa pasta).
            Puts(string.Format("[KeyStock] {0} chave(s) carregada(s) de oxide/data/KeyStock.json", storedData.Chaves.Count));
        }

        void OnServerInitialized()
        {
            CarregarConfigTiers();
            // Nao gravar KeyStock.json aqui: se a leitura em Loaded() falhar (arquivo no lugar
            // errado, por exemplo), isso sobrescreveria o estoque real com um estoque vazio.
            // O arquivo so deve ser regravado quando uma chave for de fato gerada/usada/revogada.
            timerExpiracao = timer.Repeat(300f, 0, VerificarExpiracoes);
        }

        void Unload()
        {
            if (timerExpiracao != null) timerExpiracao.Destroy();
        }

        // Permite sobrescrever a permissão de cada tier via config, mantendo os padrões do EasyChat.json
        void CarregarConfigTiers()
        {
            var tiers = Config["Tiers"] as Dictionary<string, object>;
            if (tiers == null)
            {
                tiers = new Dictionary<string, object>();
                Config["Tiers"] = tiers;
            }
            foreach (var tier in TierPermissao.Keys.ToList())
            {
                object valor;
                if (tiers.TryGetValue(tier, out valor) && valor is string && !string.IsNullOrEmpty((string)valor))
                {
                    TierPermissao[tier] = (string)valor;
                }
                else
                {
                    tiers[tier] = TierPermissao[tier];
                }
            }
            SaveConfig();
        }

        void SalvarDados()
        {
            Interface.Oxide.DataFileSystem.WriteObject("KeyStock", storedData);
            AtualizarEstoqueExterno();
        }

        // Arquivo somente-leitura, separado do estado interno, para disponibilizar o estoque de keys
        // não utilizadas a um sistema externo (loja/site).
        void AtualizarEstoqueExterno()
        {
            var estoque = TierPermissao.Keys.ToDictionary(t => t, t => new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var par in storedData.Chaves)
            {
                if (par.Value.Usada || par.Value.Revogada) continue;
                if (!estoque.ContainsKey(par.Value.Tier)) estoque[par.Value.Tier] = new List<string>();
                estoque[par.Value.Tier].Add(par.Key);
            }
            Interface.Oxide.DataFileSystem.WriteObject("KeyStock_Estoque", estoque);
        }

        bool Acess(NetUser netuser)
        {
            if (netuser.CanAdmin()) return true;
            if (permission.UserHasPermission(netuser.userID.ToString(), PermissaoAdmin)) return true;
            return false;
        }

        // Gera um hash único (SHA-256) que não expõe tier/validade e não pode ser adivinhado nem reutilizado
        string GerarHashUnico()
        {
            string chave;
            do
            {
                byte[] aleatorio = new byte[32];
                var rng = new RNGCryptoServiceProvider();
                rng.GetBytes(aleatorio);
                string bruto = storedData.Salt + "|" + Convert.ToBase64String(aleatorio) + "|" + Guid.NewGuid().ToString("N") + "|" + DateTime.UtcNow.Ticks;
                var sha = new SHA256Managed();
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(bruto));
                var sb = new StringBuilder();
                foreach (byte b in hash) sb.Append(b.ToString("X2"));
                string hex = sb.ToString().Substring(0, 20); // 20 chars = 4 blocos de 5
                chave = string.Join("-", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 5, 5)).ToArray());
            }
            while (storedData.Chaves.ContainsKey(chave)); // garante unicidade (colisão praticamente impossível)
            return chave;
        }

        [ChatCommand("gerarkey")]
        void CmdGerarKey(NetUser netuser, string cmd, string[] args)
        {
            if (!Acess(netuser)) { rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color red]Voce nao pode usar este comando!"); return; }
            if (args.Length < 2) { rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color #006400]Use /gerarkey (tier) (dias) (quantidade opcional)"); return; }

            string tier = args[0].ToLowerInvariant();
            if (!TierPermissao.ContainsKey(tier))
            {
                rust.SendChatMessage(netuser, Prefixo, string.Format("[color red][!] [color red]Tier invalido. Opcoes: [color white]{0}", string.Join(", ", TierPermissao.Keys.ToArray())));
                return;
            }
            string permissao = TierPermissao[tier];
            if (!permission.PermissionExists(permissao))
            {
                rust.SendChatMessage(netuser, Prefixo, string.Format("[color red][!] [color red]A permissao [color white]{0} [color red]nao esta registrada no servidor!", permissao));
                return;
            }
            int dias;
            if (!Int32.TryParse(args[1], out dias) || dias <= 0)
            {
                rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color #006400]Quantidade de dias invalida");
                return;
            }
            int quantidade = 1;
            if (args.Length >= 3 && (!Int32.TryParse(args[2], out quantidade) || quantidade <= 0))
            {
                rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color #006400]Quantidade de keys invalida");
                return;
            }
            if (quantidade > 100)
            {
                rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color #006400]Limite maximo de 100 keys por vez");
                return;
            }

            var geradas = new List<string>();
            for (int i = 0; i < quantidade; i++)
            {
                string chave = GerarHashUnico();
                storedData.Chaves[chave] = new ChaveData
                {
                    Tier = tier,
                    Permissao = permissao,
                    Dias = dias,
                    CriadaEm = DateTime.Now.ToString(FormatoData, Cultura),
                    CriadaPor = netuser.displayName,
                    Usada = false,
                    Revogada = false,
                };
                geradas.Add(chave);
            }
            SalvarDados();

            rust.SendChatMessage(netuser, Prefixo, string.Format("[color #006400]{0} key(s) do tier [color white]{1} [color #006400]({2} dias) geradas com sucesso! Veja o console.", quantidade, tier, dias));
            Puts(string.Format("=== {0} key(s) geradas [tier={1}, dias={2}] por {3} ===", quantidade, tier, dias, netuser.displayName));
            foreach (var chave in geradas) Puts(chave);
            Puts("=== As keys tambem foram salvas em oxide/data/KeyStock.json e KeyStock_Estoque.json ===");
        }

        [ChatCommand("ativarkey")]
        void CmdAtivarKey(NetUser netuser, string cmd, string[] args)
        {
            if (args.Length < 1) { rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color #006400]Use /ativarkey (chave)"); return; }

            string chave = args[0].Trim().ToUpperInvariant();
            ChaveData dadosChave;
            if (!storedData.Chaves.TryGetValue(chave, out dadosChave))
            {
                rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color red]Chave invalida!");
                return;
            }
            if (dadosChave.Usada || dadosChave.Revogada)
            {
                rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color red]Essa chave ja foi utilizada ou revogada!");
                return;
            }
            if (!permission.PermissionExists(dadosChave.Permissao))
            {
                rust.SendChatMessage(netuser, Prefixo, string.Format("[color red][!] [color red]A permissao [color white]{0} [color red]nao esta mais registrada, contate um admin!", dadosChave.Permissao));
                return;
            }

            // Marca como usada imediatamente para eliminar qualquer risco de reuso (inclusive uso simultâneo)
            dadosChave.Usada = true;
            dadosChave.UsadaPorId = netuser.userID.ToString();
            dadosChave.UsadaPorNome = netuser.displayName;
            DateTime agora = DateTime.Now;
            dadosChave.UsadaEm = agora.ToString(FormatoData, Cultura);
            dadosChave.ExpiraEm = agora.AddDays(dadosChave.Dias).ToString(FormatoData, Cultura);
            SalvarDados();

            rust.RunServerCommand(string.Format("oxide.grant user {0} {1}", netuser.userID.ToString(), dadosChave.Permissao));
            rust.SendChatMessage(netuser, Prefixo, string.Format("[color #006400]Key ativada! Voce recebeu o VIP [color white]{0} [color #006400]por [color white]{1} [color #006400]dias (expira em {2})", dadosChave.Tier, dadosChave.Dias, dadosChave.ExpiraEm));
            rust.BroadcastChat(Prefixo, string.Format("[color #FFB700]O jogador [color white]{0} [color #FFB700]ativou uma key e recebeu o VIP [color white]{1}[color #FFB700]!", netuser.displayName, dadosChave.Tier));
            Puts(string.Format("Chave {0} (tier={1}) ativada por {2} ({3})", chave, dadosChave.Tier, netuser.displayName, netuser.userID));
        }

        [ChatCommand("revogarkey")]
        void CmdRevogarKey(NetUser netuser, string cmd, string[] args)
        {
            if (!Acess(netuser)) { rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color red]Voce nao pode usar este comando!"); return; }
            if (args.Length < 1) { rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color #006400]Use /revogarkey (chave)"); return; }

            string chave = args[0].Trim().ToUpperInvariant();
            ChaveData dadosChave;
            if (!storedData.Chaves.TryGetValue(chave, out dadosChave))
            {
                rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color red]Chave nao encontrada!");
                return;
            }
            if (dadosChave.Usada)
            {
                rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color red]Essa chave ja foi utilizada, use /keymanager para remover a permissao do jogador!");
                return;
            }
            dadosChave.Revogada = true;
            SalvarDados();
            rust.SendChatMessage(netuser, Prefixo, "[color #006400]Chave revogada com sucesso!");
        }

        [ChatCommand("minhavip")]
        void CmdMinhaVip(NetUser netuser, string cmd, string[] args)
        {
            string id = netuser.userID.ToString();
            var ativas = storedData.Chaves.Values.Where(c => c.Usada && !c.Revogada && c.UsadaPorId == id).ToList();
            if (ativas.Count == 0)
            {
                rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color red]Voce nao possui nenhuma VIP ativada por key");
                return;
            }
            foreach (var c in ativas)
            {
                rust.SendChatMessage(netuser, Prefixo, string.Format("[color #006400]VIP [color white]{0} [color #006400]expira em [color white]{1}", c.Tier, c.ExpiraEm));
            }
        }

        [ChatCommand("estoque")]
        void CmdEstoque(NetUser netuser, string cmd, string[] args)
        {
            if (!Acess(netuser)) { rust.SendChatMessage(netuser, Prefixo, "[color red][!] [color red]Voce nao pode usar este comando!"); return; }

            rust.SendChatMessage(netuser, Prefixo, string.Format("[color #006400]Total de chaves no arquivo: [color white]{0}", storedData.Chaves.Count));
            foreach (var tier in TierPermissao.Keys.ToArray())
            {
                int disponiveis = storedData.Chaves.Values.Count(c => !c.Usada && !c.Revogada && string.Equals(c.Tier, tier, StringComparison.OrdinalIgnoreCase));
                rust.SendChatMessage(netuser, Prefixo, string.Format("  [color white]{0}[color #006400]: {1} disponivel(is)", tier, disponiveis));
            }
        }

        // Revoga automaticamente permissões de keys expiradas (equivalente ao comportamento do KeyManager)
        void VerificarExpiracoes()
        {
            DateTime agora = DateTime.Now;
            bool alterou = false;
            foreach (var par in storedData.Chaves)
            {
                var c = par.Value;
                if (!c.Usada || c.Revogada || string.IsNullOrEmpty(c.ExpiraEm)) continue;

                DateTime expira;
                if (!DateTime.TryParseExact(c.ExpiraEm, FormatoData, Cultura, DateTimeStyles.None, out expira)) continue;
                if (expira > agora) continue;

                c.Revogada = true;
                alterou = true;
                rust.RunServerCommand(string.Format("oxide.revoke user {0} {1}", c.UsadaPorId, c.Permissao));

                var alvo = rust.GetAllNetUsers().FirstOrDefault(n => n.userID.ToString() == c.UsadaPorId);
                if (alvo != null)
                {
                    rust.SendChatMessage(alvo, Prefixo, string.Format("[color red]Sua VIP ({0}) expirou!", c.Tier));
                }
            }
            if (alterou) SalvarDados();
        }
    }
}

