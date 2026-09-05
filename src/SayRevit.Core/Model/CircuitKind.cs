using System;
using System.Collections.Generic;
using System.Linq;

namespace SayRevit.Core.Model
{
    /// <summary>
    /// Tipologia idraulica di un circuito in partenza dal collettore. Decide quali componenti
    /// stanno sullo stacco oltre alla valvola di intercettazione: pompa, organo di regolazione,
    /// eventuale bypass fra mandata e ritorno.
    /// </summary>
    public enum CircuitKind
    {
        /// <summary>Diretto: pompa, nessuna miscelazione (temperatura del collettore).</summary>
        Direct,

        /// <summary>Miscelato con valvola a 3 vie sulla mandata e bypass dal ritorno, poi la pompa.</summary>
        MixThreeWay,

        /// <summary>Miscelato a 2 vie (iniezione): valvola a 2 vie sulla mandata primaria, pompa sul secondario con ricircolo.</summary>
        MixTwoWayInjection,

        /// <summary>Senza pompa: solo intercettazione, il circuito è spinto dal primario.</summary>
        NoPump,

        /// <summary>Cieco: sola intercettazione, lo stacco si ferma alla seconda flangia della valvola (nessun tubo a valle).</summary>
        Blind
    }

    /// <summary>Componente previsto lungo uno stacco, dall'asse del collettore verso l'utenza.</summary>
    public enum CircuitComponent
    {
        /// <summary>Valvola di intercettazione (sfera o boax secondo il DN).</summary>
        ShutoffValve,
        /// <summary>Pompa di circolazione in linea.</summary>
        Pump,
        /// <summary>Valvola miscelatrice a 3 vie.</summary>
        ThreeWayMixingValve,
        /// <summary>Valvola di regolazione a 2 vie.</summary>
        TwoWayValve,
        /// <summary>Bypass tra mandata e ritorno.</summary>
        Bypass,
        /// <summary>Filtro a Y.</summary>
        Strainer,
        /// <summary>Valvola di zona (farfalla wafer con riduttore manuale).</summary>
        ZoneValve
    }

    /// <summary>Descrizione di una tipologia: codice per le impostazioni, etichette e componenti.</summary>
    public sealed class CircuitKindInfo
    {
        public CircuitKind Kind { get; internal set; }

        /// <summary>Codice breve e stabile usato nelle impostazioni (es. "mix3").</summary>
        public string Code { get; internal set; }

        /// <summary>Etichetta mostrata nella tendina della riga.</summary>
        public string Label { get; internal set; }

        /// <summary>Descrizione per il suggerimento e le note dell'anteprima.</summary>
        public string Description { get; internal set; }

        /// <summary>Componenti sullo stacco di MANDATA, dal collettore verso l'utenza.</summary>
        public IReadOnlyList<CircuitComponent> SupplyComponents { get; internal set; }

        /// <summary>Componenti sullo stacco di RITORNO, dal collettore verso l'utenza.</summary>
        public IReadOnlyList<CircuitComponent> ReturnComponents { get; internal set; }

        /// <summary>True se sullo stacco va la valvola di intercettazione (tutte le tipologie).</summary>
        public bool HasShutoffValve
        {
            get { return SupplyComponents.Contains(CircuitComponent.ShutoffValve); }
        }

        /// <summary>
        /// True se lo stacco si ferma alla faccia d'uscita della valvola (seconda flangia della
        /// boax, uscita della sfera), senza tubo a valle: è il cieco.
        /// </summary>
        public bool IsBlind
        {
            get { return Kind == CircuitKind.Blind; }
        }

        /// <summary>
        /// True se la lunghezza dello stacco è fissata dal tubo DOPO la valvola (dalla faccia
        /// d'uscita dell'ultimo pezzo: seconda flangia della boax o uscita della sfera), non dalla
        /// lunghezza generica dei circuiti. Vale per il senza pompa (<see cref="ManifoldPlan.NoPumpPipeAfterValveMm"/>).
        /// </summary>
        public bool UsesPipeAfterValve { get; internal set; }

