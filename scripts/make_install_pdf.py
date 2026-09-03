"""Genera docs/Installazione_sayRevit.pdf (guida di installazione passo-passo, in italiano)."""
import os
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (KeepTogether, Paragraph, Preformatted, SimpleDocTemplate, Spacer, Table, TableStyle)

FONT_DIR = "/usr/share/fonts/truetype/dejavu/"
pdfmetrics.registerFont(TTFont("DejaVu", FONT_DIR + "DejaVuSans.ttf"))
pdfmetrics.registerFont(TTFont("DejaVu-Bold", FONT_DIR + "DejaVuSans-Bold.ttf"))
pdfmetrics.registerFont(TTFont("DejaVuMono", FONT_DIR + "DejaVuSansMono.ttf"))
pdfmetrics.registerFontFamily("DejaVu", normal="DejaVu", bold="DejaVu-Bold", italic="DejaVu", boldItalic="DejaVu-Bold")

OUT = "docs/Installazione_sayRevit.pdf"
BLUE = colors.HexColor("#1F6FB2")
GREY = colors.HexColor("#555555")
LIGHT = colors.HexColor("#F3F6FA")
BORDER = colors.HexColor("#BBBBBB")

ss = getSampleStyleSheet()
body = ParagraphStyle("body", parent=ss["Normal"], fontName="DejaVu", fontSize=10, leading=14, spaceAfter=4)
small = ParagraphStyle("small", parent=body, fontSize=8.5, leading=11, textColor=GREY)
h1 = ParagraphStyle("h1", parent=body, fontName="DejaVu-Bold", fontSize=20, leading=24, textColor=BLUE, spaceAfter=2)
h2 = ParagraphStyle("h2", parent=body, fontName="DejaVu-Bold", fontSize=13, leading=17, textColor=BLUE, spaceBefore=8, spaceAfter=3)
stepTitle = ParagraphStyle("stepTitle", parent=body, fontName="DejaVu-Bold", fontSize=10.5, leading=14, spaceAfter=2)
code = ParagraphStyle("code", parent=body, fontName="DejaVuMono", fontSize=8.8, leading=12, backColor=LIGHT,
                      borderPadding=(4, 5, 4, 5), spaceBefore=2, spaceAfter=2)
numStyle = ParagraphStyle("num", parent=body, fontName="DejaVu-Bold", fontSize=11, leading=13, textColor=colors.white, alignment=1)


def P(t, st=body):
    return Paragraph(t, st)


def Code(t):
    return Preformatted(t, code)


def Step(n, title, blocks):
    """Un passo numerato: cerchio blu con il numero a sinistra, titolo + contenuto a destra."""
    numCell = Table([[P(str(n), numStyle)]], colWidths=[10 * mm], rowHeights=[9 * mm])
    numCell.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (0, 0), BLUE),
        ("ALIGN", (0, 0), (0, 0), "CENTER"),
        ("VALIGN", (0, 0), (0, 0), "MIDDLE"),
        ("ROUNDEDCORNERS", [8, 8, 8, 8]),
    ]))
    right = [P(title, stepTitle)] + blocks
    t = Table([[numCell, right]], colWidths=[13 * mm, 157 * mm])
    t.setStyle(TableStyle([
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 0),
        ("RIGHTPADDING", (0, 0), (-1, -1), 0),
        ("TOPPADDING", (0, 0), (-1, -1), 0),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
    ]))
    return KeepTogether(t)


def footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("DejaVu", 8)
    canvas.setFillColor(GREY)
    canvas.drawString(18 * mm, 12 * mm, "sayRevit – Installazione passo per passo")
    canvas.drawRightString(A4[0] - 18 * mm, 12 * mm, "Pagina %d" % doc.page)
    canvas.restoreState()


story = []
story.append(P("sayRevit – Installazione", h1))
story.append(P("Add-in per Revit che crea tubazioni e canali (con stacchi) da una descrizione scritta. "
               "Segui i passi nell'ordine: al passo 10 avrai la prima tubazione nel modello.", body))
