using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SayRevit.Core.Parsing;

namespace SayRevit.Core.Model
{
    /// <summary>Tipo di pezzo in linea previsto su uno stacco (valvole e accessori).</summary>
    public enum ValveKind
    {
        None,
        /// <summary>Valvola a sfera: usata fino al DN di soglia.</summary>
        Ball,
        /// <summary>Valvola a farfalla (boax): usata oltre il DN di soglia.</summary>
        Butterfly,
        /// <summary>Valvola di regolazione a 2 vie (Belimo Energy Valve), una famiglia per DN.</summary>
        EnergyValve,
        /// <summary>Valvola di zona: farfalla wafer con riduttore manuale (Watts), tra due flange.</summary>
        ZoneValve,
        /// <summary>Filtro a Y flangiato.</summary>
        Strainer,
        /// <summary>Valvola di ritegno wafer sul bypass, tra due flange.</summary>
        CheckValve,
        /// <summary>Pompa di circolazione in linea (Grundfos MAGNA3), sulla mandata dopo il bypass.</summary>
        Pump
    }

    /// <summary>Tipo di famiglia scelto per una valvola, con quello che si ricava dal suo nome.</summary>
    public sealed class ValveTypePick
    {
        public string TypeName { get; set; }

        /// <summary>DN letto dal nome del tipo (mm); 0 = non leggibile.</summary>
        public double DnMm { get; set; }

        /// <summary>PN letto dal nome del tipo (bar); 0 = non indicato.</summary>
        public double PnBar { get; set; }

        /// <summary>True se il DN del tipo coincide con quello richiesto.</summary>
        public bool ExactDn { get; set; }

        /// <summary>True se il PN richiesto è rispettato (o non è indicato nei nomi dei tipi).</summary>
        public bool ExactPn { get; set; }
    }

    /// <summary>
    /// Sceglie il tipo di una famiglia di valvole a partire dal DN del tubo leggendo il NOME dei tipi.
    /// Riconosce sia la forma metrica ("DN40_PN6_48013980") sia quella in pollici ("1 1/2 pollici Lever").
    /// </summary>
    public static class ValveTypeMatcher
    {
        private static readonly Regex DnRx = new Regex(@"dn\s*[-_]?\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);
        private static readonly Regex PnRx = new Regex(@"pn\s*[-_]?\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);
        private static readonly Regex BareRx = new Regex(@"(?<!\d)(\d{1,3})(?!\d)");
        /// <summary>Codici Giacomini R60 ("R60Y002"): il numero conta lungo la serie DN commerciale a partire da DN10.</summary>
        private static readonly Regex GiacominiR60Rx = new Regex(@"^\s*r60y0*(\d+)\b", RegexOptions.IgnoreCase);

        /// <summary>Corrispondenza pollici → DN commerciale (le misure che compaiono nei nomi dei tipi).</summary>
        private static readonly KeyValuePair<double, double>[] InchToDn =
        {
            new KeyValuePair<double, double>(0.25, 8),
            new KeyValuePair<double, double>(0.375, 10),
            new KeyValuePair<double, double>(0.5, 15),
            new KeyValuePair<double, double>(0.75, 20),
            new KeyValuePair<double, double>(1, 25),
            new KeyValuePair<double, double>(1.25, 32),
            new KeyValuePair<double, double>(1.5, 40),
            new KeyValuePair<double, double>(2, 50),
            new KeyValuePair<double, double>(2.5, 65),
            new KeyValuePair<double, double>(3, 80),
            new KeyValuePair<double, double>(4, 100),
            new KeyValuePair<double, double>(5, 125),
            new KeyValuePair<double, double>(6, 150),
            new KeyValuePair<double, double>(8, 200),
            new KeyValuePair<double, double>(10, 250),
            new KeyValuePair<double, double>(12, 300),
            new KeyValuePair<double, double>(14, 350),
            new KeyValuePair<double, double>(16, 400),
            new KeyValuePair<double, double>(18, 450),
            new KeyValuePair<double, double>(20, 500),
            new KeyValuePair<double, double>(24, 600)
        };

        /// <summary>DN (mm) letto dal nome del tipo; null se il nome non lo dichiara.</summary>
        public static double? DnFromTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;

            var m = DnRx.Match(typeName);
            if (m.Success && TryNumber(m.Groups[1].Value, out var dn) && dn > 0) return dn;

            var inches = InchesFromTypeName(typeName);
            if (inches.HasValue) return DnFromInches(inches.Value);

            // Giacomini R60: "R60Y002" = DN10, "R60Y003" = DN15, "R60Y004" = DN20… lungo la serie commerciale
            var code = GiacominiR60Rx.Match(typeName);
            if (code.Success && int.TryParse(code.Groups[1].Value, out var idx) && idx >= 2 && idx - 2 < ManifoldPlan.DnSeries.Length)
                return ManifoldPlan.DnSeries[idx - 2];

            // Numero nudo (es. "40", o il 25 di "MAGNA3 25-60 PN10"): vale il primo che è un DN
            // commerciale, così i codici articolo ("48013978") e i suffissi ("MAGNA3") non vengono
            // scambiati per una misura.
            foreach (Match bare in BareRx.Matches(typeName))
            {
                if (TryNumber(bare.Groups[1].Value, out var n) && ManifoldPlan.DnSeries.Any(d => Math.Abs(d - n) < 0.001))
                    return n;
            }

            return null;
        }

        /// <summary>PN (bar) letto dal nome del tipo; null se il nome non lo dichiara.</summary>
        public static double? PnFromTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            var m = PnRx.Match(typeName);
            return m.Success && TryNumber(m.Groups[1].Value, out var pn) && pn > 0 ? pn : (double?)null;
        }

