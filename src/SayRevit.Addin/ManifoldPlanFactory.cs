using System;
using System.Linq;
using SayRevit.Core.Model;

namespace SayRevit.Addin
{
    /// <summary>
    /// Costruisce il collettore dalle impostazioni salvate e dal catalogo del progetto, senza
    /// passare dalla finestra: è lo stesso piano che produce il pannello, usato dall'automazione.
    /// </summary>
    public static class ManifoldPlanFactory
    {
        public static ManifoldPlan FromSettings(Settings settings, ModelCatalog catalog)
        {
            DirectionKind direction;
            if (!Enum.TryParse(settings.ManifoldCircuitDirection, out direction)) direction = DirectionKind.Up;

            var plan = new ManifoldPlan
            {
                SpacingMm = settings.ManifoldSpacingMm,
                AutoSpacing = settings.ManifoldAutoSpacing,
                CircuitLengthMm = settings.ManifoldCircuitLengthMm,
                HeaderDirection = DirectionKind.PlusX,
                CircuitDirection = direction,
                PipeTypeName = string.IsNullOrWhiteSpace(settings.ManifoldPipeTypeName) ? null : settings.ManifoldPipeTypeName,
                WithReturn = settings.ManifoldWithReturn,
                ReturnOffsetMm = settings.ManifoldReturnOffsetMm,
                WithValves = settings.ManifoldWithValves,
                BallValveMaxDnMm = settings.ManifoldBallValveMaxDnMm,
                BallValveFamily = FamilyOrNull(settings.ManifoldBallValveFamily, catalog),
                ButterflyValveFamily = FamilyOrNull(settings.ManifoldButterflyValveFamily, catalog),
                ValvePnBar = settings.ManifoldValvePnBar,
                ValveDistanceMm = settings.ManifoldValveDistanceMm,
                ButterflyRollDegrees = settings.ManifoldButterflyRollDeg
            };
            plan.BallValveTypes.AddRange(TypesOf(plan.BallValveFamily, catalog));
            plan.ButterflyValveTypes.AddRange(TypesOf(plan.ButterflyValveFamily, catalog));

            var type = catalog.PipeTypes.FirstOrDefault(t => t.Name == plan.PipeTypeName) ?? catalog.PipeTypes.FirstOrDefault();
            if (type != null)
            {
                plan.PipeTypeName = type.Name;
                plan.HeaderSizeCandidates.AddRange(type.Sizes);
            }
            if (settings.ManifoldHeaderDnMm > 0) plan.HeaderDnMm = settings.ManifoldHeaderDnMm;
            plan.LoadCircuitsFromString(settings.ManifoldCircuits);
            return plan;
        }

        private static string FamilyOrNull(string name, ModelCatalog catalog)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var found = catalog.PipeAccessories.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            return found == null ? null : found.Name;
        }

        private static string[] TypesOf(string family, ModelCatalog catalog)
        {
            if (family == null) return new string[0];
            var found = catalog.PipeAccessories.FirstOrDefault(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
            return found == null ? new string[0] : found.TypeNames.ToArray();
        }
    }
}
