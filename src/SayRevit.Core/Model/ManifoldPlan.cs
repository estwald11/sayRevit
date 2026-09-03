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

        /// <summary>Interasse tra due circuiti consecutivi (mm).</summary>
        public double SpacingMm { get; set; } = 150;

        /// <summary>Lunghezza di ogni circuito a partire dal collettore (mm).</summary>
        public double CircuitLengthMm { get; set; } = 500;

        /// <summary>Tratto di collettore oltre il primo e l'ultimo circuito (mm); null = metà interasse.</summary>
        public double? EndMarginMm { get; set; }

        public DirectionKind HeaderDirection { get; set; } = DirectionKind.PlusX;

        public DirectionKind CircuitDirection { get; set; } = DirectionKind.Down;

        /// <summary>Nome esatto del tipo di tubazione scelto nel progetto; null = tipo predefinito.</summary>
        public string PipeTypeName { get; set; }

        /// <summary>Circuiti con un DN valido, gli unici che vengono modellati.</summary>
        public List<ManifoldCircuit> ValidCircuits()
        {
            return Circuits.Where(c => c != null && c.IsValid).ToList();
        }

        /// <summary>Margine effettivo alle estremità: mai sotto 10 mm, altrimenti lo stacco cadrebbe sul bordo.</summary>
        public double EffectiveEndMarginMm
        {
            get { return Math.Max(EndMarginMm ?? SpacingMm / 2.0, 10); }
        }

        /// <summary>
        /// DN del collettore calcolato per equivalenza di area (√Σdn²) e arrotondato al DN commerciale
        /// superiore: il collettore non risulta mai più piccolo della somma delle sezioni derivate.
        /// </summary>
        public double AutoHeaderDnMm
        {
            get
            {
                var circuits = ValidCircuits();
                if (circuits.Count == 0) return 0;
                return SnapUpToDn(Math.Sqrt(circuits.Sum(c => c.DnMm * c.DnMm)));
            }
        }

        public double EffectiveHeaderDnMm
        {
            get { return HeaderDnMm.HasValue && HeaderDnMm.Value > 0 ? HeaderDnMm.Value : AutoHeaderDnMm; }
        }

        public double HeaderLengthMm
        {
            get
            {
                var n = ValidCircuits().Count;
                if (n == 0) return 0;
                return 2 * EffectiveEndMarginMm + (n - 1) * SpacingMm;
            }
        }

        /// <summary>Distanza di ogni circuito dall'inizio del collettore (mm).</summary>
        public List<double> CircuitPositionsMm()
        {
            var list = new List<double>();
            var n = ValidCircuits().Count;
            var margin = EffectiveEndMarginMm;
            for (var i = 0; i < n; i++) list.Add(margin + i * SpacingMm);
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

            var run = new MepRun
            {
                Kind = MepKind.Pipe,
                KindExplicit = true,
                Size = MepSize.Round(headerDn, true),
                LengthMm = HeaderLengthMm,
                Direction = HeaderDirection,
                ExplicitTypeName = string.IsNullOrWhiteSpace(PipeTypeName) ? null : PipeTypeName.Trim()
            };

            var positions = CircuitPositionsMm();
            for (var i = 0; i < circuits.Count; i++)
            {
                var branch = new MepBranch
                {
                    Size = MepSize.Round(circuits[i].DnMm, true),
                    Count = 1,
                    LengthMm = CircuitLengthMm,
                    // I circuiti non vengono raccordati: solo sovrapposti al collettore, che resta
                    // un tubo unico (il T di Revit ridimensionerebbe l'innesto alla misura del circuito).
                    Connect = false,
                    // Un circuito = un gruppo di stacchi con una sola posizione, quindi l'alternanza
                    // non può essere risolta dall'indice a valle: la fissiamo qui.
                    Direction = CircuitDirection == DirectionKind.Alternate
                        ? (i % 2 == 0 ? DirectionKind.Left : DirectionKind.Right)
                        : CircuitDirection
                };
                branch.PositionsMm.Add(positions[i]);
                run.Branches.Add(branch);
            }

            var plan = new MepPlan { SourceText = Summary() };
            plan.Runs.Add(run);

            var result = new ParseResult { Success = true, Plan = plan };
            result.Notes.Add("I circuiti non vengono raccordati: partono dall'asse del collettore, sovrapposti, senza T.");
            if (!HeaderDnMm.HasValue || HeaderDnMm.Value <= 0)
                result.Notes.Add("DN del collettore calcolato per equivalenza di area: DN" + MepSize.Fmt(headerDn) + ".");

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