        public bool HasPump
        {
            get { return SupplyComponents.Contains(CircuitComponent.Pump); }
        }

        public bool HasMixing
        {
            get
            {
                return SupplyComponents.Concat(ReturnComponents).Any(c =>
                    c == CircuitComponent.ThreeWayMixingValve || c == CircuitComponent.TwoWayValve);
            }
        }

        /// <summary>True se la tipologia collega mandata e ritorno con un bypass (stacchi accoppiati).</summary>
        public bool HasBypass
        {
            get { return SupplyComponents.Contains(CircuitComponent.Bypass); }
        }

        /// <summary>
        /// True se la catena completa dei pezzi (oltre all'intercettazione) viene modellata in Revit:
        /// oggi il mix 2 vie a iniezione. Per le altre tipologie con pompa o miscelazione i
        /// componenti restano dichiarati in anteprima.
        /// </summary>
        public bool IsChainModelled
        {
            get { return Kind == CircuitKind.MixTwoWayInjection; }
        }

        public override string ToString()
        {
            return Label;
        }
    }

    /// <summary>Le tipologie disponibili, nell'ordine proposto all'utente, con codici e componenti.</summary>
    public static class CircuitKinds
    {
        public static readonly CircuitKind Default = CircuitKind.Direct;

        private static readonly CircuitKindInfo[] Infos =
        {
            new CircuitKindInfo
            {
                Kind = CircuitKind.Direct,
                Code = "direct",
                Label = "Diretto (no mix)",
                Description = "Circuito diretto alla temperatura del collettore: intercettazione e pompa, nessuna miscelazione.",
                SupplyComponents = new[] { CircuitComponent.ShutoffValve, CircuitComponent.Pump },
                ReturnComponents = new[] { CircuitComponent.ShutoffValve }
            },
            new CircuitKindInfo
            {
                Kind = CircuitKind.MixThreeWay,
                Code = "mix3",
                Label = "Mix 3 vie",
                Description = "Circuito miscelato: valvola a 3 vie sulla mandata alimentata anche dal bypass del ritorno, poi la pompa.",
                SupplyComponents = new[] { CircuitComponent.ShutoffValve, CircuitComponent.ThreeWayMixingValve, CircuitComponent.Bypass, CircuitComponent.Pump },
                ReturnComponents = new[] { CircuitComponent.ShutoffValve, CircuitComponent.Bypass }
            },
            new CircuitKindInfo
            {
                Kind = CircuitKind.MixTwoWayInjection,
                Code = "mix2",
                Label = "Mix 2 vie (iniezione)",
                Description = "Circuito a iniezione: sulla mandata intercettazione, T del bypass, spazio per la pompa, valvola di zona e " +
                              "intercettazione; sul ritorno intercettazione, valvola a 2 vie (energy valve), T del bypass, filtro a Y, " +
                              "valvola di zona e intercettazione. Il bypass unisce i due T con una valvola di ritegno.",
                SupplyComponents = new[] { CircuitComponent.ShutoffValve, CircuitComponent.Bypass, CircuitComponent.Pump, CircuitComponent.ZoneValve, CircuitComponent.ShutoffValve },
                ReturnComponents = new[] { CircuitComponent.ShutoffValve, CircuitComponent.TwoWayValve, CircuitComponent.Bypass, CircuitComponent.Strainer, CircuitComponent.ZoneValve, CircuitComponent.ShutoffValve }
            },
            new CircuitKindInfo
            {
                Kind = CircuitKind.NoPump,
                Code = "nopump",
                Label = "Senza pompa",
                Description = "Circuito senza pompa propria: sola intercettazione su mandata e ritorno. " +
                              "Il tubo dopo la valvola (dalla seconda flangia) ha lunghezza fissa, su mandata e ritorno.",
                SupplyComponents = new[] { CircuitComponent.ShutoffValve },
                ReturnComponents = new[] { CircuitComponent.ShutoffValve },
                UsesPipeAfterValve = true
            },
            new CircuitKindInfo
            {
                Kind = CircuitKind.Blind,
                Code = "blind",
                Label = "Cieco",
                Description = "Stacco cieco: sola intercettazione, lo stacco si ferma alla seconda flangia della valvola " +
                              "(o all'uscita della sfera), senza tubo a valle, su mandata e ritorno.",
                SupplyComponents = new[] { CircuitComponent.ShutoffValve },
                ReturnComponents = new[] { CircuitComponent.ShutoffValve }
            }
        };

