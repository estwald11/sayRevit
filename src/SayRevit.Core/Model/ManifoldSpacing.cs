using System;
using System.Collections.Generic;

namespace SayRevit.Core.Model
{
    /// <summary>
    /// Ingombro di tutto ciò che sta su uno stacco (tubo, valvola, flange, leva), in mm, riferito
    /// all'asse dello stacco e al collettore: "lungo" = direzione del collettore (+X), "lato" =
    /// perpendicolare orizzontale (+ verso il ritorno), "su" = lungo lo stacco a partire dall'asse
    /// del collettore. Gli intervalli sono asimmetrici: una leva sporge da una parte sola.
    /// </summary>
    public sealed class StubFootprint
    {
        public double AlongMinMm { get; set; }
        public double AlongMaxMm { get; set; }
        public double SideMinMm { get; set; }
        public double SideMaxMm { get; set; }
        public double UpMinMm { get; set; }
        public double UpMaxMm { get; set; }

        /// <summary>Ingombro del solo tubo dello stacco (cilindro di raggio r, lungo tutto lo stacco).</summary>
        public static StubFootprint PipeOnly(double dnMm, double lengthMm)
        {
            var r = dnMm / 2.0;
            return new StubFootprint
            {
                AlongMinMm = -r, AlongMaxMm = r,
                SideMinMm = -r, SideMaxMm = r,
                UpMinMm = 0, UpMaxMm = lengthMm
            };
        }

        public StubFootprint Union(StubFootprint other)
        {
            if (other == null) return this;
            return new StubFootprint
            {
                AlongMinMm = Math.Min(AlongMinMm, other.AlongMinMm),
                AlongMaxMm = Math.Max(AlongMaxMm, other.AlongMaxMm),
                SideMinMm = Math.Min(SideMinMm, other.SideMinMm),
                SideMaxMm = Math.Max(SideMaxMm, other.SideMaxMm),
                UpMinMm = Math.Min(UpMinMm, other.UpMinMm),
                UpMaxMm = Math.Max(UpMaxMm, other.UpMaxMm)
            };
        }

        public override string ToString()
        {
            return "lungo " + MepSize.Fmt(Math.Round(AlongMinMm)) + "…" + MepSize.Fmt(Math.Round(AlongMaxMm)) +
                   ", lato " + MepSize.Fmt(Math.Round(SideMinMm)) + "…" + MepSize.Fmt(Math.Round(SideMaxMm)) +
                   ", su " + MepSize.Fmt(Math.Round(UpMinMm)) + "…" + MepSize.Fmt(Math.Round(UpMaxMm)) + " mm";
        }
    }

    /// <summary>
    /// Interasse minimo tra i circuiti di un collettore tale che nessun elemento sugli stacchi
    /// interferisca con quelli degli stacchi vicini: i vicini sulla stessa base e — con il ritorno
    /// interlacciato a mezzo interasse su una base parallela — quelli dell'altra base, se i loro
    /// ingombri si sovrappongono anche di lato e in altezza. Gli ingombri sono considerati
    /// orientati allo stesso modo su tutti gli stacchi (è così che vengono costruiti).
    /// </summary>
    public static class ManifoldSpacing
    {
        public sealed class Result
        {
            public double SpacingMm { get; set; }
            public List<string> Notes { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
        }

        /// <param name="stubs">Ingombri dei circuiti della mandata, nell'ordine lungo la base.</param>
        /// <param name="floorMm">Interasse minimo comunque rispettato (valore scelto dall'utente).</param>
        /// <param name="withReturn">Se c'è il ritorno interlacciato a mezzo interasse.</param>
        /// <param name="returnOffsetMm">Distanza tra gli assi delle due basi.</param>
        /// <param name="headerRadiusMm">Raggio esterno delle basi.</param>
        /// <param name="clearanceMm">Aria minima tra due elementi.</param>
        /// <param name="roundToMm">Arrotondamento per eccesso dell'interasse.</param>
        public static Result Minimal(IList<StubFootprint> stubs, double floorMm, bool withReturn, double returnOffsetMm,
            double headerRadiusMm, double clearanceMm, double roundToMm)
        {
            return Minimal(stubs, null, floorMm, withReturn, returnOffsetMm, headerRadiusMm, clearanceMm, roundToMm);
        }

