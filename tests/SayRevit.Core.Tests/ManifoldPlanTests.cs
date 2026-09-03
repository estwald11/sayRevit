using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class ManifoldPlanTests
    {
        private static ManifoldPlan Plan(params double[] dns)
        {
            var p = new ManifoldPlan();
            foreach (var dn in dns) p.Circuits.Add(new ManifoldCircuit(dn));
            return p;
        }

        [Fact]
        public void SenzaCircuiti_NonSiPuoCostruire()
        {
            var r = Plan().ToParseResult();
            Assert.False(r.Success);
            Assert.Contains("circuito", r.Error);
        }

        [Fact]
        public void RigheVuote_VengonoIgnorate()
        {
            var p = Plan(20, 0, 16);
            Assert.Equal(2, p.ValidCircuits().Count);
            var run = Assert.Single(p.ToParseResult().Plan.Runs);
            Assert.Equal(2, run.Branches.Count);
        }

        [Fact]
        public void DnCollettoreAutomatico_DallaFormula()
        {
            // D = √(1,5·ΣS/0,785) con S = 0,785·dn² → D = √(1,5·Σdn²)
            // 4×DN20: √(1,5·1600) ≈ 49,0 mm
            Assert.Equal(48.99, Plan(20, 20, 20, 20).ComputedHeaderDnMm, 2);
            // arrotondato al DN commerciale superiore
            Assert.Equal(50, Plan(20, 20, 20, 20).AutoHeaderDnMm);
            // 3×DN16: √(1,5·768) ≈ 33,9 → DN40
            Assert.Equal(40, Plan(16, 16, 16).AutoHeaderDnMm);
        }

        [Fact]
        public void DnCollettoreImpostato_HaLaPrecedenzaSullAutomatico()
        {
            var p = Plan(20, 20);
            p.HeaderDnMm = 100;
            Assert.Equal(100, p.EffectiveHeaderDnMm);
            var run = Assert.Single(p.ToParseResult().Plan.Runs);
            Assert.Equal(100, run.Size.DiameterMm);
            Assert.True(run.Size.IsNominalDn);
        }

        [Fact]
        public void LaBaseSporgeDi50mmDaiBordiDelPrimoEUltimoCircuito()
        {
            // Bordo del circuito = posizione ± DN/2: la base parte 50 mm prima del bordo
            // del primo (50 + 20/2 = 60 mm prima dell'asse) e finisce 50 mm dopo l'ultimo.
            var p = Plan(20, 20, 20);
            p.SpacingMm = 200;
            Assert.Equal(50 + 10 + 2 * 200 + 10 + 50, p.HeaderLengthMm); // 520
            Assert.Equal(new double[] { 60, 260, 460 }, p.CircuitPositionsMm().ToArray());
        }

        [Fact]
        public void SporgenzaConDnDiversi_UsaIlBordoDiCiascunEstremo()
        {
            var p = Plan(50, 20, 32); // primo DN50, ultimo DN32
            p.SpacingMm = 150;
            Assert.Equal(50 + 25 + 2 * 150 + 16 + 50, p.HeaderLengthMm); // 441
            Assert.Equal(new double[] { 75, 225, 375 }, p.CircuitPositionsMm().ToArray());
        }

        [Fact]
        public void UnSoloCircuito_BaseLungaDnPiu100()
        {
            var p = Plan(40);
            Assert.Equal(40 + 100, p.HeaderLengthMm);
            Assert.Equal(new double[] { 70 }, p.CircuitPositionsMm().ToArray());
        }

        [Fact]
        public void OgniCircuito_DiventaUnoStaccoConPosizioneEsplicita()
        {
            var p = Plan(20, 16);
            p.SpacingMm = 150;
            p.CircuitLengthMm = 800;
            var run = Assert.Single(p.ToParseResult().Plan.Runs);

            Assert.Equal(MepKind.Pipe, run.Kind);
            Assert.Equal(2, run.Branches.Count);
            Assert.Equal(20, run.Branches[0].Size.DiameterMm);
            Assert.Equal(16, run.Branches[1].Size.DiameterMm);
            Assert.All(run.Branches, b =>
            {
                Assert.Equal(1, b.Count);
                Assert.Equal(800, b.LengthMm);
                Assert.Single(b.PositionsMm);
            });
            Assert.Equal(60, run.Branches[0].PositionsMm[0]);   // 50 + 20/2
            Assert.Equal(210, run.Branches[1].PositionsMm[0]);  // 60 + 150
        }

        [Fact]
        public void Circuiti_NonVengonoRaccordati()
        {
            // Il T di Revit spezzerebbe il collettore e ridimensionerebbe l'innesto alla misura
            // del circuito: i circuiti restano solo sovrapposti, il collettore è un tubo unico.
            var r = Plan(20, 16, 16).ToParseResult();
            var run = Assert.Single(r.Plan.Runs);
            Assert.All(run.Branches, b => Assert.False(b.Connect));
            Assert.Contains(r.Notes, n => n.Contains("non vengono raccordati"));
        }

        [Fact]
        public void StacchiDelParserTestuale_RestanoRaccordati()
        {
            // Il default di MepBranch non deve cambiare: la modalità testuale continua a creare i T.
            Assert.True(new MepBranch().Connect);
        }

        [Fact]
        public void TutteLePosizioni_CadonoDentroIlCollettore()
        {
            var p = Plan(20, 20, 20, 20, 20);
            var run = Assert.Single(p.ToParseResult().Plan.Runs);
            foreach (var b in run.Branches)
            {
                Assert.InRange(b.PositionsMm[0], 1, run.LengthMm - 1);
            }
        }

        [Fact]
        public void CircuitiAlternati_RisoltiCircuitoPerCircuito()
        {
            // Ogni circuito è un gruppo con un solo stacco: l'alternanza va fissata nel piano,
            // altrimenti a valle l'indice sarebbe sempre 0 e uscirebbero tutti dallo stesso lato.
            var p = Plan(20, 20, 20);
            p.CircuitDirection = DirectionKind.Alternate;
            var run = Assert.Single(p.ToParseResult().Plan.Runs);
            Assert.Equal(DirectionKind.Left, run.Branches[0].Direction);
            Assert.Equal(DirectionKind.Right, run.Branches[1].Direction);
            Assert.Equal(DirectionKind.Left, run.Branches[2].Direction);
        }

        [Fact]
        public void DirezioneSingola_RiportataSuTuttiICircuiti()
        {
            var p = Plan(20, 20);
            p.CircuitDirection = DirectionKind.Up;
            var run = Assert.Single(p.ToParseResult().Plan.Runs);
            Assert.All(run.Branches, b => Assert.Equal(DirectionKind.Up, b.Direction));
        }

        [Fact]
        public void TipoTubazioneScelto_FinisceNelPianoComeTipoEsplicito()
        {
            var p = Plan(20, 16);
            p.PipeTypeName = "Ghisa REI (scarico in vista piano interrato)";
            var run = Assert.Single(p.ToParseResult().Plan.Runs);
            Assert.Equal("Ghisa REI (scarico in vista piano interrato)", run.ExplicitTypeName);
            Assert.Contains("Ghisa REI", p.Summary());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void SenzaTipoScelto_ResaAlPredefinitoDelProgetto(string name)
        {
            var p = Plan(20);
            p.PipeTypeName = name;
            var run = Assert.Single(p.ToParseResult().Plan.Runs);
            Assert.Null(run.ExplicitTypeName);
            Assert.Contains("predefinito del progetto", p.Summary());
        }

        [Fact]
        public void CircuitoPiuGrandeDelCollettore_Avvisa()
        {
            var p = Plan(20, 20);
            p.HeaderDnMm = 20;
            var r = p.ToParseResult();
            Assert.True(r.Success);
            Assert.Contains(r.Warnings, w => w.Contains("DN maggiore o uguale al collettore"));
        }

        [Fact]
        public void InteresseTroppoStretto_Avvisa()
        {
            var p = Plan(50, 50);
            p.SpacingMm = 30;
            Assert.Contains(p.ToParseResult().Warnings, w => w.Contains("Interasse"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-100)]
        public void InteresseNonValido_NonSiPuoCostruire(double spacing)
        {
            var p = Plan(20);
            p.SpacingMm = spacing;
            Assert.False(p.ToParseResult().Success);
        }

        [Fact]
        public void LunghezzaCircuitiNonValida_NonSiPuoCostruire()
        {
            var p = Plan(20);
            p.CircuitLengthMm = 0;
            Assert.False(p.ToParseResult().Success);
        }

        [Fact]
        public void SerieDn_ArrotondaVersoLAlto()
        {
            Assert.Equal(25, ManifoldPlan.SnapUpToDn(21));
            Assert.Equal(25, ManifoldPlan.SnapUpToDn(25));
            Assert.Equal(600, ManifoldPlan.SnapUpToDn(600));
        }

        [Fact]
        public void CircuitiSiSalvanoESiRileggono()
        {
            var p = Plan(20, 16, 16);
            var text = p.CircuitsToString();
            Assert.Equal("20;16;16", text);

            var back = new ManifoldPlan();
            back.LoadCircuitsFromString(text);
            Assert.Equal(new double[] { 20, 16, 16 }, back.Circuits.Select(c => c.DnMm).ToArray());
        }

        [Fact]
        public void CircuitiDaTestoSporco_IgnoraIValoriNonValidi()
        {
            var p = new ManifoldPlan();
            p.LoadCircuitsFromString("20; ;abc;-5;16");
            Assert.Equal(new double[] { 20, 16 }, p.Circuits.Select(c => c.DnMm).ToArray());
        }
    }
}
