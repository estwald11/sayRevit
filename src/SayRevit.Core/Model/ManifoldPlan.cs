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
        /// il bordo dell'ultimo; il bordo è posizione ± DN/2.
        /// </summary>
        public double HeaderLengthMm
        {
            get
            {
                var circuits = ValidCircuits();
                if (circuits.Count == 0) return 0;
                var first = circuits[0].DnMm / 2.0;
                var last = circuits[circuits.Count - 1].DnMm / 2.0;
                return OverhangMm + first + (circuits.Count - 1) * SpacingMm + last + OverhangMm;
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
            for (var i = 0; i < circuits.Count; i++)
                run.Branches.Add(MakeCircuitBranch(circuits[i].DnMm, i, positions[i]));

            var plan = new MepPlan { SourceText = Summary() };
            plan.Runs.Add(run);
            if (WithReturn) plan.Runs.Add(MakeReturnRun(headerDn, circuits));

            var result = new ParseResult { Success = true, Plan = plan };
            result.Notes.Add("I circuiti non vengono raccordati: partono dall'asse del collettore, sovrapposti, senza T.");
            if (WithReturn)
                result.Notes.Add("Collettore di ritorno: clone speculare (circuiti in ordine inverso) a " +
                                 MepSize.Fmt(ReturnOffsetMm) + " mm dalla mandata, sfasato di mezzo interasse: " +
                                 "ogni circuito di ritorno cade a metà tra due di mandata.");
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
            result.Notes.Add("Base lunga " + MepSize.Fmt(HeaderLengthMm) + " mm: sporge di " + MepSize.Fmt(OverhangMm) +
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

            return result;
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
                ExplicitTypeName = string.IsNullOrWhiteSpace(PipeTypeName) ? null : PipeTypeName.Trim()
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
                    : CircuitDirection
            };
            branch.PositionsMm.Add(positionMm);
            return branch;
        }

        /// <summary>
        /// Collettore di ritorno: clone speculare (circuiti in ordine inverso, stessa base) su un asse
        /// parallelo a <see cref="ReturnOffsetMm"/>, sfasato lungo l'asse in modo che ogni circuito di
        /// ritorno cada a metà strada tra due circuiti di mandata (clone perfetto ma chirale).
        /// La lunghezza della base non cambia: le sporgenze si scambiano semplicemente di estremità.
        /// </summary>
        private MepRun MakeReturnRun(double headerDn, List<ManifoldCircuit> circuits)
        {
            var ret = MakeHeaderRun(headerDn);

            var reversed = new List<ManifoldCircuit>(circuits);
            reversed.Reverse();

            // Posizioni locali del clone speculare: stessa regola della mandata, su lista invertita.
            var start = OverhangMm + reversed[0].DnMm / 2.0;
            for (var j = 0; j < reversed.Count; j++)
                ret.Branches.Add(MakeCircuitBranch(reversed[j].DnMm, j, start + j * SpacingMm));

            // Sfasamento lungo l'asse: il primo circuito di ritorno deve cadere a metà tra
            // il primo e il secondo di mandata → (dn₁ - dnₙ)/2 + interasse/2.
            var first = circuits[0].DnMm;
            var last = circuits[circuits.Count - 1].DnMm;
            ret.OffsetAlongMm = (first - last) / 2.0 + SpacingMm / 2.0;
            ret.OffsetSideMm = -ReturnOffsetMm; // alla destra della direzione della mandata
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
            sb.Append(", interasse ").Append(MepSize.Fmt(SpacingMm)).Append(" mm.");
            sb.AppendLine();
            if (WithReturn)
                sb.Append("Ritorno: clone speculare a ").Append(MepSize.Fmt(ReturnOffsetMm))
                  .Append(" mm, sfasato di ").Append(MepSize.Fmt(SpacingMm / 2.0)).Append(" mm.").AppendLine();
            sb.Append("Tipo tubazione: ")
              .Append(string.IsNullOrWhiteSpace(PipeTypeName) ? "predefinito del progetto" : "\"" + PipeTypeName.Trim() + "\"")
              .AppendLine();

            var positions = CircuitPositionsMm();
            for (var i = 0; i < circuits.Count; i++)
            {
                sb.Append("  ").Append(CircuitLabel(circuits[i], i));
                sb.Append(": DN").Append(MepSize.Fmt(circuits[i].DnMm));
                sb.Append(" a ").Append(MepSize.Fmt(positions[i])).Append(" mm dall'inizio");
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
