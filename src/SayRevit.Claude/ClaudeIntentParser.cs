using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using SayRevit.Core.Model;
using SayRevit.Core.Parsing;

namespace SayRevit.Claude
{
    /// <summary>Opzioni per il parser Claude.</summary>
    public sealed class ClaudeOptions
    {
        /// <summary>Modello da usare. Predefinito: claude-opus-5.</summary>
        public string Model { get; set; } = "claude-opus-5";

        /// <summary>Chiave API. Se null si usa la variabile d'ambiente ANTHROPIC_API_KEY.</summary>
        public string ApiKey { get; set; }

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
    }

    /// <summary>
    /// Parser basato su Claude: invia la descrizione e il catalogo del modello Revit, riceve un
    /// piano JSON conforme allo schema (<see cref="PlanJson.Schema"/>) tramite gli output strutturati.
    /// </summary>
    public sealed class ClaudeIntentParser : IIntentParser
    {
        private const string SystemPrompt =
            "Sei un assistente per Autodesk Revit MEP. Converti descrizioni in linguaggio naturale (italiano o inglese) di " +
            "tubazioni (pipes) e canali (ducts) in un piano JSON strutturato. Gestisci SOLO tubazioni e canali con i loro stacchi " +
            "(derivazioni a T); per qualsiasi altra richiesta compila il campo \"unsupported\".\n\n" +
            "Regole:\n" +
            "- Tutte le lunghezze in millimetri. DN200 => diameter_mm 200 con is_dn true. 1/2\" => DN15, 3/4\" => DN20, 1\" => DN25, 1 1/4\" => DN32, 1 1/2\" => DN40, 2\" => DN50, 3\" => DN80, 4\" => DN100, 6\" => DN150, 8\" => DN200.\n" +
            "- Sezioni tipo 400x200 sono canali rettangolari (width_mm x height_mm).\n" +
            "- Se la lunghezza del tratto manca usa 3000; se manca quella degli stacchi usa 500. Plurale senza numero (\"degli stacchi\") => count 2 e aggiungi un avviso.\n" +
            "- Se la dimensione di uno stacco manca, usa quella del tratto e aggiungi un avviso.\n" +
            "- Ogni frase separata da \";\", \".\" o \"poi\" è un nuovo tratto che prosegue dal precedente (continues_previous true) salvo che sia descritto come separato.\n" +
            "- Usa i nomi ESATTI del catalogo (tipi, sistemi, livelli) quando corrispondono chiaramente; altrimenti lascia null e riempi type_hints/system_class.\n" +
            "- Non inventare dati non deducibili dal testo; segnala le assunzioni in \"warnings\" (in italiano).";

        private readonly ClaudeOptions _options;
        private readonly AnthropicClient _client;

        public ClaudeIntentParser(ClaudeOptions options = null)
        {
            _options = options ?? new ClaudeOptions();
            _client = string.IsNullOrWhiteSpace(_options.ApiKey)
                ? new AnthropicClient { Timeout = _options.Timeout }
                : new AnthropicClient { Timeout = _options.Timeout, ApiKey = _options.ApiKey };
        }

        public string Name => "Claude (" + _options.Model + ")";

        public async Task<ParseResult> ParseAsync(string text, ModelCatalog catalog, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text)) return ParseResult.Fail("Inserisci una descrizione.");

            var userMessage = BuildUserMessage(text, catalog ?? ModelCatalog.Empty());

            BetaMessage response;
            try
            {
                response = await _client.Beta.Messages.Create(new MessageCreateParams
                {
                    Model = _options.Model,
                    MaxTokens = 16000,
                    System = SystemPrompt,
                    Betas = new List<Anthropic.Core.ApiEnum<string, AnthropicBeta>> { AnthropicBeta.ServerSideFallback2026_07_01 },
                    Fallbacks = new Default(),
                    OutputConfig = new BetaOutputConfig
                    {
                        Format = new BetaJsonOutputFormat { Schema = PlanJson.SchemaAsDictionary() }
                    },
                    Messages = new List<BetaMessageParam>
                    {
                        new BetaMessageParam { Role = Role.User, Content = userMessage }
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return ParseResult.Fail("Errore nella chiamata a Claude: " + ex.Message);
            }

            var stopReason = response.StopReason == null ? null : Convert.ToString(response.StopReason.Raw());
            if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                var why = string.Empty;
                var det = response.StopDetails;
                if (det != null && !string.IsNullOrEmpty(det.Explanation)) why = " (" + det.Explanation + ")";
                return ParseResult.Fail("Claude ha rifiutato la richiesta" + why + ". Prova con il parser a regole.");
            }

            var sb = new StringBuilder();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var t) && t != null) sb.Append(t.Text);
            }
            var json = sb.ToString().Trim();
            if (json.Length == 0) return ParseResult.Fail("Claude non ha restituito alcun piano.");

            try
            {
                var result = PlanJson.FromJson(json, text);
                if (result.Success) result.Notes.Add("Interpretato da " + Name + ".");
                return result;
            }
            catch (Exception ex)
            {
                return ParseResult.Fail("Risposta di Claude non interpretabile: " + ex.Message);
            }
        }

        private static string BuildUserMessage(string text, ModelCatalog c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Catalogo del modello Revit corrente (usa questi nomi esatti quando pertinenti):");
            sb.AppendLine("Tipi di tubazione:");
            foreach (var t in c.PipeTypes) sb.AppendLine("  - " + t.Name + SizesNote(t));
            if (c.PipeTypes.Count == 0) sb.AppendLine("  (nessuno)");
            sb.AppendLine("Tipi di canale:");
            foreach (var t in c.DuctTypes) sb.AppendLine("  - " + t.Name + " [" + (t.Shape == SizeShape.Rectangular ? "rettangolare" : "circolare") + "]");
            if (c.DuctTypes.Count == 0) sb.AppendLine("  (nessuno)");
            sb.AppendLine("Sistemi di tubazione:");
            foreach (var s in c.PipingSystems) sb.AppendLine("  - " + s.Name + " [" + s.SystemClass + "]");
            sb.AppendLine("Sistemi aeraulici:");
            foreach (var s in c.DuctSystems) sb.AppendLine("  - " + s.Name + " [" + s.SystemClass + "]");
            sb.AppendLine("Livelli:");
            foreach (var l in c.Levels) sb.AppendLine("  - " + l + (l == c.ActiveLevel ? " (livello della vista attiva)" : string.Empty));
            sb.AppendLine();
            sb.AppendLine("Descrizione dell'utente:");
            sb.AppendLine(text.Trim());
            return sb.ToString();
        }

        private static string SizesNote(CatalogType t)
        {
            if (t.AvailableDiametersMm.Count == 0) return string.Empty;
            var sb = new StringBuilder(" (diametri disponibili mm: ");
            for (var i = 0; i < t.AvailableDiametersMm.Count && i < 40; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(MepSize.Fmt(t.AvailableDiametersMm[i]));
            }
            if (t.AvailableDiametersMm.Count > 40) sb.Append(", ...");
            return sb.Append(")").ToString();
        }
    }
}
