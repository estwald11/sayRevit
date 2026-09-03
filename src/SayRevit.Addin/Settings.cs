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
                    "LastText=" + (LastText ?? string.Empty).Replace("\r", string.Empty).Replace("\n", "\\n")
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
