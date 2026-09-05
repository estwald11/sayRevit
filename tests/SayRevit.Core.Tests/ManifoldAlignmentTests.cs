using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    /// <summary>
    /// Le intercettazioni in cima alle catene di mandata e ritorno portano la stessa chiave di
    /// allineamento e gli stacchi gemelli la stessa chiave di coppia: il costruttore le mette alla stessa quota.
    /// </summary>
    public class ManifoldAlignmentTests
    {
        private static ManifoldPlan Plan()
        {
            var p = new ManifoldPlan
            {
                WithReturn = true,
                CircuitDirection = DirectionKind.Up,
                BallValveFamily = "Sfera",
                ButterflyValveFamily = "boax-s",
                ZoneValveFamily = "Zona",
                StrainerFamily = "Filtro"
            };
            p.BallValveTypes.Add("1\" Lever");
            p.ButterflyValveTypes.AddRange(new[] { "DN40_PN16", "DN50_PN16" });
            p.ZoneValveTypes.AddRange(new[] { "DN50 - Ductile Iron" });
            p.StrainerTypes.AddRange(new[] { "DN50" });
            p.Circuits.Add(new ManifoldCircuit(50, CircuitKind.MixTwoWayInjection));
            p.Circuits.Add(new ManifoldCircuit(25, CircuitKind.Direct));
            return p;
        }

        [Fact]
        public void IntercettazioneInCima_HaLaChiaveDiAllineamento_SuEntrambeLeCatene()
        {
            var p = Plan();
            var c = p.Circuits[0];
            foreach (var supply in new[] { true, false })
            {
                var chain = p.ChainFor(c, supply);
                var aligned = chain.Where(i => !string.IsNullOrWhiteSpace(i.AlignKey)).ToList();
                Assert.Single(aligned);
                Assert.Same(chain.Last(), aligned[0]);
                Assert.Equal(ManifoldPlan.TopShutoffAlignKey, aligned[0].AlignKey);
                Assert.Equal(ValveKind.Butterfly, aligned[0].Piece.Kind);
                // la prima intercettazione è già alla stessa quota per costruzione: nessuna chiave
                Assert.Null(chain[0].AlignKey);
            }
        }

        [Fact]
        public void StacchiGemelli_StessaChiaveDiCoppia_DiversaTraCircuiti()
        {
            var r = Plan().ToParseResult();
            Assert.True(r.Success);
            var branches = r.Plan.Runs.SelectMany(run => run.Branches).Where(b => b.PairKey != null).ToList();
            // due circuiti × mandata e ritorno
            Assert.Equal(4, branches.Count);
            var keys = branches.Select(b => b.PairKey).Distinct().ToList();
            Assert.Equal(2, keys.Count);
            foreach (var key in keys) Assert.Equal(2, branches.Count(b => b.PairKey == key));
            Assert.Contains(r.Notes, n => n.StartsWith("Intercettazioni in cima alla stessa quota su mandata e ritorno"));
        }
    }
}
