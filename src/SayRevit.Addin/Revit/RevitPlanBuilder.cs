using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using SayRevit.Core.Model;
using SayRevit.Core.Parsing;

namespace SayRevit.Addin.Revit
{
    /// <summary>Opzioni di costruzione scelte dall'utente nella finestra.</summary>
    public sealed class BuildOptions
    {
        /// <summary>Punto di partenza in unità interne (piedi). Null = origine.</summary>
        public XYZ StartPoint { get; set; }

        /// <summary>Livello scelto nella finestra (usato se il testo non ne indica uno).</summary>
        public string LevelName { get; set; }

        /// <summary>Quota rispetto al livello (mm) quando il testo non la indica.</summary>
        public double DefaultElevationMm { get; set; } = 2500;

        /// <summary>Se true la Z del punto scelto viene mantenuta invece di livello+quota.</summary>
        public bool UsePickedZ { get; set; }
    }

    /// <summary>Esito della costruzione.</summary>
    public sealed class BuildReport
    {
        public bool Succeeded { get; set; }
        public int Pipes { get; set; }
        public int Ducts { get; set; }
        public int Fittings { get; set; }
        public List<ElementId> CreatedIds { get; } = new List<ElementId>();
        public List<string> Messages { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public string Summary()
        {
            if (!Succeeded) return "Nessun elemento creato.";
            return "Creati " + Pipes + " tratti di tubazione, " + Ducts + " tratti di canale e " + Fittings + " raccordi.";
        }
    }

    /// <summary>
    /// Traduce un <see cref="MepPlan"/> in elementi Revit reali usando i tipi, i sistemi e i livelli del documento.
    /// </summary>
    public sealed class RevitPlanBuilder
    {
        private const double TolFt = 5.0 / Units.MmPerFoot; // 5 mm

        private readonly Document _doc;
        private readonly BuildReport _report = new BuildReport();
        private List<PipeType> _pipeTypes;
        private List<DuctType> _ductTypes;
        private List<PipingSystemType> _pipeSystems;
        private List<MechanicalSystemType> _ductSystems;
        private List<Level> _levels;

        public RevitPlanBuilder(Document doc)
        {
            _doc = doc;
        }

        public BuildReport Build(MepPlan plan, BuildOptions options)
        {
            options = options ?? new BuildOptions();
            LoadCollections();

            if (_pipeTypes.Count == 0 && plan.Runs.Any(r => r.Kind == MepKind.Pipe))
            {
                _report.Warnings.Add("Il progetto non contiene tipi di tubazione: carica un modello di progetto MEP o crea un tipo di tubazione.");
                return _report;
            }
            if (_ductTypes.Count == 0 && plan.Runs.Any(r => r.Kind == MepKind.Duct))
            {
                _report.Warnings.Add("Il progetto non contiene tipi di canale: carica un modello di progetto MEP o crea un tipo di canale.");
                return _report;
            }

            using (var t = new Transaction(_doc, "sayRevit: crea MEP da testo"))
            {
                var fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new WarningSwallower());
                fho.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(fho);
                t.Start();
                try
                {
                    RunContext previous = null;
                    XYZ firstStart = null;
                    for (var i = 0; i < plan.Runs.Count; i++)
                    {
                        var ctx = BuildRun(plan.Runs[i], i, previous, options, ref firstStart);
                        if (ctx == null) continue;
                        if (previous != null && plan.Runs[i].ContinuesPrevious) Connect(previous, ctx);
                        previous = ctx;
                    }

                    if (_report.CreatedIds.Count == 0)
                    {
                        t.RollBack();
                        _report.Warnings.Add("Nessun elemento creato.");
                        return _report;
                    }

                    _doc.Regenerate();
                    t.Commit();
                    _report.Succeeded = true;
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    _report.Succeeded = false;
                    _report.CreatedIds.Clear();
                    _report.Warnings.Add("Errore durante la creazione: " + ex.Message);
                }
            }
            return _report;
        }

        // ------------------------------------------------------------------ run

        private sealed class RunContext
        {
            public MepRun Run;
            public XYZ Start;
            public XYZ End;
            public XYZ Dir;
            public Level Level;
            public ElementId TypeId;
            public ElementId SystemId;
            public MepSize Size;
            public List<MEPCurve> Pieces = new List<MEPCurve>();
            public bool PreferTap;
        }