story.append(P("Repository: https://github.com/markwilson666/sayRevit · branch: claude/revit-mep-text-interface-etmi4j", small))
story.append(Spacer(1, 8))

story.append(Step(1, "Verifica la tua versione di Revit", [
    P("Apri Revit → menu ? (in alto a destra) → <b>Informazioni su Autodesk Revit</b>. Annota l'anno: "
      "<b>2024, 2025, 2026 o 2027</b>. Ti servirà ai passi 2 e 6. Poi chiudi Revit."),
]))

story.append(Step(2, "Installa l'SDK .NET", [
    P("Vai su <b>https://dotnet.microsoft.com/download</b> e scarica l'installer per Windows x64 di:"),
    P("• <b>.NET SDK 8</b> se hai Revit 2024, 2025 o 2026<br/>• <b>.NET SDK 10</b> se hai Revit 2027"),
    P("Esegui l'installer e completa con Avanti fino alla fine. Serve solo per compilare: scegli l'<b>SDK</b>, non il \"Runtime\"."),
]))

story.append(Step(3, "Installa Git per Windows", [
    P("Vai su <b>https://git-scm.com/download/win</b>, scarica l'installer ed eseguilo accettando le opzioni proposte."),
]))

story.append(Step(4, "Scarica il codice di sayRevit", [
    P("Premi <b>Start</b>, scrivi <b>PowerShell</b> e apri \"Windows PowerShell\" (utente normale, non amministratore). "
      "Copia e incolla queste tre righe, poi premi Invio:"),
    Code("git clone https://github.com/markwilson666/sayRevit\n"
         "cd sayRevit\n"
         "git checkout claude/revit-mep-text-interface-etmi4j"),
    P("Risultato: viene creata la cartella <font face='DejaVuMono'>sayRevit</font> nella posizione in cui si trovava "
      "PowerShell (di solito <font face='DejaVuMono'>C:\\Users\\&lt;tuo utente&gt;</font>) e PowerShell è già al suo interno. "
      "<b>Non chiudere questa finestra</b>: serve anche ai passi 5 e 6."),
]))

story.append(Step(5, "Autorizza l'esecuzione degli script (solo per questa finestra)", [
    P("Nella stessa finestra di PowerShell incolla e premi Invio:"),
    Code("Set-ExecutionPolicy -Scope Process Bypass"),
    P("Se compare una domanda, rispondi <b>S</b> (Sì). Vale solo per questa finestra di PowerShell."),
]))

story.append(Step(6, "Compila e installa", [
    P("Sempre nella stessa finestra, incolla il comando sostituendo <b>2025</b> con l'anno annotato al passo 1:"),
    Code(".\\scripts\\install.ps1 -RevitVersion 2025"),
    P("Lo script trova la tua installazione di Revit, compila l'add-in per il runtime esatto di quel Revit e copia da solo "
      "tutti i file al posto giusto. Al termine deve comparire la scritta verde "
      "<b>\"Installato in: …\\Addins\\&lt;anno&gt;\\SayRevit\"</b>. Se invece compare un errore rosso: leggi il messaggio, consulta la "
      "tabella all'ultima pagina e, se non risolvi, usa l'<b>installazione manuale</b> qui sotto (passi 6a–6e)."),
]))

