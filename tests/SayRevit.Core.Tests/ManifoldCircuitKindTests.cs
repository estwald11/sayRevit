using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class ManifoldCircuitKindTests
    {
        private static ManifoldPlan Plan(params ManifoldCircuit[] circuits)
        {
            var p = new ManifoldPlan { WithReturn = false, WithValves = false };
            foreach (var c in circuits) p.Circuits.Add(c);
            return p;
        }

        /// <summary>Piano con mandata, ritorno e famiglie di valvole: il caso reale.</summary>
        private static ManifoldPlan PlanWithValves(params ManifoldCircuit[] circuits)
        {
            var p = new ManifoldPlan { WithReturn = true, WithValves = true, BallValveFamily = "Sfera", ButterflyValveFamily = "Boax" };
            p.BallValveTypes.AddRange(new[] { "DN20_PN16", "DN25_PN16", "DN32_PN16" });
            p.ButterflyValveTypes.AddRange(new[] { "DN40_PN16", "DN50_PN16" });
            foreach (var c in circuits) p.Circuits.Add(c);
            return p;
        }

        [Fact]
        public void CinqueTipologie_NellOrdineProposto()
        {
            Assert.Equal(new[] { CircuitKind.Direct, CircuitKind.MixThreeWay, CircuitKind.MixTwoWayInjection, CircuitKind.NoPump, CircuitKind.Blind },
                CircuitKinds.All.Select(i => i.Kind).ToArray());
            Assert.Equal(new[] { "direct", "mix3", "mix2", "nopump", "blind" }, CircuitKinds.All.Select(i => i.Code).ToArray());
        }

        [Fact]
        public void TipologiaPredefinita_Diretto()
        {
            Assert.Equal(CircuitKind.Direct, new ManifoldCircuit(20).Kind);
            Assert.Equal(CircuitKind.Direct, new ManifoldCircuit().Kind);
        }

        [Fact]
        public void Componenti_CoerentiConLaTipologia()
        {
            var direct = CircuitKinds.Info(CircuitKind.Direct);
            Assert.True(direct.HasPump);
            Assert.False(direct.HasMixing);
            Assert.False(direct.HasBypass);
            Assert.False(direct.UsesPipeAfterValve);

            var mix3 = CircuitKinds.Info(CircuitKind.MixThreeWay);
            Assert.True(mix3.HasPump);
            Assert.True(mix3.HasMixing);
            Assert.True(mix3.HasBypass);
            Assert.Contains(CircuitComponent.ThreeWayMixingValve, mix3.SupplyComponents);

            var mix2 = CircuitKinds.Info(CircuitKind.MixTwoWayInjection);
            Assert.True(mix2.HasPump);
            Assert.True(mix2.HasMixing);
            Assert.True(mix2.HasBypass);
            Assert.Contains(CircuitComponent.TwoWayValve, mix2.SupplyComponents);

            var noPump = CircuitKinds.Info(CircuitKind.NoPump);
            Assert.False(noPump.HasPump);
            Assert.False(noPump.HasMixing);
            Assert.True(noPump.UsesPipeAfterValve);
            Assert.Equal(new[] { CircuitComponent.ShutoffValve }, noPump.SupplyComponents.ToArray());

            var blind = CircuitKinds.Info(CircuitKind.Blind);
            Assert.True(blind.IsBlind);
            Assert.True(blind.HasShutoffValve);
            Assert.False(blind.HasPump);
            Assert.Equal(new[] { CircuitComponent.ShutoffValve }, blind.SupplyComponents.ToArray());
            Assert.Equal(new[] { CircuitComponent.ShutoffValve }, blind.ReturnComponents.ToArray());
            Assert.Contains("fine alla flangia", CircuitKinds.SupplyChain(CircuitKind.Blind));

            // l'intercettazione c'è su tutte, ed è la prima cosa dopo il collettore
            Assert.All(CircuitKinds.All, i => Assert.Equal(CircuitComponent.ShutoffValve, i.SupplyComponents[0]));
            Assert.All(CircuitKinds.All, i => Assert.Equal(CircuitComponent.ShutoffValve, i.ReturnComponents[0]));

            // solo il senza pompa fissa il tubo dopo la valvola
            Assert.Equal(new[] { CircuitKind.NoPump }, CircuitKinds.All.Where(i => i.UsesPipeAfterValve).Select(i => i.Kind).ToArray());
        }

        [Theory]
        [InlineData("direct", CircuitKind.Direct)]
        [InlineData("Direct", CircuitKind.Direct)]
        [InlineData("diretto", CircuitKind.Direct)]
        [InlineData("mix3", CircuitKind.MixThreeWay)]
        [InlineData("MixThreeWay", CircuitKind.MixThreeWay)]
        [InlineData("3 vie", CircuitKind.MixThreeWay)]
        [InlineData("mix2", CircuitKind.MixTwoWayInjection)]
        [InlineData("iniezione", CircuitKind.MixTwoWayInjection)]
        [InlineData("2 vie", CircuitKind.MixTwoWayInjection)]
        [InlineData("nopump", CircuitKind.NoPump)]
        [InlineData("senza pompa", CircuitKind.NoPump)]
        [InlineData("blind", CircuitKind.Blind)]
        [InlineData("cieco", CircuitKind.Blind)]
        public void CodiciESinonimi_VengonoRiconosciuti(string text, CircuitKind expected)
        {
            CircuitKind kind;
            Assert.True(CircuitKinds.TryParse(text, out kind));
            Assert.Equal(expected, kind);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("boh")]
        public void TestoSconosciuto_NonVieneRiconosciuto(string text)
        {
            CircuitKind kind;
            Assert.False(CircuitKinds.TryParse(text, out kind));
        }

        [Fact]
        public void CircuitiConTipologia_SiSalvanoESiRileggono()
        {
            var p = Plan(new ManifoldCircuit(20, CircuitKind.Direct), new ManifoldCircuit(16, CircuitKind.MixThreeWay),
                new ManifoldCircuit(25, CircuitKind.MixTwoWayInjection), new ManifoldCircuit(32, CircuitKind.NoPump),
                new ManifoldCircuit(40, CircuitKind.Blind));
            var text = p.CircuitsToString();
            Assert.Equal("20:direct;16:mix3;25:mix2;32:nopump;40:blind", text);

            var back = new ManifoldPlan();
            back.LoadCircuitsFromString(text);
            Assert.Equal(new double[] { 20, 16, 25, 32, 40 }, back.Circuits.Select(c => c.DnMm).ToArray());
            Assert.Equal(new[] { CircuitKind.Direct, CircuitKind.MixThreeWay, CircuitKind.MixTwoWayInjection, CircuitKind.NoPump, CircuitKind.Blind },
                back.Circuits.Select(c => c.Kind).ToArray());
        }

        [Fact]
        public void ImpostazioniVecchie_SoloDn_ValgonoComeDiretto()
        {
            // i file settings.txt salvati prima delle tipologie contengono "20;16;16"
            var p = new ManifoldPlan();
            p.LoadCircuitsFromString("20;16;16");
            Assert.Equal(3, p.Circuits.Count);
            Assert.All(p.Circuits, c => Assert.Equal(CircuitKind.Direct, c.Kind));
        }

        [Fact]
        public void TipologiaSconosciuta_RicadeSulDiretto_DnNonValidoScartato()
        {
            var p = new ManifoldPlan();
            p.LoadCircuitsFromString("20:xyz;abc:mix3;:mix3;16: mix2 ");
            Assert.Equal(new double[] { 20, 16 }, p.Circuits.Select(c => c.DnMm).ToArray());
            Assert.Equal(CircuitKind.Direct, p.Circuits[0].Kind);
            Assert.Equal(CircuitKind.MixTwoWayInjection, p.Circuits[1].Kind);
        }

        [Fact]
        public void Riepilogo_MostraLaTipologiaDiOgniCircuito()
        {
            var p = Plan(new ManifoldCircuit(20, CircuitKind.MixThreeWay), new ManifoldCircuit(16, CircuitKind.NoPump));
            var summary = p.Summary();
            Assert.Contains("C1: DN20 mix 3 vie", summary);
            Assert.Contains("C2: DN16 senza pompa", summary);
        }

        [Fact]
        public void Anteprima_UnaNotaPerTipologiaPresente_ConLaCatenaDeiComponenti()
        {
            var p = Plan(new ManifoldCircuit(20, CircuitKind.Direct), new ManifoldCircuit(16, CircuitKind.Direct),
                new ManifoldCircuit(25, CircuitKind.MixThreeWay));
            var r = p.ToParseResult();
            Assert.True(r.Success);
            Assert.Contains(r.Notes, n => n.StartsWith("Diretto (no mix) (C1, C2): intercettazione → pompa"));
            Assert.Contains(r.Notes, n => n.StartsWith("Mix 3 vie (C3): intercettazione → valvola miscelatrice 3 vie → bypass mandata/ritorno → pompa"));
            Assert.DoesNotContain(r.Notes, n => n.StartsWith("Mix 2 vie"));
            Assert.DoesNotContain(r.Notes, n => n.StartsWith("Senza pompa"));
        }

        [Fact]
        public void ComponentiNonAncoraModellati_VengonoDichiarati()
        {
            var r = Plan(new ManifoldCircuit(20, CircuitKind.MixThreeWay)).ToParseResult();
            var w = Assert.Single(r.Warnings, x => x.StartsWith("Tipologie:"));
            Assert.Contains("pompe", w);
            Assert.Contains("valvole miscelatrici a 3 vie", w);
            Assert.Contains("bypass", w);
            Assert.DoesNotContain("valvole a 2 vie", w);

            // senza pompa e cieco: tutto quello che serve c'è già, nulla da dichiarare
            var quiet = PlanWithValves(new ManifoldCircuit(20, CircuitKind.NoPump), new ManifoldCircuit(20, CircuitKind.Blind)).ToParseResult();
            Assert.DoesNotContain(quiet.Warnings, x => x.StartsWith("Tipologie:"));
        }

        [Fact]
        public void LaTipologia_NonCambiaBaseNePosizioni()
        {
            var direct = Plan(new ManifoldCircuit(20, CircuitKind.Direct), new ManifoldCircuit(20, CircuitKind.Direct));
            var mixed = Plan(new ManifoldCircuit(20, CircuitKind.MixThreeWay), new ManifoldCircuit(20, CircuitKind.NoPump));
            Assert.Equal(direct.HeaderLengthMm, mixed.HeaderLengthMm);
            Assert.Equal(direct.CircuitPositionsMm(), mixed.CircuitPositionsMm());
            Assert.Equal(direct.AutoHeaderDnMm, mixed.AutoHeaderDnMm);
        }

        [Fact]
        public void CircuitiPerTipologia_NellOrdineDelleTipologie()
        {
            var p = Plan(new ManifoldCircuit(20, CircuitKind.NoPump), new ManifoldCircuit(16, CircuitKind.Direct),
                new ManifoldCircuit(25, CircuitKind.NoPump));
            var groups = p.CircuitsByKind();
            Assert.Equal(2, groups.Count);
            Assert.Equal(CircuitKind.Direct, groups[0].Key.Kind);
            Assert.Single(groups[0].Value);
            Assert.Equal(CircuitKind.NoPump, groups[1].Key.Kind);
            Assert.Equal(2, groups[1].Value.Count);
        }

        // ------------------------------------------- senza pompa: tubo dopo la valvola

        [Fact]
        public void SenzaPompa_TuboDi2000DopoLaValvola_SuMandataERitorno()
        {
            var p = PlanWithValves(new ManifoldCircuit(25, CircuitKind.NoPump), new ManifoldCircuit(40, CircuitKind.NoPump));
            var r = p.ToParseResult();
            Assert.True(r.Success);
            Assert.Equal(2, r.Plan.Runs.Count);

            foreach (var run in r.Plan.Runs) // mandata E ritorno
            {
                foreach (var b in run.Branches)
                {
                    Assert.NotNull(b.Valve);
                    Assert.Equal(2000, b.LengthAfterValveMm);
                    // lunghezza provvisoria: fino alla valvola + spazio per i pezzi + tubo dopo
                    Assert.Equal(p.ValveAxisDistanceMm + p.ValveAssemblyAllowanceMm + 2000, b.LengthMm, 6);
                    Assert.True(b.Valve.DistanceMm < b.LengthMm);
                }
            }
            Assert.Contains(r.Notes, n => n.StartsWith("Circuiti senza pompa:") && n.Contains("2000 mm") && n.Contains("seconda flangia"));
            Assert.Contains("tubo dopo la valvola 2000 mm", p.Summary());
        }

        [Fact]
        public void SenzaPompa_ValoreImpostato_DallUtente()
        {
            var p = PlanWithValves(new ManifoldCircuit(25, CircuitKind.NoPump));
            p.NoPumpPipeAfterValveMm = 1800;
            var b = p.ToParseResult().Plan.Runs[0].Branches[0];
            Assert.Equal(1800, b.LengthAfterValveMm);

            p.NoPumpPipeAfterValveMm = 0;
            Assert.False(p.ToParseResult().Success);
            // senza circuiti senza pompa il valore non conta
            var q = PlanWithValves(new ManifoldCircuit(25, CircuitKind.Direct));
            q.NoPumpPipeAfterValveMm = 0;
            Assert.True(q.ToParseResult().Success);
        }

        [Fact]
        public void SenzaPompa_SenzaValvole_RicadeSullaLunghezzaGenerica_EAvvisa()
        {
            // senza valvola "dopo la seconda flangia" non ha un riferimento
            var p = PlanWithValves(new ManifoldCircuit(25, CircuitKind.NoPump));
            p.WithValves = false;
            var r = p.ToParseResult();
            var b = r.Plan.Runs[0].Branches[0];
            Assert.Null(b.Valve);
            Assert.Null(b.LengthAfterValveMm);
            Assert.Equal(p.CircuitLengthMm, b.LengthMm);
            Assert.Contains(r.Warnings, w => w.Contains("Circuiti senza pompa senza valvola"));
        }

        // ------------------------------------------------- cieco: fine alla flangia

        [Fact]
        public void Cieco_ConValvola_SiFermaAllaFlangia_SuMandataERitorno()
        {
            var p = PlanWithValves(new ManifoldCircuit(25, CircuitKind.Blind), new ManifoldCircuit(50, CircuitKind.Blind));
            var r = p.ToParseResult();
            Assert.True(r.Success);

            foreach (var run in r.Plan.Runs) // mandata E ritorno
            {
                Assert.Equal(ValveKind.Ball, run.Branches[0].Valve.Kind);
                Assert.Equal(ValveKind.Butterfly, run.Branches[1].Valve.Kind);
                foreach (var b in run.Branches)
                {
                    Assert.Equal(0, b.LengthAfterValveMm); // nessun tubo a valle
                    // provvisoria: fino alla valvola più lo spazio per i pezzi, che Revit poi fissa
                    Assert.Equal(p.ValveAxisDistanceMm + p.ValveAssemblyAllowanceMm, b.LengthMm, 6);
                    Assert.True(b.Valve.DistanceMm < b.LengthMm);
                }
            }
            Assert.Contains(r.Notes, n => n.StartsWith("Circuiti ciechi:") && n.Contains("seconda flangia") && n.Contains("nessun tubo a valle"));
            Assert.Contains("C1: DN25 cieco", p.Summary());
            Assert.Contains("si ferma alla flangia", p.Summary());
            Assert.DoesNotContain("fondello a", p.Summary());
        }

        [Fact]
        public void Cieco_ContaPerLeValvole_ComeGliAltri()
        {
            // il cieco ha la valvola: il tipo per il suo DN va scelto e mostrato
            var p = PlanWithValves(new ManifoldCircuit(25, CircuitKind.Blind));
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n.Contains("DN25 → valvola a sfera \"DN25_PN16\""));
        }

        [Fact]
        public void Cieco_SenzaValvole_RicadeSullaLunghezzaGenerica_EAvvisa()
        {
            var p = PlanWithValves(new ManifoldCircuit(25, CircuitKind.Blind));
            p.WithValves = false;
            var r = p.ToParseResult();
            var b = r.Plan.Runs[0].Branches[0];
            Assert.Null(b.LengthAfterValveMm);
            Assert.Equal(p.CircuitLengthMm, b.LengthMm);
            Assert.Contains(r.Warnings, w => w.Contains("Circuiti ciechi senza valvola"));
        }

        // ------------------------------------------------------- le altre tipologie

        [Fact]
        public void AltreTipologie_UsanoLaLunghezzaGenerica()
        {
            var p = PlanWithValves(new ManifoldCircuit(25, CircuitKind.Direct), new ManifoldCircuit(25, CircuitKind.MixThreeWay),
                new ManifoldCircuit(25, CircuitKind.MixTwoWayInjection));
            p.CircuitLengthMm = 700;
            var run = p.ToParseResult().Plan.Runs[0];
            Assert.All(run.Branches, b =>
            {
                Assert.Null(b.LengthAfterValveMm);
                Assert.Equal(700, b.LengthMm);
            });
        }

        [Fact]
        public void AvvisoValvolaOltreLaLunghezza_SoloPerChiUsaLaLunghezzaGenerica()
        {
            // senza pompa e cieco sono lunghi quanto serve: la lunghezza generica corta non li riguarda
            var fixedEnd = PlanWithValves(new ManifoldCircuit(25, CircuitKind.NoPump), new ManifoldCircuit(25, CircuitKind.Blind));
            fixedEnd.CircuitLengthMm = 100; // < distanza valvola dall'asse
            Assert.DoesNotContain(fixedEnd.ToParseResult().Warnings, w => w.Contains("oltre la lunghezza dei circuiti"));

            var direct = PlanWithValves(new ManifoldCircuit(25, CircuitKind.Direct));
            direct.CircuitLengthMm = 100;
            Assert.Contains(direct.ToParseResult().Warnings, w => w.Contains("oltre la lunghezza dei circuiti"));
        }

        [Fact]
        public void StacchiDelParserTestuale_SenzaTuboFisso()
        {
            Assert.Null(new MepBranch().LengthAfterValveMm);
        }
    }
}
