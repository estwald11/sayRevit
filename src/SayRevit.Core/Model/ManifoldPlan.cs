using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SayRevit.Core.Parsing;

namespace SayRevit.Core.Model
{
    /// <summary>Un circuito in partenza dal collettore, identificato dal suo DN.</summary>
    public sealed class ManifoldCircuit
    {
        public ManifoldCircuit()
        {
        }

        public ManifoldCircuit(double dnMm)
        {
            DnMm = dnMm;
        }

        public ManifoldCircuit(double dnMm, CircuitKind kind)
        {
            DnMm = dnMm;
            Kind = kind;
        }

        /// <summary>Diametro nominale del circuito in millimetri (0 = riga ancora vuota).</summary>
        public double DnMm { get; set; }

        /// <summary>
        /// Tipologia idraulica: diretto, miscelato a 3 vie, miscelato a 2 vie (iniezione) o senza
        /// pompa. Decide i componenti sullo stacco oltre all'intercettazione (vedi <see cref="CircuitKinds"/>).
        /// </summary>
        public CircuitKind Kind { get; set; } = CircuitKinds.Default;

        public CircuitKindInfo KindInfo
        {
            get { return CircuitKinds.Info(Kind); }
        }

        /// <summary>Etichetta facoltativa del circuito (es. "bagno"); se vuota si usa "C1", "C2"…</summary>
        public string Name { get; set; }

        /// <summary>
        /// Tipologie con bypass (mix 2 vie, mix 3 vie): DN del circuito DOPO il bypass (mm), cioè
        /// del secondario verso l'utenza; il bypass ha sempre questo DN. 0 = uguale a <see cref="DnMm"/>,
        /// che resta il DN prima del bypass (lato collettore) e quello usato per dimensionare la base.
        /// </summary>
        public double DnAfterBypassMm { get; set; }

        /// <summary>DN effettivo dopo il bypass: quello impostato, altrimenti lo stesso dello stacco.</summary>
        public double EffectiveDnAfterBypassMm
        {
            get { return DnAfterBypassMm > 0 ? DnAfterBypassMm : DnMm; }
        }

        /// <summary>
        /// Mix 2 vie: famiglia della energy valve scelta dall'utente per questo circuito; vuota =
        /// scelta automatica sul DN dal nome del tipo ("EV025R2+BAC") o della famiglia ("ev025r2…").
        /// </summary>
        public string EnergyValveFamily { get; set; }

        /// <summary>
        /// Mix 2 vie: tipo della energy valve scelto dall'utente dentro <see cref="EnergyValveFamily"/>,
        /// quando la famiglia è unica con un tipo per DN ("EV032R2+BAC"); vuoto = il tipo del DN del circuito.
        /// </summary>
        public string EnergyValveType { get; set; }

        /// <summary>
        /// Modello (tipo) della pompa scelto nella riga, nella famiglia della pompa del piano
        /// ("MAGNA3 25-60 PN10 - 97924245"); vuoto = nessuna pompa (nel mix 2 vie resta lo spazio riservato).
        /// </summary>
        public string PumpType { get; set; }

        /// <summary>
        /// Valvola di zona sullo stacco (accessorio opzionale, scelto dall'utente per ogni circuito).
        /// Null = predefinito della tipologia: sì per il mix 2 vie, no per le altre.
        /// </summary>
        public bool? WithZoneValve { get; set; }

        public bool EffectiveWithZoneValve
        {
            // il cieco si ferma alla flangia dell'intercettazione: la valvola di zona non ha posto
            get { return !KindInfo.IsBlind && (WithZoneValve ?? KindInfo.IsChainModelled); }
        }

        public bool IsValid
        {
            get { return DnMm > 0; }
        }
    }

    /// <summary>
    /// Collettore parametrico: un tratto principale con un circuito per ogni stacco.
    /// È un modello indipendente da Revit che si traduce in un <see cref="MepPlan"/>,
    /// così la creazione riusa la stessa pipeline della modalità testuale.
    /// </summary>
    public sealed class ManifoldPlan
    {
        /// <summary>Serie di DN commerciali usata per arrotondare il diametro calcolato del collettore.</summary>
        public static readonly double[] DnSeries =
        {
            10, 15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300, 350, 400, 450, 500, 600
        };

        /// <summary>Circuiti nell'ordine in cui l'utente li ha inseriti.</summary>
        public List<ManifoldCircuit> Circuits { get; } = new List<ManifoldCircuit>();

        /// <summary>DN del collettore in mm; null o 0 = calcolato automaticamente dai circuiti.</summary>
        public double? HeaderDnMm { get; set; }

        /// <summary>Fattore di maggiorazione della formula del collettore: D = √(1,5·ΣS/0,785).</summary>
        public const double HeaderSizingFactor = 1.5;

        /// <summary>Interasse tra due circuiti consecutivi (mm).</summary>
        public double SpacingMm { get; set; } = 150;

        /// <summary>
        /// Se true l'interasse viene calcolato in Revit dagli ingombri reali degli elementi sugli
        /// stacchi (valvola, flange, leva): il minimo che evita interferenze tra stacchi vicini,
        /// mai sotto <see cref="SpacingMm"/>, che fa da pavimento.
        /// </summary>
        public bool AutoSpacing { get; set; } = true;

        /// <summary>Aria minima tra due elementi di stacchi diversi (mm), usata dall'interasse automatico.</summary>
        public double SpacingClearanceMm { get; set; } = 20;

        private ManifoldSpacing.Result _autoSpacing;

        /// <summary>Interasse minimo richiesto dall'utente, conservato quando l'automatico lo alza.</summary>
        public double SpacingFloorMm { get; private set; }

        /// <summary>
        /// Applica l'interasse automatico dagli ingombri misurati (uno per circuito valido, nello
        /// stesso ordine di <see cref="CircuitDnsMm"/>) e lo scrive in <see cref="SpacingMm"/>.
        /// </summary>
        public ManifoldSpacing.Result ApplyAutoSpacing(IList<StubFootprint> footprints, double headerRadiusMm)
        {
            return ApplyAutoSpacing(footprints, null, headerRadiusMm);
        }

        /// <param name="extras">
        /// Per ogni circuito (stesso ordine), un ingombro staccato dallo stacco di mandata ma
        /// riferito al suo asse — il tratto verticale del bypass, che sta sulla verticale della base
        /// del ritorno — oppure null.
        /// </param>
        public ManifoldSpacing.Result ApplyAutoSpacing(IList<StubFootprint> footprints, IList<StubFootprint> extras, double headerRadiusMm)
        {
            if (_autoSpacing == null) SpacingFloorMm = SpacingMm;
            var r = ManifoldSpacing.Minimal(footprints, extras, SpacingFloorMm, WithReturn, ReturnOffsetMm, headerRadiusMm, SpacingClearanceMm, 10);
            _autoSpacing = r;
            SpacingMm = r.SpacingMm;
            return r;
        }

        /// <summary>DN dei circuiti validi, nell'ordine lungo la base.</summary>
        public List<double> CircuitDnsMm()
        {
            return ValidCircuits().Select(c => c.DnMm).ToList();
        }

        /// <summary>
        /// Lunghezza generica di ogni circuito a partire dall'asse del collettore (mm). Non vale per
        /// le tipologie che fissano la fine dello stacco sulla valvola: il senza pompa (tubo dopo la
        /// valvola, <see cref="NoPumpPipeAfterValveMm"/>) e il cieco (fine alla seconda flangia).
        /// </summary>
        public double CircuitLengthMm { get; set; } = 500;

        /// <summary>
        /// Circuito senza pompa: lunghezza del tubo DOPO la valvola (mm), dalla faccia d'uscita
        /// dell'ultimo pezzo (seconda flangia della boax, uscita della sfera) alla fine dello stacco.
        /// Vale sia sulla mandata sia sul ritorno. La lunghezza totale dello stacco si conosce solo
        /// in Revit, quando l'ingombro reale della valvola è misurato.
        /// </summary>
        public double NoPumpPipeAfterValveMm { get; set; } = 2000;

        /// <summary>
        /// Spazio provvisorio riservato a valvola e flange (mm) per stimare la lunghezza totale di uno
        /// stacco che fissa la sua fine sulla valvola, prima che Revit ne misuri l'ingombro reale.
        /// </summary>
        public double ValveAssemblyAllowanceMm { get; set; } = 300;

        /// <summary>Lunghezza dello stacco (mm) per il circuito dato, secondo la sua tipologia (provvisoria se fissata sulla valvola).</summary>
        public double CircuitLengthFor(ManifoldCircuit circuit)
        {
            if (circuit == null) return CircuitLengthMm;
            var after = PipeAfterValveFor(circuit);
            if (after.HasValue)
                return ValveAxisDistanceMm + ValveAssemblyAllowanceMm + EstimatedChainLengthMm(circuit) + after.Value;
            return CircuitLengthMm;
        }

        /// <summary>
        /// Lunghezza del tubo dopo l'ultimo pezzo se la tipologia la fissa E la valvola c'è davvero:
        /// il valore impostato per il senza pompa, zero per il cieco (si ferma alla flangia), il
        /// tubo finale per il mix 2 vie (dopo l'intercettazione in cima alla catena).
        /// Senza valvola la regola non ha un riferimento e si torna alla lunghezza generica.
        /// </summary>
        public double? PipeAfterValveFor(ManifoldCircuit circuit)
        {
            if (circuit == null || ValveFor(circuit) == null) return null;
            if (circuit.KindInfo.IsBlind) return 0;
            if (circuit.KindInfo.IsChainModelled && HasChain(circuit)) return Mix2EndPipeMm;
            return circuit.KindInfo.UsesPipeAfterValve ? NoPumpPipeAfterValveMm : (double?)null;
        }

        // ------------------------------------------------- mix 2 vie (iniezione)

        /// <summary>
        /// Famiglie di accessori caricate nel progetto (nome + tipi): servono a scegliere per DN
        /// la energy valve, dal nome del tipo (famiglia unica Belimo 2027, "EV025R2+BAC") o della
        /// famiglia (vecchie famiglie per DN, "ev025r2…").
        /// </summary>
        public List<CatalogFamily> AccessoryFamilies { get; } = new List<CatalogFamily>();

        /// <summary>Parola preferita nel nome del tipo o della famiglia della energy valve a parità di DN ("bac" tra +BAC e +MID).</summary>
        public string EnergyValvePreferredWord { get; set; } = "bac";

