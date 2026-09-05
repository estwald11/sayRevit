using System;
using System.Collections.Generic;
using System.Linq;
using SayRevit.Core.Parsing;

namespace SayRevit.Core.Model
{
    /// <summary>Soglia proposta all'apertura del pannello: "da DN x in su" la prima famiglia del progetto che contiene una delle parole.</summary>
    public sealed class ElementThreshold
    {
        public ElementThreshold(double fromDnMm, params string[] hints)
        {
            FromDnMm = fromDnMm;
            Hints = hints;
        }

        public double FromDnMm { get; }
        public string[] Hints { get; }
    }

    /// <summary>Sezione del pannello in cui l'elemento si sceglie.</summary>
    public enum ElementSection
    {
        /// <summary>"Valvole sugli stacchi": intercettazioni (sfera, boax).</summary>
        Shutoff,

        /// <summary>"Mix 2 vie (iniezione)": accessori della catena.</summary>
        MixTwoWay,

        /// <summary>"Pompa": la famiglia delle pompe (il modello si sceglie nella riga del circuito).</summary>
        Pump
    }

    /// <summary>
    /// Descrizione dichiarativa di un elemento montabile sul collettore (una famiglia Revit per DN).
    /// Da questa sola voce derivano: la tendina + pulsante "per DN…" nel pannello, la chiave nel
    /// file delle impostazioni, il caricamento nella factory, la preparazione della famiglia in
    /// Revit, le note dell'anteprima. Per aggiungere un elemento basta una voce in
    /// <see cref="ManifoldElements.All"/> e il suo uso nella ricetta della catena (ChainFor).
    /// </summary>
    public sealed class ManifoldElement
    {
        /// <summary>Chiave stabile ("ball", "zone"…): indice delle mappe del piano e delle impostazioni.</summary>
        public string Key { get; set; }

        /// <summary>Tipo di pezzo che il costruttore Revit monta.</summary>
        public ValveKind Kind { get; set; }

        /// <summary>Nome per l'anteprima ("valvola di zona").</summary>
        public string Label { get; set; }

        /// <summary>Nome corto per la catena in una riga ("zona").</summary>
        public string ShortLabel { get; set; }

        /// <summary>Etichetta della tendina nel pannello ("Valvola di zona:").</summary>
        public string UiLabel { get; set; }

        /// <summary>Chiave nel file delle impostazioni (valore nella forma di <see cref="FamilyByDn"/>).</summary>
        public string SettingsKey { get; set; }

        /// <summary>Parole cercate nel nome della famiglia per proporla all'apertura del pannello.</summary>
        public string[] Hints { get; set; } = new string[0];

        /// <summary>Parola preferita nel nome del tipo a parità di DN ("ductile"); null = nessuna.</summary>
        public string TypeWord { get; set; }

        /// <summary>True se il pezzo si monta tra due flange automatiche (wafer).</summary>
        public bool WithFlanges { get; set; }

        /// <summary>
        /// Parole nel nome della famiglia per cui il pezzo è filettato e si monta SENZA le flange
        /// automatiche anche se <see cref="WithFlanges"/> è vero (es. il ritegno Giacomini R60 accanto alla boa-rvk wafer).
        /// </summary>
        public string[] NoFlangeHints { get; set; } = new string[0];

        /// <summary>
        /// Soglie proposte all'apertura del pannello quando non c'è nulla di salvato (es. ritegno: da DN40 la boa-rvk);
        /// l'utente le cambia con "per DN…".
        /// </summary>
        public ElementThreshold[] DefaultThresholds { get; set; } = new ElementThreshold[0];

        /// <summary>True se con questa famiglia il pezzo va montato senza flange (famiglia filettata).</summary>
        public bool MountsWithoutFlanges(string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return false;
            var name = TextUtil.Fold(familyName);
            return NoFlangeHints.Any(h => name.Contains(TextUtil.Fold(h)));
        }

        /// <summary>True se il pezzo con questa famiglia si monta tra due flange automatiche.</summary>
        public bool FlangesFor(string familyName)
        {
            return WithFlanges && !MountsWithoutFlanges(familyName);
        }

