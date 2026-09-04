using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
#if !NETFRAMEWORK
#endif

namespace SayRevit.Loader
{
    /// <summary>
    /// Banco di prova: quando Revit è inattivo controlla (una volta al secondo) se esiste
    /// %APPDATA%\sayRevit\automation\request.txt. Se c'è, carica l'add-in come fa il pulsante e
    /// chiama SayRevit.Addin.Automation.Run(UIApplication, string), che costruisce, esporta
    /// un'immagine e scrive l'esito su file. L'evento Idling è un contesto API valido: si può
    /// modificare il documento senza ExternalEvent.
    /// </summary>
    internal static class AutomationWatcher
    {
        public const string AutomationType = "SayRevit.Addin.Automation";

        private static DateTime _lastCheck = DateTime.MinValue;
        private static bool _busy;

        public static string Folder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sayRevit", "automation");
            }
        }

        public static string RequestPath
        {
            get { return Path.Combine(Folder, "request.txt"); }
        }

        public static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_busy) return;
            var now = DateTime.Now;
            if ((now - _lastCheck).TotalMilliseconds < 1000) return;
            _lastCheck = now;

            string request;
            try
            {
                if (!File.Exists(RequestPath)) return;
                // la richiesta viene presa in carico rinominandola: una seconda scrittura non la rilancia
                request = Path.Combine(Folder, "request.running");
                if (File.Exists(request)) File.Delete(request);
                File.Move(RequestPath, request);
            }
            catch
            {
                return;
            }

            var app = sender as UIApplication;
            _busy = true;
            try
            {
                if (app == null) throw new InvalidOperationException("UIApplication non disponibile nell'evento Idling.");
                HotLoader.RunStatic(AutomationType, "Run", new object[] { app, request });
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                try
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(Path.Combine(Folder, "result.txt"), "ERRORE DEL CARICATORE: " + inner);
                }
                catch
                {
                    // niente da fare
                }
            }
            finally
            {
                try { if (File.Exists(request)) File.Delete(request); } catch { }
                _busy = false;
            }
        }
    }
}
