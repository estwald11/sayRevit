using System.Collections.Generic;
using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class ManifoldValveTests
    {
        private const string BallFamily = "Valvola a sfera";
        private const string BoaxFamily = "boax-s with lp lever pn6_dn65_48013982 lod-200";

        private static readonly string[] BoaxTypes =
        {
            "DN20_PN6_48013978", "DN20_PN16_48013978", "DN25_PN16_48013978",
            "DN32_PN16_48013979", "DN40_PN6_48013980", "DN40_PN16_48013980",
            "DN50_PN16_48013981"
        };

        private static readonly string[] BallTypes =
        {
            "1/2\" Butterfly", "3/4\" Butterfly", "1\" Lever", "1 1/4\" Lever", "1 1/2\" Lever", "2\" Lever"
        };

        private static ManifoldPlan Plan(params double[] dns)
        {
            var p = new ManifoldPlan
            {
                WithReturn = false,
                BallValveFamily = BallFamily,
                ButterflyValveFamily = BoaxFamily
            };
            p.BallValveTypes.AddRange(BallTypes);
            p.ButterflyValveTypes.AddRange(BoaxTypes);
            foreach (var dn in dns) p.Circuits.Add(new ManifoldCircuit(dn));
            return p;
        }

        private static List<MepValve> Valves(ManifoldPlan plan)
        {
            return plan.ToParseResult().Plan.Runs.SelectMany(r => r.Branches).Select(b => b.Valve).ToList();
        }

        [Fact]
        public void SogliaPredefinita_SferaFinoADn32_BoaxOltre()
        {
            var valves = Valves(Plan(20, 32, 40, 50));
            Assert.Equal(ValveKind.Ball, valves[0].Kind);
            Assert.Equal(ValveKind.Ball, valves[1].Kind);
            Assert.Equal(ValveKind.Butterfly, valves[2].Kind);
            Assert.Equal(ValveKind.Butterfly, valves[3].Kind);
        }

        [Fact]
        public void SogliaScelta_DallUtente_SpostaIlPassaggio()
        {
            var plan = Plan(20, 25, 32, 40);
            plan.BallValveMaxDnMm = 20;
            var kinds = Valves(plan).Select(v => v.Kind).ToList();
            Assert.Equal(
                new[] { ValveKind.Ball, ValveKind.Butterfly, ValveKind.Butterfly, ValveKind.Butterfly },
                kinds);
        }

        [Fact]
        public void SogliaAlta_MetteLaSferaSuTutti()
        {
            var plan = Plan(20, 40, 50);
            plan.BallValveMaxDnMm = 100;
            Assert.All(Valves(plan), v => Assert.Equal(ValveKind.Ball, v.Kind));
        }

        [Fact]
        public void TipoEFamiglia_SonoQuelliDellaMisura()
        {
            var valves = Valves(Plan(25, 40));
            Assert.Equal(BallFamily, valves[0].FamilyName);
            Assert.Equal("1\" Lever", valves[0].TypeName);
            Assert.Equal(BoaxFamily, valves[1].FamilyName);
            Assert.Equal("DN40_PN16_48013980", valves[1].TypeName);
        }

        [Fact]
        public void SoloLaBoax_VaMontataTraDueFlange()
        {
            var valves = Valves(Plan(25, 40));
            Assert.False(valves[0].WithFlanges);
            Assert.True(valves[1].WithFlanges);
        }

        [Fact]
        public void RollioPredefinito_ZeroPerLaSfera_90PerLaBoax()
        {
            var valves = Valves(Plan(25, 40));
            Assert.Equal(0, valves[0].RollDegrees);
            Assert.Equal(90, valves[1].RollDegrees);
        }

        [Fact]
        public void RollioScelto_ValeSoloPerLaBoax()
        {
            var plan = Plan(25, 40);
            plan.ButterflyRollDegrees = 270;
            var valves = Valves(plan);
            Assert.Equal(0, valves[0].RollDegrees);
            Assert.Equal(270, valves[1].RollDegrees);
        }

        [Fact]
        public void RotazioneBoax_ScegliibileDallUtente()
        {
            var plan = Plan(40);
            plan.ButterflyRollDegrees = 180;
            Assert.Equal(180, Valves(plan).Single().RollDegrees);
            Assert.Contains(plan.ToParseResult().Notes, n => n.Contains("180") && n.Contains("asse del tubo"));
        }

        [Fact]
        public void ConLaBoax_LAnteprimaParlaDelleFlange()
        {
            Assert.Contains(Plan(40).ToParseResult().Notes, n => n.Contains("Flange"));
            Assert.DoesNotContain(Plan(20).ToParseResult().Notes, n => n.Contains("Flange"));
        }

        [Fact]
        public void PnRichiesto_SelezionaIlTipo()
        {
            var plan = Plan(40);
            plan.ValvePnBar = 6;
            Assert.Equal("DN40_PN6_48013980", Valves(plan).Single().TypeName);
        }

        [Fact]
        public void ValvoleDisattivate_NessunaValvolaSugliStacchi()
        {
            var plan = Plan(20, 40);
            plan.WithValves = false;
            Assert.All(Valves(plan), Assert.Null);
            Assert.Contains(plan.ToParseResult().Notes, n => n.Contains("Nessuna valvola"));
        }

        [Fact]
        public void AncheIlRitorno_PortaLeValvole()
        {
            var plan = Plan(20, 40);
            plan.WithReturn = true;
            var valves = Valves(plan);
            Assert.Equal(4, valves.Count);
            Assert.All(valves, Assert.NotNull);
        }

        [Fact]
        public void FamigliaNonScelta_AvvisaESaltaQuegliStacchi()
        {
            var plan = Plan(20, 40);
            plan.ButterflyValveFamily = null;
            var valves = Valves(plan);
            Assert.NotNull(valves[0]);
            Assert.Null(valves[1]);
            Assert.Contains(plan.ToParseResult().Warnings, w => w.Contains("boax"));
        }

        [Fact]
        public void MisuraAssente_NellaFamiglia_Avvisa()
        {
            var plan = Plan(65); // la famiglia boax dei test arriva a DN50
            var result = plan.ToParseResult();
            Assert.Equal("DN50_PN16_48013981", Valves(plan).Single().TypeName);
            Assert.Contains(result.Warnings, w => w.Contains("DN65") && w.Contains("più vicina"));
        }

        [Fact]
        public void DistanzaDentroIlCollettore_Avvisa()
        {
            var plan = Plan(20);
            plan.HeaderDnMm = 200;
            plan.ValveDistanceMm = 80;
            Assert.Contains(plan.ToParseResult().Warnings, w => w.Contains("dentro il collettore"));
        }

        [Fact]
        public void DistanzaOltreLoStacco_Avvisa()
        {
            var plan = Plan(20);
            plan.CircuitLengthMm = 300;
            plan.ValveDistanceMm = 400;
            Assert.Contains(plan.ToParseResult().Warnings, w => w.Contains("oltre la lunghezza"));
        }

        [Fact]
        public void DistanzaValvola_ArrivaAlloStacco()
        {
            var plan = Plan(20);
            plan.ValveDistanceMm = 220;
            Assert.Equal(220, Valves(plan).Single().DistanceMm);
        }
    }
}
