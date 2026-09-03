using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SayRevit.Core.Model;

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

        private readonly ModelCatalog _catalog;
        private readonly ComboBox _pipeType = new ComboBox();
        private readonly TextBlock _typeInfo = new TextBlock();
        private readonly StackPanel _rows = new StackPanel();
        private readonly List<CircuitRow> _circuits = new List<CircuitRow>();
        private readonly TextBlock _count = new TextBlock();

        private readonly CheckBox _autoHeaderDn = new CheckBox();
        private readonly TextBox _headerDn = new TextBox();
        private readonly TextBox _spacing = new TextBox();
        private readonly TextBox _circuitLength = new TextBox();
        private readonly ComboBox _circuitDirection = new ComboBox();
        private readonly CheckBox _withReturn = new CheckBox();
        private readonly TextBox _returnOffset = new TextBox();

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

            PlaceRow(BuildMaterialRow(), 0);
            PlaceRow(BuildCircuitHeader(), 1);
            PlaceRow(BuildCircuitList(), 2);
            PlaceRow(BuildParameters(), 3);
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
            _circuitLength.TextChanged += (s, e) => Notify();
            _returnOffset.TextChanged += (s, e) => Notify();
            _circuitDirection.SelectionChanged += (s, e) => Notify();

            return panel;
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
                CircuitLengthMm = ParseNumber(_circuitLength.Text, 500),
                HeaderDirection = DirectionKind.PlusX, // direzione fissa: +X (est)
                CircuitDirection = CircuitDirections[Math.Max(_circuitDirection.SelectedIndex, 0)].Value,
                PipeTypeName = _pipeType.SelectedItem as string,
                WithReturn = _withReturn.IsChecked == true,
                ReturnOffsetMm = ParseNumber(_returnOffset.Text, 300)
            };
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
                _circuitLength.Text = settings.ManifoldCircuitLengthMm.ToString("0.##", CultureInfo.InvariantCulture);
                _circuitDirection.SelectedIndex = Math.Max(0, IndexOf(CircuitDirections, settings.ManifoldCircuitDirection));

                _withReturn.IsChecked = settings.ManifoldWithReturn;
                _returnOffset.IsEnabled = settings.ManifoldWithReturn;
                _returnOffset.Text = settings.ManifoldReturnOffsetMm.ToString("0.##", CultureInfo.InvariantCulture);

                var storedType = _pipeType.Items.IndexOf(settings.ManifoldPipeTypeName);
                if (storedType >= 0) _pipeType.SelectedIndex = storedType;
                UpdatePipeTypeTooltip();

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
            settings.ManifoldCircuitLengthMm = plan.CircuitLengthMm;
            settings.ManifoldCircuitDirection = plan.CircuitDirection.ToString();
            settings.ManifoldCircuits = plan.CircuitsToString();
            settings.ManifoldPipeTypeName = plan.PipeTypeName ?? string.Empty;
            settings.ManifoldWithReturn = plan.WithReturn;
            settings.ManifoldReturnOffsetMm = plan.ReturnOffsetMm;
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