        /// <summary>Pollici dichiarati nel nome (1 1/2 + simbolo pollici → 1,5); null se non ce ne sono.</summary>
        public static double? InchesFromTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            var q = typeName.IndexOfAny(new[] { '"', '″' });
            if (q <= 0) return null;
            var i = q - 1;
            while (i >= 0 && (char.IsDigit(typeName[i]) || typeName[i] == '/' || typeName[i] == ' ' ||
                              typeName[i] == '-' || typeName[i] == '.' || typeName[i] == ','))
                i--;
            var text = typeName.Substring(i + 1, q - i - 1).Replace('-', ' ').Trim();
            if (text.Length == 0) return null;
            return TextUtil.TryParseNumber(text, out var inches) && inches > 0 ? inches : (double?)null;
        }

        /// <summary>Pollici → DN commerciale; fuori tabella si arrotonda alla serie DN.</summary>
        public static double DnFromInches(double inches)
        {
            foreach (var kv in InchToDn)
            {
                if (Math.Abs(kv.Key - inches) < 0.02) return kv.Value;
            }
            return ManifoldPlan.SnapUpToDn(inches * 25.4);
        }

        /// <summary>PN presenti nei nomi dei tipi, in ordine crescente (vuoto se non ne dichiarano).</summary>
        public static List<double> AvailablePn(IEnumerable<string> typeNames)
        {
            if (typeNames == null) return new List<double>();
            return typeNames.Select(PnFromTypeName).Where(p => p.HasValue).Select(p => p.Value)
                .Distinct().OrderBy(p => p).ToList();
        }

        /// <summary>
        /// Tipo della famiglia da usare per un tubo DN <paramref name="dnMm"/>: il DN esatto se c'è,
        /// altrimenti il DN più vicino. A parità di DN vince il PN richiesto (quando i nomi lo dichiarano).
        /// Restituisce null se nessun nome dichiara una misura leggibile.
        /// </summary>
        public static ValveTypePick Pick(IEnumerable<string> typeNames, double dnMm, double preferredPnBar)
        {
            return Pick(typeNames, dnMm, preferredPnBar, null);
        }

        /// <summary>
        /// Come <see cref="Pick(IEnumerable{string}, double, double)"/>, ma a parità di DN vince
        /// prima il tipo il cui nome contiene <paramref name="preferredWord"/> (es. "ductile" tra
        /// "DN50 - Cast Iron" e "DN50 - Ductile Iron"), poi il PN richiesto.
        /// </summary>
        public static ValveTypePick Pick(IEnumerable<string> typeNames, double dnMm, double preferredPnBar, string preferredWord)
        {
            if (typeNames == null || dnMm <= 0) return null;
            var word = string.IsNullOrWhiteSpace(preferredWord) ? null : TextUtil.Fold(preferredWord);

            var parsed = typeNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => new ValveTypePick
                {
                    TypeName = n,
                    DnMm = DnFromTypeName(n) ?? 0,
                    PnBar = PnFromTypeName(n) ?? 0
                })
                .Where(p => p.DnMm > 0)
                .ToList();
            if (parsed.Count == 0) return null;

            var exact = parsed.Where(p => Math.Abs(p.DnMm - dnMm) < 0.5).ToList();
            var pool = exact;
            if (pool.Count == 0)
            {
                var nearest = parsed.Min(p => Math.Abs(p.DnMm - dnMm));
                pool = parsed.Where(p => Math.Abs(Math.Abs(p.DnMm - dnMm) - nearest) < 1e-9).ToList();
            }

            var pick = pool
                .OrderBy(p => word != null && TextUtil.Fold(p.TypeName).Contains(word) ? 0 : 1)
                .ThenBy(p => PnDistance(p, preferredPnBar))
                .ThenBy(p => p.PnBar)
                .ThenBy(p => p.TypeName, StringComparer.OrdinalIgnoreCase)
                .First();
            pick.ExactDn = exact.Count > 0;
            pick.ExactPn = preferredPnBar <= 0 || pick.PnBar <= 0 || Math.Abs(pick.PnBar - preferredPnBar) < 0.001;
            return pick;
        }

        /// <summary>I tipi senza PN nel nome non vengono penalizzati: quella famiglia non lo distingue.</summary>
        private static double PnDistance(ValveTypePick pick, double preferredPnBar)
        {
            if (preferredPnBar <= 0 || pick.PnBar <= 0) return 0;
            return Math.Abs(pick.PnBar - preferredPnBar);
        }

        private static bool TryNumber(string s, out double value)
        {
            return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
