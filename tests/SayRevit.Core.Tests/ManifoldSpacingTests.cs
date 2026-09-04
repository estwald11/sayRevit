using System.Collections.Generic;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class ManifoldSpacingTests
    {
        private static StubFootprint Box(double alongMin, double alongMax, double sideMin, double sideMax, double upMin = 100, double upMax = 300)
        {
            return new StubFootprint
            {
                AlongMinMm = alongMin, AlongMaxMm = alongMax,
                SideMinMm = sideMin, SideMaxMm = sideMax,
                UpMinMm = upMin, UpMaxMm = upMax
            };
        }

        [Fact]
        public void SoloTubi_IlPavimentoVince()
        {
            var stubs = new List<StubFootprint> { StubFootprint.PipeOnly(25, 500), StubFootprint.PipeOnly(32, 500) };
            var r = ManifoldSpacing.Minimal(stubs, 150, false, 300, 60, 20, 10);
            Assert.Equal(150, r.SpacingMm);
            Assert.Empty(r.Notes);
        }

        [Fact]
        public void ViciniSullaStessaBase_LaLevaDettaLInterasse()
        {
            // leva di 250 mm verso +X sul primo, corpo simmetrico sul secondo
            var stubs = new List<StubFootprint> { Box(-50, 250, -60, 60), Box(-60, 60, -60, 60) };
            var r = ManifoldSpacing.Minimal(stubs, 150, false, 300, 60, 20, 10);
            // 250 - (-60) + 20 = 330
            Assert.Equal(330, r.SpacingMm);
            Assert.Contains(r.Notes, n => n.Contains("stessa base"));
        }

        [Fact]
        public void Ritorno_ContaSoloSeGliIngombriSiSovrapponogonoDiLato()
        {
            var stubs = new List<StubFootprint> { Box(-100, 100, -60, 60), Box(-100, 100, -60, 60) };
            // di lato: 60 + 60 < 300 → il ritorno non impone nulla, restano i vicini: 220
            var far = ManifoldSpacing.Minimal(stubs, 150, true, 300, 60, 20, 10);
            Assert.Equal(220, far.SpacingMm);

            // basi a 100 mm: gli ingombri si sovrappongono di lato → il ritorno a mezzo interasse
            // deve stare a 220 → interasse 440
            var near = ManifoldSpacing.Minimal(stubs, 150, true, 100, 60, 20, 10);
            Assert.Equal(440, near.SpacingMm);
            Assert.Contains(near.Notes, n => n.Contains("ritorno"));
        }

        [Fact]
        public void Ritorno_LevaVersoLAltraBase_Avvisa()
        {
            // la leva sporge 280 mm verso il ritorno, a quota che incrocia la base (up da 20)
            var stubs = new List<StubFootprint> { Box(-50, 50, -50, 280, 20, 300) };
            var r = ManifoldSpacing.Minimal(stubs, 150, true, 300, 60, 20, 10);
            Assert.Contains(r.Warnings, w => w.Contains("base dell'altro collettore"));
        }

        [Fact]
        public void Arrotondamento_PerEccesso()
        {
            var stubs = new List<StubFootprint> { Box(-33, 33, -30, 30), Box(-33, 33, -30, 30) };
            var r = ManifoldSpacing.Minimal(stubs, 0, false, 300, 60, 5, 10);
            // 33 + 33 + 5 = 71 → 80
            Assert.Equal(80, r.SpacingMm);
        }

        [Fact]
        public void IngombriAQuoteDiverse_NonInterferiscono()
        {
            var stubs = new List<StubFootprint> { Box(-200, 200, -60, 60, 100, 200), Box(-200, 200, -60, 60, 250, 350) };
            var r = ManifoldSpacing.Minimal(stubs, 150, false, 300, 60, 20, 10);
            Assert.Equal(150, r.SpacingMm);
        }
    }

    public class ManifoldAutoSpacingPlanTests
    {
        [Fact]
        public void InterasseAutomatico_ApplicaIlCalcoloETieneNota()
        {
            var plan = new ManifoldPlan { WithReturn = false, SpacingMm = 150, AutoSpacing = true };
            plan.Circuits.Add(new ManifoldCircuit(50));
            plan.Circuits.Add(new ManifoldCircuit(50));
            var footprints = new List<StubFootprint>
            {
                new StubFootprint { AlongMinMm = -60, AlongMaxMm = 240, SideMinMm = -60, SideMaxMm = 60, UpMinMm = 100, UpMaxMm = 300 },
                new StubFootprint { AlongMinMm = -60, AlongMaxMm = 240, SideMinMm = -60, SideMaxMm = 60, UpMinMm = 100, UpMaxMm = 300 }
            };
            var result = plan.ApplyAutoSpacing(footprints, 60);
            Assert.Equal(320, plan.SpacingMm);
            Assert.Contains(result.Notes, n => n.Contains("Interasse"));
            Assert.Contains(plan.ToParseResult().Notes, n => n.Contains("automatico"));
        }

        [Fact]
        public void InterasseAutomatico_NellAnteprima_DiceCheVerraCalcolato()
        {
            var plan = new ManifoldPlan { WithReturn = false, AutoSpacing = true };
            plan.Circuits.Add(new ManifoldCircuit(50));
            Assert.Contains(plan.ToParseResult().Notes, n => n.Contains("Interasse automatico"));
        }
    }
}
