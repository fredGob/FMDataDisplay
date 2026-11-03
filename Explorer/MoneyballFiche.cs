// Mode Moneyball : fiche joueur (écran « PlayerReport »).
//
// Le cadre « Attributs » de la fiche (#PlayerAttributesBlock) contient, de gauche à droite : le terrain des postes,
// le tableau des attributs (modèle UXML « AttributesTableTemplate », variante gardien comprise) suivi de sa légende
// (« AttributesKey »), puis le bloc « Infos ». On cache le tableau et sa légende, et on met à leur place un bloc à nous :
// note Moneyball, puis une colonne par profil du joueur avec la note sur 100 de chaque stat (la plus importante
// du profil en premier), puis les stats « pour info », hors note (NoteEngine.InfoStats : gardien).
//
// Joueur affiché : lu dans le système de liaison de l'interface (BindingSubsystem) : nœud de l'écran (Panel.Key),
// puis ses enfants jusqu'à trouver une référence « person » (dref).
// Notes : BepInEx/config/fm27_notes_detail.tsv (écrit par le calcul, après F9 ou chaque semaine) ; s'il manque,
// il est calculé une fois à partir de la dernière extraction (quelques secondes, en arrière-plan).
// Vérification régulière tant que la fiche est ouverte (le jeu peut reconstruire le cadre ou changer de joueur).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using SI.Bindable;
using SI.Bindable.Reference.Core;
using SI.Interop;
using UnityEngine.UIElements;
using FM27Note;
using Color = UnityEngine.Color;

namespace FM27Explorer;

internal static partial class Moneyball
{
    internal static string DetailPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27_notes_detail.tsv");
    private static string StatePath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27_notes_etat.txt");
    private static readonly string[] ReportScreens = { "PlayerReport" };
    private const string TableTemplate = "AttributesTableTemplate", LegendTemplate = "AttributesKey";
    private const int MaxRows = 14;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── Détail des notes (fm27_notes_detail.tsv) ──
    private sealed class StatLine { public string Stat; public double Score, Value = double.NaN; }
    private sealed class ProfLine { public string Profile; public double Raw, Final; public List<StatLine> Stats = new(), Info = new(); }
    private sealed class Detail { public double Minutes, Factor; public List<ProfLine> Profiles = new(); }

    private static Dictionary<int, Detail> _details;            // dref -> détail
    private static Task<Dictionary<int, Detail>> _detailTask;
    private static DateTime _detailFileTime, _nextDetailCheck;
    private static int _detailVersion;
    private static bool _computeTried;
    private static string _detailStatus = "pas encore lu";

    // Appelé après chaque calcul des notes : le fichier sera relu
    public static void OnNotesReloaded() => _nextDetailCheck = DateTime.MinValue;

    private static void EnsureDetails()
    {
        if (_detailTask != null)
        {
            if (!_detailTask.IsCompleted) return;
            var t = _detailTask;
            _detailTask = null;
            if (t.IsFaulted)
            {
                _detailStatus = "erreur : " + t.Exception?.GetBaseException().Message;
                Plugin.L.LogWarning("Moneyball : détail des notes : " + _detailStatus);
            }
            else
            {
                _details = t.Result;
                _detailVersion++;
                _detailStatus = $"{_details.Count} joueurs";
                try { _detailFileTime = File.GetLastWriteTimeUtc(DetailPath); } catch { }
                Plugin.L.LogMessage($"Moneyball : détail des notes lu ({_details.Count} joueurs notés).");
            }
            return;
        }
        var now = DateTime.Now;
        if (now < _nextDetailCheck) return;
        _nextDetailCheck = now.AddSeconds(5);
        var path = DetailPath;
        if (File.Exists(path))
        {
            var time = File.GetLastWriteTimeUtc(path);
            if (_details != null && time == _detailFileTime) return;
            _detailStatus = "lecture en cours";
            _detailTask = Task.Run(() => LoadDetails(path));
            return;
        }
        if (_computeTried) return;
        _computeTried = true;
        // Notes calculées avant cette version : pas encore de détail ; calcul à partir de l'extraction des notes actuelles
        var dir = Plugin.ExportDir;
        var stamp = NotesStamp() ?? NoteData.LatestStamp(dir);
        if (stamp == null) { _detailStatus = "aucune extraction : appuyer sur F9"; return; }
        _detailStatus = "calcul en cours";
        Plugin.L.LogMessage($"Moneyball : détail des notes absent, calcul à partir de l'extraction {stamp} (quelques secondes)…");
        _detailTask = Task.Run(() =>
        {
            var data = NoteData.Load(dir, stamp);
            var settings = new NoteSettings();
            NoteEngine.ComputeAll(data.Players, settings);
            NoteData.WriteGameDetails(path, data.Players, settings);
            return LoadDetails(path);
        });
    }

