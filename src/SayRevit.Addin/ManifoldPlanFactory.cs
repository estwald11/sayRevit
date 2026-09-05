using System;
using System.Linq;
using SayRevit.Core.Model;

namespace SayRevit.Addin
{
    /// <summary>
    /// Costruisce il collettore dalle impostazioni salvate e dal catalogo del progetto, senza
    /// passare dalla finestra: è lo stesso piano che produce il pannello, usato dall'automazione.
    /// Le famiglie degli elementi vengono dal registro <see cref="ManifoldElements"/>: nessun
    /// codice per elemento qui.
    /// </summary>
    public static class ManifoldPlanFactory
    {
        /// <summary>Voce salvata che significa "nessuna famiglia" (la stessa del pannello).</summary>
        public const string NoFamily = "(nessuna)";

        public static ManifoldPlan FromSettings(Settings settings, ModelCatalog catalog)
        {
            DirectionKind direction;
            if (!Enum.TryParse(settings.ManifoldCircuitDirection, out direction)) direction = DirectionKind.Up;

            var plan = new ManifoldPlan
            {
                SpacingMm = settings.ManifoldSpacingMm,
                AutoSpacing = settings.ManifoldAutoSpacing,
                CircuitLengthMm = settings.ManifoldCircuitLengthMm,
                NoPumpPipeAfterValveMm = settings.ManifoldNoPumpPipeAfterValveMm,
                HeaderDirection = DirectionKind.PlusX,
                CircuitDirection = direction,
                PipeTypeName = string.IsNullOrWhiteSpace(settings.ManifoldPipeTypeName) ? null : settings.ManifoldPipeTypeName,
                WithReturn = settings.ManifoldWithReturn,
                ReturnOffsetMm = settings.ManifoldReturnOffsetMm,
                WithValves = settings.ManifoldWithValves,
                BallValveMaxDnMm = settings.ManifoldBallValveMaxDnMm,
                ValvePnBar = settings.ManifoldValvePnBar,
                ValveDistanceMm = settings.ManifoldValveDistanceMm,
                ButterflyRollDegrees = settings.ManifoldButterflyRollDeg,
                BallRollDegrees = settings.ManifoldBallRollDeg,
                Mix2GapMm = settings.ManifoldMix2GapMm,
                Mix2FlangedGapMm = settings.ManifoldMix2FlangedGapMm,
                Mix2PumpSpaceMm = settings.ManifoldMix2PumpSpaceMm,
                Mix2EndPipeMm = settings.ManifoldMix2EndPipeMm,
                Mix2RollDegrees = settings.ManifoldMix2RollDeg,
                StrainerRollDegrees = settings.ManifoldStrainerRollDeg,
                StrainerReversed = settings.ManifoldStrainerReversed,
                PumpRollDegrees = settings.ManifoldPumpRollDeg,
                PumpReversed = settings.ManifoldPumpReversed
            };
            // famiglie per DN di ogni elemento: base + soglie; valgono solo le famiglie caricate in QUESTO progetto
            foreach (var element in ManifoldElements.All)
            {
                var map = plan.FamilyMaps[element.Key];
                LoadMap(map, settings.Family(element.Key), catalog);
                plan.ElementTypes[element.Key].AddRange(TypesOf(map.Default, catalog));
            }
            plan.AccessoryFamilies.AddRange(catalog.AllAccessories);

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

        /// <summary>
        /// Legge una famiglia per DN salvata dentro la mappa del piano: la base e le soglie con una
        /// famiglia caricata nel progetto; "(nessuna)" resta una scelta esplicita di non mettere il
        /// pezzo; una soglia con famiglia non caricata viene ignorata (vale la base).
        /// </summary>
        private static void LoadMap(FamilyByDn target, string stored, ModelCatalog catalog)
        {
            var parsed = FamilyByDn.Parse(stored);
            target.Default = FamilyOrNull(parsed.Default, catalog);
            target.Rules.Clear();
            foreach (var rule in parsed.OrderedRules())
            {
                var family = FamilyOrNull(rule.Family, catalog);
                if (family == null && rule.HasFamily && !IsNone(rule.Family)) continue;
                target.Rules.Add(new FamilyRule(rule.FromDnMm, family));
            }
        }

        private static bool IsNone(string name)
        {
            return string.Equals((name ?? string.Empty).Trim(), NoFamily, StringComparison.OrdinalIgnoreCase);
        }

        private static string FamilyOrNull(string name, ModelCatalog catalog)
        {
            if (string.IsNullOrWhiteSpace(name) || IsNone(name)) return null;
            var found = catalog.AllAccessories.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            return found == null ? null : found.Name;
        }

        private static string[] TypesOf(string family, ModelCatalog catalog)
        {
            if (family == null) return new string[0];
            var found = catalog.AllAccessories.FirstOrDefault(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
            return found == null ? new string[0] : found.TypeNames.ToArray();
        }
    }
}
