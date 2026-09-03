using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SayRevit.Core.Parsing
{
    /// <summary>Utilità testuali: normalizzazione, numeri (cifre, parole, frazioni) e unità.</summary>
    public static class TextUtil
    {
        public const string NumberPattern = @"(?:\d+(?:[.,]\d+)?(?:\s+\d+/\d+)?|\d+/\d+)";

        private static readonly Dictionary<string, int> NumberWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"zero", 0}, {"uno", 1}, {"una", 1}, {"un", 1}, {"un'", 1}, {"due", 2}, {"tre", 3}, {"quattro", 4}, {"cinque", 5},
            {"sei", 6}, {"sette", 7}, {"otto", 8}, {"nove", 9}, {"dieci", 10}, {"undici", 11}, {"dodici", 12},
            {"quindici", 15}, {"venti", 20},
            {"one", 1}, {"a", 1}, {"an", 1}, {"two", 2}, {"three", 3}, {"four", 4}, {"five", 5}, {"six", 6},
            {"seven", 7}, {"eight", 8}, {"nine", 9}, {"ten", 10}, {"eleven", 11}, {"twelve", 12}, {"fifteen", 15}, {"twenty", 20},
            {"single", 1}, {"singolo", 1}, {"singola", 1}, {"doppio", 2}, {"doppia", 2}, {"double", 2}, {"coppia", 2}, {"pair", 2}
        };

        /// <summary>Alternanza regex di tutte le parole-numero conosciute.</summary>
        public static readonly string NumberWordPattern = BuildNumberWordPattern();

        private static string BuildNumberWordPattern()
        {
            var sb = new StringBuilder("(?:");
            var first = true;
            foreach (var k in NumberWords.Keys)
            {
                if (!first) sb.Append('|');
                first = false;
                sb.Append(Regex.Escape(k));
            }
            sb.Append(')');
            return sb.ToString();
        }

        public static bool TryParseNumberWord(string word, out int value)
        {
            return NumberWords.TryGetValue(word.Trim(), out value);
        }

        /// <summary>Converte "2,5", "2.5", "1 1/2", "3/4" in double.</summary>
        public static bool TryParseNumber(string s, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Replace(',', '.');
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double total = 0;
            foreach (var p in parts)
            {
                if (p.Contains("/"))
                {
                    var f = p.Split('/');
                    if (f.Length != 2) return false;
                    if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
                    if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d == 0) return false;
                    total += n / d;
                }
                else
                {
                    if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
                    total += n;
                }
            }
            value = total;
            return true;
        }

        /// <summary>Unità di lunghezza riconosciute (alternanza regex).</summary>
        public const string LengthUnitPattern = @"(?:mm|millimetri|millimetro|cm|centimetri|centimetro|m\b|mt\b|metri|metro|meters?|metres?|ft\b|feet|foot|piedi|piede|in\b|inch(?:es)?|pollic[ei]|"")";

        /// <summary>Fattore per convertire l'unità indicata in millimetri.</summary>
        public static double UnitToMm(string unit)
        {
            if (string.IsNullOrEmpty(unit)) return 1;
            unit = unit.Trim().ToLowerInvariant();
            if (unit.StartsWith("mm") || unit.StartsWith("milli")) return 1;
            if (unit.StartsWith("cm") || unit.StartsWith("centi")) return 10;
            if (unit == "m" || unit == "mt" || unit.StartsWith("metr") || unit.StartsWith("meter")) return 1000;
            if (unit == "ft" || unit.StartsWith("feet") || unit.StartsWith("foot") || unit.StartsWith("pied")) return 304.8;
            if (unit == "in" || unit.StartsWith("inch") || unit.StartsWith("pollic") || unit == "\"") return 25.4;
            return 1;
        }

        /// <summary>Normalizza il testo per il parsing (minuscolo, simboli, spazi).</summary>
        public static string Normalize(string text)
        {
            if (text == null) return string.Empty;
            var t = text.ToLowerInvariant();
            t = t.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            t = t.Replace('×', 'x').Replace('*', 'x');
            t = t.Replace("ø", " diametro ").Replace("⌀", " diametro ").Replace("Ø", " diametro ");
            t = t.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"').Replace('″', '"');
            t = Regex.Replace(t, @"\bdiam\.?\b", "diametro ");
            t = Regex.Replace(t, @"\bn\.\s*(?=\d)", "n ");
            t = Regex.Replace(t, @"\bnr\.?\s*(?=\d)", "n ");
            t = Regex.Replace(t, @"\bd\s*n\s*(?=\d)", "dn");
            t = Regex.Replace(t, @"\s+", " ");
            return t.Trim();
        }

        /// <summary>Sostituisce con spazi l'intervallo indicato (per "consumare" una parte già interpretata).</summary>
        public static string Blank(string s, int index, int length)
        {
            if (length <= 0) return s;
            return s.Substring(0, index) + new string(' ', length) + s.Substring(index + length);
        }

        /// <summary>Confronto "fuzzy" tra due nomi: uguaglianza, contenimento o token in comune.</summary>
        public static int NameScore(string candidate, string hint)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(hint)) return 0;
            var c = Fold(candidate);
            var h = Fold(hint);
            if (c == h) return 100;
            if (c.Contains(h)) return 80;
            if (h.Contains(c)) return 60;
            var ct = c.Split(new[] { ' ', '-', '_', '/', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var ht = h.Split(new[] { ' ', '-', '_', '/', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var score = 0;
            foreach (var a in ht)
            {
                if (a.Length < 2) continue;
                foreach (var b in ct)
                {
                    if (a == b) score += 20;
                    else if (b.StartsWith(a) || a.StartsWith(b)) score += 10;
                }
            }
            return score;
        }

        /// <summary>Minuscolo senza accenti.</summary>
        public static string Fold(string s)
        {
            if (s == null) return string.Empty;
            var norm = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(norm.Length);
            foreach (var ch in norm)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).Trim();
        }
    }
}