        /// <summary>True se una famiglia vuota significa "scelta automatica sul nome" (energy valve) e non "nessun pezzo".</summary>
        public bool AutoByName { get; set; }

        public ElementSection Section { get; set; }

        /// <summary>True se la famiglia sta tra le attrezzature meccaniche (pompe) e non tra gli accessori per tubazioni.</summary>
        public bool FromMechanicalEquipment { get; set; }

        /// <summary>Suggerimento della tendina.</summary>
        public string Tooltip { get; set; }

        /// <summary>Larghezza della tendina nel pannello.</summary>
        public double UiWidth { get; set; } = 230;
    }

    /// <summary>Registro degli elementi del collettore: l'unico posto in cui si elencano.</summary>
    public static class ManifoldElements
    {
        public const string Ball = "ball";
        public const string Butterfly = "butterfly";
        public const string EnergyValve = "energy";
        public const string ZoneValve = "zone";
        public const string Strainer = "strainer";
        public const string CheckValve = "check";
        public const string Pump = "pump";

        public static readonly IReadOnlyList<ManifoldElement> All = new List<ManifoldElement>
        {
            new ManifoldElement
            {
                Key = Ball, Kind = ValveKind.Ball, Label = "valvola a sfera", ShortLabel = "sfera",
                UiLabel = "Famiglia sfera:", SettingsKey = "ManifoldBallValveFamily", UiWidth = 190,
                Hints = new[] { "sfera", "ball" }, Section = ElementSection.Shutoff,
                Tooltip = "Famiglia di valvole a sfera caricata nel progetto (accessori per tubazioni).\n" +
                          "Con \"per DN…\" una famiglia diversa per fascia di diametri."
            },
            new ManifoldElement
            {
                Key = Butterfly, Kind = ValveKind.Butterfly, Label = "valvola boax", ShortLabel = "boax",
                UiLabel = "Famiglia boax:", SettingsKey = "ManifoldButterflyValveFamily", UiWidth = 190,
                Hints = new[] { "boax", "farfalla", "butterfly", "wafer" }, WithFlanges = true, Section = ElementSection.Shutoff,
                Tooltip = "Famiglia di valvole boax caricata nel progetto (accessori per tubazioni).\n" +
                          "Con \"per DN…\" una famiglia diversa per fascia di diametri."
            },
            new ManifoldElement
            {
                Key = EnergyValve, Kind = ValveKind.EnergyValve, Label = "energy valve", ShortLabel = "energy valve",
                UiLabel = "Energy valve:", SettingsKey = "ManifoldEnergyValveFamily",
                Hints = new string[0], AutoByName = true, Section = ElementSection.MixTwoWay,
                Tooltip = "Famiglia della energy valve (Belimo) sul ritorno, prima del T del bypass.\n" +
                          "\"(automatica sul DN)\" sceglie il tipo o la famiglia il cui nome porta il DN del circuito (EV025R2+BAC, ev025r2… per DN25);\n" +
                          "con \"per DN…\" si fissa una famiglia per fascia di diametri. La scelta nella riga del circuito vince comunque."
            },
            new ManifoldElement
            {
                Key = ZoneValve, Kind = ValveKind.ZoneValve, Label = "valvola di zona", ShortLabel = "zona",
                UiLabel = "Valvola di zona:", SettingsKey = "ManifoldZoneValveFamily",
                Hints = new[] { "sylax", "watts_butterflyvalve", "valvola di zona" }, TypeWord = "ductile", WithFlanges = true,
                Section = ElementSection.MixTwoWay,
                Tooltip = "Farfalla wafer con riduttore manuale (Watts Sylax), montata tra due flange su mandata e ritorno.\n" +
                          "A parità di DN si preferisce il tipo \"Ductile Iron\". Con \"per DN…\" una famiglia diversa per fascia di diametri."
            },
            new ManifoldElement
            {
                Key = Strainer, Kind = ValveKind.Strainer, Label = "filtro a Y", ShortLabel = "filtro Y",
                UiLabel = "Filtro a Y:", SettingsKey = "ManifoldStrainerFamily",
                // base proposta: IMI TA-STR filettato (DN15–DN32, senza flange); da DN40 il VIR 895 wafer tra due
                // flange automatiche, come la boax. Le famiglie con le flange proprie (TA-STR F, Watts Y33P) restano senza.
                Hints = new[] { "ta-str_rfa", "y33p", "strainerwithdraincock", "strainer", "filtro impurita", "filtro a y", "ta-str", "vir_895", "895" },
                WithFlanges = true,
                NoFlangeHints = new[] { "ta-str", "y33p", "strainerwithdraincock" },
                DefaultThresholds = new[] { new ElementThreshold(40, "vir_895", "895") },
                Section = ElementSection.MixTwoWay,
                Tooltip = "Filtro a Y sul ritorno, prima della valvola di zona.\n" +
                          "Predefinito: IMI TA-STR filettato fino a DN32, da DN40 VIR 895 wafer tra due flange (come la boax);\n" +
                          "la soglia si cambia con \"per DN…\". TA-STR F e Watts Y33P hanno le flange proprie e si montano senza."
            },
            new ManifoldElement
            {
                Key = CheckValve, Kind = ValveKind.CheckValve, Label = "valvola di ritegno", ShortLabel = "ritegno",
                UiLabel = "Ritegno sul bypass:", SettingsKey = "ManifoldCheckValveFamily",
                // base proposta: Giacomini R60 (filettata, DN10–DN32); da DN40 la KSB BOA-RVK wafer tra flange
                Hints = new[] { "r60", "giacomini", "boa-rvk", "rvk", "ritegno", "check" }, WithFlanges = true,
                NoFlangeHints = new[] { "r60", "giacomini" },
                DefaultThresholds = new[] { new ElementThreshold(40, "boa-rvk", "rvk") },
                Section = ElementSection.MixTwoWay,
                Tooltip = "Valvola di ritegno sul tratto verticale del bypass.\n" +
                          "Predefinito: Giacomini R60 (filettata, tipi R60Y002 = DN10, R60Y003 = DN15… lungo la serie DN) fino a DN32,\n" +
                          "da DN40 la KSB BOA-RVK wafer tra due flange; la soglia si cambia con \"per DN…\"."
            },
            new ManifoldElement
            {
                Key = Pump, Kind = ValveKind.Pump, Label = "pompa", ShortLabel = "pompa",
                UiLabel = "Famiglia pompa:", SettingsKey = "ManifoldPumpFamily",
                Hints = new[] { "magna", "grundfos", "pompa", "pump", "wilo" }, Section = ElementSection.Pump,
                FromMechanicalEquipment = true,
                Tooltip = "Famiglia delle pompe di circolazione (attrezzatura meccanica, es. Grundfos MAGNA3).\n" +
                          "Il modello (tipo) si sceglie in ogni riga del circuito: diretto, mix 3 vie e mix 2 vie.\n" +
                          "La pompa va sulla mandata dopo il bypass; i modelli flangiati (\"F\" nel nome) vengono montati tra due flange."
            }
        };

        public static ManifoldElement Get(string key)
        {
            var e = All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (e == null) throw new ArgumentException("Elemento del collettore sconosciuto: " + key, "key");
            return e;
        }

        public static ManifoldElement ByKind(ValveKind kind)
        {
            return All.FirstOrDefault(x => x.Kind == kind);
        }

        /// <summary>Elemento la cui chiave nel file delle impostazioni è questa; null se non è una famiglia.</summary>
        public static ManifoldElement BySettingsKey(string settingsKey)
        {
            return All.FirstOrDefault(x => string.Equals(x.SettingsKey, settingsKey, StringComparison.OrdinalIgnoreCase));
        }

        public static IEnumerable<ManifoldElement> In(ElementSection section)
        {
            return All.Where(x => x.Section == section);
        }
    }
}
