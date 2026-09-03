using SayRevit.Core.Model;
using SayRevit.Core.Parsing;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class RuleBasedParserEdgeCaseTests
    {
        private static ParseResult P(string text)
        {
            var r = new RuleBasedParser().Parse(text, ModelCatalog.Empty());
            Assert.True(r.Success, r.Error);
            return r;
        }

        [Fact]
        public void CondottaAriaEUnCanale()
        {
            var r = P("condotta aria di ripresa 500x300 lunga 4 m");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(MepKind.Duct, run.Kind);
            Assert.Equal(SystemClass.ReturnAir, run.SystemClass);
        }

        [Fact]
        public void DiametroInMillimetriSenzaParolaChiave()
        {
            var r = P("tubazione 110 mm lunga 2 m");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(110, run.Size.DiameterMm);
            Assert.Equal(2000, run.LengthMm);
        }

        [Fact]
        public void StacchiLunghezzaInMillimetri()
        {
            var r = P("tubazione DN50 lunga 3 m con 2 stacchi DN20 lunghi 600 mm");
            var b = Assert.Single(Assert.Single(r.Plan.Runs).Branches);
            Assert.Equal(600, b.LengthMm);
            Assert.Equal(20, b.Size.DiameterMm);
        }

        [Fact]
        public void StacchiOgniMetroSenzaNumero()
        {
            var r = P("tubazione DN100 lunga 5 m con stacchi DN15 ogni metro");
            var b = Assert.Single(Assert.Single(r.Plan.Runs).Branches);
            Assert.Equal(1000, b.SpacingMm);
        }

        [Fact]
        public void LivelloConNomeComposto()
        {
            var r = P("tubazione DN80 al piano terra lunga 3 m");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal("terra", run.LevelHint);
        }

        [Fact]
        public void QuotaDaTerra()
        {
            var r = P("tubazione DN32 a 3 m da terra lunga 4 m");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(3000, run.ElevationMm);
            Assert.Equal(4000, run.LengthMm);
        }

        [Fact]
        public void DirezioneTrattoPrincipale()
        {
            var r = P("tubazione DN50 lunga 3 m verso nord");
            Assert.Equal(DirectionKind.PlusY, Assert.Single(r.Plan.Runs).Direction);
            r = P("tubazione DN50 lunga 3 m verso il basso");
            Assert.Equal(DirectionKind.Down, Assert.Single(r.Plan.Runs).Direction);
        }

        [Fact]
        public void TrattoSeparatoNonProsegue()
        {
            var r = P("tubazione DN50 lunga 3 m; un'altra tubazione DN25 lunga 2 m");
            Assert.Equal(2, r.Plan.Runs.Count);
            Assert.False(r.Plan.Runs[1].ContinuesPrevious);
            Assert.Equal(25, r.Plan.Runs[1].Size.DiameterMm);
        }

        [Fact]
        public void SistemaAerailicoSuTubazioneVieneScartato()
        {
            var r = P("tubazione DN50 aria di mandata lunga 3 m");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(MepKind.Pipe, run.Kind);
            Assert.Null(run.SystemClass);
            Assert.Contains(r.Warnings, w => w.Contains("aeraulico"));
        }

        [Fact]
        public void PolliciConFrazioni()
        {
            var r = P("pipe 1 1/2\" 20 ft long with 2 branches 3/4\"");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(40, run.Size.DiameterMm);
            Assert.Equal(20, Assert.Single(run.Branches).Size.DiameterMm);
        }

        [Fact]
        public void Riscaldamento()
        {
            var r = P("tubo rame DN22 mandata riscaldamento lungo 5 m con 3 stacchi DN15 a destra");
            var run = Assert.Single(r.Plan.Runs);
            Assert.Equal(SystemClass.SupplyHydronic, run.SystemClass);
            Assert.Contains("rame", run.TypeHints);
            Assert.Equal(DirectionKind.Right, run.Branches[0].Direction);
        }
    }
}
