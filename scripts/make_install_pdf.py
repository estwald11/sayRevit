"""Genera docs/Installazione_sayRevit.pdf (guida di installazione in italiano)."""
from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (ListFlowable, ListItem, PageBreak, Paragraph, Preformatted, SimpleDocTemplate, Spacer, Table,
                                TableStyle, KeepTogether)

FONT_DIR = "/usr/share/fonts/truetype/dejavu/"
pdfmetrics.registerFont(TTFont("DejaVu", FONT_DIR + "DejaVuSans.ttf"))
pdfmetrics.registerFont(TTFont("DejaVu-Bold", FONT_DIR + "DejaVuSans-Bold.ttf"))
OBL = FONT_DIR + "DejaVuSans-Oblique.ttf"
import os
pdfmetrics.registerFont(TTFont("DejaVu-Oblique", OBL if os.path.exists(OBL) else FONT_DIR + "DejaVuSans.ttf"))
pdfmetrics.registerFont(TTFont("DejaVuMono", FONT_DIR + "DejaVuSansMono.ttf"))
pdfmetrics.registerFontFamily("DejaVu", normal="DejaVu", bold="DejaVu-Bold", italic="DejaVu-Oblique", boldItalic="DejaVu-Bold")

OUT = "docs/Installazione_sayRevit.pdf"
BLUE = colors.HexColor("#1F6FB2")
GREY = colors.HexColor("#555555")
LIGHT = colors.HexColor("#F3F6FA")

ss = getSampleStyleSheet()
body = ParagraphStyle("body", parent=ss["Normal"], fontName="DejaVu", fontSize=10, leading=14, spaceAfter=6)
small = ParagraphStyle("small", parent=body, fontSize=8.5, leading=11, textColor=GREY)
h1 = ParagraphStyle("h1", parent=body, fontName="DejaVu-Bold", fontSize=20, leading=24, textColor=BLUE, spaceAfter=4)
h2 = ParagraphStyle("h2", parent=body, fontName="DejaVu-Bold", fontSize=13, leading=17, textColor=BLUE, spaceBefore=12, spaceAfter=4)
h3 = ParagraphStyle("h3", parent=body, fontName="DejaVu-Bold", fontSize=10.5, leading=14, spaceBefore=6, spaceAfter=2)
code = ParagraphStyle("code", parent=body, fontName="DejaVuMono", fontSize=8.8, leading=12, backColor=LIGHT,
                      borderPadding=(5, 6, 5, 6), leftIndent=4, rightIndent=4, spaceBefore=4, spaceAfter=8)
note = ParagraphStyle("note", parent=body, backColor=colors.HexColor("#FFF7E0"), borderPadding=(5, 6, 5, 6),
                      leftIndent=4, rightIndent=4, spaceBefore=4, spaceAfter=8)


def P(t, st=body):
    return Paragraph(t, st)


def Code(t):
    return Preformatted(t, code)


def Bullets(items, numbered=False):
    return ListFlowable([ListItem(P(i), leftIndent=12) for i in items],
                        bulletType="1" if numbered else "bullet", bulletFontName="DejaVu", bulletFontSize=9,
                        leftIndent=14, spaceAfter=6)


def footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("DejaVu", 8)
    canvas.setFillColor(GREY)
    canvas.drawString(20 * mm, 12 * mm, "sayRevit – Guida all'installazione")
    canvas.drawRightString(A4[0] - 20 * mm, 12 * mm, "Pagina %d" % doc.page)
    canvas.restoreState()


story = []
story.append(P("sayRevit – Guida all'installazione", h1))
story.append(P("Add-in per Autodesk Revit che crea tubazioni e canali (con stacchi) a partire da una descrizione testuale, "
               "usando i tipi, i sistemi e i livelli già presenti nel progetto.", body))
story.append(P("Repository: https://github.com/markwilson666/sayRevit &nbsp;·&nbsp; branch: claude/revit-mep-text-interface-etmi4j", small))
story.append(Spacer(1, 6))

story.append(P("1. Requisiti", h2))
tbl = Table([
    [P("<b>Versione Revit</b>"), P("<b>Runtime</b>"), P("<b>SDK da installare per compilare</b>")],
    [P("Revit 2024"), P(".NET Framework 4.8 (già incluso in Windows)"), P(".NET SDK 8")],
    [P("Revit 2025 / 2026"), P(".NET 8"), P(".NET SDK 8")],
    [P("Revit 2027"), P(".NET 10"), P(".NET SDK 10")],
    [P("(qualsiasi)"), P("rilevato dallo script da RevitAPI.runtimeconfig.json"), P("SDK di versione uguale o superiore al runtime")],
], colWidths=[38 * mm, 70 * mm, 62 * mm])
tbl.setStyle(TableStyle([
    ("BACKGROUND", (0, 0), (-1, 0), LIGHT),
    ("GRID", (0, 0), (-1, -1), 0.4, colors.HexColor("#BBBBBB")),
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("LEFTPADDING", (0, 0), (-1, -1), 5), ("RIGHTPADDING", (0, 0), (-1, -1), 5),
    ("TOPPADDING", (0, 0), (-1, -1), 3), ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
]))
story.append(tbl)
story.append(Spacer(1, 6))
story.append(Bullets([
    "Windows 10/11 a 64 bit con Autodesk Revit installato (edizione con strumenti MEP: sono necessari tipi di tubazione e canale nel progetto).",
    "<b>.NET SDK</b> (8 oppure 10, vedi tabella) scaricabile da https://dotnet.microsoft.com/download. Serve solo per compilare l'add-in.",
    "<b>Git per Windows</b> (https://git-scm.com) oppure il download dello zip del branch da GitHub.",
    "Facoltativo, solo per la modalità Claude: una chiave API Anthropic.",
]))

