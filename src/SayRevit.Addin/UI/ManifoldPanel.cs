using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SayRevit.Core.Model;
using SayRevit.Core.Parsing;

namespace SayRevit.Addin.UI
{
    /// <summary>
    /// Pannello parametrico del collettore: l'utente aggiunge un circuito per volta indicandone il DN.
    /// Non usa il linguaggio naturale: produce direttamente un <see cref="ManifoldPlan"/>.
    /// </summary>
    public sealed class ManifoldPanel : Grid
    {
        private static readonly KeyValuePair<string, DirectionKind>[] CircuitDirections =
        {
            new KeyValuePair<string, DirectionKind>("verso il basso", DirectionKind.Down),
            new KeyValuePair<string, DirectionKind>("verso l'alto", DirectionKind.Up),
            new KeyValuePair<string, DirectionKind>("laterale sinistra", DirectionKind.Left),
            new KeyValuePair<string, DirectionKind>("laterale destra", DirectionKind.Right),
            new KeyValuePair<string, DirectionKind>("alternati", DirectionKind.Alternate)
        };

        /// <summary>Voce delle tendine delle famiglie che significa "non mettere questa valvola".</summary>
        private const string NoFamily = "(nessuna)";

        /// <summary>Voce della tendina PN quando la pressione nominale non deve pesare sulla scelta.</summary>
        private const string AnyPn = "(indifferente)";

        /// <summary>Rotazioni offerte per la boax attorno all'asse del tubo.</summary>
        private static readonly string[] RollAngles = { "0", "90", "180", "270" };

        /// <summary>Parole cercate nel nome della famiglia per proporla come valvola a sfera / boax.</summary>
        private static readonly string[] BallHints = { "sfera", "ball" };
        private static readonly string[] ButterflyHints = { "boax", "farfalla", "butterfly", "wafer" };

        private readonly ModelCatalog _catalog;
        private readonly ComboBox _pipeType = new ComboBox();
        private readonly TextBlock _typeInfo = new TextBlock();
        private readonly StackPanel _rows = new StackPanel();
        private readonly List<CircuitRow> _circuits = new List<CircuitRow>();
        private readonly TextBlock _count = new TextBlock();

        private readonly CheckBox _autoHeaderDn = new CheckBox();
        private readonly TextBox _headerDn = new TextBox();
        private readonly TextBox _spacing = new TextBox();
        private readonly CheckBox _autoSpacing = new CheckBox
        {
            Content = "Interasse automatico: il minimo senza interferenze tra gli stacchi (il valore sopra è il minimo)",
            IsChecked = true,
            Margin = new Thickness(0, 2, 0, 4)
        };
        private readonly TextBox _circuitLength = new TextBox();
        private readonly ComboBox _circuitDirection = new ComboBox();
        private readonly CheckBox _withReturn = new CheckBox();
        private readonly TextBox _returnOffset = new TextBox();

        private readonly CheckBox _withValves = new CheckBox();
        private readonly TextBox _ballMaxDn = new TextBox();
        private readonly ComboBox _ballFamily = new ComboBox();
        private readonly ComboBox _butterflyFamily = new ComboBox();
        private readonly ComboBox _valvePn = new ComboBox();
        private readonly TextBox _valveDistance = new TextBox();
        private readonly ComboBox _butterflyRoll = new ComboBox();

        private bool _suspendChanged;

        /// <summary>Sollevato a ogni modifica dei campi: la finestra ne approfitta per aggiornare l'anteprima.</summary>
        public event EventHandler Changed;

        public ManifoldPanel(ModelCatalog catalog)
        {
            _catalog = catalog ?? ModelCatalog.Empty();

            Margin = new Thickness(0, 0, 0, 8);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            PlaceRow(BuildMaterialRow(), 0);
            PlaceRow(BuildCircuitHeader(), 1);
            PlaceRow(BuildCircuitList(), 2);
            PlaceRow(BuildParameters(), 3);
            PlaceRow(BuildValves(), 4);
        }

        private void PlaceRow(UIElement element, int row)
        {
            Grid.SetRow(element, row);
            Children.Add(element);
        }

        // ------------------------------------------------------------------ UI

