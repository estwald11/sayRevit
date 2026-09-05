using System.Linq;
using SayRevit.Core.Model;
using Xunit;

namespace SayRevit.Core.Tests
{
    /// <summary>La valvola di zona è opzionale per circuito, ma il cieco non la prevede mai.</summary>
    public class ManifoldZoneValveOptionTests
    {
        [Fact]
        public void Cieco_MaiConValvolaDiZona()
        {
            var blind = new ManifoldCircuit(25, CircuitKind.Blind) { WithZoneValve = true };
            Assert.False(blind.EffectiveWithZoneValve);
            // la scelta non finisce nel salvataggio
            Assert.Equal("25:blind", ManifoldPlan.CircuitToString(blind));

            var p = new ManifoldPlan { WithReturn = false, BallValveFamily = "Sfera" };
            p.BallValveTypes.Add("1\" Lever");
            p.Circuits.Add(blind);
            var r = p.ToParseResult();
            Assert.DoesNotContain(r.Warnings, w => w.Contains("valvole di zona su"));
            Assert.DoesNotContain(r.Notes.Concat(r.Warnings), m => m.Contains("valvola di zona richiesta"));
        }

        [Fact]
        public void AltreTipologie_ScelgonoLaValvolaDiZona()
        {
            var direct = new ManifoldCircuit(25, CircuitKind.Direct) { WithZoneValve = true };
            Assert.True(direct.EffectiveWithZoneValve);
            Assert.Equal("25:direct|zv=1", ManifoldPlan.CircuitToString(direct));
            Assert.False(new ManifoldCircuit(25, CircuitKind.Direct).EffectiveWithZoneValve);
            Assert.True(new ManifoldCircuit(25, CircuitKind.MixTwoWayInjection).EffectiveWithZoneValve);
        }
    }
}
