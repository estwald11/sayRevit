using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SayRevit.Core.Model
{
    /// <summary>Regola "da questo DN in su usa questa famiglia" di una <see cref="FamilyByDn"/>.</summary>
    public sealed class FamilyRule
    {
        public FamilyRule()
        {
        }

        public FamilyRule(double fromDnMm, string family)
        {
            FromDnMm = fromDnMm;
            Family = family;
        }

        /// <summary>DN minimo (compreso) da cui vale la regola.</summary>
        public double FromDnMm { get; set; }

        /// <summary>Famiglia da usare; vuota = da questo DN in su il pezzo non si mette.</summary>
        public string Family { get; set; }

        public bool HasFamily
        {
            get { return !string.IsNullOrWhiteSpace(Family); }
        }
    }

    /// <summary>
    /// Famiglia di un elemento del collettore (intercettazione, valvola di zona, filtro…) scelta
    /// per diametro: di base una sola famiglia per tutti i DN (<see cref="Default"/>); a scelta
    /// dell'utente, soglie "da DN x in su usa la famiglia y" (<see cref="Rules"/>). Sotto la prima
    /// soglia vale la famiglia di base; da ogni soglia in su vale la sua famiglia fino alla successiva.
    /// Si salva come testo: "famiglia|40=altra famiglia|100=terza" (una regola con famiglia vuota
    /// significa "da lì in su nessun pezzo").
    /// </summary>
    public sealed class FamilyByDn
    {
        private const char EntrySeparator = '|';
        private const char RuleSeparator = '=';

        public FamilyByDn()
        {
        }

        public FamilyByDn(string defaultFamily)
        {
            Default = defaultFamily;
        }

        /// <summary>Famiglia per tutti i DN non coperti da una regola; vuota = nessun pezzo.</summary>
        public string Default { get; set; }

        /// <summary>Soglie per DN, in qualunque ordine: vince quella col DN più alto non superiore al DN cercato.</summary>
        public List<FamilyRule> Rules { get; } = new List<FamilyRule>();

        /// <summary>True se la famiglia di base è vuota e nessuna regola ne dà una: l'elemento non viene mai messo.</summary>
        public bool IsEmpty
        {
            get { return string.IsNullOrWhiteSpace(Default) && !Rules.Any(r => r != null && r.HasFamily); }
        }

        /// <summary>True se l'utente ha diviso per diametro (almeno una soglia).</summary>
        public bool HasRules
        {
            get { return Rules.Any(r => r != null); }
        }

        /// <summary>Regole in ordine di DN crescente, saltando quelle nulle.</summary>
        public List<FamilyRule> OrderedRules()
        {
            return Rules.Where(r => r != null).OrderBy(r => r.FromDnMm).ToList();
        }

        /// <summary>
        /// Famiglia per il DN dato: la regola con la soglia più alta ≤ DN, altrimenti quella di base.
        /// Null se per quel DN non va messo nulla (famiglia vuota).
        /// </summary>
        public string Resolve(double dnMm)
        {
            var rule = Rules
                .Where(r => r != null && r.FromDnMm <= dnMm + 0.001)
                .OrderByDescending(r => r.FromDnMm)
                .FirstOrDefault();
            var family = rule != null ? rule.Family : Default;
            return string.IsNullOrWhiteSpace(family) ? null : family.Trim();
        }

        /// <summary>Tutte le famiglie nominate (base e regole), senza doppioni.</summary>
        public List<string> Families()
        {
            return new[] { Default }.Concat(Rules.Where(r => r != null).Select(r => r.Family))
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Testo per l'anteprima: "fam A; da DN40: fam B; da DN100: nessuna".</summary>
        public string Describe()
        {
            var parts = new List<string> { string.IsNullOrWhiteSpace(Default) ? "nessuna" : "\"" + Default.Trim() + "\"" };
            foreach (var r in OrderedRules())
                parts.Add("da DN" + MepSize.Fmt(r.FromDnMm) + ": " + (r.HasFamily ? "\"" + r.Family.Trim() + "\"" : "nessuna"));
            return string.Join("; ", parts);
        }

        public FamilyByDn Clone()
        {
            var copy = new FamilyByDn(Default);
            foreach (var r in Rules.Where(r => r != null)) copy.Rules.Add(new FamilyRule(r.FromDnMm, r.Family));
            return copy;
        }

        /// <summary>Forma salvata: famiglia di base, poi "DN=famiglia" per ogni soglia, separate da "|".</summary>
        public override string ToString()
        {
            var parts = new List<string> { Clean(Default) };
            foreach (var r in OrderedRules())
                parts.Add(r.FromDnMm.ToString("0.##", CultureInfo.InvariantCulture) + RuleSeparator + Clean(r.Family));
            return string.Join(EntrySeparator.ToString(), parts);
        }

        /// <summary>Legge la forma salvata; un nome semplice (formato vecchio) è la sola famiglia di base.</summary>
        public static FamilyByDn Parse(string text)
        {
            var map = new FamilyByDn();
            if (string.IsNullOrWhiteSpace(text)) return map;
            var parts = text.Split(EntrySeparator);
            map.Default = parts[0].Trim();
            for (var i = 1; i < parts.Length; i++)
            {
                var part = parts[i];
                var eq = part.IndexOf(RuleSeparator);
                if (eq <= 0) continue;
                double dn;
                if (!double.TryParse(part.Substring(0, eq).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dn) || dn <= 0) continue;
                map.Rules.Add(new FamilyRule(dn, part.Substring(eq + 1).Trim()));
            }
            return map;
        }

        private static string Clean(string family)
        {
            return (family ?? string.Empty).Trim().Replace(EntrySeparator.ToString(), string.Empty).Replace(RuleSeparator.ToString(), string.Empty);
        }
    }
}
