# sayRevit – tubazioni e canali da una descrizione testuale

Add-in per Autodesk Revit che aggiunge una **interfaccia testuale**: l'utente scrive cosa vuole
(ad esempio *"una tubazione DN200 con degli stacchi DN15"*) e l'add-in crea nel modello le
tubazioni o i canali descritti, con i relativi stacchi collegati tramite raccordi a T (o prese),
usando **i tipi, i sistemi e i livelli già presenti nel progetto**.

Ambito: **solo MEP – tubazioni (pipes) e canali (ducts)**.

```
┌──────────────────────────────────────────────────────────────────────┐
│ Descrivi cosa creare (solo tubazioni e canali):                       │
│ ┌──────────────────────────────────────────────────────────────────┐ │
│ │ una tubazione DN200 lunga 10 m con 3 stacchi DN15 ogni 2 m       │ │
│ │ verso l'alto, acqua fredda, in acciaio zincato                   │ │
│ └──────────────────────────────────────────────────────────────────┘ │
│ Interprete: [Regole (offline) ▾]  Livello: [Livello 1 ▾]  Quota: 2500 │
│ [Interpreta (anteprima)] [Crea in Revit] [Chiudi]                     │
│ ┌──────────────────────────────────────────────────────────────────┐ │
│ │ tubazione DN200, lunghezza 10 m, direzione +X, tipo con          │ │
│ │ "zincat/galvani", sistema acqua fredda [DomesticColdWater]       │ │
│ │    - 3 stacchi DN15, lunghezza 500 mm, verso l'alto, interasse 2 m│ │
│ └──────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────┘
```

## Come funziona

1. **Catalogo del modello** – all'apertura del comando l'add-in legge dal documento:
   tipi di tubazione (`PipeType`) con i diametri disponibili nelle *preferenze di instradamento*,
   tipi di canale (`DuctType`, circolari/rettangolari), tipi di sistema idraulico e aeraulico
   con la loro classificazione, livelli.
2. **Interpretazione** – il testo viene trasformato in un *piano* (`MepPlan`: tratti, dimensioni,
   lunghezze, direzioni, stacchi, materiale, sistema, livello, quota) da uno dei due interpreti:
   - **Regole (offline)** – parser deterministico italiano/inglese, senza dipendenze né rete.
   - **Claude (AI)** – opzionale: invia testo e catalogo a Claude tramite l'SDK ufficiale Anthropic
     e riceve un piano JSON conforme a uno schema (output strutturati). Utile per frasi libere;
     può riferirsi ai nomi esatti dei tipi/sistemi/livelli del progetto.
3. **Anteprima** – il piano viene mostrato in chiaro, con note e avvisi (ambiguità, valori assunti,
   misure non disponibili nel tipo scelto…).
4. **Creazione** – in un'unica transazione Revit: `Pipe.Create` / `Duct.Create`, impostazione del
   diametro (o larghezza/altezza), spezzatura del tratto nei punti di stacco (`BreakCurve`),
   raccordo a T (`NewTeeFitting`) o presa (`NewTakeoffFitting`) secondo la preferenza di giunzione
   del tipo, gomiti/transizioni tra tratti consecutivi. Gli avvisi non bloccanti di Revit vengono
   soppressi; gli elementi creati vengono selezionati a fine comando.

### Interfaccia con le famiglie esistenti

- **Tipo**: nome esplicito (`tipo "Acciaio zincato"`), altrimenti parole chiave sul materiale
  (acciaio, zincato, inox, rame, PVC, PPR, PEAD, multistrato, ghisa, lamiera…) cercate nei nomi
  dei tipi; altrimenti il tipo predefinito del progetto, altrimenti il primo con segmenti definiti.
- **Misure**: il diametro richiesto viene confrontato con i diametri del tipo (segmenti delle
  preferenze di instradamento per le tubazioni, impostazioni dimensioni canali per i canali
  circolari); se non esiste si usa la misura più vicina e si avvisa.
- **Raccordi**: la creazione di T/prese/gomiti usa le famiglie configurate nelle preferenze di
  instradamento del tipo. Se mancano, lo stacco viene comunque creato ma lasciato scollegato e
  l'anteprima/il resoconto lo segnalano.
- **Sistema**: frase riconosciuta (acqua fredda/calda, ricircolo, mandata/ritorno, scarico,
  antincendio, gas, aria di mandata/ripresa/espulsione…) → classificazione Revit
  (`MEPSystemClassification`) → tipo di sistema del progetto con quella classificazione
  (con preferenza per il nome più simile).
- **Livello**: `al livello 1`, `al piano terra`, `on level 2`… confrontati con i nomi dei livelli;
  altrimenti il livello scelto nella finestra (predefinito: quello della vista attiva).
- **Quota**: `a quota 2,8 m`, `a 3 m da terra`; altrimenti la quota predefinita della finestra.

## Frasi supportate (parser a regole)

