using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    public class ValveTypeMatcherTests
    {
        /// <summary>Nomi dei tipi della famiglia boax, come compaiono nel progetto.</summary>
        private static readonly string[] Boax =
        {
            "DN20_PN6_48013978", "DN20_PN10_48013978", "DN20_PN16_48013978",
            "DN25_PN6_48013978", "DN25_PN10_48013978", "DN25_PN16_48013978",
            "DN32_PN6_48013979", "DN32_PN10_48013979", "DN32_PN16_48013979",
            "DN40_PN6_48013980", "DN40_PN10_48013980", "DN40_PN16_48013980"
        };

        /// <summary>Nomi dei tipi della famiglia "Valvola a sfera": misure in pollici.</summary>
        private static readonly string[] Ball =
        {
            "1 1/2\" Lever", "1 1/4\" Lever", "1\" Lever", "1/2\" Butterfly", "2\" Lever", "3/4\" Butterfly"
        };

        [Theory]
        [InlineData("DN20_PN6_48013978", 20)]
        [InlineData("DN32_PN16_48013979", 32)]
        [InlineData("DN40_PN10_48013980", 40)]
        [InlineData("DN 65 PN16", 65)]
        public void DnMetrico_VieneLettoDalNome(string name, double expected)
        {
            Assert.Equal(expected, ValveTypeMatcher.DnFromTypeName(name));
        }

        [Theory]
        [InlineData("1/2\" Butterfly", 15)]
        [InlineData("3/4\" Butterfly", 20)]
        [InlineData("1\" Lever", 25)]
        [InlineData("1 1/4\" Lever", 32)]
        [InlineData("1 1/2\" Lever", 40)]
        [InlineData("2\" Lever", 50)]
        [InlineData("1-1/2\" Lever", 40)]
        public void MisuraInPollici_DiventaDn(string name, double expected)
        {
            Assert.Equal(expected, ValveTypeMatcher.DnFromTypeName(name));
        }

        [Theory]
        [InlineData("R60Y002", 10)]
        [InlineData("R60Y003", 15)]
        [InlineData("R60Y004", 20)]
        [InlineData("R60Y005", 25)]
        [InlineData("R60Y006", 32)]
        [InlineData("R60Y007", 40)]
        [InlineData("R60Y011", 100)]
        [InlineData("r60y008", 50)]
        public void CodiciGiacominiR60_ContanoLungoLaSerieDn(string name, double expected)
        {
            Assert.Equal(expected, ValveTypeMatcher.DnFromTypeName(name));
        }

        [Fact]
        public void CodiceArticolo_NonVieneScambiatoPerUnaMisura()
        {
            Assert.Null(ValveTypeMatcher.DnFromTypeName("48013978"));
            Assert.Null(ValveTypeMatcher.DnFromTypeName("Standard"));
        }

        [Fact]
        public void Pn_VieneLettoSoloSeDichiarato()
        {
            Assert.Equal(6, ValveTypeMatcher.PnFromTypeName("DN40_PN6_48013980"));
            Assert.Null(ValveTypeMatcher.PnFromTypeName("1 1/2\" Lever"));
        }

        [Fact]
        public void PnDisponibili_SonoQuelliDeiNomi()
        {
            Assert.Equal(new double[] { 6, 10, 16 }, ValveTypeMatcher.AvailablePn(Boax));
            Assert.Empty(ValveTypeMatcher.AvailablePn(Ball));
        }

        [Fact]
        public void Boax_SceglieDnEPnRichiesti()
        {
            var pick = ValveTypeMatcher.Pick(Boax, 40, 16);
            Assert.Equal("DN40_PN16_48013980", pick.TypeName);
            Assert.True(pick.ExactDn);
            Assert.True(pick.ExactPn);
        }

        [Fact]
        public void Boax_ConPnDiverso_CambiaSoloIlPn()
        {
            Assert.Equal("DN32_PN6_48013979", ValveTypeMatcher.Pick(Boax, 32, 6).TypeName);
            Assert.Equal("DN32_PN10_48013979", ValveTypeMatcher.Pick(Boax, 32, 10).TypeName);
        }

        [Fact]
        public void DnMancante_UsaIlPiuVicinoESegnala()
        {
            var pick = ValveTypeMatcher.Pick(Boax, 50, 16);
            Assert.Equal("DN40_PN16_48013980", pick.TypeName);
            Assert.False(pick.ExactDn);
        }

        [Fact]
        public void PnMancante_UsaIlPiuVicinoESegnala()
        {
            var pick = ValveTypeMatcher.Pick(new[] { "DN40_PN6_48013980", "DN40_PN10_48013980" }, 40, 16);
            Assert.Equal("DN40_PN10_48013980", pick.TypeName);
            Assert.True(pick.ExactDn);
            Assert.False(pick.ExactPn);
        }

        [Fact]
        public void Sfera_SceglieSulDnAncheConINomiInPollici()
        {
            var pick = ValveTypeMatcher.Pick(Ball, 25, 16);
            Assert.Equal("1\" Lever", pick.TypeName);
            Assert.True(pick.ExactDn);
            // La famiglia non distingue il PN: il PN richiesto non deve far scattare un avviso.
            Assert.True(pick.ExactPn);
        }

        [Fact]
        public void NomiSenzaMisura_NonDannoNessunTipo()
        {
            Assert.Null(ValveTypeMatcher.Pick(new[] { "Standard", "48013978" }, 40, 16));
            Assert.Null(ValveTypeMatcher.Pick(Boax, 0, 16));
        }
    }
}
