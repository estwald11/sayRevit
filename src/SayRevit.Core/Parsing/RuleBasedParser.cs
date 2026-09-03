using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SayRevit.Core.Model;

namespace SayRevit.Core.Parsing
{
    /// <summary>
    /// Parser deterministico (nessuna dipendenza esterna, funziona offline) per descrizioni
    /// in italiano o inglese di tubazioni e canali con stacchi.
    /// Esempi: "una tubazione DN200 lunga 10 m con 3 stacchi DN15 ogni 2 m verso l'alto",
    ///         "canale 400x200 di 6 metri con due stacchi 200x200 laterali".
    /// </summary>
    public sealed class RuleBasedParser : IIntentParser
    {
        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        private static readonly string Num = TextUtil.NumberPattern;
        private static readonly string Unit = TextUtil.LengthUnitPattern;
        private static readonly string MetreUnit = @"(?:m\b|mt\b|metri|metro|meters?|metres?|cm|centimetri|centimetro|ft\b|feet|foot|piedi|piede)";

        private static readonly Regex RunSplit = new Regex(
            @"\s*(?:;|\.(?=\s|$)|\n|\be poi\b|\bpoi\b|\bquindi\b|\bdopodich[eé]\b|\bsuccessivamente\b|\bin seguito\b|\bthen\b|\bafter that\b|\bnext\b)\s*", Opt);

        private static readonly Regex TrailingConnector = new Regex(
            @"\s*(?:,|;|\be\b|\band\b|\bcon\b|\bwith\b|\bdotat[oa] di\b|\bavente\b|\bche ha\b|\bda cui partono\b|\bfrom which\b|\bpi[uù]\b|\bplus\b|\bdi\b|\bda\b|\bof\b)\s*$", Opt);

        private static readonly Regex LeadingConnector = new Regex(
            @"^\s*(?:,|;|\be\b|\band\b|\bcon\b|\bwith\b|\bpi[uù]\b|\bplus\b|\boltre a\b)\s*", Opt);

        private const string SizeToken = @"(?:dn\s*\d+|\d+(?:[.,]\d+)?\s*(?:mm|cm)?\s*x\s*\d+(?:[.,]\d+)?\s*(?:mm|cm)?|\d+(?:[.,]\d+)?(?:\s+\d+/\d+)?\s*(?:""|''|mm|inch(?:es)?|pollic[ei])|\d+/\d+\s*(?:""|inch(?:es)?)?|diametro\s*\d+\s*(?:mm)?)";

        private static readonly Regex BranchStart = new Regex(
            @"(?<![\w/])(?:n\s*)?(?:(?<indef>" + IndefPattern() + @")|(?<num>\d+)(?![\d.,/x])|(?<word>" + TextUtil.NumberWordPattern + @"\b))?\s*(?:x\s+)?" +
            @"(?<mid>(?:(?:" + SizeToken + @"|(?!(?:" + TextUtil.NumberWordPattern + @"|con|with|e|and|of|di|da|dn)\b)[a-z][a-z\-']*)\s+){0,2})" +
            @"(?<noun>" + Lexicon.BranchNoun + @")", Opt);

        private static readonly Regex ExplicitType = new Regex(
            @"(?:\btipo\b|\btype\b|\bfamiglia\b|\bfamily\b)\s*[:=]?\s*(?:""(?<n>[^""]+)""|'(?<n>[^']+)')", Opt);

        private static readonly Regex PipeNounRe = new Regex(@"\b" + Lexicon.PipeNoun, Opt);
        private static readonly Regex DuctNounRe = new Regex(@"\b" + Lexicon.DuctNoun, Opt);