| Cosa | Esempi |
|---|---|
| Elemento | `tubazione`, `tubo`, `condotta`, `collettore`, `pipe`; `canale`, `canalizzazione`, `condotta aria`, `duct` |
| Dimensione | `DN200`, `diametro 160 mm`, `da 110`, `Ø 315`, `400x200`, `6"`, `1 1/2"`, `3/4 inch` |
| Lunghezza | `lunga 10 m`, `di 6 metri`, `per 3 m`, `30 ft long`, `lunghi 60 cm` |
| Stacchi | `con 3 stacchi DN15`, `degli stacchi` (=2, con nota), `uno stacco`, `due derivazioni`, `4 branches`, `taps` |
| Interasse / posizioni | `ogni 2 m`, `ogni metro`, `interasse 1,5 m`, `a 1, 2.5 e 4 m dall'inizio` |
| Direzione | `verso l'alto/basso`, `a destra/sinistra`, `laterali`, `alternati`, `verso nord/sud/est/ovest`, `lungo x` |
| Materiale / tipo | `in acciaio zincato`, `rame`, `pvc`, `ppr`, `tipo "Nome esatto del tipo"` |
| Sistema | `acqua fredda`, `acqua calda sanitaria`, `ricircolo`, `mandata/ritorno riscaldamento`, `scarico`, `antincendio`, `gas`, `aria di mandata/ripresa/espulsione` |
| Livello / quota | `al livello 1`, `al piano terra`, `a quota 2,8 m`, `a 3 m da terra` |
| Più tratti | separati da `;`, `.` o `poi`: `tubazione DN80 lunga 5 m; poi verso l'alto per 2 m; poi DN65 lungo x per 3 m` (i tratti proseguono e vengono raccordati; `un'altra tubazione…` crea un tratto separato) |

Valori predefiniti: lunghezza tratto 3 m, lunghezza stacco 500 mm, direzione tratto +X,
direzione stacchi verso l'alto (tubazioni) o laterale (canali), stacchi distribuiti uniformemente.

## Requisiti e installazione

- Windows con Autodesk Revit **2024** (.NET Framework 4.8), **2025** o **2026** (.NET 8), **2027** (.NET 10).
- [.NET SDK 8](https://dotnet.microsoft.com/download) per compilare (SDK .NET 10 per Revit 2027).

```powershell
git clone <repo> sayRevit
cd sayRevit
.\scripts\install.ps1 -RevitVersion 2025     # oppure 2024 / 2026 / 2027
```

Lo script cerca l'installazione in `C:\Program Files\Autodesk\Revit <versione>`, legge da
`RevitAPI.runtimeconfig.json` il runtime .NET su cui gira quel Revit, compila per lo stesso framework
usando le librerie API della cartella di Revit, poi copia i file in
`%APPDATA%\Autodesk\Revit\Addins\<versione>\SayRevit\` e il manifest `SayRevit.addin` accanto.
La versione passata allo script deve essere quella **effettivamente installata**: un add-in compilato
per .NET 10 (Revit 2027) caricato in un Revit su .NET 8 (2025/2026) dà l'errore
`Could not load file or assembly 'System.Runtime, Version=10.0.0.0'`.
Al riavvio di Revit compare la scheda **sayRevit → Testo → Tubazioni/Canali**.
Per rimuovere: `.\scripts\uninstall.ps1 -RevitVersion 2025`.

Compilazione manuale: `dotnet build src/SayRevit.Addin/SayRevit.Addin.csproj -p:RevitVersion=2024`
(output in `artifacts/<versione>/`). Opzioni: `-p:RevitFramework=net8.0-windows` forza il framework,
`-p:RevitApiDir="C:\Program Files\Autodesk\Revit 2026"` usa le DLL API dell'installazione invece dei
pacchetti NuGet. Il progetto compila anche su Linux/macOS (riferimenti Revit
API dai pacchetti NuGet `Nice3point.Revit.Api.*`, interfaccia WPF costruita da codice senza XAML).

## Modalità Claude (opzionale)

1. Imposta la variabile d'ambiente `ANTHROPIC_API_KEY` (oppure una credenziale riconosciuta
   dall'SDK Anthropic) **prima** di avviare Revit.
2. Nella finestra scegli *Interprete: Claude (AI)*; il modello predefinito è `claude-opus-5`.

Il prompt include il catalogo del progetto (nomi di tipi, sistemi, livelli, diametri disponibili),
così il modello può usare i nomi esatti. La chiamata usa gli output strutturati (schema JSON in
`src/SayRevit.Claude/PlanJson.cs`) e i *fallback* lato server (`fallbacks: default`): se il modello
rifiuta la richiesta la risposta viene servita da un modello di ripiego; in caso di rifiuto
definitivo la finestra lo segnala e si può usare il parser a regole. La libreria Anthropic viene
caricata solo quando si seleziona questa modalità: senza chiave si lavora normalmente offline.

## Struttura del repository

```
sayRevit.sln
src/SayRevit.Core     modello (MepPlan), parser a regole IT/EN, formattatore anteprima  [netstandard2.0, nessuna dipendenza]
src/SayRevit.Claude   parser Claude con output strutturati                            [netstandard2.0, SDK Anthropic]
src/SayRevit.Addin    add-in Revit: ribbon, finestra WPF, lettura catalogo, costruzione elementi [net48 / net8.0-windows / net10.0-windows]
tests/                test xunit del parser e della conversione JSON
scripts/              install.ps1 / uninstall.ps1
```

Test: `dotnet test` (32 test, eseguibili su qualsiasi sistema operativo).

## Limitazioni e note

- Solo tratti rettilinei con stacchi ortogonali; niente pendenze, isolamenti, flessibili, apparecchi.
- Il collegamento degli stacchi dipende dalle famiglie di raccordo configurate nel tipo: senza
  raccordo a T (o presa) nelle preferenze di instradamento lo stacco resta scollegato (segnalato).
- Cambio di direzione e di dimensione nello stesso punto tra due tratti: Revit non crea un gomito
  con diametri diversi; si consiglia un tratto intermedio.
- Il codice Revit è stato compilato contro le API 2024/2025/2026/2027 ma va verificato nel proprio
  modello (famiglie e preferenze di instradamento variano da progetto a progetto): usare sempre
  l'anteprima e, se necessario, *Annulla* di Revit (la creazione è un'unica transazione).
- In Revit 2024 (.NET Framework) l'SDK Anthropic porta con sé `System.Text.Json` e altre librerie
  `System.*`: se un altro add-in carica versioni diverse potrebbero esserci conflitti nella sola
  modalità Claude; il parser a regole non ne è influenzato.
