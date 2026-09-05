using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SayRevit.Core.Model;
using SayRevit.Core.Parsing;

namespace SayRevit.Addin.UI
{
    /// <summary>
    /// Scelta della famiglia di un elemento del registro (<see cref="ManifoldElement"/>): etichetta,
    /// tendina con le famiglie di accessori del progetto (proposta sul nome) e pulsante "per DN…"
    /// con le soglie per diametro. Sa riempire la mappa del piano e leggere/scrivere la forma salvata.
    /// Il pannello non conosce i singoli elementi: ne crea uno per ogni voce del registro.
    /// </summary>
    internal sealed class FamilyPicker
    {
        private readonly ModelCatalog _catalog;
        private readonly string _none;

        public ManifoldElement Element { get; }

        public ComboBox Combo { get; } = new ComboBox();

        public DnRulesButton Rules { get; }

        /// <summary>Etichetta + tendina + pulsante, da mettere nel pannello.</summary>
        public UIElement View { get; }

        /// <summary>Sollevato a ogni cambio di famiglia di base o di soglie.</summary>
        public event EventHandler Changed;

        /// <param name="noneLabel">Voce che significa "nessun pezzo" ("(nessuna)").</param>
        /// <param name="autoLabel">Voce che significa "automatica sul nome", usata al posto di noneLabel per gli elementi <see cref="ManifoldElement.AutoByName"/>.</param>
        public FamilyPicker(ManifoldElement element, ModelCatalog catalog, string noneLabel, string autoLabel)
        {
            Element = element;
            _catalog = catalog;
            _none = element.AutoByName ? autoLabel : noneLabel;

            var choices = new List<string> { _none };
            if (element.AutoByName)
            {
                // energy valve: prima le famiglie riconosciute dal nome (ev025r2…), poi le altre
                var probe = new ManifoldPlan();
                probe.AccessoryFamilies.AddRange(catalog.PipeAccessories);
                choices.AddRange(probe.EnergyValveFamilies().Select(f => f.Name));
            }
            choices.AddRange(catalog.PipeAccessories.Select(f => f.Name).Where(n => !choices.Contains(n)));
            foreach (var c in choices) Combo.Items.Add(c);
            Combo.SelectedIndex = 0;
            Combo.ToolTip = element.Tooltip;
            Combo.Width = element.UiWidth;
            Combo.VerticalAlignment = VerticalAlignment.Center;

            // proposta sul nome: la prima famiglia che contiene una delle parole del registro
            foreach (var f in catalog.PipeAccessories)
            {
                var name = TextUtil.Fold(f.Name);
                if (!element.Hints.Any(h => name.Contains(h))) continue;
                Combo.SelectedItem = f.Name;
                break;
            }

            Rules = new DnRulesButton(element.Label, choices, _none, () => SelectedFamily);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 6) };
            panel.Children.Add(new TextBlock { Text = element.UiLabel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            panel.Children.Add(Combo);
            panel.Children.Add(Rules);
            View = panel;

            Combo.SelectionChanged += (s, e) => Raise();
            Rules.RulesChanged += (s, e) => Raise();
        }

        /// <summary>Famiglia di base scelta; null con "(nessuna)"/"(automatica)".</summary>
        public string SelectedFamily
        {
            get
            {
                var name = Combo.SelectedItem as string;
                return string.IsNullOrWhiteSpace(name) || name == _none ? null : name;
            }
        }

        public bool IsEnabled
        {
            set
            {
                Combo.IsEnabled = value;
                Rules.IsEnabled = value;
            }
        }

        /// <summary>Tipi della famiglia di base (per il piano, che li usa quando il catalogo non basta).</summary>
        public List<string> DefaultTypes()
        {
            return TypesOf(SelectedFamily);
        }

        /// <summary>Tipi di tutte le famiglie in gioco (base e soglie): per la lista dei PN.</summary>
        public IEnumerable<string> AllTypes()
        {
            return TypesOf(SelectedFamily).Concat(Rules.Rules.SelectMany(r => TypesOf(r.Family)));
        }

        private List<string> TypesOf(string family)
        {
            if (string.IsNullOrWhiteSpace(family) || family == _none) return new List<string>();
            var found = _catalog.PipeAccessories.FirstOrDefault(f => f.Name == family);
            return found == null ? new List<string>() : found.TypeNames;
        }

        /// <summary>Riempie la famiglia per DN del piano: la voce "nessuna"/"automatica" diventa null.</summary>
        public void FillMap(FamilyByDn target)
        {
            target.Default = SelectedFamily;
            target.Rules.Clear();
            foreach (var r in Rules.Rules)
            {
                var family = string.IsNullOrWhiteSpace(r.Family) || r.Family == _none ? null : r.Family;
                target.Rules.Add(new FamilyRule(r.FromDnMm, family));
            }
        }

        /// <summary>
        /// Forma salvata: "(nessuna)" resta scritto perché è una scelta dell'utente; per l'elemento
        /// automatico sul nome il vuoto significa "automatica".
        /// </summary>
        public string Store()
        {
            var map = new FamilyByDn();
            FillMap(map);
            var noneStored = Element.AutoByName ? string.Empty : _none;
            map.Default = map.Default ?? noneStored;
            foreach (var r in map.Rules) r.Family = r.Family ?? noneStored;
            return map.ToString();
        }

        /// <summary>
        /// Carica la forma salvata: la base solo se caricata anche in questo progetto (altrimenti
        /// resta la proposta sul nome), le soglie solo con una famiglia presente in tendina.
        /// </summary>
        public void Load(string stored)
        {
            var map = FamilyByDn.Parse(stored);
            var wanted = string.IsNullOrWhiteSpace(map.Default) ? (Element.AutoByName ? _none : null) : map.Default;
            if (wanted != null && Combo.Items.Contains(wanted)) Combo.SelectedItem = wanted;
            var kept = new List<FamilyRule>();
            foreach (var r in map.OrderedRules())
            {
                var family = string.IsNullOrWhiteSpace(r.Family) ? _none : r.Family;
                if (!Combo.Items.Contains(family)) continue;
                kept.Add(new FamilyRule(r.FromDnMm, family));
            }
            Rules.SetRules(kept);
        }

        private void Raise()
        {
            var handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
