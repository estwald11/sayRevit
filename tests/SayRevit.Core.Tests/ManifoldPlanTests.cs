using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class ManifoldPlanTests
    {
        private static ManifoldPlan Plan(params double[] dns)
        {
            var p = new ManifoldPlan { WithReturn = false }; // i test storici guardano la sola mandata
            foreach (var dn in dns) p.Circuits.Add(new ManifoldCircuit(dn));
            return p;
        }

        private static ManifoldPlan PlanWithReturn(params double[] dns)
        {
            var p = Plan(dns);
            p.WithReturn = true;
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
        public void FormulaCalcolataAllaLettera_CoincideColCodice()
        {
            // D = √(1,5·(S₁+S₂+…)/0,785) con S = sezioni (aree) dei circuiti.
            // Il codice usa la forma semplificata √(1,5·Σdn²): qui la formula viene
            // calcolata alla lettera, passando dalle aree, e deve dare lo stesso valore.
            var dns = new double[] { 20, 16, 25, 32 };
            var sumAreas = dns.Sum(d => 0.785 * d * d);          // S₁+S₂+…
            var literal = System.Math.Sqrt(1.5 * sumAreas / 0.785);

            Assert.Equal(literal, Plan(dns).ComputedHeaderDnMm, 6);
            // esempio a mano: 2×DN20 → S=314 mm² l'una → √(1,5·628/0,785) = √1200 ≈ 34,64
            Assert.Equal(34.64, Plan(20, 20).ComputedHeaderDnMm, 2);
        }

        private static void AddSizes(ManifoldPlan p, params double[][] nominalInner)
        {
            foreach (var z in nominalInner)
                p.HeaderSizeCandidates.Add(new CatalogPipeSize { NominalMm = z[0], InnerMm = z[1] });
        }

        [Fact]
        public void DnBase_MinimoDiametroInternoMaggioreOUgualeAllaFormula()
        {
            // 2×DN20 → D formula ≈ 34,64 mm. Serve la misura col Øint MINIMO tra quelli ≥ 34,64.
            var p = Plan(20, 20);
            AddSizes(p, new[] { 32.0, 28.4 }, new[] { 40.0, 36.2 }, new[] { 50.0, 45.8 });
            var pick = p.PickHeaderSize();
            Assert.Equal(40, pick.NominalMm);   // Øint 36,2 ≥ 34,64; DN50 (45,8) basterebbe ma non è il minimo
            Assert.Equal(40, p.AutoHeaderDnMm);
        }

        [Fact]
        public void DnBase_ContaIlDiametroInternoNonIlNominale()
        {
            // Tubo plastico con interni molto minori del DN: il nominale 40 NON basta
            // anche se 40 ≥ 34,64, perché il suo interno è 32,6.
            var p = Plan(20, 20);
            AddSizes(p, new[] { 40.0, 32.6 }, new[] { 50.0, 40.8 }, new[] { 63.0, 51.4 });
            Assert.Equal(50, p.AutoHeaderDnMm); // Øint 40,8 è il minimo ≥ 34,64
        }

        [Fact]
        public void NessunaMisuraSufficiente_UsaLaPiuGrandeEAvvisa()
        {
            var p = Plan(50, 50, 50); // D formula ≈ 106 mm
            AddSizes(p, new[] { 50.0, 45.8 }, new[] { 65.0, 60.2 });
            Assert.Equal(65, p.AutoHeaderDnMm);
            var r = p.ToParseResult();
            Assert.Contains(r.Warnings, w => w.Contains("Nessuna misura del tipo"));
        }

        [Fact]
        public void SenzaDiametriInterni_RipiegaSullaSerieCommerciale()
        {
            var p = Plan(20, 20, 20, 20); // D formula ≈ 49 mm
            Assert.Null(p.PickHeaderSize());
            Assert.Equal(50, p.AutoHeaderDnMm); // arrotondamento DN commerciale come prima
            AddSizes(p, new[] { 40.0, 0.0 }); // interno non leggibile: non è un candidato valido
            Assert.Equal(50, p.AutoHeaderDnMm);
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
        public void LeBasi_ChiedonoIFondelliAlleEstremita()
        {
            var r = PlanWithReturn(20, 20).ToParseResult();
            Assert.All(r.Plan.Runs, run => Assert.True(run.CapEnds));
            Assert.Contains(r.Notes, n => n.Contains("Enddeckel"));
            // il default di MepRun resta false: la modalità testuale non mette fondelli
            Assert.False(new MepRun().CapEnds);
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
        public void Ritorno_BaseAllineataStacchiInterlacciatiEScambiati()
        {
            var p = PlanWithReturn(20, 16, 25);
            p.SpacingMm = 150;
            p.ReturnOffsetMm = 300;
            var plan = p.ToParseResult().Plan;

            Assert.Equal(2, plan.Runs.Count);
            var first = plan.Runs[0];
            var second = plan.Runs[1];

            // clone perfetto: stessa base, stessa direzione, stesso tipo
            Assert.Equal(first.Size.DiameterMm, second.Size.DiameterMm);
            Assert.Equal(first.LengthMm, second.LengthMm);
            Assert.Equal(first.Direction, second.Direction);
            Assert.False(second.ContinuesPrevious);

            // basi perfettamente allineate: nessuna traslazione lungo l'asse, solo laterale
            // (a sinistra della direzione +X, cioè in +Y)
            Assert.Equal(0, second.OffsetAlongMm);
            Assert.Equal(300, second.OffsetSideMm);

            // stacchi identici (stessi DN, stesso ordine) su entrambi
            Assert.Equal(new double[] { 20, 16, 25 }, second.Branches.Select(b => b.Size.DiameterMm).ToArray());
            Assert.All(second.Branches, b => Assert.False(b.Connect));

            // collettori scambiati: il PRIMO porta gli stacchi sfasati (135, 285, 435),
            // il secondo quelli non sfasati (60, 210, 360); interlacciati a metà l'uno dell'altro
            var firstX = first.Branches.Select(b => b.PositionsMm[0]).ToArray();
            var secondX = second.Branches.Select(b => b.PositionsMm[0]).ToArray();
            Assert.Equal(new double[] { 135, 285, 435 }, firstX);
            Assert.Equal(new double[] { 60, 210, 360 }, secondX);
            Assert.Equal((secondX[0] + secondX[1]) / 2, firstX[0], 6);
            Assert.Equal((secondX[1] + secondX[2]) / 2, firstX[1], 6);
        }

        [Fact]
        public void ConRitorno_LeBasiSiAllunganoDiMezzoInterasse()
        {
            // Il vincolo dei 5 cm vale sul PRIMO stacco della mandata e sull'ULTIMO del ritorno
            // (mezzo interasse più avanti): le basi, identiche e allineate, si allungano di s/2.
            var solo = Plan(20, 20, 20);
            solo.SpacingMm = 150;
            var doppio = PlanWithReturn(20, 20, 20);
            doppio.SpacingMm = 150;
            Assert.Equal(solo.HeaderLengthMm + 75, doppio.HeaderLengthMm);
        }

        [Fact]
        public void ConRitorno_NessunoStaccoCadeFuoriDallaBase()
        {
            // Era il bug del troncamento: l'ultimo stacco di ritorno cadeva oltre la fine
            // della base e il builder lo scartava, lasciando il ritorno con uno stacco in meno.
            var p = PlanWithReturn(20, 16, 25, 20);
            p.SpacingMm = 150;
            var r = p.ToParseResult();
            var first = r.Plan.Runs[0];
            var second = r.Plan.Runs[1];

            Assert.Equal(first.Branches.Count, second.Branches.Count); // TUTTI replicati
            foreach (var run in r.Plan.Runs)
            {
                foreach (var b in run.Branches)
                {
                    Assert.InRange(b.PositionsMm[0], 1, run.LengthMm - 1);
                }
            }
            // 5 cm esatti sul primo stacco di UN collettore (il secondo, non sfasato)...
            Assert.Equal(50, second.Branches[0].PositionsMm[0] - 20 / 2.0);
            // ...e sull'ultimo stacco dell'ALTRO (il primo, sfasato)
            var lastFirst = first.Branches[first.Branches.Count - 1];
            Assert.Equal(50, first.LengthMm - lastFirst.PositionsMm[0] - 20 / 2.0, 6);
            Assert.DoesNotContain(r.Warnings, w => w.Contains("oltre la fine della base"));
        }

        [Fact]
        public void Ritorno_Disattivato_UnSoloTratto()
        {
            var plan = Plan(20, 16).ToParseResult().Plan;
            Assert.Single(plan.Runs);
        }

        [Fact]
        public void Ritorno_DistanzaNonValida_NonSiPuoCostruire()
        {
            var p = PlanWithReturn(20);
            p.ReturnOffsetMm = 0;
            Assert.False(p.ToParseResult().Success);
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
