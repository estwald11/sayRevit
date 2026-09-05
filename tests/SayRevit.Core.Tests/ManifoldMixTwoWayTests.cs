using System.Collections.Generic;
using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    /// <summary>
    /// Mix 2 vie (iniezione): la catena dei pezzi su mandata e ritorno, il bypass tra i due T e
    /// la scelta delle famiglie per DN, con i nomi reali del modello ATZ.
    /// </summary>
    public class ManifoldMixTwoWayTests
    {
        private const string ZoneFamily = "WATTS_ButterflyValve_DrinkingWater_SylaxManualGearBox_StainlessSteel_Wafer_(25-350)";
        private const string StrainerFamily = "WATTS_Valve_StrainerWithDrainCock_Y33P";
        private const string CheckFamily = "boa-rvkwithmaterialnumber-bimdata48860607dn80lod-200b11a 11 48860607_pn6";

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
            p.ZoneValveTypes.AddRange(new[] { "DN25 - Cast Iron", "DN32 - Cast Iron", "DN32 - Ductile Iron", "DN50 - Cast Iron", "DN50 - Ductile Iron" });
            p.StrainerTypes.AddRange(new[] { "DN40", "DN50", "DN65" });
            p.CheckValveTypes.AddRange(new[] { "DN25_PN6_48860602", "DN32_PN6_48860603", "DN50_PN6_48860605" });
            p.AccessoryFamilies.Add(Family("ev025r2+bac_(1)", "EV025R2+BAC"));
            p.AccessoryFamilies.Add(Family("ev032r2+bac_(1)", "EV032R2+BAC"));
            p.AccessoryFamilies.Add(Family("ev050r2+mid_(1)", "EV050R2+MID"));
            p.AccessoryFamilies.Add(Family("ev050r2+bac_(1)", "EV050R2+BAC"));
            p.AccessoryFamilies.Add(Family("ev125f+bac (1)", "EV125F+BAC"));
            p.AccessoryFamilies.Add(Family(ZoneFamily, "DN50 - Ductile Iron"));
            foreach (var c in circuits) p.Circuits.Add(c);
            return p;
        }

        private static ManifoldCircuit Mix2(double dn)
        {
            return new ManifoldCircuit(dn, CircuitKind.MixTwoWayInjection);
        }

        private static List<ValveKind> Kinds(IEnumerable<StubItem> chain)
        {
            return chain.Where(i => i.Kind == StubItemKind.Piece).Select(i => i.Piece.Kind).ToList();
        }

        [Fact]
        public void EnergyValve_UnaFamigliaPerDn_PreferendoBac()
        {
            var p = Plan(Mix2(50));
            Assert.Equal("ev025r2+bac_(1)", p.EnergyValveFamilyFor(25).Name);
            Assert.Equal("ev032r2+bac_(1)", p.EnergyValveFamilyFor(32).Name);
            Assert.Equal("ev050r2+bac_(1)", p.EnergyValveFamilyFor(50).Name); // +BAC vince su +MID
            Assert.Equal("ev125f+bac (1)", p.EnergyValveFamilyFor(125).Name);
            Assert.Null(p.EnergyValveFamilyFor(40));
            Assert.Equal(new double[] { 25, 32, 50, 125 }, p.EnergyValveDns());

            var ev = p.EnergyValveFor(50);
            Assert.Equal(ValveKind.EnergyValve, ev.Kind);
            Assert.Equal("EV050R2+BAC", ev.TypeName);
            Assert.False(ev.WithFlanges);
        }

        [Fact]
        public void ValvolaDiZona_PreferisceDuctileIron_APariDn()
        {
            var p = Plan(Mix2(50));
            Assert.Equal("DN50 - Ductile Iron", p.ZoneValveFor(50).TypeName);
            Assert.Equal("DN32 - Ductile Iron", p.ZoneValveFor(32).TypeName);
            Assert.Equal("DN25 - Cast Iron", p.ZoneValveFor(25).TypeName); // il DN25 c'è solo in ghisa grigia
            Assert.True(p.ZoneValveFor(50).WithFlanges);
        }

        [Fact]
        public void CateneDiMandataERitorno_NellOrdineDelloSchema()
        {
            var p = Plan(Mix2(50));
            var c = p.Circuits[0];
            Assert.True(p.HasChain(c));

            var supply = p.ChainFor(c, true);
            Assert.Equal(new[] { ValveKind.Butterfly, ValveKind.ZoneValve, ValveKind.Butterfly }, Kinds(supply));
            Assert.Single(supply, i => i.Kind == StubItemKind.Tee);
            // intercettazione → T → (spazio pompa) → valvola di zona → intercettazione
            Assert.Equal(StubItemKind.Piece, supply[0].Kind);
            Assert.Equal(p.ValveAxisDistanceMm, supply[0].CenterMm);
            Assert.Equal(StubItemKind.Tee, supply[2].Kind);
            Assert.Contains(supply, i => i.Kind == StubItemKind.Gap && i.LengthMm == p.Mix2PumpSpaceMm && i.Label.Contains("pompa"));

            var ret = p.ChainFor(c, false);
            Assert.Equal(new[] { ValveKind.Butterfly, ValveKind.EnergyValve, ValveKind.Strainer, ValveKind.ZoneValve, ValveKind.Butterfly }, Kinds(ret));
            var teeAt = ret.FindIndex(i => i.Kind == StubItemKind.Tee);
            var evAt = ret.FindIndex(i => i.Kind == StubItemKind.Piece && i.Piece.Kind == ValveKind.EnergyValve);
            var strainerAt = ret.FindIndex(i => i.Kind == StubItemKind.Piece && i.Piece.Kind == ValveKind.Strainer);
            Assert.True(evAt < teeAt && teeAt < strainerAt, "il T del bypass sta tra la energy valve e il filtro");
            // tra filtro, valvola di zona e intercettazione: il tubo tra pezzi flangiati (50 mm predefiniti)
            Assert.Equal(50, ret[strainerAt + 1].LengthMm);
            Assert.Equal(StubItemKind.Gap, ret[strainerAt + 1].Kind);
        }

        [Fact]
        public void TraDueElementi_SempreAlmeno50mmDiTuboDiritto()
        {
            var p = Plan(Mix2(50));
            p.Mix2FlangedGapMm = 0;
            p.Mix2GapMm = 10;
            p.Mix2PumpSpaceMm = 0;
            var c = p.Circuits[0];
            var gaps = p.ChainFor(c, true).Concat(p.ChainFor(c, false)).Where(i => i.Kind == StubItemKind.Gap).ToList();
            Assert.NotEmpty(gaps);
            Assert.All(gaps, g => Assert.True(g.LengthMm >= ManifoldPlan.Mix2MinGapMm, g + " sotto il minimo"));
            Assert.Equal(ManifoldPlan.Mix2MinGapMm, gaps.Min(g => g.LengthMm));
            Assert.Contains(p.ToParseResult().Notes, n => n.Contains("mai meno di 50 mm"));
        }

        [Fact]
        public void Bypass_PrimaDiTraverso_PoiLungoLaBase()
        {
            var p = Plan(Mix2(50));
            var r = p.ToParseResult();
            var supply = r.Plan.Runs[0].Branches[0].Bypass;
            var ret = r.Plan.Runs[1].Branches[0].Bypass;
            // dalla mandata: solo di traverso, per tutta la distanza tra le basi
            Assert.Equal(0, supply.LegAlongMm);
            Assert.Equal(300, supply.LegSideMm);
            // dal ritorno: solo lungo la base, mezzo interasse indietro
            Assert.Equal(-200, ret.LegAlongMm);
            Assert.Equal(0, ret.LegSideMm);
            // i due tubi finiscono nello stesso punto in pianta (rispetto allo stacco di mandata)
            Assert.Equal(supply.LegAlongMm, supply.PartnerAlongMm + ret.LegAlongMm, 6);
            Assert.Equal(supply.LegSideMm, supply.PartnerSideMm + ret.LegSideMm, 6);
            Assert.Contains(r.Notes, n => n.StartsWith("Bypass mix 2 vie") && n.Contains("di traverso") && n.Contains("lungo la base"));
        }

        [Fact]
        public void InterasseAutomatico_TieneContoDelTrattoVerticaleDelBypass()
        {
            // due circuiti: il bypass del primo sta sulla verticale della base del ritorno, a metà
            // tra gli stacchi di ritorno: il suo ingombro allarga l'interasse
            var stubs = new List<StubFootprint> { StubFootprint.PipeOnly(50, 1000), StubFootprint.PipeOnly(50, 1000) };
            var without = ManifoldSpacing.Minimal(stubs, null, 100, true, 300, 40, 20, 10).SpacingMm;
            var bypass = new StubFootprint { AlongMinMm = -85, AlongMaxMm = 85, SideMinMm = 300 - 85, SideMaxMm = 300 + 85, UpMinMm = 0, UpMaxMm = 1000 };
            var with = ManifoldSpacing.Minimal(stubs, new List<StubFootprint> { bypass, null }, 100, true, 300, 40, 20, 10);
            // stacco di ritorno 1 (raggio 25) a mezzo interasse: (85 + 25 + 20) / 0.5 = 260
            Assert.Equal(260, with.SpacingMm);
            Assert.True(with.SpacingMm > without);
            Assert.Contains(with.Notes, n => n.Contains("bypass del circuito 1") && n.Contains("del ritorno"));
        }

        [Fact]
        public void StacchiDelPiano_PortanoCatenaEBypass()
        {
            var p = Plan(Mix2(50), new ManifoldCircuit(40, CircuitKind.Direct));
            var r = p.ToParseResult();
            Assert.True(r.Success);
            Assert.Equal(2, r.Plan.Runs.Count);

            var supply = r.Plan.Runs[0].Branches[0];
            var ret = r.Plan.Runs[1].Branches[0];
            Assert.NotEmpty(supply.Chain);
            Assert.NotEmpty(ret.Chain);
            Assert.Same(supply.Valve, supply.Chain[0].Piece); // la prima voce è la stessa intercettazione
            Assert.Equal(p.Mix2EndPipeMm, supply.LengthAfterValveMm);
            Assert.Equal(p.Mix2EndPipeMm, ret.LengthAfterValveMm);
            Assert.True(supply.LengthMm > p.ValveAxisDistanceMm + 1000, "lo stacco provvisorio deve contenere la catena");

            // bypass: stessa chiave, partner speculare (mezzo interasse lungo la base, distanza tra le basi di lato)
            Assert.NotNull(supply.Bypass);
            Assert.NotNull(ret.Bypass);
            Assert.Equal(supply.Bypass.Key, ret.Bypass.Key);
            Assert.Equal(200, supply.Bypass.PartnerAlongMm);
            Assert.Equal(300, supply.Bypass.PartnerSideMm);
            Assert.Equal(-200, ret.Bypass.PartnerAlongMm);
            Assert.Equal(-300, ret.Bypass.PartnerSideMm);
            Assert.Equal(ValveKind.CheckValve, supply.Bypass.InlinePiece.Kind);
            Assert.Equal("DN50_PN6_48860605", supply.Bypass.InlinePiece.TypeName);

            // il diretto resta com'era
            var direct = r.Plan.Runs[0].Branches[1];
            Assert.Empty(direct.Chain);
            Assert.Null(direct.Bypass);
            Assert.Null(direct.LengthAfterValveMm);
        }

        [Fact]
        public void SenzaRitorno_NessunT_ENessunBypass_ConAvviso()
        {
            var p = Plan(Mix2(50));
            p.WithReturn = false;
            var r = p.ToParseResult();
            var b = r.Plan.Runs[0].Branches[0];
            Assert.DoesNotContain(b.Chain, i => i.Kind == StubItemKind.Tee);
            Assert.Null(b.Bypass);
            Assert.Contains(r.Warnings, w => w.Contains("senza collettore di ritorno") && w.Contains("bypass"));
        }

        [Fact]
        public void StacchiLaterali_NessunBypass_ConAvviso()
        {
            var p = Plan(Mix2(50));
            p.CircuitDirection = DirectionKind.Left;
            var r = p.ToParseResult();
            Assert.All(r.Plan.Runs.SelectMany(x => x.Branches), b => Assert.Null(b.Bypass));
            Assert.Contains(r.Warnings, w => w.Contains("stacchi laterali"));
        }

        [Fact]
        public void DnSenzaEnergyValve_RitornoSenzaValvola2Vie_ConAvviso()
        {
            var p = Plan(Mix2(40));
            var r = p.ToParseResult();
            var ret = r.Plan.Runs[1].Branches[0];
            Assert.DoesNotContain(Kinds(ret.Chain), k => k == ValveKind.EnergyValve);
            Assert.Contains(r.Warnings, w => w.StartsWith("C1 DN40: nessuna famiglia di energy valve") && w.Contains("DN25, DN32, DN50"));
            // il resto della catena c'è comunque
            Assert.Contains(Kinds(ret.Chain), k => k == ValveKind.Strainer);
            Assert.Contains(Kinds(ret.Chain), k => k == ValveKind.ZoneValve);
        }

        [Fact]
        public void FamiglieNonScelte_PezziSaltati_ConAvviso()
        {
            var p = Plan(Mix2(50));
            p.ZoneValveFamily = null;
            p.StrainerFamily = null;
            p.CheckValveFamily = null;
            var r = p.ToParseResult();
            var ret = r.Plan.Runs[1].Branches[0];
            Assert.Equal(new[] { ValveKind.Butterfly, ValveKind.EnergyValve, ValveKind.Butterfly }, Kinds(ret.Chain));
            Assert.Null(r.Plan.Runs[0].Branches[0].Bypass.InlinePiece);
            Assert.Contains(r.Warnings, w => w.Contains("valvola di zona"));
            Assert.Contains(r.Warnings, w => w.Contains("filtro a Y"));
            Assert.Contains(r.Notes, n => n.Contains("nessuna valvola di ritegno"));
        }

        [Fact]
        public void SenzaIntercettazione_NessunaCatena_ConAvviso()
        {
            var p = Plan(Mix2(50));
            p.WithValves = false;
            var r = p.ToParseResult();
            var b = r.Plan.Runs[0].Branches[0];
            Assert.Empty(b.Chain);
            Assert.Null(b.Bypass);
            Assert.Null(b.LengthAfterValveMm);
            Assert.Contains(r.Warnings, w => w.Contains("Mix 2 vie senza valvola di intercettazione"));
        }

        [Fact]
        public void Anteprima_DichiaraPezziELunghezze_ENonPiuIlMix2ComeNonModellato()
        {
            var p = Plan(Mix2(50));
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n.StartsWith("Mix 2 vie (iniezione) (C1): mandata intercettazione") && n.Contains("ritorno intercettazione → valvola 2 vie"));
            Assert.Contains(r.Notes, n => n.StartsWith("Mix 2 vie: tubo libero 150 mm") && n.Contains("spazio per la pompa 400 mm") && n.Contains("tubo finale 100 mm"));
            Assert.Contains(r.Notes, n => n.StartsWith("Bypass mix 2 vie") && n.Contains("valvola di ritegno"));
            Assert.Contains(r.Notes, n => n.StartsWith("C1 DN50 → energy valve \"ev050r2+bac_(1)\" tipo \"EV050R2+BAC\" (automatica sul DN)"));
            Assert.Contains(r.Notes, n => n == "DN50 → valvola di zona \"DN50 - Ductile Iron\".");
            Assert.Contains(r.Notes, n => n == "DN50 → filtro a Y \"DN50\".");
            Assert.Contains(r.Notes, n => n == "DN50 → valvola di ritegno \"DN50_PN6_48860605\".");
            Assert.DoesNotContain(r.Warnings, w => w.StartsWith("Tipologie:"));
            Assert.Contains("catena mix 2 vie", p.Summary());
        }

        [Fact]
        public void FiltroSenzaLaMisura_UsaLaPiuVicina_ConAvviso()
        {
            var p = Plan(Mix2(25));
            var r = p.ToParseResult();
            Assert.Contains(r.Warnings, w => w.StartsWith("DN25: la famiglia \"" + StrainerFamily + "\" (filtro a Y) non ha un tipo DN25"));
        }

        [Fact]
        public void DnDopoIlBypass_DecideBypassEPezziDopoIlT()
        {
            var p = Plan(Mix2(50));
            p.Circuits[0].DnAfterBypassMm = 32;
            var c = p.Circuits[0];
            Assert.Equal(32, c.EffectiveDnAfterBypassMm);

            var supply = p.ChainFor(c, true);
            var tee = supply.Single(i => i.Kind == StubItemKind.Tee);
            Assert.Equal(32, tee.SizeAfterMm);
            Assert.Equal(50, supply[0].Piece.DnMm);                                  // prima intercettazione: DN prima del bypass
            Assert.Equal(ValveKind.Butterfly, supply[0].Piece.Kind);
            Assert.Equal(32, supply.Last().Piece.DnMm);                              // intercettazione in cima: DN dopo
            Assert.Equal(ValveKind.Ball, supply.Last().Piece.Kind);                  // DN32 → sfera
            Assert.Equal("DN32 - Ductile Iron", supply.Single(i => i.Piece != null && i.Piece.Kind == ValveKind.ZoneValve).Piece.TypeName);

            var ret = p.ChainFor(c, false);
            Assert.Equal(50, ret.Single(i => i.Piece != null && i.Piece.Kind == ValveKind.EnergyValve).Piece.DnMm); // energy valve prima del T
            Assert.Equal(32, ret.Single(i => i.Piece != null && i.Piece.Kind == ValveKind.Strainer).Piece.DnMm);    // filtro dopo il T

            var r = p.ToParseResult();
            var bp = r.Plan.Runs[0].Branches[0].Bypass;
            Assert.Equal(32, bp.DnMm);
            Assert.Equal("DN32_PN6_48860603", bp.InlinePiece.TypeName);
            Assert.Contains(r.Notes, n => n.StartsWith("DN prima e dopo il bypass: C1 DN50 prima del bypass → DN32 dopo il bypass"));
            Assert.Contains("DN50 prima del bypass → DN32 dopo il bypass", p.Summary());
        }

        [Fact]
        public void EnergyValve_SceltaNellaRiga_VinceSullAutomatica()
        {
            var p = Plan(Mix2(50));
            p.Circuits[0].EnergyValveFamily = "ev050r2+mid_(1)";
            var ev = p.EnergyValveFor(p.Circuits[0]);
            Assert.Equal("ev050r2+mid_(1)", ev.FamilyName);
            Assert.Equal("EV050R2+MID", ev.TypeName);
            Assert.Contains(p.ToParseResult().Notes, n => n.Contains("ev050r2+mid_(1)") && n.Contains("scelta nella riga"));

            // famiglia non caricata: si torna all'automatica
            p.Circuits[0].EnergyValveFamily = "ev999";
            Assert.Equal("ev050r2+bac_(1)", p.EnergyValveFor(p.Circuits[0]).FamilyName);

            // DN diverso dal circuito: avviso
            p.Circuits[0].EnergyValveFamily = "ev032r2+bac_(1)";
            Assert.Contains(p.ToParseResult().Notes, n => n.Contains("DN32 su un circuito DN50") && n.Contains("riduzione"));
            Assert.Equal(new[] { "ev025r2+bac_(1)", "ev032r2+bac_(1)", "ev050r2+bac_(1)", "ev050r2+mid_(1)", "ev125f+bac (1)" },
                p.EnergyValveFamilies().Select(f => f.Name).ToArray());
        }

        [Fact]
        public void Impostazioni_DnDopoIlBypassEEnergyValve_VannoEVengono()
        {
            var p = new ManifoldPlan();
            p.Circuits.Add(new ManifoldCircuit(50, CircuitKind.MixTwoWayInjection) { DnAfterBypassMm = 32, EnergyValveFamily = "ev050r2+mid_(1)" });
            p.Circuits.Add(new ManifoldCircuit(40, CircuitKind.MixThreeWay) { DnAfterBypassMm = 25 });
            p.Circuits.Add(new ManifoldCircuit(20, CircuitKind.Direct));
            var text = p.CircuitsToString();
            Assert.Equal("50:mix2|out=32|ev=ev050r2+mid_(1);40:mix3|out=25;20:direct", text);

            var back = new ManifoldPlan();
            back.LoadCircuitsFromString(text);
            Assert.Equal(32, back.Circuits[0].DnAfterBypassMm);
            Assert.Equal("ev050r2+mid_(1)", back.Circuits[0].EnergyValveFamily);
            Assert.Equal(25, back.Circuits[1].DnAfterBypassMm);
            Assert.Equal(0, back.Circuits[2].DnAfterBypassMm);
            Assert.Null(back.Circuits[2].EnergyValveFamily);
            // il vecchio formato resta leggibile
            back.LoadCircuitsFromString("25:mix2;20");
            Assert.Equal(2, back.Circuits.Count);
            Assert.Equal(25, back.Circuits[0].EffectiveDnAfterBypassMm);
        }

        [Fact]
        public void FiltroAY_CapovoltoPerDefault_ConRotazionePropria()
        {
            var p = Plan(Mix2(50));
            var strainer = p.StrainerFor(50);
            Assert.True(strainer.Reversed);
            Assert.Equal(270, strainer.RollDegrees);
            p.StrainerRollDegrees = 180;
            p.StrainerReversed = false;
            strainer = p.StrainerFor(50);
            Assert.False(strainer.Reversed);
            Assert.Equal(180, strainer.RollDegrees);
            Assert.False(p.ZoneValveFor(50).Reversed);
            Assert.Contains(p.ToParseResult().Notes, n => n.StartsWith("Filtro a Y girato di 180°") && n.Contains("verso della famiglia"));
        }

        [Fact]
        public void EnergyValve_TrattoRettilineoDi5DiametriInterniSopra()
        {
            var p = Plan(Mix2(50));
            p.HeaderSizeCandidates.Add(new CatalogPipeSize { NominalMm = 50, InnerMm = 54.5, OuterMm = 60.3 });
            Assert.Equal(54.5, p.InnerDiameterMm(50));
            Assert.Equal(25, p.InnerDiameterMm(25)); // senza dati: il DN
            Assert.Equal(273, p.EnergyValveStraightMm(50)); // ceil(5 × 54.5)

            var ret = p.ChainFor(p.Circuits[0], false);
            var evAt = ret.FindIndex(i => i.Kind == StubItemKind.Piece && i.Piece.Kind == ValveKind.EnergyValve);
            var after = ret[evAt + 1];
            Assert.Equal(StubItemKind.Gap, after.Kind);
            Assert.Equal(273, after.LengthMm);
            Assert.Contains("5×Øint", after.Label);
            // subito dopo il tratto rettilineo c'è il T: nessun altro tubo libero in mezzo
            Assert.Equal(StubItemKind.Tee, ret[evAt + 2].Kind);
            Assert.Contains(p.ToParseResult().Notes, n => n.Contains("tubo rettilineo di 273 mm (5 × Øint 54.5 mm del tubo DN50 adiacente alla valvola)"));

            // con un tubo piccolo vale comunque il tubo libero normale
            var small = Plan(Mix2(25));
            Assert.Equal(150, small.EnergyValveStraightMm(25)); // 5 × 25 = 125 < 150

            // energy valve più piccola dello stacco: il tratto rettilineo è del tubo della valvola, adiacente, prima della riduzione
            var reduced = Plan(Mix2(80));
            reduced.Circuits[0].EnergyValveFamily = "ev050r2+bac_(1)";
            reduced.HeaderSizeCandidates.Add(new CatalogPipeSize { NominalMm = 80, InnerMm = 82.5, OuterMm = 88.9 });
            reduced.HeaderSizeCandidates.Add(new CatalogPipeSize { NominalMm = 50, InnerMm = 54.5, OuterMm = 60.3 });
            var evPiece = reduced.EnergyValveFor(reduced.Circuits[0]);
            Assert.Equal(50, ManifoldPlan.EnergyValveStraightDn(evPiece, 80));
            var chain = reduced.ChainFor(reduced.Circuits[0], false);
            var at = chain.FindIndex(i => i.Kind == StubItemKind.Piece && i.Piece.Kind == ValveKind.EnergyValve);
            Assert.Equal(273, chain[at + 1].LengthMm);
            Assert.Contains(reduced.ToParseResult().Notes, n => n.Contains("tubo DN50 adiacente alla valvola, prima della riduzione"));
        }

        [Fact]
        public void ValvolaDiZona_OpzionalePerCircuito()
        {
            // predefinito: sì per il mix 2 vie, no per le altre tipologie
            Assert.True(Mix2(50).EffectiveWithZoneValve);
            Assert.False(new ManifoldCircuit(50, CircuitKind.Direct).EffectiveWithZoneValve);

            var p = Plan(Mix2(50), Mix2(25));
            p.Circuits[1].WithZoneValve = false;
            Assert.Contains(Kinds(p.ChainFor(p.Circuits[0], true)), k => k == ValveKind.ZoneValve);
            Assert.DoesNotContain(Kinds(p.ChainFor(p.Circuits[1], true)), k => k == ValveKind.ZoneValve);
            Assert.DoesNotContain(Kinds(p.ChainFor(p.Circuits[1], false)), k => k == ValveKind.ZoneValve);
            var r = p.ToParseResult();
            Assert.Contains(r.Notes, n => n == "Senza valvola di zona (scelta nella riga): C2.");
            Assert.Contains("senza valvola di zona", p.Summary());

            // richiesta su una tipologia senza catena: dichiarata, non taciuta
            var direct = Plan(new ManifoldCircuit(40, CircuitKind.Direct) { WithZoneValve = true });
            Assert.Contains(direct.ToParseResult().Warnings, w => w.StartsWith("Tipologie:") && w.Contains("valvole di zona su C1"));

            // impostazioni: si salva solo quando differisce dal predefinito
            var s = new ManifoldPlan();
            s.Circuits.Add(new ManifoldCircuit(50, CircuitKind.MixTwoWayInjection) { WithZoneValve = false });
            s.Circuits.Add(new ManifoldCircuit(40, CircuitKind.Direct) { WithZoneValve = true });
            s.Circuits.Add(new ManifoldCircuit(25, CircuitKind.MixTwoWayInjection) { WithZoneValve = true });
            Assert.Equal("50:mix2|zv=0;40:direct|zv=1;25:mix2", s.CircuitsToString());
            var back = new ManifoldPlan();
            back.LoadCircuitsFromString(s.CircuitsToString());
            Assert.False(back.Circuits[0].EffectiveWithZoneValve);
            Assert.True(back.Circuits[1].EffectiveWithZoneValve);
            Assert.True(back.Circuits[2].EffectiveWithZoneValve);
        }

        [Fact]
        public void PickConParolaPreferita_NonCambiaIlDn()
        {
            var types = new[] { "DN25 - Cast Iron", "DN32 - Ductile Iron", "DN50 - Cast Iron", "DN50 - Ductile Iron" };
            Assert.Equal("DN50 - Ductile Iron", ValveTypeMatcher.Pick(types, 50, 0, "ductile").TypeName);
            Assert.Equal("DN50 - Cast Iron", ValveTypeMatcher.Pick(types, 50, 0, "cast").TypeName);
            Assert.Equal("DN25 - Cast Iron", ValveTypeMatcher.Pick(types, 25, 0, "ductile").TypeName);
            Assert.Equal("DN50 - Cast Iron", ValveTypeMatcher.Pick(types, 50, 0, null).TypeName); // senza parola: ordine alfabetico
        }
    }
}
