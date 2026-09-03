using System.Linq;
using SayRevit.Core.Model;
using SayRevit.Core.Parsing;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class RuleBasedParserTests
    {
        private static ParseResult P(string text)
        {
            var r = new RuleBasedParser().Parse(text, ModelCatalog.Empty());
            Assert.True(r.Success, r.Error);
            return r;
        }

        [Fact]
        public void EsempioUtente_TubazioneDn200ConStacchiDn15()
        {
            var r = P("una tubazione DN200 con degli stacchi DN15");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(MepKind.Pipe, run.Kind);
            Assert.Equal(200, run.Size.DiameterMm);
            Assert.True(run.Size.IsNominalDn);
            Assert.Equal(3000, run.LengthMm);
            var b = Assert.Single(run.Branches);
            Assert.Equal(15, b.Size.DiameterMm);
            Assert.Equal(2, b.Count);
            Assert.Contains(r.Notes, n => n.Contains("non specificato"));
        }

        [Fact]
        public void LunghezzaContoStacchiInterasseDirezione()
        {
            var r = P("Una tubazione DN100 lunga 10 m con 3 stacchi DN20 ogni 2 m verso l'alto");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(10000, run.LengthMm);
            var b = Assert.Single(run.Branches);
            Assert.Equal(3, b.Count);
            Assert.Equal(20, b.Size.DiameterMm);
            Assert.Equal(2000, b.SpacingMm);
            Assert.Equal(DirectionKind.Up, b.Direction);
        }

        [Fact]
        public void CanaleRettangolareConStacchiLaterali()
        {
            var r = P("un canale 400x200 di 6 metri con due stacchi 200x200 laterali lunghi 80 cm");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(MepKind.Duct, run.Kind);
            Assert.Equal(SizeShape.Rectangular, run.Size.Shape);
            Assert.Equal(400, run.Size.WidthMm);
            Assert.Equal(200, run.Size.HeightMm);
            Assert.Equal(6000, run.LengthMm);
            var b = Assert.Single(run.Branches);
            Assert.Equal(2, b.Count);
            Assert.Equal(200, b.Size.WidthMm);
            Assert.Equal(800, b.LengthMm);
            Assert.Equal(DirectionKind.Left, b.Direction);
        }

        [Fact]
        public void CanaleCircolareDiametro()
        {
            var r = P("canale circolare diametro 315 mm lungo 5 m aria di mandata");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(MepKind.Duct, run.Kind);
            Assert.Equal(SizeShape.Round, run.Size.Shape);
            Assert.Equal(315, run.Size.DiameterMm);
            Assert.Equal(5000, run.LengthMm);
            Assert.Equal(SystemClass.SupplyAir, run.SystemClass);
        }

        [Fact]
        public void MaterialeSistemaLivelloQuota()
        {
            var r = P("tubo in acciaio zincato DN50 acqua calda sanitaria al livello 1 a quota 2,8 m lungo 4 m");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Contains("zincat", run.TypeHints);
            Assert.Equal(SystemClass.DomesticHotWater, run.SystemClass);
            Assert.Equal("1", run.LevelHint);
            Assert.Equal(2800, run.ElevationMm);
            Assert.Equal(4000, run.LengthMm);
            Assert.Equal(50, run.Size.DiameterMm);
        }

        [Fact]
        public void TipoEsplicito()
        {
            var r = P("tubazione tipo \"PVC-U scarico\" diametro 110 lunga 3 m scarico");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal("PVC-U scarico", run.ExplicitTypeName);
            Assert.Equal(110, run.Size.DiameterMm);
            Assert.Equal(SystemClass.Sanitary, run.SystemClass);
        }

        [Fact]
        public void Inglese()
        {
            var r = P("a 6 inch steel pipe 30 ft long with 4 branches 1/2\" every 5 ft going up, chilled water supply");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(MepKind.Pipe, run.Kind);
            Assert.Equal(150, run.Size.DiameterMm);
            Assert.Equal(30 * 304.8, run.LengthMm, 3);
            Assert.Contains("steel", run.TypeHints);
            Assert.Equal(SystemClass.SupplyHydronic, run.SystemClass);
            var b = Assert.Single(run.Branches);
            Assert.Equal(4, b.Count);
            Assert.Equal(15, b.Size.DiameterMm);
            Assert.Equal(5 * 304.8, b.SpacingMm.Value, 3);
            Assert.Equal(DirectionKind.Up, b.Direction);
        }

        [Fact]
        public void IngleseDuct()
        {
            var r = P("a 600x300 supply air duct 8 m long with two 200x200 taps on the left");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(MepKind.Duct, run.Kind);
            Assert.Equal(600, run.Size.WidthMm);
            Assert.Equal(8000, run.LengthMm);
            Assert.Equal(SystemClass.SupplyAir, run.SystemClass);
            var b = Assert.Single(run.Branches);
            Assert.Equal(2, b.Count);
            Assert.Equal(DirectionKind.Left, b.Direction);
        }

        [Fact]
        public void TrattiConcatenati()
        {
            var r = P("tubazione DN80 lunga 5 m; poi verso l'alto per 2 m; poi DN65 lungo x per 3 m");
            Assert.Equal(3, r.Plan.Runs.Count);
            Assert.False(r.Plan.Runs[0].ContinuesPrevious);
            Assert.True(r.Plan.Runs[1].ContinuesPrevious);
            Assert.Equal(80, r.Plan.Runs[1].Size.DiameterMm);
            Assert.Equal(DirectionKind.Up, r.Plan.Runs[1].Direction);
            Assert.Equal(2000, r.Plan.Runs[1].LengthMm);
            Assert.Equal(65, r.Plan.Runs[2].Size.DiameterMm);
            Assert.Equal(DirectionKind.PlusX, r.Plan.Runs[2].Direction);
            Assert.Equal(3000, r.Plan.Runs[2].LengthMm);
        }

        [Fact]
        public void PiuStacchiDiversi()
        {
            var r = P("tubazione DN100 lunga 8 m con 2 stacchi DN25 verso il basso e uno stacco DN40 a destra lungo 1 m");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(2, run.Branches.Count);
            Assert.Equal(2, run.Branches[0].Count);
            Assert.Equal(25, run.Branches[0].Size.DiameterMm);
            Assert.Equal(DirectionKind.Down, run.Branches[0].Direction);
            Assert.Equal(1, run.Branches[1].Count);
            Assert.Equal(40, run.Branches[1].Size.DiameterMm);
            Assert.Equal(DirectionKind.Right, run.Branches[1].Direction);
            Assert.Equal(1000, run.Branches[1].LengthMm);
        }

        [Fact]
        public void PosizioniEsplicite()
        {
            var r = P("tubazione DN50 lunga 6 m con stacchi DN15 a 1, 2.5 e 4 m dall'inizio");
            var b = Assert.Single(Assert.Single(r.Plan.Runs).Branches);
            Assert.Equal(new[] { 1000.0, 2500.0, 4000.0 }, b.PositionsMm);
            Assert.Equal(3, b.Count);
        }

        [Fact]
        public void DimensioneMancanteProduceAvviso()
        {
            var r = P("una tubazione con 3 stacchi");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(50, run.Size.DiameterMm);
            Assert.Contains(r.Warnings, w => w.Contains("dimensione non indicata"));
            Assert.Equal(3, run.Branches[0].Count);
            Assert.Equal(50, run.Branches[0].Size.DiameterMm);
        }

        [Fact]
        public void TestoVuotoFallisce()
        {
            var r = new RuleBasedParser().Parse("   ", ModelCatalog.Empty());
            Assert.False(r.Success);
        }

        [Fact]
        public void DiametroSenzaDnDopoDa()
        {
            var r = P("tubazione da 160 in pvc scarico lunga 12 metri");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(160, run.Size.DiameterMm);
            Assert.False(run.Size.IsNominalDn);
            Assert.Equal(12000, run.LengthMm);
            Assert.Contains("pvc", run.TypeHints);
        }

        [Fact]
        public void StaccoSingolare()
        {
            var r = P("tubazione DN40 lunga 2 m con uno stacco DN20 lungo 30 cm");
            var b = Assert.Single(Assert.Single(r.Plan.Runs).Branches);
            Assert.Equal(1, b.Count);
            Assert.Equal(300, b.LengthMm);
        }

        [Fact]
        public void NumeroInParole()
        {
            var r = P("tubazione DN100 lunga 10 m con cinque stacchi DN15 alternati");
            var b = Assert.Single(Assert.Single(r.Plan.Runs).Branches);
            Assert.Equal(5, b.Count);
            Assert.Equal(DirectionKind.Alternate, b.Direction);
        }

        [Fact]
        public void FormatterProduceTesto()
        {
            var r = P("una tubazione DN200 con degli stacchi DN15");
            var txt = PlanFormatter.Describe(r);
            Assert.Contains("DN200", txt);
            Assert.Contains("2 stacchi DN15", txt);
        }
    }
}