        private RunContext BuildRun(MepRun run, int index, RunContext previous, BuildOptions options, ref XYZ firstStart)
        {
            var label = "Tratto " + (index + 1) + ": ";
            var ctx = new RunContext { Run = run };

            ctx.Level = ResolveLevel(run.LevelHint, options.LevelName, previous?.Level);
            if (ctx.Level == null)
            {
                _report.Warnings.Add(label + "nessun livello nel progetto.");
                return null;
            }

            MEPCurveType type;
            if (run.Kind == MepKind.Pipe)
            {
                type = ResolvePipeType(run, label);
                ctx.SystemId = ResolvePipeSystem(run, label);
            }
            else
            {
                type = ResolveDuctType(run, label);
                ctx.SystemId = ResolveDuctSystem(run, label);
            }
            if (type == null) return null;
            ctx.TypeId = type.Id;
            ctx.PreferTap = SafePreferTap(type);
            ctx.Size = SnapSize(run.Size, type, label);

            var dir = RunDirection(run.Direction);
            ctx.Dir = dir;

            var zBase = ctx.Level.Elevation + Units.MmToFt(run.ElevationMm ?? options.DefaultElevationMm);
            XYZ start;
            if (previous != null && run.ContinuesPrevious)
            {
                start = previous.End;
            }
            else if (previous != null)
            {
                // tratto separato: affiancato al primo, 1 m più in là in -Y
                var offset = new XYZ(0, -Units.MmToFt(1000) * index, 0);
                start = new XYZ(firstStart.X + offset.X, firstStart.Y + offset.Y, zBase);
            }
            else
            {
                var p = options.StartPoint ?? XYZ.Zero;
                start = new XYZ(p.X, p.Y, options.UsePickedZ && options.StartPoint != null ? p.Z : zBase);
                firstStart = start;
            }
            ctx.Start = start;
            ctx.End = start + dir * Units.MmToFt(run.LengthMm);

            var main = CreateCurve(run.Kind, ctx.SystemId, ctx.TypeId, ctx.Level.Id, ctx.Start, ctx.End, ctx.Size, label);
            if (main == null) return null;
            ctx.Pieces.Add(main);

            for (var b = 0; b < run.Branches.Count; b++)
            {
                BuildBranches(ctx, run.Branches[b], type, label + "stacchi gruppo " + (b + 1) + ": ");
            }
            return ctx;
        }

        // ------------------------------------------------------------- branches

        private void BuildBranches(RunContext ctx, MepBranch branch, MEPCurveType type, string label)
        {
            var lengthMm = ctx.Run.LengthMm;
            var positions = BranchPositions(branch, lengthMm);
            if (positions.Count == 0)
            {
                _report.Warnings.Add(label + "nessuna posizione valida lungo il tratto.");
                return;
            }
            if (positions.Count < branch.Count)
            {
                _report.Warnings.Add(label + "solo " + positions.Count + " stacchi su " + branch.Count + " entrano nel tratto.");
            }

            var size = SnapSize(branch.Size, type, label);
            if (ctx.Run.Kind == MepKind.Pipe && size.Shape == SizeShape.Rectangular) size = MepSize.Round(size.WidthMm);

            for (var i = 0; i < positions.Count; i++)
            {
                var pMm = positions[i];
                var point = ctx.Start + ctx.Dir * Units.MmToFt(pMm);
                var bdir = BranchDirection(branch.Direction, ctx.Dir, ctx.Run.Kind, i);
                if (Math.Abs(bdir.DotProduct(ctx.Dir)) > 0.99)
                {
                    _report.Warnings.Add(label + "direzione dello stacco parallela al tratto: uso la direzione predefinita.");
                    bdir = BranchDirection(DirectionKind.Default, ctx.Dir, ctx.Run.Kind, i);
                }

                var piece = FindPieceContaining(ctx, point);
                if (piece == null)
                {
                    _report.Warnings.Add(label + "posizione " + PlanFormatter.Len(pMm) + " non trovata sul tratto (forse coincide con un altro stacco).");
                    continue;
                }

                var bEnd = point + bdir * Units.MmToFt(branch.LengthMm);
                var branchCurve = CreateCurve(ctx.Run.Kind, ctx.SystemId, ctx.TypeId, ctx.Level.Id, point, bEnd, size, label);
                if (branchCurve == null) continue;

                var branchConnector = FindConnectorAt(branchCurve, point);
                if (branchConnector == null)
                {
                    _report.Warnings.Add(label + "connettore dello stacco non trovato: stacco lasciato scollegato.");
                    continue;
                }

                if (ctx.PreferTap)
                {
                    if (TryTakeoff(branchConnector, piece, label)) continue;
                }

                TryTee(ctx, piece, point, branchConnector, label);
            }
        }

