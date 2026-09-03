using System;
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
        private readonly ModelCatalog _catalog;
        private readonly Settings _settings;

        private readonly TextBox _input = new TextBox();
        private readonly ComboBox _parserMode = new ComboBox();
        private readonly ComboBox _level = new ComboBox();
        private readonly TextBox _elevation = new TextBox();
        private readonly ComboBox _startMode = new ComboBox();
        private readonly CheckBox _usePickedZ = new CheckBox();
        private readonly TextBox _claudeModel = new TextBox();
        private readonly TextBox _preview = new TextBox();
        private readonly TextBlock _status = new TextBlock();
        private readonly Button _interpret = new Button();
        private readonly Button _create = new Button();

        private CancellationTokenSource _cts;
        private string _previewedText;

        /// <summary>Risultato dell'interpretazione confermato con "Crea".</summary>
        public ParseResult Result { get; private set; }

        public string SelectedLevel => _level.SelectedItem as string;

        public MainWindow(ModelCatalog catalog, Settings settings)
        {
            _catalog = catalog;
            _settings = settings;

            Title = "sayRevit – tubazioni e canali da testo";
            Width = 760;
            Height = 640;
            MinWidth = 600;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontSize = 13;

            Content = BuildLayout();
            LoadSettings();
            Loaded += (s, e) => { _input.Focus(); _input.SelectAll(); };
        }

        // ------------------------------------------------------------------ UI

        private UIElement BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(110) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Descrivi cosa creare (solo tubazioni e canali):",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(title, 0);
            root.Children.Add(title);

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
                if (_previewedText != null && _input.Text != _previewedText)
                {
                    _create.IsEnabled = false;
                    SetStatus("Testo modificato: premi \"Interpreta\" per aggiornare l'anteprima.", false);
                }
            };
            Grid.SetRow(_input, 1);
            root.Children.Add(_input);

            var examples = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 8),
                Text = "Esempi: \"una tubazione DN200 con degli stacchi DN15\" · \"tubo in acciaio DN80 acqua fredda lungo 6 m con 4 stacchi DN20 ogni 1,5 m verso l'alto\" · " +
                       "\"canale 400x200 aria di mandata lungo 8 m con 2 stacchi 200x200 laterali\" · \"tubazione DN65 lunga 5 m; poi verso l'alto per 2 m\""
            };
            Grid.SetRow(examples, 2);
            root.Children.Add(examples);

            var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            options.Children.Add(Labeled("Interprete:", _parserMode, 150));
            _parserMode.Items.Add("Regole (offline)");
            _parserMode.Items.Add("Claude (AI)");
            _parserMode.SelectionChanged += (s, e) => _claudeModel.IsEnabled = _parserMode.SelectedIndex == 1;

            options.Children.Add(Labeled("Modello Claude:", _claudeModel, 150));
            _claudeModel.ToolTip = "Richiede la variabile d'ambiente ANTHROPIC_API_KEY (oppure l'accesso configurato con l'SDK Anthropic).";

            options.Children.Add(Labeled("Livello:", _level, 160));
            foreach (var l in _catalog.Levels) _level.Items.Add(l);
            if (_catalog.ActiveLevel != null && _level.Items.Contains(_catalog.ActiveLevel)) _level.SelectedItem = _catalog.ActiveLevel;
            else if (_level.Items.Count > 0) _level.SelectedIndex = 0;
            _level.ToolTip = "Livello usato se il testo non ne indica uno.";

            options.Children.Add(Labeled("Quota predefinita (mm):", _elevation, 80));
            _elevation.ToolTip = "Quota rispetto al livello quando il testo non la indica.";

            options.Children.Add(Labeled("Punto iniziale:", _startMode, 190));
            _startMode.Items.Add("Origine del progetto");
            _startMode.Items.Add("Scegli nel modello (dopo Crea)");
            _startMode.SelectionChanged += (s, e) => _usePickedZ.IsEnabled = _startMode.SelectedIndex == 1;

            _usePickedZ.Content = "Usa la Z del punto scelto";
            _usePickedZ.VerticalAlignment = VerticalAlignment.Center;
            _usePickedZ.Margin = new Thickness(0, 6, 12, 0);
            options.Children.Add(_usePickedZ);

            Grid.SetRow(options, 3);
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
            _create.Click += (s, e) => Confirm();
            buttons.Children.Add(_create);

            var close = new Button { Content = "Chiudi", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
            buttons.Children.Add(close);

            var catalogInfo = new TextBlock
            {
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
                Text = "Nel progetto: " + _catalog.PipeTypes.Count + " tipi tubazione, " + _catalog.DuctTypes.Count + " tipi canale, " +
                       _catalog.PipingSystems.Count + "+" + _catalog.DuctSystems.Count + " sistemi, " + _catalog.Levels.Count + " livelli"
            };
            buttons.Children.Add(catalogInfo);
            Grid.SetRow(buttons, 4);
            root.Children.Add(buttons);

            _preview.IsReadOnly = true;
            _preview.TextWrapping = TextWrapping.Wrap;
            _preview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _preview.FontFamily = new FontFamily("Consolas");
            _preview.Padding = new Thickness(6);
            _preview.Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));
            _preview.Text = "L'anteprima di ciò che verrà creato comparirà qui.";
            Grid.SetRow(_preview, 5);
            root.Children.Add(_preview);

            _status.Margin = new Thickness(0, 6, 0, 0);
            _status.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(_status, 6);
            root.Children.Add(_status);

            return root;
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
            _elevation.Text = _settings.DefaultElevationMm.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _startMode.SelectedIndex = _settings.StartMode == "pick" ? 1 : 0;
            _usePickedZ.IsChecked = _settings.UsePickedZ;
            _usePickedZ.IsEnabled = _startMode.SelectedIndex == 1;
            _input.Text = string.IsNullOrWhiteSpace(_settings.LastText) ? "una tubazione DN200 lunga 10 m con 3 stacchi DN15" : _settings.LastText;
        }

        private void StoreSettings()
        {
            _settings.ParserMode = _parserMode.SelectedIndex == 1 ? "claude" : "rules";
            _settings.ClaudeModel = string.IsNullOrWhiteSpace(_claudeModel.Text) ? "claude-opus-5" : _claudeModel.Text.Trim();
            if (double.TryParse(_elevation.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q))
                _settings.DefaultElevationMm = q;
            _settings.StartMode = _startMode.SelectedIndex == 1 ? "pick" : "origin";
            _settings.UsePickedZ = _usePickedZ.IsChecked == true;
            _settings.LastText = _input.Text;
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
            if (Result == null || !Result.Success || _previewedText != _input.Text)
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
