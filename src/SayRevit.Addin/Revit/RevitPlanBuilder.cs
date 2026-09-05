using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>
        /// Tipo di tubazione scelto nella finestra, usato quando il piano non ne indica uno.
        /// Null = tipo predefinito del progetto.
        /// </summary>
        public string PipeTypeName { get; set; }
    }

    /// <summary>Esito della costruzione.</summary>
    public sealed class BuildReport
    {
        public bool Succeeded { get; set; }
        public int Pipes { get; set; }
        public int Ducts { get; set; }
        public int Fittings { get; set; }
        public int Valves { get; set; }
        public List<ElementId> CreatedIds { get; } = new List<ElementId>();
        public List<string> Messages { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public string Summary()
        {
            if (!Succeeded) return "Nessun elemento creato.";
            var s = "Creati " + Pipes + " tratti di tubazione, " + Ducts + " tratti di canale e " + Fittings + " raccordi";
            if (Valves > 0) s += ", più " + Valves + (Valves == 1 ? " valvola" : " valvole");
            return s + ".";
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
        private string _defaultPipeTypeName;

        public RevitPlanBuilder(Document doc)
        {
            _doc = doc;
        }

        public BuildReport Build(MepPlan plan, BuildOptions options)
        {
            options = options ?? new BuildOptions();
            _defaultPipeTypeName = options.PipeTypeName;
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

            PrepareValveFamilies(plan.Runs.SelectMany(r => r.Branches)
                .SelectMany(PiecesOf)
                .Select(v => v.FamilyName));

            using (var t = new Transaction(_doc, "sayRevit: crea MEP da testo"))
            {
                var fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new WarningSwallower(this));
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
                    // catene rimaste ferme davanti a un pezzo da allineare senza gemello: si completano
                    FinishPausedChains();
                    // i bypass uniscono stacchi di tratti diversi: si chiudono quando ci sono tutti
                    CompleteBypasses();

                    if (_report.CreatedIds.Count == 0)
                    {
                        t.RollBack();
                        _report.Warnings.Add("Nessun elemento creato.");
                        return _report;
                    }

                    _doc.Regenerate();
                    CheckCreated();
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
            if (_report.Succeeded) CheckAfterCommit();
            FlushDiag();
            return _report;
        }

        /// <summary>Tutti i pezzi in linea previsti su uno stacco: valvola, catena, pezzo del bypass.</summary>
        private static IEnumerable<MepValve> PiecesOf(MepBranch b)
        {
            if (b == null) yield break;
            if (b.Valve != null) yield return b.Valve;
            foreach (var item in b.Chain)
            {
                if (item.Kind == StubItemKind.Piece && item.Piece != null) yield return item.Piece;
            }
            if (b.Bypass != null && b.Bypass.InlinePiece != null) yield return b.Bypass.InlinePiece;
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
                XYZ offset;
                if (run.OffsetAlongMm.HasValue || run.OffsetSideMm.HasValue)
                {
                    // scostamento esplicito rispetto al primo tratto (es. collettore di ritorno)
                    var left = XYZ.BasisZ.CrossProduct(dir);
                    if (left.GetLength() < 1e-6) left = XYZ.BasisY; // tratto verticale
                    left = left.Normalize();
                    offset = dir * Units.MmToFt(run.OffsetAlongMm ?? 0) + left * Units.MmToFt(run.OffsetSideMm ?? 0);
                }
                else
                {
                    // tratto separato: affiancato al primo, 1 m più in là in -Y
                    offset = new XYZ(0, -Units.MmToFt(1000) * index, 0);
                }
                start = new XYZ(firstStart.X + offset.X, firstStart.Y + offset.Y, zBase + offset.Z);
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

            if (run.CapEnds) CapRunEnds(ctx, label + "fondelli: ");
            return ctx;
        }

        // ------------------------------------------------------- fondelli (Enddeckel)

        /// <summary>Famiglie di fondello per materiale; per gli altri materiali per ora nessun fondello.</summary>
        private static string CapFamilyFor(string pipeTypeName)
        {
            var n = TextUtil.Fold(pipeTypeName ?? string.Empty);
            if (n.Contains("inox")) return "ATZ_INOX-WELD_Enddeckel";
            if (n.Contains("acciaio nero") || n.Contains("c-stahl") || n.Contains("stahl") || n.Contains("nero"))
                return "ATZ_C-STAHL-WELD_5_Enddeckel";
            return null;
        }

        /// <summary>Posiziona un fondello su ciascuna estremità libera del tratto principale.</summary>
        private void CapRunEnds(RunContext ctx, string label)
        {
            if (ctx.Run.Kind != MepKind.Pipe) return;
            var symbol = ResolveCapSymbol(ctx, label, "estremità lasciate aperte");
            if (symbol == null) return;

            PlaceCap(symbol, ctx, ctx.Pieces, ctx.Start, label + "inizio: ");
            PlaceCap(symbol, ctx, ctx.Pieces, ctx.End, label + "fine: ");
        }

        /// <summary>
        /// Tipo di fondello per il materiale del tipo di tubazione del tratto; null (con messaggio,
        /// una volta sola) se il materiale non ne prevede uno o la famiglia non è caricata.
        /// </summary>
        private FamilySymbol ResolveCapSymbol(RunContext ctx, string label, string consequence)
        {
            var typeName = (_doc.GetElement(ctx.TypeId) as MEPCurveType)?.Name ?? string.Empty;
            var familyName = CapFamilyFor(typeName);
            if (familyName == null)
            {
                NoteOnce(label + "nessuna famiglia di fondello definita per \"" + typeName +
                         "\" (per ora solo inox e acciaio nero): " + consequence + ".");
                return null;
            }

            var symbol = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase));
            if (symbol == null)
            {
                WarnOnce(label + "famiglia \"" + familyName + "\" non caricata nel progetto: " + consequence + ".");
                return null;
            }
            if (!symbol.IsActive)
            {
                symbol.Activate();
                _doc.Regenerate();
            }
            return symbol;
        }

        private void PlaceCap(FamilySymbol symbol, RunContext ctx, IEnumerable<MEPCurve> pieces, XYZ point, string label)
        {
            Connector target = null;
            foreach (var piece in pieces)
            {
                target = FindConnectorAt(piece, point);
                if (target != null) break;
            }
            if (target == null)
            {
                _report.Warnings.Add(label + "connettore dell'estremità non trovato.");
                return;
            }
            if (target.IsConnected) return; // estremità già occupata (es. tratti concatenati)

            FamilyInstance cap = null;
            try
            {
                cap = _doc.Create.NewFamilyInstance(target.Origin, symbol, ctx.Level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                _doc.Regenerate();

                var capConn = GetCapConnector(cap);
                if (capConn == null) throw new InvalidOperationException("la famiglia non ha un connettore tubazione.");

                TrySizeFitting(cap, target.Radius);
                _doc.Regenerate();
                capConn = GetCapConnector(cap);

                // orienta il fondello: il suo connettore deve guardare il tubo (assi Z opposti)
                var capZ = capConn.CoordinateSystem.BasisZ;
                var wanted = target.CoordinateSystem.BasisZ.Negate();
                var angle = capZ.AngleTo(wanted);
                if (angle > 1e-6)
                {
                    var axis = capZ.CrossProduct(wanted);
                    if (axis.GetLength() < 1e-9)
                    {
                        // vettori opposti: qualsiasi asse perpendicolare va bene
                        axis = capZ.CrossProduct(XYZ.BasisZ);
                        if (axis.GetLength() < 1e-9) axis = capZ.CrossProduct(XYZ.BasisX);
                    }
                    ElementTransformUtils.RotateElement(_doc, cap.Id,
                        Line.CreateUnbound(capConn.Origin, axis.Normalize()), angle);
                    _doc.Regenerate();
                    capConn = GetCapConnector(cap);
                }

                var delta = target.Origin - capConn.Origin;
                if (delta.GetLength() > 1e-9)
                {
                    ElementTransformUtils.MoveElement(_doc, cap.Id, delta);
                    _doc.Regenerate();
                    capConn = GetCapConnector(cap);
                }

                capConn.ConnectTo(target);
                _report.CreatedIds.Add(cap.Id);
                _report.Fittings++;

                if (Math.Abs(capConn.Radius - target.Radius) > TolFt / 5)
                    _report.Warnings.Add(label + "misura non adattata: fondello Ø" +
                                         MepSize.Fmt(Units.FtToMm(capConn.Radius * 2)) + " mm su tubo Ø" +
                                         MepSize.Fmt(Units.FtToMm(target.Radius * 2)) +
                                         " mm. Indica il nome del parametro diametro della famiglia per aggiungerlo alla ricerca.");
            }
            catch (Exception ex)
            {
                _report.Warnings.Add(label + "fondello non posizionato: " + ex.Message);
                if (cap != null)
                {
                    try { _doc.Delete(cap.Id); } catch { }
                }
            }
        }

        private static Connector GetCapConnector(FamilyInstance cap)
        {
            var cm = cap.MEPModel?.ConnectorManager;
            if (cm == null) return null;
            foreach (Connector c in cm.Connectors)
            {
                if (IsPipeEnd(c)) return c;
            }
            return null;
        }

        /// <summary>
        /// Connettore di estremità di tubo o canale: le famiglie di regolazione (energy valve)
        /// portano anche connettori elettrici, che non c'entrano con il montaggio in linea.
        /// </summary>
        private static bool IsPipeEnd(Connector c)
        {
            try
            {
                if (c.ConnectorType != ConnectorType.End) return false;
                var d = c.Domain;
                return d == Domain.DomainPiping || d == Domain.DomainHvac;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Adatta la misura di un pezzo (fondello, flangia) al tubo. Ogni tentativo viene VERIFICATO
        /// sul raggio del connettore dopo una rigenerazione: ci si ferma solo quando la misura
        /// corrisponde davvero. Ordine: raggio del connettore, poi ogni parametro d'istanza dal nome
        /// riconducibile al diametro — in piedi se è una Lunghezza, in millimetri se è un numero puro
        /// (es. "DN" = 50).
        /// </summary>
        private void TrySizeFitting(FamilyInstance cap, double wantRadius)
        {
            if (RadiusMatches(cap, wantRadius)) return;

            // 1) raggio del connettore
            try
            {
                var capConn = GetCapConnector(cap);
                if (capConn != null) capConn.Radius = wantRadius;
                _doc.Regenerate();
            }
            catch
            {
                // raggio guidato da parametro: si passa alla ricerca per nome
            }
            if (RadiusMatches(cap, wantRadius)) return;

            // 2) parametri d'istanza
            var keywords = new[] { "dn", "nominal", "durchmesser", "diametro", "diameter", "nennweite" };
            var dnMm = Units.FtToMm(wantRadius * 2);
            foreach (Parameter p in cap.Parameters)
            {
                if (p == null || p.IsReadOnly) continue;
                var name = TextUtil.Fold(p.Definition?.Name ?? string.Empty);
                var match = keywords.Any(k => name.Contains(k)) || name == "d" || name == "d1";
                if (!match) continue;
                try
                {
                    if (p.StorageType == StorageType.Double)
                    {
                        // Lunghezza → unità interne (piedi); numero puro (es. "DN") → valore in mm
                        if (IsLengthParam(p)) p.Set(wantRadius * 2);
                        else p.Set(dnMm);
                    }
                    else if (p.StorageType == StorageType.Integer)
                    {
                        p.Set((int)Math.Round(dnMm));
                    }
                    else
                    {
                        continue;
                    }
                    _doc.Regenerate();
                }
                catch
                {
                    continue; // parametro non impostabile: si prova il successivo
                }
                if (RadiusMatches(cap, wantRadius)) return;
            }
        }

        private bool RadiusMatches(FamilyInstance cap, double wantRadius)
        {
            try
            {
                var c = GetCapConnector(cap);
                return c != null && Math.Abs(c.Radius - wantRadius) < TolFt / 5; // 1 mm
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLengthParam(Parameter p)
        {
            try
            {
                return p.Definition != null && p.Definition.GetDataType() == SpecTypeId.Length;
            }
            catch
            {
                return true; // in dubbio, trattalo come lunghezza (comportamento precedente)
            }
        }

        // -------------------------------------------------- valvole sugli stacchi

        private List<FamilySymbol> _accessorySymbols;
        private readonly HashSet<string> _saidOnce = new HashSet<string>();

        /// <summary>
        /// Avviso detto una volta sola: le valvole si ripetono su ogni stacco e su entrambi i
        /// collettori, quindi lo stesso problema produrrebbe decine di righe identiche.
        /// </summary>
        private void WarnOnce(string message)
        {
            if (_saidOnce.Add(message)) _report.Warnings.Add(message);
        }

        private void NoteOnce(string message)
        {
            if (_saidOnce.Add(message)) _report.Messages.Add(message);
        }

        // Diagnostica dettagliata del montaggio dei pezzi in linea: troppo lunga per la finestra
        // di riepilogo, finisce in un file accanto alle impostazioni.
        private readonly List<string> _diag = new List<string>();

        public static string DiagPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Settings.FilePath), "diagnostica-valvole.txt"); }
        }

        private void Diag(string line)
        {
            _diag.Add(line);
        }

        private void FlushDiag()
        {
            if (_diag.Count == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiagPath));
                File.WriteAllLines(DiagPath, _diag);
                NoteOnce("Diagnostica del montaggio delle valvole scritta in " + DiagPath);
            }
            catch
            {
                // non bloccante
            }
        }

        /// <summary>Accessori e raccordi per tubazioni caricati nel progetto: le valvole stanno qui.</summary>
        private List<FamilySymbol> AccessorySymbols()
        {
            if (_accessorySymbols != null) return _accessorySymbols;
            _accessorySymbols = new List<FamilySymbol>();
            // accessori, raccordi e attrezzature meccaniche (pompe): tutto ciò che si monta in linea
            foreach (var category in new[] { BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_MechanicalEquipment })
            {
                try
                {
                    _accessorySymbols.AddRange(new FilteredElementCollector(_doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(category)
                        .Cast<FamilySymbol>());
                }
                catch
                {
                    // categoria non disponibile: si prova la successiva
                }
            }
            return _accessorySymbols;
        }

        /// <summary>Famiglie di flangia per materiale, come per i fondelli; null = nessuna flangia.</summary>
        private static string FlangeFamilyFor(string pipeTypeName)
        {
            var n = TextUtil.Fold(pipeTypeName ?? string.Empty);
            if (n.Contains("inox")) return "ATZ_NEUTRAL_6_Flansch";
            if (n.Contains("acciaio nero") || n.Contains("c-stahl") || n.Contains("stahl") || n.Contains("nero"))
                return "ATZ_C-STAHL-WELD_6_Flansch";
            return null;
        }

        /// <summary>
        /// Inserisce la valvola in linea sullo stacco, allineata all'ASSE del tubo, e — per la boax —
        /// tra due flange. Lo stacco viene accorciato fino alla faccia del primo pezzo e riprende
        /// dalla faccia dell'ultimo, così la catena tubo–flangia–valvola–flangia–tubo è continua e
        /// collegata. I pezzi vengono montati oltre la fine dello stacco, dove non c'è nulla con cui
        /// interferire, e portati in posizione solo dopo che si è verificato che ci stiano: se
        /// qualcosa non va, lo stacco resta intero e non si crea geometria a metà.
        /// </summary>
        /// <summary>Valvola (o altro pezzo in linea) con le sue eventuali flange, montate al banco e poi portate in posizione insieme.</summary>
        private sealed class Assembly
        {
            public FamilyInstance Body;
            public FamilyInstance First;
            public FamilyInstance Second;
            /// <summary>Metà luce del corpo (piedi).</summary>
            public double Half;
            /// <summary>Spessore di una flangia (piedi); 0 senza flange.</summary>
            public double FlangeLength;
            /// <summary>
            /// Geometria del corpo che sporge oltre la faccia esterna dell'insieme, lato collettore /
            /// lato utenza (mm): il tubo creato dal connettore la attraversa (es. il sensore della
            /// energy valve Belimo, 150 mm oltre il connettore), quindi NON è tubo libero.
            /// </summary>
            public double OverhangNearMm;
            public double OverhangFarMm;

            /// <summary>Dal centro del corpo alla faccia esterna dell'insieme (piedi).</summary>
            public double Reach
            {
                get { return Half + FlangeLength; }
            }

            public double ReachMm
            {
                get { return Units.FtToMm(Reach); }
            }

            /// <summary>Pezzo dal lato del collettore / dal lato dell'utenza.</summary>
            public FamilyInstance Near
            {
                get { return First ?? Body; }
            }

            public FamilyInstance Far
            {
                get { return Second ?? Body; }
            }

            public List<FamilyInstance> Pieces
            {
                get
                {
                    var list = new List<FamilyInstance> { Body };
                    if (First != null) list.Add(First);
                    if (Second != null) list.Add(Second);
                    return list;
                }
            }
        }

        /// <summary>
        /// Monta al banco il corpo e — se il pezzo le vuole e il materiale ne ha una — le due
        /// flange, tutte allineate a <paramref name="dir"/> con il rollio del pezzo, le flange col
        /// disco verso il corpo. Null se il corpo non si monta (già segnalato).
        /// </summary>
        private Assembly MountAssembly(FamilySymbol symbol, RunContext ctx, MepValve valve, XYZ bench, XYZ dir, double pipeRadius, string what)
        {
            var roll = valve.RollDegrees * Math.PI / 180.0;
            var body = PlaceInline(symbol, ctx, bench, dir, roll, 0, what);
            if (body == null) return null;
            if (valve.Reversed)
            {
                // verso invertito: 180° attorno alla normale del piano di lavoro (resta nel piano),
                // entrata e uscita si scambiano; il rollio è già quello voluto
                if (!TryRotate(body, Line.CreateUnbound(Midpoint(body), PlaneUp(dir, roll)), Math.PI))
                    WarnOnce(what + "verso non invertibile: il pezzo resta col verso della famiglia.");
                else
                    Diag("  verso invertito (180° attorno alla normale al tubo).");
            }
            var asm = new Assembly { Body = body, Half = BodyLength(body, dir) / 2.0 };

            // Flange prima e dopo: solo se il pezzo le vuole e solo se il materiale del tipo ne ha una definita.
            if (valve.WithFlanges)
            {
                var flangeSymbol = ResolveFlangeSymbol(ctx, valve, what);
                if (flangeSymbol != null)
                {
                    // flange della misura del corpo (se il corpo è più piccolo del tubo, la riduzione
                    // va fuori dalle flange); senza attacchi leggibili, della misura del tubo
                    var bodyRadius = EndConnectors(body).Select(SafeRadius).FirstOrDefault(r => r > 0);
                    var flangeRadius = bodyRadius > 0 ? bodyRadius : pipeRadius;
                    // stesso rollio della valvola: le forature restano allineate
                    var first = PlaceInline(flangeSymbol, ctx, bench + dir * Units.MmToFt(1000), dir, roll, flangeRadius, what);
                    var second = PlaceInline(flangeSymbol, ctx, bench + dir * Units.MmToFt(2000), dir, roll, flangeRadius, what);
                    if (first == null || second == null)
                    {
                        WarnOnce(what + "flange non montate: la valvola viene inserita senza.");
                        DeleteAll(new[] { first, second });
                    }
                    else
                    {
                        asm.First = first;
                        asm.Second = second;
                        asm.FlangeLength = BodyLength(first, dir);
                        // il disco della flangia guarda la valvola, il collare guarda il tubo:
                        // sotto la valvola il disco sta in alto, sopra la valvola sta in basso
                        OrientFlange(first, dir, PlaneUp(dir, roll), true, what);
                        OrientFlange(second, dir, PlaneUp(dir, roll), false, what);
                    }
                }
            }
            MeasureOverhang(asm, dir, pipeRadius);
            return asm;
        }

        /// <summary>
        /// Quanto il corpo sporge oltre le facce dell'insieme lungo l'asse: si contano solo i vertici
        /// vicini all'asse (entro due raggi e mezzo più 30 mm), così un attuatore di fianco non conta
        /// e il sensore in linea della energy valve sì. Quel tratto lo attraversa il tubo: non è libero.
        /// </summary>
        private void MeasureOverhang(Assembly asm, XYZ dir, double pipeRadius)
        {
            asm.OverhangNearMm = 0;
            asm.OverhangFarMm = 0;
            try
            {
                var center = Midpoint(asm.Body);
                var bodyR = EndConnectors(asm.Body).Select(SafeRadius).FirstOrDefault(r => r > 0);
                var radius = Math.Max(bodyR, pipeRadius);
                if (radius <= 0) radius = Units.MmToFt(25);
                var limit = 2.5 * radius + Units.MmToFt(30);
                var min = double.MaxValue;
                var max = double.MinValue;
                foreach (var v in Vertices(asm.Body))
                {
                    var rel = v - center;
                    var along = rel.DotProduct(dir);
                    var radial = (rel - dir * along).GetLength();
                    if (radial > limit) continue;
                    if (along < min) min = along;
                    if (along > max) max = along;
                }
                if (min == double.MaxValue) return;
                asm.OverhangNearMm = Math.Max(0, Units.FtToMm(-min - asm.Reach));
                asm.OverhangFarMm = Math.Max(0, Units.FtToMm(max - asm.Reach));
                if (asm.OverhangNearMm > 1 || asm.OverhangFarMm > 1)
                    Diag("  geometria oltre le facce: " + PlanFormatter.Len(asm.OverhangNearMm) + " lato collettore, " +
                         PlanFormatter.Len(asm.OverhangFarMm) + " lato utenza: il tubo la attraversa, non conta come tubo libero.");
            }
            catch
            {
                // solo misura: senza, si ragiona sui connettori
            }
        }

        /// <summary>Porta l'insieme in posizione, corpo centrato in <paramref name="center"/>, sempre lungo l'asse.</summary>
        private bool PositionAssembly(Assembly asm, XYZ center, XYZ dir, string what)
        {
            var moved = CenterAt(asm.Body, center, what);
            if (asm.First != null)
            {
                moved &= CenterAt(asm.First, center - dir * (asm.Half + asm.FlangeLength / 2.0), what);
                moved &= CenterAt(asm.Second, center + dir * (asm.Half + asm.FlangeLength / 2.0), what);
            }
            return moved;
        }

        /// <summary>Collega flangia–corpo–flangia tra loro.</summary>
        private void JoinAssembly(Assembly asm, XYZ center, XYZ dir, string what)
        {
            if (asm.First != null) Join(asm.First, asm.Body, center - dir * asm.Half, what);
            if (asm.Second != null) Join(asm.Body, asm.Second, center + dir * asm.Half, what);
        }

        private void RegisterAssembly(Assembly asm)
        {
            _report.CreatedIds.Add(asm.Body.Id);
            _report.Valves++;
            if (asm.First != null && asm.Second != null)
            {
                _report.CreatedIds.Add(asm.First.Id);
                _report.CreatedIds.Add(asm.Second.Id);
                _report.Fittings += 2;
            }
        }

        private void PlaceValve(RunContext ctx, MEPCurve stub, XYZ stubStart, XYZ dir, MepBranch branch, MepSize size, string label)
        {
            PlaceValve(ctx, stub, stubStart, dir, branch, size, label, 0);
        }

        /// <param name="endMarginMm">
        /// Spazio da lasciare libero alle due estremità del tubo (mm), oltre al pezzo: serve sul
        /// bypass, dove ai due capi del tratto vanno i gomiti.
        /// </param>
        private void PlaceValve(RunContext ctx, MEPCurve stub, XYZ stubStart, XYZ dir, MepBranch branch, MepSize size, string label, double endMarginMm)
        {
            var valve = branch == null ? null : branch.Valve;
            if (valve == null || stub == null) return;

            var what = label + valve.KindLabel + " DN" + MepSize.Fmt(valve.DnMm) + ": ";
            if (ctx.Run.Kind != MepKind.Pipe)
            {
                WarnOnce(what + "le valvole si inseriscono solo sulle tubazioni.");
                return;
            }
            if (valve.DistanceMm <= 0 || valve.DistanceMm >= branch.LengthMm)
            {
                WarnOnce(what + "distanza " + PlanFormatter.Len(valve.DistanceMm) + " fuori dallo stacco (" +
                         PlanFormatter.Len(branch.LengthMm) + "): non inserita.");
                return;
            }

            var symbol = ResolveValveSymbol(valve, what);
            if (symbol == null) return;

            var center = stubStart + dir * Units.MmToFt(valve.DistanceMm);
            // Fine dello stacco com'è stato creato; se la tipologia fissa il tubo DOPO la valvola
            // (circuito diretto) la fine definitiva si decide più sotto, dalla faccia d'uscita
            // dell'ultimo pezzo, e questa resta solo il ripiego se il montaggio fallisce.
            var originalEnd = stubStart + dir * Units.MmToFt(branch.LengthMm);
            var stubEnd = originalEnd;
            var afterMm = branch.LengthAfterValveMm;
            var pipeRadius = PipeRadius(stub);
            // Banco di montaggio sull'asse dello stacco ma oltre la sua fine: il tragitto fino alla
            // posizione finale resta sull'asse, quindi dentro il piano di lavoro del pezzo.
            var farthestMm = Math.Max(branch.LengthMm, valve.DistanceMm + (afterMm ?? 0) + 1000);
            var bench = center + dir * Units.MmToFt(farthestMm + 3000);

            var asm = MountAssembly(symbol, ctx, valve, bench, dir, pipeRadius, what);
            if (asm == null) return;
            var placed = asm.Pieces;
            var body = asm.Body;
            var first = asm.First;
            var second = asm.Second;
            var half = asm.Half;
            var flangeLength = asm.FlangeLength;

            var reach = asm.Reach;
            var reachMm = asm.ReachMm;
            // Con pezzi a spessore quasi nullo (valvole wafer, flange sottili) non c'è niente da
            // togliere: restano infilati sul tubo, che non viene tagliato.
            var cut = reachMm > 1;
            // con il tubo dopo la valvola fissato, la fine dello stacco si adatta: conta solo che
            // il pezzo non arrivi al collettore
            var tooFar = !afterMm.HasValue && valve.DistanceMm + reachMm > branch.LengthMm - 1 - endMarginMm;
            if (cut && (valve.DistanceMm - reachMm < 1 + endMarginMm || tooFar))
            {
                WarnOnce(what + "l'ingombro (" + PlanFormatter.Len(2 * reachMm) + ") non entra nello stacco a " +
                         PlanFormatter.Len(valve.DistanceMm) + " dall'asse" +
                         (endMarginMm > 0 ? " lasciando " + PlanFormatter.Len(endMarginMm) + " ai capi" : "") + ": valvola non inserita.");
                DeleteAll(placed);
                return;
            }

            var nearFace = center - dir * reach;
            var farFace = center + dir * reach;
            // Tubo dopo la valvola di lunghezza fissa: la fine dello stacco è la faccia d'uscita
            // dell'ultimo pezzo (seconda flangia, o la valvola stessa) più quella lunghezza.
            // A zero (stacco cieco) lo stacco si ferma alla flangia: nessun tubo a valle.
            if (afterMm.HasValue) stubEnd = farFace + dir * Units.MmToFt(afterMm.Value);
            var endsAtValve = afterMm.HasValue && afterMm.Value < 1;
            MEPCurve after = null;
            if (cut)
            {
                if (!TrimCurve(stub, stubStart, nearFace))
                {
                    WarnOnce(what + "lo stacco non è stato accorciato per fare posto alla valvola: non inserita.");
                    DeleteAll(placed);
                    return;
                }
                if (!endsAtValve)
                    after = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, farFace, stubEnd, size, label);
            }
            else
            {
                NoteOnce(what + "i connettori della famiglia distano meno di 1 mm: lo stacco non viene tagliato e i pezzi restano infilati sul tubo.");
                // niente da tagliare, ma la fine dello stacco va comunque portata alla misura voluta
                // (al centro del pezzo, se si ferma alla valvola)
                if (afterMm.HasValue && !TrimCurve(stub, stubStart, stubEnd))
                    WarnOnce(what + "lo stacco non è stato portato a " + PlanFormatter.Len(afterMm.Value) + " dopo la valvola.");
            }

            Diag("  montaggio: taglio dello stacco " + (cut ? "sì" : "no") + ", corpo " + body.Id + " luce " +
                 PlanFormatter.Len(Units.FtToMm(2 * half)) + (first == null ? ", senza flange" :
                 ", flange " + first.Id + " e " + second.Id + " spessore " + PlanFormatter.Len(Units.FtToMm(flangeLength))) +
                 ", facce a " + PlanFormatter.Len(Units.FtToMm((nearFace - stubStart).DotProduct(dir))) + " e " +
                 PlanFormatter.Len(Units.FtToMm((farFace - stubStart).DotProduct(dir))) + " dall'inizio dello stacco" +
                 (endsAtValve ? ", si ferma alla faccia d'uscita (nessun tubo a valle)" :
                  afterMm.HasValue ? ", tubo dopo la valvola " + PlanFormatter.Len(afterMm.Value) + " (stacco totale " +
                                     PlanFormatter.Len(Units.FtToMm((stubEnd - stubStart).DotProduct(dir))) + ")" : "") + ".");

            // dal banco alla posizione definitiva, sempre lungo l'asse dello stacco
            var moved = PositionAssembly(asm, center, dir, what);
            if (!moved)
            {
                WarnOnce(what + "pezzi non portati in posizione: annullo l'inserimento.");
                DeleteAll(placed);
                if (after != null) DeleteCurve(after);
                TrimCurve(stub, stubStart, originalEnd);
                return;
            }

            // catena: tubo – flangia – valvola – flangia – tubo
            if (cut)
            {
                Join(stub, asm.Near, nearFace, what);
                JoinAssembly(asm, center, dir, what);
                if (after != null) Join(asm.Far, after, farFace, what);
            }

            RegisterAssembly(asm);

            var ends = CountConnectedEnds(body);
            Diag("  esito: corpo a " + VecMm(Midpoint(body) - center) + " mm dal centro, estremità collegate " + ends + "/2" +
                 (first == null ? "" : ", flangia sotto a " + VecMm(Midpoint(first) - (center - dir * (half + flangeLength / 2.0))) +
                                       " mm, flangia sopra a " + VecMm(Midpoint(second) - (center + dir * (half + flangeLength / 2.0))) + " mm dal previsto") + ".");
            if (cut && ends < 2)
                WarnOnce(what + "inserita ma collegata su " + ends + " estremità su 2: controlla la misura del tipo \"" +
                         symbol.Name + "\" rispetto al tubo.");
        }

        // ------------------------------------------------ catena di pezzi sullo stacco

        /// <summary>Metà bypass costruita su uno stacco: il T, il tubo orizzontale che ne parte e dove finisce.</summary>
        private sealed class BypassHalf
        {
            public string Key;
            public RunContext Ctx;
            public string Label;
            /// <summary>Punto del T sull'asse dello stacco.</summary>
            public XYZ Point;
            /// <summary>Fine del tubo orizzontale (a metà strada tra i due stacchi, in pianta).</summary>
            public XYZ End;
            public MEPCurve BranchPipe;
            /// <summary>Direzione dello stacco (il tratto di raccordo del bypass corre lungo questa).</summary>
            public XYZ Dir;
            public MepSize Size;
            public MepValve InlinePiece;
        }

        private readonly Dictionary<string, List<BypassHalf>> _bypassHalves = new Dictionary<string, List<BypassHalf>>();

        /// <summary>
        /// Monta sullo stacco la catena completa dei pezzi (mix 2 vie): dall'asse del collettore,
        /// il primo pezzo centrato alla sua distanza, poi ogni pezzo a partire dalla faccia
        /// d'uscita del precedente più il tubo libero indicato; i T del bypass spezzano lo stacco e
        /// fanno partire il tubo orizzontale verso lo stacco gemello. Ogni pezzo è montato al banco,
        /// misurato e portato in posizione come la valvola singola; il tubo provvisorio dello
        /// stacco viene via via accorciato alla faccia del pezzo successivo, e alla fine portato
        /// alla lunghezza del tubo dopo l'ultimo pezzo. Un pezzo che non si monta viene saltato
        /// (con avviso) e la catena prosegue.
        /// </summary>
        private void BuildChain(RunContext ctx, MEPCurve stub, XYZ stubStart, XYZ dir, MepBranch branch, MepSize size, string label)
        {
            if (branch == null || stub == null || branch.Chain.Count == 0) return;
            var chainWhat = label + "catena: ";
            if (ctx.Run.Kind != MepKind.Pipe)
            {
                WarnOnce(chainWhat + "la catena di pezzi si monta solo sulle tubazioni.");
                return;
            }
            dir = dir.Normalize();
            var s = new ChainState
            {
                Ctx = ctx,
                StubStart = stubStart,
                Dir = dir,
                Branch = branch,
                Size = size,
                Label = label,
                ChainWhat = chainWhat,
                PipeRadius = PipeRadius(stub),
                ProvisionalEnd = stubStart + dir * Units.MmToFt(branch.LengthMm),
                // banco unico oltre la fine provvisoria: ogni pezzo viene subito portato in posizione
                Bench = stubStart + dir * Units.MmToFt(branch.LengthMm + 6000),
                Pending = stub,
                PendingStart = stubStart
            };
            RunChain(s);
        }

        /// <summary>
        /// Stato di una catena in costruzione: serve a fermarla davanti a un pezzo da allineare con lo
        /// stacco gemello (<see cref="StubItem.AlignKey"/>) e a riprenderla quando si conosce la quota
        /// dell'altro. Le catene ferme a fine costruzione vengono completate alla loro quota naturale.
        /// </summary>
        private sealed class ChainState
        {
            public RunContext Ctx;
            public XYZ StubStart;
            public XYZ Dir;
            public MepBranch Branch;
            public MepSize Size;
            public string Label;
            public string ChainWhat;
            public double PipeRadius;
            public XYZ ProvisionalEnd;
            public XYZ Bench;
            public MEPCurve Pending;          // tubo già esistente, da accorciare alla prossima faccia
            public XYZ PendingStart;          // dove comincia (o comincerà) il tubo verso il prossimo pezzo
            public FamilyInstance PrevPiece;  // ultimo pezzo montato, per la giunzione diretta o col tubo
            public MEPCurve PrevAdapter;      // tratto corto della misura del pezzo dopo l'ultimo pezzo: il prossimo tubo ci si collega con una riduzione
            public double CursorMm;           // faccia d'uscita dell'ultimo elemento, dall'asse del collettore
            public double GapMm;              // tubo libero accumulato dopo il cursore
            public double AbsorbedGapMm;      // tubo libero già speso nel tratto di adattamento dopo l'ultimo pezzo
            public readonly List<string> Built = new List<string>();
            public readonly List<string> Skipped = new List<string>();
            public int Index;                 // prossima voce della catena da costruire
            public Assembly PausedAssembly;   // pezzo da allineare, già montato al banco, in attesa della quota
            public double PausedNaturalMm;    // quota naturale del centro di quel pezzo
            public double? ForcedCenterMm;    // quota imposta al prossimo pezzo allineato (dallo stacco gemello)
        }

        /// <summary>Catene ferme davanti al pezzo da allineare, per chiave "circuito|pezzo".</summary>
        private readonly Dictionary<string, ChainState> _pausedChains = new Dictionary<string, ChainState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A fine costruzione: le catene rimaste senza gemello si completano alla quota naturale.</summary>
        private void FinishPausedChains()
        {
            var pending = _pausedChains.Values.ToList();
            _pausedChains.Clear();
            foreach (var s in pending)
            {
                s.ForcedCenterMm = null;
                RunChain(s);
            }
        }

        private void RunChain(ChainState s)
        {
            var ctx = s.Ctx;
            var stubStart = s.StubStart;
            var dir = s.Dir;
            var branch = s.Branch;
            var label = s.Label;
            var chainWhat = s.ChainWhat;
            var provisionalEnd = s.ProvisionalEnd;
            var bench = s.Bench;
            var built = s.Built;
            var skipped = s.Skipped;
            ChainState resumePartner = null;

            // collega l'inizio di un tubo appena creato a quello che c'è prima (pezzo o tratto di adattamento)
            Action<MEPCurve, XYZ, string> connectStart = (pipe, at, w) =>
            {
                if (pipe == null) return;
                if (s.PrevAdapter != null) MakeTransition(s.PrevAdapter, pipe, at, w);
                else if (s.PrevPiece != null) Join(s.PrevPiece, pipe, at, w);
            };

            for (; s.Index < branch.Chain.Count; s.Index++)
            {
                var index = s.Index;
                var item = branch.Chain[index];
                if (item.Kind == StubItemKind.Gap)
                {
                    var taken = Math.Min(item.LengthMm, s.AbsorbedGapMm);
                    s.AbsorbedGapMm -= taken;
                    s.GapMm += item.LengthMm - taken;
                    continue;
                }

                if (item.Kind == StubItemKind.Tee)
                {
                    // Il raccordo a T consuma tubo su entrambi i lati del suo punto (e se lo stacco cambia
                    // misura al T, Revit aggiunge da sé una riduzione tra il T e il tubo più piccolo):
                    // quanto, si misura al banco con le misure vere. Il tubo libero richiesto prima del T
                    // (s.GapMm: es. il tratto rettilineo dopo la energy valve) deve restare tubo e basta,
                    // senza raccordi dentro: il punto del T sta oltre quel tratto di tutto lo spazio consumato.
                    var afterSize = item.SizeAfterMm > 0 && Math.Abs(item.SizeAfterMm - s.Size.DiameterMm) > 0.5 ? MepSize.Round(item.SizeAfterMm, true) : s.Size;
                    var branchSize = branch.Bypass != null && branch.Bypass.DnMm > 0 && Math.Abs(branch.Bypass.DnMm - afterSize.DiameterMm) > 0.5
                        ? MepSize.Round(branch.Bypass.DnMm, true) : afterSize;
                    var measured = MeasureTeeRoomMm(ctx, bench + dir * Units.MmToFt(4500), dir, s.Size, afterSize, branchSize, chainWhat);
                    var roomMm = (measured.HasValue ? measured.Value : TeeRoomMm(s.Size, branch.Bypass)) + TeeMarginMm;
                    var pointMm = s.CursorMm + s.GapMm + roomMm;
                    if (s.PrevAdapter != null && pointMm - s.CursorMm < AdapterMm + roomMm) pointMm = s.CursorMm + AdapterMm + roomMm; // posto per la riduzione
                    var point = stubStart + dir * Units.MmToFt(pointMm);
                    Diag("  catena: T del bypass a " + PlanFormatter.Len(pointMm) + " dall'asse: " + PlanFormatter.Len(s.GapMm) +
                         " di tubo libero dopo l'elemento precedente, poi " + PlanFormatter.Len(roomMm) + " consumati dal T" +
                         (measured.HasValue ? " (misurati al banco)" : " (stimati)") + ".");

                    MEPCurve before;
                    if (s.Pending != null)
                    {
                        if (!TrimCurve(s.Pending, s.PendingStart, point))
                        {
                            WarnOnce(chainWhat + "tubo non accorciato al T del bypass: T non creato, catena interrotta.");
                            return;
                        }
                        before = s.Pending;
                    }
                    else
                    {
                        before = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, s.PendingStart, point, s.Size, label);
                        if (before == null) return;
                        connectStart(before, s.PendingStart, chainWhat);
                    }
                    // Verifica sui connettori veri: una riduzione appena fatta (dopo il tratto di
                    // adattamento) ha preso il suo; se al tubo "prima" resta meno di quanto il T
                    // consuma, il T si sposta più avanti allungando quel tubo.
                    var beforeEndC = FindNearestEnd(before, point);
                    var beforeStartC = OtherEnd(before, beforeEndC);
                    if (beforeEndC != null && beforeStartC != null)
                    {
                        var freeMm = Units.FtToMm(beforeStartC.Origin.DistanceTo(beforeEndC.Origin));
                        if (freeMm < roomMm - 0.5)
                        {
                            var from = beforeStartC.Origin;
                            var moved = from + dir * Units.MmToFt(roomMm);
                            if (TrimCurve(before, from, moved))
                            {
                                Diag("  catena: T del bypass spostato da " + PlanFormatter.Len(pointMm) + " a " +
                                     PlanFormatter.Len(Units.FtToMm((moved - stubStart).DotProduct(dir))) + " dall'asse: davanti al T c'erano " +
                                     PlanFormatter.Len(freeMm) + " di tubo, ne servono " + PlanFormatter.Len(roomMm) +
                                     (measured.HasValue ? " (misurati al banco)" : " (stimati)") + ".");
                                point = moved;
                                pointMm = Units.FtToMm((point - stubStart).DotProduct(dir));
                            }
                            else
                            {
                                WarnOnce(chainWhat + "davanti al T del bypass restano solo " + PlanFormatter.Len(freeMm) +
                                         " di tubo e non si riesce ad allungarlo: il T potrebbe non riuscire.");
                            }
                        }
                    }

                    // dopo il T lo stacco può cambiare misura (DN dopo il bypass): T ridotto
                    if (item.SizeAfterMm > 0 && Math.Abs(item.SizeAfterMm - s.Size.DiameterMm) > 0.5)
                    {
                        s.Size = MepSize.Round(item.SizeAfterMm, true);
                        Diag("  catena: dal T in poi DN" + MepSize.Fmt(item.SizeAfterMm) + ".");
                    }
                    var after = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, point, provisionalEnd, s.Size, label);
                    if (after == null) return;
                    s.PipeRadius = PipeRadius(after);

                    var tee = PlaceBypassTee(ctx, branch, before, after, point, dir, s.Size, label, item.Label);
                    // il T occupa spazio sull'asse: il cursore riparte dalla sua faccia verso l'utenza,
                    // e il tubo "dopo" comincia lì (Revit lo ha accorciato)
                    var prevFaceMm = s.CursorMm;
                    var afterStartC = FindNearestEnd(after, point);
                    s.PendingStart = afterStartC != null ? afterStartC.Origin : point;
                    var newCursor = (s.PendingStart - stubStart).DotProduct(dir);
                    s.CursorMm = Math.Max(pointMm, Units.FtToMm(newCursor));
                    if (tee != null)
                    {
                        var teeNearMm = EndConnectors(tee).Select(c => Units.FtToMm((c.Origin - stubStart).DotProduct(dir))).DefaultIfEmpty(pointMm).Min();
                        if (teeNearMm < prevFaceMm - 1 && s.PrevPiece != null)
                            WarnOnce(chainWhat + "il T del bypass (faccia a " + PlanFormatter.Len(teeNearMm) + " dall'asse) tocca il pezzo precedente: aumenta il tubo libero attorno ai T.");
                        built.Add(item.Label);
                    }
                    else
                    {
                        skipped.Add(item.Label);
                    }
                    s.Pending = after;
                    s.PrevPiece = null;
                    s.PrevAdapter = null;
                    s.GapMm = 0;
                    continue;
                }

                // ------------------------------------------------------------ pezzo in linea
                var valve = item.Piece;
                if (valve == null) continue;
                var what = label + valve.KindLabel + " DN" + MepSize.Fmt(valve.DnMm) + ": ";
                Assembly asm;
                if (s.PausedAssembly != null)
                {
                    // ripresa: il pezzo è già montato al banco
                    asm = s.PausedAssembly;
                    s.PausedAssembly = null;
                }
                else
                {
                    var symbol = ResolveValveSymbol(valve, what);
                    if (symbol == null)
                    {
                        skipped.Add(item.ToString());
                        continue;
                    }
                    asm = MountAssembly(symbol, ctx, valve, bench, dir, s.PipeRadius, what);
                    if (asm == null)
                    {
                        skipped.Add(item.ToString());
                        continue;
                    }
                }
                var reachMm = asm.ReachMm;
                var pipeRadius = s.PipeRadius;

                // misura degli attacchi del pezzo rispetto al tubo: se diversa (es. energy valve DN50 su
                // uno stacco DN80) prima e dopo il pezzo va un tratto corto della sua misura e una riduzione
                var nearConn = SideConnector(asm.Near, dir, false);
                var farConn = SideConnector(asm.Far, dir, true);
                var nearR = SafeRadius(nearConn);
                var farR = SafeRadius(farConn);
                // famiglia con un attacco solo (energy valve DN50): l'altra estremità ha la stessa
                // misura dell'attacco che c'è, e la riduzione a valle va messa lo stesso
                if (nearR <= 0 && farR > 0 && nearConn == null) nearR = farR;
                if (farR <= 0 && nearR > 0 && farConn == null) farR = nearR;
                var nearMismatch = nearR > 0 && pipeRadius > 0 && Math.Abs(nearR - pipeRadius) > Units.MmToFt(1);
                var farMismatch = farR > 0 && pipeRadius > 0 && Math.Abs(farR - pipeRadius) > Units.MmToFt(1);

                double centerMm;
                if (item.CenterMm.HasValue && s.PrevPiece == null && built.Count == 0)
                    centerMm = item.CenterMm.Value; // prima intercettazione: centrata alla distanza dall'asse
                else
                    centerMm = s.CursorMm + s.GapMm + asm.OverhangNearMm + reachMm; // la sporgenza sta oltre il tubo libero
                var nearMm = centerMm - reachMm;
                var farMm = centerMm + reachMm;
                var direct = s.PrevPiece != null && s.PrevAdapter == null && !nearMismatch && asm.OverhangNearMm < 1 && nearMm - s.CursorMm < 1; // flangia contro flangia, nessun tubo
                // tubo minimo prima del pezzo: qualche millimetro per crearlo, più il posto per le riduzioni, più la sporgenza del pezzo
                var required = (s.PrevPiece != null || s.PrevAdapter != null ? 5.0 : 0.0) + (s.PrevAdapter != null ? AdapterMm : 0) + (nearMismatch ? AdapterMm + 50 : 0) + asm.OverhangNearMm;
                if (!direct && required > 0 && nearMm - s.CursorMm < required)
                {
                    centerMm += required - (nearMm - s.CursorMm);
                    nearMm = centerMm - reachMm;
                    farMm = centerMm + reachMm;
                }

                // ---------------------------------------------- allineamento con lo stacco gemello
                // Il pezzo con una chiave di allineamento (l'intercettazione in cima) deve stare alla
                // stessa quota nelle catene di mandata e ritorno: la prima catena che ci arriva si
                // ferma qui, col pezzo montato al banco; la seconda calcola la quota comune (la più
                // alta delle due naturali), si completa e poi riprende la prima.
                if (!string.IsNullOrWhiteSpace(item.AlignKey) && !string.IsNullOrWhiteSpace(branch.PairKey))
                {
                    var key = branch.PairKey + "|" + item.AlignKey;
                    if (s.ForcedCenterMm.HasValue)
                    {
                        // ripresa: la quota l'ha decisa lo stacco gemello
                        var forced = s.ForcedCenterMm.Value;
                        s.ForcedCenterMm = null;
                        if (forced > centerMm + 0.5)
                        {
                            Diag("  catena: " + item.AlignKey + " portata da " + PlanFormatter.Len(centerMm) + " a " + PlanFormatter.Len(forced) +
                                 " dall'asse per allinearla allo stacco gemello.");
                            NoteOnce(chainWhat + item.AlignKey + " alla stessa quota dello stacco gemello (" + PlanFormatter.Len(forced) +
                                     " dall'asse, +" + PlanFormatter.Len(forced - centerMm) + " di tubo).");
                            centerMm = forced;
                            nearMm = centerMm - reachMm;
                            farMm = centerMm + reachMm;
                        }
                        direct = false;
                    }
                    else
                    {
                        ChainState partner;
                        if (_pausedChains.TryGetValue(key, out partner))
                        {
                            _pausedChains.Remove(key);
                            var target = Math.Max(centerMm, partner.PausedNaturalMm);
                            partner.ForcedCenterMm = target;
                            resumePartner = partner;
                            if (target > centerMm + 0.5)
                            {
                                Diag("  catena: " + item.AlignKey + " portata da " + PlanFormatter.Len(centerMm) + " a " + PlanFormatter.Len(target) +
                                     " dall'asse per allinearla allo stacco gemello.");
                                NoteOnce(chainWhat + item.AlignKey + " alla stessa quota dello stacco gemello (" + PlanFormatter.Len(target) +
                                         " dall'asse, +" + PlanFormatter.Len(target - centerMm) + " di tubo).");
                                centerMm = target;
                                nearMm = centerMm - reachMm;
                                farMm = centerMm + reachMm;
                                direct = false;
                            }
                        }
                        else
                        {
                            // primo dei due: si ferma qui finché l'altro non arriva allo stesso pezzo
                            s.PausedAssembly = asm;
                            s.PausedNaturalMm = centerMm;
                            _pausedChains[key] = s;
                            Diag("  catena: " + item.AlignKey + " in attesa dello stacco gemello (quota naturale " + PlanFormatter.Len(centerMm) + " dall'asse).");
                            return;
                        }
                    }
                }

                if (nearMm < s.CursorMm - 1)
                {
                    WarnOnce(what + "entrerebbe nell'elemento precedente (faccia a " + PlanFormatter.Len(nearMm) + " dall'asse, precedente fino a " +
                             PlanFormatter.Len(s.CursorMm) + "): non inserita.");
                    DeleteAll(asm.Pieces);
                    skipped.Add(item.ToString());
                    continue;
                }
                var center = stubStart + dir * Units.MmToFt(centerMm);
                var nearFace = stubStart + dir * Units.MmToFt(nearMm);
                var farFace = stubStart + dir * Units.MmToFt(farMm);
                var cut = reachMm > 1;

                MEPCurve beforePipe = null; // tubo della misura dello stacco che arriva verso il pezzo
                MEPCurve nearPipe = null;   // tubo che tocca il pezzo (lo stesso, o il tratto di adattamento)
                if (!direct)
                {
                    var bigEnd = nearMismatch ? nearFace - dir * Units.MmToFt(AdapterMm) : nearFace;
                    if (s.Pending != null)
                    {
                        if (!TrimCurve(s.Pending, s.PendingStart, bigEnd))
                        {
                            WarnOnce(what + "lo stacco non è stato accorciato per fare posto al pezzo: non inserita.");
                            DeleteAll(asm.Pieces);
                            skipped.Add(item.ToString());
                            continue;
                        }
                        beforePipe = s.Pending;
                    }
                    else
                    {
                        beforePipe = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, s.PendingStart, bigEnd, s.Size, label);
                        if (beforePipe != null && cut) connectStart(beforePipe, s.PendingStart, what);
                    }
                    nearPipe = beforePipe;
                    if (beforePipe != null && nearMismatch)
                    {
                        var adapter = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, bigEnd, nearFace, SizeOfRadius(nearR), label);
                        if (adapter != null && MakeTransition(beforePipe, adapter, bigEnd, what))
                        {
                            nearPipe = adapter;
                            Diag("  catena: riduzione prima del pezzo, da Ø" + MepSize.Fmt(Math.Round(Units.FtToMm(2 * pipeRadius))) + " a Ø" +
                                 MepSize.Fmt(Math.Round(Units.FtToMm(2 * nearR))) + " mm.");
                        }
                        else
                        {
                            // ripiego: il tubo dello stacco arriva alla faccia, il pezzo resta accostato
                            if (adapter != null) DeleteCurve(adapter);
                            TrimCurve(beforePipe, s.PendingStart, nearFace);
                        }
                    }
                }

                if (!PositionAssembly(asm, center, dir, what))
                {
                    WarnOnce(what + "pezzi non portati in posizione: annullo l'inserimento.");
                    DeleteAll(asm.Pieces);
                    skipped.Add(item.ToString());
                    // il tubo resta accorciato alla faccia prevista: il prossimo pezzo riparte da lì
                    if (beforePipe != null) { s.Pending = null; s.PendingStart = nearFace; s.CursorMm = nearMm; s.GapMm = 0; s.PrevPiece = null; s.PrevAdapter = null; }
                    continue;
                }

                if (cut)
                {
                    if (direct) Join(s.PrevPiece, asm.Near, nearFace, what);
                    else if (nearPipe != null) Join(nearPipe, asm.Near, nearFace, what);
                    JoinAssembly(asm, center, dir, what);
                }
                RegisterAssembly(asm);
                built.Add(item.ToString());
                Diag("  catena: " + valve.KindLabel + " centro a " + PlanFormatter.Len(centerMm) + " dall'asse, facce a " +
                     PlanFormatter.Len(nearMm) + " e " + PlanFormatter.Len(farMm) + (direct ? ", flangia contro flangia" : "") + ".");

                s.PrevPiece = asm.Far;
                s.PrevAdapter = null;
                s.CursorMm = farMm + asm.OverhangFarMm; // il tubo parte dalla faccia, ma è libero solo oltre la sporgenza
                s.GapMm = 0;
                s.Pending = null;
                s.PendingStart = farFace;

                if (cut && farMismatch)
                {
                    // dopo il pezzo: un tratto della sua misura lungo quanto il tubo libero che segue
                    // (così il tratto rettilineo richiesto sta ADIACENTE al pezzo, prima della riduzione),
                    // poi la riduzione verso lo stacco, che mette il tubo successivo quando viene creato
                    double following = 0;
                    for (var k = index + 1; k < branch.Chain.Count && branch.Chain[k].Kind == StubItemKind.Gap; k++)
                        following += branch.Chain[k].LengthMm;
                    var adapterLenMm = Math.Max(AdapterMm, asm.OverhangFarMm + following); // sporgenza + tubo libero richiesto
                    var adapterEnd = farFace + dir * Units.MmToFt(adapterLenMm);
                    var adapter = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, farFace, adapterEnd, SizeOfRadius(farR), label);
                    if (adapter != null)
                    {
                        // con un attacco solo il tratto resta accostato alla faccia (Join lo salta senza avvisi)
                        if (farConn != null) Join(asm.Far, adapter, farFace, what);
                        s.PrevAdapter = adapter;
                        s.PendingStart = adapterEnd;
                        s.CursorMm = farMm + adapterLenMm;
                        s.AbsorbedGapMm = following; // il tubo libero che segue è già in questo tratto
                        Diag("  catena: tratto di adattamento Ø" + MepSize.Fmt(Math.Round(Units.FtToMm(2 * farR))) + " mm lungo " +
                             PlanFormatter.Len(adapterLenMm) + " dopo il pezzo" +
                             (asm.OverhangFarMm > 1 ? " (di cui " + PlanFormatter.Len(asm.OverhangFarMm) + " dentro la sporgenza del pezzo)" : "") +
                             ", riduzione al tubo successivo.");
                    }
                }
            }

            // fine dello stacco: il tubo dopo l'ultimo pezzo, se fissato; altrimenti la lunghezza generica
            var afterMm = branch.LengthAfterValveMm;
            var endMm = afterMm.HasValue ? s.CursorMm + s.GapMm + afterMm.Value : Math.Max(branch.LengthMm, s.CursorMm + s.GapMm);
            var end = stubStart + dir * Units.MmToFt(endMm);
            if (endMm - s.CursorMm < 1)
            {
                if (s.Pending != null) DeleteCurve(s.Pending);
            }
            else if (s.Pending != null)
            {
                if (!TrimCurve(s.Pending, s.PendingStart, end))
                    WarnOnce(chainWhat + "il tubo finale non è stato portato a " + PlanFormatter.Len(endMm) + " dall'asse.");
            }
            else
            {
                if (s.PrevAdapter != null && endMm - s.CursorMm < AdapterMm) { endMm = s.CursorMm + AdapterMm; end = stubStart + dir * Units.MmToFt(endMm); }
                var last = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, s.PendingStart, end, s.Size, label);
                connectStart(last, s.PendingStart, chainWhat);
            }

            NoteOnce(chainWhat + "montati " + built.Count + " elementi (" + string.Join(" → ", built) + "), stacco lungo " +
                     PlanFormatter.Len(endMm) + " dall'asse del collettore." +
                     (skipped.Count == 0 ? string.Empty : " Saltati: " + string.Join(", ", skipped) + "."));

            // lo stacco gemello fermo davanti allo stesso pezzo riparte ora, alla quota comune
            if (resumePartner != null) RunChain(resumePartner);
        }

        /// <summary>Tratto corto della misura del pezzo, prima e dopo un pezzo più piccolo del tubo, su cui si innesta la riduzione (mm).</summary>
        private const double AdapterMm = 100;

        /// <summary>
        /// Tubo libero che deve restare davanti al T del bypass (mm): il raccordo taglia i tubi per
        /// il proprio corpo, circa un diametro e un quarto della misura più grande tra stacco e
        /// bypass, più un margine.
        /// </summary>
        private static double TeeRoomMm(MepSize stub, MepBypass bypass)
        {
            var dn = stub == null ? 50 : stub.DiameterMm;
            if (bypass != null && bypass.DnMm > dn) dn = bypass.DnMm;
            return Math.Ceiling(1.25 * dn + 20);
        }

        /// <summary>Tubo che deve restare comunque davanti al T oltre a quanto il raccordo consuma (mm).</summary>
        private const double TeeMarginMm = 10;

        private readonly Dictionary<string, double?> _teeRoomCache = new Dictionary<string, double?>();

        /// <summary>
        /// Misura al banco quanto tubo il T del bypass consuma dal suo punto verso il tubo "prima"
        /// (mm): raccordo più l'eventuale riduzione che Revit aggiunge da sé quando i due tubi dello
        /// stacco hanno misure diverse. Tre tubi di prova, il T, la lettura del connettore del tubo
        /// "prima" dopo l'inserimento, poi via tutto. Null se la prova non riesce (si stima).
        /// </summary>
        private double? MeasureTeeRoomMm(RunContext ctx, XYZ at, XYZ dir, MepSize before, MepSize after, MepSize branchSize, string what)
        {
            var key = ctx.TypeId + "|" + before.DiameterMm + "|" + after.DiameterMm + "|" + branchSize.DiameterMm;
            double? cached;
            if (_teeRoomCache.TryGetValue(key, out cached)) return cached;

            var side = XYZ.BasisZ.CrossProduct(dir);
            if (side.GetLength() < 1e-6) side = XYZ.BasisY;
            side = side.Normalize();
            var len = Units.MmToFt(800);
            var pipes = new List<MEPCurve>();
            var fittings = new HashSet<ElementId>();
            double? room = null;
            try
            {
                var a = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, at - dir * len, at, before, what + "prova T: ");
                var b = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, at, at + dir * len, after, what + "prova T: ");
                var c = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, at, at + side * len, branchSize, what + "prova T: ");
                foreach (var pipe in new[] { a, b, c }) if (pipe != null) pipes.Add(pipe);
                if (a == null || b == null || c == null) throw new InvalidOperationException("tubi di prova non creati.");
                var c1 = FindConnectorAt(a, at);
                var c2 = FindConnectorAt(b, at);
                var c3 = FindConnectorAt(c, at);
                if (c1 == null || c2 == null || c3 == null) throw new InvalidOperationException("connettori di prova non trovati.");
                var tee = _doc.Create.NewTeeFitting(c1, c2, c3);
                if (tee == null) throw new InvalidOperationException("Revit non ha restituito il raccordo di prova.");
                _doc.Regenerate();
                fittings.Add(tee.Id);
                var face = FindNearestEnd(a, at);
                if (face == null) throw new InvalidOperationException("connettore del tubo di prova non trovato.");
                room = Math.Max(0, Units.FtToMm((at - face.Origin).DotProduct(dir)));
                var faceAfter = FindNearestEnd(b, at);
                Diag("  banco: T del bypass DN" + MepSize.Fmt(before.DiameterMm) + "→DN" + MepSize.Fmt(after.DiameterMm) + " con derivazione DN" +
                     MepSize.Fmt(branchSize.DiameterMm) + " consuma " + PlanFormatter.Len(room.Value) + " verso il tubo prima" +
                     (faceAfter == null ? "" : " e " + PlanFormatter.Len(Math.Max(0, Units.FtToMm((faceAfter.Origin - at).DotProduct(dir)))) + " verso il tubo dopo") + ".");
            }
            catch (Exception ex)
            {
                Diag("  banco: prova del T del bypass non riuscita (" + ex.Message + "): spazio stimato.");
            }
            finally
            {
                // via tutto: i raccordi attaccati ai tubi di prova (T e riduzioni automatiche), poi i tubi
                foreach (var pipe in pipes)
                {
                    try
                    {
                        foreach (Connector pc in pipe.ConnectorManager.Connectors)
                        {
                            if (!IsPipeEnd(pc)) continue;
                            foreach (Connector r in pc.AllRefs)
                            {
                                var owner = r.Owner as FamilyInstance;
                                if (owner == null) continue;
                                fittings.Add(owner.Id);
                                foreach (Connector oc in owner.MEPModel.ConnectorManager.Connectors)
                                    foreach (Connector rr in oc.AllRefs)
                                    {
                                        var o2 = rr.Owner as FamilyInstance;
                                        if (o2 != null) fittings.Add(o2.Id);
                                    }
                            }
                        }
                    }
                    catch
                    {
                        // solo raccolta
                    }
                }
                foreach (var id in fittings)
                {
                    try { if (_doc.GetElement(id) != null) _doc.Delete(id); } catch { }
                }
                foreach (var pipe in pipes) DeleteCurve(pipe);
            }
            _teeRoomCache[key] = room;
            return room;
        }

        /// <summary>L'altro connettore di estremità di un tubo.</summary>
        private static Connector OtherEnd(MEPCurve curve, Connector one)
        {
            if (curve == null || one == null) return null;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (!IsPipeEnd(c)) continue;
                if (c.Id != one.Id) return c;
            }
            return null;
        }

        private static double SafeRadius(Connector c)
        {
            try { return c == null ? 0 : c.Radius; } catch { return 0; }
        }

        private static MepSize SizeOfRadius(double radiusFt)
        {
            return MepSize.Round(Math.Round(Units.FtToMm(2 * radiusFt)), true);
        }

        /// <summary>
        /// Connettore del pezzo dal lato chiesto (verso +dir = utenza, verso -dir = collettore);
        /// null se da quel lato non c'è (famiglie con un attacco solo).
        /// </summary>
        private static Connector SideConnector(FamilyInstance fi, XYZ dir, bool farSide)
        {
            var cs = EndConnectors(fi);
            if (cs.Count == 0) return null;
            if (cs.Count == 1)
            {
                var t = (cs[0].Origin - Midpoint(fi)).DotProduct(dir);
                return farSide ? (t > 0 ? cs[0] : null) : (t < 0 ? cs[0] : null);
            }
            var ordered = cs.OrderBy(c => c.Origin.DotProduct(dir)).ToList();
            return farSide ? ordered[ordered.Count - 1] : ordered[0];
        }

        /// <summary>
        /// Riduzione tra due tubi di misura diversa che si toccano nel punto dato: la transizione
        /// del tipo di tubazione (preferenze di instradamento), che accorcia i tubi quanto le serve.
        /// </summary>
        private bool MakeTransition(MEPCurve a, MEPCurve b, XYZ point, string what)
        {
            try
            {
                var c1 = FindConnectorAt(a, point);
                var c2 = FindConnectorAt(b, point);
                if (c1 == null || c2 == null) throw new InvalidOperationException("connettori non trovati nel punto della riduzione.");
                var fi = _doc.Create.NewTransitionFitting(c1, c2);
                if (fi == null) throw new InvalidOperationException("Revit non ha restituito il raccordo.");
                _doc.Regenerate();
                _report.CreatedIds.Add(fi.Id);
                _report.Fittings++;
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce(what + "riduzione non creata (" + ex.Message + "): i tubi di misura diversa restano accostati ma scollegati. " +
                         "Verifica che il tipo di tubazione abbia una transizione nelle preferenze di instradamento.");
                return false;
            }
        }

        private static Connector FindNearestEnd(MEPCurve curve, XYZ point)
        {
            Connector best = null;
            var bestDist = double.MaxValue;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (!IsPipeEnd(c)) continue;
                var d = c.Origin.DistanceTo(point);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>
        /// T del bypass nel punto dato dello stacco: crea il tubo orizzontale verso lo stacco gemello
        /// (sulla congiungente in pianta, fino a metà strada) e il raccordo a T tra i due tubi dello
        /// stacco e quello. La metà costruita viene registrata: il bypass si chiude quando c'è
        /// anche l'altra (<see cref="CompleteBypasses"/>). Null se il T non si crea.
        /// </summary>
        private FamilyInstance PlaceBypassTee(RunContext ctx, MepBranch branch, MEPCurve before, MEPCurve after, XYZ point, XYZ dir,
            MepSize size, string label, string teeLabel)
        {
            var what = label + (teeLabel ?? "T del bypass") + ": ";
            var bp = branch.Bypass;
            if (bp == null)
            {
                WarnOnce(what + "nessun bypass previsto per questo stacco: T non creato.");
                return null;
            }

            var left = XYZ.BasisZ.CrossProduct(ctx.Dir);
            if (left.GetLength() < 1e-6) left = XYZ.BasisY;
            left = left.Normalize();
            // tubo orizzontale di questa metà: di traverso dalla mandata, lungo la base dal ritorno
            var leg = ctx.Dir * Units.MmToFt(bp.LegAlongMm) + left * Units.MmToFt(bp.LegSideMm);
            if (leg.GetLength() < Units.MmToFt(20))
            {
                WarnOnce(what + "tubo orizzontale del bypass troppo corto (" + PlanFormatter.Len(bp.LegLengthMm) + "): T non creato.");
                return null;
            }
            var hdir = leg.Normalize();
            if (Math.Abs(hdir.DotProduct(dir)) > 0.01)
            {
                WarnOnce(what + "il bypass non è perpendicolare allo stacco: previsto solo per stacchi verticali, T non creato.");
                return null;
            }
            var end = point + leg;
            // il bypass ha il DN di dopo il bypass (di norma quello dello stacco dopo il T)
            if (bp.DnMm > 0 && Math.Abs(bp.DnMm - size.DiameterMm) > 0.5) size = MepSize.Round(bp.DnMm, true);
            var branchPipe = CreateCurve(MepKind.Pipe, ctx.SystemId, ctx.TypeId, ctx.Level.Id, point, end, size, label);
            if (branchPipe == null) return null;

            FamilyInstance tee = null;
            try
            {
                var c1 = FindConnectorAt(before, point);
                var c2 = FindConnectorAt(after, point);
                var c3 = FindConnectorAt(branchPipe, point);
                if (c1 == null || c2 == null || c3 == null) throw new InvalidOperationException("connettori dei tubi non trovati nel punto del T.");
                tee = _doc.Create.NewTeeFitting(c1, c2, c3);
                if (tee == null) throw new InvalidOperationException("Revit non ha restituito il raccordo.");
                _doc.Regenerate();
                _report.CreatedIds.Add(tee.Id);
                _report.Fittings++;
            }
            catch (Exception ex)
            {
                WarnOnce(what + "raccordo a T non creato (" + ex.Message + "): il tubo del bypass resta scollegato. " +
                         "Verifica che il tipo di tubazione abbia un raccordo a T nelle preferenze di instradamento.");
            }

            List<BypassHalf> halves;
            if (!_bypassHalves.TryGetValue(bp.Key, out halves))
            {
                halves = new List<BypassHalf>();
                _bypassHalves[bp.Key] = halves;
            }
            halves.Add(new BypassHalf
            {
                Key = bp.Key, Ctx = ctx, Label = label, Point = point, End = end, BranchPipe = branchPipe,
                Dir = dir, Size = size, InlinePiece = bp.InlinePiece
            });
            Diag("  bypass " + bp.Key + ": T a " + VecMm(point) + ", tubo orizzontale verso " + Vec(hdir) + " lungo " +
                 PlanFormatter.Len(bp.LegLengthMm) + (tee == null ? ", T NON creato." : ", T " + tee.Id + "."));
            return tee;
        }

        /// <summary>
        /// Chiude ogni bypass di cui esistono le due metà: un tratto lungo la direzione degli stacchi
        /// tra le fini dei due tubi orizzontali (che in pianta coincidono), con la valvola di ritegno
        /// al centro e un gomito a ciascun capo. Va chiamato dopo aver costruito tutti i tratti.
        /// </summary>
        private void CompleteBypasses()
        {
            foreach (var kv in _bypassHalves)
            {
                var what = "Bypass " + kv.Key + ": ";
                var halves = kv.Value;
                if (halves.Count != 2)
                {
                    WarnOnce(what + "T presente su " + halves.Count + " stacchi su 2: bypass non chiuso.");
                    continue;
                }
                var a = halves[0];
                var b = halves[1];
                if (a.BranchPipe == null || b.BranchPipe == null) continue;

                var delta = b.End - a.End;
                var rise = delta.DotProduct(a.Dir);
                var planGap = (delta - a.Dir * rise).GetLength();
                if (planGap > Units.MmToFt(20))
                {
                    WarnOnce(what + "i due tubi orizzontali non si incontrano in pianta (scarto " + PlanFormatter.Len(Units.FtToMm(planGap)) +
                             "): bypass non chiuso.");
                    continue;
                }
                var lo = rise >= 0 ? a : b;
                var hi = rise >= 0 ? b : a;
                var hMm = Units.FtToMm(Math.Abs(rise));
                var dn = a.Size == null ? 50 : a.Size.DiameterMm;
                // spazio per un gomito a ciascun capo: circa 1,5 DN di tangente più un margine
                var elbowMm = 1.5 * dn + 20;
                if (hMm < 2 * elbowMm)
                {
                    WarnOnce(what + "dislivello tra i due T di soli " + PlanFormatter.Len(hMm) + ": non c'è posto per i gomiti, bypass non chiuso.");
                    continue;
                }

                var vertical = CreateCurve(MepKind.Pipe, lo.Ctx.SystemId, lo.Ctx.TypeId, lo.Ctx.Level.Id, lo.End, hi.End, lo.Size, what);
                if (vertical == null) continue;

                if (a.InlinePiece != null)
                {
                    var piece = new MepValve
                    {
                        Kind = a.InlinePiece.Kind, FamilyName = a.InlinePiece.FamilyName, TypeName = a.InlinePiece.TypeName,
                        DnMm = a.InlinePiece.DnMm, PnBar = a.InlinePiece.PnBar, WithFlanges = a.InlinePiece.WithFlanges,
                        RollDegrees = a.InlinePiece.RollDegrees, DistanceMm = hMm / 2.0
                    };
                    var fake = new MepBranch { LengthMm = hMm, Valve = piece, Size = lo.Size };
                    PlaceValve(lo.Ctx, vertical, lo.End, a.Dir, fake, lo.Size, what, elbowMm);
                }

                // gomiti: in basso tra il tubo orizzontale e il tratto verticale, in alto con l'ultimo tubo che arriva alla fine
                var top = PipeEndingAt(hi.End) ?? vertical;
                MakeElbow(lo.BranchPipe, vertical, lo.End, what + "gomito in basso: ");
                MakeElbow(hi.BranchPipe, top, hi.End, what + "gomito in alto: ");
                Diag("  bypass " + kv.Key + " chiuso: tratto verticale " + PlanFormatter.Len(hMm) + " tra " + VecMm(lo.End) + " e " + VecMm(hi.End) + ".");
            }
        }

        /// <summary>Tubo creato in questa costruzione con un'estremità libera nel punto dato.</summary>
        private MEPCurve PipeEndingAt(XYZ point)
        {
            foreach (var id in Enumerable.Reverse(_report.CreatedIds))
            {
                var curve = _doc.GetElement(id) as MEPCurve;
                if (curve == null) continue;
                var c = FindConnectorAt(curve, point);
                if (c != null && !c.IsConnected) return curve;
            }
            return null;
        }

        private void MakeElbow(MEPCurve first, MEPCurve second, XYZ point, string what)
        {
            try
            {
                var c1 = FindConnectorAt(first, point);
                var c2 = FindConnectorAt(second, point);
                if (c1 == null || c2 == null) throw new InvalidOperationException("connettori non trovati nel punto del gomito.");
                var fi = _doc.Create.NewElbowFitting(c1, c2);
                if (fi == null) throw new InvalidOperationException("Revit non ha restituito il raccordo.");
                _doc.Regenerate();
                _report.CreatedIds.Add(fi.Id);
                _report.Fittings++;
            }
            catch (Exception ex)
            {
                WarnOnce(what + "gomito non creato (" + ex.Message + "): i tubi restano scollegati.");
            }
        }

        /// <summary>
        /// Colloca un pezzo in linea (valvola o flangia) allineato all'asse del tubo.
        ///
        /// Le famiglie basate sul livello non si inclinano: Revit accetta la rotazione attorno a
        /// un asse orizzontale ma la trasforma in silenzio in una rotazione attorno alla verticale.
        /// Il meccanismo previsto per i pezzi inclinati è la famiglia BASATA SU PIANO DI LAVORO:
        /// prima della costruzione le famiglie delle valvole e delle flange vengono convertite
        /// (vedi PrepareValveFamilies) e qui il pezzo viene creato su un piano che contiene l'asse
        /// del tubo con la direzione di riferimento lungo il tubo. In tutte le famiglie viste il
        /// flusso è lungo l'X della famiglia, che con la direzione di riferimento finisce sul tubo;
        /// lo Z della famiglia va sulla normale del piano, scelta in base al rollio richiesto.
        /// Restano solo rotazioni nel piano (ammesse). Ogni tentativo è verificato sull'asse dei
        /// connettori; se non è in asse viene cancellato e si ripiega sul livello.
        /// </summary>
        private FamilyInstance PlaceInline(FamilySymbol symbol, RunContext ctx, XYZ at, XYZ dir, double roll, double wantRadius, string what)
        {
            try
            {
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    _doc.Regenerate();
                }
            }
            catch (Exception ex)
            {
                WarnOnce(what + "tipo \"" + symbol.Name + "\" non attivabile: " + ex.Message);
                return null;
            }

            dir = dir.Normalize();
            // dove deve andare lo Z della famiglia (normale del piano) e lo Y (secondo asse del piano)
            var zDir = PlaneUp(dir, roll);
            var yDir = zDir.CrossProduct(dir).Normalize();
            Diag(string.Empty);
            Diag(what + "famiglia \"" + ModelCatalogReader.SafeFamilyName(symbol) + "\" tipo \"" + symbol.Name +
                 "\" (" + PlacementTypeOf(symbol) + "): stacco " + Vec(dir) + ", Z famiglia → " + Vec(zDir) + ", Y famiglia → " + Vec(yDir) +
                 ", rollio " + MepSize.Fmt(Math.Round(roll * 180 / Math.PI)) + "°.");

            // Una famiglia rimasta basata sul livello non si inclina: Revit la lascia orizzontale
            // anche quando i connettori dicono il contrario (è così che flange e valvole finivano
            // di traverso). Su un tubo non orizzontale non si monta: meglio saltarla e dirlo.
            if (Math.Abs(dir.Z) > 0.01 && !IsWorkPlaneBased(symbol))
            {
                WarnOnce(what + "la famiglia \"" + ModelCatalogReader.SafeFamilyName(symbol) + "\" è ancora basata sul livello e non è stato possibile " +
                         "convertirla e ricaricarla (vedi gli avvisi sulle famiglie): su uno stacco verticale resterebbe orizzontale, quindi non viene montata. " +
                         "Chiudi eventuali finestre di quella famiglia aperte in Revit e riprova, oppure aprila, attiva \"Basata su piano di lavoro\" e ricaricala.");
                Diag("  saltata: famiglia basata sul livello su stacco non orizzontale.");
                return null;
            }

            var how = "piano di lavoro con direzione";
            var fi = PlaceOnPlane(symbol, at, dir, zDir, yDir, true, wantRadius, what, how);
            if (fi == null)
            {
                how = "piano di lavoro";
                fi = PlaceOnPlane(symbol, at, dir, zDir, yDir, false, wantRadius, what, how);
            }
            if (fi == null)
            {
                how = "livello";
                fi = PlaceOnLevel(symbol, ctx.Level, at, dir, zDir, yDir, wantRadius, what, how);
            }
            if (fi == null)
            {
                WarnOnce(what + "\"" + symbol.Name + "\" non si riesce ad allineare all'asse del tubo: non inserita. " +
                         "Dettagli in " + DiagPath);
                return null;
            }

            // riga di controllo: dice come è stato montato e con che asse, utile quando una
            // famiglia si comporta in modo strano
            NoteOnce(what + "\"" + symbol.Name + "\" montata su " + how + ": " + EndConnectors(fi).Count +
                     " connettori, asse " + Vec(AxisOf(fi)) + ", luce " + PlanFormatter.Len(Units.FtToMm(InlineLength(fi))) +
                     ", ingombro " + ExtentsText(Extents(fi, dir, zDir, yDir)) + ", stacco " + Vec(dir) + ".");
            return fi;
        }

        /// <summary>
        /// Crea il pezzo su un piano di lavoro (X = asse del tubo, Y = yDir, normale = zDir) con o
        /// senza direzione di riferimento, e lo verifica. Il piano viene eliminato se il tentativo fallisce.
        /// </summary>
        private FamilyInstance PlaceOnPlane(FamilySymbol symbol, XYZ at, XYZ dir, XYZ zDir, XYZ yDir, bool withDirection,
            double wantRadius, string what, string how)
        {
            FamilyInstance fi = null;
            SketchPlane sketch = null;
            try
            {
                var plane = Plane.CreateByOriginAndBasis(at, dir, yDir);
                sketch = SketchPlane.Create(_doc, plane);
                fi = withDirection
                    ? _doc.Create.NewFamilyInstance(at, symbol, dir, sketch,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                    : _doc.Create.NewFamilyInstance(at, symbol, sketch,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                _doc.Regenerate();
                var ok = Finish(fi, at, dir, zDir, yDir, wantRadius, what, how);
                if (ok == null) DeleteSketch(sketch);
                return ok;
            }
            catch (Exception ex)
            {
                Diag("  " + how + ": eccezione: " + ex.Message);
                Discard(fi);
                DeleteSketch(sketch);
                return null;
            }
        }

        private FamilyInstance PlaceOnLevel(FamilySymbol symbol, Level level, XYZ at, XYZ dir, XYZ up, XYZ normal,
            double wantRadius, string what, string how)
        {
            FamilyInstance fi = null;
            try
            {
                fi = _doc.Create.NewFamilyInstance(at, symbol, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                _doc.Regenerate();
                return Finish(fi, at, dir, up, normal, wantRadius, what, how);
            }
            catch (Exception ex)
            {
                Diag("  " + how + ": eccezione: " + ex.Message);
                Discard(fi);
                return null;
            }
        }

        private void DeleteSketch(SketchPlane sketch)
        {
            if (sketch == null) return;
            try
            {
                _doc.Delete(sketch.Id);
            }
            catch
            {
                // resta un piano di lavoro vuoto: innocuo
            }
        }

        /// <summary>Dimensiona, orienta, porta in posizione e verifica sui connettori; null (col pezzo cancellato) se non è in asse.</summary>
        private FamilyInstance Finish(FamilyInstance fi, XYZ at, XYZ dir, XYZ up, XYZ normal, double wantRadius, string what, string how)
        {
            if (fi == null) return null;
            if (wantRadius > 0) TrySizeFitting(fi, wantRadius);
            var ends = EndConnectors(fi).Count;
            if (ends == 0)
            {
                WarnOnce(what + "la famiglia \"" + fi.Symbol.Name + "\" non ha connettori di tubazione: non si può montare sul tubo.");
                Diag("  " + how + ": nessun connettore di tubazione, ne servono 2.");
                Discard(fi);
                return null;
            }
            if (ends == 1)
                NoteOnce(what + "la famiglia \"" + fi.Symbol.Name + "\" ha un solo connettore di tubazione: viene collegata da quel lato, " +
                         "l'altra estremità è la faccia opposta letta dalla geometria (il tubo ci arriva ma non risulta collegato).");

            Diag("  " + how + ", appena creata: " + Describe(fi, at, dir, up, normal));
            var oriented = Orient(fi, dir, up, how);
            CenterAt(fi, at, what);
            var aligned = IsAligned(fi, dir);
            // Controllo anche sulla geometria: con una famiglia basata sul livello Revit può
            // riportare i connettori girati come chiesto e lasciare il corpo orizzontale. Se il
            // corpo si estende lungo il tubo meno della luce tra i connettori, il pezzo è di traverso.
            var bodyAlong = 0.0;
            var luce = InlineLength(fi);
            if (aligned && luce > Units.MmToFt(20))
            {
                var ext = Extents(fi, dir, up, normal);
                bodyAlong = ext == null ? luce : ext[0];
                if (bodyAlong < luce - Units.MmToFt(5)) aligned = false;
            }
            Diag("  " + how + ", dopo orientamento: " + Describe(fi, at, dir, up, normal) +
                 " → rotazioni " + (oriented ? "riuscite" : "RIFIUTATE") + ", connettori " + (aligned ? "in asse" : "NON in asse") +
                 (bodyAlong > 0 && bodyAlong < luce - Units.MmToFt(5) ? " (corpo lungo il tubo " + PlanFormatter.Len(Units.FtToMm(bodyAlong)) +
                  " contro una luce di " + PlanFormatter.Len(Units.FtToMm(luce)) + ": è di traverso)" : "") + ".");
            if (aligned) return fi;
            Discard(fi);
            return null;
        }

        /// <summary>
        /// Orienta il pezzo in due rotazioni: prima porta l'asse dei connettori sul tubo (rotazione
        /// minima), poi gira attorno al tubo finché lo Z della famiglia (o lo Y, se l'asse è lo Z)
        /// non guarda "su". Così il rollio è lo stesso per ogni pezzo della stessa famiglia e
        /// dipende solo dal valore scelto, non da come Revit ha creato l'istanza.
        /// </summary>
        private bool Orient(FamilyInstance fi, XYZ dir, XYZ up, string how)
        {
            var axis = AxisOf(fi);
            if (axis == null) return false;
            var d = axis.DotProduct(dir) < 0 ? dir.Negate() : dir;

            var angle = axis.AngleTo(d);
            if (angle > 1e-6)
            {
                var rotAxis = axis.CrossProduct(d);
                if (rotAxis.GetLength() < 1e-9) rotAxis = PerpendicularTo(axis);
                if (!TryRotate(fi, Line.CreateUnbound(Midpoint(fi), rotAxis.Normalize()), angle))
                {
                    Diag("  " + how + ": rotazione dell'asse sul tubo rifiutata da Revit.");
                    return false;
                }
            }

            XYZ reference;
            try
            {
                var t = fi.GetTransform();
                reference = Math.Abs(t.BasisZ.DotProduct(d)) < 0.9 ? t.BasisZ : t.BasisY;
            }
            catch
            {
                return true;
            }
            reference = reference - d * reference.DotProduct(d);
            var wanted = up - d * up.DotProduct(d);
            if (reference.GetLength() < 1e-9 || wanted.GetLength() < 1e-9) return true;
            reference = reference.Normalize();
            wanted = wanted.Normalize();
            var roll = Math.Atan2(reference.CrossProduct(wanted).DotProduct(d), reference.DotProduct(wanted));
            if (Math.Abs(roll) < 1e-6) return true;
            if (!TryRotate(fi, Line.CreateUnbound(Midpoint(fi), d), roll))
            {
                Diag("  " + how + ": rollio attorno al tubo rifiutato da Revit.");
                return false;
            }
            return true;
        }

        /// <summary>Lunghezza del pezzo lungo il tubo: la luce tra i connettori; l'ingombro geometrico solo se i connettori coincidono.</summary>
        private double BodyLength(FamilyInstance fi, XYZ dir)
        {
            var byConnectors = InlineLength(fi);
            if (byConnectors > Units.MmToFt(1)) return byConnectors;
            var up = PerpendicularTo(dir);
            var ext = Extents(fi, dir, up, dir.CrossProduct(up).Normalize());
            var byGeometry = ext == null ? 0 : ext[0];
            Diag("  connettori coincidenti: come lunghezza si usa l'ingombro lungo il tubo, " + PlanFormatter.Len(Units.FtToMm(byGeometry)) + ".");
            return byGeometry;
        }

        /// <summary>Vertici dei solidi dell'istanza (coordinate di modello); vuoto se la famiglia non ha geometria leggibile.</summary>
        private static List<XYZ> Vertices(FamilyInstance fi)
        {
            var list = new List<XYZ>();
            try
            {
                var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
                var ge = fi.get_Geometry(opt);
                if (ge != null) CollectVertices(ge, list);
            }
            catch
            {
                // geometria non leggibile
            }
            return list;
        }

        private static void CollectVertices(GeometryElement ge, List<XYZ> list)
        {
            foreach (var go in ge)
            {
                var inst = go as GeometryInstance;
                if (inst != null)
                {
                    GeometryElement g = null;
                    try { g = inst.GetInstanceGeometry(); } catch { }
                    if (g != null) CollectVertices(g, list);
                    continue;
                }
                var solid = go as Solid;
                if (solid == null || solid.Faces.Size == 0) continue;
                foreach (Face f in solid.Faces)
                {
                    Mesh mesh = null;
                    try { mesh = f.Triangulate(); } catch { }
                    if (mesh == null) continue;
                    foreach (XYZ v in mesh.Vertices) list.Add(v);
                }
            }
        }

        /// <summary>Ingombro della geometria lungo tre direzioni (piedi); null senza geometria.</summary>
        private static double[] Extents(FamilyInstance fi, XYZ a, XYZ b, XYZ c)
        {
            var vs = Vertices(fi);
            if (vs.Count == 0) return null;
            var axes = new[] { a, b, c };
            var ext = new double[3];
            for (var i = 0; i < 3; i++)
            {
                var min = double.MaxValue;
                var max = double.MinValue;
                foreach (var v in vs)
                {
                    var d = v.DotProduct(axes[i]);
                    if (d < min) min = d;
                    if (d > max) max = d;
                }
                ext[i] = max - min;
            }
            return ext;
        }

        private static string ExtentsText(double[] ext)
        {
            if (ext == null) return "sconosciuto";
            return "lungo il tubo " + PlanFormatter.Len(Units.FtToMm(ext[0])) + ", di traverso " +
                   PlanFormatter.Len(Units.FtToMm(ext[1])) + " × " + PlanFormatter.Len(Units.FtToMm(ext[2]));
        }

        private static bool IsWorkPlaneBased(FamilySymbol symbol)
        {
            try
            {
                if (symbol.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased) return true;
                var p = symbol.Family.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED);
                return p != null && p.AsInteger() == 1;
            }
            catch
            {
                return true; // in dubbio si prova: la verifica sui connettori resta
            }
        }

        private static string PlacementTypeOf(FamilySymbol symbol)
        {
            try
            {
                return symbol.Family.FamilyPlacementType.ToString();
            }
            catch
            {
                return "collocazione sconosciuta";
            }
        }

        /// <summary>Riga di diagnostica completa: connettori, orientamento dell'istanza, ingombro, posizione.</summary>
        private string Describe(FamilyInstance fi, XYZ at, XYZ dir, XYZ up, XYZ normal)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var lp = fi.Location as LocationPoint;
                var origin = lp != null ? lp.Point : at;
                sb.Append("posizione ").Append(VecMm(origin - at)).Append(" mm dal bersaglio");
                var t = fi.GetTransform();
                sb.Append("; istanza X ").Append(Vec(t.BasisX)).Append(" Y ").Append(Vec(t.BasisY)).Append(" Z ").Append(Vec(t.BasisZ));
                var i = 0;
                foreach (var c in EndConnectors(fi))
                {
                    XYZ z = null;
                    try { z = c.CoordinateSystem.BasisZ; } catch { }
                    sb.Append("; conn").Append(++i).Append(" a ").Append(VecMm(c.Origin - origin)).Append(" mm, verso ")
                      .Append(Vec(z)).Append(", r ").Append(MepSize.Fmt(Math.Round(Units.FtToMm(c.Radius), 1))).Append(" mm");
                }
                sb.Append("; ingombro ").Append(ExtentsText(Extents(fi, dir, up, normal)));
            }
            catch (Exception ex)
            {
                sb.Append(" [descrizione interrotta: ").Append(ex.Message).Append("]");
            }
            return sb.ToString();
        }

        private static string VecMm(XYZ v)
        {
            if (v == null) return "?";
            return "(" + MepSize.Fmt(Math.Round(Units.FtToMm(v.X))) + "; " + MepSize.Fmt(Math.Round(Units.FtToMm(v.Y))) +
                   "; " + MepSize.Fmt(Math.Round(Units.FtToMm(v.Z))) + ")";
        }

        /// <summary>
        /// Asse di scorrimento del pezzo: la DIREZIONE del connettore (CoordinateSystem.BasisZ),
        /// non il segmento che unisce i due connettori. Su corpi sottili — valvola a farfalla,
        /// flangia — i due connettori sono vicinissimi o sfalsati e quel segmento punta dove capita:
        /// è così che i pezzi finivano di traverso al tubo. È lo stesso criterio con cui vengono
        /// orientati i fondelli, che nel modello risultano corretti.
        /// </summary>
        private static XYZ AxisOf(FamilyInstance fi)
        {
            var cs = EndConnectors(fi);
            foreach (var c in cs)
            {
                try
                {
                    var z = c.CoordinateSystem.BasisZ;
                    if (z != null && z.GetLength() > 1e-9) return z.Normalize();
                }
                catch
                {
                    // connettore senza sistema di riferimento: si prova il successivo
                }
            }
            if (cs.Count < 2) return null;
            var v = cs[1].Origin - cs[0].Origin;
            return v.GetLength() < 1e-9 ? null : v.Normalize();
        }

        private bool TryRotate(FamilyInstance fi, Line axis, double angle)
        {
            try
            {
                ElementTransformUtils.RotateElement(_doc, fi.Id, axis, angle);
                _doc.Regenerate();
                return true;
            }
            catch
            {
                // famiglia non orientabile in questa posizione: l'esito viene verificato dal chiamante
                return false;
            }
        }

        /// <summary>Sposta il pezzo finché il punto medio tra i suoi connettori non cade sul centro.</summary>
        private bool CenterAt(FamilyInstance fi, XYZ center, string what)
        {
            var delta = center - Midpoint(fi);
            if (delta.GetLength() < 1e-9) return true;
            try
            {
                ElementTransformUtils.MoveElement(_doc, fi.Id, delta);
                _doc.Regenerate();
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce(what + "pezzo non spostabile in posizione: " + ex.Message);
                return false;
            }
        }

        private static XYZ Midpoint(FamilyInstance fi)
        {
            var ends = EndPoints(fi);
            return (ends[0] + ends[1]) / 2.0;
        }

        private static bool IsAligned(FamilyInstance fi, XYZ dir)
        {
            var axis = AxisOf(fi);
            return axis != null && Math.Abs(axis.DotProduct(dir)) > 0.999;
        }

        /// <summary>Una perpendicolare qualsiasi alla direzione, sempre la stessa a parità di direzione.</summary>
        private static XYZ PerpendicularTo(XYZ dir)
        {
            var up = Math.Abs(dir.Z) > 0.9 ? XYZ.BasisY : XYZ.BasisZ;
            var p = dir.CrossProduct(up);
            if (p.GetLength() < 1e-9) p = dir.CrossProduct(XYZ.BasisX);
            return p.Normalize();
        }

        /// <summary>
        /// Asse Y del piano di lavoro: la perpendicolare all'asse del tubo girata del rollio chiesto.
        /// È così che si ottiene la rotazione attorno all'asse (la boax va messa a 90°) senza dover
        /// ruotare il pezzo fuori dal suo piano di lavoro, cosa che Revit rifiuta.
        /// </summary>
        private static XYZ PlaneUp(XYZ dir, double roll)
        {
            var y = PerpendicularTo(dir);
            if (Math.Abs(roll) < 1e-9) return y;
            var z = dir.CrossProduct(y).Normalize();
            return (y * Math.Cos(roll) + z * Math.Sin(roll)).Normalize();
        }

        /// <summary>
        /// Gira la flangia in modo che il DISCO (la parte più larga) guardi la valvola e il collare
        /// il tubo. Il lato del disco si legge dalla geometria: si confronta quanto sporge dal
        /// proprio asse la geometria vicino a un'estremità e vicino all'altra. Se le due estremità
        /// sporgono uguali (flangia simmetrica) non c'è nulla da girare.
        /// </summary>
        private void OrientFlange(FamilyInstance flange, XYZ dir, XYZ zDir, bool discTowardPlusDir, string what)
        {
            var discAtPlus = DiscAtPlusEnd(flange, dir);
            if (discAtPlus == null)
            {
                Diag("  flangia: disco e collare non distinguibili dalla geometria, lasciata com'è.");
                return;
            }
            if (discAtPlus.Value == discTowardPlusDir) return;

            // 180° attorno alla normale del piano di lavoro: resta nel piano, quindi è ammessa anche
            // per le famiglie basate su piano di lavoro; l'asse del flusso si inverte
            var ok = TryRotate(flange, Line.CreateUnbound(Midpoint(flange), zDir), Math.PI);
            var after = DiscAtPlusEnd(flange, dir);
            if (ok && after != null && after.Value != discTowardPlusDir)
            {
                ok = TryRotate(flange, Line.CreateUnbound(Midpoint(flange), PerpendicularTo(dir)), Math.PI);
                after = DiscAtPlusEnd(flange, dir);
            }
            Diag("  flangia girata di 180° per portare il disco verso la valvola: " + (ok ? "riuscita" : "RIFIUTATA") +
                 ", disco ora " + (after == null ? "?" : after.Value ? "verso +asse" : "verso -asse") + ".");
            if (!ok) WarnOnce(what + "flangia non girabile: il collare potrebbe guardare la valvola invece del tubo.");
        }

        /// <summary>
        /// True se il disco della flangia (la parte che sporge di più dall'asse) sta all'estremità
        /// verso +dir, false se verso -dir, null se non si distingue.
        /// </summary>
        private static bool? DiscAtPlusEnd(FamilyInstance flange, XYZ dir)
        {
            var vs = Vertices(flange);
            if (vs.Count == 0) return null;
            var m = Midpoint(flange);
            var min = double.MaxValue;
            var max = double.MinValue;
            foreach (var v in vs)
            {
                var t = (v - m).DotProduct(dir);
                if (t < min) min = t;
                if (t > max) max = t;
            }
            var span = max - min;
            if (span < Units.MmToFt(2)) return null;

            double lowReach = 0, highReach = 0;
            foreach (var v in vs)
            {
                var rel = v - m;
                var t = rel.DotProduct(dir);
                var lateral = (rel - dir * t).GetLength();
                if (t < min + span * 0.3 && lateral > lowReach) lowReach = lateral;
                if (t > max - span * 0.3 && lateral > highReach) highReach = lateral;
            }
            if (Math.Abs(highReach - lowReach) < Units.MmToFt(3)) return null;
            return highReach > lowReach;
        }

        /// <summary>Accorcia il tubo al segmento indicato; false se non è possibile.</summary>
        private bool TrimCurve(MEPCurve curve, XYZ from, XYZ to)
        {
            try
            {
                var lc = curve.Location as LocationCurve;
                if (lc == null || from.DistanceTo(to) < TolFt) return false;
                lc.Curve = Line.CreateBound(from, to);
                _doc.Regenerate();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Collega i due elementi nel punto in cui si toccano.</summary>
        private void Join(Element a, Element b, XYZ point, string what)
        {
            if (a == null || b == null) return;
            var ca = NearestConnector(a, point);
            var cb = NearestConnector(b, point);
            if (ca == null || cb == null)
            {
                if ((ca == null && HasSingleConnector(a)) || (cb == null && HasSingleConnector(b)))
                {
                    Diag("  giunzione saltata: la famiglia ha un solo connettore e questo è il lato senza.");
                    return;
                }
                WarnOnce(what + "connettori non trovati nel punto di giunzione: pezzo lasciato scollegato.");
                return;
            }
            try
            {
                // misure diverse (es. filtro DN40 su un circuito DN25): collegarli porta Revit a
                // invertire o cancellare il tubo al commit; meglio lasciarli accostati e dirlo
                double ra = 0, rb = 0;
                try { ra = ca.Radius; rb = cb.Radius; } catch { }
                if (ra > 0 && rb > 0 && Math.Abs(ra - rb) > Units.MmToFt(1))
                {
                    WarnOnce(what + "misure diverse nel punto di giunzione (Ø" + MepSize.Fmt(Math.Round(Units.FtToMm(2 * ra))) + " e Ø" +
                             MepSize.Fmt(Math.Round(Units.FtToMm(2 * rb))) + " mm): pezzi accostati ma non collegati, serve una riduzione o una famiglia della misura giusta.");
                    return;
                }
                if (!ca.IsConnectedTo(cb)) ca.ConnectTo(cb);
            }
            catch (Exception ex)
            {
                WarnOnce(what + "collegamento non riuscito: " + ex.Message);
            }
        }

        private static ConnectorManager ConnectorsOf(Element e)
        {
            var curve = e as MEPCurve;
            if (curve != null) return curve.ConnectorManager;
            var fi = e as FamilyInstance;
            return fi == null || fi.MEPModel == null ? null : fi.MEPModel.ConnectorManager;
        }

        private static Connector NearestConnector(Element e, XYZ point)
        {
            var cm = ConnectorsOf(e);
            if (cm == null) return null;
            Connector best = null;
            var bestDist = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                if (!IsPipeEnd(c)) continue;
                var d = c.Origin.DistanceTo(point);
                if (d >= bestDist) continue;
                bestDist = d;
                best = c;
            }
            return bestDist <= TolFt * 4 ? best : null;
        }

        private static List<Connector> EndConnectors(Element e)
        {
            var list = new List<Connector>();
            var cm = ConnectorsOf(e);
            if (cm == null) return list;
            foreach (Connector c in cm.Connectors)
            {
                if (IsPipeEnd(c)) list.Add(c);
            }
            return list;
        }

        private static List<Connector> EndConnectorsAlong(Element e, XYZ dir)
        {
            return EndConnectors(e).OrderBy(c => c.Origin.DotProduct(dir)).ToList();
        }

        /// <summary>
        /// Le due estremità del pezzo sul suo asse: le origini dei connettori. Se la famiglia ha un
        /// connettore solo (la energy valve DN50 è così), la seconda estremità è la faccia opposta
        /// letta dalla geometria: il corpo si estende dal connettore in verso contrario alla sua
        /// direzione. Così anche quei pezzi si centrano e si misurano come gli altri; restano
        /// collegati da un lato solo.
        /// </summary>
        private static List<XYZ> EndPoints(FamilyInstance fi)
        {
            var cs = EndConnectors(fi);
            if (cs.Count == 2) return new List<XYZ> { cs[0].Origin, cs[1].Origin };
            if (cs.Count > 2)
            {
                // raccordo a T o croce: l'ordine dei connettori non è stabile (cambia al commit),
                // quindi si usa il punto di inserimento, che non dipende da quale coppia si legge
                var lpt = fi.Location as LocationPoint;
                if (lpt != null) return new List<XYZ> { lpt.Point, lpt.Point };
                var avg = XYZ.Zero;
                foreach (var c in cs) avg += c.Origin;
                avg /= cs.Count;
                return new List<XYZ> { avg, avg };
            }
            if (cs.Count == 1)
            {
                var o = cs[0].Origin;
                XYZ z = null;
                try { z = cs[0].CoordinateSystem.BasisZ; } catch { }
                if (z != null && z.GetLength() > 1e-9)
                {
                    z = z.Normalize();
                    // solo i vertici vicini all'asse: il corpo del tubo, non l'attuatore che sporge
                    // di lato e si allunga oltre la faccia d'attacco
                    double radius = 0;
                    try { radius = cs[0].Radius; } catch { }
                    var lateralMax = Math.Max(2.0 * radius, Units.MmToFt(30));
                    double far = 0;
                    foreach (var v in Vertices(fi))
                    {
                        var rel = v - o;
                        var t = -rel.DotProduct(z);
                        var lateral = (rel + z * t).GetLength();
                        if (lateral > lateralMax) continue;
                        if (t > far) far = t;
                    }
                    return new List<XYZ> { o, o - z * far };
                }
            }
            var lp = fi.Location as LocationPoint;
            var p = lp == null ? XYZ.Zero : lp.Point;
            return new List<XYZ> { p, p };
        }

        private static bool HasSingleConnector(Element e)
        {
            return e is FamilyInstance && EndConnectors(e).Count == 1;
        }

        /// <summary>Luce che il pezzo occupa sul tubo: distanza tra le estremità misurata sull'asse.</summary>
        private static double InlineLength(FamilyInstance fi)
        {
            var ends = EndPoints(fi);
            var v = ends[1] - ends[0];
            var axis = AxisOf(fi);
            return axis == null ? v.GetLength() : Math.Abs(v.DotProduct(axis));
        }

        private static string Vec(XYZ v)
        {
            if (v == null) return "?";
            return "(" + MepSize.Fmt(Math.Round(v.X, 2)) + "; " + MepSize.Fmt(Math.Round(v.Y, 2)) +
                   "; " + MepSize.Fmt(Math.Round(v.Z, 2)) + ")";
        }

        private static double PipeRadius(MEPCurve curve)
        {
            try
            {
                foreach (Connector c in curve.ConnectorManager.Connectors)
                {
                    if (IsPipeEnd(c)) return c.Radius;
                }
            }
            catch
            {
                // tubo senza connettori leggibili: le flange restano alla misura del tipo
            }
            return 0;
        }

        private void Discard(FamilyInstance fi)
        {
            if (fi == null) return;
            try
            {
                _doc.Delete(fi.Id);
                _doc.Regenerate();
            }
            catch
            {
                // niente da fare: l'elemento resta, ma non viene collegato né contato
            }
        }

        private void DeleteAll(IEnumerable<FamilyInstance> items)
        {
            foreach (var fi in items) Discard(fi);
        }

        /// <summary>Toglie un tubo creato per errore, anche dal conteggio del rapporto.</summary>
        private void DeleteCurve(MEPCurve curve)
        {
            if (curve == null) return;
            try
            {
                _report.CreatedIds.Remove(curve.Id);
                if (curve is Pipe) _report.Pipes--; else _report.Ducts--;
                _doc.Delete(curve.Id);
                _doc.Regenerate();
            }
            catch
            {
                // non bloccante
            }
        }

        private static int CountConnectedEnds(FamilyInstance valve)
        {
            var n = 0;
            foreach (var c in EndConnectors(valve))
            {
                if (c.IsConnected) n++;
            }
            return n;
        }

        /// <summary>Flangia del materiale del tipo di tubazione; null se non ce n'è una definita.</summary>
        private FamilySymbol ResolveFlangeSymbol(RunContext ctx, MepValve valve, string what)
        {
            var pipeType = _doc.GetElement(ctx.TypeId) as MEPCurveType;
            var typeName = pipeType == null ? string.Empty : pipeType.Name;
            var familyName = FlangeFamilyFor(typeName);
            if (familyName == null)
            {
                NoteOnce(what + "nessuna flangia definita per \"" + typeName +
                         "\" (per ora solo inox e acciaio nero): valvola montata senza flange.");
                return null;
            }

            var family = AccessorySymbols()
                .Where(s => string.Equals(ModelCatalogReader.SafeFamilyName(s), familyName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (family.Count == 0)
            {
                WarnOnce(what + "famiglia \"" + familyName + "\" non caricata nel progetto: valvola montata senza flange.");
                return null;
            }
            if (family.Count == 1) return family[0];

            var pick = ValveTypeMatcher.Pick(family.Select(s => s.Name).ToList(), valve.DnMm, valve.PnBar);
            return pick == null
                ? family.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).First()
                : family.First(s => s.Name == pick.TypeName);
        }

        /// <summary>Tipo della famiglia di valvole da usare: quello dell'anteprima, altrimenti sul DN.</summary>
        private FamilySymbol ResolveValveSymbol(MepValve valve, string what)
        {
            var family = AccessorySymbols()
                .Where(s => string.Equals(ModelCatalogReader.SafeFamilyName(s), valve.FamilyName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (family.Count == 0)
            {
                WarnOnce(what + "famiglia \"" + valve.FamilyName + "\" non caricata nel progetto: valvola non inserita.");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(valve.TypeName))
            {
                var exact = family.FirstOrDefault(s => string.Equals(s.Name, valve.TypeName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }

            var pick = ValveTypeMatcher.Pick(family.Select(s => s.Name).ToList(), valve.DnMm, valve.PnBar);
            if (pick == null)
            {
                var first = family.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).First();
                WarnOnce(what + "nessun tipo di \"" + valve.FamilyName +
                         "\" dichiara una misura nel nome: uso \"" + first.Name + "\".");
                return first;
            }
            var chosen = family.First(s => s.Name == pick.TypeName);
            if (!pick.ExactDn)
                NoteOnce(what + "nessun tipo DN" + MepSize.Fmt(valve.DnMm) + " nella famiglia: uso \"" + chosen.Name + "\".");
            return chosen;
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

                if (!branch.Connect)
                {
                    // Nessun raccordo: il tratto principale resta un tubo unico e lo stacco parte
                    // dall'asse, semplicemente sovrapposto.
                    var freeEnd = point + bdir * Units.MmToFt(branch.LengthMm);
                    var free = CreateCurve(ctx.Run.Kind, ctx.SystemId, ctx.TypeId, ctx.Level.Id, point, freeEnd, size, label);
                    if (branch.Chain.Count > 0) BuildChain(ctx, free, point, bdir, branch, size, label);
                    else PlaceValve(ctx, free, point, bdir, branch, size, label);
                    continue;
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

                ConnectBranch(ctx, piece, point, branchCurve, label);
                // La valvola va inserita DOPO il raccordo: inserendola prima si spezzerebbe lo
                // stacco e il connettore da raccordare non sarebbe più su questo elemento.
                if (branch.Chain.Count > 0) BuildChain(ctx, branchCurve, point, bdir, branch, size, label);
                else PlaceValve(ctx, branchCurve, point, bdir, branch, size, label);
            }
        }

        /// <summary>Raccorda lo stacco al tratto principale con una presa o con un T.</summary>
        private void ConnectBranch(RunContext ctx, MEPCurve piece, XYZ point, MEPCurve branchCurve, string label)
        {
            var branchConnector = FindConnectorAt(branchCurve, point);
            if (branchConnector == null)
            {
                _report.Warnings.Add(label + "connettore dello stacco non trovato: stacco lasciato scollegato.");
                return;
            }

            if (ctx.PreferTap && TryTakeoff(branchConnector, piece, label)) return;

            TryTee(ctx, piece, point, branchConnector, label);
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
                if (!IsPipeEnd(c)) continue;
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
            var chosen = ResolveType(_pipeTypes.Cast<MEPCurveType>().ToList(), run, label, _defaultPipeTypeName);
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
            return ResolveType(compatible, run, label, null) as DuctType;
        }

        /// <param name="preferredName">
        /// Tipo scelto nella finestra: vale quando il piano non indica né un tipo esplicito né un materiale.
        /// </param>
        private MEPCurveType ResolveType(List<MEPCurveType> candidates, MepRun run, string label, string preferredName)
        {
            if (candidates.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(run.ExplicitTypeName))
            {
                var exact = FindByExactName(candidates, run.ExplicitTypeName);
                if (exact != null) return exact;

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

            // Tipo scelto dall'utente nella finestra.
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                var exact = FindByExactName(candidates, preferredName);
                if (exact != null) return exact;
                _report.Warnings.Add(label + "il tipo \"" + preferredName + "\" scelto nella finestra non esiste in questo progetto: uso il tipo predefinito.");
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

        /// <summary>
        /// Corrispondenza sul nome esatto: i nomi dei tipi si assomigliano molto
        /// (es. "RM Inoxpres 304 (raffreddamento)" e "RM Inoxpres 316 (acqua potabile)"),
        /// quindi la scelta fatta dall'utente non deve passare dal confronto approssimato.
        /// </summary>
        private static MEPCurveType FindByExactName(List<MEPCurveType> candidates, string name)
        {
            return candidates.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
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
            private readonly RevitPlanBuilder _owner;

            public WarningSwallower(RevitPlanBuilder owner)
            {
                _owner = owner;
            }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                var resolved = false;
                foreach (var f in failuresAccessor.GetFailureMessages())
                {
                    string text = "?";
                    try
                    {
                        text = f.GetDescriptionText() + " — elementi " + string.Join(", ", f.GetFailingElementIds().Select(id => id.ToString()));
                        _owner.Diag("Revit [" + f.GetSeverity() + "]: " + text);
                    }
                    catch
                    {
                        // solo diagnostica
                    }
                    if (f.GetSeverity() == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(f);
                        continue;
                    }
                    // Errore: senza intervento Revit apre una finestra e il banco resta fermo finché
                    // qualcuno non la chiude. Si applica la risoluzione predefinita (di solito la
                    // cancellazione dell'elemento) e lo si dichiara; la verifica dopo il commit
                    // segnala comunque quello che è sparito.
                    try
                    {
                        if (f.HasResolutions())
                        {
                            failuresAccessor.ResolveFailure(f);
                            resolved = true;
                            _owner.WarnOnce("Revit ha segnalato un errore alla chiusura della transazione e si è applicata la risoluzione predefinita: " + text);
                        }
                    }
                    catch
                    {
                        // nessuna risoluzione applicabile: resta la finestra di Revit
                    }
                }
                return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }

        // -------------------------------------------------- famiglie basate su piano di lavoro

        /// <summary>
        /// Le famiglie basate sul livello non si inclinano: Revit accetta la rotazione attorno a un
        /// asse orizzontale ma la trasforma in silenzio in una rotazione attorno alla verticale
        /// ("rotazione riuscita", pezzo ancora orizzontale). Il modo previsto da Revit per montare
        /// un pezzo su un tubo verticale è la famiglia BASATA SU PIANO DI LAVORO. Prima di
        /// costruire, alle famiglie di valvole e flange senza quel flag lo si attiva nella famiglia
        /// stessa (togliendo anche "Sempre verticale") e la si ricarica nel progetto.
        /// Va fatto FUORI dalla transazione: EditFamily non è ammesso con una transazione aperta.
        /// </summary>
        private readonly HashSet<string> _preparedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void PrepareValveFamilies(IEnumerable<string> valveFamilies)
        {
            var names = valveFamilies
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Concat(new[] { "ATZ_NEUTRAL_6_Flansch", "ATZ_C-STAHL-WELD_6_Flansch" })
                .Where(n => _preparedFamilies.Add(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var name in names)
            {
                var symbol = AccessorySymbols().FirstOrDefault(s =>
                    string.Equals(ModelCatalogReader.SafeFamilyName(s), name, StringComparison.OrdinalIgnoreCase));
                Family family = null;
                try { family = symbol == null ? null : symbol.Family; } catch { }
                if (family == null) continue;

                var vertical = ReadFlag(family, BuiltInParameter.FAMILY_ALWAYS_VERTICAL);
                var planeBased = ReadFlag(family, BuiltInParameter.FAMILY_WORK_PLANE_BASED);
                var isFlange = ReadFlag(family, BuiltInParameter.FAMILY_CONTENT_PART_TYPE) == (int)PartType.PipeFlange;
                Diag("Famiglia \"" + name + "\": sempre verticale = " + FlagText(vertical) + ", basata su piano di lavoro = " + FlagText(planeBased) +
                     ", categoria " + SafeCategoryName(family) + ", tipo di parte " + PartTypeText(family) + ".");
                if (planeBased == 1 && vertical != 1 && !isFlange) continue;
                if (PrepareFamily(family, name, isFlange)) _accessorySymbols = null;
            }
        }

        private static string SafeCategoryName(Family family)
        {
            try { return family.FamilyCategory == null ? "?" : family.FamilyCategory.Name; } catch { return "?"; }
        }

        private static string PartTypeText(Family family)
        {
            try
            {
                var p = family.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                return p == null ? "?" : ((PartType)p.AsInteger()).ToString() + " (" + p.AsInteger() + ")";
            }
            catch
            {
                return "?";
            }
        }

        private static int ReadFlag(Family family, BuiltInParameter bip)
        {
            try
            {
                var p = family.get_Parameter(bip);
                return p == null ? -1 : p.AsInteger();
            }
            catch
            {
                return -1;
            }
        }

        private static string FlagText(int v)
        {
            return v < 0 ? "?" : v == 1 ? "sì" : "no";
        }

        /// <summary>
        /// Sistema la famiglia nel suo documento e la ricarica: "Basata su piano di lavoro" (senza
        /// "Sempre verticale") e, per le flange, un tipo di parte diverso da "Flangia tubo".
        ///
        /// Le flange con tipo di parte "Flangia tubo" (PipeFlange) sono GESTITE da Revit: a ogni
        /// rigenerazione le confronta con le preferenze di instradamento del tipo di tubazione e,
        /// se lì le flange sono "Nessuna" (come per i tipi saldati), cancella quelle collegate,
        /// senza avviso, alla chiusura della transazione. È per questo che le flange montate e
        /// collegate tubo–flangia–valvola sparivano al commit. Con un tipo di parte ordinario
        /// restano raccordi normali e Revit non le tocca.
        /// </summary>
        private bool PrepareFamily(Family family, string name, bool demoteFlange)
        {
            Document famDoc = null;
            var madePlaneBased = false;
            try
            {
                famDoc = _doc.EditFamily(family);
                using (var t = new Transaction(famDoc, "sayRevit: preparazione famiglia"))
                {
                    t.Start();
                    var wpb = famDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED);
                    if (wpb == null || wpb.IsReadOnly)
                    {
                        t.RollBack();
                        WarnOnce("Famiglia \"" + name + "\": non è \"Basata su piano di lavoro\" e il flag non è modificabile: " +
                                 "sugli stacchi verticali resterà orizzontale. Aprila, attiva \"Basata su piano di lavoro\" e ricaricala.");
                        return false;
                    }
                    if (wpb.AsInteger() != 1)
                    {
                        wpb.Set(1);
                        madePlaneBased = true;
                    }
                    var vertical = famDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALWAYS_VERTICAL);
                    if (vertical != null && !vertical.IsReadOnly && vertical.AsInteger() == 1)
                    {
                        vertical.Set(0);
                        madePlaneBased = true;
                    }

                    if (demoteFlange)
                    {
                        var partType = famDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                        var newType = partType == null || partType.IsReadOnly ? "non modificabile" : null;
                        if (newType == null)
                        {
                            foreach (var candidate in new[] { PartType.Undefined, PartType.Normal, PartType.Union })
                            {
                                try
                                {
                                    if (partType.Set((int)candidate) && partType.AsInteger() == (int)candidate)
                                    {
                                        newType = candidate.ToString();
                                        break;
                                    }
                                }
                                catch
                                {
                                    // valore non ammesso per questa categoria: si prova il successivo
                                }
                            }
                        }
                        Diag("Famiglia \"" + name + "\": tipo di parte \"Flangia tubo\" → " + (newType ?? "nessun valore accettato") + ".");
                        if (newType == null || newType == "non modificabile")
                        {
                            WarnOnce("Famiglia \"" + name + "\": ha tipo di parte \"Flangia tubo\" e non si riesce a cambiarlo: Revit cancella " +
                                     "le flange collegate ai tipi di tubazione senza flange nelle preferenze di instradamento. " +
                                     "Aprila, imposta un altro tipo di parte e ricaricala.");
                        }
                        else
                        {
                            NoteOnce("Famiglia \"" + name + "\": tipo di parte cambiato da \"Flangia tubo\" a \"" + newType + "\" e ricaricata, " +
                                     "altrimenti Revit cancella le flange collegate (il tipo di tubazione non ha flange nelle preferenze di instradamento).");
                        }
                    }
                    t.Commit();
                }

                // LoadFamily vuole il progetto SENZA transazioni aperte: se fallisce, l'errore vero
                // va riportato così com'è (un secondo tentativo dentro una transazione fallirebbe
                // sempre, nascondendo la causa).
                Family loaded;
                try
                {
                    loaded = famDoc.LoadFamily(_doc, new OverwriteFamily());
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("ricarica nel progetto non riuscita: " + ex.Message, ex);
                }
                Diag("Famiglia \"" + name + "\": " + (madePlaneBased ? "resa basata su piano di lavoro e " : "") + "ricaricata (" +
                     (loaded == null ? "esito ignoto" : "ok") + ").");
                if (madePlaneBased)
                    NoteOnce("Famiglia \"" + name + "\": resa \"Basata su piano di lavoro\" e ricaricata nel progetto, " +
                             "altrimenti Revit non permette di montarla su tubi non orizzontali.");
                return true;
            }
            catch (Exception ex)
            {
                Diag("Famiglia \"" + name + "\": conversione a piano di lavoro fallita: " + ex.Message);
                WarnOnce("Famiglia \"" + name + "\": non sono riuscito a renderla \"Basata su piano di lavoro\" (" + ex.Message +
                         "): sugli stacchi verticali resterà orizzontale. Aprila, attiva il flag e ricaricala.");
                return false;
            }
            finally
            {
                if (famDoc != null)
                {
                    try { famDoc.Close(false); } catch { }
                }
            }
        }

        private sealed class OverwriteFamily : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        // -------------------------------------------------- interasse automatico

        /// <summary>
        /// Interasse minimo senza interferenze: per ogni circuito si monta valvola (e flange) in una
        /// zona di prova, si misura l'ingombro reale di tutti i pezzi, si annulla, e si lascia al
        /// piano il calcolo (<see cref="ManifoldPlan.ApplyAutoSpacing"/>). Va chiamato PRIMA di
        /// ToParseResult, perché l'interasse decide le posizioni degli stacchi.
        /// </summary>
        public void ResolveAutoSpacing(ManifoldPlan plan)
        {
            if (plan == null || !plan.AutoSpacing) return;
            try
            {
                LoadCollections();
                // tutte le famiglie che il piano può montare: base e soglie per DN di ogni elemento
                PrepareValveFamilies(plan.ConfiguredFamilies());

                var footprints = new List<StubFootprint>();
                var extras = new List<StubFootprint>();
                var cache = new Dictionary<string, StubFootprint>();
                foreach (var circuit in plan.ValidCircuits())
                {
                    // lunghezza e valvola secondo la tipologia: il cieco non ha valvola, il
                    // diretto è lungo quanto serve al tubo dopo la valvola
                    var dn = circuit.DnMm;
                    var fp = StubFootprint.PipeOnly(dn, plan.CircuitLengthFor(circuit));
                    var valve = plan.ValveFor(circuit);
                    StubFootprint extra = null;
                    if (valve != null)
                    {
                        // tratto verticale del bypass: sulla verticale della base del ritorno, con la
                        // valvola di ritegno (o il solo tubo); alto quanto lo stacco, per prudenza
                        var bypass = plan.BypassFor(circuit, 0, true);
                        if (bypass != null)
                        {
                            extra = StubFootprint.PipeOnly(dn, plan.CircuitLengthFor(circuit));
                            if (bypass.InlinePiece != null)
                            {
                                var key = "bypass|" + bypass.InlinePiece.FamilyName + "/" + bypass.InlinePiece.TypeName + "/" + bypass.InlinePiece.RollDegrees + "|" + dn;
                                StubFootprint measured;
                                if (!cache.TryGetValue(key, out measured))
                                {
                                    measured = MeasureFootprint(plan, new List<StubItem> { StubItem.Of(bypass.InlinePiece, plan.ValveAxisDistanceMm) }, dn);
                                    cache[key] = measured;
                                }
                                if (measured != null)
                                    extra = extra.Union(new StubFootprint
                                    {
                                        AlongMinMm = measured.AlongMinMm, AlongMaxMm = measured.AlongMaxMm,
                                        SideMinMm = measured.SideMinMm, SideMaxMm = measured.SideMaxMm,
                                        UpMinMm = 0, UpMaxMm = plan.CircuitLengthFor(circuit)
                                    });
                            }
                            extra.SideMinMm += bypass.LegSideMm;
                            extra.SideMaxMm += bypass.LegSideMm;
                        }
                    }
                    extras.Add(extra);
                    if (valve != null)
                    {
                        // con la catena (mix 2 vie) si misurano tutti i pezzi, di mandata e di ritorno
                        var chains = plan.HasChain(circuit)
                            ? new[] { plan.ChainFor(circuit, true), plan.ChainFor(circuit, false) }
                            : new[] { new List<StubItem> { StubItem.Of(valve, valve.DistanceMm) } };
                        foreach (var chain in chains)
                        {
                            var key = string.Join("|", chain.Select(i => i.Kind == StubItemKind.Piece && i.Piece != null
                                ? i.Piece.FamilyName + "/" + i.Piece.TypeName + "/" + i.Piece.WithFlanges + "/" + i.Piece.RollDegrees + "/" + i.CenterMm
                                : i.Kind + "/" + i.LengthMm)) + "|" + dn;
                            StubFootprint measured;
                            if (!cache.TryGetValue(key, out measured))
                            {
                                measured = MeasureFootprint(plan, chain, dn);
                                cache[key] = measured;
                            }
                            fp = fp.Union(measured);
                        }
                    }
                    footprints.Add(fp);
                }

                var r = plan.ApplyAutoSpacing(footprints, extras, plan.HeaderOuterRadiusMm);
                NoteOnce("Interasse automatico: " + MepSize.Fmt(r.SpacingMm) + " mm (minimo richiesto " + MepSize.Fmt(plan.SpacingFloorMm) + " mm).");
                foreach (var n in r.Notes) NoteOnce(n);
                foreach (var w in r.Warnings) WarnOnce(w);
                Diag("Interasse automatico: " + MepSize.Fmt(r.SpacingMm) + " mm.");
            }
            catch (Exception ex)
            {
                WarnOnce("Interasse automatico non calcolato (" + ex.Message + "): uso " + MepSize.Fmt(plan.SpacingMm) + " mm.");
            }
        }

        /// <summary>
        /// Ingombro reale (mm, riferito all'asse dello stacco e al collettore) di valvola e flange di
        /// un circuito, misurato montandole in una zona di prova dentro una transazione annullata.
        /// </summary>
        private StubFootprint MeasureFootprint(ManifoldPlan plan, IList<StubItem> chain, double dnMm)
        {
            var level = _levels == null ? null : _levels.FirstOrDefault();
            var pipeType = _pipeTypes == null ? null
                : _pipeTypes.FirstOrDefault(t => t.Name == plan.PipeTypeName) ?? _pipeTypes.FirstOrDefault();
            if (level == null || pipeType == null) return null;

            var dir = plan.CircuitDirection == DirectionKind.Down ? XYZ.BasisZ.Negate() : XYZ.BasisZ;
            var axisPoint = new XYZ(0, 0, Units.MmToFt(60000)); // zona di prova, lontana dal modello
            var bench = axisPoint + dir * Units.MmToFt(20000);
            var ctx = new RunContext { Level = level, TypeId = pipeType.Id, Run = new MepRun { Kind = MepKind.Pipe }, Dir = dir };
            var pipeRadius = Units.MmToFt(dnMm / 2.0);

            StubFootprint fp = null;
            using (var t = new Transaction(_doc, "sayRevit: misura ingombro valvola"))
            {
                t.Start();
                try
                {
                    // stessa regola della costruzione: primo pezzo centrato alla sua distanza, gli
                    // altri dalla faccia del precedente più il tubo libero; il T vale due DN
                    var pieces = new List<FamilyInstance>();
                    double cursorMm = 0, gapMm = 0;
                    var first = true;
                    foreach (var item in chain)
                    {
                        if (item.Kind == StubItemKind.Gap) { gapMm += item.LengthMm; continue; }
                        if (item.Kind == StubItemKind.Tee) { cursorMm += gapMm + 2 * dnMm; gapMm = 0; continue; }
                        var valve = item.Piece;
                        if (valve == null) continue;
                        var what = "misura " + valve.KindLabel + " DN" + MepSize.Fmt(valve.DnMm > 0 ? valve.DnMm : dnMm) + ": ";
                        var symbol = ResolveValveSymbol(valve, what);
                        // ogni pezzo con le flange della propria misura (dopo il T il DN può cambiare)
                        var pieceRadius = valve.DnMm > 0 ? Units.MmToFt(valve.DnMm / 2.0) : pipeRadius;
                        var asm = symbol == null ? null : MountAssembly(symbol, ctx, valve, bench, dir, pieceRadius, what);
                        if (asm == null) { cursorMm += gapMm + plan.ValveAssemblyAllowanceMm; gapMm = 0; first = false; continue; }
                        var centerMm = first && item.CenterMm.HasValue ? item.CenterMm.Value : cursorMm + gapMm + asm.ReachMm;
                        PositionAssembly(asm, axisPoint + dir * Units.MmToFt(centerMm), dir, what);
                        pieces.AddRange(asm.Pieces);
                        cursorMm = centerMm + asm.ReachMm;
                        gapMm = 0;
                        first = false;
                    }
                    var kindLabel = chain.Count == 1 && chain[0].Piece != null ? chain[0].Piece.KindLabel : "catena di " + chain.Count(i => i.Kind != StubItemKind.Gap) + " elementi";

                    var along = new[] { double.MaxValue, double.MinValue };
                    var side = new[] { double.MaxValue, double.MinValue };
                    var up = new[] { double.MaxValue, double.MinValue };
                    var any = false;
                    foreach (var piece in pieces)
                    {
                        foreach (var v in Vertices(piece))
                        {
                            var rel = v - axisPoint;
                            Grow(along, rel.X);
                            Grow(side, rel.Y);
                            Grow(up, rel.DotProduct(dir));
                            any = true;
                        }
                    }
                    if (any)
                    {
                        fp = new StubFootprint
                        {
                            AlongMinMm = Units.FtToMm(along[0]), AlongMaxMm = Units.FtToMm(along[1]),
                            SideMinMm = Units.FtToMm(side[0]), SideMaxMm = Units.FtToMm(side[1]),
                            UpMinMm = Units.FtToMm(up[0]), UpMaxMm = Units.FtToMm(up[1])
                        };
                        Diag("Ingombro " + kindLabel + " DN" + MepSize.Fmt(dnMm) + " (" + pieces.Count + " pezzi): " + fp);
                    }
                    else
                    {
                        Diag("Ingombro " + kindLabel + " DN" + MepSize.Fmt(dnMm) + ": nessun pezzo montabile, si considera solo il tubo.");
                    }
                }
                finally
                {
                    t.RollBack();
                }
            }
            return fp;
        }

        private static void Grow(double[] range, double v)
        {
            if (v < range[0]) range[0] = v;
            if (v > range[1]) range[1] = v;
        }

        /// <summary>A fine costruzione: gli elementi creati esistono ancora? (Revit può cancellarne risolvendo un errore.)</summary>
        private void CheckCreated()
        {
            var missing = 0;
            var flanges = 0;
            var valves = 0;
            _beforeCommit.Clear();
            foreach (var id in _report.CreatedIds)
            {
                var e = _doc.GetElement(id);
                if (e == null)
                {
                    missing++;
                    Diag("Elemento " + id + " creato ma non più presente prima del commit.");
                    continue;
                }
                var fi = e as FamilyInstance;
                if (fi == null) continue;
                _beforeCommit[id] = Midpoint(fi);
                var fam = TextUtil.Fold(ModelCatalogReader.SafeFamilyName(fi.Symbol));
                if (fam.Contains("flansch") || fam.Contains("flangia")) flanges++;
                else if (fi.Category != null && fi.Category.Id.Value == (long)BuiltInCategory.OST_PipeAccessory) valves++;
            }
            Diag(string.Empty);
            Diag("Verifica finale: " + _report.CreatedIds.Count + " elementi creati, " + missing + " mancanti, " +
                 valves + " accessori (valvole), " + flanges + " flange.");
            if (missing > 0) WarnOnce(missing + " elementi creati risultano cancellati prima del salvataggio: vedi " + DiagPath);
        }

        // posizione (punto medio tra i connettori) di ogni pezzo prima del commit, per confronto dopo
        private readonly Dictionary<ElementId, XYZ> _beforeCommit = new Dictionary<ElementId, XYZ>();

        /// <summary>
        /// Dopo il commit: Revit chiude la transazione con una rigenerazione completa e con la
        /// risoluzione degli errori, che può cancellare o spostare elementi senza che il codice
        /// dentro la transazione se ne accorga. Qui si confronta con quanto verificato prima.
        /// </summary>
        private void CheckAfterCommit()
        {
            var missing = new List<string>();
            var moved = new List<string>();
            foreach (var id in _report.CreatedIds.ToList())
            {
                var e = _doc.GetElement(id);
                if (e == null)
                {
                    missing.Add(id.ToString());
                    _report.CreatedIds.Remove(id);
                    continue;
                }
                var fi = e as FamilyInstance;
                XYZ before;
                if (fi == null || !_beforeCommit.TryGetValue(id, out before)) continue;
                var delta = Midpoint(fi) - before;
                if (delta.GetLength() > Units.MmToFt(1))
                    moved.Add(ModelCatalogReader.SafeFamilyName(fi.Symbol) + " " + id + " di " + VecMm(delta) + " mm");
            }
            Diag("Dopo il commit: " + missing.Count + " elementi spariti" + (missing.Count == 0 ? "" : " (" + string.Join(", ", missing) + ")") +
                 ", " + moved.Count + " pezzi spostati" + (moved.Count == 0 ? "." : ": " + string.Join("; ", moved) + "."));
            if (missing.Count > 0)
                WarnOnce(missing.Count + " elementi creati sono stati cancellati da Revit alla chiusura della transazione: vedi " + DiagPath);
            if (moved.Count > 0)
                WarnOnce(moved.Count + " pezzi sono stati spostati da Revit alla chiusura della transazione: vedi " + DiagPath);
        }
    }
}
