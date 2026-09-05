# Guida per l'agente: il collettore di sayRevit

Questa guida serve a chi (persona o agente) deve **modificare o estendere il collettore**:
aggiungere un elemento, una tipologia di circuito, un parametro, oppure riprodurre in Revit un
assemblaggio partendo da una foto o da uno schema. È scritta per ridurre ogni intervento a pochi
casi ricorrenti e per dire, per ciascuno, quali file toccare e quali no.

Regole fisse, prima di tutto:

- **La geometria si decide nel Core, in millimetri, senza tipi Revit** (`src/SayRevit.Core`). Il
  costruttore Revit (`RevitPlanBuilder`) esegue e basta. Se una decisione geometrica sta nel
  builder, è nel posto sbagliato.
- **Non lanciare il banco di prova da solo.** Si compila e si installa; la prova in Revit la fa
  l'utente, che manda i risultati (`result.txt`, `view.png`). I test xunit invece si lanciano sempre.
- Ogni modifica di comportamento ha **un test nel Core** e, se cambia i messaggi dell'anteprima,
  aggiorna i test esistenti sul testo.
- Nomi, etichette e commenti in italiano; il file delle impostazioni resta leggibile all'indietro.

Comandi:

```
dotnet test tests/SayRevit.Core.Tests/SayRevit.Core.Tests.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install.ps1 -RevitVersion 2027
```

## 1. Mappa del codice

| Dove | Cosa | Quando si tocca |
| --- | --- | --- |
| `Core/Model/ManifoldElements.cs` | **Registro degli elementi** (sfera, boax, energy valve, zona, filtro, ritegno, pompa): chiave, `ValveKind`, etichette, parole per proporre la famiglia, flange, sezione del pannello, chiave nelle impostazioni, `FromMechanicalEquipment` (pompe: categoria "Attrezzatura meccanica"), `NoFlangeHints` (famiglie filettate senza flange automatiche), `DefaultThresholds` (soglie proposte, es. ritegno: R60 fino a DN32, boa-rvk da DN40) | Nuovo accessorio |
| `Core/Model/FamilyByDn.cs` | Famiglia per diametro: base + soglie "da DN x in su usa y"; forma salvata `fam\|40=altra` | Quasi mai |
| `Core/Model/CircuitKind.cs` | Tipologie di circuito (`CircuitKind`, `CircuitKindInfo`: codice, componenti di mandata/ritorno, flag `HasBypass`, `IsChainModelled`…) | Nuova tipologia |
| `Core/Model/ManifoldPlan.cs` | **Il piano**: parametri in mm, `ChainFor` (ricetta della catena), `BypassFor`, `PieceFor`, `ValveFor`, `DescribeChain`, messaggi dell'anteprima (`Collect…Messages`), serializzazione dei circuiti (`CircuitToString`/`ParseCircuit`), interasse automatico | Ordine dei pezzi, distanze, nuove regole |
| `Core/Model/MepPlan.cs` | Modello di uscita: `MepRun`/`MepBranch`, `StubItem` (Piece/Gap/Tee), `MepBypass`, `MepValve` (famiglia, tipo, DN, flange, rotazione, `Reversed`) | Nuovo primitivo geometrico |
| `Core/Model/ManifoldSpacing.cs` | Interasse minimo dagli ingombri misurati | Raramente |
| `Core/Model/ValveTypeMatcher.cs` | Scelta del tipo sul nome (DN, PN, parola preferita) | Nuovi formati di nome |
| `Addin/Revit/RevitPlanBuilder.cs` | Costruzione in Revit: `BuildChain`, `PlaceInline`/`MountAssembly`, `PlaceBypassTee`, `CompleteBypasses`, `MakeTransition`, `MakeElbow`, `ResolveAutoSpacing`, `PrepareFamily` | Solo per estendere un primitivo |
| `Addin/ManifoldPlanFactory.cs` | Impostazioni + catalogo → piano (usato dal banco) | Nuovo parametro |
| `Addin/Settings.cs` | `%APPDATA%\sayRevit\settings.txt`, chiave=valore | Nuovo parametro |
| `Addin/UI/ManifoldPanel.cs` | Pannello WPF: righe dei circuiti, sezioni "Valvole sugli stacchi" e "Mix 2 vie" | Nuovo parametro / campo di riga |
| `Addin/UI/FamilyPicker.cs`, `FamilyRulesDialog.cs` | Tendina famiglia + pulsante "per DN…" e finestra delle soglie | Quasi mai |
| `Addin/Automation.cs` | Banco headless (`request.txt` → `result.txt`, `view.png`, `created.txt`, `catalog.txt`) | Nuove diagnostiche |
| `tests/SayRevit.Core.Tests` | xunit: `ManifoldMixTwoWayTests`, `ManifoldCircuitKindTests`, `ManifoldValveTests`, `FamilyByDnTests`, `ManifoldElementsTests` | Sempre |