        private static readonly Regex DnRe = new Regex(@"\bdn\s*(?<v>\d+(?:[.,]\d+)?)", Opt);
        private static readonly Regex RectRe = new Regex(@"(?<![\w/])(?<w>\d+(?:[.,]\d+)?)\s*(?:mm|cm)?\s*x\s*(?<h>\d+(?:[.,]\d+)?)\s*(?<u>mm|cm)?(?![\w/])", Opt);
        private static readonly Regex InchRe = new Regex(@"(?<![\w/])(?<v>" + Num + @")\s*(?:""|''|pollic[ei]|inch(?:es)?|in\.)", Opt);
        private static readonly Regex DiaWordRe = new Regex(@"(?:\bdiametro\b|\bdiameter\b|\bdia\b|\bd\b)\s*(?:di\b|nominale\b|nominal\b|esterno\b|interno\b|=|:)?\s*(?<v>" + Num + @")\s*(?<u>mm|cm|m\b|""|pollic[ei]|inch(?:es)?)?", Opt);
        private static readonly Regex BareSizeRe = new Regex(@"(?:\bda\b|\bdi\b|\bof\b|\bsize\b|\bdimensione\b|\bsezione\b|\bmisura\b)\s*(?<v>\d+(?:[.,]\d+)?)\s*(?<u>mm|cm)?(?!\s*(?:m\b|mt\b|metri|metro|meters?|metres?|ft\b|feet|piedi|x|/))(?![\w/])", Opt);
        private static readonly Regex MmSizeRe = new Regex(@"(?<![\w/])(?<v>\d+(?:[.,]\d+)?)\s*(?:mm|millimetri)\b", Opt);

        private static readonly Regex LengthKeyRe = new Regex(
            @"(?:\blung[ahoi]\b|\blunghezza\b|\blength\b|\blong\b|\bestensione\b|\bestes[ao]\b|\bper\b|\bfor\b)\s*(?:di\b|of\b|=|:)?\s*(?<v>" + Num + @")\s*(?<u>" + Unit + @")", Opt);
        private static readonly Regex LengthDiRe = new Regex(
            @"(?:\bdi\b|\bda\b|\bof\b)\s*(?<v>" + Num + @")\s*(?<u>" + MetreUnit + @")", Opt);
        private static readonly Regex LengthBareRe = new Regex(
            @"(?<![\w/])(?<v>" + Num + @")\s*(?<u>" + MetreUnit + @")", Opt);
        private static readonly Regex LengthBareMmRe = new Regex(
            @"(?<![\w/])(?<v>" + Num + @")\s*(?<u>mm|millimetri)\b", Opt);

        private static readonly Regex ElevationRe = new Regex(
            @"(?:\ba\b|\balla\b|\bad\b|\bat\b|\bin\b)?\s*(?:\bquota\b|\baltezza\b|\belevazione\b|\bheight\b|\belevation\b|\boffset\b|\bh\b)\s*(?:di\b|of\b|=|:)?\s*(?<v>" + Num + @")\s*(?<u>" + Unit + @")?", Opt);
        private static readonly Regex ElevationFloorRe = new Regex(
            @"(?:\ba\b|\bat\b)\s*(?<v>" + Num + @")\s*(?<u>" + Unit + @")\s*(?:da terra|dal pavimento|dal piano|dal suolo|sopra il pavimento|from (?:the )?floor|above (?:the )?floor|above ffl|\bafl\b|\bagl\b|\bffl\b)", Opt);

        private static readonly Regex LevelRe = new Regex(
            @"(?:\bal\b|\bsul\b|\bdel\b|\bon\b|\bat\b)?\s*(?:\blivello\b|\bpiano\b|\blevel\b|\bfloor\b|\bstorey\b|\bstory\b)\s+(?:di\b|del\b|dello\b|della\b|dei\b|the\b)?\s*(?<a>[a-z0-9àèéìòù'\-\.]+)(?:\s+(?<b>[a-z0-9àèéìòù'\-\.]+))?", Opt);

        private static readonly HashSet<string> LevelStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "con", "e", "and", "with", "lunga", "lungo", "lunghi", "lunghe", "lunghezza", "di", "da", "of", "verso", "a", "at", "in",
            "per", "for", "quota", "altezza", "dn", "che", "poi", "tipo", "type", "the", "long", "length", "ogni", "every"
        };

