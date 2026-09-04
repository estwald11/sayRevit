using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SayRevit.Core.Model;
using SayRevit.Core.Parsing;

namespace SayRevit.Addin.UI
{
    /// <summary>Finestra principale (WPF costruita da codice, nessun XAML): testo → anteprima → creazione.</summary>
    public sealed class MainWindow : Window
    {
        private const string AutoPipeType = "Automatico (dal testo o predefinito del progetto)";

        private readonly ModelCatalog _catalog;
        private readonly Settings _settings;

        private readonly TextBox _input = new TextBox();
        private readonly CheckBox _manifoldFlag = new CheckBox();
        private readonly ManifoldPanel _manifoldPanel;
        private readonly StackPanel _textPanel = new StackPanel();
        private readonly TextBlock _title = new TextBlock();
        private readonly ComboBox _parserMode = new ComboBox();
        private readonly ComboBox _pipeType = new ComboBox();
        private readonly TextBox _claudeModel = new TextBox();
        private readonly TextBox _preview = new TextBox();
        private readonly Expander _more = new Expander();
        private readonly TextBlock _status = new TextBlock();
        private const double CompactHeight = 430;
        private const double FullHeight = 760;
        private readonly Button _interpret = new Button();
        private readonly Button _create = new Button();

        private UIElement _parserOption;
        private UIElement _claudeOption;
        private UIElement _pipeTypeOption;
        private CancellationTokenSource _cts;
        private string _previewedText;

        /// <summary>True quando è attivo il flag "Collettore" (modalità parametrica, senza testo).</summary>
        public bool ManifoldMode => _manifoldFlag.IsChecked == true;

        /// <summary>Risultato dell'interpretazione confermato con "Crea".</summary>
        public ParseResult Result { get; private set; }

        /// <summary>Il collettore parametrico dell'ultima anteprima (per l'interasse automatico, risolto in Revit).</summary>
        public ManifoldPlan ManifoldPlan { get; private set; }

        /// <summary>
        /// Tipo di tubazione scelto per la modalità testuale; null con "Automatico".
        /// La sezione collettore ha una propria scelta, deterministica, che ha la precedenza.
        /// </summary>
        public string SelectedPipeType => _pipeType.SelectedIndex <= 0 ? null : _pipeType.SelectedItem as string;