        /// <summary>
        /// Materiale/tipo di tubazione: scelta deterministica dell'utente fra i tipi caricati nel
        /// progetto. In questa sezione non c'è interpretazione, quindi non esiste un "automatico":
        /// il tipo mostrato è esattamente quello che verrà usato.
        /// </summary>
        private UIElement BuildMaterialRow()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock
            {
                Text = "Materiale / tipo tubazione:",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            _pipeType.Width = 330;
            _pipeType.VerticalAlignment = VerticalAlignment.Center;
            foreach (var t in _catalog.PipeTypes) _pipeType.Items.Add(t.Name);
            if (_pipeType.Items.Count > 0)
            {
                _pipeType.SelectedIndex = 0;
            }
            else
            {
                _pipeType.IsEnabled = false;
                _pipeType.ToolTip = "Questo progetto non contiene tipi di tubazione.";
            }
            _pipeType.SelectionChanged += (s, e) => { UpdatePipeTypeTooltip(); Notify(); };
            panel.Children.Add(_pipeType);

            _typeInfo.Foreground = Brushes.Gray;
            _typeInfo.VerticalAlignment = VerticalAlignment.Center;
            _typeInfo.Margin = new Thickness(10, 0, 0, 0);
            _typeInfo.TextTrimming = TextTrimming.CharacterEllipsis;
            panel.Children.Add(_typeInfo);

            UpdatePipeTypeTooltip();
            return panel;
        }

        /// <summary>Tipo selezionato nel catalogo del progetto, null se non ce ne sono.</summary>
        public CatalogType SelectedType
        {
            get
            {
                var name = _pipeType.SelectedItem as string;
                return name == null ? null : _catalog.PipeTypes.FirstOrDefault(t => t.Name == name);
            }
        }

        /// <summary>Riepilogo del tipo scelto: misure disponibili e raccordi configurati.</summary>
        public string DescribeSelectedType()
        {
            var type = SelectedType;
            if (type == null) return null;

            var sb = new System.Text.StringBuilder();
            sb.Append("Tipo \"").Append(type.Name).Append("\": ");
            if (type.Sizes.Any(z => z.InnerMm > 0))
            {
                // il DN della base viene scelto sul diametro interno: mostrarlo aiuta a capire la scelta
                sb.Append("misure disponibili ");
                sb.Append(string.Join(", ", type.Sizes.Select(z => "DN" + MepSize.Fmt(z.NominalMm) +
                    (z.InnerMm > 0 ? " (Øint " + MepSize.Fmt(z.InnerMm) + ")" : string.Empty))));
            }
            else if (type.AvailableDiametersMm.Count > 0)
            {
                sb.Append("misure disponibili DN ");
                sb.Append(string.Join(", ", type.AvailableDiametersMm.Select(MepSize.Fmt)));
            }
            else
            {
                sb.Append("nessuna misura leggibile dalle preferenze di instradamento");
            }
            // Niente informazioni sui raccordi a T: nel collettore i circuiti non vengono raccordati.
            return sb.ToString();
        }

        private void UpdatePipeTypeTooltip()
        {
            var type = SelectedType;
            if (type == null)
            {
                _typeInfo.Text = string.Empty;
                return;
            }
            _pipeType.ToolTip = DescribeSelectedType();
            _typeInfo.Text = type.AvailableDiametersMm.Count > 0
                ? type.AvailableDiametersMm.Count + " misure disponibili"
                : "misure non leggibili";
        }

        private UIElement BuildCircuitHeader()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = "Circuiti",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            var add = new Button
            {
                Content = "+",
                Width = 26,
                Height = 24,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Padding = new Thickness(0),
                ToolTip = "Aggiungi un circuito (anche con Invio dal campo DN)"
            };
            add.Click += (s, e) => AddCircuit(0, true);
            panel.Children.Add(add);

            panel.Children.Add(new TextBlock
            {
                Text = "aggiungi un circuito e indicane il DN",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 12, 0)
            });