        private static readonly Regex SpacingRe = new Regex(
            @"(?:\bogni\b|\bevery\b|\beach\b|\binterasse\b|\bpasso\b|\ba passo di\b|\bspaced\b|\bspacing\b|\bdistanziat[ie]\b|\ba distanza di\b|\bdistanza\b|\bpitch\b|\bcentres?\b|\bcenters?\b)\s*(?:di\b|of\b|=|:|at\b)?\s*(?<v>" + Num + @")?\s*(?<u>" + Unit + @")", Opt);

        private static readonly Regex PositionsRe = new Regex(
            @"(?:\balle\b|\balla\b|\ba\b|\bat\b|\bin posizione\b|\bposizioni\b|\bpositions?\b)\s*(?:posizion[ei]\b|distanz[ae]\b)?\s*(?:di\b|of\b)?\s*(?<list>(?:" + Num + @"\s*(?:" + Unit + @")?\s*(?:,|\be\b|\band\b|;)\s*)*" + Num + @"\s*(?<u>" + Unit + @"))\s*(?:dall'inizio|dall'attacco|dal punto iniziale|dalla partenza|dall'origine|from (?:the )?start|from (?:the )?beginning|from (?:the )?origin)", Opt);

        private static readonly Regex SeparateRunRe = new Regex(
            @"\b(?:un'?altr[ao]|another|separat[ao]|separate|nuov[ao]|new|second[ao]|second|terz[ao]|third|parallel[ao]|parallel)\b", Opt);

        private static readonly Regex OneRe = new Regex(@"\b(?:un[oa]?|a|an|one|single|singol[oa])\b", Opt);

        private static readonly List<KeyValuePair<Regex, string>> SystemRes = Lexicon.Systems
            .Select(kv => new KeyValuePair<Regex, string>(new Regex(@"\b(?:" + kv.Key + @")", Opt), kv.Value)).ToList();

        private static readonly List<KeyValuePair<Regex, string[]>> MaterialRes = Lexicon.Materials
            .Select(kv => new KeyValuePair<Regex, string[]>(new Regex(@"\b(?:" + kv.Key + @")", Opt), kv.Value)).ToList();

        private static readonly List<KeyValuePair<Regex, DirectionKind>> DirectionRes = Lexicon.Directions
            .Select(kv => new KeyValuePair<Regex, DirectionKind>(new Regex(@"(?:" + kv.Key + @")", Opt), kv.Value)).ToList();

        private static readonly (double inch, double dn)[] InchToDn =
        {
            (0.375, 10), (0.5, 15), (0.75, 20), (1, 25), (1.25, 32), (1.5, 40), (2, 50), (2.5, 65), (3, 80), (4, 100),
            (5, 125), (6, 150), (8, 200), (10, 250), (12, 300), (14, 350), (16, 400), (18, 450), (20, 500)
        };

        private Dictionary<string, string> _originalCase = new Dictionary<string, string>();

        public string Name => "Regole (offline)";

        public Task<ParseResult> ParseAsync(string text, ModelCatalog catalog, CancellationToken cancellationToken)
        {
            return Task.FromResult(Parse(text, catalog));
        }

