using System.Collections.Generic;

namespace SayRevit.Core.Model
{
    /// <summary>Una misura disponibile per un tipo di tubazione: DN e diametro interno reale.</summary>
    public sealed class CatalogPipeSize
    {
        public double NominalMm { get; set; }

        /// <summary>Diametro interno (mm); 0 = non leggibile dal segmento.</summary>
        public double InnerMm { get; set; }
    }

    /// <summary>Descrizione di un tipo di tubazione/canale presente nel modello.</summary>
    public sealed class CatalogType
    {
        public string Name { get; set; }
        public MepKind Kind { get; set; }
        public SizeShape Shape { get; set; } = SizeShape.Round;

        /// <summary>Diametri nominali disponibili (mm) per il tipo, letti dalle preferenze di instradamento.</summary>
        public List<double> AvailableDiametersMm { get; } = new List<double>();

        /// <summary>Misure disponibili con il diametro interno (solo tubazioni), stesse fonti.</summary>
        public List<CatalogPipeSize> Sizes { get; } = new List<CatalogPipeSize>();

        public bool HasElbows { get; set; }
        public bool HasTees { get; set; }
        public bool HasTransitions { get; set; }
        public bool HasTakeoffs { get; set; }
    }

    /// <summary>Una famiglia caricata nel progetto con i nomi dei suoi tipi (es. le valvole).</summary>
    public sealed class CatalogFamily
    {
        public string Name { get; set; }

        public List<string> TypeNames { get; } = new List<string>();
    }

    public sealed class CatalogSystem
    {
        public string Name { get; set; }
        public string SystemClass { get; set; }
    }

    /// <summary>
    /// Catalogo delle famiglie/tipi presenti nel documento Revit corrente.
    /// Viene compilato dall'add-in e usato dai parser per riferirsi a nomi esistenti.
    /// </summary>
    public sealed class ModelCatalog
    {
        public List<CatalogType> PipeTypes { get; } = new List<CatalogType>();
        public List<CatalogType> DuctTypes { get; } = new List<CatalogType>();
        public List<CatalogSystem> PipingSystems { get; } = new List<CatalogSystem>();
        public List<CatalogSystem> DuctSystems { get; } = new List<CatalogSystem>();

        /// <summary>Famiglie di accessori per tubazioni (valvole e simili) caricate nel progetto.</summary>
        public List<CatalogFamily> PipeAccessories { get; } = new List<CatalogFamily>();
        public List<string> Levels { get; } = new List<string>();
        public string ActiveLevel { get; set; }
        public string ProjectUnitsNote { get; set; }

        public static ModelCatalog Empty()
        {
            return new ModelCatalog();
        }
    }
}