        public MainWindow(ModelCatalog catalog, Settings settings)
        {
            _catalog = catalog;
            _settings = settings;
            _manifoldPanel = new ManifoldPanel(catalog);

            Title = "sayRevit – tubazioni e canali da testo";
            Width = 760;
            Height = FullHeight;
            MinWidth = 600;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontSize = 13;

            Content = BuildLayout();
            LoadSettings();
            // Esc chiude senza creare (il pulsante "Chiudi" non c'è più)
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    DialogResult = false;
                    Close();
                }
            };
            Loaded += (s, e) =>
            {
                if (ManifoldMode)
                {
                    _manifoldPanel.FocusFirstEmpty();
                }
                else
                {
                    _input.Focus();
                    _input.SelectAll();
                }
            };
        }

        // ------------------------------------------------------------------ UI

        private UIElement BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 intestazione + flag
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 1 contenuto della modalità
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 opzioni comuni
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 3 pulsanti
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 4 anteprima
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 5 stato

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };

            _manifoldFlag.Content = "Collettore";
            _manifoldFlag.FontWeight = FontWeights.SemiBold;
            _manifoldFlag.VerticalAlignment = VerticalAlignment.Center;
            _manifoldFlag.ToolTip = "Modalità parametrica: niente testo, si inseriscono i circuiti indicandone il DN.";
            _manifoldFlag.Checked += (s, e) => ApplyMode();
            _manifoldFlag.Unchecked += (s, e) => ApplyMode();
            DockPanel.SetDock(_manifoldFlag, Dock.Right);
            header.Children.Add(_manifoldFlag);

            _title.Text = "Descrivi cosa creare (solo tubazioni e canali):";
            _title.FontWeight = FontWeights.SemiBold;
            _title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(_title);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var modeContent = new Grid();
            Grid.SetRow(modeContent, 1);
            root.Children.Add(modeContent);

            _manifoldPanel.Changed += (s, e) => UpdateManifoldPreview();
            _manifoldPanel.Visibility = Visibility.Collapsed;
            modeContent.Children.Add(_manifoldPanel);

            modeContent.Children.Add(_textPanel);

            _input.Height = 110;
            _input.AcceptsReturn = true;
            _input.TextWrapping = TextWrapping.Wrap;
            _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _input.Padding = new Thickness(6);
            _input.ToolTip = "Es.: una tubazione DN200 lunga 10 m con 3 stacchi DN15 ogni 2 m verso l'alto\nCtrl+Invio = Interpreta";
            _input.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    e.Handled = true;
                    _ = InterpretAsync();
                }
            };
            _input.TextChanged += (s, e) =>
            {
                if (!ManifoldMode && _previewedText != null && _input.Text != _previewedText)
                {
                    _create.IsEnabled = false;
                    SetStatus("Testo modificato: premi \"Interpreta\" per aggiornare l'anteprima.", false);
                }
            };
            _textPanel.Children.Add(_input);

            var examples = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 8),
                Text = "Esempi: \"una tubazione DN200 con degli stacchi DN15\" · \"tubo in acciaio DN80 acqua fredda lungo 6 m con 4 stacchi DN20 ogni 1,5 m verso l'alto\" · " +
                       "\"canale 400x200 aria di mandata lungo 8 m con 2 stacchi 200x200 laterali\" · \"tubazione DN65 lunga 5 m; poi verso l'alto per 2 m\""
            };
            _textPanel.Children.Add(examples);

            var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _parserOption = Labeled("Interprete:", _parserMode, 150);
            options.Children.Add(_parserOption);
            _parserMode.Items.Add("Regole (offline)");
            _parserMode.Items.Add("Claude (AI)");
            _parserMode.SelectionChanged += (s, e) => _claudeModel.IsEnabled = _parserMode.SelectedIndex == 1;

            _claudeOption = Labeled("Modello Claude:", _claudeModel, 150);
            options.Children.Add(_claudeOption);
            _claudeModel.ToolTip = "Richiede la variabile d'ambiente ANTHROPIC_API_KEY (oppure l'accesso configurato con l'SDK Anthropic).";

            _pipeTypeOption = Labeled("Tipo tubazione:", _pipeType, 250);
            options.Children.Add(_pipeTypeOption);
            _pipeType.Items.Add(AutoPipeType);
            foreach (var t in _catalog.PipeTypes) _pipeType.Items.Add(t.Name);
            _pipeType.SelectedIndex = 0;
            _pipeType.SelectionChanged += (s, e) => UpdatePipeTypeTooltip();

            Grid.SetRow(options, 2);
            root.Children.Add(options);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            _interpret.Content = "Interpreta (anteprima)";
            _interpret.Padding = new Thickness(12, 5, 12, 5);
            _interpret.Margin = new Thickness(0, 0, 8, 0);
            _interpret.Click += async (s, e) => await InterpretAsync();
            buttons.Children.Add(_interpret);

            _create.Content = "Crea in Revit";
            _create.Padding = new Thickness(12, 5, 12, 5);
            _create.Margin = new Thickness(0, 0, 8, 0);
            _create.IsEnabled = false;
            _create.FontWeight = FontWeights.SemiBold;
            _create.ToolTip = "Crea gli elementi in Revit a partire dall'origine del progetto\n" +
                              "(oppure da un punto scelto nel modello: vedi \"Punto iniziale\" in \"Mostra di più\").";
            _create.Click += (s, e) => Confirm();
            buttons.Children.Add(_create);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            // In modalità Collettore tutto ciò che segue i DN e il materiale (parametri, punto
            // iniziale, valvole e la stessa anteprima) sta in "Mostra di più", chiuso all'apertura.
            var bottom = new Grid();
            bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bottom.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);

            _more.Header = "Mostra di più";
            _more.IsExpanded = false;
            _more.Margin = new Thickness(0, 0, 0, 6);
            // la sezione è lunga: scorre da sola, così l'anteprima e la riga di stato restano visibili
            _more.Content = new ScrollViewer
            {
                Content = _manifoldPanel.MoreSection,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 340
            };
            _more.Expanded += (s, e) => ApplyMoreState();
            _more.Collapsed += (s, e) => ApplyMoreState();
            Grid.SetRow(_more, 0);
            bottom.Children.Add(_more);

            _preview.IsReadOnly = true;
            _preview.TextWrapping = TextWrapping.Wrap;
            _preview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _preview.FontFamily = new FontFamily("Consolas");
            _preview.Padding = new Thickness(6);
            _preview.Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));
            _preview.Text = "L'anteprima di ciò che verrà creato comparirà qui.";
            Grid.SetRow(_preview, 1);
            bottom.Children.Add(_preview);

            _status.Margin = new Thickness(0, 6, 0, 0);
            _status.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(_status, 5);
            root.Children.Add(_status);

            return root;
        }

        /// <summary>Mostra i controlli della modalità attiva (testuale o collettore parametrico).</summary>
        private void ApplyMode()
        {
            var manifold = ManifoldMode;
            _textPanel.Visibility = manifold ? Visibility.Collapsed : Visibility.Visible;
            _manifoldPanel.Visibility = manifold ? Visibility.Visible : Visibility.Collapsed;
            _interpret.Visibility = manifold ? Visibility.Collapsed : Visibility.Visible;
            if (_parserOption != null) _parserOption.Visibility = manifold ? Visibility.Collapsed : Visibility.Visible;
            if (_claudeOption != null) _claudeOption.Visibility = manifold ? Visibility.Collapsed : Visibility.Visible;
            if (_pipeTypeOption != null) _pipeTypeOption.Visibility = manifold ? Visibility.Collapsed : Visibility.Visible;

            _title.Text = manifold
                ? "Collettore parametrico: aggiungi i circuiti e indica il DN di ciascuno."
                : "Descrivi cosa creare (solo tubazioni e canali):";
            Title = manifold ? "sayRevit – collettore parametrico" : "sayRevit – tubazioni e canali da testo";
            _create.Content = manifold ? "Crea collettore" : "Crea in Revit";
            _more.Visibility = manifold ? Visibility.Visible : Visibility.Collapsed;
            ApplyMoreState();

            if (manifold)
            {
                UpdateManifoldPreview();
            }
            else
            {
                Result = null;
                _previewedText = null;
                _create.IsEnabled = false;
                _preview.Text = "L'anteprima di ciò che verrà creato comparirà qui.";
                SetStatus(string.Empty, false);
            }
        }

        /// <summary>
        /// "Mostra di più" chiuso: restano DN, materiale e il pulsante, e la finestra si stringe;
        /// aperto: parametri, punto iniziale, valvole e anteprima, con la finestra alta.
        /// In modalità testuale l'anteprima è sempre visibile.
        /// </summary>
        private void ApplyMoreState()
        {
            var manifold = ManifoldMode;
            var open = !manifold || _more.IsExpanded;
            _more.Header = _more.IsExpanded ? "Mostra di meno" : "Mostra di più";
            _preview.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (!IsLoaded || WindowState != WindowState.Normal)
            {
                Height = open ? FullHeight : CompactHeight;
                return;
            }
            if (open && Height < FullHeight) Height = FullHeight;
            if (!open) Height = CompactHeight;
        }

        private void UpdatePipeTypeTooltip()
        {
            var name = SelectedPipeType;
            var type = name == null ? null : _catalog.PipeTypes.FirstOrDefault(t => t.Name == name);
            if (type == null)
            {
                _pipeType.ToolTip = "Tipo usato quando la descrizione non ne indica uno. " +
                                    "\"Automatico\" lascia decidere al testo e, in mancanza, al tipo predefinito del progetto.";
                return;
            }
            var sizes = type.AvailableDiametersMm.Count > 0
                ? "misure disponibili DN " + string.Join(", ", type.AvailableDiametersMm.Select(MepSize.Fmt))
                : "nessuna misura leggibile dalle preferenze di instradamento";
            _pipeType.ToolTip = "Tipo \"" + type.Name + "\": " + sizes +
                                (type.HasTees ? " · raccordi a T configurati" : " · nessun raccordo a T configurato");
        }

        /// <summary>Ricalcola il collettore a ogni modifica dei campi: l'anteprima è sempre allineata.</summary>
        private void UpdateManifoldPreview()
        {
            if (!ManifoldMode) return;
            var plan = _manifoldPanel.BuildPlan();
            ManifoldPlan = plan;
            var result = plan.ToParseResult();
            Result = result;

            var typeInfo = _manifoldPanel.DescribeSelectedType();
            if (typeInfo != null) result.Notes.Add(typeInfo);

            if (result.Success)
            {
                _preview.Text = plan.Summary() + Environment.NewLine + PlanFormatter.Describe(result);
                _create.IsEnabled = true;
                // con l'anteprima nascosta gli avvisi vanno almeno contati qui
                var warnings = result.Warnings.Count;
                SetStatus(warnings == 0
                        ? "Pronto: premi \"Crea collettore\"."
                        : "Pronto, con " + warnings + (warnings == 1 ? " avviso" : " avvisi") + ": vedi \"Mostra di più\" prima di creare.",
                    false);
            }
            else
            {
                _preview.Text = plan.Summary();
                _create.IsEnabled = false;
                SetStatus(result.Error, true);
            }
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

        // ------------------------------------------------------------ settings

        private void LoadSettings()
        {
            _parserMode.SelectedIndex = _settings.ParserMode == "claude" ? 1 : 0;
            _claudeModel.Text = _settings.ClaudeModel;
            _claudeModel.IsEnabled = _parserMode.SelectedIndex == 1;
            _input.Text = string.IsNullOrWhiteSpace(_settings.LastText) ? "una tubazione DN200 lunga 10 m con 3 stacchi DN15" : _settings.LastText;

            var storedType = _pipeType.Items.IndexOf(_settings.PipeTypeName);
            _pipeType.SelectedIndex = storedType > 0 ? storedType : 0;
            UpdatePipeTypeTooltip();

            _manifoldPanel.LoadSettings(_settings);
            _manifoldFlag.IsChecked = _settings.ManifoldMode;
            ApplyMode(); // anche quando il flag non cambia stato rispetto al valore iniziale
        }

        private void StoreSettings()
        {
            _settings.ParserMode = _parserMode.SelectedIndex == 1 ? "claude" : "rules";
            _settings.ClaudeModel = string.IsNullOrWhiteSpace(_claudeModel.Text) ? "claude-opus-5" : _claudeModel.Text.Trim();
            _settings.LastText = _input.Text;
            _settings.PipeTypeName = SelectedPipeType ?? string.Empty;
            _settings.ManifoldMode = ManifoldMode;
            _manifoldPanel.StoreSettings(_settings);
        }

        // -------------------------------------------------------------- actions

        private IIntentParser CreateParser()
        {
            if (_parserMode.SelectedIndex == 1)
            {
                var model = string.IsNullOrWhiteSpace(_claudeModel.Text) ? "claude-opus-5" : _claudeModel.Text.Trim();
                return ClaudeParserFactory.Create(model);
            }
            return new RuleBasedParser();
        }

        private async Task<bool> InterpretAsync()
        {
            var text = _input.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("Scrivi una descrizione.", true);
                return false;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            IIntentParser parser;
            try
            {
                parser = CreateParser();
            }
            catch (Exception ex)
            {
                SetStatus("Interprete non disponibile: " + ex.Message, true);
                return false;
            }

            SetBusy(true);
            SetStatus("Interpretazione con " + parser.Name + "…", false);
            try
            {
                var result = await parser.ParseAsync(text, _catalog, token);
                if (token.IsCancellationRequested) return false;
                Result = result;
                _preview.Text = PlanFormatter.Describe(result);
                _previewedText = text;
                _create.IsEnabled = result.Success;
                SetStatus(result.Success
                        ? "Anteprima pronta. Controlla e premi \"Crea in Revit\"."
                        : "Non interpretabile: " + result.Error,
                    !result.Success);
                return result.Success;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _preview.Text = "ERRORE: " + ex.Message;
                _create.IsEnabled = false;
                SetStatus("Errore: " + ex.Message, true);
                return false;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void Confirm()
        {
            if (ManifoldMode)
            {
                UpdateManifoldPreview();
                if (Result == null || !Result.Success) return;
            }
            else if (Result == null || !Result.Success || _previewedText != _input.Text)
            {
                var ok = await InterpretAsync();
                if (!ok) return;
            }
            StoreSettings();
            DialogResult = true;
            Close();
        }

        private void SetBusy(bool busy)
        {
            if (ManifoldMode) return; // la modalità parametrica non ha attese asincrone
            _interpret.IsEnabled = !busy;
            _create.IsEnabled = !busy && Result != null && Result.Success && _previewedText == _input.Text;
            _input.IsEnabled = !busy;
            Cursor = busy ? Cursors.Wait : null;
        }

        private void SetStatus(string text, bool isError)
        {
            _status.Text = text;
            _status.Foreground = isError ? Brushes.Firebrick : Brushes.DimGray;
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            StoreSettings();
            base.OnClosed(e);
        }
    }

    /// <summary>
    /// Crea il parser Claude in un metodo separato, così l'assembly SayRevit.Claude (e l'SDK Anthropic)
    /// viene caricato solo quando l'utente sceglie davvero la modalità AI.
    /// </summary>
    internal static class ClaudeParserFactory
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static IIntentParser Create(string model)
        {
            return new SayRevit.Claude.ClaudeIntentParser(new SayRevit.Claude.ClaudeOptions { Model = model });
        }
    }
}
