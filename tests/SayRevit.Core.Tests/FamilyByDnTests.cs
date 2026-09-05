using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    /// <summary>
    /// Famiglia per diametro: di base una sola famiglia per elemento, a scelta soglie
    /// "da DN x in su usa y"; salvataggio come testo e uso nel piano del collettore.
    /// </summary>
    public class FamilyByDnTests
    {
        private static CatalogFamily Family(string name, params string[] types)
        {
            var f = new CatalogFamily { Name = name };
            f.TypeNames.AddRange(types);
            return f;
        }

        [Fact]
        public void SenzaSoglie_UnaFamigliaPerTuttiIDn()
        {
            var map = new FamilyByDn("Valvola a sfera");
            Assert.False(map.HasRules);
            Assert.False(map.IsEmpty);
            Assert.Equal("Valvola a sfera", map.Resolve(15));
            Assert.Equal("Valvola a sfera", map.Resolve(300));
            Assert.Equal("Valvola a sfera", map.ToString());
        }

        [Fact]
        public void Soglie_DaDnInSu_FinoAllaSuccessiva()
        {
            var map = new FamilyByDn("A");
            map.Rules.Add(new FamilyRule(100, null)); // da DN100 nessun pezzo
            map.Rules.Add(new FamilyRule(40, "B"));
            Assert.Equal("A", map.Resolve(32));
            Assert.Equal("B", map.Resolve(40));
            Assert.Equal("B", map.Resolve(80));
            Assert.Null(map.Resolve(100));
            Assert.Null(map.Resolve(150));
            Assert.False(map.IsEmpty);
            Assert.Equal(new[] { "A", "B" }, map.Families());
            Assert.Equal("\"A\"; da DN40: \"B\"; da DN100: nessuna", map.Describe());
        }

        [Fact]
        public void Salvataggio_AndataERitorno_ELetturaDelFormatoVecchio()
        {
            var map = new FamilyByDn("fam A");
            map.Rules.Add(new FamilyRule(40, "fam B"));
            map.Rules.Add(new FamilyRule(100, string.Empty));
            var text = map.ToString();
            Assert.Equal("fam A|40=fam B|100=", text);

            var back = FamilyByDn.Parse(text);
            Assert.Equal("fam A", back.Default);
            Assert.Equal(2, back.Rules.Count);
            Assert.Equal(40, back.Rules[0].FromDnMm);
            Assert.Equal("fam B", back.Rules[0].Family);
            Assert.False(back.Rules[1].HasFamily);

            // il formato vecchio (solo il nome) è la sola famiglia di base
            var old = FamilyByDn.Parse("boax-s");
            Assert.Equal("boax-s", old.Default);
            Assert.False(old.HasRules);
            Assert.True(FamilyByDn.Parse(string.Empty).IsEmpty);
            Assert.True(FamilyByDn.Parse("|40=").IsEmpty);
        }

        [Fact]
        public void Intercettazione_FamigliaDiversaPerFasciaDiDn()
        {
            var p = new ManifoldPlan { WithReturn = false, BallValveFamily = "Sfera piccola", ButterflyValveFamily = "boax-s" };
            p.BallValveTypes.AddRange(new[] { "1/2\" Lever", "3/4\" Lever", "1\" Lever" });
            p.ButterflyValveTypes.AddRange(new[] { "DN40_PN16", "DN50_PN16" });
            p.AccessoryFamilies.Add(Family("Sfera grande", "1 1/4\" Lever", "2\" Lever"));
            p.AccessoryFamilies.Add(Family("boax-h", "DN65_PN16", "DN80_PN16"));
            p.BallValveMap.Rules.Add(new FamilyRule(32, "Sfera grande"));
            p.ButterflyValveMap.Rules.Add(new FamilyRule(65, "boax-h"));

            Assert.Equal("Sfera piccola", p.ValveFor(20).FamilyName);
            Assert.Equal("Sfera grande", p.ValveFor(32).FamilyName);
            Assert.Equal("1 1/4\" Lever", p.ValveFor(32).TypeName); // tipi dal catalogo della famiglia della soglia
            Assert.Equal("boax-s", p.ValveFor(50).FamilyName);
            Assert.Equal("boax-h", p.ValveFor(80).FamilyName);
            Assert.Equal("DN80_PN16", p.ValveFor(80).TypeName);
            Assert.Equal(new[] { "Sfera piccola", "Sfera grande", "boax-s", "boax-h" }, p.ConfiguredFamilies());

            p.Circuits.Add(new ManifoldCircuit(20));
            p.Circuits.Add(new ManifoldCircuit(80));
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n == "Famiglia per DN (valvola a sfera): \"Sfera piccola\"; da DN32: \"Sfera grande\".");
            Assert.Contains(r.Notes, n => n == "Famiglia per DN (valvola boax): \"boax-s\"; da DN65: \"boax-h\".");
            Assert.Contains(r.Notes, n => n.StartsWith("DN80 → valvola boax \"DN80_PN16\""));
        }

        [Fact]
        public void SogliaSenzaFamiglia_IlPezzoNonSiMette()
        {
            var p = new ManifoldPlan { WithReturn = false, BallValveFamily = "Sfera", ButterflyValveFamily = "boax-s" };
            p.ButterflyValveTypes.AddRange(new[] { "DN40_PN16", "DN50_PN16" });
            p.ButterflyValveMap.Rules.Add(new FamilyRule(65, null));
            Assert.NotNull(p.ValveFor(50));
            Assert.Null(p.ValveFor(80));
            p.Circuits.Add(new ManifoldCircuit(80));
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n.Contains("DN80: nessuna famiglia per la valvola boax su questa misura"));
            Assert.DoesNotContain(r.Warnings, w => w.StartsWith("Nessuna famiglia scelta per la valvola boax"));
        }

        [Fact]
        public void AccessoriMix2_SogliePerDn()
        {
            var p = new ManifoldPlan
            {
                WithReturn = true,
                CircuitDirection = DirectionKind.Up,
                BallValveFamily = "Sfera",
                ButterflyValveFamily = "boax-s",
                ZoneValveFamily = "Zona piccola",
                StrainerFamily = "Filtro A",
                CheckValveFamily = "Ritegno"
            };
            p.BallValveTypes.AddRange(new[] { "1\" Lever" });
            p.ButterflyValveTypes.AddRange(new[] { "DN40_PN16", "DN50_PN16", "DN80_PN16" });
            p.ZoneValveTypes.AddRange(new[] { "DN25 - Cast Iron", "DN32 - Ductile Iron" });
            p.StrainerTypes.AddRange(new[] { "DN25", "DN32" });
            p.AccessoryFamilies.Add(Family("Zona grande", "DN50 - Ductile Iron", "DN80 - Ductile Iron"));
            p.AccessoryFamilies.Add(Family("Filtro B", "DN50", "DN80"));
            p.AccessoryFamilies.Add(Family("ev050r2+bac_(1)", "EV050R2+BAC"));
            p.AccessoryFamilies.Add(Family("ev080f+bac", "EV080F+BAC"));
            p.ZoneValveMap.Rules.Add(new FamilyRule(50, "Zona grande"));
            p.StrainerMap.Rules.Add(new FamilyRule(40, "Filtro B"));

            Assert.Equal("Zona piccola", p.ZoneValveFor(32).FamilyName);
            Assert.Equal("DN32 - Ductile Iron", p.ZoneValveFor(32).TypeName);
            Assert.Equal("Zona grande", p.ZoneValveFor(80).FamilyName);
            Assert.Equal("DN80 - Ductile Iron", p.ZoneValveFor(80).TypeName);
            Assert.Equal("Filtro B", p.StrainerFor(50).FamilyName);
            Assert.Equal("Ritegno", p.CheckValveFor(80).FamilyName);

            var c = new ManifoldCircuit(80, CircuitKind.MixTwoWayInjection);
            p.Circuits.Add(c);
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n == "Famiglia per DN (valvola di zona): \"Zona piccola\"; da DN50: \"Zona grande\".");
            Assert.Contains(r.Notes, n => n == "Famiglia per DN (filtro a Y): \"Filtro A\"; da DN40: \"Filtro B\".");
            Assert.Contains(r.Notes, n => n.StartsWith("DN80 → valvola di zona \"DN80 - Ductile Iron\""));
            Assert.Contains(p.ChainFor(c, false), i => i.Piece != null && i.Piece.Kind == ValveKind.Strainer && i.Piece.FamilyName == "Filtro B");
        }

        [Fact]
        public void EnergyValve_RigaPoiSogliaPoiAutomatica()
        {
            var p = new ManifoldPlan { WithReturn = true, BallValveFamily = "Sfera", ButterflyValveFamily = "boax-s" };
            p.ButterflyValveTypes.AddRange(new[] { "DN50_PN16", "DN80_PN16" });
            p.AccessoryFamilies.Add(Family("ev050r2+bac_(1)", "EV050R2+BAC"));
            p.AccessoryFamilies.Add(Family("ev050r2+mid_(1)", "EV050R2+MID"));
            p.AccessoryFamilies.Add(Family("ev080f+bac", "EV080F+BAC"));

            var c80 = new ManifoldCircuit(80, CircuitKind.MixTwoWayInjection);
            var c50 = new ManifoldCircuit(50, CircuitKind.MixTwoWayInjection);
            p.Circuits.Add(c80);
            p.Circuits.Add(c50);

            // senza soglie: automatica sul nome
            Assert.Equal("ev080f+bac", p.EnergyValveFor(c80).FamilyName);
            Assert.Equal("automatica sul DN", p.EnergyValveSourceOf(c80));

            // soglia: da DN65 in su usa la DN50 (con riduzioni), sotto resta automatica
            p.EnergyValveMap.Rules.Add(new FamilyRule(65, "ev050r2+mid_(1)"));
            Assert.Equal("ev050r2+mid_(1)", p.EnergyValveFor(c80).FamilyName);
            Assert.Equal("fissata per DN nelle impostazioni", p.EnergyValveSourceOf(c80));
            Assert.Equal("ev050r2+bac_(1)", p.EnergyValveFor(c50).FamilyName);
            Assert.Contains(p.ConfiguredFamilies(), f => f == "ev050r2+mid_(1)");

            // la riga vince sulla soglia
            c80.EnergyValveFamily = "ev080f+bac";
            Assert.Equal("ev080f+bac", p.EnergyValveFor(c80).FamilyName);
            Assert.Equal("scelta nella riga", p.EnergyValveSourceOf(c80));

            // soglia con famiglia non caricata: si torna all'automatica
            c80.EnergyValveFamily = null;
            p.EnergyValveMap.Rules.Clear();
            p.EnergyValveMap.Rules.Add(new FamilyRule(65, "famiglia inesistente"));
            Assert.Equal("ev080f+bac", p.EnergyValveFor(c80).FamilyName);

            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n.StartsWith("Famiglia per DN (energy valve): automatica sul DN; da DN65: \"famiglia inesistente\""));
        }

        [Fact]
        public void LaFamigliaDiBaseCambiata_ContinuaAUsareITipiPassatiAMano()
        {
            var p = new ManifoldPlan { WithReturn = false, BallValveFamily = "Sfera", ButterflyValveFamily = "boax-s" };
            p.ButterflyValveTypes.AddRange(new[] { "DN40_PN16", "DN50_PN16" });
            // il catalogo dice altro per la stessa famiglia: per la base vale la lista passata a mano
            p.AccessoryFamilies.Add(Family("boax-s", "DN65_PN16"));
            Assert.Equal("DN50_PN16", p.ValveFor(50).TypeName);
        }
    }
}
