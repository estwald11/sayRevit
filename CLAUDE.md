# sayRevit — istruzioni per l'agente

Add-in Revit (2025/2027, .NET) che costruisce tubazioni e collettori da testo o dal pannello parametrico.
Per tutto ciò che riguarda il **collettore** leggi prima `docs/GUIDA-AGENTE-collettore.md`: mappa del
codice, i cinque casi geometrici ricorrenti, le ricette (nuovo elemento, nuova tipologia, nuovo
parametro) e la procedura "da foto a catena".

## Regole

- Geometria e decisioni nel Core (`src/SayRevit.Core`, mm, nessun tipo Revit); `RevitPlanBuilder` esegue.
- Gli elementi montabili stanno in un solo posto: `ManifoldElements.All`. Da lì derivano pannello,
  impostazioni, factory, anteprima e preparazione delle famiglie. Non aggiungere codice per elemento altrove.
- Ogni pezzo entra in catena con `StubItem` (Piece/Gap/Tee) o `MepBypass`; se non basta, si estende
  il primitivo e si aggiunge un test, non un caso speciale nel builder.
- **Non lanciare il banco di prova** (`%APPDATA%\sayRevit\automation`) da solo: compila, installa e
  aspetta i risultati dell'utente (`result.txt`, `view.png`). I test xunit vanno sempre lanciati.
- Messaggi dell'anteprima in italiano; se ne cambi il testo aggiorna i test che lo verificano.
- Il file `settings.txt` resta compatibile all'indietro (nuove chiavi con predefinito, vecchi formati letti).

## Comandi

```
dotnet test tests/SayRevit.Core.Tests/SayRevit.Core.Tests.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install.ps1 -RevitVersion 2027
```

L'installazione funziona anche con Revit aperto (aggiornamento a caldo); se cambia il caricatore
serve un riavvio di Revit, lo script lo dice.