        private static readonly Regex QuotedRe = new Regex(@"""([^""]+)""|'([^']+)'", Opt);

        public ParseResult Parse(string text, ModelCatalog catalog)
        {
            var result = new ParseResult { Plan = new MepPlan { SourceText = text } };
            var originalCase = new Dictionary<string, string>();
            foreach (Match q in QuotedRe.Matches(text ?? string.Empty))
            {
                var v = q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value;
                originalCase[v.Trim().ToLowerInvariant()] = v.Trim();
            }
            _originalCase = originalCase;
            var norm = TextUtil.Normalize(text);
            if (norm.Length == 0) return ParseResult.Fail("Inserisci una descrizione, ad esempio: \"una tubazione DN200 lunga 10 m con 3 stacchi DN15\".");

            var segments = RunSplit.Split(norm)
                .Select(s => TrailingConnector.Replace(s.Trim(), string.Empty).Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (segments.Count == 0) return ParseResult.Fail("Non ho trovato una descrizione da interpretare.");

            MepRun prev = null;
            for (var i = 0; i < segments.Count; i++)
            {
                var run = ParseRun(segments[i], i, prev, result);
                if (run == null) continue;
                result.Plan.Runs.Add(run);
                prev = run;
            }

            if (result.Plan.Runs.Count == 0) return ParseResult.Fail("Non ho riconosciuto nessuna tubazione o canale nella descrizione.");

            Validate(result, catalog);
            result.Success = true;
            return result;
        }

        // ------------------------------------------------------------------ run

        private MepRun ParseRun(string segment, int index, MepRun prev, ParseResult result)
        {
            var run = new MepRun();

            // Attributi validi per tutto il tratto (tipo, sistema, materiale, livello, quota) possono
            // comparire ovunque nella frase: si estraggono prima di separare testa e stacchi.
            segment = ParseCommon(segment, run);

            // Separa la testa (tratto principale) dalle clausole degli stacchi.
            var branchMatches = BranchStart.Matches(segment).Cast<Match>().ToList();
            string head;
            var branchClauses = new List<string>();
            if (branchMatches.Count == 0)
            {
                head = segment;
            }
            else
            {
                head = segment.Substring(0, branchMatches[0].Index);
                for (var k = 0; k < branchMatches.Count; k++)
                {
                    var start = branchMatches[k].Index;
                    var end = k + 1 < branchMatches.Count ? branchMatches[k + 1].Index : segment.Length;
                    var clause = segment.Substring(start, end - start);
                    clause = TrailingConnector.Replace(clause, string.Empty);
                    branchClauses.Add(clause.Trim());
                }
            }

            for (var guard = 0; guard < 3; guard++) head = TrailingConnector.Replace(head, string.Empty).Trim();

            var hasKindNoun = ParseHead(head, run, result, index);
            run.KindExplicit = hasKindNoun;

            if (index > 0 && prev != null)
            {
                var separate = SeparateRunRe.IsMatch(head);
                run.ContinuesPrevious = !separate;
                if (run.Size == null) run.Size = prev.Size?.Clone();
                if (!hasKindNoun) { run.Kind = prev.Kind; run.KindExplicit = prev.KindExplicit; }
                if (run.ExplicitTypeName == null) run.ExplicitTypeName = prev.ExplicitTypeName;
                if (run.TypeHints.Count == 0) run.TypeHints.AddRange(prev.TypeHints);
                if (run.SystemClass == null) { run.SystemClass = prev.SystemClass; run.SystemPhrase = prev.SystemPhrase; }
                if (run.LevelHint == null) run.LevelHint = prev.LevelHint;
                if (run.ElevationMm == null && !run.ContinuesPrevious) run.ElevationMm = prev.ElevationMm;
            }

            foreach (var clause in branchClauses)
            {
                var b = ParseBranch(clause, run, result);
                if (b != null) run.Branches.Add(b);
            }

            return run;
        }

        /// <summary>Interpreta la testa del tratto. Ritorna true se è stato trovato un sostantivo (tubo/canale).</summary>
        private bool ParseHead(string head, MepRun run, ParseResult result, int index)
        {
            var s = head;

            var hasNoun = false;
            var duct = DuctNounRe.Match(s);
            var pipe = PipeNounRe.Match(s);
            if (duct.Success && (!pipe.Success || duct.Index <= pipe.Index))
            {
                run.Kind = MepKind.Duct;
                hasNoun = true;
                s = TextUtil.Blank(s, duct.Index, duct.Length);
            }
            else if (pipe.Success)
            {
                // "condotta" è ambigua: se il sistema è aeraulico si intende un canale.
                run.Kind = pipe.Value.StartsWith("condott", StringComparison.OrdinalIgnoreCase) && SystemClass.IsDuctClass(run.SystemClass)
                    ? MepKind.Duct
                    : MepKind.Pipe;
                hasNoun = true;
                s = TextUtil.Blank(s, pipe.Index, pipe.Length);
            }

            run.Size = ParseExplicitSize(ref s, result);

            double? length = ParseKeywordLength(ref s);

            if (run.Size == null) run.Size = ParseBareSize(ref s, hasNoun || index > 0);

            if (length == null) length = ParseBareLength(ref s, run.Size != null);
            if (length != null) run.LengthMm = length.Value;

            var dir = ParseDirection(ref s);
            switch (dir)
            {
                case DirectionKind.Left: run.Direction = DirectionKind.PlusY; break;
                case DirectionKind.Right: run.Direction = DirectionKind.MinusY; break;
                case DirectionKind.Alternate: run.Direction = DirectionKind.Default; break;
                default: run.Direction = dir; break;
            }

            return hasNoun;
        }

        /// <summary>Estrae gli attributi comuni al tratto e restituisce il testo con le parti consumate.</summary>
        private string ParseCommon(string segment, MepRun run)
        {
            var s = segment;

            var et = ExplicitType.Match(s);
            if (et.Success)
            {
                var key = et.Groups["n"].Value.Trim();
                run.ExplicitTypeName = _originalCase.TryGetValue(key, out var orig) ? orig : key;
                s = TextUtil.Blank(s, et.Index, et.Length);
            }

            // Sistema: si sceglie la frase più lunga tra quelle riconosciute.
            Match bestSys = null;
            string bestCls = null;
            foreach (var kv in SystemRes)
            {
                var m = kv.Key.Match(s);
                if (m.Success && (bestSys == null || m.Length > bestSys.Length))
                {
                    bestSys = m;
                    bestCls = kv.Value;
                }
            }
            if (bestSys != null)
            {
                run.SystemClass = bestCls;
                run.SystemPhrase = bestSys.Value.Trim();
                s = TextUtil.Blank(s, bestSys.Index, bestSys.Length);
            }

            foreach (var kv in MaterialRes)
            {
                var m = kv.Key.Match(s);
                if (!m.Success) continue;
                foreach (var syn in kv.Value) if (!run.TypeHints.Contains(syn)) run.TypeHints.Add(syn);
                s = TextUtil.Blank(s, m.Index, m.Length);
            }

            var lvl = LevelRe.Match(s);
            if (lvl.Success)
            {
                var a = lvl.Groups["a"].Value;
                var b = lvl.Groups["b"].Success ? lvl.Groups["b"].Value : null;
                if (LevelStop.Contains(a) || Regex.IsMatch(a, @"^\d+(?:[.,]\d+)?$") && b != null && Regex.IsMatch(b, "^" + Unit + "$", Opt))
                {
                    // "piano 3 m" non è un livello: ignora
                }
                else
                {
                    var name = a;
                    if (b != null && !LevelStop.Contains(b) && !Regex.IsMatch(b, "^" + Unit + "$", Opt) && !Regex.IsMatch(b, @"^\d")) name += " " + b;
                    run.LevelHint = name.Trim('.', ',', ' ');
                    var consumed = name == a ? lvl.Groups["a"].Index + lvl.Groups["a"].Length - lvl.Index : lvl.Length;
                    s = TextUtil.Blank(s, lvl.Index, consumed);
                }
            }

            var ef = ElevationFloorRe.Match(s);
            if (ef.Success && TextUtil.TryParseNumber(ef.Groups["v"].Value, out var ev))
            {
                run.ElevationMm = ev * TextUtil.UnitToMm(ef.Groups["u"].Value);
                s = TextUtil.Blank(s, ef.Index, ef.Length);
            }
            else
            {
                var e = ElevationRe.Match(s);
                if (e.Success && TextUtil.TryParseNumber(e.Groups["v"].Value, out ev))
                {
                    var u = e.Groups["u"].Success ? e.Groups["u"].Value : (ev < 20 ? "m" : "mm");
                    run.ElevationMm = ev * TextUtil.UnitToMm(u);
                    s = TextUtil.Blank(s, e.Index, e.Length);
                }
            }

            return s;
        }

        // --------------------------------------------------------------- branch

        private MepBranch ParseBranch(string clause, MepRun run, ParseResult result)
        {
            var s = LeadingConnector.Replace(clause, string.Empty);
            var b = new MepBranch();

            var m = BranchStart.Match(s);
            if (!m.Success) return null;

            var noun = m.Groups["noun"].Value;
            var plural = Regex.IsMatch(noun, @"(?:hi|ni|ie|s|es)$", Opt) && !Regex.IsMatch(noun, @"^(?:tee|spur)$", Opt);

            if (m.Groups["num"].Success)
            {
                b.Count = int.Parse(m.Groups["num"].Value);
            }
            else if (m.Groups["word"].Success && TextUtil.TryParseNumberWord(m.Groups["word"].Value, out var w))
            {
                b.Count = w;
            }
            else if (m.Groups["indef"].Success)
            {
                b.Count = IndefCount(m.Groups["indef"].Value);
                result.Notes.Add("\"" + m.Groups["indef"].Value + " " + noun + "\": numero non specificato, ne creo " + b.Count + ".");
            }
            else if (plural)
            {
                b.Count = 2;
                result.Notes.Add("\"" + noun + "\": numero non specificato, ne creo 2.");
            }
            else
            {
                b.Count = 1;
            }
            s = TextUtil.Blank(s, m.Index, m.Length);

            b.Size = ParseExplicitSize(ref s, result);

            var pos = PositionsRe.Match(s);
            if (pos.Success)
            {
                var lastUnit = pos.Groups["u"].Value;
                foreach (Match nm in Regex.Matches(pos.Groups["list"].Value, @"(?<v>" + Num + @")\s*(?<u>" + Unit + @")?", Opt))
                {
                    if (!TextUtil.TryParseNumber(nm.Groups["v"].Value, out var v)) continue;
                    var u = nm.Groups["u"].Success ? nm.Groups["u"].Value : lastUnit;
                    b.PositionsMm.Add(v * TextUtil.UnitToMm(u));
                }
                s = TextUtil.Blank(s, pos.Index, pos.Length);
                if (b.PositionsMm.Count > 0 && !m.Groups["num"].Success && !m.Groups["word"].Success) b.Count = b.PositionsMm.Count;
            }

            var sp = SpacingRe.Match(s);
            if (sp.Success)
            {
                double v = 1;
                if (sp.Groups["v"].Success && !TextUtil.TryParseNumber(sp.Groups["v"].Value, out v)) v = 1;
                b.SpacingMm = v * TextUtil.UnitToMm(sp.Groups["u"].Value);
                s = TextUtil.Blank(s, sp.Index, sp.Length);
            }

            double? length = ParseKeywordLength(ref s);
            if (b.Size == null) b.Size = ParseBareSize(ref s, true);
            if (length == null) length = ParseBareLength(ref s, b.Size != null);
            if (length != null) b.LengthMm = length.Value;

            b.Direction = ParseDirection(ref s);
            return b;
        }

        // ----------------------------------------------------------- helpers

        private static string IndefPattern()
        {
            var items = Lexicon.IndefinitePlural.OrderByDescending(x => x.Length).Select(Regex.Escape);
            return "(?:un paio di|a couple of|una coppia di|" + string.Join("|", items) + @")\b";
        }

        private static int IndefCount(string phrase)
        {
            phrase = phrase.Trim().ToLowerInvariant();
            if (phrase == "un paio di" || phrase == "a couple of" || phrase == "una coppia di") return 2;
            return 2;
        }

        private MepSize ParseExplicitSize(ref string s, ParseResult result)
        {
            var dn = DnRe.Match(s);
            if (dn.Success && TextUtil.TryParseNumber(dn.Groups["v"].Value, out var v))
            {
                s = TextUtil.Blank(s, dn.Index, dn.Length);
                return MepSize.Round(v, true);
            }

            var rect = RectRe.Match(s);
            if (rect.Success && TextUtil.TryParseNumber(rect.Groups["w"].Value, out var w) && TextUtil.TryParseNumber(rect.Groups["h"].Value, out var h))
            {
                var f = rect.Groups["u"].Success ? TextUtil.UnitToMm(rect.Groups["u"].Value) : 1;
                s = TextUtil.Blank(s, rect.Index, rect.Length);
                return MepSize.Rectangular(w * f, h * f);
            }

            var inch = InchRe.Match(s);
            if (inch.Success && TextUtil.TryParseNumber(inch.Groups["v"].Value, out var iv))
            {
                s = TextUtil.Blank(s, inch.Index, inch.Length);
                var best = InchToDn.OrderBy(t => Math.Abs(t.inch - iv)).First();
                if (Math.Abs(best.inch - iv) > 0.01) result.Warnings.Add(iv + "\" non è una misura standard: uso DN" + best.dn + ".");
                return MepSize.Round(best.dn, true);
            }

            var dia = DiaWordRe.Match(s);
            if (dia.Success && TextUtil.TryParseNumber(dia.Groups["v"].Value, out var dv))
            {
                s = TextUtil.Blank(s, dia.Index, dia.Length);
                var u = dia.Groups["u"].Success ? dia.Groups["u"].Value : "mm";
                if (u == "\"" || u.StartsWith("pollic") || u.StartsWith("inch"))
                {
                    var best = InchToDn.OrderBy(t => Math.Abs(t.inch - dv)).First();
                    return MepSize.Round(best.dn, true);
                }
                return MepSize.Round(dv * TextUtil.UnitToMm(u));
            }

            return null;
        }

        private MepSize ParseBareSize(ref string s, bool allowBare)
        {
            if (allowBare)
            {
                var bare = BareSizeRe.Match(s);
                if (bare.Success && TextUtil.TryParseNumber(bare.Groups["v"].Value, out var v))
                {
                    s = TextUtil.Blank(s, bare.Index, bare.Length);
                    var f = bare.Groups["u"].Success ? TextUtil.UnitToMm(bare.Groups["u"].Value) : 1;
                    return MepSize.Round(v * f);
                }
            }

            var mm = MmSizeRe.Match(s);
            if (mm.Success && TextUtil.TryParseNumber(mm.Groups["v"].Value, out var mv))
            {
                s = TextUtil.Blank(s, mm.Index, mm.Length);
                return MepSize.Round(mv);
            }
            return null;
        }

        private static double? ParseKeywordLength(ref string s)
        {
            var m = LengthKeyRe.Match(s);
            if (m.Success && TextUtil.TryParseNumber(m.Groups["v"].Value, out var v))
            {
                s = TextUtil.Blank(s, m.Index, m.Length);
                return v * TextUtil.UnitToMm(m.Groups["u"].Value);
            }
            m = LengthDiRe.Match(s);
            if (m.Success && TextUtil.TryParseNumber(m.Groups["v"].Value, out v))
            {
                s = TextUtil.Blank(s, m.Index, m.Length);
                return v * TextUtil.UnitToMm(m.Groups["u"].Value);
            }
            return null;
        }

        private static double? ParseBareLength(ref string s, bool sizeKnown)
        {
            var m = LengthBareRe.Match(s);
            if (m.Success && TextUtil.TryParseNumber(m.Groups["v"].Value, out var v))
            {
                s = TextUtil.Blank(s, m.Index, m.Length);
                return v * TextUtil.UnitToMm(m.Groups["u"].Value);
            }
            if (sizeKnown)
            {
                m = LengthBareMmRe.Match(s);
                if (m.Success && TextUtil.TryParseNumber(m.Groups["v"].Value, out v))
                {
                    s = TextUtil.Blank(s, m.Index, m.Length);
                    return v;
                }
            }
            return null;
        }

        private static DirectionKind ParseDirection(ref string s)
        {
            foreach (var kv in DirectionRes)
            {
                var m = kv.Key.Match(s);
                if (!m.Success) continue;
                s = TextUtil.Blank(s, m.Index, m.Length);
                return kv.Value;
            }
            return DirectionKind.Default;
        }

        // --------------------------------------------------------- validation

        private static void Validate(ParseResult result, ModelCatalog catalog)
        {
            var plan = result.Plan;
            for (var i = 0; i < plan.Runs.Count; i++)
            {
                var run = plan.Runs[i];
                var label = plan.Runs.Count > 1 ? "Tratto " + (i + 1) + ": " : string.Empty;

                // Categoria dedotta dalla forma o dal sistema quando manca il sostantivo.
                if (run.Size != null && run.Size.Shape == SizeShape.Rectangular)
                {
                    if (run.KindExplicit && run.Kind == MepKind.Pipe) result.Warnings.Add(label + "una tubazione non può essere rettangolare: creo un canale.");
                    run.Kind = MepKind.Duct;
                }
                else if (!run.KindExplicit && SystemClass.IsDuctClass(run.SystemClass)) run.Kind = MepKind.Duct;

                if (run.Size == null)
                {
                    run.Size = run.Kind == MepKind.Pipe ? MepSize.Round(50, true) : MepSize.Rectangular(300, 200);
                    result.Warnings.Add(label + "dimensione non indicata: uso " + run.Size + ". Scrivi ad esempio \"DN100\", \"diametro 160 mm\" o \"400x200\".");
                }

                if (run.LengthMm <= 0)
                {
                    run.LengthMm = 3000;
                    result.Warnings.Add(label + "lunghezza non valida: uso 3 m.");
                }

                if (run.SystemClass != null && run.Kind == MepKind.Pipe && SystemClass.IsDuctClass(run.SystemClass))
                {
                    result.Warnings.Add(label + "sistema \"" + run.SystemPhrase + "\" è aeraulico ma l'elemento è una tubazione: il sistema verrà scelto tra quelli idraulici.");
                    run.SystemClass = null;
                }
                if (run.SystemClass != null && run.Kind == MepKind.Duct && !SystemClass.IsDuctClass(run.SystemClass))
                {
                    result.Warnings.Add(label + "sistema \"" + run.SystemPhrase + "\" è idraulico ma l'elemento è un canale: il sistema verrà scelto tra quelli aeraulici.");
                    run.SystemClass = null;
                }

                for (var j = 0; j < run.Branches.Count; j++)
                {
                    var b = run.Branches[j];
                    var bl = label + "stacco " + (j + 1) + ": ";
                    if (b.Size == null)
                    {
                        b.Size = run.Size.Clone();
                        result.Warnings.Add(bl + "dimensione non indicata: uso quella del tratto principale (" + b.Size + ").");
                    }
                    if (b.Count < 1) b.Count = 1;
                    if (b.Count > 200)
                    {
                        result.Warnings.Add(bl + "numero di stacchi limitato a 200.");
                        b.Count = 200;
                    }
                    if (b.LengthMm <= 0) b.LengthMm = 500;

                    if (b.PositionsMm.Count == 0 && b.SpacingMm.HasValue && b.SpacingMm.Value * b.Count > run.LengthMm)
                    {
                        result.Warnings.Add(bl + b.Count + " stacchi con interasse " + MepSize.Fmt(b.SpacingMm.Value) + " mm non stanno in " + MepSize.Fmt(run.LengthMm) + " mm: alcuni verranno omessi.");
                    }
                    foreach (var p in b.PositionsMm)
                    {
                        if (p <= 0 || p >= run.LengthMm)
                            result.Warnings.Add(bl + "posizione " + MepSize.Fmt(p) + " mm fuori dal tratto: verrà ignorata.");
                    }

                    if (b.Size.Shape == SizeShape.Rectangular && run.Kind == MepKind.Pipe)
                        result.Warnings.Add(bl + "una tubazione non può avere stacchi rettangolari: uso il diametro " + b.Size.WidthMm + " mm.");
                }
            }
        }
    }
}
