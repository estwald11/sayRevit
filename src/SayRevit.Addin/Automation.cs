using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SayRevit.Addin.Revit;
using SayRevit.Core.Model;

namespace SayRevit.Addin
{
    /// <summary>
    /// Banco di prova senza finestra, chiamato dal caricatore quando trova un file di richiesta in
    /// %APPDATA%\sayRevit\automation\request.txt. Costruisce il collettore con le impostazioni
    /// salvate (le stesse della finestra), esporta un'immagine della vista 3D e scrive riepilogo,
    /// avvisi ed elementi creati su file. Gli elementi della prova precedente vengono cancellati
    /// prima di ricostruire, così il modello resta pulito.
    ///
    /// Richiesta (chiave=valore per riga, tutte facoltative):
    ///   clean=tracked|all|none   cosa cancellare prima (predefinito tracked = solo la prova precedente)
    ///   build=none               si ferma dopo la pulizia, senza costruire
    ///   start=x,y                punto di partenza in pianta (mm), per costruire lontano da quanto già c'è
    ///   Manifold*=...            qualsiasi impostazione del collettore, sovrascrive quella salvata
    /// Risultato: result.txt (riepilogo), created.txt (id), view.png (immagine), più la diagnostica.
    /// </summary>
    public static class Automation
    {
        public static string Folder
        {
            get { return Path.Combine(Path.GetDirectoryName(Settings.FilePath), "automation"); }
        }

        public static void Run(UIApplication app, string requestPath)
        {
            var log = new StringBuilder();
            var started = DateTime.Now;
            Directory.CreateDirectory(Folder);
            var resultPath = Path.Combine(Folder, "result.txt");
            var createdPath = Path.Combine(Folder, "created.txt");
            var imagePath = Path.Combine(Folder, "view.png");
            try { File.Delete(resultPath); } catch { }
            try { File.Delete(imagePath); } catch { }

            try
            {
                var request = ReadRequest(requestPath);
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null || uidoc.Document == null) throw new InvalidOperationException("Nessun documento aperto.");
                var doc = uidoc.Document;
                log.AppendLine("Documento: " + doc.Title);

                // 1) pulizia
                var clean = Value(request, "clean", "tracked");
                var deleted = Clean(doc, clean, createdPath);
                log.AppendLine("Cancellati " + deleted + " elementi (" + clean + ").");

                // 2) piano dalle impostazioni (con eventuali sovrascritture della richiesta)
                var settings = Settings.Load();
                ApplyOverrides(settings, request);
                var catalog = ModelCatalogReader.Read(doc);
                var manifold = ManifoldPlanFactory.FromSettings(settings, catalog);

                if (string.Equals(Value(request, "build", "yes"), "none", StringComparison.OrdinalIgnoreCase))
                {
                    log.AppendLine("Solo pulizia: nessuna costruzione richiesta.");
                    return;
                }

                var builder = new RevitPlanBuilder(doc);
                if (manifold.AutoSpacing) builder.ResolveAutoSpacing(manifold);
                var result = manifold.ToParseResult();
                log.AppendLine("Piano: " + manifold.Summary().Replace("\r", string.Empty).Replace("\n", " | "));
                foreach (var n in result.Notes) log.AppendLine("• " + n);
                foreach (var w in result.Warnings) log.AppendLine("⚠ " + w);
                if (!result.Success)
                {
                    log.AppendLine("PIANO NON VALIDO.");
                    return;
                }

                // 3) costruzione
                var options = new BuildOptions
                {
                    DefaultElevationMm = settings.DefaultElevationMm,
                    PipeTypeName = manifold.PipeTypeName
                };
                // start=x,y (mm): punto di partenza in pianta, per costruire lontano da quanto già c'è
                var start = Value(request, "start", null);
                if (start != null)
                {
                    var parts = start.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    double sx, sy;
                    if (parts.Length >= 2 &&
                        double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sx) &&
                        double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sy))
                    {
                        options.StartPoint = new XYZ(SayRevit.Addin.Revit.Units.MmToFt(sx), SayRevit.Addin.Revit.Units.MmToFt(sy), 0);
                        log.AppendLine("Partenza: (" + sx + "; " + sy + ") mm.");
                    }
                }
                var report = builder.Build(result.Plan, options);
                log.AppendLine(report.Succeeded ? "COSTRUZIONE RIUSCITA" : "COSTRUZIONE FALLITA");
                log.AppendLine(report.Summary());
                foreach (var m in report.Messages) log.AppendLine("• " + m);
                foreach (var w in report.Warnings) log.AppendLine("⚠ " + w);
                File.WriteAllLines(createdPath, report.CreatedIds.Select(id => id.Value.ToString(CultureInfo.InvariantCulture)));