manual = [
    P("<b>Alternativa al passo 6 – installazione manuale</b> (usala solo se lo script fallisce)", stepTitle),
    P("<b>6a.</b> Nella stessa finestra di PowerShell compila con questo comando (sostituisci <b>2025</b> con il tuo anno):"),
    Code("dotnet build src\\SayRevit.Addin\\SayRevit.Addin.csproj -c Release -p:RevitVersion=2025"),
    P("Attendi la scritta \"Compilazione completata\" / \"Build succeeded\"."),
    P("<b>6b.</b> Apri Esplora file e vai nella cartella del progetto <font face='DejaVuMono'>sayRevit</font> "
      "(di solito <font face='DejaVuMono'>C:\\Users\\&lt;tuo utente&gt;\\sayRevit</font>): dentro trovi la cartella "
      "<font face='DejaVuMono'>artifacts\\2025</font> (o il tuo anno) con i file compilati."),
    P("<b>6c.</b> In un'altra finestra di Esplora file scrivi nella barra dell'indirizzo "
      "<font face='DejaVuMono'>%APPDATA%\\Autodesk\\Revit\\Addins\\2025</font> e premi Invio "
      "(se la cartella dell'anno non esiste, creala). Qui dentro crea una nuova cartella chiamata esattamente "
      "<font face='DejaVuMono'>SayRevit</font>."),
    P("<b>6d.</b> Seleziona <b>tutti i file e le sottocartelle CONTENUTI</b> in <font face='DejaVuMono'>artifacts\\2025</font> "
      "(Ctrl+A) e copiali <b>dentro</b> la cartella <font face='DejaVuMono'>SayRevit</font> appena creata "
      "(non copiare la cartella artifacts stessa)."),
    P("<b>6e.</b> Nella cartella <font face='DejaVuMono'>SayRevit</font> trova il singolo file "
      "<font face='DejaVuMono'>SayRevit.addin</font> e <b>copialo</b> (Ctrl+C, Ctrl+V) anche un livello sopra, cioè direttamente in "
      "<font face='DejaVuMono'>%APPDATA%\\Autodesk\\Revit\\Addins\\2025</font>, accanto alla cartella SayRevit, non al suo interno. "
      "Al termine devi avere: il file <font face='DejaVuMono'>SayRevit.addin</font> in <font face='DejaVuMono'>Addins\\2025</font> "
      "e la cartella <font face='DejaVuMono'>Addins\\2025\\SayRevit</font> piena di file (tra cui SayRevit.Addin.dll)."),
]
mt = Table([["", manual]], colWidths=[13 * mm, 157 * mm])
mt.setStyle(TableStyle([
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("BACKGROUND", (1, 0), (1, 0), colors.HexColor("#FFF7E0")),
    ("LEFTPADDING", (0, 0), (-1, -1), 0), ("RIGHTPADDING", (0, 0), (-1, -1), 0),
    ("TOPPADDING", (1, 0), (1, 0), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 7),
    ("LEFTPADDING", (1, 0), (1, 0), 6), ("RIGHTPADDING", (1, 0), (1, 0), 6),
]))
story.append(mt)

story.append(Step(7, "Avvia Revit e autorizza l'add-in", [
    P("Apri Revit. Alla finestra di sicurezza che nomina \"sayRevit\" premi <b>Carica sempre</b>."),
]))

story.append(Step(8, "Apri un progetto MEP e una pianta", [
    P("Crea un nuovo progetto scegliendo il modello <b>Impianti</b> (o Meccanico/Sistemi), oppure apri un tuo progetto MEP. "
      "Apri una <b>vista in pianta</b> di un livello (es. Livello 1)."),
]))

story.append(Step(9, "Apri il comando", [
    P("Nella barra multifunzione in alto, clicca la scheda <b>sayRevit</b> e poi il pulsante <b>Testo → Tubazioni/Canali</b>."),
]))

story.append(Step(10, "Crea la prima tubazione", [
    P("Nella casella di testo scrivi esattamente:"),
    Code("una tubazione DN100 lunga 6 m con 3 stacchi DN20 ogni 1,5 m verso l'alto"),
    P("Premi <b>Interpreta (anteprima)</b> e controlla il riepilogo. Poi premi <b>Crea in Revit</b>: compare un resoconto e "
      "gli elementi restano selezionati. Aprili in una vista 3D per vederli. Per eliminarli tutti insieme: <b>Ctrl+Z</b>."),
]))

