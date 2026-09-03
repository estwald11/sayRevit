using System;
using System.Collections.Generic;
using System.Text.Json;
using SayRevit.Core.Model;

namespace SayRevit.Claude
{
    /// <summary>Schema JSON del piano (per gli output strutturati) e conversione JSON → <see cref="MepPlan"/>.</summary>
    public static class PlanJson
    {
        private const string SizeSchema = @"{
  ""type"": ""object"", ""additionalProperties"": false,
  ""properties"": {
    ""shape"": { ""type"": ""string"", ""enum"": [""round"", ""rectangular""] },
    ""diameter_mm"": { ""type"": ""number"", ""description"": ""Diametro nominale in mm (per DN usare il numero del DN). 0 se rettangolare."" },
    ""width_mm"": { ""type"": ""number"", ""description"": ""Larghezza in mm (solo rettangolare, altrimenti 0)."" },
    ""height_mm"": { ""type"": ""number"", ""description"": ""Altezza in mm (solo rettangolare, altrimenti 0)."" },
    ""is_dn"": { ""type"": ""boolean"", ""description"": ""true se espresso come DN (diametro nominale)."" }
  },
  ""required"": [""shape"", ""diameter_mm"", ""width_mm"", ""height_mm"", ""is_dn""]
}";

        public static readonly string Schema = @"{
  ""type"": ""object"", ""additionalProperties"": false,
  ""properties"": {
    ""runs"": {
      ""type"": ""array"",
      ""description"": ""Tratti rettilinei da creare, nell'ordine in cui sono descritti."",
      ""items"": {
        ""type"": ""object"", ""additionalProperties"": false,
        ""properties"": {
          ""kind"": { ""type"": ""string"", ""enum"": [""pipe"", ""duct""] },
          ""size"": " + SizeSchema + @",
          ""length_mm"": { ""type"": ""number"", ""description"": ""Lunghezza del tratto in mm. Se non indicata: 3000."" },
          ""direction"": { ""type"": ""string"", ""enum"": [""default"", ""plus_x"", ""minus_x"", ""plus_y"", ""minus_y"", ""up"", ""down""] },
          ""type_name"": { ""anyOf"": [{ ""type"": ""string"" }, { ""type"": ""null"" }], ""description"": ""Nome ESATTO di un tipo presente nel catalogo, se l'utente lo indica o se un tipo del catalogo corrisponde chiaramente al materiale richiesto. Altrimenti null."" },
          ""type_hints"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Parole chiave su materiale/tipo (es. acciaio, pvc, rame)."" },
          ""system_class"": { ""anyOf"": [{ ""type"": ""string"", ""enum"": [""DomesticColdWater"", ""DomesticHotWater"", ""SupplyHydronic"", ""ReturnHydronic"", ""Sanitary"", ""Vent"", ""FireProtectWet"", ""OtherPipe"", ""SupplyAir"", ""ReturnAir"", ""ExhaustAir"", ""OtherDuct""] }, { ""type"": ""null"" }] },
          ""system_name"": { ""anyOf"": [{ ""type"": ""string"" }, { ""type"": ""null"" }], ""description"": ""Nome ESATTO di un tipo di sistema del catalogo, se identificabile. Altrimenti null."" },
          ""level"": { ""anyOf"": [{ ""type"": ""string"" }, { ""type"": ""null"" }], ""description"": ""Nome ESATTO di un livello del catalogo, se indicato. Altrimenti null."" },
          ""elevation_mm"": { ""anyOf"": [{ ""type"": ""number"" }, { ""type"": ""null"" }], ""description"": ""Quota rispetto al livello in mm, se indicata."" },
          ""continues_previous"": { ""type"": ""boolean"", ""description"": ""true se il tratto prosegue dalla fine del tratto precedente (collegato con gomito/transizione)."" },
          ""branches"": {
            ""type"": ""array"",
            ""items"": {
              ""type"": ""object"", ""additionalProperties"": false,
              ""properties"": {
                ""size"": " + SizeSchema + @",
                ""count"": { ""type"": ""integer"", ""description"": ""Numero di stacchi di questo gruppo (plurale senza numero: 2)."" },
                ""length_mm"": { ""type"": ""number"", ""description"": ""Lunghezza di ogni stacco in mm. Se non indicata: 500."" },
                ""direction"": { ""type"": ""string"", ""enum"": [""default"", ""up"", ""down"", ""left"", ""right"", ""alternate"", ""plus_x"", ""minus_x"", ""plus_y"", ""minus_y""] },
                ""spacing_mm"": { ""anyOf"": [{ ""type"": ""number"" }, { ""type"": ""null"" }], ""description"": ""Interasse tra gli stacchi in mm, se indicato."" },
                ""positions_mm"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""Posizioni esplicite dall'inizio del tratto in mm, se indicate."" }
              },
              ""required"": [""size"", ""count"", ""length_mm"", ""direction"", ""spacing_mm"", ""positions_mm""]
            }
          }
        },
        ""required"": [""kind"", ""size"", ""length_mm"", ""direction"", ""type_name"", ""type_hints"", ""system_class"", ""system_name"", ""level"", ""elevation_mm"", ""continues_previous"", ""branches""]
      }
    },
    ""warnings"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Ambiguità o assunzioni fatte, in italiano, da mostrare all'utente."" },
    ""unsupported"": { ""anyOf"": [{ ""type"": ""string"" }, { ""type"": ""null"" }], ""description"": ""Se la richiesta non riguarda tubazioni o canali, spiega qui perché non è supportata; altrimenti null."" }
  },
  ""required"": [""runs"", ""warnings"", ""unsupported""]
}";