Flusso: **pannello / impostazioni → `ManifoldPlan` → `ToParseResult()` (MepPlan + note e avvisi) →
`RevitPlanBuilder`**. L'anteprima nella finestra è la stessa `ParseResult` che si costruirà: se
l'anteprima dice una cosa e Revit ne fa un'altra, il baco è nel builder.

## 2. I cinque casi geometrici ricorrenti

Tutto ciò che sta su uno stacco si esprime con questi primitivi, e solo con questi. Se un'esigenza
nuova non ci rientra, si **estende il primitivo** (un campo in più su `StubItem`/`MepBypass`, gestito
in `BuildChain`/`CompleteBypasses`) e si aggiunge un test: mai un ramo speciale nel builder per un
singolo elemento.

| # | Caso | Nel Core | In Revit | Cose già gestite |
| --- | --- | --- | --- | --- |
| 1 | **Pezzo in linea** (valvola, filtro, energy valve…) | `StubItem.Of(MepValve)`; il pezzo viene da `PieceFor(chiave, dn)` o `ValveFor(dn)` | `PlaceInline` → `MountAssembly` (pezzo + eventuali flange), `PositionAssembly`, `JoinAssembly` | Flange automatiche (`WithFlanges`), rotazione attorno al tubo (`RollDegrees` 0/90/180/270), verso invertito (`Reversed`), pezzo più piccolo del tubo → tratti corti + riduzioni (`MakeTransition`), famiglie con un solo connettore, famiglie level-based (convertite o saltate con avviso) |
| 2 | **Tubo libero** tra due elementi | `StubItem.Gap(mm, etichetta)`; `Gap()` impone il minimo `Mix2MinGapMm` = 50 | il cursore di `BuildChain` | Vincoli espressi come gap etichettati (es. tratto rettilineo 5×Øint sopra la energy valve: `EnergyValveStraightMm`) |
| 3 | **T che divide lo stacco** | `StubItem.TeeFor(etichetta, dnDopo)`; da lì in poi tubi e pezzi hanno `dnDopo` | `PlaceBypassTee` (T ridotto dalle preferenze di instradamento) | Registra la metà di bypass da chiudere |
| 4 | **Derivazione fuori dallo stacco** (bypass) | `MepBypass`: dove parte (`LegAlongMm`/`LegSideMm`), dove arriva (`PartnerAlongMm`/`PartnerSideMm`), DN, pezzo in linea (`InlinePiece`) | `CompleteBypasses`: prima di traverso (Y), poi lungo (X), tratto verticale con due gomiti e il pezzo | Oggi un solo caso: bypass mandata↔ritorno del mix 2 vie. Una derivazione diversa = nuovi campi su `MepBypass`, non codice nuovo |
| 5 | **Terminazione dello stacco** | `PipeAfterValveFor(circuito)`: `Mix2EndPipeMm`, `NoPumpPipeAfterValveMm`, 0 per il cieco; fondelli con `CapEnds` | trim del tubo finale | — |

Il primo pezzo della catena è l'intercettazione, centrata a `ValveAxisDistanceMm` dall'asse del
collettore; ogni pezzo successivo parte dalla faccia d'uscita del precedente più il gap.

**Allineamento tra stacchi gemelli.** Un pezzo con `StubItem.AlignKey` (oggi l'intercettazione in
cima, `ManifoldPlan.TopShutoffAlignKey`) viene montato alla stessa quota nelle catene di mandata e
ritorno dello stesso circuito (`MepBranch.PairKey`). Il costruttore non stima: la prima catena che
arriva a quel pezzo si ferma (`ChainState` in `_pausedChains`, pezzo già montato al banco), la
seconda calcola la quota comune (la più alta delle due naturali), si completa e riprende la prima;
la catena più corta riceve tubo in più davanti al pezzo. Le catene rimaste senza gemello si
completano a fine costruzione (`FinishPausedChains`). Per allineare un altro pezzo basta dargli la
stessa `AlignKey` su entrambe le catene.

## 3. Ricette

### 3.1 Aggiungere un elemento (accessorio)