        /// <param name="extras">
        /// Per ogni stacco (stesso indice) un ingombro AGGIUNTIVO, staccato dallo stacco ma riferito
        /// al suo asse (es. il tratto verticale del bypass, di lato di tutta la distanza tra le
        /// basi), oppure null. Viene confrontato con gli stacchi delle due basi ma non con la base
        /// dell'altro collettore (sta in alto) né con gli altri ingombri aggiuntivi.
        /// </param>
        public static Result Minimal(IList<StubFootprint> stubs, IList<StubFootprint> extras, double floorMm, bool withReturn, double returnOffsetMm,
            double headerRadiusMm, double clearanceMm, double roundToMm)
        {
            var result = new Result();
            var s = Math.Max(0, floorMm);
            if (stubs == null || stubs.Count == 0)
            {
                result.SpacingMm = s;
                return result;
            }

            // ingombri aggiuntivi (bypass) contro gli stacchi: della stessa base a (k - i)·s, del
            // ritorno a (j - i + 1/2)·s lungo la base e a returnOffset di lato
            if (extras != null)
            {
                for (var i = 0; i < stubs.Count && i < extras.Count; i++)
                {
                    var e = extras[i];
                    if (e == null) continue;
                    for (var k = 0; k < stubs.Count; k++)
                    {
                        var b = stubs[k];
                        if (k != i && Overlap(e.SideMinMm, e.SideMaxMm, b.SideMinMm, b.SideMaxMm) && Overlap(e.UpMinMm, e.UpMaxMm, b.UpMinMm, b.UpMaxMm))
                        {
                            var d = k - i;
                            var need = d > 0 ? (e.AlongMaxMm - b.AlongMinMm + clearanceMm) / d : (b.AlongMaxMm - e.AlongMinMm + clearanceMm) / -d;
                            if (need > s)
                            {
                                s = need;
                                result.Notes.Add("Interasse portato a " + MepSize.Fmt(Math.Round(need)) + " mm dal bypass del circuito " + (i + 1) +
                                                 " e dal circuito " + (k + 1) + " della mandata.");
                            }
                        }
                        if (!withReturn) continue;
                        if (!Overlap(e.SideMinMm, e.SideMaxMm, returnOffsetMm + b.SideMinMm, returnOffsetMm + b.SideMaxMm) ||
                            !Overlap(e.UpMinMm, e.UpMaxMm, b.UpMinMm, b.UpMaxMm))
                            continue;
                        var dr = k - i + 0.5;
                        var needR = dr > 0 ? (e.AlongMaxMm - b.AlongMinMm + clearanceMm) / dr : (b.AlongMaxMm - e.AlongMinMm + clearanceMm) / -dr;
                        if (needR > s)
                        {
                            s = needR;
                            result.Notes.Add("Interasse portato a " + MepSize.Fmt(Math.Round(needR)) + " mm dal bypass del circuito " + (i + 1) +
                                             " e dal circuito " + (k + 1) + " del ritorno.");
                        }
                    }
                }
            }

            // vicini sulla stessa base: lo stacco i+1 sta a +s
            for (var i = 0; i + 1 < stubs.Count; i++)
            {
                if (!Overlap(stubs[i].SideMinMm, stubs[i].SideMaxMm, stubs[i + 1].SideMinMm, stubs[i + 1].SideMaxMm) ||
                    !Overlap(stubs[i].UpMinMm, stubs[i].UpMaxMm, stubs[i + 1].UpMinMm, stubs[i + 1].UpMaxMm))
                    continue;
                var need = stubs[i].AlongMaxMm - stubs[i + 1].AlongMinMm + clearanceMm;
                if (need > s)
                {
                    s = need;
                    result.Notes.Add("Interasse portato a " + MepSize.Fmt(Math.Round(need)) + " mm dai circuiti " + (i + 1) + " e " + (i + 2) + " (stessa base).");
                }
            }

            if (withReturn)
            {
                // stacco j del ritorno: a (j - i + 1/2)·s lungo la base e a returnOffset di lato
                for (var i = 0; i < stubs.Count; i++)
                {
                    for (var j = 0; j < stubs.Count; j++)
                    {
                        var a = stubs[i];
                        var b = stubs[j];
                        if (!Overlap(a.SideMinMm, a.SideMaxMm, returnOffsetMm + b.SideMinMm, returnOffsetMm + b.SideMaxMm) ||
                            !Overlap(a.UpMinMm, a.UpMaxMm, b.UpMinMm, b.UpMaxMm))
                            continue;
                        var d = j - i + 0.5; // in unità di interasse
                        var need = d > 0
                            ? (a.AlongMaxMm - b.AlongMinMm + clearanceMm) / d
                            : (b.AlongMaxMm - a.AlongMinMm + clearanceMm) / -d;
                        if (need > s)
                        {
                            s = need;
                            result.Notes.Add("Interasse portato a " + MepSize.Fmt(Math.Round(need)) + " mm dal circuito " + (i + 1) +
                                             " della mandata e dal " + (j + 1) + " del ritorno (ingombri sovrapposti di lato).");
                        }
                    }
                }

                // interferenza con la base dell'altro collettore: l'interasse non può risolverla
                for (var i = 0; i < stubs.Count; i++)
                {
                    var a = stubs[i];
                    if (a.UpMinMm >= headerRadiusMm + clearanceMm) continue;
                    var reachToReturn = a.SideMaxMm + headerRadiusMm + clearanceMm;
                    var reachToSupply = -a.SideMinMm + headerRadiusMm + clearanceMm;
                    var reach = Math.Max(reachToReturn, reachToSupply);
                    if (reach > returnOffsetMm)
                        result.Warnings.Add("Il circuito " + (i + 1) + " arriva alla base dell'altro collettore: porta la distanza tra le basi ad almeno " +
                                            MepSize.Fmt(Math.Ceiling(reach / 10) * 10) + " mm (ora " + MepSize.Fmt(returnOffsetMm) + ").");
                }
            }

            if (roundToMm > 0) s = Math.Ceiling(s / roundToMm - 1e-9) * roundToMm;
            result.SpacingMm = s;
            return result;
        }

        private static bool Overlap(double aMin, double aMax, double bMin, double bMax)
        {
            return aMin < bMax && bMin < aMax;
        }
    }
}
