using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    /// <summary>Ritegno sul bypass: Giacomini R60 (filettata) fino a DN32, KSB BOA-RVK (wafer tra flange) da DN40.</summary>
    public class ManifoldCheckValveFamilyTests
    {
        private const string R60 = "Giacomini_R60_RFA_2027_LevelBased";
        private const string Rvk = "boa-rvkwithmaterialnumber-bimdata48860607dn80lod-200b11a 11 48860607_pn6";

        private static CatalogFamily Family(string name, params string[] types)
        {
            var f = new CatalogFamily { Name = name };
            f.TypeNames.AddRange(types);
            return f;
        }

        private static ManifoldPlan Plan()
        {
            var p = new ManifoldPlan();
            p.AccessoryFamilies.Add(Family(R60, "R60Y002", "R60Y003", "R60Y004", "R60Y005", "R60Y006", "R60Y007", "R60Y008", "R60Y009", "R60Y010", "R60Y011"));
            p.AccessoryFamilies.Add(Family(Rvk, "DN40_PN6_48860604", "DN50_PN6_48860605", "DN65_PN6_48860606", "DN80_PN6_48860607"));
            p.CheckValveMap.Default = R60;
            p.CheckValveMap.Rules.Add(new FamilyRule(40, Rvk));
            return p;
        }

        [Fact]
        public void Registro_PredefinitoR60_PoiRvkDaDn40()
        {
            var e = ManifoldElements.Get(ManifoldElements.CheckValve);
            Assert.Equal("r60", e.Hints[0]);
            Assert.True(e.WithFlanges);
            Assert.True(e.MountsWithoutFlanges(R60));
            Assert.False(e.MountsWithoutFlanges(Rvk));
            Assert.False(e.FlangesFor(R60));
            Assert.True(e.FlangesFor(Rvk));
            var t = Assert.Single(e.DefaultThresholds);
            Assert.Equal(40, t.FromDnMm);
            Assert.Contains("rvk", t.Hints);
        }

        [Fact]
        public void FinoADn32_R60FilettataSenzaFlange_TipoDalCodice()
        {
            var p = Plan();
            var dn10 = p.CheckValveFor(10);
            Assert.Equal(R60, dn10.FamilyName);
            Assert.Equal("R60Y002", dn10.TypeName);
            Assert.False(dn10.WithFlanges);
            Assert.Equal("R60Y005", p.CheckValveFor(25).TypeName);
            Assert.Equal("R60Y006", p.CheckValveFor(32).TypeName);
            Assert.Equal(ValveKind.CheckValve, dn10.Kind);
        }

        [Fact]
        public void DaDn40_BoaRvkTraDueFlange()
        {
            var p = Plan();
            var dn40 = p.CheckValveFor(40);
            Assert.Equal(Rvk, dn40.FamilyName);
            Assert.Equal("DN40_PN6_48860604", dn40.TypeName);
            Assert.True(dn40.WithFlanges);
            Assert.Equal("DN50_PN6_48860605", p.CheckValveFor(50).TypeName);
        }

        [Fact]
        public void LaSogliaLaDecideLUtente()
        {
            var p = Plan();
            p.CheckValveMap.Rules.Clear();
            p.CheckValveMap.Rules.Add(new FamilyRule(65, Rvk));
            Assert.Equal("R60Y008", p.CheckValveFor(50).TypeName); // R60 anche a DN50
            Assert.Equal(Rvk, p.CheckValveFor(65).FamilyName);
            Assert.Equal("Giacomini_R60_RFA_2027_LevelBased|65=" + Rvk, p.CheckValveMap.ToString());
            Assert.Contains(p.ConfiguredFamilies(), f => f == R60);
            Assert.Contains(p.ConfiguredFamilies(), f => f == Rvk);
        }
    }
}