story.append(P("2. Scaricare il codice", h2))
story.append(P("Apri <b>PowerShell</b> (non serve come amministratore) e lancia:"))
story.append(Code("git clone https://github.com/markwilson666/sayRevit\n"
                  "cd sayRevit\n"
                  "git checkout claude/revit-mep-text-interface-etmi4j"))
story.append(P("In alternativa scarica lo zip del branch da GitHub (pulsante <i>Code → Download ZIP</i> dopo aver selezionato il branch), "
               "estrailo e apri PowerShell nella cartella estratta."))

story.append(P("3. Compilare e installare", h2))
story.append(P("Sempre da PowerShell, nella cartella del progetto, esegui lo script indicando la <b>tua</b> versione di Revit:"))
story.append(Code(".\\scripts\\install.ps1 -RevitVersion 2025      # oppure 2024, 2026, 2027"))
story.append(P("Lo script cerca Revit in <font face='DejaVuMono'>C:\\Program Files\\Autodesk\\Revit &lt;versione&gt;</font>, legge il runtime .NET "
               "su cui gira quel Revit (file RevitAPI.runtimeconfig.json), compila l'add-in per lo stesso framework con le librerie API "
               "dell'installazione e copia i file in:"))
story.append(Code("%APPDATA%\\Autodesk\\Revit\\Addins\\<versione>\\SayRevit\\      (librerie)\n"
                  "%APPDATA%\\Autodesk\\Revit\\Addins\\<versione>\\SayRevit.addin  (manifest)"))
story.append(P("<b>Importante:</b> indica la versione di Revit <b>realmente installata</b>. Un add-in compilato per una versione "
               "diversa non viene caricato (vedi la tabella dei problemi). Se hai più versioni, ripeti lo script per ciascuna.", note))
story.append(P("Se PowerShell rifiuta di eseguire lo script (\"l'esecuzione di script è disabilitata\"), sblocca l'esecuzione "
               "solo per la finestra corrente e riprova:", note))
story.append(Code("Set-ExecutionPolicy -Scope Process Bypass\n.\\scripts\\install.ps1 -RevitVersion 2025"))
story.append(P("Compilazione manuale (senza script)", h3))
story.append(Code("dotnet build src/SayRevit.Addin/SayRevit.Addin.csproj -c Release -p:RevitVersion=2025"))
story.append(P("I file compilati si trovano in <font face='DejaVuMono'>artifacts\\&lt;versione&gt;\\</font>: copia l'intera cartella in "
               "<font face='DejaVuMono'>%APPDATA%\\Autodesk\\Revit\\Addins\\&lt;versione&gt;\\SayRevit\\</font> e il file "
               "<font face='DejaVuMono'>SayRevit.addin</font> nella cartella <font face='DejaVuMono'>Addins\\&lt;versione&gt;\\</font>."))

story.append(P("4. Primo avvio di Revit", h2))
story.append(Bullets([
    "Avvia Revit. Alla richiesta di sicurezza sull'add-in \"sayRevit\" scegli <b>Carica sempre</b>.",
    "Apri un progetto creato dal <b>modello MEP</b> (Sistemi / Meccanico): servono almeno un tipo di tubazione o di canale e un tipo di sistema.",
    "Posizionati in una <b>vista in pianta</b> di un livello.",
    "Nella barra multifunzione compare la scheda <b>sayRevit</b> con il pulsante <b>Testo → Tubazioni/Canali</b>.",
], numbered=True))

story.append(P("5. Test di funzionamento", h2))
story.append(Bullets([
    "Lascia <i>Interprete: Regole (offline)</i> e <i>Punto iniziale: Origine del progetto</i>.",
    "Scrivi nella casella: <font face='DejaVuMono'>una tubazione DN100 lunga 6 m con 3 stacchi DN20 ogni 1,5 m verso l'alto</font>",
    "Premi <b>Interpreta (anteprima)</b> e leggi il riepilogo con eventuali avvisi (es. diametro non presente nel tipo).",
    "Premi <b>Crea in Revit</b>. Compare un resoconto e gli elementi creati restano selezionati: aprili in una vista 3D.",
    "Per annullare tutto in un colpo solo usa <b>Annulla</b> di Revit (Ctrl+Z): la creazione è un'unica transazione.",
], numbered=True))
story.append(P("Altre frasi da provare", h3))
story.append(Code("canale 400x200 aria di mandata lungo 8 m con 2 stacchi 200x200 laterali\n"
                  "tubo in acciaio zincato DN50 acqua calda sanitaria al livello 1 a quota 2,8 m lungo 4 m\n"
                  "tubazione DN80 lunga 5 m; poi verso l'alto per 2 m; poi DN65 lungo x per 3 m\n"
                  "tubazione DN50 lunga 6 m con stacchi DN15 a 1, 2.5 e 4 m dall'inizio"))

