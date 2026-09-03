using System.Collections.Generic;
using SayRevit.Core.Model;

namespace SayRevit.Core.Parsing
{
    /// <summary>Vocabolario italiano/inglese usato dal parser a regole.</summary>
    public static class Lexicon
    {
        public const string PipeNoun = @"(?:tubazion[ei]|tubatur[ae]|tub[oi]\b|condott[ae]\b|collettor[ei]|montant[ei]|colonn[ae] montant[ei]|pip(?:e|es|ing|eline|elines)\b|riser|risers|main\b|mains\b|line\b)";
        public const string DuctNoun = @"(?:canal[ei]\b|canalizzazion[ei]|canale d'aria|condott[ao] (?:d'|dell')aria|condott[oi] aria|duct(?:s|work)?\b|air duct(?:s)?)";
        public const string BranchNoun = @"(?:stacc(?:o|hi)|derivazion[ei]|diramazion[ei]|ramificazion[ei]|attacc(?:o|hi)|pres[ae]\b|spur|spurs|branch(?:es)?|tee(?:s)?\b|take-?offs?|tap(?:s)?\b|outlet(?:s)?)";

        public static readonly string[] IndefinitePlural =
        {
            "degli", "dei", "delle", "alcuni", "alcune", "vari", "varie", "diversi", "diverse", "qualche",
            "some", "several", "a few", "a couple of", "multiple"
        };

        /// <summary>Frase di sistema → classificazione. Ordinate per lunghezza decrescente in fase di matching.</summary>
        public static readonly List<KeyValuePair<string, string>> Systems = new List<KeyValuePair<string, string>>
        {
            // Idronico
            new KeyValuePair<string, string>(@"acqua (?:calda )?sanitaria|acs\b|acqua calda|hot water|domestic hot|dhw\b", SystemClass.DomesticHotWater),
            new KeyValuePair<string, string>(@"acqua fredda(?: sanitaria)?|afs\b|cold water|domestic cold|dcw\b|acqua potabile|idrico sanitario|idrico|potabile", SystemClass.DomesticColdWater),
            new KeyValuePair<string, string>(@"ricircolo|recirculation", SystemClass.DomesticHotWater),
            new KeyValuePair<string, string>(@"mandata(?: riscaldamento| raffrescamento| impianto)?|riscaldamento|heating supply|hydronic supply|supply water|chilled water supply|mandata caldo|mandata freddo|acqua refrigerata", SystemClass.SupplyHydronic),
            new KeyValuePair<string, string>(@"ritorno(?: riscaldamento| raffrescamento| impianto)?|heating return|hydronic return|return water|chilled water return", SystemClass.ReturnHydronic),
            new KeyValuePair<string, string>(@"scarico|scarichi|fognatura|acque nere|acque reflue|acque grigie|acque bianche|acque meteoriche|pluviale|pluviali|sanitary|drain(?:age)?|waste|sewer|condensa|condensate", SystemClass.Sanitary),
            new KeyValuePair<string, string>(@"ventilazione(?: scarichi| primaria| secondaria)?|sfiato|sfiati|vent\b", SystemClass.Vent),
            new KeyValuePair<string, string>(@"antincendio|idranti|sprinkler|naspi|fire(?: protection| fighting)?", SystemClass.FireProtectWet),
            new KeyValuePair<string, string>(@"gas\b|metano|aria compressa|compressed air|vapore|steam|olio|oil|glicole|glycol", SystemClass.OtherPipe),
            // Aeraulico
            new KeyValuePair<string, string>(@"aria di mandata|mandata aria|aria primaria|immissione|supply air|supply\b", SystemClass.SupplyAir),
            new KeyValuePair<string, string>(@"aria di ripresa|ripresa|aria di ritorno|ritorno aria|return air", SystemClass.ReturnAir),
            new KeyValuePair<string, string>(@"aria di espulsione|espulsione|estrazione|esausta|exhaust(?: air)?|extract(?:ion)?", SystemClass.ExhaustAir),
            new KeyValuePair<string, string>(@"aria esterna|presa d'aria|outside air|fresh air|aria\b", SystemClass.OtherDuct),
        };

