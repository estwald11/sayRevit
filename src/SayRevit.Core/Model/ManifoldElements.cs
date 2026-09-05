using System;
using System.Collections.Generic;
using System.Linq;

namespace SayRevit.Core.Model
{
    /// <summary>Sezione del pannello in cui l'elemento si sceglie.</summary>
    public enum ElementSection
    {
        /// <summary>"Valvole sugli stacchi": intercettazioni (sfera, boax).</summary>
        Shutoff,

        /// <summary>"Mix 2 vie (iniezione)": accessori della catena.</summary>
        MixTwoWay
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

        /// <summary>True se una famiglia vuota significa "scelta automatica sul nome" (energy valve) e non "nessun pezzo".</summary>
        public bool AutoByName { get; set; }

        public ElementSection Section { get; set; }

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
                          "\"(automatica sul DN)\" sceglie la famiglia il cui nome porta il DN del circuito (ev025r2… per DN25);\n" +
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
                Hints = new[] { "y33p", "strainerwithdraincock", "strainer", "filtro impurita", "filtro a y" },
                Section = ElementSection.MixTwoWay,
                Tooltip = "Filtro a Y sul ritorno, prima della valvola di zona (Watts Y33P: da DN40; per DN minori scegli un'altra famiglia con \"per DN…\")."
            },
            new ManifoldElement
            {
                Key = CheckValve, Kind = ValveKind.CheckValve, Label = "valvola di ritegno", ShortLabel = "ritegno",
                UiLabel = "Ritegno sul bypass:", SettingsKey = "ManifoldCheckValveFamily",
                Hints = new[] { "boa-rvk", "rvk", "ritegno", "check" }, WithFlanges = true, Section = ElementSection.MixTwoWay,
                Tooltip = "Valvola di ritegno wafer sul tratto verticale del bypass, tra due flange (KSB BOA-RVK)."
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
