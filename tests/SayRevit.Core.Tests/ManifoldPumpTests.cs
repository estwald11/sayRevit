using System.Collections.Generic;
using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    /// <summary>Pompa di circolazione: modello scelto nella riga, montata sulla mandata dopo il bypass.</summary>
    public class ManifoldPumpTests
    {
        private const string PumpFamily = "Grundfos_MAGNA3_RFA_2027_LevelBased";
        private const string ZoneFamily = "Watts_ButterflyValve_Sylax";
        private const string StrainerFamily = "Watts_Y33P_StrainerWithDrainCock";
        private const string CheckFamily = "boa-rvk";

        private static readonly string[] PumpTypes =
        {
            "MAGNA3 25-40 PN10 - 97924244", "MAGNA3 25-60 PN10 - 97924245", "MAGNA3 32-40 F PN6 - 98333834",
            "MAGNA3 32-60 PN10 - 97924255", "MAGNA3 40-80 F PN6 - 98333900", "MAGNA3 50-120 F PN6 - 98333950"
        };

        private static CatalogFamily Family(string name, params string[] types)
        {
            var f = new CatalogFamily { Name = name };
            f.TypeNames.AddRange(types);
            return f;
        }

        private static ManifoldPlan Plan(params ManifoldCircuit[] circuits)
        {
            var p = new ManifoldPlan
            {
                WithReturn = true,
                WithValves = true,
                BallValveFamily = "Valvola a sfera",
                ButterflyValveFamily = "boax-s",
                CircuitDirection = DirectionKind.Up,
                ZoneValveFamily = ZoneFamily,
                StrainerFamily = StrainerFamily,
                CheckValveFamily = CheckFamily,
                SpacingMm = 400,
                ReturnOffsetMm = 300
            };
            p.BallValveTypes.AddRange(new[] { "1\" Lever", "1 1/4\" Lever", "2\" Lever" });
            p.ButterflyValveTypes.AddRange(new[] { "DN40_PN16_48013980", "DN50_PN16_48013981", "DN65_PN16_48013982" });
            p.ZoneValveTypes.AddRange(new[] { "DN25 - Ductile Iron", "DN32 - Ductile Iron", "DN40 - Ductile Iron", "DN50 - Ductile Iron" });
            p.StrainerTypes.AddRange(new[] { "DN40", "DN50", "DN65" });
            p.CheckValveTypes.AddRange(new[] { "DN25_PN6_48860602", "DN32_PN6_48860603", "DN50_PN6_48860605" });
            p.AccessoryFamilies.Add(Family("ev050r2+bac_(1)", "EV050R2+BAC"));
            p.AccessoryFamilies.Add(Family("ev032r2+bac_(1)", "EV032R2+BAC"));
            p.AccessoryFamilies.Add(Family(PumpFamily, PumpTypes));
            p.FamilyMaps[ManifoldElements.Pump].Default = PumpFamily;
            foreach (var c in circuits) p.Circuits.Add(c);
            return p;
        }

        private static List<ValveKind> Kinds(IEnumerable<StubItem> chain)
        {
            return chain.Where(i => i.Kind == StubItemKind.Piece).Select(i => i.Piece.Kind).ToList();
        }

        [Fact]
        public void Registro_HaLaPompa_TraLeAttrezzatureMeccaniche()
        {
            var e = ManifoldElements.Get(ManifoldElements.Pump);
            Assert.Equal(ValveKind.Pump, e.Kind);
            Assert.True(e.FromMechanicalEquipment);
            Assert.Equal(ElementSection.Pump, e.Section);
            Assert.Equal("pompa", MepValve.KindLabelOf(ValveKind.Pump));
            Assert.Contains("magna", e.Hints);
        }

        [Fact]
        public void NomeDelModello_DnEFlange()
        {
            Assert.Equal(25, ValveTypeMatcher.DnFromTypeName("MAGNA3 25-60 PN10 - 97924245"));
            Assert.Equal(32, ValveTypeMatcher.DnFromTypeName("MAGNA3 32-40 F PN6 - 98333834"));
            Assert.Equal(10, ValveTypeMatcher.PnFromTypeName("MAGNA3 25-60 PN10 - 97924245"));
            Assert.True(ManifoldPlan.PumpTypeIsFlanged("MAGNA3 32-40 F PN6 - 98333834"));
            Assert.False(ManifoldPlan.PumpTypeIsFlanged("MAGNA3 25-60 PN10 - 97924245"));
            Assert.False(ManifoldPlan.PumpTypeIsFlanged("MAGNA3 25-60 FN10"));
        }

        [Fact]
        public void Diretto_ConModello_MontaLaPompaDopoLIntercettazione_SoloSullaMandata()
        {
            var p = Plan(new ManifoldCircuit(32, CircuitKind.Direct) { PumpType = "MAGNA3 32-60 PN10 - 97924255" });
            var c = p.Circuits[0];
            Assert.True(p.HasPumpChain(c));
            Assert.True(p.HasChain(c));
            Assert.Equal(PumpTypes, p.PumpTypeNames(32));

            var supply = p.ChainFor(c, true);
            Assert.Equal(new[] { ValveKind.Ball, ValveKind.Pump }, Kinds(supply));
            var pump = supply.Last().Piece;
            Assert.Equal(PumpFamily, pump.FamilyName);
            Assert.Equal("MAGNA3 32-60 PN10 - 97924255", pump.TypeName);
            Assert.Equal(32, pump.DnMm);
            Assert.False(pump.WithFlanges);
            Assert.Equal(StubItemKind.Gap, supply[1].Kind);
            Assert.True(supply[1].LengthMm >= ManifoldPlan.Mix2MinGapMm);
            Assert.Empty(p.ChainFor(c, false));

            var r = p.ToParseResult();
            Assert.True(r.Success, string.Join("; ", r.Warnings));
            var s = r.Plan.Runs[0].Branches[0];
            Assert.Equal(2, s.Chain.Count(i => i.Kind == StubItemKind.Piece));
            Assert.Null(s.Bypass);
            Assert.Null(s.LengthAfterValveMm);                       // il diretto tiene la lunghezza generica
            var ret = r.Plan.Runs[1].Branches[0];
            Assert.Empty(ret.Chain);                                  // ritorno: sola intercettazione
            Assert.NotNull(ret.Valve);
            Assert.Contains(r.Notes, n => n.StartsWith("C1 DN32 → pompa \"MAGNA3 32-60 PN10 - 97924255\" (famiglia \"" + PumpFamily + "\") sulla mandata dopo l'intercettazione, attacchi filettati"));
            Assert.DoesNotContain(r.Warnings, w => w.StartsWith("Tipologie:"));
            Assert.StartsWith("collettore → [sfera DN32] — 150 — [pompa DN32]", p.DescribeChain(c, true));
        }

        [Fact]
        public void Diretto_ConValvolaDiZona_DopoLaPompa()
        {
            var p = Plan(new ManifoldCircuit(32, CircuitKind.Direct) { PumpType = "MAGNA3 32-60 PN10 - 97924255", WithZoneValve = true });
            var supply = p.ChainFor(p.Circuits[0], true);
            Assert.Equal(new[] { ValveKind.Ball, ValveKind.Pump, ValveKind.ZoneValve }, Kinds(supply));
            var r = p.ToParseResult();
            Assert.DoesNotContain(r.Warnings, w => w.StartsWith("Tipologie:"));
        }

        [Fact]
        public void ModelloFlangiato_TraDueFlange_EDnDiverso_ConNota()
        {
            var p = Plan(new ManifoldCircuit(40, CircuitKind.Direct) { PumpType = "MAGNA3 32-40 F PN6 - 98333834" });
            var pump = p.PumpFor(p.Circuits[0]);
            Assert.True(pump.WithFlanges);
            Assert.Equal(40, pump.DnMm);
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n.Contains("pompa \"MAGNA3 32-40 F PN6 - 98333834\"") && n.Contains("tra due flange") && n.Contains("modello DN32 su stacco DN40"));
            Assert.Contains("[pompa DN40*]", p.DescribeChain(p.Circuits[0], true));
        }

        [Fact]
        public void Mix2_ConModello_LaPompaPrendeIlPostoDelloSpazioRiservato()
        {
            var p = Plan(new ManifoldCircuit(50, CircuitKind.MixTwoWayInjection) { DnAfterBypassMm = 32, PumpType = "MAGNA3 32-60 PN10 - 97924255" });
            var c = p.Circuits[0];
            var supply = p.ChainFor(c, true);
            Assert.Equal(new[] { ValveKind.Butterfly, ValveKind.Pump, ValveKind.ZoneValve, ValveKind.Ball }, Kinds(supply));
            var teeAt = supply.FindIndex(i => i.Kind == StubItemKind.Tee);
            var pumpAt = supply.FindIndex(i => i.Piece != null && i.Piece.Kind == ValveKind.Pump);
            Assert.True(teeAt < pumpAt, "la pompa sta dopo il T del bypass");
            Assert.Equal(32, supply[pumpAt].Piece.DnMm);            // DN dopo il bypass
            Assert.DoesNotContain(supply, i => i.Kind == StubItemKind.Gap && i.Label != null && i.Label.Contains("pompa"));
            Assert.NotNull(p.BypassFor(c, 0, true));
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n.StartsWith("C1 DN32 → pompa \"MAGNA3 32-60 PN10 - 97924255\"") && n.Contains("dopo il T del bypass"));
            Assert.Equal(p.Mix2EndPipeMm, r.Plan.Runs[0].Branches[0].LengthAfterValveMm);

            // senza modello: resta lo spazio riservato
            c.PumpType = null;
            var plain = p.ChainFor(c, true);
            Assert.DoesNotContain(Kinds(plain), k => k == ValveKind.Pump);
            Assert.Contains(plain, i => i.Kind == StubItemKind.Gap && i.LengthMm == p.Mix2PumpSpaceMm && i.Label.Contains("pompa"));
        }

        [Fact]
        public void Mix3_ConModello_PompaDopoLIntercettazione_ConAvvisoSuValvolaEBypass()
        {
            var p = Plan(new ManifoldCircuit(40, CircuitKind.MixThreeWay) { PumpType = "MAGNA3 40-80 F PN6 - 98333900" });
            var supply = p.ChainFor(p.Circuits[0], true);
            Assert.Equal(new[] { ValveKind.Butterfly, ValveKind.Pump }, Kinds(supply));
            Assert.Null(p.BypassFor(p.Circuits[0], 0, true));
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n.Contains("pompa \"MAGNA3 40-80 F PN6 - 98333900\"") && n.Contains("valvola a 3 vie e bypass non ancora modellati"));
            var w = Assert.Single(r.Warnings, x => x.StartsWith("Tipologie:"));
            Assert.DoesNotContain("pompe", w);
            Assert.Contains("valvole miscelatrici a 3 vie", w);
            Assert.Contains("bypass", w);
        }

        [Fact]
        public void SenzaModelloONonTrovato_NessunaPompa_ConAvviso()
        {
            var p = Plan(new ManifoldCircuit(32, CircuitKind.Direct));
            Assert.Null(p.PumpFor(p.Circuits[0]));
            Assert.False(p.HasChain(p.Circuits[0]));
            var r = p.ToParseResult();
            Assert.Contains(r.Warnings, w => w.StartsWith("Tipologie:") && w.Contains("pompe (nessun modello scelto nella riga)"));

            p.Circuits[0].PumpType = "MAGNA3 999";
            Assert.Null(p.PumpFor(p.Circuits[0]));
            Assert.Contains(p.ToParseResult().Warnings, w => w.StartsWith("C1: modello di pompa \"MAGNA3 999\" non trovato"));

            p.Circuits[0].PumpType = "MAGNA3 25-60 PN10 - 97924245";
            p.FamilyMaps[ManifoldElements.Pump].Default = null;
            Assert.Null(p.PumpFor(p.Circuits[0]));
            Assert.Contains(p.ToParseResult().Warnings, w => w.Contains("nessuna famiglia della pompa"));

            // senza pompa e cieco: il modello non ha senso e non si salva
            var np = new ManifoldCircuit(20, CircuitKind.NoPump) { PumpType = "MAGNA3 25-60 PN10 - 97924245" };
            Assert.Null(Plan(np).PumpFor(np));
            Assert.Equal("20:nopump", ManifoldPlan.CircuitToString(np));
        }

        [Fact]
        public void Impostazioni_IlModelloVaEViene()
        {
            var c = new ManifoldCircuit(32, CircuitKind.Direct) { PumpType = "MAGNA3 32-60 PN10 - 97924255" };
            var text = ManifoldPlan.CircuitToString(c);
            Assert.Equal("32:direct|pump=MAGNA3 32-60 PN10 - 97924255", text);
            var back = ManifoldPlan.ParseCircuit(text);
            Assert.Equal("MAGNA3 32-60 PN10 - 97924255", back.PumpType);
            Assert.Equal(CircuitKind.Direct, back.Kind);

            var mix2 = new ManifoldCircuit(50, CircuitKind.MixTwoWayInjection) { DnAfterBypassMm = 32, EnergyValveFamily = "ev050r2+bac_(1)", PumpType = "MAGNA3 32-40 F PN6 - 98333834" };
            var t2 = ManifoldPlan.CircuitToString(mix2);
            Assert.Equal("50:mix2|out=32|ev=ev050r2+bac_(1)|pump=MAGNA3 32-40 F PN6 - 98333834", t2);
            Assert.Equal("MAGNA3 32-40 F PN6 - 98333834", ManifoldPlan.ParseCircuit(t2).PumpType);
        }

        [Fact]
        public void RotazioneEVerso_DalleImpostazioniDelPiano()
        {
            var p = Plan(new ManifoldCircuit(32, CircuitKind.Direct) { PumpType = "MAGNA3 32-60 PN10 - 97924255" });
            Assert.Equal(90, new ManifoldPlan().PumpRollDegrees); // predefinito come la boax
            p.PumpRollDegrees = 180;
            p.PumpReversed = true;
            var pump = p.PumpFor(p.Circuits[0]);
            Assert.Equal(180, pump.RollDegrees);
            Assert.True(pump.Reversed);
            Assert.Contains(p.ToParseResult().Notes, n => n.Contains("pompa") && n.Contains("rotazione 180°") && n.Contains("verso invertito"));
            Assert.Contains(PumpFamily, p.ConfiguredFamilies());
        }
    }
}