        /// <summary>Tutte le tipologie nell'ordine di presentazione.</summary>
        public static IReadOnlyList<CircuitKindInfo> All
        {
            get { return Infos; }
        }

        public static CircuitKindInfo Info(CircuitKind kind)
        {
            return Infos.FirstOrDefault(i => i.Kind == kind) ?? Infos[0];
        }

        public static string Code(CircuitKind kind)
        {
            return Info(kind).Code;
        }

        public static string Label(CircuitKind kind)
        {
            return Info(kind).Label;
        }

        /// <summary>
        /// Riconosce un codice ("mix3"), il nome dell'enum ("MixThreeWay") o qualche sinonimo
        /// tollerante ("3vie", "iniezione", "senza pompa"). Vuoto o sconosciuto → false.
        /// </summary>
        public static bool TryParse(string text, out CircuitKind kind)
        {
            kind = Default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);

            foreach (var i in Infos)
            {
                if (t == i.Code || t == i.Kind.ToString().ToLowerInvariant())
                {
                    kind = i.Kind;
                    return true;
                }
            }

            if (t == "diretto" || t == "nomix" || t == "d")
            {
                kind = CircuitKind.Direct;
                return true;
            }
            if (t == "3vie" || t == "mix3vie" || t == "threeway" || t == "3")
            {
                kind = CircuitKind.MixThreeWay;
                return true;
            }
            if (t == "2vie" || t == "mix2vie" || t == "iniezione" || t == "injection" || t == "twoway" || t == "2")
            {
                kind = CircuitKind.MixTwoWayInjection;
                return true;
            }
            if (t == "senzapompa" || t == "nopompa" || t == "np")
            {
                kind = CircuitKind.NoPump;
                return true;
            }
            if (t == "cieco" || t == "cieca" || t == "tappato" || t == "chiuso" || t == "riserva" || t == "cap")
            {
                kind = CircuitKind.Blind;
                return true;
            }
            return false;
        }

        /// <summary>Nome leggibile di un componente, per note e anteprima.</summary>
        public static string ComponentLabel(CircuitComponent c)
        {
            switch (c)
            {
                case CircuitComponent.ShutoffValve: return "intercettazione";
                case CircuitComponent.Pump: return "pompa";
                case CircuitComponent.ThreeWayMixingValve: return "valvola miscelatrice 3 vie";
                case CircuitComponent.TwoWayValve: return "valvola 2 vie";
                case CircuitComponent.Bypass: return "bypass mandata/ritorno";
                case CircuitComponent.Strainer: return "filtro a Y";
                case CircuitComponent.ZoneValve: return "valvola di zona";
                default: return c.ToString();
            }
        }

        /// <summary>Catena dei componenti di mandata in una riga ("intercettazione → valvola 3 vie → pompa").</summary>
        public static string SupplyChain(CircuitKind kind)
        {
            var info = Info(kind);
            var chain = string.Join(" → ", info.SupplyComponents.Select(ComponentLabel));
            return info.IsBlind ? chain + " (fine alla flangia)" : chain;
        }

        /// <summary>Catena dei componenti di ritorno in una riga.</summary>
        public static string ReturnChain(CircuitKind kind)
        {
            var info = Info(kind);
            return string.Join(" → ", info.ReturnComponents.Select(ComponentLabel));
        }
    }
}
