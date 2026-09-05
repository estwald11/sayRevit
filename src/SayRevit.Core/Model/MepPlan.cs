using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SayRevit.Core.Model
{
    /// <summary>Categoria di elemento MEP supportata.</summary>
    public enum MepKind
    {
        Pipe,
        Duct
    }

    /// <summary>Forma della sezione.</summary>
    public enum SizeShape
    {
        Round,
        Rectangular
    }

    /// <summary>Direzione di un tratto o di uno stacco.</summary>
    public enum DirectionKind
    {
        /// <summary>Non specificata: +X per i tratti principali, "Side" o "Up" per gli stacchi.</summary>
        Default,
        PlusX,
        MinusX,
        PlusY,
        MinusY,
        Up,
        Down,
        /// <summary>Perpendicolare a sinistra rispetto al tratto principale (solo stacchi).</summary>
        Left,
        /// <summary>Perpendicolare a destra rispetto al tratto principale (solo stacchi).</summary>
        Right,
        /// <summary>Stacchi alternati sinistra/destra (solo stacchi).</summary>
        Alternate
    }

    /// <summary>Dimensione nominale di un elemento.</summary>
    public sealed class MepSize
    {
        public SizeShape Shape { get; set; } = SizeShape.Round;

        /// <summary>Diametro nominale in millimetri (per DN si usa il valore numerico del DN).</summary>
        public double DiameterMm { get; set; }

        public double WidthMm { get; set; }
        public double HeightMm { get; set; }

        /// <summary>True se l'utente ha scritto "DN...".</summary>
        public bool IsNominalDn { get; set; }

        public static MepSize Round(double diameterMm, bool isDn = false)
        {
            return new MepSize { Shape = SizeShape.Round, DiameterMm = diameterMm, IsNominalDn = isDn };
        }

        public static MepSize Rectangular(double widthMm, double heightMm)
        {
            return new MepSize { Shape = SizeShape.Rectangular, WidthMm = widthMm, HeightMm = heightMm };
        }

        public MepSize Clone()
        {
            return (MepSize)MemberwiseClone();
        }

        public override string ToString()
        {
            if (Shape == SizeShape.Rectangular)
                return Fmt(WidthMm) + "x" + Fmt(HeightMm) + " mm";
            return IsNominalDn ? "DN" + Fmt(DiameterMm) : "Ø" + Fmt(DiameterMm) + " mm";
        }

        public static string Fmt(double v)
        {
            return v.ToString(Math.Abs(v - Math.Round(v)) < 1e-6 ? "0" : "0.#", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Valvola da inserire lungo uno stacco, in linea sul tubo.</summary>
    public sealed class MepValve
    {
        public ValveKind Kind { get; set; }

        /// <summary>Nome esatto della famiglia caricata nel progetto.</summary>
        public string FamilyName { get; set; }

        /// <summary>Nome del tipo scelto sul DN; null = da risolvere sul documento al momento della creazione.</summary>
        public string TypeName { get; set; }

        /// <summary>DN del tubo su cui va la valvola (mm).</summary>
        public double DnMm { get; set; }

        /// <summary>PN richiesto (bar); 0 = indifferente.</summary>
        public double PnBar { get; set; }

        /// <summary>Distanza dall'inizio dello stacco (asse del collettore) al centro della valvola (mm).</summary>
        public double DistanceMm { get; set; } = 150;

        /// <summary>True se la valvola va montata tra due flange (boax): la famiglia dipende dal materiale.</summary>
        public bool WithFlanges { get; set; }

        /// <summary>Rotazione della valvola attorno all'asse del tubo (gradi); la boax va a 90°.</summary>
        public double RollDegrees { get; set; }

        /// <summary>
        /// Parola che, a parità di DN, fa preferire un tipo della famiglia (es. "ductile" per la
        /// valvola di zona Watts, che ha un tipo in ghisa grigia e uno in ghisa sferoidale per DN).
        /// </summary>
        public string PreferredTypeWord { get; set; }

        /// <summary>
        /// True = pezzo montato col verso invertito (girato di 180° attorno alla normale al tubo,
        /// scambiando entrata e uscita): serve al filtro a Y, che dalla famiglia esce capovolto.
        /// </summary>
        public bool Reversed { get; set; }

        public string KindLabel
        {
            get { return KindLabelOf(Kind); }
        }

        public static string KindLabelOf(ValveKind kind)
        {
            switch (kind)
            {
                case ValveKind.Ball: return "valvola a sfera";
                case ValveKind.EnergyValve: return "valvola 2 vie (energy valve)";
                case ValveKind.ZoneValve: return "valvola di zona";
                case ValveKind.Strainer: return "filtro a Y";
                case ValveKind.CheckValve: return "valvola di ritegno";
                case ValveKind.Pump: return "pompa";
                default: return "valvola boax";
            }
        }

        public override string ToString()
        {
            var s = KindLabel;
            s += string.IsNullOrWhiteSpace(TypeName)
                ? " (tipo scelto in Revit sulla misura)"
                : " \"" + TypeName + "\"";
            return s + " a " + MepSize.Fmt(DistanceMm) + " mm dal collettore";
        }
    }

    /// <summary>Cosa c'è in un punto della catena di uno stacco.</summary>
    public enum StubItemKind
    {
        /// <summary>Pezzo in linea (valvola, filtro…), con o senza flange.</summary>
        Piece,
        /// <summary>Tratto di tubo libero di lunghezza data (anche uno spazio riservato, es. alla pompa).</summary>
        Gap,
        /// <summary>Raccordo a T da cui parte (o in cui arriva) il bypass.</summary>
        Tee
    }

    /// <summary>
    /// Un elemento della catena montata su uno stacco, dall'asse del collettore verso l'utenza.
    /// Le posizioni non sono fissate qui: ogni pezzo parte dalla faccia d'uscita del precedente
    /// (più il tubo libero che lo separa), perché le lunghezze reali dei pezzi si conoscono solo
    /// in Revit; fa eccezione il primo pezzo, centrato a <see cref="CenterMm"/> dall'asse.
    /// </summary>
    public sealed class StubItem
    {
        public StubItemKind Kind { get; set; }

        /// <summary>Pezzo da montare (solo <see cref="StubItemKind.Piece"/>).</summary>
        public MepValve Piece { get; set; }

        /// <summary>Lunghezza del tubo libero (solo <see cref="StubItemKind.Gap"/>), mm.</summary>
        public double LengthMm { get; set; }

        /// <summary>
        /// Solo per il primo pezzo: distanza dall'asse del collettore al CENTRO del pezzo (mm),
        /// come per la valvola di intercettazione. Null = a partire dalla faccia precedente.
        /// </summary>
        public double? CenterMm { get; set; }

        /// <summary>Nome leggibile (es. "spazio riservato alla pompa"), per anteprima e diagnostica.</summary>
        public string Label { get; set; }

        /// <summary>
        /// Solo per il T: DN dello stacco DOPO il T (mm), verso l'utenza; 0 = invariato. Il T è
        /// ridotto e da lì in poi tubi e pezzi hanno questa misura.
        /// </summary>
        public double SizeAfterMm { get; set; }

        /// <summary>
        /// Solo per i pezzi: i pezzi con la stessa chiave nelle catene gemelle (mandata/ritorno di
        /// uno stesso circuito, <see cref="MepBranch.PairKey"/>) vengono montati alla stessa quota:
        /// la catena più corta riceve tubo in più davanti al pezzo. Null = nessun allineamento.
        /// </summary>
        public string AlignKey { get; set; }

        public static StubItem Gap(double lengthMm, string label = null)
        {
            return new StubItem { Kind = StubItemKind.Gap, LengthMm = lengthMm, Label = label ?? "tubo" };
        }

        public static StubItem Of(MepValve piece, double? centerMm = null)
        {
            return new StubItem { Kind = StubItemKind.Piece, Piece = piece, CenterMm = centerMm, Label = piece == null ? "pezzo" : piece.KindLabel };
        }

        public static StubItem TeeFor(string label, double sizeAfterMm = 0)
        {
            return new StubItem { Kind = StubItemKind.Tee, Label = label ?? "T del bypass", SizeAfterMm = sizeAfterMm };
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case StubItemKind.Piece: return Piece == null ? "pezzo" : Piece.KindLabel + (string.IsNullOrWhiteSpace(Piece.TypeName) ? "" : " \"" + Piece.TypeName + "\"");
                case StubItemKind.Gap: return Label + " " + MepSize.Fmt(LengthMm) + " mm";
                default: return Label;
            }
        }
    }

    /// <summary>
    /// Bypass tra due stacchi gemelli (mandata e ritorno dello stesso circuito). Dal T di ogni
    /// stacco parte un tubo orizzontale ortogonale: dalla mandata di traverso (verso la base del
    /// ritorno, per tutta la distanza tra le basi), dal ritorno lungo la base (mezzo interasse,
    /// indietro verso la mandata). I due tubi finiscono nello stesso punto in pianta, a quote
    /// diverse, e sono uniti da un tratto lungo la direzione degli stacchi con due gomiti, sul
    /// quale sta l'eventuale valvola di ritegno.
    /// </summary>
    public sealed class MepBypass
    {
        /// <summary>Stessa chiave sui due stacchi del circuito (es. "C3").</summary>
        public string Key { get; set; }

        /// <summary>Posizione dello stacco gemello rispetto a questo: lungo il tratto principale (mm).</summary>
        public double PartnerAlongMm { get; set; }

        /// <summary>Posizione dello stacco gemello rispetto a questo: a sinistra della direzione del tratto (mm).</summary>
        public double PartnerSideMm { get; set; }

        /// <summary>Tubo orizzontale che parte dal T di QUESTO stacco: componente lungo il tratto (mm). Uno solo dei due è diverso da zero.</summary>
        public double LegAlongMm { get; set; }

        /// <summary>Tubo orizzontale che parte dal T di QUESTO stacco: componente a sinistra della direzione del tratto (mm).</summary>
        public double LegSideMm { get; set; }

        /// <summary>Lunghezza del tubo orizzontale che parte da questo T (mm).</summary>
        public double LegLengthMm
        {
            get { return Math.Sqrt(LegAlongMm * LegAlongMm + LegSideMm * LegSideMm); }
        }

        /// <summary>Pezzo sul tratto di raccordo tra i due tubi orizzontali (valvola di ritegno); null = niente.</summary>
        public MepValve InlinePiece { get; set; }

        /// <summary>DN dei tubi del bypass (mm): quello del circuito dopo il bypass. 0 = come lo stacco.</summary>
        public double DnMm { get; set; }

        /// <summary>Distanza in pianta tra i due stacchi (mm).</summary>
        public double PlanDistanceMm
        {
            get { return Math.Sqrt(PartnerAlongMm * PartnerAlongMm + PartnerSideMm * PartnerSideMm); }
        }
    }

    /// <summary>Uno stacco (derivazione) da un tratto principale.</summary>
    public sealed class MepBranch
    {
        /// <summary>Valvola in linea sullo stacco; null = nessuna valvola.</summary>
        public MepValve Valve { get; set; }

        /// <summary>
        /// Catena completa dei pezzi sullo stacco, quando non c'è solo la valvola: intercettazione
        /// (primo elemento, centrato alla sua distanza), tubi liberi, T del bypass, altri pezzi.
        /// Vuota = solo <see cref="Valve"/>. Con la catena, <see cref="LengthAfterValveMm"/> è il
        /// tubo dopo l'ULTIMO pezzo della catena.
        /// </summary>
        public List<StubItem> Chain { get; } = new List<StubItem>();

        /// <summary>Bypass verso lo stacco gemello, se la catena ha un T; null = nessuno.</summary>
        public MepBypass Bypass { get; set; }

        /// <summary>
        /// Chiave comune agli stacchi gemelli di uno stesso circuito (mandata e ritorno): i pezzi
        /// con lo stesso <see cref="StubItem.AlignKey"/> nelle due catene vengono portati alla
        /// stessa quota dal costruttore. Null = nessun gemello.
        /// </summary>
        public string PairKey { get; set; }

        public MepSize Size { get; set; }
        public int Count { get; set; } = 1;
        public double LengthMm { get; set; } = 500;
        public DirectionKind Direction { get; set; } = DirectionKind.Default;

        /// <summary>
        /// Lunghezza del tubo DOPO la valvola (mm), dalla faccia d'uscita dell'ultimo pezzo montato
        /// (seconda flangia della boax, uscita della sfera) alla fine dello stacco. Se valorizzata,
        /// <see cref="LengthMm"/> è solo provvisoria: la fine dello stacco viene fissata in Revit
        /// quando l'ingombro reale della valvola è noto. Zero = lo stacco si ferma alla flangia,
        /// nessun tubo a valle (stacco cieco).
        /// </summary>
        public double? LengthAfterValveMm { get; set; }

        /// <summary>
        /// False = niente raccordo: il tratto principale non viene spezzato e lo stacco parte
        /// dall'asse del tubo, semplicemente sovrapposto (usato dal collettore, dove il T
        /// instraderebbe la derivazione con un raccordo della misura dello stacco).
        /// </summary>
        public bool Connect { get; set; } = true;

        /// <summary>Interasse tra gli stacchi (mm); se null si distribuiscono uniformemente.</summary>
        public double? SpacingMm { get; set; }

        /// <summary>Posizioni esplicite lungo il tratto (mm dall'inizio). Se valorizzate hanno la precedenza.</summary>
        public List<double> PositionsMm { get; } = new List<double>();
    }

    /// <summary>Un tratto rettilineo di tubazione o canale con i suoi stacchi.</summary>
    public sealed class MepRun
    {
        public MepKind Kind { get; set; } = MepKind.Pipe;

        /// <summary>True se la categoria è stata scritta esplicitamente (tubazione/canale) e non dedotta.</summary>
        public bool KindExplicit { get; set; }

        public MepSize Size { get; set; }
        public double LengthMm { get; set; } = 3000;
        public DirectionKind Direction { get; set; } = DirectionKind.Default;

        /// <summary>Nome esatto del tipo Revit richiesto (es. tipo "Acciaio zincato").</summary>
        public string ExplicitTypeName { get; set; }

        /// <summary>Parole chiave su materiale/tipo (es. "acciaio", "pvc").</summary>
        public List<string> TypeHints { get; } = new List<string>();

        /// <summary>Classificazione di sistema (chiave di <see cref="SystemClass"/>).</summary>
        public string SystemClass { get; set; }

        /// <summary>Frase originale sul sistema (es. "acqua fredda").</summary>
        public string SystemPhrase { get; set; }

        public string LevelHint { get; set; }

        /// <summary>Quota rispetto al livello (mm). Null = predefinita.</summary>
        public double? ElevationMm { get; set; }

        /// <summary>True se il tratto è la prosecuzione del precedente (collegato con gomito/transizione).</summary>
        public bool ContinuesPrevious { get; set; }

        /// <summary>
        /// Scostamento del punto di partenza rispetto al PRIMO tratto, lungo la direzione del tratto (mm).
        /// Con <see cref="OffsetSideMm"/> valorizzato disattiva la disposizione automatica dei tratti separati.
        /// </summary>
        public double? OffsetAlongMm { get; set; }

        /// <summary>Scostamento laterale rispetto al primo tratto (mm, positivo = sinistra della direzione).</summary>
        public double? OffsetSideMm { get; set; }

        /// <summary>
        /// True = alle estremità libere del tratto vengono posizionati dei fondelli (Enddeckel),
        /// se per il materiale del tipo è definita una famiglia (usato dal collettore).
        /// </summary>
        public bool CapEnds { get; set; }

        public List<MepBranch> Branches { get; } = new List<MepBranch>();
    }

    /// <summary>Chiavi di classificazione di sistema, indipendenti da Revit.</summary>
    public static class SystemClass
    {
        public const string DomesticColdWater = "DomesticColdWater";
        public const string DomesticHotWater = "DomesticHotWater";
        public const string SupplyHydronic = "SupplyHydronic";
        public const string ReturnHydronic = "ReturnHydronic";
        public const string Sanitary = "Sanitary";
        public const string Vent = "Vent";
        public const string FireProtectWet = "FireProtectWet";
        public const string OtherPipe = "OtherPipe";
        public const string SupplyAir = "SupplyAir";
        public const string ReturnAir = "ReturnAir";
        public const string ExhaustAir = "ExhaustAir";
        public const string OtherDuct = "OtherDuct";

        public static readonly string[] PipeClasses =
        {
            DomesticColdWater, DomesticHotWater, SupplyHydronic, ReturnHydronic, Sanitary, Vent, FireProtectWet, OtherPipe
        };

        public static readonly string[] DuctClasses = { SupplyAir, ReturnAir, ExhaustAir, OtherDuct };

        public static bool IsDuctClass(string cls)
        {
            return cls == SupplyAir || cls == ReturnAir || cls == ExhaustAir || cls == OtherDuct;
        }
    }

    /// <summary>Piano completo da costruire.</summary>
    public sealed class MepPlan
    {
        public string SourceText { get; set; }
        public List<MepRun> Runs { get; } = new List<MepRun>();
    }

    /// <summary>Esito del parsing.</summary>
    public sealed class ParseResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public MepPlan Plan { get; set; }
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();

        public static ParseResult Fail(string error)
        {
            return new ParseResult { Success = false, Error = error };
        }
    }
}