        /// <summary>Parole chiave materiale/tipo → sinonimi da cercare nei nomi dei tipi Revit.</summary>
        public static readonly List<KeyValuePair<string, string[]>> Materials = new List<KeyValuePair<string, string[]>>
        {
            new KeyValuePair<string, string[]>(@"acciaio inox|inox|stainless(?: steel)?", new[] { "inox", "stainless" }),
            new KeyValuePair<string, string[]>(@"acciaio zincato|zincat[oa]|galvani[sz]ed", new[] { "zincat", "galvani" }),
            new KeyValuePair<string, string[]>(@"acciaio nero|acciaio(?: al carbonio)?|carbon steel|steel\b|ferro", new[] { "acciaio", "steel", "carbon" }),
            new KeyValuePair<string, string[]>(@"rame|copper", new[] { "rame", "copper" }),
            new KeyValuePair<string, string[]>(@"pvc\b|pvc-u|upvc", new[] { "pvc" }),
            new KeyValuePair<string, string[]>(@"ppr\b|polipropilene|pp-r|polypropylene", new[] { "ppr", "polipropilene", "polypropylene", "pp-r" }),
            new KeyValuePair<string, string[]>(@"pead|pe\b|hdpe|polietilene|polyethylene", new[] { "pead", "hdpe", "polietilene", "polyethylene", "pe " }),
            new KeyValuePair<string, string[]>(@"multistrato|pex|multilayer", new[] { "multistrato", "pex", "multilayer" }),
            new KeyValuePair<string, string[]>(@"ghisa|cast iron", new[] { "ghisa", "cast iron" }),
            new KeyValuePair<string, string[]>(@"lamiera(?: zincata)?|sheet metal", new[] { "lamiera", "sheet" }),
            new KeyValuePair<string, string[]>(@"flessibile|flex\b|flexible", new[] { "fless", "flex" }),
            new KeyValuePair<string, string[]>(@"isolat[oa]|coibentat[oa]|insulated", new[] { "isol", "coib", "insul" }),
            new KeyValuePair<string, string[]>(@"saldat[oa]|welded", new[] { "saldat", "welded" }),
            new KeyValuePair<string, string[]>(@"filettat[oa]|threaded", new[] { "filett", "threaded" }),
            new KeyValuePair<string, string[]>(@"pressfitting|press-?fit|a pressare", new[] { "press" }),
            new KeyValuePair<string, string[]>(@"spiralat[oa]|spiral", new[] { "spiral" }),
        };

        /// <summary>Direzioni (regex → DirectionKind). L'ordine conta: le più specifiche prima.</summary>
        public static readonly List<KeyValuePair<string, DirectionKind>> Directions = new List<KeyValuePair<string, DirectionKind>>
        {
            new KeyValuePair<string, DirectionKind>(@"alternat[ie]|alternating|a destra e a sinistra|left and right|su entrambi i lati|both sides|da entrambe le parti", DirectionKind.Alternate),
            new KeyValuePair<string, DirectionKind>(@"verso l'alto|verso alto|in alto|in su\b|a salire|salient[ei]|montant[ei]|upwards?|going up|\bup\b|verticale verso l'alto", DirectionKind.Up),
            new KeyValuePair<string, DirectionKind>(@"verso il basso|verso basso|in basso|in gi[uù]\b|a scendere|discendent[ei]|downwards?|going down|\bdown\b|verticale verso il basso", DirectionKind.Down),
            new KeyValuePair<string, DirectionKind>(@"a sinistra|sulla sinistra|verso sinistra|to the left|on the left|leftwards?|\bleft\b", DirectionKind.Left),
            new KeyValuePair<string, DirectionKind>(@"a destra|sulla destra|verso destra|to the right|on the right|rightwards?|\bright\b", DirectionKind.Right),
            new KeyValuePair<string, DirectionKind>(@"verso (?:il )?nord|a nord|northwards?|\bnorth\b|lungo (?:l'asse )?y\b|(?:in direzione|direzione|asse) \+?y\b|\+y\b", DirectionKind.PlusY),
            new KeyValuePair<string, DirectionKind>(@"verso (?:il )?sud|a sud|southwards?|\bsouth\b|(?:in direzione|direzione|asse) -y\b|-y\b", DirectionKind.MinusY),
            new KeyValuePair<string, DirectionKind>(@"verso (?:l')?est|a est|eastwards?|\beast\b|lungo (?:l'asse )?x\b|(?:in direzione|direzione|asse) \+?x\b|\+x\b", DirectionKind.PlusX),
            new KeyValuePair<string, DirectionKind>(@"verso (?:l')?ovest|a ovest|westwards?|\bwest\b|(?:in direzione|direzione|asse) -x\b|-x\b", DirectionKind.MinusX),
            new KeyValuePair<string, DirectionKind>(@"vertical[ei]|vertical(?:ly)?|a piombo", DirectionKind.Up),
            new KeyValuePair<string, DirectionKind>(@"lateral[ei]|di lato|laterally|sideways|orizzontal[ei]|horizontal(?:ly)?", DirectionKind.Left),
        };
    }
}
