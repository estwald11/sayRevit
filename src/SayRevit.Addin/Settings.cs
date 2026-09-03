using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SayRevit.Addin
{
    /// <summary>Impostazioni persistite in %APPDATA%\sayRevit\settings.txt (formato chiave=valore, nessuna dipendenza).</summary>
    public sealed class Settings
    {
        public string ParserMode { get; set; } = "rules";       // rules | claude
        public string ClaudeModel { get; set; } = "claude-opus-5";
        public double DefaultElevationMm { get; set; } = 2500;
        public string StartMode { get; set; } = "origin";       // origin | pick
        public bool UsePickedZ { get; set; }
        public string LastText { get; set; } = string.Empty;
        /// <summary>Nome del tipo di tubazione scelto nella finestra; vuoto = automatico.</summary>
        public string PipeTypeName { get; set; } = string.Empty;

        // --- modalità Collettore (parametrica) ---
        public bool ManifoldMode { get; set; }
        /// <summary>DN del collettore in mm; 0 = calcolato automaticamente dai circuiti.</summary>
        public double ManifoldHeaderDnMm { get; set; }
        public double ManifoldSpacingMm { get; set; } = 150;
        public double ManifoldCircuitLengthMm { get; set; } = 500;
        public string ManifoldHeaderDirection { get; set; } = "PlusX";
        public string ManifoldCircuitDirection { get; set; } = "Down";
        /// <summary>DN dei circuiti separati da ";" (es. "20;16;16").</summary>
        public string ManifoldCircuits { get; set; } = string.Empty;
        /// <summary>Tipo di tubazione scelto nella sezione collettore (scelta deterministica).</summary>
        public string ManifoldPipeTypeName { get; set; } = string.Empty;
        /// <summary>Se true si crea anche il collettore di ritorno (clone speculare interlacciato).</summary>
        public bool ManifoldWithReturn { get; set; } = true;
        public double ManifoldReturnOffsetMm { get; set; } = 300;

        public static string FilePath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sayRevit");
                return Path.Combine(dir, "settings.txt");
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "ParserMode": s.ParserMode = v; break;
                        case "ClaudeModel": s.ClaudeModel = v; break;
                        case "DefaultElevationMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) s.DefaultElevationMm = d; break;
                        case "StartMode": s.StartMode = v; break;
                        case "UsePickedZ": s.UsePickedZ = v == "true"; break;
                        case "LastText": s.LastText = v.Replace("\\n", "\n"); break;
                        case "PipeTypeName": s.PipeTypeName = v; break;
                        case "ManifoldMode": s.ManifoldMode = v == "true"; break;
                        case "ManifoldHeaderDnMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var hdn)) s.ManifoldHeaderDnMm = hdn; break;
                        case "ManifoldSpacingMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp)) s.ManifoldSpacingMm = sp; break;
                        case "ManifoldCircuitLengthMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var cl)) s.ManifoldCircuitLengthMm = cl; break;
                        case "ManifoldHeaderDirection": s.ManifoldHeaderDirection = v; break;
                        case "ManifoldCircuitDirection": s.ManifoldCircuitDirection = v; break;
                        case "ManifoldCircuits": s.ManifoldCircuits = v; break;
                        case "ManifoldPipeTypeName": s.ManifoldPipeTypeName = v; break;
                        case "ManifoldWithReturn": s.ManifoldWithReturn = v == "true"; break;
                        case "ManifoldReturnOffsetMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ro)) s.ManifoldReturnOffsetMm = ro; break;
                    }
                }
            }
            catch
            {
                // impostazioni corrotte: si usano i valori predefiniti
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>
                {
                    "ParserMode=" + ParserMode,
                    "ClaudeModel=" + ClaudeModel,
                    "DefaultElevationMm=" + DefaultElevationMm.ToString(CultureInfo.InvariantCulture),
                    "StartMode=" + StartMode,
                    "UsePickedZ=" + (UsePickedZ ? "true" : "false"),
                    "LastText=" + (LastText ?? string.Empty).Replace("\r", string.Empty).Replace("\n", "\\n"),
                    "PipeTypeName=" + (PipeTypeName ?? string.Empty),
                    "ManifoldMode=" + (ManifoldMode ? "true" : "false"),
                    "ManifoldHeaderDnMm=" + ManifoldHeaderDnMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldSpacingMm=" + ManifoldSpacingMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldCircuitLengthMm=" + ManifoldCircuitLengthMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldHeaderDirection=" + ManifoldHeaderDirection,
                    "ManifoldCircuitDirection=" + ManifoldCircuitDirection,
                    "ManifoldCircuits=" + (ManifoldCircuits ?? string.Empty),
                    "ManifoldPipeTypeName=" + (ManifoldPipeTypeName ?? string.Empty),
                    "ManifoldWithReturn=" + (ManifoldWithReturn ? "true" : "false"),
                    "ManifoldReturnOffsetMm=" + ManifoldReturnOffsetMm.ToString(CultureInfo.InvariantCulture)
                };
                File.WriteAllLines(FilePath, lines);
            }
            catch
            {
                // non bloccante
            }
        }
    }
}