story.append(P("Aggiornare / rimuovere", h2))
story.append(P("<b>Aggiornare</b>: chiudi Revit, apri PowerShell nella cartella <font face='DejaVuMono'>sayRevit</font> "
               "(tasto destro sulla cartella → \"Apri nel terminale\") ed esegui, con il tuo anno:"))
story.append(Code("git pull\nSet-ExecutionPolicy -Scope Process Bypass\n.\\scripts\\install.ps1 -RevitVersion 2025"))
story.append(P("<b>Rimuovere</b>: stesso procedimento con <font face='DejaVuMono'>.\\scripts\\uninstall.ps1 -RevitVersion 2025</font>."))

story.append(P("Modalità Claude (facoltativa)", h2))
story.append(P("Serve una chiave API Anthropic. Start → \"variabili d'ambiente\" → <b>Modifica le variabili d'ambiente relative "
               "all'account</b> → Nuova… → nome <font face='DejaVuMono'>ANTHROPIC_API_KEY</font>, valore = la tua chiave → OK. "
               "Riavvia Revit e nella finestra scegli <b>Interprete: Claude (AI)</b>. Senza chiave usa l'interprete \"Regole (offline)\": "
               "funziona sempre, senza internet."))

story.append(P("Se qualcosa non va", h2))
rows = [
    [P("<b>Errore / sintomo</b>"), P("<b>Soluzione</b>")],
    [P("\"l'esecuzione di script è disabilitata\""), P("Hai saltato il passo 5: esegui il comando del passo 5 e ripeti il passo 6.")],
    [P("\"dotnet non riconosciuto\""), P("Hai saltato il passo 2, o PowerShell era aperto durante l'installazione dell'SDK: chiudi e riapri PowerShell, esegui cd sayRevit e ripeti dal passo 5.")],
    [P("\"Revit … non trovato in C:\\Program Files\\Autodesk\\…\""), P("L'anno indicato al passo 6 non corrisponde a un Revit installato. Il messaggio elenca le versioni trovate: ripeti il passo 6 con una di quelle.")],
    [P("All'avvio di Revit: \"Could not load file or assembly 'System.Runtime…'\""), P("Add-in compilato per un'altra versione di Revit. Esegui uninstall.ps1 con l'anno sbagliato, poi install.ps1 con l'anno giusto (passo 1).")],
    [P("La scheda sayRevit non compare"), P("Alla finestra di sicurezza hai premuto \"Non caricare\". Chiudi Revit, ripeti il passo 6 e all'avvio premi Carica sempre.")],
    [P("\"Il progetto non contiene tipi di tubazione/canale\""), P("Il progetto non è MEP: ripeti il passo 8 scegliendo il modello Impianti.")],
    [P("Nel resoconto: stacchi \"lasciati scollegati\""), P("Mancano i raccordi nel tipo usato: seleziona una tubazione → Modifica tipo → Preferenze di instradamento → imposta Giunzioni e Gomiti, poi riprova.")],
]
tbl = Table(rows, colWidths=[62 * mm, 108 * mm])
tbl.setStyle(TableStyle([
    ("BACKGROUND", (0, 0), (-1, 0), LIGHT),
    ("GRID", (0, 0), (-1, -1), 0.4, BORDER),
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("LEFTPADDING", (0, 0), (-1, -1), 5), ("RIGHTPADDING", (0, 0), (-1, -1), 5),
    ("TOPPADDING", (0, 0), (-1, -1), 3), ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
]))
story.append(tbl)
story.append(Spacer(1, 6))
story.append(P("Per ogni altro problema: copia il messaggio d'errore esatto e la versione di Revit e inviali allo sviluppatore.", small))

os.makedirs("docs", exist_ok=True)
doc = SimpleDocTemplate(OUT, pagesize=A4, leftMargin=18 * mm, rightMargin=18 * mm, topMargin=16 * mm, bottomMargin=20 * mm,
                        title="sayRevit – Installazione passo per passo", author="sayRevit",
                        subject="Installazione dell'add-in sayRevit per Revit")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print("scritto", OUT)
