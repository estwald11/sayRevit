using System.Linq;
using SayRevit.Core.Model;
using SayRevit.Core.Parsing;
using Xunit;

namespace SayRevit.Core.Tests
{
    /// <summary>Filtro a Y: IMI TA-STR filettato fino a DN32, VIR 895 wafer tra due flange da DN40.</summary>
    public class ManifoldStrainerFamilyTests
    {
        private const string Threaded = "IMI_TA-STR_RFA_2027_LevelBased";
        private const string Flanged = "VIR_895_RFA_2027_LevelBased";
        private const string OwnFlanges = "IMI_TA-STR_F_RFA_2027_LevelBased";

        private static CatalogFamily Family(string name, params string[] types)
        {
            var f = new CatalogFamily { Name = name };
            f.TypeNames.AddRange(types);
            return f;
        }

        private static ManifoldPlan Plan()
        {
            var p = new ManifoldPlan();
            p.AccessoryFamilies.Add(Family(Threaded, "43250-000315 DN15", "43250-000320 DN20", "43250-000325 DN25", "43250-000332 DN32", "43250-000340 DN40", "43250-000350 DN50"));
            p.AccessoryFamilies.Add(Family(Flanged, "895 DN32", "895 DN40", "895 DN50", "895 DN65", "895 DN80", "895 DN100"));
            p.AccessoryFamilies.Add(Family(OwnFlanges, "43250-036250 DN50", "43250-036265 DN65"));
            p.StrainerMap.Default = Threaded;
            p.StrainerMap.Rules.Add(new FamilyRule(40, Flanged));
            return p;
        }

        [Fact]
        public void Registro_PropostaTaStr_PoiVir895DaDn40()
        {
            var e = ManifoldElements.Get(ManifoldElements.Strainer);
            // la parola della base prende solo il filettato, quella della soglia solo il flangiato
            Assert.Contains(e.Hints[0], TextUtil.Fold(Threaded));
            Assert.DoesNotContain(e.Hints[0], TextUtil.Fold(Flanged));
            var t = Assert.Single(e.DefaultThresholds);
            Assert.Equal(40, t.FromDnMm);
            Assert.Contains(TextUtil.Fold(t.Hints[0]), TextUtil.Fold(Flanged));
            Assert.DoesNotContain(TextUtil.Fold(t.Hints[0]), TextUtil.Fold(Threaded));
            // VIR 895 tra due flange automatiche; filettata e famiglie con flange proprie senza
            Assert.True(e.FlangesFor(Flanged));
            Assert.False(e.FlangesFor(Threaded));
            Assert.False(e.FlangesFor(OwnFlanges));
            Assert.False(e.FlangesFor("Watts_Y33P_StrainerWithDrainCock"));
        }

        [Fact]
        public void FinoADn32_Filettato_DaDn40_Vir895TraFlange_TipoDalDn()
        {
            var p = Plan();
            var dn15 = p.StrainerFor(15);
            Assert.Equal(Threaded, dn15.FamilyName);
            Assert.Equal("43250-000315 DN15", dn15.TypeName);
            Assert.False(dn15.WithFlanges);
            Assert.Equal("43250-000332 DN32", p.StrainerFor(32).TypeName);
            var dn40 = p.StrainerFor(40);
            Assert.Equal(Flanged, dn40.FamilyName);
            Assert.Equal("895 DN40", dn40.TypeName);
            Assert.True(dn40.WithFlanges);
            var dn50 = p.StrainerFor(50);
            Assert.Equal("895 DN50", dn50.TypeName);
            Assert.True(dn50.WithFlanges);
            Assert.Equal("895 DN80", p.StrainerFor(80).TypeName);
            Assert.Equal(p.StrainerRollDegrees, dn50.RollDegrees);
            Assert.Equal(p.StrainerReversed, dn50.Reversed);
            Assert.Equal(Threaded + "|40=" + Flanged, p.StrainerMap.ToString());

            // famiglia con le flange proprie scelta a mano: nessuna flangia automatica
            p.StrainerMap.Rules.Clear();
            p.StrainerMap.Rules.Add(new FamilyRule(50, OwnFlanges));
            Assert.False(p.StrainerFor(50).WithFlanges);
        }
    }
}