1. `ValveKind` (in `ValveTypeMatcher.cs`) e la sua etichetta in `MepValve.KindLabelOf` (`MepPlan.cs`).
2. Una voce in `ManifoldElements.All`: `Key`, `Kind`, `Label`, `ShortLabel`, `UiLabel`,
   `SettingsKey` (`Manifold<Nome>Family`), `Hints` (parole nel nome della famiglia per proporla),
   `TypeWord` (parola preferita nel tipo), `WithFlanges`, `Section`, `Tooltip`.
3. Il suo posto nella ricetta: in `ChainFor` (o `BypassFor`) `chain.Add(StubItem.Of(PieceFor(ManifoldElements.Nuovo, dn)))`
   più i gap attorno. `PieceFor` restituisce null senza famiglia: il pezzo si salta e l'anteprima lo dice.
4. Se serve dichiararlo nelle tipologie: `CircuitComponent` in `CircuitKind.cs` e le liste dei componenti.
5. Un test nel Core: la catena contiene il pezzo nel punto giusto, `DescribeChain` dà la riga attesa,
   l'anteprima ha la nota. Un test sul salvataggio non serve: è generico.
6. Particolarità della famiglia (verso, rotazione propria) → campi su `MepValve` letti dal builder
   (`Reversed`, `RollDegrees`), come per il filtro a Y. Mai un `if` sul nome della famiglia nel builder.

Gratis, dal registro: tendina + "per DN…" nel pannello, chiave nelle impostazioni (lettura e
scrittura), factory, nota "Famiglia per DN" in anteprima, preparazione della famiglia in Revit
(`ConfiguredFamilies`), ingombro nell'interasse automatico.

### 3.2 Aggiungere una tipologia di circuito

1. `CircuitKind` + `CircuitKindInfo` in `CircuitKind.cs` (codice breve per il salvataggio, etichetta,
   componenti di mandata e ritorno, flag). La tendina della riga si popola da `CircuitKinds.All`.
2. Se ha una catena modellata: `IsChainModelled` = true e il ramo in `ChainFor`/`BypassFor` (riusare i
   cinque casi). Se non è ancora modellata, i componenti restano dichiarati come "in attesa".
3. Test in `ManifoldCircuitKindTests` (componenti, riepilogo, salvataggio `DN:codice|…`).

### 3.3 Aggiungere un parametro (lunghezza, rotazione, opzione)

Sei punti, sempre gli stessi: proprietà su `ManifoldPlan` (con predefinito e commento) → chiave in
`Settings` (proprietà, `case` in `MergeFrom`, riga in `Save`) → `ManifoldPlanFactory` → campo nel
pannello (`Labeled(...)`, evento `Notify`) → `BuildPlan`/`LoadSettings`/`StoreSettings` → una nota
in anteprima e un test. Le opzioni per circuito vanno invece su `ManifoldCircuit` e nel formato
`DN:codice|chiave=valore` (`CircuitToString`/`ParseCircuit`), con la casella nella riga.

### 3.4 Cambiare ordine, distanze o DN dei pezzi

Solo `ChainFor`/`BypassFor` e i test. Le distanze sono sempre gap etichettati; i cambi di DN sono
sempre un T con `SizeAfterMm` o l'accoppiata tratto corto + riduzione che il builder fa da sé.

## 4. Da una foto (o uno schema) alla catena

L'agente può leggere immagini con lo strumento Read (PNG/JPG). Procedura:

1. **Guardare l'immagine e compilare una tabella per montante**, dal collettore verso l'utenza:
   `elemento | DN | flangiato | orientamento (leva/attuatore dove) | tubo prima (mm)`. Le
   derivazioni a parte: da quale pezzo partono, in che direzione vanno prima (di traverso o lungo
   il collettore), cosa portano.
2. **Scrivere la catena nella notazione di una riga** usata da `DescribeChain`:

   ```
   collettore → [sfera DN25] — 150 — [T →DN25] — 150 — (spazio riservato alla pompa 400) — 150 — [zona DN25*] — 50 — [sfera DN25] — 100 fine
   ```

   Tra parentesi quadre i pezzi (`*` = tra due flange), i numeri sono tubo libero in mm, `[T →DNxx]`
   un T che riduce. Questa riga compare nell'anteprima e nei test: è il **contratto** tra immagine,
   codice e Revit. Concordarla con l'utente prima di scrivere codice, se l'immagine è ambigua.
3. **Ricondurre ogni voce ai cinque casi** del §2. Se qualcosa non ci rientra, dirlo: o si estende
   un primitivo o si chiede all'utente.
4. **Implementare** in `ChainFor`/`BypassFor` e aggiungere un test che confronta `DescribeChain` con
   la riga concordata.