story.append(P("6. Modalità Claude (facoltativa)", h2))
story.append(Bullets([
    "Imposta la variabile d'ambiente <b>ANTHROPIC_API_KEY</b>: Impostazioni di Windows → Sistema → Informazioni → "
    "Impostazioni di sistema avanzate → Variabili d'ambiente → Nuova (variabili utente).",
    "Riavvia Revit (le variabili si leggono all'avvio).",
    "Nella finestra scegli <i>Interprete: Claude (AI)</i>. Il modello predefinito è <font face='DejaVuMono'>claude-opus-5</font>.",
    "Senza chiave l'add-in funziona normalmente con l'interprete a regole, completamente offline.",
]))

story.append(P("7. Aggiornare o rimuovere", h2))
story.append(P("Aggiornamento: nella cartella del progetto esegui <font face='DejaVuMono'>git pull</font> e poi di nuovo "
               "<font face='DejaVuMono'>.\\scripts\\install.ps1 -RevitVersion &lt;versione&gt;</font> con Revit chiuso."))
story.append(P("Rimozione:"))
story.append(Code(".\\scripts\\uninstall.ps1 -RevitVersion 2025"))

story.append(P("8. Risoluzione dei problemi", h2))
prob = Table([
    [P("<b>Sintomo</b>"), P("<b>Cosa controllare</b>")],
    [P("La scheda sayRevit non compare"), P("Verifica che in <font face='DejaVuMono'>%APPDATA%\\Autodesk\\Revit\\Addins\\&lt;versione&gt;</font> ci siano "
                                           "il file SayRevit.addin e la cartella SayRevit, e che la versione passata allo script sia quella di Revit. "
                                           "Controlla di aver risposto \"Carica sempre\" all'avviso di sicurezza.")],
    [P("Errore all'avvio: \"Could not load file or assembly 'System.Runtime, Version=10.0.0.0'\" (o 8.0.0.0)"),
     P("L'add-in è stato compilato per un runtime .NET diverso da quello di Revit (es. compilato per Revit 2027/.NET 10 ma caricato "
       "da Revit 2025-2026/.NET 8). Rimuovi con uninstall.ps1 e rilancia install.ps1 con la versione di Revit realmente installata: "
       "lo script legge il runtime dalla cartella di Revit e compila di conseguenza.")],
    [P("Errore \"dotnet non riconosciuto\""), P("L'SDK .NET non è installato o PowerShell è stato aperto prima dell'installazione: installa l'SDK e riapri PowerShell.")],
    [P("\"Il progetto non contiene tipi di tubazione/canale\""), P("Il progetto non è basato su un modello MEP: apri un progetto con contenuti Sistemi/Meccanico o carica un tipo di tubazione.")],
    [P("Stacchi creati ma \"lasciati scollegati\""), P("Nel tipo di tubazione/canale usato apri <i>Preferenze di instradamento</i> e verifica che esistano famiglie per "
                                                      "Giunzioni (raccordo a T o presa) e Gomiti; caricale dalla libreria di Revit e riprova.")],
    [P("Misura diversa da quella richiesta"), P("Il diametro non è tra quelli del tipo: l'add-in usa il più vicino e lo segnala nell'anteprima. Scegli un altro tipo con "
                                               "<font face='DejaVuMono'>tipo \"Nome del tipo\"</font> oppure aggiungi la misura al segmento del tipo.")],
    [P("Errore durante la creazione"), P("Copia il testo del resoconto, la frase usata e la versione di Revit e inviali per la correzione. Nulla resta nel modello: "
                                        "la transazione viene annullata.")],
], colWidths=[52 * mm, 118 * mm])
prob.setStyle(TableStyle([
    ("BACKGROUND", (0, 0), (-1, 0), LIGHT),
    ("GRID", (0, 0), (-1, -1), 0.4, colors.HexColor("#BBBBBB")),
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("LEFTPADDING", (0, 0), (-1, -1), 5), ("RIGHTPADDING", (0, 0), (-1, -1), 5),
    ("TOPPADDING", (0, 0), (-1, -1), 3), ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
]))
story.append(prob)
story.append(Spacer(1, 8))
story.append(P("Nota: l'add-in è compilato contro le API Revit 2024–2027 ma va verificato nel proprio modello, perché famiglie e preferenze di "
               "instradamento cambiano da progetto a progetto. Usa sempre l'anteprima prima di creare.", small))

doc = SimpleDocTemplate(OUT, pagesize=A4, leftMargin=20 * mm, rightMargin=20 * mm, topMargin=18 * mm, bottomMargin=20 * mm,
                        title="sayRevit – Guida all'installazione", author="sayRevit", subject="Installazione dell'add-in sayRevit per Revit")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print("scritto", OUT)
