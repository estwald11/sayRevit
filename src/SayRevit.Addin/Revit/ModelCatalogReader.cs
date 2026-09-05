using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using SayRevit.Core.Model;

namespace SayRevit.Addin.Revit
{
    /// <summary>Legge dal documento i tipi, i sistemi e i livelli disponibili (le "famiglie esistenti").</summary>
    public static class ModelCatalogReader
    {
        public static ModelCatalog Read(Document doc)
        {
            var c = new ModelCatalog();

            foreach (var pt in new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().OrderBy(t => t.Name))
            {
                var ct = new CatalogType { Name = pt.Name, Kind = MepKind.Pipe, Shape = SizeShape.Round };
                ReadRoutingPreferences(doc, pt, ct);
                c.PipeTypes.Add(ct);
            }

            List<double> roundDuctSizes = null;
            foreach (var dt in new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().OrderBy(t => t.Name))
            {
                var ct = new CatalogType
                {
                    Name = dt.Name,
                    Kind = MepKind.Duct,
                    Shape = SafeShape(dt) == ConnectorProfileType.Rectangular ? SizeShape.Rectangular : SizeShape.Round
                };
                ReadRoutingPreferences(doc, dt, ct);
                if (ct.Shape == SizeShape.Round)
                {
                    if (roundDuctSizes == null) roundDuctSizes = ReadRoundDuctSizes(doc);
                    ct.AvailableDiametersMm.AddRange(roundDuctSizes);
                }
                c.DuctTypes.Add(ct);
            }

            foreach (var st in new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().OrderBy(t => t.Name))
            {
                c.PipingSystems.Add(new CatalogSystem { Name = st.Name, SystemClass = st.SystemClassification.ToString() });
            }

            foreach (var st in new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().OrderBy(t => t.Name))
            {
                c.DuctSystems.Add(new CatalogSystem { Name = st.Name, SystemClass = st.SystemClassification.ToString() });
            }

            foreach (var lv in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
            {
                c.Levels.Add(lv.Name);
            }

            // Valvole e simili: normalmente accessori per tubazioni. Se il progetto non ne ha,
            // si ripiega sui raccordi, dove qualche libreria colloca le stesse famiglie.
            c.PipeAccessories.AddRange(ReadFamilies(doc, BuiltInCategory.OST_PipeAccessory));
            if (c.PipeAccessories.Count == 0)
                c.PipeAccessories.AddRange(ReadFamilies(doc, BuiltInCategory.OST_PipeFitting));
            // pompe: attrezzature meccaniche
            c.MechanicalEquipment.AddRange(ReadFamilies(doc, BuiltInCategory.OST_MechanicalEquipment));

            try
            {
                var gen = doc.ActiveView?.GenLevel;
                c.ActiveLevel = gen != null ? gen.Name : null;
            }
            catch
            {
                c.ActiveLevel = null;
            }

            return c;
        }

        public static ConnectorProfileType SafeShape(MEPCurveType type)
        {
            try
            {
                return type.Shape;
            }
            catch
            {
                return ConnectorProfileType.Invalid;
            }
        }

        /// <summary>Famiglie caricate in una categoria, con i nomi dei tipi di ciascuna.</summary>
        private static List<CatalogFamily> ReadFamilies(Document doc, BuiltInCategory category)
        {
            var list = new List<CatalogFamily>();
            try
            {
                var groups = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(category)
                    .Cast<FamilySymbol>()
                    .GroupBy(s => SafeFamilyName(s))
                    .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
                foreach (var g in groups)
                {
                    var family = new CatalogFamily { Name = g.Key };
                    family.TypeNames.AddRange(g.Select(s => s.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
                    list.Add(family);
                }
            }
            catch
            {
                // categoria non disponibile in questo documento
            }
            return list;
        }

        public static string SafeFamilyName(ElementType type)
        {
            try
            {
                return type.FamilyName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void ReadRoutingPreferences(Document doc, MEPCurveType type, CatalogType ct)
        {
            try
            {
                var rpm = type.RoutingPreferenceManager;
                if (rpm == null) return;
                ct.HasElbows = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows) > 0;
                ct.HasTees = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) > 0;
                ct.HasTransitions = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Transitions) > 0;

                var n = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments);
                // DN → diametro interno: se lo stesso DN compare in più segmenti si tiene
                // l'interno più piccolo (scelta prudente per il dimensionamento).
                var sizes = new SortedDictionary<double, double>();
                var outers = new Dictionary<double, double>();
                for (var i = 0; i < n; i++)
                {
                    var rule = rpm.GetRule(RoutingPreferenceRuleGroupType.Segments, i);
                    if (rule == null || rule.MEPPartId == ElementId.InvalidElementId) continue;
                    var seg = doc.GetElement(rule.MEPPartId) as Segment;
                    if (seg == null) continue;
                    foreach (MEPSize s in seg.GetSizes())
                    {
                        var nominal = Math.Round(Units.FtToMm(s.NominalDiameter), 1);
                        var inner = Math.Round(Units.FtToMm(s.InnerDiameter), 1);
                        var outer = Math.Round(Units.FtToMm(s.OuterDiameter), 1);
                        if (!sizes.TryGetValue(nominal, out var existing) || (inner > 0 && (existing <= 0 || inner < existing)))
                        {
                            sizes[nominal] = inner;
                            outers[nominal] = outer;
                        }
                    }
                }
                foreach (var kv in sizes)
                {
                    ct.AvailableDiametersMm.Add(kv.Key);
                    if (ct.Kind == MepKind.Pipe)
                        ct.Sizes.Add(new CatalogPipeSize { NominalMm = kv.Key, InnerMm = kv.Value, OuterMm = outers[kv.Key] });
                }
            }
            catch
            {
                // preferenze non leggibili: il tipo resta utilizzabile senza validazione delle misure
            }
        }

        private static List<double> ReadRoundDuctSizes(Document doc)
        {
            var list = new List<double>();
            try
            {
                var settings = DuctSizeSettings.GetDuctSizeSettings(doc);
                var sizes = settings[DuctShape.Round];
                foreach (MEPSize s in sizes)
                {
                    list.Add(Math.Round(Units.FtToMm(s.NominalDiameter), 1));
                }
                list.Sort();
            }
            catch
            {
                // impostazioni non leggibili
            }
            return list;
        }
    }
}