5. **Compilare e installare**; chiedere all'utente di costruire (o di lanciare il banco) e di mandare
   `view.png`, `result.txt`, `created.txt`. Il banco: `%APPDATA%\sayRevit\automation\request.txt` con
   `build=yes`, `clean=tracked`, `view=x,y,z` (occhio della vista: scegliere una direzione che non
   nasconda la derivazione), `catalog=yes` per l'elenco di famiglie, tipi e connettori
   (`catalog.txt`), `Manifold*=…` per sovrascrivere impostazioni. Revit deve essere in primo piano,
   senza dialoghi né comandi attivi.
6. **Confrontare `view.png` con l'immagine di riferimento** e correggere: verso (`Reversed`),
   rotazione (`RollDegrees`, 0/90/180/270), gap, DN. Ripetere.

## 5. Cose note da non riscoprire

- Tra due elementi ci sono sempre almeno 50 mm di tubo (`Mix2MinGapMm`).
- La pompa (Grundfos MAGNA3, attrezzatura meccanica, un tipo per modello "MAGNA3 25-60 PN10 - …") ha la famiglia
  nel registro e il **modello per circuito** in `ManifoldCircuit.PumpType` (`|pump=` nelle impostazioni). `PumpFor`
  la monta sulla mandata: nel mix 2 vie al posto dello spazio riservato dopo il T del bypass; nel diretto e nel mix 3 vie
  con la catena corta intercettazione → pompa (→ zona) (`HasPumpChain`), ritorno senza catena. I modelli "F" vanno tra
  due flange; rotazione e verso da `PumpRollDegrees`/`PumpReversed`.
- La energy valve Belimo è una famiglia unica (`Belimo_EV…R2_BAC_RFA_2027_LevelBased`) con un tipo per
  DN (`EV015R2+BAC` … `EV050R2+BAC`): il DN si legge dal nome del tipo (`EnergyValveChoices` in
  `ManifoldPlan`); le vecchie famiglie per DN (`ev025r2…`) restano riconosciute dal nome della famiglia.
- Il **tubo libero** (i `Gap` della catena, incluso il tratto rettilineo dopo la energy valve) deve essere tubo e
  basta: il builder lo conta oltre la geometria che il pezzo ha fuori dai connettori (`Assembly.OverhangFarMm`,
  es. il sensore Belimo, 150 mm) e mette il T del bypass oltre il tratto libero di tutto lo spazio che il T e la
  sua riduzione automatica consumano (`MeasureTeeRoomMm`, misurato al banco). Niente raccordi dentro il tratto.
- La energy valve Belimo vuole a monte un tratto rettilineo di 5×Øint del tubo **adiacente** (se è più
  piccola dello stacco, il tratto corto della sua misura, prima della riduzione).
- Le famiglie Belimo DN50 hanno un solo connettore idraulico: il pezzo si collega da un lato e
  l'altra estremità si legge dalla geometria del corpo.
- `Join` rifiuta connettori di raggio diverso (altrimenti Revit cancella il tubo al commit); le
  riduzioni si fanno con `MakeTransition` e un tratto corto della misura del pezzo (`AdapterMm`).
- Famiglie level-based su stacchi verticali vengono saltate con avviso; il caricatore prova a
  convertirle in work-plane-based una volta sola e riporta l'errore vero.
- Filtro a Y predefinito: IMI TA-STR filettato (`IMI_TA-STR_RFA_2027_LevelBased`, tipi `43250-000315 DN15`…) fino a DN32,
  da DN40 `VIR_895_RFA_2027_LevelBased` (tipi `895 DN40`…) wafer tra due flange automatiche come la boax
  (`WithFlanges` vero; `NoFlangeHints` esclude TA-STR e Watts Y33P, che hanno le flange proprie).
- I T e i gomiti vengono dalle preferenze di instradamento del tipo di tubazione; "Acc nero
  saldare" ha Bogen, Abzweigung, Übergang, Flansch, Enddeckel.
- Il banco è muto se Revit ha un dialogo aperto o un comando attivo (Esc, poi in primo piano).
- Il file delle impostazioni deve restare compatibile: nuove chiavi con predefinito, formati vecchi letti.

## 6. Stato e lavori aperti

- Pompa: nessuna famiglia, spazio riservato (`Mix2PumpSpaceMm`). Quando arriva: elemento nel
  registro + `StubItem.Of(PieceFor(ManifoldElements.Pump, dn))` al posto del gap etichettato.
- Mix 3 vie: componenti dichiarati, catena non modellata (valvola a 3 vie e bypass).
- Valvola di zona per le tipologie senza catena: scelta salvata e dichiarata, non montata.
- Circuito diretto: pompa dichiarata.
