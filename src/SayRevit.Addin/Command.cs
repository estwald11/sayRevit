using System;
using System.Text;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SayRevit.Addin.Revit;
using SayRevit.Addin.UI;

namespace SayRevit.Addin
{
    /// <summary>Comando: apre la finestra testuale e crea gli elementi descritti.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
            {
                message = "Apri un progetto prima di usare sayRevit.";
                return Result.Failed;
            }
            var doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show("sayRevit", "Il comando funziona solo nei documenti di progetto, non nelle famiglie.");
                return Result.Cancelled;
            }

            var settings = Settings.Load();
            var catalog = ModelCatalogReader.Read(doc);

            var window = new MainWindow(catalog, settings);
            try
            {
                new WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
            }
            catch
            {
                // proprietario opzionale
            }

            var ok = window.ShowDialog();
            settings.Save();
            if (ok != true || window.Result == null || !window.Result.Success) return Result.Cancelled;

            // Livello: quello della vista attiva (o il primo del progetto); quota: DefaultElevationMm
            // delle impostazioni sopra il livello. Il punto di partenza si sceglie sempre nel modello.
            var options = new BuildOptions
            {
                DefaultElevationMm = settings.DefaultElevationMm,
                PipeTypeName = window.SelectedPipeType
            };

            try
            {
                options.StartPoint = uidoc.Selection.PickPoint(ObjectSnapTypes.None, "Scegli il punto di partenza del collettore/tubazione (Esc annulla)");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("sayRevit", "Impossibile scegliere un punto in questa vista (" + ex.Message + "). Uso l'origine del progetto.");
            }

            var plan = window.Result.Plan;
            var builder = new RevitPlanBuilder(doc);
            if (window.ManifoldMode && window.ManifoldPlan != null && window.ManifoldPlan.AutoSpacing)
            {
                // l'interasse automatico si misura sul modello: si risolve qui e si rigenera il piano
                builder.ResolveAutoSpacing(window.ManifoldPlan);
                var resolved = window.ManifoldPlan.ToParseResult();
                if (resolved.Success) plan = resolved.Plan;
            }
            var report = builder.Build(plan, options);

            // In modalità Collettore la creazione riuscita e pulita non interrompe l'utente con
            // il riepilogo; il dialogo resta per gli errori e per gli avvisi (che vanno visti).
            var silent = window.ManifoldMode && report.Succeeded && report.Warnings.Count == 0;
            if (!silent)
            {
                var sb = new StringBuilder();
                sb.AppendLine(report.Summary());
                foreach (var m in report.Messages) sb.AppendLine("• " + m);
                foreach (var w in report.Warnings) sb.AppendLine("⚠ " + w);

                var dialog = new TaskDialog("sayRevit")
                {
                    MainInstruction = report.Succeeded ? "Elementi creati" : "Creazione non riuscita",
                    MainContent = sb.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                dialog.Show();
            }

            if (report.Succeeded && report.CreatedIds.Count > 0)
            {
                try
                {
                    uidoc.Selection.SetElementIds(report.CreatedIds);
                    uidoc.ShowElements(report.CreatedIds);
                }
                catch
                {
                    // solo comodità
                }
            }

            return report.Succeeded ? Result.Succeeded : Result.Failed;
        }
    }
}
