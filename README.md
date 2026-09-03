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
  dei tipi; altrimenti il **tipo scelto nella tendina "Tipo tubazione"** della finestra;
  altrimenti il tipo predefinito del progetto, altrimenti il primo con segmenti definiti.
  La tendina elenca tutti i `PipeType` caricati nel progetto (es. *Ghisa REI*, *RM Inoxpres 316*,
  *Multistrato stabil PEX-a*…) più la voce *Automatico*, che lascia decidere al testo. Il nome
  scelto viene confrontato in modo **esatto**, perché i nomi si assomigliano molto. Il suggerimento
  del controllo mostra le misure disponibili per quel tipo e se ha raccordi a T configurati.
  La sezione Collettore ha una **propria** tendina, senza *Automatico*: lì la scelta è vincolante.
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

## Modalità Collettore (parametrica)

Il flag **Collettore** in alto a destra nella finestra sostituisce il campo testuale con una
scheda parametrica: niente linguaggio naturale, si compilano dei campi. Questa sezione è
**puramente deterministica** — nessun valore viene dedotto o interpretato.

- **Materiale / tipo tubazione**: tendina in cima alla scheda con i `PipeType` del progetto.
  Non c'è la voce *Automatico*: il tipo mostrato è esattamente quello che verrà usato, e il
  nome viene risolto per corrispondenza esatta. Accanto compare il numero di misure disponibili,
  con l'elenco completo nel suggerimento e nell'anteprima.
- **Circuiti**: si inserisce un circuito per volta indicandone il **DN**. Il pulsante **+**
  aggiunge una riga (lo fa anche **Invio** dal campo DN); **✕** la rimuove. Le righe si
  numerano da sole `C1`, `C2`, … e quelle lasciate vuote vengono ignorate.
- **DN collettore**: con *automatico* attivo, la formula **D = √(1,5·(S₁+S₂+…)/0,785)**
  sulle sezioni dei circuiti (con S = 0,785·dn² si semplifica in √(1,5·Σdn²)) dà il diametro
  richiesto; il DN scelto è la misura del tipo col **diametro interno minimo tra quelli ≥ D**
  (gli interni vengono letti dai segmenti delle preferenze di instradamento — per un PEX o
  una ghisa contano quelli, non il nominale). Se nessuna misura basta si usa la più grande e
  si avvisa; senza dati sugli interni si ripiega sull'arrotondamento alla serie DN commerciale.
  L'anteprima mostra formula, DN scelto e Øint. Togliendo la spunta lo si impone a mano.
- **Interasse**: distanza tra due circuiti consecutivi. La base sporge di **5 cm oltre il
  bordo** del primo e dell'ultimo circuito (bordo = asse ± DN/2), quindi con DN d'estremità
  diversi le due sporgenze d'asse sono diverse. Con il ritorno attivo il vincolo dei 5 cm
  vale sul **primo stacco della mandata** e sull'**ultimo stacco del ritorno** (mezzo
  interasse più avanti): entrambe le basi, identiche e allineate, si allungano di s/2 e
  nessuno stacco cade mai fuori dalla base.
- **Lunghezza circuiti** e **partenza circuiti** (basso, alto, sinistra, destra, alternati).
  La direzione dei collettori è fissa: +X (est), col secondo collettore in +Y. A creazione riuscita senza avvisi il
  riepilogo non compare; il dialogo resta per errori e avvisi.
- **Collettore di ritorno**: spuntando l'opzione viene creato anche il ritorno: base
  **identica e perfettamente allineata** alla mandata (stesso DN, stessa lunghezza, stessi
  estremi) su un asse parallelo alla **distanza mandata/ritorno** indicata. Solo gli stacchi
  vengono riposizionati: stessi DN nello stesso ordine, spostati di mezzo interasse, così
  ogni circuito di un collettore cade a metà tra due dell'altro, e tutti gli stacchi
  vengono sempre replicati su entrambi. Gli stacchi sfasati di mezzo interasse
  stanno sul primo collettore, quelli non sfasati sul secondo.

- **Fondelli (Enddeckel)**: alle quattro estremità delle basi (mandata e ritorno) vengono
  posizionati automaticamente dei fondelli, orientati, dimensionati e collegati al tubo.
  La famiglia dipende dal materiale del tipo scelto: nomi con *inox* →
  `ATZ_INOX-WELD_Enddeckel`, con *acciaio nero / C-Stahl* → `ATZ_C-STAHL-WELD_5_Enddeckel`;
  per gli altri materiali, per ora, le estremità restano aperte (segnalato in anteprima).
  Se la famiglia non è caricata nel progetto compare un avviso.

A differenza della modalità testuale, **i circuiti non vengono raccordati**: il collettore
resta un tubo unico (nessun `BreakCurve`) e ogni circuito parte dall'asse del collettore,
semplicemente sovrapposto. Un raccordo a T di Revit spezzerebbe il collettore e
ridimensionerebbe l'innesto alla misura del circuito.

L'anteprima riporta le misure disponibili per il tipo scelto, così si vede subito se i DN
inseriti esistono (in caso contrario la creazione usa la misura più vicina e lo segnala).
L'anteprima si aggiorna a ogni modifica; livello, quota e punto iniziale restano quelli
della finestra. Il collettore viene tradotto in un `MepPlan` (un tratto principale con un
gruppo di stacchi per circuito, in posizione esplicita e con `Connect = false`) e creato
dallo stesso `RevitPlanBuilder` della modalità testuale.

Valori predefiniti: interasse 150 mm, lunghezza circuiti 500 mm, collettore lungo +X,
circuiti verso il basso. Le impostazioni e l'elenco dei DN vengono ricordati tra una
sessione e l'altra.

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

Compilazione manuale: `dotnet build src/SayRevit.Addin/SayRevit.Addin.csproj -c Release -p:RevitVersion=<anno>`.
I file compilati finiscono in `artifacts/<anno>/`. Installazione manuale: crea la cartella
`%APPDATA%\Autodesk\Revit\Addins\<anno>\SayRevit`, copia **il contenuto** di `artifacts/<anno>/`
(tutti i file, non la cartella) dentro `SayRevit`, poi copia il singolo file `SayRevit.addin` anche in
`%APPDATA%\Autodesk\Revit\Addins\<anno>\` (accanto alla cartella `SayRevit`, non al suo interno). Opzioni: `-p:RevitFramework=net8.0-windows` forza il framework,
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
src/SayRevit.Core     modello (MepPlan, ManifoldPlan), parser a regole IT/EN, formattatore anteprima [netstandard2.0, nessuna dipendenza]
src/SayRevit.Claude   parser Claude con output strutturati                            [netstandard2.0, SDK Anthropic]
src/SayRevit.Addin    add-in Revit: ribbon, finestra WPF, lettura catalogo, costruzione elementi [net48 / net8.0-windows / net10.0-windows]
tests/                test xunit del parser e della conversione JSON
scripts/              install.ps1 / uninstall.ps1
```

Test: `dotnet test` (53 test, eseguibili su qualsiasi sistema operativo).

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
