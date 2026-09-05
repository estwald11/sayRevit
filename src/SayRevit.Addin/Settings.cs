using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SayRevit.Core.Model;

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
        /// <summary>PN preferito nei nomi dei tipi delle valvole; 0 = indifferente.</summary>
        public double ManifoldValvePnBar { get; set; } = 16;
        /// <summary>Distanza dal bordo esterno del collettore al centro della valvola (mm).</summary>
        public double ManifoldValveDistanceMm { get; set; } = 150;
        /// <summary>Rotazione della boax attorno all'asse del tubo (gradi).</summary>
        public double ManifoldButterflyRollDeg { get; set; } = 90;
        /// <summary>Rotazione della valvola a sfera attorno all'asse del tubo (gradi).</summary>
        public double ManifoldBallRollDeg { get; set; } = 90;

        // --- famiglie degli elementi (registro ManifoldElements): una chiave per elemento ---
        // Ogni chiave "…Family" (es. ManifoldZoneValveFamily) è una famiglia per DN nella forma di
        // FamilyByDn: "famiglia" oppure "famiglia|40=altra|100=terza" (soglie "da DN in su").
        // Per la energy valve il vuoto significa "automatica sul nome" (ev025r2… per DN25).
        /// <summary>Famiglia per DN di ogni elemento, per chiave del registro (ball, butterfly, energy, zone, strainer, check).</summary>
        public Dictionary<string, string> ManifoldFamilies { get; } =
            ManifoldElements.All.ToDictionary(e => e.Key, e => string.Empty, StringComparer.OrdinalIgnoreCase);

        /// <summary>Famiglia per DN salvata per l'elemento; vuota se mai impostata.</summary>
        public string Family(string elementKey)
        {
            string value;
            return ManifoldFamilies.TryGetValue(elementKey, out value) ? value ?? string.Empty : string.Empty;
        }

        // --- mix 2 vie (iniezione): lunghezze della catena ---
        /// <summary>Tubo libero tra i pezzi della catena e attorno ai T (mm).</summary>
        public double ManifoldMix2GapMm { get; set; } = 150;
        /// <summary>Tubo tra pezzi flangiati consecutivi (mm); mai sotto i 50 mm di tubo diritto.</summary>
        public double ManifoldMix2FlangedGapMm { get; set; } = 50;
        /// <summary>Spazio riservato alla pompa sulla mandata (mm).</summary>
        public double ManifoldMix2PumpSpaceMm { get; set; } = 400;
        /// <summary>Tubo dopo l'intercettazione in cima alla catena (mm).</summary>
        public double ManifoldMix2EndPipeMm { get; set; } = 100;
        /// <summary>Rotazione degli accessori del mix 2 vie attorno all'asse del tubo (gradi).</summary>
        public double ManifoldMix2RollDeg { get; set; } = 90;
        /// <summary>Rotazione del filtro a Y attorno all'asse del tubo (gradi).</summary>
        public double ManifoldStrainerRollDeg { get; set; } = 270;
        /// <summary>Filtro a Y montato col verso invertito rispetto alla famiglia.</summary>
        public bool ManifoldStrainerReversed { get; set; } = true;

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
                // famiglie degli elementi: la chiave viene dal registro, non serve un case per elemento
                var element = ManifoldElements.BySettingsKey(k);
                if (element != null)
                {
                    s.ManifoldFamilies[element.Key] = v;
                    continue;
                }
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
                case "ManifoldValvePnBar": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var pn)) s.ManifoldValvePnBar = pn; break;
                case "ManifoldValveDistanceMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vd)) s.ManifoldValveDistanceMm = vd; break;
                case "ManifoldButterflyRollDeg": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var br)) s.ManifoldButterflyRollDeg = br; break;
                case "ManifoldBallRollDeg": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var sr)) s.ManifoldBallRollDeg = sr; break;
                case "ManifoldMix2GapMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var g1)) s.ManifoldMix2GapMm = g1; break;
                case "ManifoldMix2FlangedGapMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var g2)) s.ManifoldMix2FlangedGapMm = g2; break;
                case "ManifoldMix2PumpSpaceMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) s.ManifoldMix2PumpSpaceMm = ps; break;
                case "ManifoldMix2EndPipeMm": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ep)) s.ManifoldMix2EndPipeMm = ep; break;
                case "ManifoldMix2RollDeg": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var mr)) s.ManifoldMix2RollDeg = mr; break;
                case "ManifoldStrainerRollDeg": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var fr)) s.ManifoldStrainerRollDeg = fr; break;
                case "ManifoldStrainerReversed": s.ManifoldStrainerReversed = v == "true"; break;
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
                    "ManifoldValvePnBar=" + ManifoldValvePnBar.ToString(CultureInfo.InvariantCulture),
                    "ManifoldValveDistanceMm=" + ManifoldValveDistanceMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldButterflyRollDeg=" + ManifoldButterflyRollDeg.ToString(CultureInfo.InvariantCulture),
                    "ManifoldBallRollDeg=" + ManifoldBallRollDeg.ToString(CultureInfo.InvariantCulture),
                    "ManifoldMix2GapMm=" + ManifoldMix2GapMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldMix2FlangedGapMm=" + ManifoldMix2FlangedGapMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldMix2PumpSpaceMm=" + ManifoldMix2PumpSpaceMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldMix2EndPipeMm=" + ManifoldMix2EndPipeMm.ToString(CultureInfo.InvariantCulture),
                    "ManifoldMix2RollDeg=" + ManifoldMix2RollDeg.ToString(CultureInfo.InvariantCulture),
                    "ManifoldStrainerRollDeg=" + ManifoldStrainerRollDeg.ToString(CultureInfo.InvariantCulture),
                    "ManifoldStrainerReversed=" + (ManifoldStrainerReversed ? "true" : "false")
                };
                // una riga per elemento del registro (ManifoldBallValveFamily=…, ManifoldZoneValveFamily=…)
                foreach (var element in ManifoldElements.All)
                    lines.Add(element.SettingsKey + "=" + Family(element.Key));
                File.WriteAllLines(FilePath, lines);
            }
            catch
            {
                // non bloccante
            }
        }
    }
}
