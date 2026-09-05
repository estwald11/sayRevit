using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    /// <summary>
    /// Registro degli elementi: è l'unico elenco; da lì derivano mappe, tipi, impostazioni e note.
    /// E la catena in una riga (DescribeChain), il contratto tra foto, codice e Revit.
    /// </summary>
    public class ManifoldElementsTests
    {
        private static CatalogFamily Family(string name, params string[] types)
        {
            var f = new CatalogFamily { Name = name };
            f.TypeNames.AddRange(types);
            return f;
        }

        [Fact]
        public void Registro_ChiaviUnicheECompleto()
        {
            var keys = ManifoldElements.All.Select(e => e.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.Equal(keys.Count, ManifoldElements.All.Select(e => e.SettingsKey).Distinct().Count());
            Assert.Equal(keys.Count, ManifoldElements.All.Select(e => e.Kind).Distinct().Count());
            Assert.All(ManifoldElements.All, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Label));
                Assert.False(string.IsNullOrWhiteSpace(e.ShortLabel));
                Assert.False(string.IsNullOrWhiteSpace(e.UiLabel));
                Assert.StartsWith("Manifold", e.SettingsKey);
                Assert.EndsWith("Family", e.SettingsKey);
            });
            Assert.Equal(2, ManifoldElements.In(ElementSection.Shutoff).Count());
            Assert.Same(ManifoldElements.Get("zone"), ManifoldElements.BySettingsKey("ManifoldZoneValveFamily"));
            Assert.Same(ManifoldElements.Get(ManifoldElements.Strainer), ManifoldElements.ByKind(ValveKind.Strainer));
            Assert.Null(ManifoldElements.BySettingsKey("ManifoldSpacingMm"));
        }

        [Fact]
        public void IlPiano_HaUnaMappaEUnaListaTipiPerOgniElemento()
        {
            var p = new ManifoldPlan();
            foreach (var e in ManifoldElements.All)
            {
                Assert.True(p.FamilyMaps.ContainsKey(e.Key));
                Assert.True(p.ElementTypes.ContainsKey(e.Key));
            }
            // le scorciatoie storiche puntano alle stesse istanze del dizionario
            p.ZoneValveFamily = "Zona";
            Assert.Equal("Zona", p.FamilyMap(ManifoldElements.ZoneValve).Default);
            p.BallValveTypes.Add("1\" Lever");
            Assert.Single(p.ElementTypes[ManifoldElements.Ball]);
            Assert.Equal("ductile", p.ZoneValveTypeWord);
        }

        [Fact]
        public void PieceFor_UsaKindFlangeEParolaDelRegistro()
        {
            var p = new ManifoldPlan { ValvePnBar = 16, Mix2RollDegrees = 180 };
            p.AccessoryFamilies.Add(Family("Zona", "DN50 - Cast Iron", "DN50 - Ductile Iron"));
            p.AccessoryFamilies.Add(Family("Filtro", "DN50"));
            p.FamilyMap(ManifoldElements.ZoneValve).Default = "Zona";
            p.FamilyMap(ManifoldElements.Strainer).Default = "Filtro";

            var zone = p.PieceFor(ManifoldElements.ZoneValve, 50);
            Assert.Equal(ValveKind.ZoneValve, zone.Kind);
            Assert.True(zone.WithFlanges);
            Assert.Equal("DN50 - Ductile Iron", zone.TypeName);
            Assert.Equal(180, zone.RollDegrees);

            var strainer = p.PieceFor(ManifoldElements.Strainer, 50);
            Assert.Equal(ValveKind.Strainer, strainer.Kind);
            Assert.True(strainer.WithFlanges); // il filtro a Y va tra due flange, salvo famiglie filettate o con flange proprie (NoFlangeHints)
            Assert.Null(p.PieceFor(ManifoldElements.CheckValve, 50)); // senza famiglia
        }

        [Fact]
        public void DescribeChain_LaCatenaInUnaRiga()
        {
            var p = new ManifoldPlan
            {
                WithReturn = true,
                CircuitDirection = DirectionKind.Up,
                BallValveFamily = "Sfera",
                ButterflyValveFamily = "boax-s",
                ZoneValveFamily = "Zona",
                StrainerFamily = "Filtro",
                CheckValveFamily = "Ritegno",
                ValveDistanceMm = 150
            };
            p.BallValveTypes.AddRange(new[] { "1\" Lever" });
            p.ButterflyValveTypes.AddRange(new[] { "DN40_PN16", "DN50_PN16" });
            p.ZoneValveTypes.AddRange(new[] { "DN40 - Ductile Iron", "DN50 - Ductile Iron" });
            p.StrainerTypes.AddRange(new[] { "DN40", "DN50" });
            p.AccessoryFamilies.Add(Family("ev050r2+bac_(1)", "EV050R2+BAC"));
            var c = new ManifoldCircuit(50, CircuitKind.MixTwoWayInjection) { DnAfterBypassMm = 40 };
            p.Circuits.Add(c);

            Assert.Equal(
                "collettore → [boax DN50*] — 150 — [T →DN40] — 150 — (spazio riservato alla pompa 400) — 150 — [zona DN40*] — 50 — [boax DN40*] — 100 fine",
                p.DescribeChain(c, true));

            var ret = p.DescribeChain(c, false);
            Assert.StartsWith("collettore → [boax DN50*] — 150 — [energy valve DN50] — (tratto rettilineo 5×Øint sopra la energy valve 250) — [T →DN40] — 150 — [filtro Y DN40*] — 50 — [zona DN40*] — 50 — [boax DN40*] — 100 fine", ret);

            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n.StartsWith("C1 mandata: collettore → [boax DN50*]"));
            Assert.Contains(r.Notes, n => n.StartsWith("C1 ritorno: collettore → [boax DN50*] — 150 — [energy valve DN50]"));
        }
    }
}