                // 4) immagine della vista 3D
                try
                {
                    var exported = ExportView(uidoc, report.CreatedIds, imagePath);
                    log.AppendLine("Immagine: " + (exported ?? "non esportata"));
                }
                catch (Exception ex)
                {
                    log.AppendLine("Immagine non esportata: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("ERRORE: " + ex);
            }
            finally
            {
                log.AppendLine("Durata: " + (DateTime.Now - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                try { File.WriteAllText(resultPath, log.ToString()); } catch { }
                try { File.Delete(requestPath); } catch { }
            }
        }

        private static Dictionary<string, string> ReadRequest(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return map;
            foreach (var line in File.ReadAllLines(path))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            return map;
        }

        private static string Value(Dictionary<string, string> map, string key, string fallback)
        {
            string v;
            return map.TryGetValue(key, out v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
        }

        /// <summary>Le chiavi "Manifold*" della richiesta sovrascrivono le impostazioni, con la stessa sintassi del file settings.txt.</summary>
        private static void ApplyOverrides(Settings settings, Dictionary<string, string> request)
        {
            var overrides = request.Where(kv => kv.Key.StartsWith("Manifold", StringComparison.OrdinalIgnoreCase)).ToList();
            if (overrides.Count == 0) return;
            var temp = Path.Combine(Folder, "settings.override.txt");
            var lines = new List<string>();
            foreach (var kv in overrides) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(temp, lines);
            settings.MergeFrom(temp);
        }

        private static int Clean(Document doc, string mode, string createdPath)
        {
            var ids = new List<ElementId>();
            if (mode == "none") return 0;
            if (mode == "all")
            {
                var cats = new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory };
                foreach (var cat in cats)
                {
                    ids.AddRange(new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().ToElementIds());
                }
            }
            else if (File.Exists(createdPath))
            {
                foreach (var line in File.ReadAllLines(createdPath))
                {
                    long v;
                    if (long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) ids.Add(new ElementId(v));
                }
            }
            ids = ids.Where(id => doc.GetElement(id) != null).Distinct().ToList();
            if (ids.Count == 0) return 0;
            using (var t = new Transaction(doc, "sayRevit: pulizia banco di prova"))
            {
                t.Start();
                var deleted = 0;
                try
                {
                    deleted = doc.Delete(ids).Count;
                }
                catch
                {
                    // alcuni elementi possono essere già spariti a catena: si procede uno a uno
                    foreach (var id in ids)
                    {
                        try { if (doc.GetElement(id) != null) deleted += doc.Delete(id).Count; } catch { }
                    }
                }
                t.Commit();
                try { File.Delete(createdPath); } catch { }
                return deleted;
            }
        }

        /// <summary>
        /// Esporta un'immagine da una vista 3D dedicata ("sayRevit banco"): riquadro di sezione
        /// attorno agli elementi creati, ombreggiata, senza livelli e griglie, inquadrata a pagina.
        /// </summary>
        private static string ExportView(UIDocument uidoc, ICollection<ElementId> created, string imagePath)
        {
            var doc = uidoc.Document;
            const string name = "sayRevit banco";
            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);

            BoundingBoxXYZ box = null;
            foreach (var id in created)
            {
                var e = doc.GetElement(id);
                var bb = e == null ? null : e.get_BoundingBox(null);
                if (bb == null) continue;
                if (box == null) box = new BoundingBoxXYZ { Min = bb.Min, Max = bb.Max };
                else
                {
                    box.Min = new XYZ(Math.Min(box.Min.X, bb.Min.X), Math.Min(box.Min.Y, bb.Min.Y), Math.Min(box.Min.Z, bb.Min.Z));
                    box.Max = new XYZ(Math.Max(box.Max.X, bb.Max.X), Math.Max(box.Max.Y, bb.Max.Y), Math.Max(box.Max.Z, bb.Max.Z));
                }
            }

            using (var t = new Transaction(doc, "sayRevit: vista del banco di prova"))
            {
                t.Start();
                if (view == null)
                {
                    var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                        .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                    if (vft == null) { t.RollBack(); return null; }
                    view = View3D.CreateIsometric(doc, vft.Id);
                    try { view.Name = name; } catch { }
                }
                try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
                try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                foreach (var cat in new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_CLines, BuiltInCategory.OST_SectionBox })
                {
                    try { view.SetCategoryHidden(new ElementId(cat), true); } catch { }
                }
                if (box != null)
                {
                    var m = SayRevit.Addin.Revit.Units.MmToFt(300);
                    box.Min = box.Min - new XYZ(m, m, m);
                    box.Max = box.Max + new XYZ(m, m, m);
                    try { view.IsSectionBoxActive = true; view.SetSectionBox(box); } catch { }
                }
                // vista da sud-ovest, dall'alto: collettori lungo X visti di tre quarti
                try
                {
                    var eye = new XYZ(-1, -1.2, 0.9).Normalize();
                    var up = new XYZ(0, 0, 1);
                    var forward = eye.Negate();
                    var right = forward.CrossProduct(up).Normalize();
                    up = right.CrossProduct(forward).Normalize();
                    view.SetOrientation(new ViewOrientation3D(eye, up, forward));
                }
                catch { }
                t.Commit();
            }

            var options = new ImageExportOptions
            {
                FilePath = imagePath,
                ExportRange = ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 1800,
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);
            if (File.Exists(imagePath)) return imagePath;
            // Revit aggiunge il nome della vista al file
            var dir = Path.GetDirectoryName(imagePath) ?? Folder;
            var stem = Path.GetFileNameWithoutExtension(imagePath);
            var candidate = Directory.GetFiles(dir, stem + "*.png").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (candidate == null) return null;
            try
            {
                File.Copy(candidate, imagePath, true);
                File.Delete(candidate);
                return imagePath;
            }
            catch
            {
                return candidate;
            }
        }
    }
}
