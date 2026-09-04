using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SayRevit.Loader
{
    /// <summary>
    /// Il comando del pulsante: carica l'add-in vero dalla cartella di installazione e gli passa
    /// la chiamata. Se il caricamento fallisce mostra il motivo e la cartella da controllare.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LoaderCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                return HotLoader.Run(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                TaskDialog.Show("sayRevit",
                    "Non riesco a caricare l'add-in.\n\n" + inner.GetType().Name + ": " + inner.Message +
                    "\n\nCartella: " + HotLoader.Folder +
                    "\nControlla che contenga " + HotLoader.MainAssembly + ".dll compilato per questa versione di Revit," +
                    " oppure rilancia scripts\\install.ps1.");
                message = inner.Message;
                return Result.Failed;
            }
        }
    }
}
