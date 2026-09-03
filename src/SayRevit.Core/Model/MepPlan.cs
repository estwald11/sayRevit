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

    /// <summary>Uno stacco (derivazione) da un tratto principale.</summary>
    public sealed class MepBranch
    {
        public MepSize Size { get; set; }
        public int Count { get; set; } = 1;
        public double LengthMm { get; set; } = 500;
        public DirectionKind Direction { get; set; } = DirectionKind.Default;

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