            _count.Foreground = Brushes.Gray;
            _count.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(_count);
            return panel;
        }

        private UIElement BuildCircuitList()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 180,
                Margin = new Thickness(0, 0, 0, 8),
                Content = _rows
            };
            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8),
                Child = scroll
            };
        }

        private UIElement BuildParameters()
        {
            var panel = new WrapPanel();

            _autoHeaderDn.Content = "DN collettore automatico";
            _autoHeaderDn.IsChecked = true;
            _autoHeaderDn.VerticalAlignment = VerticalAlignment.Center;
            _autoHeaderDn.Margin = new Thickness(0, 0, 12, 6);
            _autoHeaderDn.ToolTip = "D = √(1,5·(S₁+S₂+…)/0,785) sulle sezioni dei circuiti, arrotondato al DN commerciale superiore.";
            _autoHeaderDn.Checked += (s, e) => { _headerDn.IsEnabled = false; Notify(); };
            _autoHeaderDn.Unchecked += (s, e) => { _headerDn.IsEnabled = true; Notify(); };
            panel.Children.Add(_autoHeaderDn);

            _headerDn.IsEnabled = false;
            panel.Children.Add(Labeled("DN collettore:", _headerDn, 70));

            panel.Children.Add(Labeled("Interasse (mm):", _spacing, 70));
            _spacing.ToolTip = "Distanza tra due circuiti consecutivi lungo il collettore.";
            panel.Children.Add(_autoSpacing);
            _autoSpacing.ToolTip = "Calcolato in Revit al momento della creazione dagli ingombri reali di valvole, flange e leve, " +
                                   "compresi gli stacchi del ritorno interlacciati.";

            panel.Children.Add(Labeled("Lunghezza circuiti (mm):", _circuitLength, 70));

            foreach (var d in CircuitDirections) _circuitDirection.Items.Add(d.Key);
            _circuitDirection.SelectedIndex = 0;
            panel.Children.Add(Labeled("Partenza circuiti:", _circuitDirection, 130));

            _withReturn.Content = "Collettore di ritorno";
            _withReturn.IsChecked = true;
            _withReturn.VerticalAlignment = VerticalAlignment.Center;
            _withReturn.Margin = new Thickness(0, 0, 12, 6);
            _withReturn.ToolTip = "Clone speculare della mandata su un asse parallelo, sfasato di mezzo interasse:\n" +
                                  "ogni circuito di ritorno cade a metà tra due circuiti di mandata.";
            _withReturn.Checked += (s, e) => { _returnOffset.IsEnabled = true; Notify(); };
            _withReturn.Unchecked += (s, e) => { _returnOffset.IsEnabled = false; Notify(); };
            panel.Children.Add(_withReturn);

            panel.Children.Add(Labeled("Distanza mandata/ritorno (mm):", _returnOffset, 70));
            _returnOffset.ToolTip = "Distanza tra l'asse del collettore di mandata e quello di ritorno.";

            _headerDn.TextChanged += (s, e) => Notify();
            _spacing.TextChanged += (s, e) => Notify();
            _autoSpacing.Checked += (s, e) => Notify();
            _autoSpacing.Unchecked += (s, e) => Notify();
            _circuitLength.TextChanged += (s, e) => Notify();
            _returnOffset.TextChanged += (s, e) => Notify();
            _circuitDirection.SelectionChanged += (s, e) => Notify();

            return panel;
        }

        // --------------------------------------------------------------- valvole

        /// <summary>
        /// Valvole in linea sugli stacchi. La regola è una sola soglia di DN, modificabile:
        /// fino a quel DN (compreso) si usa la valvola a sfera, oltre la boax. Le famiglie sono
        /// quelle caricate nel progetto, proposte per nome ma sempre scegliibili dall'utente.
        /// </summary>
        private UIElement BuildValves()
        {
            var section = new StackPanel();

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            _withValves.Content = "Valvole sugli stacchi";
            _withValves.IsChecked = true;
            _withValves.FontWeight = FontWeights.SemiBold;
            _withValves.VerticalAlignment = VerticalAlignment.Center;
            _withValves.ToolTip = "Inserisce una valvola in linea su ogni stacco, di mandata e di ritorno.";
            _withValves.Checked += (s, e) => { UpdateValvesEnabled(); Notify(); };
            _withValves.Unchecked += (s, e) => { UpdateValvesEnabled(); Notify(); };
            head.Children.Add(_withValves);
            section.Children.Add(head);

            var panel = new WrapPanel();

            panel.Children.Add(Labeled("Sfera fino a DN:", _ballMaxDn, 60));
            _ballMaxDn.Text = "32";
            _ballMaxDn.ToolTip = "Fino a questo DN compreso si usa la valvola a sfera; oltre si usa la boax.\n" +
                                 "Predefinito 32: sfera fino a DN32, boax da DN40 in su.";

            FillFamilies(_ballFamily, BallHints);
            panel.Children.Add(Labeled("Famiglia sfera:", _ballFamily, 190));
            _ballFamily.ToolTip = "Famiglia di valvole a sfera caricata nel progetto (accessori per tubazioni).";

            FillFamilies(_butterflyFamily, ButterflyHints);
            panel.Children.Add(Labeled("Famiglia boax:", _butterflyFamily, 190));
            _butterflyFamily.ToolTip = "Famiglia di valvole boax caricata nel progetto (accessori per tubazioni).";

            panel.Children.Add(Labeled("PN:", _valvePn, 110));
            _valvePn.ToolTip = "Pressione nominale preferita quando i nomi dei tipi la dichiarano (es. DN40_PN16).";

            panel.Children.Add(Labeled("Distanza dal collettore (mm):", _valveDistance, 60));
            _valveDistance.Text = "150";
            _valveDistance.ToolTip = "Distanza dall'asse del collettore al centro della valvola, lungo lo stacco.";

            foreach (var a in RollAngles) _butterflyRoll.Items.Add(a + "°");
            _butterflyRoll.SelectedItem = "90°";
            panel.Children.Add(Labeled("Rotazione boax:", _butterflyRoll, 70));
            _butterflyRoll.ToolTip = "Rotazione della boax (e delle sue flange) attorno all'asse del tubo.\n" +
                                     "Se la leva esce dal verso sbagliato, cambia qui senza ricompilare.";

            _ballMaxDn.TextChanged += (s, e) => Notify();
            _valveDistance.TextChanged += (s, e) => Notify();
            _butterflyRoll.SelectionChanged += (s, e) => Notify();
            _ballFamily.SelectionChanged += (s, e) => { FillPressureClasses(); Notify(); };
            _butterflyFamily.SelectionChanged += (s, e) => { FillPressureClasses(); Notify(); };
            _valvePn.SelectionChanged += (s, e) => Notify();

            section.Children.Add(panel);

            if (_catalog.PipeAccessories.Count == 0)
            {
                _withValves.IsChecked = false;
                _withValves.IsEnabled = false;
                _withValves.ToolTip = "Questo progetto non contiene famiglie di accessori per tubazioni: carica le valvole per usarle.";
            }
            FillPressureClasses();
            UpdateValvesEnabled();
            return section;
        }

        /// <summary>Riempie una tendina con le famiglie di accessori, proponendo quella suggerita dal nome.</summary>
        private void FillFamilies(ComboBox combo, string[] hints)
        {
            combo.Items.Add(NoFamily);
            foreach (var f in _catalog.PipeAccessories) combo.Items.Add(f.Name);
            combo.SelectedIndex = 0;

            foreach (var f in _catalog.PipeAccessories)
            {
                var name = TextUtil.Fold(f.Name);
                if (!hints.Any(h => name.Contains(h))) continue;
                combo.SelectedItem = f.Name;
                break;
            }
        }

        /// <summary>PN offerti: quelli dichiarati nei nomi dei tipi delle famiglie scelte.</summary>
        private void FillPressureClasses()
        {
            var previous = _valvePn.SelectedItem as string;
            var names = FamilyTypes(_ballFamily).Concat(FamilyTypes(_butterflyFamily));
            var available = ValveTypeMatcher.AvailablePn(names);

            var suspended = _suspendChanged;
            _suspendChanged = true;
            try
            {
                _valvePn.Items.Clear();
                _valvePn.Items.Add(AnyPn);
                foreach (var pn in available) _valvePn.Items.Add("PN" + MepSize.Fmt(pn));
                _valvePn.SelectedIndex = 0;
                if (previous != null && _valvePn.Items.Contains(previous)) _valvePn.SelectedItem = previous;
                else if (_valvePn.Items.Contains("PN16")) _valvePn.SelectedItem = "PN16";
                _valvePn.IsEnabled = _withValves.IsChecked == true && available.Count > 0;
            }
            finally
            {
                _suspendChanged = suspended;
            }
        }

        /// <summary>Nomi dei tipi della famiglia scelta nella tendina; vuoto con "(nessuna)".</summary>
        private List<string> FamilyTypes(ComboBox combo)
        {
            var name = SelectedFamily(combo);
            if (name == null) return new List<string>();
            var family = _catalog.PipeAccessories.FirstOrDefault(f => f.Name == name);
            return family == null ? new List<string>() : family.TypeNames;
        }

        /// <summary>Famiglia scelta nella tendina; null con "(nessuna)".</summary>
        private static string SelectedFamily(ComboBox combo)
        {
            var name = combo.SelectedItem as string;
            return string.IsNullOrWhiteSpace(name) || name == NoFamily ? null : name;
        }

        private void UpdateValvesEnabled()
        {
            var on = _withValves.IsChecked == true;
            _ballMaxDn.IsEnabled = on;
            _butterflyRoll.IsEnabled = on;
            _ballFamily.IsEnabled = on;
            _butterflyFamily.IsEnabled = on;
            _valveDistance.IsEnabled = on;
            _valvePn.IsEnabled = on && _valvePn.Items.Count > 1;
        }

        private static UIElement Labeled(string label, FrameworkElement control, double width)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 6) };
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            control.Width = width;
            control.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(control);
            return panel;
        }

        // -------------------------------------------------------------- circuiti

        private sealed class CircuitRow
        {
            public FrameworkElement Container;
            public TextBox Dn;
        }

        /// <summary>Aggiunge una riga circuito; <paramref name="dnMm"/> a 0 lascia il campo vuoto.</summary>
        private void AddCircuit(double dnMm, bool focus)
        {
            var row = new CircuitRow();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

            var label = new TextBlock
            {
                Width = 34,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };
            panel.Children.Add(label);

            panel.Children.Add(new TextBlock { Text = "DN", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });

            row.Dn = new TextBox
            {
                Width = 70,
                VerticalAlignment = VerticalAlignment.Center,
                Text = dnMm > 0 ? dnMm.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                ToolTip = "Diametro nominale del circuito in mm. Invio = aggiungi un altro circuito."
            };
            row.Dn.TextChanged += (s, e) => { UpdateRowValidity(row); Notify(); };
            row.Dn.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    AddCircuit(0, true);
                }
            };
            panel.Children.Add(row.Dn);

            var remove = new Button
            {
                Content = "✕",
                Width = 24,
                Height = 22,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                ToolTip = "Rimuovi questo circuito"
            };
            remove.Click += (s, e) => RemoveCircuit(row);
            panel.Children.Add(remove);

            row.Container = panel;
            _circuits.Add(row);
            _rows.Children.Add(panel);
            RenumberRows();
            Notify();

            if (focus)
            {
                row.Dn.Focus();
                // la riga appena aggiunta può essere fuori dall'area visibile
                row.Container.BringIntoView();
            }
        }

        private void RemoveCircuit(CircuitRow row)
        {
            _circuits.Remove(row);
            _rows.Children.Remove(row.Container);
            if (_circuits.Count == 0) AddCircuit(0, true);
            RenumberRows();
            Notify();
        }

        private void RenumberRows()
        {
            for (var i = 0; i < _circuits.Count; i++)
            {
                var panel = _circuits[i].Container as StackPanel;
                var label = panel?.Children[0] as TextBlock;
                if (label != null) label.Text = "C" + (i + 1);
            }
            var valid = 0;
            foreach (var r in _circuits)
            {
                if (ParseDn(r.Dn.Text) > 0) valid++;
            }
            _count.Text = valid == _circuits.Count
                ? valid + (valid == 1 ? " circuito" : " circuiti")
                : valid + " su " + _circuits.Count + " con DN valido";
        }

        private static void UpdateRowValidity(CircuitRow row)
        {
            var text = row.Dn.Text;
            var bad = !string.IsNullOrWhiteSpace(text) && ParseDn(text) <= 0;
            row.Dn.BorderBrush = bad ? Brushes.Firebrick : SystemColors.ControlDarkBrush;
        }

        private static double ParseDn(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            double v;
            var cleaned = text.Trim().TrimStart('D', 'N', 'd', 'n').Replace(',', '.');
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 ? v : 0;
        }

        /// <summary>PN scelto nella tendina ("PN16" → 16); 0 con "(indifferente)".</summary>
        private double SelectedPn()
        {
            var text = _valvePn.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(text) || text == AnyPn) return 0;
            return ParseNumber(text.TrimStart('P', 'N', 'p', 'n'), 0);
        }

        private static double ParseNumber(string text, double fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            double v;
            return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                ? v
                : fallback;
        }

        private void Notify()
        {
            if (_suspendChanged) return;
            RenumberRows();
            var handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ----------------------------------------------------------------- piano

        /// <summary>Costruisce il collettore con i valori correnti dei campi.</summary>
        public ManifoldPlan BuildPlan()
        {
            var plan = new ManifoldPlan
            {
                SpacingMm = ParseNumber(_spacing.Text, 150),
                AutoSpacing = _autoSpacing.IsChecked == true,
                CircuitLengthMm = ParseNumber(_circuitLength.Text, 500),
                HeaderDirection = DirectionKind.PlusX, // direzione fissa: +X (est)
                CircuitDirection = CircuitDirections[Math.Max(_circuitDirection.SelectedIndex, 0)].Value,
                PipeTypeName = _pipeType.SelectedItem as string,
                WithReturn = _withReturn.IsChecked == true,
                ReturnOffsetMm = ParseNumber(_returnOffset.Text, 300),
                WithValves = _withValves.IsChecked == true,
                BallValveMaxDnMm = ParseNumber(_ballMaxDn.Text, 32),
                BallValveFamily = SelectedFamily(_ballFamily),
                ButterflyValveFamily = SelectedFamily(_butterflyFamily),
                ValvePnBar = SelectedPn(),
                ValveDistanceMm = ParseNumber(_valveDistance.Text, 150),
                ButterflyRollDegrees = ParseNumber((_butterflyRoll.SelectedItem as string ?? "90").TrimEnd('°'), 90)
            };
            plan.BallValveTypes.AddRange(FamilyTypes(_ballFamily));
            plan.ButterflyValveTypes.AddRange(FamilyTypes(_butterflyFamily));
            var type = SelectedType;
            if (type != null) plan.HeaderSizeCandidates.AddRange(type.Sizes);
            if (_autoHeaderDn.IsChecked != true)
            {
                var dn = ParseDn(_headerDn.Text);
                if (dn > 0) plan.HeaderDnMm = dn;
            }
            foreach (var row in _circuits)
            {
                var dn = ParseDn(row.Dn.Text);
                if (dn > 0) plan.Circuits.Add(new ManifoldCircuit(dn));
            }
            return plan;
        }

        // --------------------------------------------------------- impostazioni

        public void LoadSettings(Settings settings)
        {
            _suspendChanged = true;
            try
            {
                _autoHeaderDn.IsChecked = settings.ManifoldHeaderDnMm <= 0;
                _headerDn.IsEnabled = _autoHeaderDn.IsChecked != true;
                _headerDn.Text = settings.ManifoldHeaderDnMm > 0
                    ? settings.ManifoldHeaderDnMm.ToString("0.##", CultureInfo.InvariantCulture)
                    : string.Empty;
                _spacing.Text = settings.ManifoldSpacingMm.ToString("0.##", CultureInfo.InvariantCulture);
                _autoSpacing.IsChecked = settings.ManifoldAutoSpacing;
                _circuitLength.Text = settings.ManifoldCircuitLengthMm.ToString("0.##", CultureInfo.InvariantCulture);
                _circuitDirection.SelectedIndex = Math.Max(0, IndexOf(CircuitDirections, settings.ManifoldCircuitDirection));

                _withReturn.IsChecked = settings.ManifoldWithReturn;
                _returnOffset.IsEnabled = settings.ManifoldWithReturn;
                _returnOffset.Text = settings.ManifoldReturnOffsetMm.ToString("0.##", CultureInfo.InvariantCulture);

                var storedType = _pipeType.Items.IndexOf(settings.ManifoldPipeTypeName);
                if (storedType >= 0) _pipeType.SelectedIndex = storedType;
                UpdatePipeTypeTooltip();

                if (_withValves.IsEnabled) _withValves.IsChecked = settings.ManifoldWithValves;
                _ballMaxDn.Text = settings.ManifoldBallValveMaxDnMm.ToString("0.##", CultureInfo.InvariantCulture);
                _valveDistance.Text = settings.ManifoldValveDistanceMm.ToString("0.##", CultureInfo.InvariantCulture);
                var storedRoll = MepSize.Fmt(settings.ManifoldButterflyRollDeg) + "°";
                if (_butterflyRoll.Items.Contains(storedRoll)) _butterflyRoll.SelectedItem = storedRoll;
                // Le famiglie salvate valgono solo se sono caricate anche in QUESTO progetto:
                // altrimenti resta la proposta fatta sul nome all'apertura.
                SelectFamily(_ballFamily, settings.ManifoldBallValveFamily);
                SelectFamily(_butterflyFamily, settings.ManifoldButterflyValveFamily);
                FillPressureClasses();
                if (settings.ManifoldValvePnBar > 0)
                {
                    var pn = "PN" + MepSize.Fmt(settings.ManifoldValvePnBar);
                    if (_valvePn.Items.Contains(pn)) _valvePn.SelectedItem = pn;
                }
                else
                {
                    _valvePn.SelectedIndex = 0;
                }
                UpdateValvesEnabled();

                var stored = new ManifoldPlan();
                stored.LoadCircuitsFromString(settings.ManifoldCircuits);
                foreach (var c in stored.Circuits) AddCircuit(c.DnMm, false);
            }
            finally
            {
                _suspendChanged = false;
            }
            if (_circuits.Count == 0) AddCircuit(0, false);
            RenumberRows();
        }

        public void StoreSettings(Settings settings)
        {
            var plan = BuildPlan();
            settings.ManifoldHeaderDnMm = plan.HeaderDnMm ?? 0;
            settings.ManifoldSpacingMm = plan.SpacingMm;
            settings.ManifoldAutoSpacing = plan.AutoSpacing;
            settings.ManifoldCircuitLengthMm = plan.CircuitLengthMm;
            settings.ManifoldCircuitDirection = plan.CircuitDirection.ToString();
            settings.ManifoldCircuits = plan.CircuitsToString();
            settings.ManifoldPipeTypeName = plan.PipeTypeName ?? string.Empty;
            settings.ManifoldWithReturn = plan.WithReturn;
            settings.ManifoldReturnOffsetMm = plan.ReturnOffsetMm;
            settings.ManifoldWithValves = plan.WithValves;
            settings.ManifoldBallValveMaxDnMm = plan.BallValveMaxDnMm;
            // "(nessuna)" viene salvato com'è: è una scelta dell'utente, non un valore mancante.
            settings.ManifoldBallValveFamily = plan.BallValveFamily ?? NoFamily;
            settings.ManifoldButterflyValveFamily = plan.ButterflyValveFamily ?? NoFamily;
            settings.ManifoldValvePnBar = plan.ValvePnBar;
            settings.ManifoldValveDistanceMm = plan.ValveDistanceMm;
            settings.ManifoldButterflyRollDeg = plan.ButterflyRollDegrees;
        }

        /// <summary>Sceglie la famiglia salvata, se è caricata anche in questo progetto.</summary>
        private static void SelectFamily(ComboBox combo, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (combo.Items.Contains(name)) combo.SelectedItem = name;
        }

        private static int IndexOf(KeyValuePair<string, DirectionKind>[] options, string name)
        {
            DirectionKind kind;
            if (!Enum.TryParse(name, out kind)) return -1;
            for (var i = 0; i < options.Length; i++)
            {
                if (options[i].Value == kind) return i;
            }
            return -1;
        }

        /// <summary>Porta il focus sul primo campo DN ancora vuoto (o sull'ultimo).</summary>
        public void FocusFirstEmpty()
        {
            foreach (var row in _circuits)
            {
                if (ParseDn(row.Dn.Text) <= 0)
                {
                    row.Dn.Focus();
                    return;
                }
            }
            if (_circuits.Count > 0) _circuits[_circuits.Count - 1].Dn.Focus();
        }
    }
}
