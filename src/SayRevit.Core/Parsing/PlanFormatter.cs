using System.Globalization;
using System.Text;
using SayRevit.Core.Model;

namespace SayRevit.Core.Parsing
{
    /// <summary>Descrizione leggibile del piano, mostrata all'utente prima della creazione.</summary>
    public static class PlanFormatter
    {
        public static string Describe(ParseResult result)
        {
            var sb = new StringBuilder();
            if (result == null) return string.Empty;
            if (!result.Success)
            {
                sb.AppendLine("ERRORE: " + result.Error);
                return sb.ToString();
            }

            var plan = result.Plan;
            for (var i = 0; i < plan.Runs.Count; i++)
            {
                var r = plan.Runs[i];
                sb.Append(plan.Runs.Count > 1 ? "Tratto " + (i + 1) + ": " : string.Empty);
                sb.Append(r.Kind == MepKind.Pipe ? "tubazione " : "canale ");
                sb.Append(r.Size);
                sb.Append(", lunghezza ").Append(Len(r.LengthMm));
                sb.Append(", direzione ").Append(DirName(r.Direction, false, r.Kind));
                if (r.ContinuesPrevious) sb.Append(" (prosegue dal tratto precedente)");
                if (r.ExplicitTypeName != null) sb.Append(", tipo \"").Append(r.ExplicitTypeName).Append("\"");
                else if (r.TypeHints.Count > 0) sb.Append(", tipo con \"").Append(string.Join("/", r.TypeHints)).Append("\"");
                if (r.SystemClass != null) sb.Append(", sistema ").Append(r.SystemPhrase ?? r.SystemClass).Append(" [").Append(r.SystemClass).Append("]");
                if (r.LevelHint != null) sb.Append(", livello \"").Append(r.LevelHint).Append("\"");
                if (r.ElevationMm.HasValue) sb.Append(", quota ").Append(Len(r.ElevationMm.Value));
                sb.AppendLine();

                foreach (var b in r.Branches)
                {
                    sb.Append("   - ").Append(b.Count).Append(b.Count == 1 ? " stacco " : " stacchi ").Append(b.Size);
                    sb.Append(", lunghezza ").Append(Len(b.LengthMm));
                    sb.Append(", direzione ").Append(DirName(b.Direction, true, r.Kind));
                    if (!b.Connect) sb.Append(b.Count == 1 ? ", non raccordato (sovrapposto)" : ", non raccordati (sovrapposti)");
                    if (b.PositionsMm.Count > 0)
                    {
                        sb.Append(", a ");
                        for (var k = 0; k < b.PositionsMm.Count; k++)
                        {
                            if (k > 0) sb.Append(", ");
                            sb.Append(Len(b.PositionsMm[k]));
                        }
                        sb.Append(" dall'inizio");
                    }
                    else if (b.SpacingMm.HasValue) sb.Append(", interasse ").Append(Len(b.SpacingMm.Value));
                    else if (b.Count > 1) sb.Append(", distribuiti uniformemente");
                    else sb.Append(", a metà del tratto");
                    sb.AppendLine();
                }
            }

            foreach (var n in result.Notes) sb.AppendLine("Nota: " + n);
            foreach (var w in result.Warnings) sb.AppendLine("Attenzione: " + w);
            return sb.ToString();
        }

        public static string Len(double mm)
        {
            if (mm >= 1000 && System.Math.Abs(mm / 1000 - System.Math.Round(mm / 1000, 2)) < 1e-9)
                return (mm / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " m";
            return mm.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
        }

        public static string DirName(DirectionKind d, bool isBranch, MepKind kind)
        {
            switch (d)
            {
                case DirectionKind.PlusX: return "+X (est)";
                case DirectionKind.MinusX: return "-X (ovest)";
                case DirectionKind.PlusY: return "+Y (nord)";
                case DirectionKind.MinusY: return "-Y (sud)";
                case DirectionKind.Up: return "verso l'alto";
                case DirectionKind.Down: return "verso il basso";
                case DirectionKind.Left: return "laterale sinistra";
                case DirectionKind.Right: return "laterale destra";
                case DirectionKind.Alternate: return "alternati sinistra/destra";
                default:
                    if (!isBranch) return "+X (predefinita)";
                    return kind == MepKind.Pipe ? "verso l'alto (predefinita)" : "laterale sinistra (predefinita)";
            }
        }
    }
}