        private bool TryTakeoff(Connector branchConnector, MEPCurve main, string label)
        {
            try
            {
                var fi = _doc.Create.NewTakeoffFitting(branchConnector, main);
                if (fi != null)
                {
                    _report.CreatedIds.Add(fi.Id);
                    _report.Fittings++;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _report.Warnings.Add(label + "presa (takeoff) non creata: " + ex.Message);
            }
            return false;
        }

        private void TryTee(RunContext ctx, MEPCurve piece, XYZ point, Connector branchConnector, string label)
        {
            MEPCurve second = null;
            try
            {
                var newId = ctx.Run.Kind == MepKind.Pipe
                    ? PlumbingUtils.BreakCurve(_doc, piece.Id, point)
                    : MechanicalUtils.BreakCurve(_doc, piece.Id, point);
                second = _doc.GetElement(newId) as MEPCurve;
                if (second == null) throw new InvalidOperationException("BreakCurve non ha restituito un elemento.");
                ctx.Pieces.Add(second);
                _report.CreatedIds.Add(second.Id);
                if (ctx.Run.Kind == MepKind.Pipe) _report.Pipes++; else _report.Ducts++;

                var c1 = FindConnectorAt(piece, point);
                var c2 = FindConnectorAt(second, point);
                if (c1 == null || c2 == null) throw new InvalidOperationException("connettori del tratto non trovati nel punto di stacco.");

                var tee = _doc.Create.NewTeeFitting(c1, c2, branchConnector);
                if (tee != null)
                {
                    _report.CreatedIds.Add(tee.Id);
                    _report.Fittings++;
                }
            }
            catch (Exception ex)
            {
                var msg = label + "raccordo a T non creato (" + ex.Message + "). Verifica che il tipo abbia un raccordo a T nelle preferenze di instradamento.";
                // seconda possibilità: presa sul tratto originale
                if (second != null || !ctx.PreferTap)
                {
                    if (TryTakeoff(branchConnector, piece, label)) return;
                }
                _report.Warnings.Add(msg + " Lo stacco è stato lasciato scollegato.");
            }
        }

        private static List<double> BranchPositions(MepBranch b, double lengthMm)
        {
            var list = new List<double>();
            var margin = 1.0; // mm
            if (b.PositionsMm.Count > 0)
            {
                list.AddRange(b.PositionsMm.Where(p => p > margin && p < lengthMm - margin));
            }
            else if (b.SpacingMm.HasValue && b.SpacingMm.Value > 0)
            {
                for (var i = 1; i <= b.Count; i++)
                {
                    var p = b.SpacingMm.Value * i;
                    if (p >= lengthMm - margin) break;
                    list.Add(p);
                }
            }
            else
            {
                for (var i = 1; i <= b.Count; i++) list.Add(lengthMm * i / (b.Count + 1));
            }
            list.Sort();
            // rimuove doppioni troppo vicini
            var result = new List<double>();
            foreach (var p in list)
            {
                if (result.Count == 0 || p - result[result.Count - 1] > 5) result.Add(p);
            }
            return result;
        }

        // ------------------------------------------------------------ geometry

        private static XYZ RunDirection(DirectionKind d)
        {
            switch (d)
            {
                case DirectionKind.MinusX: return -XYZ.BasisX;
                case DirectionKind.PlusY: return XYZ.BasisY;
                case DirectionKind.MinusY: return -XYZ.BasisY;
                case DirectionKind.Up: return XYZ.BasisZ;
                case DirectionKind.Down: return -XYZ.BasisZ;
                case DirectionKind.Left: return XYZ.BasisY;
                case DirectionKind.Right: return -XYZ.BasisY;
                default: return XYZ.BasisX;
            }
        }

        private static XYZ BranchDirection(DirectionKind d, XYZ runDir, MepKind kind, int index)
        {
            var left = XYZ.BasisZ.CrossProduct(runDir);
            if (left.GetLength() < 1e-6) left = XYZ.BasisY; // tratto verticale
            left = left.Normalize();
            switch (d)
            {
                case DirectionKind.Up: return Math.Abs(runDir.Z) > 0.99 ? XYZ.BasisX : XYZ.BasisZ;
                case DirectionKind.Down: return Math.Abs(runDir.Z) > 0.99 ? -XYZ.BasisX : -XYZ.BasisZ;
                case DirectionKind.Left: return left;
                case DirectionKind.Right: return -left;
                case DirectionKind.Alternate: return index % 2 == 0 ? left : -left;
                case DirectionKind.PlusX: return XYZ.BasisX;
                case DirectionKind.MinusX: return -XYZ.BasisX;
                case DirectionKind.PlusY: return XYZ.BasisY;
                case DirectionKind.MinusY: return -XYZ.BasisY;
                default:
                    if (kind == MepKind.Pipe) return Math.Abs(runDir.Z) > 0.99 ? left : XYZ.BasisZ;
                    return left;
            }
        }

        private static MEPCurve FindPieceContaining(RunContext ctx, XYZ point)
        {
            foreach (var piece in ctx.Pieces)
            {
                var lc = piece.Location as LocationCurve;
                var line = lc?.Curve as Line;
                if (line == null) continue;
                var a = line.GetEndPoint(0);
                var b = line.GetEndPoint(1);
                var ab = b - a;
                var len = ab.GetLength();
                if (len < 1e-9) continue;
                var u = ab / len;
                var t = (point - a).DotProduct(u);
                var perp = (point - (a + u * t)).GetLength();
                if (perp > TolFt) continue;
                if (t > TolFt && t < len - TolFt) return piece;
            }
            return null;
        }

        private static Connector FindConnectorAt(MEPCurve curve, XYZ point)
        {
            Connector best = null;
            var bestDist = double.MaxValue;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (c.ConnectorType != ConnectorType.End) continue;
                var d = c.Origin.DistanceTo(point);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return bestDist <= TolFt * 4 ? best : null;
        }

        private void Connect(RunContext prev, RunContext cur)
        {
            var prevPiece = prev.Pieces.LastOrDefault(p => FindConnectorAt(p, prev.End) != null);
            var c1 = prevPiece != null ? FindConnectorAt(prevPiece, prev.End) : null;
            var c2 = FindConnectorAt(cur.Pieces[0], cur.Start);
            if (c1 == null || c2 == null)
            {
                _report.Warnings.Add("Collegamento tra tratti non riuscito: connettori non trovati.");
                return;
            }
            var sameDir = Math.Abs(prev.Dir.DotProduct(cur.Dir)) > 0.999;
            var sameSize = SameSize(prev.Size, cur.Size);
            try
            {
                if (sameDir)
                {
                    if (sameSize)
                    {
                        c1.ConnectTo(c2);
                    }
                    else
                    {
                        var fi = _doc.Create.NewTransitionFitting(c1, c2);
                        if (fi != null) { _report.CreatedIds.Add(fi.Id); _report.Fittings++; }
                    }
                }
                else
                {
                    if (!sameSize) _report.Warnings.Add("Cambio di direzione e di dimensione nello stesso punto: il gomito potrebbe non essere creato.");
                    var fi = _doc.Create.NewElbowFitting(c1, c2);
                    if (fi != null) { _report.CreatedIds.Add(fi.Id); _report.Fittings++; }
                }
            }
            catch (Exception ex)
            {
                _report.Warnings.Add("Raccordo tra tratti non creato: " + ex.Message);
            }
        }

        private static bool SameSize(MepSize a, MepSize b)
        {
            if (a == null || b == null) return false;
            if (a.Shape != b.Shape) return false;
            if (a.Shape == SizeShape.Round) return Math.Abs(a.DiameterMm - b.DiameterMm) < 0.5;
            return Math.Abs(a.WidthMm - b.WidthMm) < 0.5 && Math.Abs(a.HeightMm - b.HeightMm) < 0.5;
        }

        // ------------------------------------------------------------- creation

        private MEPCurve CreateCurve(MepKind kind, ElementId systemId, ElementId typeId, ElementId levelId, XYZ p0, XYZ p1, MepSize size, string label)
        {
            try
            {
                MEPCurve curve;
                if (kind == MepKind.Pipe)
                {
                    curve = Pipe.Create(_doc, systemId, typeId, levelId, p0, p1);
                    SetParam(curve, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, Units.MmToFt(size.DiameterMm), label + "diametro");
                    _report.Pipes++;
                }
                else
                {
                    curve = Duct.Create(_doc, systemId, typeId, levelId, p0, p1);
                    if (size.Shape == SizeShape.Rectangular)
                    {
                        SetParam(curve, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, Units.MmToFt(size.WidthMm), label + "larghezza");
                        SetParam(curve, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, Units.MmToFt(size.HeightMm), label + "altezza");
                    }
                    else
                    {
                        SetParam(curve, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, Units.MmToFt(size.DiameterMm), label + "diametro");
                    }
                    _report.Ducts++;
                }
                _report.CreatedIds.Add(curve.Id);
                return curve;
            }
            catch (Exception ex)
            {
                _report.Warnings.Add(label + "elemento non creato: " + ex.Message);
                return null;
            }
        }

        private void SetParam(Element e, BuiltInParameter bip, double value, string what)
        {
            var p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly)
            {
                _report.Warnings.Add(what + ": parametro non impostabile.");
                return;
            }
            try
            {
                p.Set(value);
            }
            catch (Exception ex)
            {
                _report.Warnings.Add(what + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------ resolvers

        private void LoadCollections()
        {
            _pipeTypes = new FilteredElementCollector(_doc).OfClass(typeof(PipeType)).Cast<PipeType>().OrderBy(t => t.Name).ToList();
            _ductTypes = new FilteredElementCollector(_doc).OfClass(typeof(DuctType)).Cast<DuctType>().OrderBy(t => t.Name).ToList();
            _pipeSystems = new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().OrderBy(t => t.Name).ToList();
            _ductSystems = new FilteredElementCollector(_doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().OrderBy(t => t.Name).ToList();
            _levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        }

        private Level ResolveLevel(string hint, string chosen, Level previous)
        {
            if (_levels.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(hint))
            {
                var best = _levels.Select(l => new { l, s = TextUtil.NameScore(l.Name, hint) }).OrderByDescending(x => x.s).First();
                if (best.s > 0) return best.l;
                // "piano terra" / "ground" → livello più basso; "primo"/"1" → secondo livello ecc.
                var h = TextUtil.Fold(hint);
                if (h.Contains("terra") || h.Contains("ground") || h == "0" || h == "pt") return _levels[0];
                if (h == "primo" || h == "first" || h == "1") return _levels.Count > 1 ? _levels[1] : _levels[0];
                if (h == "secondo" || h == "second" || h == "2") return _levels.Count > 2 ? _levels[2] : _levels[_levels.Count - 1];
                _report.Warnings.Add("Livello \"" + hint + "\" non trovato: uso \"" + (previous ?? PickDefaultLevel(chosen)).Name + "\".");
            }
            if (previous != null) return previous;
            return PickDefaultLevel(chosen);
        }

        private Level PickDefaultLevel(string chosen)
        {
            if (!string.IsNullOrWhiteSpace(chosen))
            {
                var exact = _levels.FirstOrDefault(l => l.Name == chosen);
                if (exact != null) return exact;
            }
            try
            {
                var gen = _doc.ActiveView?.GenLevel;
                if (gen != null) return gen;
            }
            catch
            {
                // vista senza livello
            }
            return _levels[0];
        }

        private PipeType ResolvePipeType(MepRun run, string label)
        {
            var chosen = ResolveType(_pipeTypes.Cast<MEPCurveType>().ToList(), run, label, t => true);
            return chosen as PipeType;
        }

        private DuctType ResolveDuctType(MepRun run, string label)
        {
            var wantRect = run.Size.Shape == SizeShape.Rectangular;
            var compatible = _ductTypes.Where(t =>
            {
                var s = ModelCatalogReader.SafeShape(t);
                return wantRect ? s == ConnectorProfileType.Rectangular : s == ConnectorProfileType.Round;
            }).Cast<MEPCurveType>().ToList();
            if (compatible.Count == 0)
            {
                _report.Warnings.Add(label + "nessun tipo di canale " + (wantRect ? "rettangolare" : "circolare") + " nel progetto: uso il primo tipo disponibile.");
                compatible = _ductTypes.Cast<MEPCurveType>().ToList();
            }
            return ResolveType(compatible, run, label, t => true) as DuctType;
        }

        private MEPCurveType ResolveType(List<MEPCurveType> candidates, MepRun run, string label, Func<MEPCurveType, bool> filter)
        {
            candidates = candidates.Where(filter).ToList();
            if (candidates.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(run.ExplicitTypeName))
            {
                var best = candidates.Select(t => new { t, s = TextUtil.NameScore(t.Name, run.ExplicitTypeName) }).OrderByDescending(x => x.s).First();
                if (best.s > 0)
                {
                    if (best.s < 100) _report.Messages.Add(label + "tipo \"" + run.ExplicitTypeName + "\" non esatto: uso \"" + best.t.Name + "\".");
                    return best.t;
                }
                _report.Warnings.Add(label + "tipo \"" + run.ExplicitTypeName + "\" non trovato.");
            }

            if (run.TypeHints.Count > 0)
            {
                var scored = candidates.Select(t =>
                {
                    var name = TextUtil.Fold(t.Name);
                    var score = run.TypeHints.Count(h => name.Contains(TextUtil.Fold(h)));
                    return new { t, score };
                }).OrderByDescending(x => x.score).ToList();
                if (scored[0].score > 0)
                {
                    _report.Messages.Add(label + "tipo scelto per materiale: \"" + scored[0].t.Name + "\".");
                    return scored[0].t;
                }
                _report.Warnings.Add(label + "nessun tipo con \"" + string.Join("/", run.TypeHints) + "\" nel nome: uso il tipo predefinito.");
            }

            // Tipo predefinito del progetto, altrimenti il primo con segmenti definiti, altrimenti il primo.
            try
            {
                var group = run.Kind == MepKind.Pipe ? ElementTypeGroup.PipeType : ElementTypeGroup.DuctType;
                var defId = _doc.GetDefaultElementTypeId(group);
                var def = candidates.FirstOrDefault(t => t.Id == defId);
                if (def != null) return def;
            }
            catch
            {
                // nessun predefinito
            }
            var withSegments = candidates.FirstOrDefault(t => SafeRuleCount(t, RoutingPreferenceRuleGroupType.Segments) > 0);
            return withSegments ?? candidates[0];
        }

        private ElementId ResolvePipeSystem(MepRun run, string label)
        {
            if (_pipeSystems.Count == 0) return ElementId.InvalidElementId;
            var chosen = ResolveSystem(_pipeSystems.Cast<MEPSystemType>().ToList(), run, label,
                new[] { MEPSystemClassification.DomesticColdWater, MEPSystemClassification.SupplyHydronic, MEPSystemClassification.Sanitary, MEPSystemClassification.OtherPipe });
            return chosen?.Id ?? _pipeSystems[0].Id;
        }

        private ElementId ResolveDuctSystem(MepRun run, string label)
        {
            if (_ductSystems.Count == 0) return ElementId.InvalidElementId;
            var chosen = ResolveSystem(_ductSystems.Cast<MEPSystemType>().ToList(), run, label,
                new[] { MEPSystemClassification.SupplyAir, MEPSystemClassification.ReturnAir, MEPSystemClassification.ExhaustAir, MEPSystemClassification.OtherAir });
            return chosen?.Id ?? _ductSystems[0].Id;
        }

        private MEPSystemType ResolveSystem(List<MEPSystemType> candidates, MepRun run, string label, MEPSystemClassification[] priority)
        {
            var usable = candidates.Where(s => s.SystemClassification != MEPSystemClassification.Fitting
                                               && s.SystemClassification != MEPSystemClassification.Global
                                               && s.SystemClassification != MEPSystemClassification.UndefinedSystemClassification).ToList();
            if (usable.Count == 0) usable = candidates;

            // 1) nome esplicito (dal parser Claude o dalla frase)
            if (!string.IsNullOrWhiteSpace(run.SystemPhrase))
            {
                var best = usable.Select(s => new { s, sc = TextUtil.NameScore(s.Name, run.SystemPhrase) }).OrderByDescending(x => x.sc).First();
                if (best.sc >= 60)
                {
                    _report.Messages.Add(label + "sistema \"" + best.s.Name + "\".");
                    return best.s;
                }
            }

            // 2) classificazione
            if (!string.IsNullOrWhiteSpace(run.SystemClass) && Enum.TryParse(run.SystemClass, out MEPSystemClassification cls))
            {
                var same = usable.Where(s => s.SystemClassification == cls).ToList();
                if (same.Count > 0)
                {
                    var pick = same[0];
                    if (!string.IsNullOrWhiteSpace(run.SystemPhrase))
                    {
                        var best = same.Select(s => new { s, sc = TextUtil.NameScore(s.Name, run.SystemPhrase) }).OrderByDescending(x => x.sc).First();
                        pick = best.s;
                    }
                    _report.Messages.Add(label + "sistema \"" + pick.Name + "\" (" + cls + ").");
                    return pick;
                }
                _report.Warnings.Add(label + "nessun sistema con classificazione " + cls + ": uso un sistema predefinito.");
            }

            // 3) predefinito per priorità
            foreach (var p in priority)
            {
                var s = usable.FirstOrDefault(x => x.SystemClassification == p);
                if (s != null)
                {
                    _report.Messages.Add(label + "sistema predefinito \"" + s.Name + "\".");
                    return s;
                }
            }
            return usable[0];
        }

        private MepSize SnapSize(MepSize requested, MEPCurveType type, string label)
        {
            if (requested == null) return MepSize.Round(50, true);
            if (requested.Shape == SizeShape.Rectangular) return requested;

            var available = AvailableDiameters(type);
            if (available.Count == 0) return requested;
            var d = requested.DiameterMm;
            if (available.Any(a => Math.Abs(a - d) < 0.6)) return requested;
            var nearest = available.OrderBy(a => Math.Abs(a - d)).First();
            _report.Warnings.Add(label + requested + " non è tra le misure del tipo \"" + type.Name + "\": uso " + MepSize.Fmt(nearest) + " mm.");
            return MepSize.Round(nearest, requested.IsNominalDn);
        }

        private List<double> AvailableDiameters(MEPCurveType type)
        {
            var list = new List<double>();
            try
            {
                if (type is PipeType)
                {
                    var rpm = type.RoutingPreferenceManager;
                    var n = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments);
                    for (var i = 0; i < n; i++)
                    {
                        var rule = rpm.GetRule(RoutingPreferenceRuleGroupType.Segments, i);
                        var seg = rule == null ? null : _doc.GetElement(rule.MEPPartId) as Segment;
                        if (seg == null) continue;
                        foreach (MEPSize s in seg.GetSizes()) list.Add(Math.Round(Units.FtToMm(s.NominalDiameter), 1));
                    }
                }
                else if (type is DuctType && ModelCatalogReader.SafeShape(type) == ConnectorProfileType.Round)
                {
                    var settings = DuctSizeSettings.GetDuctSizeSettings(_doc);
                    foreach (MEPSize s in settings[DuctShape.Round]) list.Add(Math.Round(Units.FtToMm(s.NominalDiameter), 1));
                }
            }
            catch
            {
                list.Clear();
            }
            return list.Distinct().OrderBy(x => x).ToList();
        }

        private static int SafeRuleCount(MEPCurveType type, RoutingPreferenceRuleGroupType group)
        {
            try
            {
                return type.RoutingPreferenceManager?.GetNumberOfRules(group) ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool SafePreferTap(MEPCurveType type)
        {
            try
            {
                return type.PreferredJunctionType == JunctionType.Tap;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Elimina gli avvisi non bloccanti durante la transazione, così non compaiono finestre di dialogo.</summary>
        private sealed class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (var f in failuresAccessor.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) failuresAccessor.DeleteWarning(f);
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
