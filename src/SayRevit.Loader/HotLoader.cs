using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace SayRevit.Loader
{
    /// <summary>
    /// Carica SayRevit.Addin.dll e le sue dipendenze DALLA MEMORIA (i file su disco non vengono
    /// bloccati) a ogni esecuzione, così l'add-in si può sostituire con Revit aperto.
    /// Su .NET (Revit 2025+) ogni clic usa un AssemblyLoadContext scaricabile: le librerie di
    /// Revit, di .NET e di WPF restano nel contesto predefinito, quindi i tipi condivisi
    /// (IExternalCommand, Document...) sono gli stessi. Su .NET Framework (Revit 2024) non si
    /// può scaricare: ogni clic carica una copia nuova e le vecchie restano in memoria.
    /// </summary>
    internal static class HotLoader
    {
        public const string MainAssembly = "SayRevit.Addin";
        public const string CommandType = "SayRevit.Addin.Command";

        public static string Folder
        {
            get { return Path.GetDirectoryName(typeof(HotLoader).Assembly.Location); }
        }

        public static Result Run(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var dir = Folder;
            var main = Path.Combine(dir, MainAssembly + ".dll");
            if (!File.Exists(main)) throw new FileNotFoundException("Add-in non trovato: " + main);

#if NETFRAMEWORK
            var asm = NetFx.Load(dir, main);
            return Invoke(asm, data, ref message, elements);
#else
            var context = new HotContext(dir);
            try
            {
                var asm = context.LoadFromFile(main);
                return Invoke(asm, data, ref message, elements);
            }
            finally
            {
                try { context.Unload(); } catch { }
            }
#endif
        }

        /// <summary>Carica l'add-in e chiama un metodo statico (usato dal banco di prova).</summary>
        public static object RunStatic(string typeName, string method, object[] args)
        {
            var dir = Folder;
            var main = Path.Combine(dir, MainAssembly + ".dll");
            if (!File.Exists(main)) throw new FileNotFoundException("Add-in non trovato: " + main);
#if NETFRAMEWORK
            var asm = NetFx.Load(dir, main);
            return InvokeStatic(asm, typeName, method, args);
#else
            var context = new HotContext(dir);
            try
            {
                var asm = context.LoadFromFile(main);
                return InvokeStatic(asm, typeName, method, args);
            }
            finally
            {
                try { context.Unload(); } catch { }
            }
#endif
        }

        private static object InvokeStatic(Assembly asm, string typeName, string method, object[] args)
        {
            var type = asm.GetType(typeName, true);
            var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null) throw new MissingMethodException(typeName, method);
            try
            {
                return mi.Invoke(null, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static Result Invoke(Assembly asm, ExternalCommandData data, ref string message, ElementSet elements)
        {
            var type = asm.GetType(CommandType, true);
            var command = Activator.CreateInstance(type) as IExternalCommand;
            if (command == null)
                throw new InvalidOperationException(CommandType + " non è un IExternalCommand utilizzabile (RevitAPIUI caricata due volte?).");
            return command.Execute(data, ref message, elements);
        }

        internal static byte[] ReadAll(string path)
        {
            // lettura con condivisione completa: non ostacola chi sta copiando il file
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

#if !NETFRAMEWORK
    internal sealed class HotContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public HotContext(string dir) : base("sayRevit " + DateTime.Now.ToString("HH:mm:ss.fff"), isCollectible: true)
        {
            _dir = dir;
        }

        /// <summary>Le librerie presenti nella cartella dell'add-in si caricano qui; tutto il resto dal contesto predefinito.</summary>
        protected override Assembly Load(AssemblyName name)
        {
            var candidate = Path.Combine(_dir, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromFile(candidate) : null;
        }

        public Assembly LoadFromFile(string path)
        {
            using (var dll = new MemoryStream(HotLoader.ReadAll(path)))
            {
                var pdbPath = Path.ChangeExtension(path, ".pdb");
                if (!File.Exists(pdbPath)) return LoadFromStream(dll);
                using (var pdb = new MemoryStream(HotLoader.ReadAll(pdbPath)))
                {
                    return LoadFromStream(dll, pdb);
                }
            }
        }
    }
#else
    internal static class NetFx
    {
        private static string _dir;
        private static Dictionary<string, Assembly> _generation;
        private static bool _hooked;

        public static Assembly Load(string dir, string main)
        {
            _dir = dir;
            _generation = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            if (!_hooked)
            {
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                _hooked = true;
            }
            return LoadBytes(main);
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                if (_dir == null || _generation == null) return null;
                var name = new AssemblyName(args.Name).Name;
                Assembly found;
                if (_generation.TryGetValue(name, out found)) return found;
                var path = Path.Combine(_dir, name + ".dll");
                if (!File.Exists(path)) return null;
                found = LoadBytes(path);
                _generation[name] = found;
                return found;
            }
            catch
            {
                return null;
            }
        }

        private static Assembly LoadBytes(string path)
        {
            var pdb = Path.ChangeExtension(path, ".pdb");
            return File.Exists(pdb)
                ? Assembly.Load(HotLoader.ReadAll(path), HotLoader.ReadAll(pdb))
                : Assembly.Load(HotLoader.ReadAll(path));
        }
    }
#endif
}
