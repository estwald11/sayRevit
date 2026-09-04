using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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

        /// <summary>Diametro nominale del circuito in millimetri (0 = riga ancora vuota).</summary>
        public double DnMm { get; set; }

        /// <summary>Etichetta facoltativa del circuito (es. "bagno"); se vuota si usa "C1", "C2"…</summary>
        public string Name { get; set; }

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
            if (_autoSpacing == null) SpacingFloorMm = SpacingMm;
            var r = ManifoldSpacing.Minimal(footprints, SpacingFloorMm, WithReturn, ReturnOffsetMm, headerRadiusMm, SpacingClearanceMm, 10);
            _autoSpacing = r;
            SpacingMm = r.SpacingMm;
            return r;
        }

        /// <summary>DN dei circuiti validi, nell'ordine lungo la base.</summary>
        public List<double> CircuitDnsMm()
        {
            return ValidCircuits().Select(c => c.DnMm).ToList();
        }

        /// <summary>Lunghezza di ogni circuito a partire dal collettore (mm).</summary>
        public double CircuitLengthMm { get; set; } = 500;

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

        /// <summary>Nome esatto della famiglia di valvole a sfera caricata nel progetto; vuoto = nessuna.</summary>
        public string BallValveFamily { get; set; }

        /// <summary>Nome esatto della famiglia di valvole boax caricata nel progetto; vuoto = nessuna.</summary>
        public string ButterflyValveFamily { get; set; }

        /// <summary>Nomi dei tipi delle due famiglie: servono a scegliere il tipo sul DN già in anteprima.</summary>
        public List<string> BallValveTypes { get; } = new List<string>();

        public List<string> ButterflyValveTypes { get; } = new List<string>();

        /// <summary>PN preferito quando i nomi dei tipi lo dichiarano (0 = indifferente).</summary>
        public double ValvePnBar { get; set; } = 16;

        /// <summary>Distanza dall'asse del collettore al centro della valvola, lungo lo stacco (mm).</summary>
        public double ValveDistanceMm { get; set; } = 150;

        /// <summary>
        /// Rotazione della boax attorno all'asse del tubo (gradi). A 0° lo Z della famiglia (la
        /// leva, nella boax) guarda lungo il collettore; a 90° guarda di traverso.
        /// </summary>
        public double ButterflyRollDegrees { get; set; } = 90;

        /// <summary>
        /// Valvola prevista per un circuito di questo DN: sotto la soglia la sfera, sopra la boax.
        /// Null se le valvole sono disattivate o la famiglia corrispondente non è stata scelta.
        /// Il tipo viene deciso qui, così l'anteprima mostra esattamente quello che verrà inserito.
        /// </summary>
        public MepValve ValveFor(double dnMm)
        {
            if (!WithValves || dnMm <= 0) return null;

            var ball = dnMm <= BallValveMaxDnMm + 0.001;
            var family = ball ? BallValveFamily : ButterflyValveFamily;
            if (string.IsNullOrWhiteSpace(family)) return null;

            var pick = ValveTypeMatcher.Pick(ball ? BallValveTypes : ButterflyValveTypes, dnMm, ValvePnBar);
            return new MepValve
            {
                Kind = ball ? ValveKind.Ball : ValveKind.Butterfly,
                FamilyName = family.Trim(),
                TypeName = pick == null ? null : pick.TypeName,
                DnMm = dnMm,
                PnBar = ValvePnBar,
                DistanceMm = ValveDistanceMm,
                // la boax si monta tra due flange e ruotata sull'asse, la valvola a sfera no
                WithFlanges = !ball,
                RollDegrees = ball ? 0 : ButterflyRollDegrees
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

            var headerDn = EffectiveHeaderDnMm;
            if (headerDn <= 0) return ParseResult.Fail("DN del collettore non valido.");
            if (WithReturn && ReturnOffsetMm <= 0)
                return ParseResult.Fail("La distanza tra mandata e ritorno deve essere maggiore di zero.");

            var run = MakeHeaderRun(headerDn);
            var positions = CircuitPositionsMm();
            // Sfasamento: il PRIMO collettore porta gli stacchi non sfasati,
            // il secondo quelli sfasati di mezzo interasse.
            for (var i = 0; i < circuits.Count; i++)
                run.Branches.Add(MakeCircuitBranch(circuits[i].DnMm, i, positions[i]));

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

            return result;
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

            var dns = circuits.Select(c => c.DnMm).Distinct().OrderBy(d => d).ToList();
            var hasBall = dns.Any(d => d <= BallValveMaxDnMm + 0.001);
            var hasButterfly = dns.Any(d => d > BallValveMaxDnMm + 0.001);

            result.Notes.Add("Valvole in linea su ogni stacco: a sfera fino a DN" + MepSize.Fmt(BallValveMaxDnMm) +
                             " compreso, boax oltre; centro a " + MepSize.Fmt(ValveDistanceMm) +
                             " mm dall'asse del collettore.");
            if (hasButterfly && !string.IsNullOrWhiteSpace(ButterflyValveFamily))
            {
                result.Notes.Add("Flange (Flansch) prima e dopo ogni valvola boax: automatiche per inox e acciaio nero, " +
                                 "come i fondelli; per gli altri materiali la valvola resta senza flange.");
                if (Math.Abs(ButterflyRollDegrees) > 0.001)
                    result.Notes.Add("Valvole boax girate di " + MepSize.Fmt(ButterflyRollDegrees) +
                                     "° attorno all'asse del tubo (flange comprese).");
            }

            if (hasBall && string.IsNullOrWhiteSpace(BallValveFamily))
                result.Warnings.Add("Nessuna famiglia scelta per la valvola a sfera: i circuiti fino a DN" +
                                    MepSize.Fmt(BallValveMaxDnMm) + " restano senza valvola.");
            if (hasButterfly && string.IsNullOrWhiteSpace(ButterflyValveFamily))
                result.Warnings.Add("Nessuna famiglia scelta per la valvola boax: i circuiti oltre DN" +
                                    MepSize.Fmt(BallValveMaxDnMm) + " restano senza valvola.");

            foreach (var dn in dns)
            {
                var ball = dn <= BallValveMaxDnMm + 0.001;
                var family = ball ? BallValveFamily : ButterflyValveFamily;
                if (string.IsNullOrWhiteSpace(family)) continue;

                var kind = ball ? "valvola a sfera" : "valvola boax";
                var pick = ValveTypeMatcher.Pick(ball ? BallValveTypes : ButterflyValveTypes, dn, ValvePnBar);
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

            if (ValveDistanceMm <= headerDn / 2.0)
                result.Warnings.Add("Valvole a " + MepSize.Fmt(ValveDistanceMm) + " mm dall'asse: cadono dentro il collettore (DN" +
                                    MepSize.Fmt(headerDn) + "). Aumenta la distanza ad almeno " +
                                    MepSize.Fmt(headerDn / 2.0 + 50) + " mm.");
            if (ValveDistanceMm >= CircuitLengthMm)
                result.Warnings.Add("Valvole a " + MepSize.Fmt(ValveDistanceMm) + " mm dall'asse: oltre la lunghezza dei circuiti (" +
                                    MepSize.Fmt(CircuitLengthMm) + " mm). Non verranno inserite.");
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

        private MepBranch MakeCircuitBranch(double dnMm, int index, double positionMm)
        {
            var branch = new MepBranch
            {
                Size = MepSize.Round(dnMm, true),
                Count = 1,
                LengthMm = CircuitLengthMm,
                // I circuiti non vengono raccordati: solo sovrapposti al collettore, che resta
                // un tubo unico (il T di Revit ridimensionerebbe l'innesto alla misura del circuito).
                Connect = false,
                // Un circuito = un gruppo di stacchi con una sola posizione, quindi l'alternanza
                // non può essere risolta dall'indice a valle: la fissiamo qui.
                Direction = CircuitDirection == DirectionKind.Alternate
                    ? (index % 2 == 0 ? DirectionKind.Left : DirectionKind.Right)
                    : CircuitDirection,
                Valve = ValveFor(dnMm)
            };
            branch.PositionsMm.Add(positionMm);
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
            for (var i = 0; i < circuits.Count; i++)
                ret.Branches.Add(MakeCircuitBranch(circuits[i].DnMm, i, supplyPositions[i] + shift));

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
                sb.Append(" a ").Append(MepSize.Fmt(positions[i])).Append(" mm dall'inizio");
                var valve = ValveFor(circuits[i].DnMm);
                if (valve != null)
                {
                    sb.Append(" · ").Append(valve.KindLabel);
                    if (!string.IsNullOrWhiteSpace(valve.TypeName)) sb.Append(" \"").Append(valve.TypeName).Append("\"");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ------------------------------------------------------- serializzazione

        /// <summary>DN dei circuiti in forma "20;16;16" per le impostazioni.</summary>
        public string CircuitsToString()
        {
            return string.Join(";", Circuits.Where(c => c != null && c.IsValid)
                .Select(c => c.DnMm.ToString("0.##", CultureInfo.InvariantCulture)));
        }

        public void LoadCircuitsFromString(string value)
        {
            Circuits.Clear();
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (var part in value.Split(';'))
            {
                double dn;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dn) && dn > 0)
                    Circuits.Add(new ManifoldCircuit(dn));
            }
        }
    }
}