        public static Dictionary<string, JsonElement> SchemaAsDictionary()
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Schema);
        }

        /// <summary>Converte il JSON prodotto dal modello in un <see cref="ParseResult"/>.</summary>
        public static ParseResult FromJson(string json, string sourceText)
        {
            var result = new ParseResult { Plan = new MepPlan { SourceText = sourceText } };
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("unsupported", out var uns) && uns.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(uns.GetString()))
                {
                    return ParseResult.Fail(uns.GetString());
                }
                if (root.TryGetProperty("warnings", out var warns) && warns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var w in warns.EnumerateArray()) if (w.ValueKind == JsonValueKind.String) result.Warnings.Add(w.GetString());
                }
                if (!root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                    return ParseResult.Fail("Risposta del modello priva di tratti.");

                foreach (var r in runs.EnumerateArray())
                {
                    var run = new MepRun
                    {
                        Kind = Str(r, "kind") == "duct" ? MepKind.Duct : MepKind.Pipe,
                        Size = ReadSize(r, "size"),
                        LengthMm = Num(r, "length_mm") ?? 3000,
                        Direction = ReadDirection(Str(r, "direction")),
                        ExplicitTypeName = Str(r, "type_name"),
                        SystemClass = Str(r, "system_class"),
                        SystemPhrase = Str(r, "system_name"),
                        LevelHint = Str(r, "level"),
                        ElevationMm = Num(r, "elevation_mm"),
                        ContinuesPrevious = Bool(r, "continues_previous")
                    };
                    if (r.TryGetProperty("type_hints", out var th) && th.ValueKind == JsonValueKind.Array)
                        foreach (var h in th.EnumerateArray()) if (h.ValueKind == JsonValueKind.String) run.TypeHints.Add(h.GetString().ToLowerInvariant());
                    if (run.Size == null)
                    {
                        run.Size = run.Kind == MepKind.Pipe ? MepSize.Round(50, true) : MepSize.Rectangular(300, 200);
                        result.Warnings.Add("Dimensione non indicata: uso " + run.Size + ".");
                    }
                    if (run.LengthMm <= 0) run.LengthMm = 3000;

                    if (r.TryGetProperty("branches", out var brs) && brs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var b in brs.EnumerateArray())
                        {
                            var br = new MepBranch
                            {
                                Size = ReadSize(b, "size") ?? run.Size.Clone(),
                                Count = (int)(Num(b, "count") ?? 1),
                                LengthMm = Num(b, "length_mm") ?? 500,
                                Direction = ReadDirection(Str(b, "direction")),
                                SpacingMm = Num(b, "spacing_mm")
                            };
                            if (br.Count < 1) br.Count = 1;
                            if (br.Count > 200) br.Count = 200;
                            if (br.LengthMm <= 0) br.LengthMm = 500;
                            if (br.SpacingMm.HasValue && br.SpacingMm.Value <= 0) br.SpacingMm = null;
                            if (b.TryGetProperty("positions_mm", out var pos) && pos.ValueKind == JsonValueKind.Array)
                                foreach (var p in pos.EnumerateArray()) if (p.ValueKind == JsonValueKind.Number) br.PositionsMm.Add(p.GetDouble());
                            run.Branches.Add(br);
                        }
                    }
                    result.Plan.Runs.Add(run);
                }
            }

            if (result.Plan.Runs.Count == 0) return ParseResult.Fail("Il modello non ha riconosciuto tubazioni o canali nella descrizione.");
            result.Success = true;
            return result;
        }

        private static MepSize ReadSize(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var s) || s.ValueKind != JsonValueKind.Object) return null;
            var shape = Str(s, "shape");
            if (shape == "rectangular")
            {
                var w = Num(s, "width_mm") ?? 0;
                var h = Num(s, "height_mm") ?? 0;
                if (w <= 0 || h <= 0) return null;
                return MepSize.Rectangular(w, h);
            }
            var d = Num(s, "diameter_mm") ?? 0;
            if (d <= 0) return null;
            return MepSize.Round(d, Bool(s, "is_dn"));
        }

        private static DirectionKind ReadDirection(string d)
        {
            switch (d)
            {
                case "plus_x": return DirectionKind.PlusX;
                case "minus_x": return DirectionKind.MinusX;
                case "plus_y": return DirectionKind.PlusY;
                case "minus_y": return DirectionKind.MinusY;
                case "up": return DirectionKind.Up;
                case "down": return DirectionKind.Down;
                case "left": return DirectionKind.Left;
                case "right": return DirectionKind.Right;
                case "alternate": return DirectionKind.Alternate;
                default: return DirectionKind.Default;
            }
        }

        private static string Str(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static double? Num(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;
        }

        private static bool Bool(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        }
    }
}
