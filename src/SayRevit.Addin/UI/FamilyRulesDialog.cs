using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SayRevit.Core.Model;

namespace SayRevit.Addin.UI
{
    /// <summary>
    /// Pulsante "per DN…" accanto alla tendina di una famiglia: tiene le soglie "da DN x in su usa
    /// la famiglia y" dell'elemento e apre la finestra per modificarle. Senza soglie vale la sola
    /// famiglia della tendina per tutti i diametri.
    /// </summary>
    internal sealed class DnRulesButton : Button
    {
        private readonly string _what;
        private readonly IList<string> _families;
        private readonly string _noneLabel;
        private readonly Func<string> _defaultFamily;

        /// <summary>Soglie correnti; la famiglia è il nome com'è nella tendina (anche la voce "nessuna").</summary>
        public List<FamilyRule> Rules { get; } = new List<FamilyRule>();

        /// <summary>Sollevato quando l'utente conferma una modifica alle soglie.</summary>
        public event EventHandler RulesChanged;

        /// <param name="what">Nome dell'elemento, per il titolo della finestra ("valvola di zona").</param>
        /// <param name="families">Famiglie offerte, con la voce "nessuna" in testa.</param>
        /// <param name="noneLabel">Voce che significa "nessuna famiglia" (o "automatica").</param>
        /// <param name="defaultFamily">Famiglia di base al momento dell'apertura (per spiegare cosa vale sotto la prima soglia).</param>
        public DnRulesButton(string what, IList<string> families, string noneLabel, Func<string> defaultFamily)
        {
            _what = what;
            _families = families;
            _noneLabel = noneLabel;
            _defaultFamily = defaultFamily;
            Padding = new Thickness(6, 1, 6, 1);
            Margin = new Thickness(4, 0, 0, 0);
            VerticalAlignment = VerticalAlignment.Center;
            Click += (s, e) => Open();
            Refresh();
        }

        /// <summary>Sostituisce le soglie (caricamento impostazioni), senza sollevare l'evento.</summary>
        public void SetRules(IEnumerable<FamilyRule> rules)
        {
            Rules.Clear();
            if (rules != null) Rules.AddRange(rules.Where(r => r != null).OrderBy(r => r.FromDnMm));
            Refresh();
        }

        private void Open()
        {
            var dialog = new FamilyRulesDialog(_what, _defaultFamily(), _families, _noneLabel, Rules)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true) return;
            Rules.Clear();
            Rules.AddRange(dialog.Result);
            Refresh();
            var handler = RulesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void Refresh()
        {
            Content = Rules.Count == 0 ? "per DN…" : "per DN (" + Rules.Count.ToString(CultureInfo.InvariantCulture) + ")";
            FontWeight = Rules.Count == 0 ? FontWeights.Normal : FontWeights.SemiBold;
            ToolTip = Rules.Count == 0
                ? "Una sola famiglia per tutti i diametri. Clicca per dividere per DN (\"da DN 40 in su usa…\")."
                : "Famiglia per diametro:\n" + string.Join("\n", Rules.Select(r => "da DN" + MepSize.Fmt(r.FromDnMm) + " in su: " + r.Family)) +
                  "\nSotto la prima soglia vale la famiglia della tendina.";
        }
    }

    /// <summary>
    /// Finestra delle soglie per DN di un elemento: una riga per soglia, "da DN [x] in su usa [famiglia]".
    /// Le righe senza DN valido vengono ignorate; a parità di DN vale l'ultima.
    /// </summary>
    internal sealed class FamilyRulesDialog : Window
    {
        private readonly IList<string> _families;
        private readonly string _noneLabel;
        private readonly StackPanel _rows = new StackPanel();
        private readonly List<RuleRow> _ruleRows = new List<RuleRow>();

        /// <summary>Soglie confermate (solo dopo OK), in ordine di DN.</summary>
        public List<FamilyRule> Result { get; } = new List<FamilyRule>();

        public FamilyRulesDialog(string what, string defaultFamily, IList<string> families, string noneLabel, IEnumerable<FamilyRule> current)
        {
            _families = families;
            _noneLabel = noneLabel;

            Title = "Famiglia per diametro: " + what;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "Sotto la prima soglia vale la famiglia scelta nel pannello (" +
                       (string.IsNullOrWhiteSpace(defaultFamily) ? noneLabel : defaultFamily) + "). " +
                       "Da ogni soglia in su (DN compreso) vale la famiglia indicata, fino alla soglia successiva. " +
                       "\"" + noneLabel + "\" su una soglia vale da quel DN in su."
            });

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            header.Children.Add(new TextBlock { Text = "da DN", Width = 88, Foreground = Brushes.Gray });
            header.Children.Add(new TextBlock { Text = "in su usa la famiglia", Foreground = Brushes.Gray });
            root.Children.Add(header);
            root.Children.Add(_rows);

            var add = new Button { Content = "+ Aggiungi soglia", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 10) };
            add.Click += (s, e) => AddRow(NextDn(), null);
            root.Children.Add(add);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) => Confirm();
            var cancel = new Button { Content = "Annulla", Width = 80, IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            foreach (var rule in (current ?? new FamilyRule[0]).Where(r => r != null).OrderBy(r => r.FromDnMm))
                AddRow(rule.FromDnMm, rule.Family);
            if (_ruleRows.Count == 0) AddRow(40, null);
        }

        private double NextDn()
        {
            var last = _ruleRows.Select(r => ParseDn(r.Dn.Text)).Where(d => d > 0).DefaultIfEmpty(0).Max();
            var steps = new[] { 15.0, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300 };
            var next = steps.FirstOrDefault(s => s > last + 0.001);
            return next > 0 ? next : last + 50;
        }

        private void AddRow(double dn, string family)
        {
            var row = new RuleRow();
            row.Panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Dn = new TextBox { Width = 60, Margin = new Thickness(0, 0, 28, 0), Text = dn > 0 ? MepSize.Fmt(dn) : string.Empty, VerticalAlignment = VerticalAlignment.Center };
            row.Family = new ComboBox { Width = 360, VerticalAlignment = VerticalAlignment.Center };
            foreach (var f in _families) row.Family.Items.Add(f);
            row.Family.SelectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(family) && row.Family.Items.Contains(family)) row.Family.SelectedItem = family;
            var remove = new Button { Content = "✕", Width = 24, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Togli questa soglia" };
            remove.Click += (s, e) =>
            {
                _rows.Children.Remove(row.Panel);
                _ruleRows.Remove(row);
            };
            row.Panel.Children.Add(row.Dn);
            row.Panel.Children.Add(row.Family);
            row.Panel.Children.Add(remove);
            _rows.Children.Add(row.Panel);
            _ruleRows.Add(row);
        }

        private void Confirm()
        {
            var byDn = new Dictionary<double, string>();
            foreach (var row in _ruleRows)
            {
                var dn = ParseDn(row.Dn.Text);
                if (dn <= 0) continue;
                byDn[dn] = row.Family.SelectedItem as string ?? _noneLabel;
            }
            Result.Clear();
            Result.AddRange(byDn.OrderBy(kv => kv.Key).Select(kv => new FamilyRule(kv.Key, kv.Value)));
            DialogResult = true;
        }

        private static double ParseDn(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var s = text.Trim();
            if (s.StartsWith("DN", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2).Trim();
            double value;
            return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private sealed class RuleRow
        {
            public StackPanel Panel;
            public TextBox Dn;
            public ComboBox Family;
        }
    }
}
