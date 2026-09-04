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
        /// <summary>Quota del collettore/tubazione sopra il livello (mm), quando il testo non la indica.</summary>
        public double DefaultElevationMm { get; set; } = 2500;
        /// <summary>Punto di partenza: "origin" (origine del progetto, predefinito) o "pick" (scelto nel modello dopo Crea).</summary>
        public string StartMode { get; set; } = "origin";
        public string LastText { get; set; } = string.Empty;
        /// <summary>Nome del tipo di tubazione scelto nella finestra; vuoto = automatico.</summary>
        public string PipeTypeName { get; set; } = string.Empty;

        // --- modalità Collettore (parametrica) ---
        public bool ManifoldMode { get; set; }
        /// <summary>DN del collettore in mm; 0 = calcolato automaticamente dai circuiti.</summary>
        public double ManifoldHeaderDnMm { get; set; }
        public double ManifoldSpacingMm { get; set; } = 150;
        /// <summary>Se true l'interasse è il minimo senza interferenze, calcolato in Revit; ManifoldSpacingMm fa da pavimento.</summary>
        public bool ManifoldAutoSpacing { get; set; } = true;
        public double ManifoldCircuitLengthMm { get; set; } = 500;
        /// <summary>Circuito senza pompa: tubo dopo la valvola (dalla seconda flangia), mandata e ritorno (mm).</summary>
        public double ManifoldNoPumpPipeAfterValveMm { get; set; } = 2000;
        public string ManifoldCircuitDirection { get; set; } = "Down";
        /// <summary>Circuiti separati da ";" come DN:tipologia (es. "20:direct;16:mix3;16:nopump"); il DN nudo vale come diretto.</summary>
        public string ManifoldCircuits { get; set; } = string.Empty;
        /// <summary>Tipo di tubazione scelto nella sezione collettore (scelta deterministica).</summary>
        public string ManifoldPipeTypeName { get; set; } = string.Empty;
        /// <summary>Se true si crea anche il collettore di ritorno (clone speculare interlacciato).</summary>
        public bool ManifoldWithReturn { get; set; } = true;
        public double ManifoldReturnOffsetMm { get; set; } = 300;

        // --- valvole sugli stacchi del collettore ---
        /// <summary>Se true su ogni stacco viene inserita una valvola in linea.</summary>
        public bool ManifoldWithValves { get; set; } = true;
        /// <summary>DN massimo (incluso) per la valvola a sfera; oltre si usa la boax.</summary>
        public double ManifoldBallValveMaxDnMm { get; set; } = 32;
        public string ManifoldBallValveFamily { get; set; } = string.Empty;
        public string ManifoldButterflyValveFamily { get; set; } = string.Empty;
        /// <summary>PN preferito nei nomi dei tipi delle valvole; 0 = indifferente.</summary>
        public double ManifoldValvePnBar { get; set; } = 16;
        /// <summary>Distanza dal bordo esterno del collettore al centro della valvola (mm).</summary>
        public double ManifoldValveDistanceMm { get; set; } = 150;
        /// <summary>Rotazione della boax attorno all'asse del tubo (gradi).</summary>
        public double ManifoldButterflyRollDeg { get; set; } = 90;
        /// <summary>Rotazione della valvola a sfera attorno all'asse del tubo (gradi).</summary>
        public double ManifoldBallRollDeg { get; set; } = 90;

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
                if (File.Exists(FilePath)) s.MergeFrom(FilePath);
            }
            catch
            {
                // impostazioni corrotte: si usano i valori predefiniti
            }
            return s;
        }

        /// <summary>Applica le righe chiave=valore di un file sopra i valori correnti (usato anche dall'automazione).</summary>
        public void MergeFrom(string path)
        {
            var s = this;
            foreach (var line in File.ReadAllLines(path))
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
                case "StartMode": s.StartMode = v == "pick" ? "pick" : "origin"; break;
                case "LastText": s.LastText = v.Replace("\\n", "\n"); break;
                case "PipeTypeName": s.PipeTypeName = v; break;
                case "ManifoldMode": s.ManifoldMode = v == "true"; break;
                case "ManifoldHeaderDnMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var hdn)) s.ManifoldHeaderDnMm = hdn; break;
                case "ManifoldSpacingMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp)) s.ManifoldSpacingMm = sp; break;
                case "ManifoldAutoSpacing": s.ManifoldAutoSpacing = v == "true"; break;
                case "ManifoldCircuitLengthMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var cl)) s.ManifoldCircuitLengthMm = cl; break;
                case "ManifoldNoPumpPipeAfterValveMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var np)) s.ManifoldNoPumpPipeAfterValveMm = np; break;
                case "ManifoldCircuitDirection": s.ManifoldCircuitDirection = v; break;
                case "ManifoldCircuits": s.ManifoldCircuits = v; break;
                case "ManifoldPipeTypeName": s.ManifoldPipeTypeName = v; break;
                case "ManifoldWithReturn": s.ManifoldWithReturn = v == "true"; break;
                case "ManifoldReturnOffsetMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ro)) s.ManifoldReturnOffsetMm = ro; break;
                case "ManifoldWithValves": s.ManifoldWithValves = v == "true"; break;
                case "ManifoldBallValveMaxDnMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var bmax)) s.ManifoldBallValveMaxDnMm = bmax; break;
                case "ManifoldBallValveFamily": s.ManifoldBallValveFamily = v; break;
                case "ManifoldButterflyValveFamily": s.ManifoldButterflyValveFamily = v; break;
                case "ManifoldValvePnBar": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var pn)) s.ManifoldValvePnBar = pn; break;
                case "ManifoldValveDistanceMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vd)) s.ManifoldValveDistanceMm = vd; break;
                case "ManifoldButterflyRollDeg": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var br)) s.ManifoldButterflyRollDeg = br; break;
                case "ManifoldBallRollDeg": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var sr)) s.ManifoldBallRollDeg = sr; break;
            }
            }
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
                    "LastText=" + (LastText ?? string.Empty).Replace("\r", string.Empty).Replace("\n", "\\n"),
                    "PipeTypeName=" + (PipeTypeName ?? string.Empty),
                    "ManifoldMode=" + (ManifoldMode ? "true" : "false"),
                    "ManifoldHeaderDnMm=" + ManifoldHeaderDnMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldSpacingMm=" + ManifoldSpacingMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldAutoSpacing=" + (ManifoldAutoSpacing ? "true" : "false"),
                    "ManifoldCircuitLengthMm=" + ManifoldCircuitLengthMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldNoPumpPipeAfterValveMm=" + ManifoldNoPumpPipeAfterValveMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldCircuitDirection=" + ManifoldCircuitDirection,
                    "ManifoldCircuits=" + (ManifoldCircuits ?? string.Empty),
                    "ManifoldPipeTypeName=" + (ManifoldPipeTypeName ?? string.Empty),
                    "ManifoldWithReturn=" + (ManifoldWithReturn ? "true" : "false"),
                    "ManifoldReturnOffsetMm=" + ManifoldReturnOffsetMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldWithValves=" + (ManifoldWithValves ? "true" : "false"),
                    "ManifoldBallValveMaxDnMm=" + ManifoldBallValveMaxDnMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldBallValveFamily=" + (ManifoldBallValveFamily ?? string.Empty),
                    "ManifoldButterflyValveFamily=" + (ManifoldButterflyValveFamily ?? string.Empty),
                    "ManifoldValvePnBar=" + ManifoldValvePnBar.ToString(CultureInfo.InvariantCulture),
                    "ManifoldValveDistanceMm=" + ManifoldValveDistanceMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldButterflyRollDeg=" + ManifoldButterflyRollDeg.ToString(CultureInfo.InvariantCulture),
                    "ManifoldBallRollDeg=" + ManifoldBallRollDeg.ToString(CultureInfo.InvariantCulture)
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