        /// <summary>
        /// Famiglia per DN di ogni elemento del registro (<see cref="ManifoldElements.All"/>), per chiave.
        /// Le proprietà "…Map" e "…Family" qui sotto sono scorciatoie su questo dizionario.
        /// </summary>
        public Dictionary<string, FamilyByDn> FamilyMaps { get; } =
            ManifoldElements.All.ToDictionary(e => e.Key, e => new FamilyByDn(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tipi della famiglia DI BASE di ogni elemento, passati a mano (test, o cataloghi assenti);
        /// le famiglie delle soglie prendono i tipi da <see cref="AccessoryFamilies"/>.
        /// </summary>
        public Dictionary<string, List<string>> ElementTypes { get; } =
            ManifoldElements.All.ToDictionary(e => e.Key, e => new List<string>(), StringComparer.OrdinalIgnoreCase);

        public FamilyByDn FamilyMap(string elementKey)
        {
            return FamilyMaps[ManifoldElements.Get(elementKey).Key];
        }

        /// <summary>
        /// Famiglia della energy valve per DN, scelta dall'utente: di base vuota (= automatica sul
        /// nome, tipo "EV025R2+BAC" o famiglia "ev025r2…" per DN25); con le soglie si può fissare
        /// una famiglia per fascia di DN.
        /// La scelta fatta nella riga del circuito vince comunque.
        /// </summary>
        public FamilyByDn EnergyValveMap
        {
            get { return FamilyMaps[ManifoldElements.EnergyValve]; }
        }

        /// <summary>Famiglia della valvola di zona (farfalla wafer Watts) per DN; vuota = nessuna.</summary>
        public FamilyByDn ZoneValveMap
        {
            get { return FamilyMaps[ManifoldElements.ZoneValve]; }
        }

        /// <summary>Famiglia di base della valvola di zona (tutti i DN senza soglia); vuota = nessuna.</summary>
        public string ZoneValveFamily
        {
            get { return ZoneValveMap.Default; }
            set { ZoneValveMap.Default = value; }
        }

        /// <summary>Tipi della famiglia di base della valvola di zona (le altre famiglie li prendono da <see cref="AccessoryFamilies"/>).</summary>
        public List<string> ZoneValveTypes
        {
            get { return ElementTypes[ManifoldElements.ZoneValve]; }
        }

        /// <summary>Parola preferita nel nome del tipo della valvola di zona a parità di DN (dal registro).</summary>
        public string ZoneValveTypeWord
        {
            get { return ManifoldElements.Get(ManifoldElements.ZoneValve).TypeWord; }
        }

        /// <summary>Famiglia del filtro a Y per DN; vuota = nessuna.</summary>
        public FamilyByDn StrainerMap
        {
            get { return FamilyMaps[ManifoldElements.Strainer]; }
        }

        /// <summary>Famiglia di base del filtro a Y; vuota = nessuna.</summary>
        public string StrainerFamily
        {
            get { return StrainerMap.Default; }
            set { StrainerMap.Default = value; }
        }

        public List<string> StrainerTypes
        {
            get { return ElementTypes[ManifoldElements.Strainer]; }
        }

        /// <summary>Famiglia della valvola di ritegno sul bypass per DN; vuota = nessuna.</summary>
        public FamilyByDn CheckValveMap
        {
            get { return FamilyMaps[ManifoldElements.CheckValve]; }
        }

        /// <summary>Famiglia di base della valvola di ritegno; vuota = nessuna.</summary>
        public string CheckValveFamily
        {
            get { return CheckValveMap.Default; }
            set { CheckValveMap.Default = value; }
        }

        public List<string> CheckValveTypes
        {
            get { return ElementTypes[ManifoldElements.CheckValve]; }
        }

        /// <summary>
        /// Nomi dei tipi di una famiglia: dal catalogo del progetto (<see cref="AccessoryFamilies"/>)
        /// se la famiglia è caricata; per la famiglia di base di un elemento vale anche la lista
        /// passata a mano (<paramref name="defaultTypes"/>, usata quando il catalogo non c'è).
        /// </summary>
        private List<string> TypesFor(string family, FamilyByDn map, List<string> defaultTypes)
        {
            if (string.IsNullOrWhiteSpace(family)) return new List<string>();
            var isDefault = map != null && !string.IsNullOrWhiteSpace(map.Default) &&
                            string.Equals(map.Default.Trim(), family.Trim(), StringComparison.OrdinalIgnoreCase);
            if (isDefault && defaultTypes != null && defaultTypes.Count > 0) return defaultTypes;
            var loaded = AccessoryFamilies.FirstOrDefault(f => f != null &&
                string.Equals(f.Name, family.Trim(), StringComparison.OrdinalIgnoreCase));
            return loaded != null ? loaded.TypeNames : new List<string>();
        }

        /// <summary>
        /// Tutte le famiglie che il piano può montare (base e soglie di ogni elemento, più le
        /// energy valve automatiche per i DN presenti nel progetto): servono a chi le prepara in Revit.
        /// </summary>
        public List<string> ConfiguredFamilies()
        {
            var names = new List<string>();
            foreach (var map in FamilyMaps.Values) names.AddRange(map.Families());
            foreach (var dn in EnergyValveDns())
            {
                var f = EnergyValveFamilyFor(dn);
                if (f != null) names.Add(f.Name);
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Tubo diritto minimo tra due elementi consecutivi (mm): nessun tubo libero scende sotto questo valore.</summary>
        public const double Mix2MinGapMm = 50;

        /// <summary>Tubo libero tra due pezzi consecutivi della catena, e prima/dopo il T del bypass (mm).</summary>
        public double Mix2GapMm { get; set; } = 150;

        /// <summary>Tubo tra due pezzi flangiati consecutivi (filtro → valvola di zona → intercettazione), mm; mai sotto <see cref="Mix2MinGapMm"/>.</summary>
        public double Mix2FlangedGapMm { get; set; } = Mix2MinGapMm;

        /// <summary>Tubo libero effettivo: il valore chiesto, ma almeno il minimo.</summary>
        private static double Gap(double mm)
        {
            return Math.Max(Mix2MinGapMm, mm);
        }

        /// <summary>Tratto rettilineo richiesto sopra la energy valve, in diametri interni del tubo (Belimo: 5×).</summary>
        public const double EnergyValveStraightFactor = 5;

        /// <summary>
        /// Diametro interno (mm) della misura DN del tipo di tubazione scelto, dalle sue misure;
        /// senza dati, il DN stesso.
        /// </summary>
        public double InnerDiameterMm(double dnMm)
        {
            var size = HeaderSizeCandidates.FirstOrDefault(c => c != null && Math.Abs(c.NominalMm - dnMm) < 0.001);
            return size != null && size.InnerMm > 0 ? size.InnerMm : dnMm;
        }

        /// <summary>
        /// Tubo libero sopra la energy valve (mm): almeno <see cref="EnergyValveStraightFactor"/>
        /// diametri interni del tubo dello stacco, e comunque non meno del tubo libero normale.
        /// </summary>
        public double EnergyValveStraightMm(double dnMm)
        {
            return Math.Max(Gap(Mix2GapMm), Math.Ceiling(EnergyValveStraightFactor * InnerDiameterMm(dnMm)));
        }

        /// <summary>DN del tubo adiacente alla energy valve: quello della valvola (dal nome del tipo o della famiglia) se è più piccola dello stacco, altrimenti lo stacco.</summary>
        public static double EnergyValveStraightDn(MepValve ev, double stubDnMm)
        {
            var evDn = EnergyValveDnOf(ev);
            return evDn.HasValue && evDn.Value > 0 && evDn.Value < stubDnMm ? evDn.Value : stubDnMm;
        }

        /// <summary>Spazio riservato alla pompa sulla mandata (mm): tubo libero, quando nella riga non è scelto un modello di pompa.</summary>
        public double Mix2PumpSpaceMm { get; set; } = 400;

        /// <summary>Tubo dopo l'intercettazione in cima alla catena (mm), su mandata e ritorno.</summary>
        public double Mix2EndPipeMm { get; set; } = 100;

        /// <summary>Rotazione attorno all'asse del tubo di energy valve, valvola di zona, filtro e ritegno (gradi).</summary>
        public double Mix2RollDegrees { get; set; } = 90;

        /// <summary>
        /// True se sullo stacco del circuito va montata una catena di pezzi: quella completa del mix 2
        /// vie, oppure intercettazione → pompa (→ valvola di zona) sulla mandata di un diretto o di
        /// un mix 3 vie con un modello di pompa scelto. Serve comunque l'intercettazione.
        /// </summary>
        public bool HasChain(ManifoldCircuit circuit)
        {
            if (circuit == null || ValveFor(circuit.DnMm) == null) return false;
            return circuit.KindInfo.IsChainModelled || HasPumpChain(circuit);
        }

        /// <summary>Diretto o mix 3 vie con un modello di pompa scelto e trovato nella famiglia: la pompa si monta sulla mandata.</summary>
        public bool HasPumpChain(ManifoldCircuit circuit)
        {
            return circuit != null && !circuit.KindInfo.IsChainModelled && circuit.KindInfo.HasPump && PumpFor(circuit) != null;
        }

        /// <summary>True se il bypass del mix 2 vie può essere costruito: serve il ritorno e stacchi verticali.</summary>
        public bool CanBuildBypass
        {
            get { return WithReturn && (CircuitDirection == DirectionKind.Up || CircuitDirection == DirectionKind.Down); }
        }

        private static readonly Regex EnergyValveRx = new Regex(@"^ev0*(\d+)(r2|f)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Una energy valve montabile: famiglia, tipo e DN dichiarato nel nome. Il DN viene dal nome
        /// del tipo quando la famiglia è unica con un tipo per misura (Belimo 2027,
        /// "Belimo_EV…R2_BAC…" con tipi "EV025R2+BAC" → DN25), altrimenti dal nome della famiglia
        /// (vecchie famiglie per DN, "ev025r2+bac_(1)" → DN25).
        /// </summary>
        public sealed class EnergyValveChoice
        {
            public CatalogFamily Family { get; set; }
            public string TypeName { get; set; }
            public double DnMm { get; set; }
            /// <summary>True se il DN è letto dal nome del tipo (famiglia unica con un tipo per DN).</summary>
            public bool DnFromType { get; set; }

            public string FamilyName { get { return Family == null ? null : Family.Name; } }

            /// <summary>Voce per la tendina della riga: "EV032R2+BAC — famiglia" se il DN sta nel tipo, altrimenti la famiglia.</summary>
            public string Label { get { return DnFromType ? TypeName + " — " + FamilyName : FamilyName; } }

            /// <summary>Contiene la parola preferita (nel tipo o nella famiglia)?</summary>
            public bool Has(string foldedWord)
            {
                if (string.IsNullOrEmpty(foldedWord)) return false;
                return TextUtil.Fold(TypeName ?? string.Empty).Contains(foldedWord) ||
                       TextUtil.Fold(FamilyName ?? string.Empty).Contains(foldedWord);
            }
        }

        /// <summary>
        /// Tutte le energy valve del progetto: per ogni famiglia caricata, i tipi il cui nome porta il
        /// DN ("EV025R2+BAC"); se nessun tipo lo porta ma il nome della famiglia sì ("ev025r2…"),
        /// una sola scelta con il primo tipo. In ordine di DN, poi famiglia, poi tipo.
        /// </summary>
        public List<EnergyValveChoice> EnergyValveChoices()
        {
            var list = new List<EnergyValveChoice>();
            foreach (var f in AccessoryFamilies)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Name)) continue;
                list.AddRange(ChoicesOf(f));
            }
            return list.OrderBy(c => c.DnMm)
                .ThenBy(c => c.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.TypeName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<EnergyValveChoice> ChoicesOf(CatalogFamily family)
        {
            var list = new List<EnergyValveChoice>();
            if (family == null) return list;
            foreach (var t in family.TypeNames)
            {
                var dn = EnergyValveDnOf(t);
                if (dn.HasValue) list.Add(new EnergyValveChoice { Family = family, TypeName = t, DnMm = dn.Value, DnFromType = true });
            }
            if (list.Count == 0)
            {
                var dn = EnergyValveDnOf(family.Name);
                if (dn.HasValue) list.Add(new EnergyValveChoice { Family = family, TypeName = family.TypeNames.FirstOrDefault(), DnMm = dn.Value });
            }
            return list;
        }

        /// <summary>
        /// Energy valve per il DN, scelta automatica sul nome: tra le scelte del progetto quelle con
        /// quel DN (nel tipo o nella famiglia); a parità vince chi ha <see cref="EnergyValvePreferredWord"/>
        /// nel nome ("bac" tra +BAC e +MID). Null se per quel DN non c'è.
        /// </summary>
        public EnergyValveChoice EnergyValveChoiceFor(double dnMm)
        {
            if (dnMm <= 0) return null;
            var word = TextUtil.Fold(EnergyValvePreferredWord ?? string.Empty);
            return EnergyValveChoices()
                .Where(c => Math.Abs(c.DnMm - dnMm) < 0.5)
                .OrderBy(c => c.Has(word) ? 0 : 1)
                .ThenByDescending(c => ChoicesOf(c.Family).Count) // la famiglia unica (molti tipi per DN) vince sulle vecchie per DN
                .ThenBy(c => c.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.TypeName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        /// <summary>Famiglia della energy valve automatica per il DN (null se non c'è).</summary>
        public CatalogFamily EnergyValveFamilyFor(double dnMm)
        {
            var c = EnergyValveChoiceFor(dnMm);
            return c == null ? null : c.Family;
        }

        /// <summary>DN per cui esiste una energy valve nel progetto, in ordine crescente.</summary>
        public List<double> EnergyValveDns()
        {
            return EnergyValveChoices().Select(c => c.DnMm).Distinct().OrderBy(d => d).ToList();
        }

        /// <summary>Energy valve del DN dato (scelta automatica sul nome); null se nel progetto non c'è quella misura.</summary>
        public MepValve EnergyValveFor(double dnMm)
        {
            return EnergyValveOf(EnergyValveChoiceFor(dnMm), dnMm);
        }

        /// <summary>
        /// Energy valve del circuito: la famiglia scelta dall'utente nella riga, se c'è ed è caricata;
        /// altrimenti quella fissata per fascia di DN in <see cref="EnergyValveMap"/>; altrimenti
        /// quella automatica sul nome per il DN prima del bypass. Dentro una famiglia con un tipo per
        /// DN si prende il tipo del DN del circuito; se manca, il più grande tra quelli più piccoli
        /// (poi riduzioni), altrimenti il più piccolo.
        /// </summary>
        public MepValve EnergyValveFor(ManifoldCircuit circuit)
        {
            if (circuit == null) return null;
            var inRow = LoadedFamily(circuit.EnergyValveFamily);
            if (inRow != null)
            {
                // tipo esplicitato nella riga (famiglia unica con un tipo per DN): vince sul DN del circuito
                var type = string.IsNullOrWhiteSpace(circuit.EnergyValveType) ? null
                    : inRow.TypeNames.FirstOrDefault(t => string.Equals(t, circuit.EnergyValveType.Trim(), StringComparison.OrdinalIgnoreCase));
                if (type != null)
                {
                    var dn = EnergyValveDnOf(type);
                    return EnergyValveOf(new EnergyValveChoice { Family = inRow, TypeName = type, DnMm = dn ?? circuit.DnMm, DnFromType = dn.HasValue }, circuit.DnMm);
                }
                return EnergyValveOf(ChoiceInFamily(inRow, circuit.DnMm), circuit.DnMm);
            }
            var chosen = LoadedFamily(EnergyValveMap.Resolve(circuit.DnMm));
            if (chosen != null) return EnergyValveOf(ChoiceInFamily(chosen, circuit.DnMm), circuit.DnMm);
            return EnergyValveFor(circuit.DnMm);
        }

        /// <summary>La scelta migliore dentro una famiglia fissata dall'utente, per il DN del circuito.</summary>
        private EnergyValveChoice ChoiceInFamily(CatalogFamily family, double dnMm)
        {
            var word = TextUtil.Fold(EnergyValvePreferredWord ?? string.Empty);
            var choices = ChoicesOf(family)
                .OrderBy(c => c.Has(word) ? 0 : 1)
                .ThenBy(c => c.TypeName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (choices.Count == 0)
            {
                // famiglia senza DN nel nome né nei tipi: si monta com'è, primo tipo
                return new EnergyValveChoice { Family = family, TypeName = family.TypeNames.FirstOrDefault(), DnMm = dnMm };
            }
            return choices.FirstOrDefault(c => Math.Abs(c.DnMm - dnMm) < 0.5)
                   ?? choices.Where(c => c.DnMm < dnMm).OrderByDescending(c => c.DnMm).FirstOrDefault()
                   ?? choices.OrderBy(c => c.DnMm).First();
        }

        /// <summary>Da dove viene la energy valve del circuito, per l'anteprima.</summary>
        public string EnergyValveSourceOf(ManifoldCircuit circuit)
        {
            if (circuit == null) return string.Empty;
            if (LoadedFamily(circuit.EnergyValveFamily) != null) return "scelta nella riga";
            if (LoadedFamily(EnergyValveMap.Resolve(circuit.DnMm)) != null) return "fissata per DN nelle impostazioni";
            return "automatica sul DN";
        }

        private CatalogFamily LoadedFamily(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return AccessoryFamilies.FirstOrDefault(f => f != null &&
                string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Tutte le famiglie di energy valve caricate (DN nel nome della famiglia o dei tipi), per la
        /// tendina della riga: in ordine del DN più piccolo che offrono, poi per nome.
        /// </summary>
        public List<CatalogFamily> EnergyValveFamilies()
        {
            return EnergyValveChoices()
                .GroupBy(c => c.Family)
                .OrderBy(g => g.Min(c => c.DnMm))
                .ThenBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .ToList();
        }

        /// <summary>DN dichiarato in un nome di famiglia o di tipo di energy valve ("ev025r2…", "EV025R2+BAC"); null se il nome non lo dice.</summary>
        public static double? EnergyValveDnOf(string familyOrTypeName)
        {
            var m = EnergyValveRx.Match(TextUtil.Fold(familyOrTypeName ?? string.Empty));
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (double?)null;
        }

        /// <summary>DN dichiarato dal pezzo: prima nel nome del tipo, poi in quello della famiglia.</summary>
        public static double? EnergyValveDnOf(MepValve ev)
        {
            if (ev == null) return null;
            return EnergyValveDnOf(ev.TypeName) ?? EnergyValveDnOf(ev.FamilyName);
        }

        private MepValve EnergyValveOf(EnergyValveChoice choice, double dnMm)
        {
            if (choice == null || choice.Family == null) return null;
            return new MepValve
            {
                Kind = ValveKind.EnergyValve,
                FamilyName = choice.Family.Name,
                TypeName = choice.TypeName,
                DnMm = dnMm,
                WithFlanges = false, // attacchi filettati (R2)
                RollDegrees = Mix2RollDegrees
            };
        }

        /// <summary>Valvola di zona del DN dato (tra due flange); null senza famiglia scelta.</summary>
        public MepValve ZoneValveFor(double dnMm)
        {
            return PieceFor(ManifoldElements.ZoneValve, dnMm);
        }

        /// <summary>Rotazione del filtro a Y attorno all'asse del tubo (gradi), separata dagli altri accessori.</summary>
        public double StrainerRollDegrees { get; set; } = 270;

        /// <summary>
        /// True = filtro a Y montato col verso invertito rispetto a come esce dalla famiglia
        /// (girato di 180° attorno alla normale al tubo): la Y guarda verso il collettore.
        /// </summary>
        public bool StrainerReversed { get; set; } = true;

        /// <summary>Rotazione della pompa attorno all'asse del tubo (gradi): decide da che parte esce il motore.</summary>
        public double PumpRollDegrees { get; set; } = 90;

        /// <summary>True = pompa montata col verso invertito rispetto alla famiglia (girata di 180° attorno alla normale al tubo).</summary>
        public bool PumpReversed { get; set; }

        private static readonly Regex PumpFlangedRx = new Regex(@"(?<![A-Za-z0-9])F(?![A-Za-z0-9])");

        /// <summary>True se il modello di pompa è flangiato ("MAGNA3 32-40 F PN6"): si monta tra due flange.</summary>
        public static bool PumpTypeIsFlanged(string typeName)
        {
            return !string.IsNullOrWhiteSpace(typeName) && PumpFlangedRx.IsMatch(typeName);
        }

        /// <summary>DN dello stacco dove sta la pompa: dopo il bypass nel mix 2 vie, il DN del circuito altrimenti.</summary>
        public double PumpDnFor(ManifoldCircuit circuit)
        {
            return circuit.KindInfo.IsChainModelled ? circuit.EffectiveDnAfterBypassMm : circuit.DnMm;
        }

        /// <summary>Famiglia della pompa per il DN del circuito (base o soglia); null se non scelta.</summary>
        public string PumpFamilyFor(ManifoldCircuit circuit)
        {
            var family = FamilyMaps[ManifoldElements.Pump].Resolve(PumpDnFor(circuit));
            return string.IsNullOrWhiteSpace(family) ? null : family.Trim();
        }

        /// <summary>Modelli (tipi) della famiglia della pompa per il DN: le voci della tendina della riga.</summary>
        public List<string> PumpTypeNames(double dnMm)
        {
            var map = FamilyMaps[ManifoldElements.Pump];
            var family = map.Resolve(dnMm > 0 ? dnMm : 1);
            if (string.IsNullOrWhiteSpace(family)) return new List<string>();
            return TypesFor(family, map, ElementTypes[ManifoldElements.Pump]).ToList();
        }

        /// <summary>
        /// Pompa del circuito: il modello scelto nella riga dentro la famiglia della pompa, sul DN
        /// dello stacco dove sta; null senza modello, senza famiglia o se il modello non è tra i tipi.
        /// I modelli flangiati ("F") vanno tra due flange; rotazione e verso dalle impostazioni.
        /// </summary>
        public MepValve PumpFor(ManifoldCircuit circuit)
        {
            if (circuit == null || !circuit.KindInfo.HasPump || string.IsNullOrWhiteSpace(circuit.PumpType)) return null;
            var dn = PumpDnFor(circuit);
            if (dn <= 0) return null;
            var map = FamilyMaps[ManifoldElements.Pump];
            var family = PumpFamilyFor(circuit);
            if (family == null) return null;
            var type = TypesFor(family, map, ElementTypes[ManifoldElements.Pump])
                .FirstOrDefault(t => string.Equals(t, circuit.PumpType.Trim(), StringComparison.OrdinalIgnoreCase));
            if (type == null) return null;
            return new MepValve
            {
                Kind = ValveKind.Pump,
                FamilyName = family,
                TypeName = type,
                DnMm = dn,
                PnBar = ValvePnBar,
                WithFlanges = PumpTypeIsFlanged(type),
                RollDegrees = PumpRollDegrees,
                Reversed = PumpReversed
            };
        }

        /// <summary>Filtro a Y del DN dato (flange proprie della famiglia); null senza famiglia scelta.</summary>
        public MepValve StrainerFor(double dnMm)
        {
            var piece = PieceFor(ManifoldElements.Strainer, dnMm);
            if (piece != null)
            {
                piece.RollDegrees = StrainerRollDegrees;
                piece.Reversed = StrainerReversed;
            }
            return piece;
        }

        /// <summary>Valvola di ritegno del DN dato (wafer tra due flange); null senza famiglia scelta.</summary>
        public MepValve CheckValveFor(double dnMm)
        {
            return PieceFor(ManifoldElements.CheckValve, dnMm);
        }

        /// <summary>
        /// Pezzo generico di un elemento del registro per il DN dato: famiglia dalla mappa per DN,
        /// tipo scelto sul nome (DN, PN, parola preferita), flange e rotazione dal registro/impostazioni.
        /// Null se per quel DN non c'è famiglia. È l'unico modo con cui gli accessori entrano in catena.
        /// </summary>
        public MepValve PieceFor(string elementKey, double dnMm)
        {
            if (dnMm <= 0) return null;
            var element = ManifoldElements.Get(elementKey);
            var map = FamilyMaps[element.Key];
            var family = map.Resolve(dnMm);
            if (string.IsNullOrWhiteSpace(family)) return null;
            var pick = ValveTypeMatcher.Pick(TypesFor(family, map, ElementTypes[element.Key]), dnMm, ValvePnBar, element.TypeWord);
            return new MepValve
            {
                Kind = element.Kind,
                FamilyName = family.Trim(),
                TypeName = pick == null ? null : pick.TypeName,
                DnMm = dnMm,
                PnBar = ValvePnBar,
                WithFlanges = element.FlangesFor(family), // le famiglie filettate (R60) senza flange automatiche
                RollDegrees = Mix2RollDegrees,
                PreferredTypeWord = element.TypeWord
            };
        }

        /// <summary>
        /// La catena di uno stacco in una riga, per confrontarla a colpo d'occhio con una foto o uno
        /// schema: "[sfera DN25] — 150 — [T →DN25] — (spazio riservato alla pompa 400) — … — 100 fine".
        /// Tra parentesi quadre i pezzi (* = tra due flange), i numeri sono tubo libero in mm.
        /// </summary>
        public string DescribeChain(ManifoldCircuit circuit, bool supply)
        {
            var parts = new List<string>();
            foreach (var item in ChainFor(circuit, supply))
            {
                switch (item.Kind)
                {
                    case StubItemKind.Piece:
                        if (item.Piece == null) break;
                        var element = ManifoldElements.ByKind(item.Piece.Kind);
                        parts.Add("[" + (element != null ? element.ShortLabel : item.Piece.KindLabel) + " DN" + MepSize.Fmt(item.Piece.DnMm) +
                                  (item.Piece.WithFlanges ? "*" : string.Empty) + "]");
                        break;
                    case StubItemKind.Gap:
                        // il tubo ordinario (anche quello tra pezzi flangiati) è solo un numero; restano
                        // etichettati gli spazi riservati e i vincoli (pompa, tratto rettilineo)
                        var plain = item.Label == "tubo" || item.Label == "tra pezzi flangiati";
                        parts.Add(plain ? MepSize.Fmt(item.LengthMm) : "(" + item.Label + " " + MepSize.Fmt(item.LengthMm) + ")");
                        break;
                    case StubItemKind.Tee:
                        parts.Add("[T" + (item.SizeAfterMm > 0 ? " →DN" + MepSize.Fmt(item.SizeAfterMm) : string.Empty) + "]");
                        break;
                }
            }
            var end = PipeAfterValveFor(circuit);
            if (end.HasValue && end.Value > 0) parts.Add(MepSize.Fmt(end.Value) + " fine");
            return "collettore → " + string.Join(" — ", parts);
        }

        /// <summary>
        /// Catena dei pezzi sullo stacco di un circuito mix 2 vie, dall'asse del collettore:
        /// mandata: intercettazione → T del bypass → spazio pompa → valvola di zona → intercettazione;
        /// ritorno: intercettazione → energy valve → T del bypass → filtro a Y → valvola di zona → intercettazione.
        /// I pezzi senza famiglia (o senza misura) vengono saltati: restano dichiarati in anteprima.
        /// </summary>
        /// <summary>Chiave di allineamento dell'intercettazione in cima alle catene di mandata e ritorno.</summary>
        public const string TopShutoffAlignKey = "intercettazione in cima";

        public List<StubItem> ChainFor(ManifoldCircuit circuit, bool supply)
        {
            var chain = new List<StubItem>();
            if (!HasChain(circuit)) return chain;
            var dn = circuit.DnMm;                       // prima del bypass (lato collettore)
            var dnAfter = circuit.EffectiveDnAfterBypassMm; // dopo il bypass (verso l'utenza), come il bypass
            var shutoff = ValveFor(dn);
            chain.Add(StubItem.Of(shutoff, ValveAxisDistanceMm));

            if (!circuit.KindInfo.IsChainModelled)
            {
                // diretto / mix 3 vie con pompa: solo sulla mandata, intercettazione → pompa (→ valvola di
                // zona); sul ritorno resta la sola intercettazione (catena vuota = stacco semplice).
                // La valvola a 3 vie e il bypass del mix 3 vie non sono ancora modellati: la pompa
                // segue l'intercettazione, e si sposterà dopo il bypass quando ci sarà.
                if (!supply) { chain.Clear(); return chain; }
                var pump = PumpFor(circuit);
                if (pump == null) { chain.Clear(); return chain; }
                chain.Add(StubItem.Gap(Gap(Mix2GapMm)));
                chain.Add(StubItem.Of(pump));
                var zoneAfterPump = circuit.EffectiveWithZoneValve ? ZoneValveFor(dn) : null;
                if (zoneAfterPump != null)
                {
                    chain.Add(StubItem.Gap(Gap(Mix2GapMm)));
                    chain.Add(StubItem.Of(zoneAfterPump));
                }
                return chain;
            }

            if (supply)
            {
                if (CanBuildBypass)
                {
                    chain.Add(StubItem.Gap(Gap(Mix2GapMm)));
                    chain.Add(StubItem.TeeFor("T del bypass (uscita verso il ritorno)", dnAfter));
                }
                chain.Add(StubItem.Gap(Gap(Mix2GapMm)));
                // la pompa dopo il bypass: il modello scelto nella riga, altrimenti resta lo spazio riservato
                var pump = PumpFor(circuit);
                if (pump != null) chain.Add(StubItem.Of(pump));
                else chain.Add(StubItem.Gap(Gap(Mix2PumpSpaceMm), "spazio riservato alla pompa"));
                chain.Add(StubItem.Gap(Gap(Mix2GapMm)));
            }
            else
            {
                var ev = EnergyValveFor(circuit);
                var straightPending = false; // il tratto rettilineo sopra la energy valve è già stato messo
                if (ev != null)
                {
                    chain.Add(StubItem.Gap(Gap(Mix2GapMm)));
                    chain.Add(StubItem.Of(ev));
                    // vincolo Belimo: sopra la energy valve (a monte, il ritorno scende) un tratto
                    // rettilineo di almeno 5 volte il diametro interno del tubo ADIACENTE alla valvola:
                    // se la valvola è più piccola dello stacco, è il tubo della sua misura, prima della riduzione
                    chain.Add(StubItem.Gap(EnergyValveStraightMm(EnergyValveStraightDn(ev, dn)),
                        "tratto rettilineo " + MepSize.Fmt(EnergyValveStraightFactor) + "×Øint sopra la energy valve"));
                    straightPending = true;
                }
                if (CanBuildBypass)
                {
                    if (!straightPending) chain.Add(StubItem.Gap(Gap(Mix2GapMm)));
                    chain.Add(StubItem.TeeFor("T del bypass (arrivo dalla mandata)", dnAfter));
                    straightPending = false;
                }
                if (!straightPending) chain.Add(StubItem.Gap(Gap(Mix2GapMm)));
                var strainer = StrainerFor(dnAfter);
                if (strainer != null)
                {
                    chain.Add(StubItem.Of(strainer));
                    chain.Add(StubItem.Gap(Gap(Mix2FlangedGapMm), "tra pezzi flangiati"));
                }
            }

            // valvola di zona: accessorio opzionale, scelto per circuito
            var zone = circuit.EffectiveWithZoneValve ? ZoneValveFor(dnAfter) : null;
            if (zone != null)
            {
                chain.Add(StubItem.Of(zone));
                chain.Add(StubItem.Gap(Gap(Mix2FlangedGapMm), "tra pezzi flangiati"));
            }
            // seconda intercettazione in cima, della misura dopo il bypass
            var top = ValveFor(dnAfter);
            top.DistanceMm = 0;
            var topItem = StubItem.Of(top);
            // mandata e ritorno: le due intercettazioni in cima alla stessa quota (la catena più
            // corta riceve tubo in più davanti alla valvola; lo decide il costruttore sulle misure vere)
            topItem.AlignKey = TopShutoffAlignKey;
            chain.Add(topItem);
            return chain;
        }

        /// <summary>Bypass del circuito verso lo stacco gemello; null se non si può costruire.</summary>
        public MepBypass BypassFor(ManifoldCircuit circuit, int index, bool supply)
        {
            if (!HasChain(circuit) || !circuit.KindInfo.IsChainModelled || !CanBuildBypass) return null;
            var sign = supply ? 1 : -1;
            return new MepBypass
            {
                Key = CircuitLabel(circuit, index),
                PartnerAlongMm = sign * SpacingMm / 2.0,
                PartnerSideMm = sign * ReturnOffsetMm,
                // dalla mandata prima di traverso (Y) fino alla verticale della base del ritorno,
                // dal ritorno lungo la base (X) fino allo stesso punto: il tratto verticale sta lì
                LegAlongMm = supply ? 0 : -SpacingMm / 2.0,
                LegSideMm = supply ? ReturnOffsetMm : 0,
                // il bypass ha sempre il DN di dopo il bypass
                DnMm = circuit.EffectiveDnAfterBypassMm,
                InlinePiece = CheckValveFor(circuit.EffectiveDnAfterBypassMm)
            };
        }

        /// <summary>Stima della lunghezza della catena oltre la prima intercettazione (mm), per lo stacco provvisorio e l'ingombro.</summary>
        public double EstimatedChainLengthMm(ManifoldCircuit circuit)
        {
            if (!HasChain(circuit)) return 0;
            // la catena più lunga tra mandata e ritorno: le due devono comunque stare nello stesso interasse
            double Estimate(bool supply)
            {
                double total = 0;
                foreach (var item in ChainFor(circuit, supply).Skip(1))
                {
                    if (item.Kind == StubItemKind.Gap) total += item.LengthMm;
                    else if (item.Kind == StubItemKind.Tee) total += 2 * circuit.DnMm;
                    else total += ValveAssemblyAllowanceMm;
                }
                return total;
            }
            return Math.Max(Estimate(true), Estimate(false));
        }

        /// <summary>Valvola del circuito: quella del suo DN, se la tipologia la prevede.</summary>
        public MepValve ValveFor(ManifoldCircuit circuit)
        {
            if (circuit == null || !circuit.KindInfo.HasShutoffValve) return null;
            return ValveFor(circuit.DnMm);
        }

        /// <summary>Sporgenza del collettore oltre il BORDO del primo e dell'ultimo circuito (mm).</summary>
        public double OverhangMm { get; set; } = 50;

        /// <summary>Se true viene creato anche il collettore di ritorno: clone speculare interlacciato.</summary>
        public bool WithReturn { get; set; } = true;

        /// <summary>Distanza tra l'asse della mandata e quello del ritorno (mm).</summary>
        public double ReturnOffsetMm { get; set; } = 300;

        public DirectionKind HeaderDirection { get; set; } = DirectionKind.PlusX;

        public DirectionKind CircuitDirection { get; set; } = DirectionKind.Down;

        /// <summary>Nome esatto del tipo di tubazione scelto nel progetto; null = tipo predefinito.</summary>
        public string PipeTypeName { get; set; }

        /// <summary>
        /// Misure del tipo scelto (DN + Øinterno): se presenti, il DN automatico della base è
        /// quello col diametro INTERNO minimo tra quelli ≥ D calcolato dalla formula.
        /// </summary>
        public List<CatalogPipeSize> HeaderSizeCandidates { get; } = new List<CatalogPipeSize>();

        // ------------------------------------------------------------- valvole

        /// <summary>Se true su ogni stacco viene inserita una valvola in linea.</summary>
        public bool WithValves { get; set; } = true;

        /// <summary>
        /// DN massimo (incluso) per cui si usa la valvola a sfera; oltre questo DN si usa la boax.
        /// Predefinito 32: sfera fino a DN32, boax da DN40 in su.
        /// </summary>
        public double BallValveMaxDnMm { get; set; } = 32;

        /// <summary>Famiglia della valvola a sfera per DN (base + eventuali soglie); vuota = nessuna.</summary>
        public FamilyByDn BallValveMap
        {
            get { return FamilyMaps[ManifoldElements.Ball]; }
        }

        /// <summary>Famiglia della valvola boax per DN (base + eventuali soglie); vuota = nessuna.</summary>
        public FamilyByDn ButterflyValveMap
        {
            get { return FamilyMaps[ManifoldElements.Butterfly]; }
        }

        /// <summary>Famiglia di base delle valvole a sfera (tutti i DN senza soglia); vuota = nessuna.</summary>
        public string BallValveFamily
        {
            get { return BallValveMap.Default; }
            set { BallValveMap.Default = value; }
        }

        /// <summary>Famiglia di base delle valvole boax (tutti i DN senza soglia); vuota = nessuna.</summary>
        public string ButterflyValveFamily
        {
            get { return ButterflyValveMap.Default; }
            set { ButterflyValveMap.Default = value; }
        }

        /// <summary>Nomi dei tipi delle due famiglie di base: servono a scegliere il tipo sul DN già in anteprima.</summary>
        public List<string> BallValveTypes
        {
            get { return ElementTypes[ManifoldElements.Ball]; }
        }

        public List<string> ButterflyValveTypes
        {
            get { return ElementTypes[ManifoldElements.Butterfly]; }
        }

        /// <summary>PN preferito quando i nomi dei tipi lo dichiarano (0 = indifferente).</summary>
        public double ValvePnBar { get; set; } = 16;

        /// <summary>
        /// Distanza dal BORDO esterno del collettore al centro della valvola, lungo lo stacco (mm).
        /// La distanza dall'asse, quella che serve a costruire, è questa più il raggio esterno
        /// della base (<see cref="HeaderOuterRadiusMm"/>): così il valore non dipende dal DN
        /// che la formula assegna al collettore.
        /// </summary>
        public double ValveDistanceMm { get; set; } = 150;

        /// <summary>Distanza dall'asse del collettore al centro della valvola (mm): bordo + raggio esterno.</summary>
        public double ValveAxisDistanceMm
        {
            get { return HeaderOuterRadiusMm + ValveDistanceMm; }
        }

        /// <summary>
        /// Raggio esterno della base (mm): dal diametro esterno della misura scelta nel tipo;
        /// senza dati sul tipo, un'approssimazione DN/2 + 5 mm.
        /// </summary>
        public double HeaderOuterRadiusMm
        {
            get
            {
                var dn = EffectiveHeaderDnMm;
                if (dn <= 0) return 0;
                var size = HeaderSizeCandidates.FirstOrDefault(c => c != null && Math.Abs(c.NominalMm - dn) < 0.001);
                if (size != null && size.OuterMm > 0) return size.OuterMm / 2.0;
                return dn / 2.0 + 5;
            }
        }

        /// <summary>
        /// Rotazione della boax attorno all'asse del tubo (gradi). A 0° lo Z della famiglia (la
        /// leva, nella boax) guarda lungo il collettore; a 90° guarda di traverso.
        /// </summary>
        public double ButterflyRollDegrees { get; set; } = 90;

        /// <summary>Rotazione della valvola a sfera attorno all'asse del tubo (gradi), stessa convenzione della boax.</summary>
        public double BallRollDegrees { get; set; } = 90;

        /// <summary>
        /// Valvola prevista per un circuito di questo DN: sotto la soglia la sfera, sopra la boax.
        /// Null se le valvole sono disattivate o la famiglia corrispondente non è stata scelta.
        /// Il tipo viene deciso qui, così l'anteprima mostra esattamente quello che verrà inserito.
        /// </summary>
        public MepValve ValveFor(double dnMm)
        {
            if (!WithValves || dnMm <= 0) return null;

            var ball = dnMm <= BallValveMaxDnMm + 0.001;
            var map = ball ? BallValveMap : ButterflyValveMap;
            var family = map.Resolve(dnMm);
            if (string.IsNullOrWhiteSpace(family)) return null;

            var pick = ValveTypeMatcher.Pick(TypesFor(family, map, ball ? BallValveTypes : ButterflyValveTypes), dnMm, ValvePnBar);
            return new MepValve
            {
                Kind = ball ? ValveKind.Ball : ValveKind.Butterfly,
                FamilyName = family.Trim(),
                TypeName = pick == null ? null : pick.TypeName,
                DnMm = dnMm,
                PnBar = ValvePnBar,
                DistanceMm = ValveAxisDistanceMm,
                // la boax si monta tra due flange, la valvola a sfera no; ognuna ha la sua rotazione
                WithFlanges = !ball,
                RollDegrees = ball ? BallRollDegrees : ButterflyRollDegrees
            };
        }

        /// <summary>Circuiti con un DN valido, gli unici che vengono modellati.</summary>
        public List<ManifoldCircuit> ValidCircuits()
        {
            return Circuits.Where(c => c != null && c.IsValid).ToList();
        }

        /// <summary>
        /// Diametro grezzo dalla formula D = √(1,5·(S₁+S₂+…)/0,785): con S = 0,785·dn²
        /// si semplifica in √(1,5·Σdn²).
        /// </summary>
        public double ComputedHeaderDnMm
        {
            get
            {
                var circuits = ValidCircuits();
                if (circuits.Count == 0) return 0;
                return Math.Sqrt(HeaderSizingFactor * circuits.Sum(c => c.DnMm * c.DnMm));
            }
        }

        /// <summary>
        /// Tra le misure del tipo, quella col diametro interno minimo ma ≥ D della formula;
        /// se nessuna basta, la più grande (segnalato a valle); null senza dati sugli interni.
        /// </summary>
        public CatalogPipeSize PickHeaderSize()
        {
            var d = ComputedHeaderDnMm;
            if (d <= 0) return null;
            var usable = HeaderSizeCandidates
                .Where(c => c != null && c.InnerMm > 0 && c.NominalMm > 0)
                .OrderBy(c => c.InnerMm).ThenBy(c => c.NominalMm)
                .ToList();
            if (usable.Count == 0) return null;
            var pick = usable.FirstOrDefault(c => c.InnerMm >= d - 0.001);
            return pick ?? usable[usable.Count - 1];
        }

        /// <summary>
        /// DN automatico della base: dalla misura scelta con <see cref="PickHeaderSize"/>;
        /// senza diametri interni si ripiega sull'arrotondamento alla serie DN commerciale.
        /// </summary>
        public double AutoHeaderDnMm
        {
            get
            {
                var computed = ComputedHeaderDnMm;
                if (computed <= 0) return 0;
                var pick = PickHeaderSize();
                return pick != null ? pick.NominalMm : SnapUpToDn(computed);
            }
        }

        public double EffectiveHeaderDnMm
        {
            get { return HeaderDnMm.HasValue && HeaderDnMm.Value > 0 ? HeaderDnMm.Value : AutoHeaderDnMm; }
        }

        /// <summary>
        /// La base parte 5 cm (OverhangMm) prima del bordo del primo circuito e finisce 5 cm dopo
        /// il bordo dell'ultimo (bordo = posizione ± DN/2). Con il ritorno attivo gli stacchi che
        /// governano gli estremi sono il PRIMO della mandata e l'ULTIMO del ritorno (che sta mezzo
        /// interasse più avanti): entrambe le basi, identiche e allineate, si allungano di s/2.
        /// </summary>
        public double HeaderLengthMm
        {
            get
            {
                var circuits = ValidCircuits();
                if (circuits.Count == 0) return 0;
                var first = circuits[0].DnMm / 2.0;
                var last = circuits[circuits.Count - 1].DnMm / 2.0;
                var length = OverhangMm + first + (circuits.Count - 1) * SpacingMm + last + OverhangMm;
                if (WithReturn) length += SpacingMm / 2.0;
                return length;
            }
        }

        /// <summary>Distanza di ogni circuito dall'inizio del collettore (mm).</summary>
        public List<double> CircuitPositionsMm()
        {
            var list = new List<double>();
            var circuits = ValidCircuits();
            if (circuits.Count == 0) return list;
            var start = OverhangMm + circuits[0].DnMm / 2.0;
            for (var i = 0; i < circuits.Count; i++) list.Add(start + i * SpacingMm);
            return list;
        }

        public static double SnapUpToDn(double mm)
        {
            foreach (var dn in DnSeries)
            {
                if (dn >= mm - 0.001) return dn;
            }
            return Math.Ceiling(mm);
        }

        public static string CircuitLabel(ManifoldCircuit circuit, int index)
        {
            return string.IsNullOrWhiteSpace(circuit?.Name) ? "C" + (index + 1) : circuit.Name.Trim();
        }

        /// <summary>Traduce il collettore nel piano MEP generico costruito da RevitPlanBuilder.</summary>
        public ParseResult ToParseResult()
        {
            var circuits = ValidCircuits();
            if (circuits.Count == 0) return ParseResult.Fail("Aggiungi almeno un circuito indicando il DN.");
            if (SpacingMm <= 0) return ParseResult.Fail("L'interasse tra i circuiti deve essere maggiore di zero.");
            if (CircuitLengthMm <= 0) return ParseResult.Fail("La lunghezza dei circuiti deve essere maggiore di zero.");
            if (circuits.Any(c => c.KindInfo.UsesPipeAfterValve) && NoPumpPipeAfterValveMm <= 0)
                return ParseResult.Fail("La lunghezza del tubo dopo la valvola (circuito senza pompa) deve essere maggiore di zero.");

            var headerDn = EffectiveHeaderDnMm;
            if (headerDn <= 0) return ParseResult.Fail("DN del collettore non valido.");
            if (WithReturn && ReturnOffsetMm <= 0)
                return ParseResult.Fail("La distanza tra mandata e ritorno deve essere maggiore di zero.");

            var run = MakeHeaderRun(headerDn);
            var positions = CircuitPositionsMm();
            // Sfasamento: il PRIMO collettore porta gli stacchi non sfasati,
            // il secondo quelli sfasati di mezzo interasse.
            for (var i = 0; i < circuits.Count; i++)
                run.Branches.Add(MakeCircuitBranch(circuits[i], i, positions[i], true));

            var plan = new MepPlan { SourceText = Summary() };
            plan.Runs.Add(run);

            var result = new ParseResult { Success = true, Plan = plan };
            result.Notes.Add("I circuiti non vengono raccordati: partono dall'asse del collettore, sovrapposti, senza T.");
            result.Notes.Add("Fondelli (Enddeckel) alle estremità delle basi: automatici per inox e acciaio nero; " +
                             "per gli altri materiali le estremità restano aperte.");
            if (AutoSpacing)
            {
                if (_autoSpacing == null)
                    result.Notes.Add("Interasse automatico: verrà calcolato in Revit dagli ingombri reali di valvole e flange, " +
                                     "mai sotto " + MepSize.Fmt(SpacingMm) + " mm.");
                else
                {
                    result.Notes.Add("Interasse automatico: " + MepSize.Fmt(SpacingMm) + " mm (minimo richiesto " +
                                     MepSize.Fmt(SpacingFloorMm) + " mm, aria " + MepSize.Fmt(SpacingClearanceMm) + " mm).");
                    foreach (var n in _autoSpacing.Notes) result.Notes.Add(n);
                    foreach (var w in _autoSpacing.Warnings) result.Warnings.Add(w);
                }
            }

            if (WithReturn)
            {
                plan.Runs.Add(MakeReturnRun(headerDn, circuits, positions));
                result.Notes.Add("Due collettori: basi identiche e allineate a " + MepSize.Fmt(ReturnOffsetMm) +
                                 " mm; il secondo porta gli stacchi sfasati di mezzo interasse, " +
                                 "interlacciati a metà di quelli del primo.");
            }
            if (!HeaderDnMm.HasValue || HeaderDnMm.Value <= 0)
            {
                var computed = MepSize.Fmt(ComputedHeaderDnMm);
                var pick = PickHeaderSize();
                if (pick != null && pick.InnerMm >= ComputedHeaderDnMm - 0.001)
                {
                    result.Notes.Add("DN collettore dalla formula D = √(1,5·ΣS/0,785): " + computed +
                                     " mm → DN" + MepSize.Fmt(pick.NominalMm) + " (Øint " + MepSize.Fmt(pick.InnerMm) +
                                     " mm, il minimo ≥ D tra le misure del tipo).");
                }
                else if (pick != null)
                {
                    result.Warnings.Add("Nessuna misura del tipo ha Øint ≥ " + computed + " mm (formula): uso la più grande, DN" +
                                        MepSize.Fmt(pick.NominalMm) + " (Øint " + MepSize.Fmt(pick.InnerMm) + " mm).");
                }
                else
                {
                    result.Notes.Add("DN collettore dalla formula D = √(1,5·ΣS/0,785): " + computed +
                                     " mm → DN" + MepSize.Fmt(headerDn) +
                                     " (serie commerciale: diametri interni del tipo non disponibili).");
                }
            }
            result.Notes.Add(WithReturn
                ? "Basi lunghe " + MepSize.Fmt(HeaderLengthMm) + " mm: sporgono di " + MepSize.Fmt(OverhangMm) +
                  " mm dal bordo del primo circuito di mandata e dell'ultimo circuito di ritorno."
                : "Base lunga " + MepSize.Fmt(HeaderLengthMm) + " mm: sporge di " + MepSize.Fmt(OverhangMm) +
                  " mm dal bordo del primo e dell'ultimo circuito.");

            var oversized = circuits
                .Select((c, i) => new { c, i })
                .Where(x => x.c.DnMm >= headerDn)
                .Select(x => CircuitLabel(x.c, x.i) + " DN" + MepSize.Fmt(x.c.DnMm))
                .ToList();
            if (oversized.Count > 0)
                result.Warnings.Add("Circuiti con DN maggiore o uguale al collettore (DN" + MepSize.Fmt(headerDn) + "): " +
                                    string.Join(", ", oversized) + ".");

            var minSpacing = circuits.Max(c => c.DnMm);
            if (SpacingMm < minSpacing)
                result.Warnings.Add("Interasse " + MepSize.Fmt(SpacingMm) + " mm inferiore al DN massimo dei circuiti (" +
                                    MepSize.Fmt(minSpacing) + "): i circuiti potrebbero sovrapporsi tra loro.");

            CollectValveMessages(circuits, headerDn, result);
            CollectCircuitKindMessages(circuits, result);

            return result;
        }

        /// <summary>Circuiti validi raggruppati per tipologia, nell'ordine di <see cref="CircuitKinds.All"/>.</summary>
        public List<KeyValuePair<CircuitKindInfo, List<ManifoldCircuit>>> CircuitsByKind()
        {
            var circuits = ValidCircuits();
            return CircuitKinds.All
                .Select(info => new KeyValuePair<CircuitKindInfo, List<ManifoldCircuit>>(info, circuits.Where(c => c.Kind == info.Kind).ToList()))
                .Where(kv => kv.Value.Count > 0)
                .ToList();
        }

        /// <summary>
        /// Note sulle tipologie dei circuiti: quali sono presenti, con quali componenti, e cosa di
        /// quei componenti viene modellato oggi (solo l'intercettazione: pompe, valvole di
        /// regolazione e bypass sono il passo successivo e vengono dichiarati, non taciuti).
        /// </summary>
        private void CollectCircuitKindMessages(List<ManifoldCircuit> circuits, ParseResult result)
        {
            var groups = CircuitsByKind();
            foreach (var kv in groups)
            {
                var labels = kv.Value.Select(c => CircuitLabel(c, circuits.IndexOf(c))).ToList();
                result.Notes.Add(kv.Key.Label + " (" + string.Join(", ", labels) + "): " +
                                 (kv.Key.IsChainModelled
                                     ? "mandata " + CircuitKinds.SupplyChain(kv.Key.Kind) + "; ritorno " + CircuitKinds.ReturnChain(kv.Key.Kind)
                                     : CircuitKinds.SupplyChain(kv.Key.Kind)) + ".");
            }

            CollectMixTwoWayMessages(circuits, result);
            CollectPumpMessages(circuits, result);

            var noPump = circuits.Where(c => c.KindInfo.UsesPipeAfterValve).ToList();
            if (noPump.Any(c => PipeAfterValveFor(c).HasValue))
                result.Notes.Add("Circuiti senza pompa: tubo lungo " + MepSize.Fmt(NoPumpPipeAfterValveMm) +
                                 " mm dopo la valvola (dalla seconda flangia, o dall'uscita della sfera), su mandata e ritorno; " +
                                 "la fine dello stacco viene fissata in Revit sull'ingombro reale della valvola.");
            else if (noPump.Count > 0)
                result.Warnings.Add("Circuiti senza pompa senza valvola: la regola del tubo dopo la valvola non ha un riferimento, " +
                                    "uso la lunghezza generica dei circuiti (" + MepSize.Fmt(CircuitLengthMm) + " mm).");

            var blind = circuits.Where(c => c.KindInfo.IsBlind).ToList();
            if (blind.Any(c => PipeAfterValveFor(c).HasValue))
                result.Notes.Add("Circuiti ciechi: lo stacco si ferma alla seconda flangia della valvola (o all'uscita della sfera), " +
                                 "nessun tubo a valle, su mandata e ritorno.");
            else if (blind.Count > 0)
                result.Warnings.Add("Circuiti ciechi senza valvola: non c'è una flangia a cui fermarsi, " +
                                    "uso la lunghezza generica dei circuiti (" + MepSize.Fmt(CircuitLengthMm) + " mm).");

            // le tipologie la cui catena non è ancora modellata: dichiarate, non taciute
            var declared = circuits.Where(c => !c.KindInfo.IsChainModelled).ToList();
            var pending = new List<string>();
            if (declared.Any(c => c.KindInfo.HasPump && PumpFor(c) == null)) pending.Add("pompe (nessun modello scelto nella riga)");
            if (declared.Any(c => c.Kind == CircuitKind.MixThreeWay)) pending.Add("valvole miscelatrici a 3 vie");
            if (declared.Any(c => c.KindInfo.HasBypass)) pending.Add("bypass mandata/ritorno");
            var zoneElsewhere = declared.Where(c => c.EffectiveWithZoneValve && !HasPumpChain(c)).ToList();
            if (zoneElsewhere.Count > 0)
                pending.Add("valvole di zona su " + string.Join(", ", zoneElsewhere.Select(c => CircuitLabel(c, circuits.IndexOf(c)))));
            if (pending.Count > 0)
                result.Warnings.Add("Tipologie: " + string.Join(", ", pending) +
                                    " non ancora modellati in Revit; sugli stacchi viene inserita solo l'intercettazione (e la pompa, se scelta).");
        }

        /// <summary>Pompe: per ogni circuito con pompa, il modello che verrà montato e dove, oppure perché manca.</summary>
        private void CollectPumpMessages(List<ManifoldCircuit> circuits, ParseResult result)
        {
            foreach (var c in circuits)
            {
                if (!c.KindInfo.HasPump) continue;
                var label = CircuitLabel(c, circuits.IndexOf(c));
                if (string.IsNullOrWhiteSpace(c.PumpType)) continue;
                var family = PumpFamilyFor(c);
                if (family == null)
                {
                    result.Warnings.Add(label + ": modello di pompa \"" + c.PumpType + "\" scelto ma nessuna famiglia della pompa in \"Mostra di più\": stacco senza pompa.");
                    continue;
                }
                var pump = PumpFor(c);
                if (pump == null)
                {
                    result.Warnings.Add(label + ": modello di pompa \"" + c.PumpType + "\" non trovato nella famiglia \"" + family + "\": stacco senza pompa.");
                    continue;
                }
                if (ValveFor(c.DnMm) == null)
                {
                    result.Warnings.Add(label + ": pompa \"" + pump.TypeName + "\" senza valvola di intercettazione: la catena parte dall'intercettazione, quindi la pompa non viene montata.");
                    continue;
                }
                var where = c.KindInfo.IsChainModelled
                    ? "sulla mandata dopo il T del bypass"
                    : c.Kind == CircuitKind.MixThreeWay
                        ? "sulla mandata dopo l'intercettazione (valvola a 3 vie e bypass non ancora modellati: la pompa seguirà il bypass quando ci sarà)"
                        : "sulla mandata dopo l'intercettazione";
                var typeDn = ValveTypeMatcher.DnFromTypeName(pump.TypeName);
                result.Notes.Add(label + " DN" + MepSize.Fmt(pump.DnMm) + " → pompa \"" + pump.TypeName + "\" (famiglia \"" + pump.FamilyName + "\") " + where +
                                 (pump.WithFlanges ? ", tra due flange" : ", attacchi filettati") +
                                 (typeDn.HasValue && Math.Abs(typeDn.Value - pump.DnMm) > 0.5
                                     ? "; modello DN" + MepSize.Fmt(typeDn.Value) + " su stacco DN" + MepSize.Fmt(pump.DnMm) + ": riduzioni prima e dopo"
                                     : string.Empty) +
                                 "; rotazione " + MepSize.Fmt(PumpRollDegrees) + "°" + (PumpReversed ? ", verso invertito" : "") + ".");
            }
        }

        /// <summary>
        /// Mix 2 vie (iniezione): cosa verrà montato per ogni DN (famiglie e tipi scelti), le
        /// lunghezze della catena, e cosa manca (famiglie non scelte, DN senza energy valve,
        /// bypass non costruibile).
        /// </summary>
        private void CollectMixTwoWayMessages(List<ManifoldCircuit> circuits, ParseResult result)
        {
            var mix2 = circuits.Where(c => c.Kind == CircuitKind.MixTwoWayInjection).ToList();
            if (mix2.Count == 0) return;

            var without = mix2.Where(c => !HasChain(c)).ToList();
            if (without.Count > 0)
                result.Warnings.Add("Mix 2 vie senza valvola di intercettazione (" +
                                    string.Join(", ", without.Select(c => CircuitLabel(c, circuits.IndexOf(c)))) +
                                    "): la catena parte dall'intercettazione, quindi non viene modellata; resta il solo stacco.");
            var chained = mix2.Where(HasChain).ToList();
            if (chained.Count == 0) return;

            result.Notes.Add("Mix 2 vie: tubo libero " + MepSize.Fmt(Gap(Mix2GapMm)) + " mm tra i pezzi e attorno ai T, " +
                             MepSize.Fmt(Gap(Mix2FlangedGapMm)) + " mm tra filtro, valvola di zona e intercettazione (mai meno di " +
                             MepSize.Fmt(Mix2MinGapMm) + " mm di tubo diritto tra due elementi); spazio per la pompa " + MepSize.Fmt(Gap(Mix2PumpSpaceMm)) +
                             " mm nelle righe senza modello di pompa; tubo finale " + MepSize.Fmt(Mix2EndPipeMm) +
                             " mm dopo l'intercettazione in cima; accessori girati di " + MepSize.Fmt(Mix2RollDegrees) + "°.");
            if (!WithReturn)
                result.Warnings.Add("Mix 2 vie senza collettore di ritorno: il bypass non ha lo stacco gemello e non viene costruito.");
            else if (!CanBuildBypass)
                result.Warnings.Add("Mix 2 vie con stacchi laterali: il bypass è previsto solo per stacchi verticali (verso l'alto o il basso) e non viene costruito.");
            else
                result.Notes.Add("Bypass mix 2 vie: dal T di mandata (sopra l'intercettazione) un tubo di traverso fino alla verticale della base del ritorno (" +
                                 MepSize.Fmt(ReturnOffsetMm) + " mm), lì il tratto verticale con due gomiti" +
                                 (CheckValveMap.IsEmpty ? " (nessuna valvola di ritegno: famiglia non scelta)" : " e la valvola di ritegno") +
                                 ", poi un tubo lungo la base (mezzo interasse, " + MepSize.Fmt(SpacingMm / 2.0) + " mm) fino al T di ritorno sopra la energy valve.");

            if (ZoneValveMap.IsEmpty && chained.Any(c => c.EffectiveWithZoneValve))
                result.Warnings.Add("Mix 2 vie: nessuna famiglia scelta per la valvola di zona; le catene restano senza.");
            var noZone = chained.Where(c => !c.EffectiveWithZoneValve).ToList();
            if (noZone.Count > 0)
                result.Notes.Add("Senza valvola di zona (scelta nella riga): " + string.Join(", ", noZone.Select(c => CircuitLabel(c, circuits.IndexOf(c)))) + ".");
            if (StrainerMap.IsEmpty)
                result.Warnings.Add("Mix 2 vie: nessuna famiglia scelta per il filtro a Y; il ritorno resta senza filtro.");
            foreach (var element in ManifoldElements.In(ElementSection.MixTwoWay))
                DescribeRules(result, element.Label, FamilyMaps[element.Key], element.AutoByName ? "automatica sul DN" : null);

            // la catena in una riga, per confrontarla con la foto/lo schema di riferimento
            foreach (var c in chained)
            {
                var label = CircuitLabel(c, circuits.IndexOf(c));
                result.Notes.Add(label + " mandata: " + DescribeChain(c, true));
                if (WithReturn) result.Notes.Add(label + " ritorno: " + DescribeChain(c, false));
            }
            if (WithReturn)
                result.Notes.Add("Intercettazioni in cima alla stessa quota su mandata e ritorno: la catena più corta riceve " +
                                 "tubo in più davanti all'ultima intercettazione (deciso in Revit sulle misure vere dei pezzi).");

            // energy valve: per circuito (la riga può fissarla), sul DN prima del bypass
            var evDns = EnergyValveDns();
            foreach (var c in chained)
            {
                var label = CircuitLabel(c, circuits.IndexOf(c));
                var ev = EnergyValveFor(c);
                if (ev == null)
                {
                    result.Warnings.Add(label + " DN" + MepSize.Fmt(c.DnMm) + ": nessuna famiglia di energy valve per questa misura (nel progetto: " +
                                        (evDns.Count == 0 ? "nessuna" : string.Join(", ", evDns.Select(d => "DN" + MepSize.Fmt(d)))) +
                                        "); scegline una nella riga o il ritorno resta senza valvola a 2 vie.");
                    continue;
                }
                var evDn = EnergyValveDnOf(ev);
                result.Notes.Add(label + " DN" + MepSize.Fmt(c.DnMm) + " → energy valve \"" + ev.FamilyName + "\" tipo \"" + ev.TypeName + "\"" +
                                 " (" + EnergyValveSourceOf(c) + ")" +
                                 "; sopra di essa tubo rettilineo di " + MepSize.Fmt(EnergyValveStraightMm(EnergyValveStraightDn(ev, c.DnMm))) + " mm (" +
                                 MepSize.Fmt(EnergyValveStraightFactor) + " × Øint " + MepSize.Fmt(InnerDiameterMm(EnergyValveStraightDn(ev, c.DnMm))) +
                                 " mm del tubo DN" + MepSize.Fmt(EnergyValveStraightDn(ev, c.DnMm)) + " adiacente alla valvola" +
                                 (EnergyValveStraightDn(ev, c.DnMm) < c.DnMm ? ", prima della riduzione" : "") + ").");
                if (evDn.HasValue && Math.Abs(evDn.Value - c.DnMm) > 0.5)
                    result.Notes.Add(label + ": energy valve DN" + MepSize.Fmt(evDn.Value) + " su un circuito DN" + MepSize.Fmt(c.DnMm) +
                                     ": prima e dopo il pezzo viene inserita una riduzione (transizione del tipo di tubazione) con un tratto corto della misura del pezzo.");
            }

            // il resto della catena e il bypass hanno il DN di dopo il bypass
            foreach (var dn in chained.Select(c => c.EffectiveDnAfterBypassMm).Distinct().OrderBy(d => d))
            {
                if (chained.Any(c => c.EffectiveWithZoneValve && Math.Abs(c.EffectiveDnAfterBypassMm - dn) < 0.001))
                    DescribePick(result, dn, "valvola di zona", ZoneValveMap, ZoneValveTypes, ZoneValveTypeWord);
                DescribePick(result, dn, "filtro a Y", StrainerMap, StrainerTypes, null);
                if (CanBuildBypass) DescribePick(result, dn, "valvola di ritegno", CheckValveMap, CheckValveTypes, null);
            }

            var withBypass = circuits.Where(c => c.KindInfo.HasBypass).ToList();
            if (withBypass.Count > 0)
                result.Notes.Add("DN prima e dopo il bypass: " + string.Join(", ", withBypass.Select(c => CircuitLabel(c, circuits.IndexOf(c)) +
                                 " DN" + MepSize.Fmt(c.DnMm) + " prima del bypass → DN" + MepSize.Fmt(c.EffectiveDnAfterBypassMm) + " dopo il bypass")) +
                                 "; il bypass, i pezzi dopo il T e l'intercettazione in cima hanno il DN dopo il bypass" +
                                 (withBypass.Any(c => Math.Abs(c.EffectiveDnAfterBypassMm - c.DnMm) > 0.001) ? " (T ridotto)." : "."));
            if (!StrainerMap.IsEmpty)
                result.Notes.Add("Filtro a Y girato di " + MepSize.Fmt(StrainerRollDegrees) + "° attorno al tubo" +
                                 (StrainerReversed ? ", col verso invertito rispetto alla famiglia (Y verso il collettore)." : ", col verso della famiglia."));
        }

        /// <summary>Nota sulle soglie per DN di un elemento, solo se l'utente ne ha messe.</summary>
        private static void DescribeRules(ParseResult result, string what, FamilyByDn map, string emptyMeans)
        {
            if (map == null || !map.HasRules) return;
            var text = map.Describe();
            if (!string.IsNullOrWhiteSpace(emptyMeans)) text = text.Replace("nessuna", emptyMeans);
            result.Notes.Add("Famiglia per DN (" + what + "): " + text + ".");
        }

        private void DescribePick(ParseResult result, double dn, string what, FamilyByDn map, List<string> defaultTypes, string word)
        {
            if (map == null || map.IsEmpty) return;
            var family = map.Resolve(dn);
            if (string.IsNullOrWhiteSpace(family))
            {
                result.Notes.Add("DN" + MepSize.Fmt(dn) + ": nessuna famiglia per " + what + " su questa misura (soglia per DN); il pezzo non viene messo.");
                return;
            }
            var pick = ValveTypeMatcher.Pick(TypesFor(family, map, defaultTypes), dn, ValvePnBar, word);
            if (pick == null)
                result.Warnings.Add("DN" + MepSize.Fmt(dn) + ": nessun tipo della famiglia \"" + family + "\" (" + what +
                                    ") dichiara una misura nel nome; il tipo verrà scelto in Revit.");
            else if (!pick.ExactDn)
                result.Warnings.Add("DN" + MepSize.Fmt(dn) + ": la famiglia \"" + family + "\" (" + what + ") non ha un tipo DN" +
                                    MepSize.Fmt(dn) + "; uso \"" + pick.TypeName + "\" (DN" + MepSize.Fmt(pick.DnMm) + "), la misura più vicina.");
            else
                result.Notes.Add("DN" + MepSize.Fmt(dn) + " → " + what + " \"" + pick.TypeName + "\".");
        }

        /// <summary>
        /// Note e avvisi sulle valvole, uno per DN e non uno per circuito: la regola di scelta,
        /// il tipo che verrà usato per ogni DN e i casi in cui la famiglia non ha la misura giusta.
        /// </summary>
        private void CollectValveMessages(List<ManifoldCircuit> circuits, double headerDn, ParseResult result)
        {
            if (!WithValves)
            {
                result.Notes.Add("Nessuna valvola sugli stacchi (opzione disattivata).");
                return;
            }

            var valved = circuits.Where(c => c.KindInfo.HasShutoffValve).ToList();
            if (valved.Count == 0) return;
            var dns = valved.Select(c => c.DnMm).Distinct().OrderBy(d => d).ToList();
            var hasBall = dns.Any(d => d <= BallValveMaxDnMm + 0.001);
            var hasButterfly = dns.Any(d => d > BallValveMaxDnMm + 0.001);

            result.Notes.Add("Valvole in linea su ogni stacco: a sfera fino a DN" + MepSize.Fmt(BallValveMaxDnMm) +
                             " compreso, boax oltre; centro a " + MepSize.Fmt(ValveDistanceMm) +
                             " mm dal bordo del collettore (" + MepSize.Fmt(ValveAxisDistanceMm) + " mm dall'asse, raggio esterno " +
                             MepSize.Fmt(HeaderOuterRadiusMm) + " mm).");
            foreach (var element in ManifoldElements.In(ElementSection.Shutoff))
                DescribeRules(result, element.Label, FamilyMaps[element.Key], null);
            if (hasButterfly && !ButterflyValveMap.IsEmpty)
            {
                result.Notes.Add("Flange (Flansch) prima e dopo ogni valvola boax: automatiche per inox e acciaio nero, " +
                                 "come i fondelli; per gli altri materiali la valvola resta senza flange.");
                if (Math.Abs(ButterflyRollDegrees) > 0.001)
                    result.Notes.Add("Valvole boax girate di " + MepSize.Fmt(ButterflyRollDegrees) +
                                     "° attorno all'asse del tubo (flange comprese).");
            }
            if (hasBall && !BallValveMap.IsEmpty && Math.Abs(BallRollDegrees) > 0.001)
                result.Notes.Add("Valvole a sfera girate di " + MepSize.Fmt(BallRollDegrees) + "° attorno all'asse del tubo.");

            if (hasBall && BallValveMap.IsEmpty)
                result.Warnings.Add("Nessuna famiglia scelta per la valvola a sfera: i circuiti fino a DN" +
                                    MepSize.Fmt(BallValveMaxDnMm) + " restano senza valvola.");
            if (hasButterfly && ButterflyValveMap.IsEmpty)
                result.Warnings.Add("Nessuna famiglia scelta per la valvola boax: i circuiti oltre DN" +
                                    MepSize.Fmt(BallValveMaxDnMm) + " restano senza valvola.");

            foreach (var dn in dns)
            {
                var ball = dn <= BallValveMaxDnMm + 0.001;
                var map = ball ? BallValveMap : ButterflyValveMap;
                var kind = ball ? "valvola a sfera" : "valvola boax";
                if (map.IsEmpty) continue;
                var family = map.Resolve(dn);
                if (string.IsNullOrWhiteSpace(family))
                {
                    result.Notes.Add("DN" + MepSize.Fmt(dn) + ": nessuna famiglia per la " + kind + " su questa misura (soglia per DN); lo stacco resta senza valvola.");
                    continue;
                }

                var pick = ValveTypeMatcher.Pick(TypesFor(family, map, ball ? BallValveTypes : ButterflyValveTypes), dn, ValvePnBar);
                if (pick == null)
                {
                    result.Warnings.Add("DN" + MepSize.Fmt(dn) + ": nessun tipo della famiglia \"" + family +
                                        "\" dichiara una misura nel nome; il tipo verrà scelto in Revit al momento della creazione.");
                    continue;
                }
                if (!pick.ExactDn)
                {
                    result.Warnings.Add("DN" + MepSize.Fmt(dn) + ": la famiglia \"" + family + "\" non ha un tipo DN" +
                                        MepSize.Fmt(dn) + "; uso \"" + pick.TypeName + "\" (DN" + MepSize.Fmt(pick.DnMm) +
                                        "), la misura più vicina.");
                }
                else if (!pick.ExactPn)
                {
                    result.Warnings.Add("DN" + MepSize.Fmt(dn) + ": nessun tipo PN" + MepSize.Fmt(ValvePnBar) +
                                        " nella famiglia \"" + family + "\"; uso \"" + pick.TypeName + "\" (PN" +
                                        MepSize.Fmt(pick.PnBar) + ").");
                }
                else
                {
                    result.Notes.Add("DN" + MepSize.Fmt(dn) + " → " + kind + " \"" + pick.TypeName + "\".");
                }
            }

            if (ValveDistanceMm <= 0)
                result.Warnings.Add("Valvole a " + MepSize.Fmt(ValveDistanceMm) + " mm dal bordo: cadono dentro il collettore (DN" +
                                    MepSize.Fmt(headerDn) + "). Usa una distanza positiva, almeno 50 mm.");
            // la lunghezza generica vale solo per i circuiti che non la fissano altrimenti
            var generic = valved.Any(c => !PipeAfterValveFor(c).HasValue);
            if (generic && ValveAxisDistanceMm >= CircuitLengthMm)
                result.Warnings.Add("Valvole a " + MepSize.Fmt(ValveDistanceMm) + " mm dal bordo (" + MepSize.Fmt(ValveAxisDistanceMm) +
                                    " mm dall'asse): oltre la lunghezza dei circuiti (" + MepSize.Fmt(CircuitLengthMm) +
                                    " mm). Non verranno inserite.");
        }

        private MepRun MakeHeaderRun(double headerDn)
        {
            return new MepRun
            {
                Kind = MepKind.Pipe,
                KindExplicit = true,
                Size = MepSize.Round(headerDn, true),
                LengthMm = HeaderLengthMm,
                Direction = HeaderDirection,
                ExplicitTypeName = string.IsNullOrWhiteSpace(PipeTypeName) ? null : PipeTypeName.Trim(),
                CapEnds = true // fondelli (Enddeckel) automatici alle estremità della base
            };
        }

        private MepBranch MakeCircuitBranch(ManifoldCircuit circuit, int index, double positionMm, bool supply)
        {
            var dnMm = circuit.DnMm;
            var branch = new MepBranch
            {
                Size = MepSize.Round(dnMm, true),
                Count = 1,
                // lunghezza secondo la tipologia: per il senza pompa e il cieco è provvisoria, la
                // fissa Revit sulla faccia d'uscita della valvola (più il tubo a valle, se c'è)
                LengthMm = CircuitLengthFor(circuit),
                LengthAfterValveMm = PipeAfterValveFor(circuit),
                // I circuiti non vengono raccordati: solo sovrapposti al collettore, che resta
                // un tubo unico (il T di Revit ridimensionerebbe l'innesto alla misura del circuito).
                Connect = false,
                // Un circuito = un gruppo di stacchi con una sola posizione, quindi l'alternanza
                // non può essere risolta dall'indice a valle: la fissiamo qui.
                Direction = CircuitDirection == DirectionKind.Alternate
                    ? (index % 2 == 0 ? DirectionKind.Left : DirectionKind.Right)
                    : CircuitDirection,
                Valve = ValveFor(circuit),
                // mandata e ritorno dello stesso circuito: i pezzi da allineare si riconoscono da qui
                PairKey = CircuitLabel(circuit, index)
            };
            branch.PositionsMm.Add(positionMm);
            if (HasChain(circuit))
            {
                // mix 2 vie: la catena completa; diretto / mix 3 vie con pompa: intercettazione → pompa
                // sulla mandata (la prima voce è la stessa intercettazione di Valve). Catena vuota
                // (ritorno di un diretto con pompa) = stacco semplice con la sola valvola.
                var chain = ChainFor(circuit, supply);
                if (chain.Count > 1)
                {
                    if (chain[0].Piece != null) chain[0].Piece = branch.Valve;
                    branch.Chain.AddRange(chain);
                    branch.Bypass = BypassFor(circuit, index, supply);
                }
            }
            return branch;
        }

        /// <summary>
        /// Secondo collettore: base IDENTICA e perfettamente allineata alla prima (nessuna
        /// traslazione lungo l'asse), su un asse parallelo a <see cref="ReturnOffsetMm"/>.
        /// TUTTI gli stacchi vengono replicati (stessi DN, stesso ordine) e sfasati di mezzo
        /// interasse. Le basi, allungate di s/2 in <see cref="HeaderLengthMm"/>, rispettano
        /// i 5 cm sul primo stacco del primo collettore e sull'ultimo di questo: nessuno
        /// stacco cade mai fuori dalla base.
        /// </summary>
        private MepRun MakeReturnRun(double headerDn, List<ManifoldCircuit> circuits, List<double> supplyPositions)
        {
            var ret = MakeHeaderRun(headerDn);

            var shift = SpacingMm / 2.0;
            // stessa tipologia sul ritorno: stesso tubo dopo la valvola per il senza pompa, stessa
            // fine alla flangia per il cieco
            for (var i = 0; i < circuits.Count; i++)
                ret.Branches.Add(MakeCircuitBranch(circuits[i], i, supplyPositions[i] + shift, false));

            ret.OffsetAlongMm = 0;             // basi perfettamente allineate
            // Alla sinistra della direzione: con la base verso +X il secondo collettore sta
            // in +Y (nord) — è la coppia di prima ruotata di 180° attorno alla verticale.
            ret.OffsetSideMm = ReturnOffsetMm;
            return ret;
        }

        /// <summary>Riepilogo compatto mostrato sopra l'anteprima.</summary>
        public string Summary()
        {
            var circuits = ValidCircuits();
            if (circuits.Count == 0) return "Collettore senza circuiti.";

            var sb = new StringBuilder();
            sb.Append("Collettore DN").Append(MepSize.Fmt(EffectiveHeaderDnMm));
            sb.Append(HeaderDnMm.HasValue && HeaderDnMm.Value > 0 ? " (impostato)" : " (automatico)");
            sb.Append(", lunghezza ").Append(MepSize.Fmt(HeaderLengthMm)).Append(" mm");
            sb.Append(", ").Append(circuits.Count).Append(circuits.Count == 1 ? " circuito" : " circuiti");
            sb.Append(", interasse ").Append(MepSize.Fmt(SpacingMm)).Append(AutoSpacing ? " mm (automatico)." : " mm.");
            sb.AppendLine();
            if (WithReturn)
                sb.Append("Ritorno: base allineata a ").Append(MepSize.Fmt(ReturnOffsetMm))
                  .Append(" mm, stacchi spostati di ").Append(MepSize.Fmt(SpacingMm / 2.0)).Append(" mm.").AppendLine();
            sb.Append("Tipo tubazione: ")
              .Append(string.IsNullOrWhiteSpace(PipeTypeName) ? "predefinito del progetto" : "\"" + PipeTypeName.Trim() + "\"")
              .AppendLine();

            var positions = CircuitPositionsMm();
            for (var i = 0; i < circuits.Count; i++)
            {
                sb.Append("  ").Append(CircuitLabel(circuits[i], i));
                sb.Append(": DN").Append(MepSize.Fmt(circuits[i].DnMm));
                if (circuits[i].KindInfo.HasBypass)
                    sb.Append(" prima del bypass → DN").Append(MepSize.Fmt(circuits[i].EffectiveDnAfterBypassMm)).Append(" dopo il bypass");
                sb.Append(" ").Append(circuits[i].KindInfo.Label.ToLowerInvariant());
                sb.Append(" a ").Append(MepSize.Fmt(positions[i])).Append(" mm dall'inizio");
                var valve = ValveFor(circuits[i]);
                if (valve != null)
                {
                    sb.Append(" · ").Append(valve.KindLabel);
                    if (!string.IsNullOrWhiteSpace(valve.TypeName)) sb.Append(" \"").Append(valve.TypeName).Append("\"");
                }
                var after = PipeAfterValveFor(circuits[i]);
                if (HasChain(circuits[i]))
                    sb.Append(" · catena mix 2 vie (mandata ").Append(ChainFor(circuits[i], true).Count(x => x.Kind != StubItemKind.Gap))
                      .Append(" pezzi, ritorno ").Append(ChainFor(circuits[i], false).Count(x => x.Kind != StubItemKind.Gap)).Append(" pezzi")
                      .Append(circuits[i].EffectiveWithZoneValve ? ", con valvola di zona)" : ", senza valvola di zona)")
                      .Append(", tubo finale ").Append(MepSize.Fmt(after ?? 0)).Append(" mm");
                else if (circuits[i].EffectiveWithZoneValve)
                    sb.Append(" · valvola di zona richiesta");
                else if (after.HasValue && after.Value > 0) sb.Append(" · tubo dopo la valvola ").Append(MepSize.Fmt(after.Value)).Append(" mm");
                else if (after.HasValue) sb.Append(" · si ferma alla flangia");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ------------------------------------------------------- serializzazione

        /// <summary>
        /// Circuiti in forma "20:direct;16:mix3;25:nopump" per le impostazioni: DN e codice della
        /// tipologia (<see cref="CircuitKinds.Code"/>). Il DN nudo ("20") è ancora accettato in
        /// lettura e vale come diretto, così i file salvati prima delle tipologie restano validi.
        /// </summary>
        public string CircuitsToString()
        {
            return string.Join(";", Circuits.Where(c => c != null && c.IsValid).Select(CircuitToString));
        }

        /// <summary>
        /// "25:mix2|out=32|ev=ev025r2+bac_(1)": DN e tipologia, poi solo i campi che si discostano
        /// dal predefinito (DN dopo il bypass, famiglia della energy valve scelta a mano e, per la
        /// famiglia unica, il tipo "evt=EV025R2+BAC").
        /// </summary>
        public static string CircuitToString(ManifoldCircuit c)
        {
            var s = c.DnMm.ToString("0.##", CultureInfo.InvariantCulture) + ":" + CircuitKinds.Code(c.Kind);
            if (c.DnAfterBypassMm > 0 && Math.Abs(c.DnAfterBypassMm - c.DnMm) > 0.001)
                s += "|out=" + c.DnAfterBypassMm.ToString("0.##", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(c.EnergyValveFamily))
            {
                s += "|ev=" + c.EnergyValveFamily.Trim().Replace(";", string.Empty).Replace("|", string.Empty);
                if (!string.IsNullOrWhiteSpace(c.EnergyValveType))
                    s += "|evt=" + c.EnergyValveType.Trim().Replace(";", string.Empty).Replace("|", string.Empty);
            }
            if (c.KindInfo.HasPump && !string.IsNullOrWhiteSpace(c.PumpType))
                s += "|pump=" + c.PumpType.Trim().Replace(";", string.Empty).Replace("|", string.Empty);
            // il cieco non ha la valvola di zona: la scelta non si salva
            if (!c.KindInfo.IsBlind && c.WithZoneValve.HasValue && c.WithZoneValve.Value != c.KindInfo.IsChainModelled)
                s += "|zv=" + (c.WithZoneValve.Value ? "1" : "0");
            return s;
        }

        public void LoadCircuitsFromString(string value)
        {
            Circuits.Clear();
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (var part in value.Split(';'))
            {
                var circuit = ParseCircuit(part);
                if (circuit != null) Circuits.Add(circuit);
            }
        }

        /// <summary>
        /// "20:mix3" → DN20 miscelato a 3 vie; "20" → DN20 diretto; "25:mix2|out=32|ev=…" con DN dopo
        /// il bypass e famiglia della energy valve; null se il DN non è valido.
        /// </summary>
        public static ManifoldCircuit ParseCircuit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var fields = text.Trim().Split('|');
            var t = fields[0].Trim();
            var kindText = string.Empty;
            var colon = t.IndexOf(':');
            if (colon >= 0)
            {
                kindText = t.Substring(colon + 1);
                t = t.Substring(0, colon);
            }
            double dn;
            if (!double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dn) || dn <= 0) return null;
            CircuitKind kind;
            if (!CircuitKinds.TryParse(kindText, out kind)) kind = CircuitKinds.Default;
            var circuit = new ManifoldCircuit(dn, kind);
            for (var i = 1; i < fields.Length; i++)
            {
                var eq = fields[i].IndexOf('=');
                if (eq <= 0) continue;
                var key = fields[i].Substring(0, eq).Trim().ToLowerInvariant();
                var value = fields[i].Substring(eq + 1).Trim();
                double n;
                if (key == "out" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && n > 0)
                    circuit.DnAfterBypassMm = n;
                else if (key == "ev" && value.Length > 0)
                    circuit.EnergyValveFamily = value;
                else if (key == "evt" && value.Length > 0)
                    circuit.EnergyValveType = value;
                else if (key == "pump" && value.Length > 0)
                    circuit.PumpType = value;
                else if (key == "zv")
                    circuit.WithZoneValve = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            return circuit;
        }
    }
}