    // Extraction qui a servi aux notes actuelles (fm27_notes_etat.txt, « extraction=… »)
    private static string NotesStamp()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            foreach (var line in File.ReadAllLines(StatePath))
                if (line.StartsWith("extraction=")) return line.Substring(11).Trim();
        }
        catch { }
        return null;
    }

    private static Dictionary<int, Detail> LoadDetails(string path)
    {
        var d = new Dictionary<int, Detail>();
        bool header = true;
        foreach (var line in File.ReadLines(path))
        {
            if (header) { header = false; continue; }
            var c = line.Split('\t');
            if (c.Length < 3 || !int.TryParse(c[0], NumberStyles.Integer, Inv, out var idx)) continue;
            var det = new Detail { Minutes = Num(c[1]), Factor = Num(c[2]) };
            for (int i = 3; i < c.Length; i++)
            {
                var parts = c[i].Split('|');
                if (parts.Length < 4) continue;
                var pl = new ProfLine { Profile = parts[0], Raw = Num(parts[1]), Final = Num(parts[2]) };
                ParseStats(parts[3], pl.Stats);
                if (parts.Length > 4) ParseStats(parts[4], pl.Info);
                det.Profiles.Add(pl);
            }
            d[Probe.PersonDrefBase + idx] = det;
        }
        return d;
    }

    private static void ParseStats(string text, List<StatLine> into)
    {
        foreach (var st in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = st.Split(':');
            if (f.Length < 2) continue;
            into.Add(new StatLine { Stat = f[0], Score = Num(f[1]), Value = f.Length > 2 ? Num(f[2]) : double.NaN });
        }
    }

    private static double Num(string s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : double.NaN;

    // ── Fiches joueur ouvertes ──
    private sealed class Report
    {
        public SI.Bindable.Panel Panel;
        public VisualElement Table, Legend, Host, Block;
        public string Shown;              // contenu affiché (joueur + version des notes)
        public DateTime NextCheck, ClosedSince;
        public bool Closed, Placed;
    }

    private static readonly Dictionary<IntPtr, Report> Reports = new();
    private static int _logs, _treeDumps;

    // Un écran s'ouvre ou devient actif : si c'est une fiche joueur, on la suit
    private static void OnReportPanel(SI.Bindable.Panel p)
    {
        if (Array.IndexOf(ReportScreens, ScreenName(p)) < 0) return;
        if (!Reports.TryGetValue(p.Pointer, out var r)) Reports[p.Pointer] = r = new Report { Panel = p };
        r.NextCheck = DateTime.Now.AddSeconds(0.1);
        r.Placed = false;
    }

    private static void TickReports()
    {
        if (Reports.Count == 0) return;
        EnsureDetails();
        var now = DateTime.Now;
        List<IntPtr> gone = null;
        foreach (var kv in Reports)
        {
            var r = kv.Value;
            if (r.NextCheck > now) continue;
            try { r.Placed = Check(r); }
            catch (Exception e) { r.Placed = false; Counters.Note("Moneyball.Check", e); }
            // Rapide tant que notre bloc n'est pas en place (le moins longtemps possible avec les attributs visibles)
            r.NextCheck = now.AddSeconds(r.Placed ? 1.0 : 0.2);
            if (r.Closed && (now - r.ClosedSince).TotalSeconds > 60) (gone ??= new()).Add(kv.Key);
        }
        if (gone != null) foreach (var k in gone) Reports.Remove(k);
    }

    // Vérifie la fiche : tableau caché, notre bloc en place et à jour. Vrai si tout est en place.
    private static bool Check(Report r)
    {
        var p = r.Panel;
        bool open = p.panel != null && p.CurrentState == SI.Bindable.Panel.State.Active;
        if (!open)
        {
            if (!r.Closed) { r.Closed = true; r.ClosedSince = DateTime.Now; }
            return true;
        }
        r.Closed = false;

        if (!Attached(r.Table, p) || r.Host == null || !Attached(r.Host, p)) FindTable(r);
        if (r.Table == null) return false;
        Hide(r.Table);
        if (r.Legend != null) Hide(r.Legend);

        if (r.Block == null || r.Block.parent == null || r.Block.parent.Pointer != r.Host.Pointer)
        {
            r.Block = Column("fm27-moneyball", "flex-grow-class", "align-self-stretch");
            r.Block.style.flexGrow = new StyleFloat(1f);
            r.Host.Insert(0, r.Block);
            r.Shown = null;
        }

        int dref = FindPerson(r);
        string shown = $"{dref}/{_detailVersion}/{_detailStatus}";
        if (shown != r.Shown)
        {
            Fill(r.Block, dref);
            r.Shown = shown;
            if (_logs++ < 30)
                Plugin.L.LogInfo($"Moneyball : fiche joueur {(dref == 0 ? "(joueur non identifié)" : $"index {dref - Probe.PersonDrefBase}")} : " +
                                 $"attributs remplacés (notes : {_detailStatus}).");
        }
        return true;
    }

    private static bool Attached(VisualElement e, VisualElement root)
    {
        if (e == null) return false;
        try
        {
            if (e.panel == null) return false;
            var x = e;
            for (int i = 0; i < 300 && x != null; i++, x = x.parent)
                if (x.Pointer == root.Pointer) return true;
        }
        catch { }
        return false;
    }

    // Recherche du tableau des attributs (TemplateContainer « AttributesTableTemplate ») et de sa légende
    private static void FindTable(Report r)
    {
        r.Table = r.Legend = r.Host = null;
        var stack = new Stack<VisualElement>();
        stack.Push(r.Panel);
        int seen = 0;
        while (stack.Count > 0 && seen < 30000)
        {
            var e = stack.Pop();
            if (e == null) continue;
            seen++;
            if (TemplateId(e) == TableTemplate)
            {
                r.Table = e;
                r.Host = e.parent;
                break;
            }
            int n = 0;
            try { n = e.hierarchy.childCount; } catch { }
            for (int i = n - 1; i >= 0; i--)
            {
                try { stack.Push(e.hierarchy[i]); } catch { }
            }
        }
        if (r.Host == null) { r.Table = null; return; }
        int m = r.Host.hierarchy.childCount;
        for (int i = 0; i < m; i++)
        {
            var c = r.Host.hierarchy[i];
            if (TemplateId(c) == LegendTemplate) r.Legend = c;
        }
    }

    private static string TemplateId(VisualElement e)
    {
        if (ClassName(IL2CPP.il2cpp_object_get_class(e.Pointer)) != "TemplateContainer") return null;
        try { return e.TryCast<TemplateContainer>()?.templateId; } catch { return null; }
    }

    private static void Hide(VisualElement e)
    {
        try
        {
            if (e.resolvedStyle.display != DisplayStyle.None) e.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
        }
        catch (Exception ex) { Counters.Note("Moneyball.Hide", ex); }
    }

    // ── Joueur affiché : référence « person » dans les nœuds de liaison de l'écran ──
    private static int FindPerson(Report r)
    {
        var bs = FM.UI.EmbeddedDataHandler.s_bindingSubsystem;
        if (bs == null) return 0;
        var nodes = bs.m_nodes;
        if (nodes == null) return 0;
        var keys = new List<ulong>();
        try { keys.Add(r.Panel.Key.m_key); } catch { }
        try
        {
            var h = r.Panel.hierarchy;
            for (int i = 0; i < h.childCount; i++)
            {
                var br = h[i].TryCast<BindingRoot>();
                if (br != null) keys.Add(br.Key.m_key);
            }
        }
        catch { }

        var log = _treeDumps < 3 ? new StringBuilder() : null;
        int result = 0;
        foreach (var k in keys.Distinct())
        {
            log?.AppendLine($"== nœud de départ 0x{k:X16}");
            if (k == 0 || !nodes.ContainsKey(k)) { log?.AppendLine("   (absent)"); continue; }
            result = SearchPerson(nodes[k], bs, log);
            if (result != 0 && log == null) break;
        }
        if (log != null)
        {
            _treeDumps++;
            try
            {
                var dir = Path.Combine(Plugin.ExportDir, "ecrans");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"FM27_Liaisons_{Safe(ScreenName(r.Panel))}_{Plugin.Stamp}.txt");
                File.WriteAllText(path, $"# Joueur trouvé : {(result == 0 ? "aucun" : $"index {result - Probe.PersonDrefBase}")}\n" + log,
                                  new UTF8Encoding(true));
                Plugin.L.LogInfo($"Moneyball : nœuds de liaison de la fiche écrits dans {path}");
            }
            catch (Exception e) { Plugin.L.LogWarning("Moneyball : nœuds de liaison : " + e.Message); }
        }
        return result;
    }

    // Parcours en largeur (enfants et alias), au plus 400 nœuds sur 4 niveaux
    private static int SearchPerson(Bindings.Node root, BindingSubsystem bs, StringBuilder log)
    {
        var queue = new Queue<(Bindings.Node n, int depth, string path)>();
        queue.Enqueue((root, 0, ""));
        var seen = new HashSet<IntPtr>();
        int visited = 0, result = 0;
        while (queue.Count > 0 && visited < 400)
        {
            var (n, depth, path) = queue.Dequeue();
            if (n == null || !seen.Add(n.Pointer)) continue;
            visited++;
            string name = Try(() => n.Name);
            string full = path.Length == 0 ? name : path + "." + name;
            InteropReference refr = null;
            try { refr = n.Value?.Get()?.TryCast<InteropReference>(); } catch { }
            log?.Append(' ', depth * 2 + 2).Append(name.Length == 0 ? "(sans nom)" : name)
                .Append(refr != null ? " = " + Names.Describe(refr) : "").AppendLine();
            if (refr != null && result == 0 && Names.TypeOf(refr) == "person")
            {
                result = DrefOf(refr);
                if (result != 0) log?.AppendLine($"  -> joueur : {full}");
                if (result != 0 && log == null) break;
            }
            if (depth >= 4) continue;
            try
            {
                ulong a = n.Alias.m_key;
                if (a != 0 && bs.m_nodes.ContainsKey(a)) queue.Enqueue((bs.m_nodes[a], depth + 1, full + "@"));
            }
            catch { }
            try { for (var c = n.FirstChild; c != null; c = c.NextSibling) queue.Enqueue((c, depth + 1, full)); }
            catch { }
        }
        return result;
    }

    private static int DrefOf(InteropReference r)
    {
        var ids = r.m_id;
        for (int i = 0; ids != null && i < ids.Count; i++)
            if (ids[i].m_id == Probe.KeyDref) return ids[i].m_value;
        return 0;
    }

    // ── Notre bloc ──
    private static void Fill(VisualElement b, int dref)
    {
        b.Clear();
        var head = Row(null, "justify-content-space-between", "align-items-center");
        head.style.marginBottom = new StyleLength(8f);
        b.Add(head);
        var left = Row(null, "align-items-center");
        head.Add(left);
        left.Add(Text("MONEYBALL", "secondary-text-colour", "condensed-xs-regular"));

        Detail det = null;
        if (dref != 0 && _details != null) _details.TryGetValue(dref, out det);
        if (det == null || det.Profiles.Count == 0)
        {
            string why = dref == 0 ? "Joueur non identifié."
                : _details == null ? $"Notes en préparation ({_detailStatus})…"
                : "Pas de note : moins de 200 minutes jouées, ou joueur absent de la dernière extraction (F9).";
            var msg = Text(why, "secondary-text-colour", "body-md");
            msg.style.marginTop = new StyleLength(12f);
            msg.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            b.Add(msg);
            return;
        }

        var best = det.Profiles[0];
        var big = Text(Score(best.Final), "body-md-strong");
        big.style.fontSize = new StyleLength(22f);
        big.style.color = new StyleColor(ScoreColour(best.Final));
        big.style.marginLeft = new StyleLength(10f);
        big.style.marginRight = new StyleLength(6f);
        left.Add(big);
        left.Add(Text(ProfileLabel(best.Profile), "white", "body-md"));
        head.Add(Text($"{det.Minutes.ToString("#,0", Inv).Replace(',', ' ')} min jouées", "secondary-text-colour", "body-sm"));

        var cols = Row(null, "flex-grow-class", "align-items-start");
        cols.style.flexGrow = new StyleFloat(1f);
        b.Add(cols);
        int k = 0;
        foreach (var pl in det.Profiles.Take(3))
        {
            var col = Column(null, "flex-grow-class", "flex-basis-0");
            col.style.flexGrow = new StyleFloat(1f);
            col.style.flexBasis = new StyleLength(0f);
            if (k++ > 0) col.style.marginLeft = new StyleLength(20f);
            cols.Add(col);

            var title = Row(null, "justify-content-space-between", "align-items-center");
            title.style.height = new StyleLength(22f);
            title.Add(Text(ProfileLabel(pl.Profile).ToUpperInvariant(), "secondary-text-colour", "condensed-xs-regular"));
            var t = Text(Score(pl.Final), "body-md-strong");
            t.style.color = new StyleColor(ScoreColour(pl.Final));
            title.Add(t);
            col.Add(title);
            // Note finale = note des stats × facteur du championnat (réputation) : expliqué quand il change la note
            if (!double.IsNaN(det.Factor) && det.Factor < 0.995)
            {
                var why = Text($"{Score(pl.Raw)} sur ses stats, × {Dec(det.Factor, 2)} pour le niveau du championnat",
                               "secondary-text-colour", "body-sm");
                why.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
                why.style.marginBottom = new StyleLength(2f);
                col.Add(why);
            }
            var line = new VisualElement();
            line.style.height = new StyleLength(1f);
            line.style.backgroundColor = new StyleColor(new Color(1f, 1f, 1f, 0.15f));
            line.style.marginBottom = new StyleLength(2f);
            col.Add(line);

            var weights = SafeWeights(pl.Profile);
            var byStat = new Dictionary<string, StatLine>();
            foreach (var s in pl.Stats) byStat[s.Stat] = s;
            int rows = 0, rest = 0;
            foreach (var (stat, w) in weights.Where(w => w.w > 0).OrderByDescending(w => w.w))
            {
                if (!byStat.TryGetValue(stat, out var s)) continue;
                if (rows >= MaxRows) { rest++; continue; }
                rows++;
                col.Add(StatRow(StatLabel(stat), s, stat));
            }
            if (rest > 0) col.Add(Text($"… et {rest} autre(s) stat(s)", "secondary-text-colour", "body-sm"));
            if (pl.Info.Count > 0)
            {
                var sub = Text("POUR INFO (HORS NOTE)", "secondary-text-colour", "condensed-xs-regular");
                sub.style.marginTop = new StyleLength(10f);
                sub.style.marginBottom = new StyleLength(2f);
                col.Add(sub);
                foreach (var s in pl.Info) col.Add(StatRow(StatLabel(s.Stat), s, s.Stat));
            }
        }
    }

    private static VisualElement StatRow(string label, StatLine s, string stat)
    {
        var row = Row(null, "justify-content-space-between", "align-items-center");
        row.style.height = new StyleLength(24f);
        var name = Text(label, "content-no-wrap", "content-ellipsis", "white", "body-md");
        name.style.flexGrow = new StyleFloat(1f);
        name.style.flexShrink = new StyleFloat(1f);
        row.Add(name);
        row.Add(Fixed(Text(Value(stat, s.Value), "secondary-text-colour", "body-sm"), 50));
        var sc = Fixed(Text(Score(s.Score), "body-md-strong"), 30);
        sc.style.color = new StyleColor(ScoreColour(s.Score));
        row.Add(sc);
        return row;
    }

    private static Label Fixed(Label l, float width)
    {
        l.style.width = new StyleLength(width);
        l.style.flexShrink = new StyleFloat(0f);
        l.style.unityTextAlign = new StyleEnum<UnityEngine.TextAnchor>(UnityEngine.TextAnchor.MiddleRight);
        return l;
    }

    private static (string stat, double w)[] SafeWeights(string profile)
    {
        try { return NoteEngine.Weights(profile, "Complet"); }
        catch { return Array.Empty<(string, double)>(); }
    }

    private static string ProfileLabel(string p) => NoteEngine.ProfileLabels.TryGetValue(p, out var l) ? l : p;
    private static string StatLabel(string s) => NoteEngine.Catalog.TryGetValue(s, out var d) && !string.IsNullOrEmpty(d.Label) ? d.Label : s;
    private static string Score(double v) => double.IsNaN(v) ? "–" : Math.Round(v).ToString("0", Inv);
    private static string Dec(double v, int n) => double.IsNaN(v) ? "–" : v.ToString("F" + n, Inv).Replace('.', ',');

    private static string Value(string stat, double v)
    {
        if (double.IsNaN(v)) return "";
        if (stat.Contains("Percentage")) return Dec(v, 0) + " %";
        return Dec(v, Math.Abs(v) >= 10 ? 1 : 2);
    }

    // Couleur d'une note 0–100 (du rouge au vert, comme les attributs du jeu)
    private static Color ScoreColour(double v)
    {
        if (double.IsNaN(v)) return new Color(0.6f, 0.6f, 0.6f);
        if (v >= 80) return new Color(0.24f, 0.81f, 0.56f);
        if (v >= 65) return new Color(0.61f, 0.83f, 0.35f);
        if (v >= 50) return new Color(0.91f, 0.83f, 0.30f);
        if (v >= 35) return new Color(0.94f, 0.63f, 0.29f);
        return new Color(0.90f, 0.33f, 0.29f);
    }

    // Éléments : classes de style du jeu (mêmes polices et couleurs que la fiche) + quelques réglages directs
    private static VisualElement Row(string name, params string[] classes)
    {
        var e = El(name, classes);
        e.AddToClassList("row-direction-normal");
        e.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
        return e;
    }

    private static VisualElement Column(string name, params string[] classes)
    {
        var e = El(name, classes);
        e.AddToClassList("column-direction-normal");
        e.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Column);
        return e;
    }

    private static VisualElement El(string name, string[] classes)
    {
        var e = new VisualElement();
        if (name != null) e.name = name;
        e.pickingMode = PickingMode.Ignore;
        foreach (var c in classes) e.AddToClassList(c);
        return e;
    }

    private static Label Text(string text, params string[] classes)
    {
        var l = new Label(text);
        l.pickingMode = PickingMode.Ignore;
        l.AddToClassList("sitext");
        foreach (var c in classes) l.AddToClassList(c);
        return l;
    }
}
