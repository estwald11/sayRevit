using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace SayRevit.Loader
{
    /// <summary>
    /// Punto di ingresso registrato in Revit: crea la scheda "sayRevit" con il pulsante, che non
    /// esegue direttamente l'add-in ma il caricatore (<see cref="LoaderCommand"/>). Questo assembly
    /// non cambia quasi mai: è l'unico che richiede il riavvio di Revit per essere aggiornato.
    /// </summary>
    public class LoaderApp : IExternalApplication
    {
        public const string TabName = "sayRevit";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // scheda già esistente
            }

            // banco di prova: esegue l'add-in senza finestra quando compare un file di richiesta
            application.Idling += AutomationWatcher.OnIdling;

            var panel = application.CreateRibbonPanel(TabName, "MEP da testo");
            var asm = Assembly.GetExecutingAssembly().Location;
            var data = new PushButtonData("SayRevit_TextToMep", "Testo →\nTubazioni/Canali", asm, typeof(LoaderCommand).FullName)
            {
                ToolTip = "Descrivi a parole tubazioni e canali (es. \"una tubazione DN200 con degli stacchi DN15\") e creali nel modello.",
                LongDescription = "Interfaccia testuale per creare tubazioni e canali con stacchi usando i tipi, i sistemi e i livelli già presenti nel progetto.\n" +
                                  "L'add-in viene caricato a ogni clic dalla cartella di installazione: si può aggiornare senza chiudere Revit."
            };
            var button = panel.AddItem(data) as PushButton;
            if (button != null)
            {
                try
                {
                    button.LargeImage = CreateIcon(32);
                    button.Image = CreateIcon(16);
                }
                catch
                {
                    // l'icona è opzionale
                }
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= AutomationWatcher.OnIdling;
            return Result.Succeeded;
        }

        /// <summary>Icona generata a runtime (nessuna risorsa esterna da distribuire).</summary>
        private static BitmapSource CreateIcon(int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bg = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xB2));
                dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), size / 6.0, size / 6.0);
                var pen = new Pen(Brushes.White, Math.Max(1, size / 8.0));
                // tubazione orizzontale con uno stacco verticale
                dc.DrawLine(pen, new Point(size * 0.15, size * 0.62), new Point(size * 0.85, size * 0.62));
                dc.DrawLine(pen, new Point(size * 0.5, size * 0.62), new Point(size * 0.5, size * 0.2));
                if (size >= 32)
                {
                    var text = new FormattedText("T", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), size * 0.35, Brushes.White, 1.0);
                    dc.DrawText(text, new Point(size * 0.62, size * 0.62));
                }
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
