// Mode Moneyball (branche moneyball) : sur la fiche joueur, le tableau des attributs est remplacé par nos notes
// (note globale, notes par profil, note sur 100 de chaque stat). Affichage : MoneyballFiche.cs.
//
// Ce fichier : réglages, crochets sur les écrans du jeu, et exploration. Chaque écran est un SI.Bindable.Panel
// (élément d'interface Unity) ; avec exploration=1, quand il devient actif, on attend quelques secondes (le temps
// que les valeurs arrivent du moteur), puis on écrit son arbre d'éléments dans
// Documents/Sports Interactive/Football Manager 27/exports/ecrans/FM27_Ecran_<écran>_<date>.txt :
// type d'élément, nom, classes de style, modèle UXML, taille, texte affiché et chemins de liaison (binding.xxx).
//
// Réglage : BepInEx/config/fm27explorer_moneyball.txt (moneyball=1, exploration=1), lu au lancement du jeu.
// Les crochets ne sont installés que si l'un des deux est actif.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using SI.Bindable;
using UnityEngine.UIElements;

namespace FM27Explorer;

internal static partial class Moneyball
{
    private static string ConfigPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27explorer_moneyball.txt");

    private const string DefaultConfig =
@"# Mode Moneyball (FM27 Explorer, en développement)
# moneyball=1 : sur la fiche joueur, le tableau des attributs est remplacé par nos notes (note sur 100 de chaque stat).
#               Pris en compte au lancement du jeu.
moneyball=0
# exploration=1 : à chaque écran ouvert (fiche joueur…), sa structure est écrite dans
#                 Documents/Sports Interactive/Football Manager 27/exports/ecrans/ (rien n'est modifié à l'écran).
#                 Pris en compte au lancement du jeu. 0 = désactivé.
exploration=0
# nombre d'enregistrements par écran et par lancement du jeu
par_ecran=3
# secondes d'attente après l'ouverture d'un écran (le temps que les valeurs s'affichent)
attente=3
";

    private static bool _loaded, _explore, _moneyball;
    private static int _perScreen = 3, _maxFiles = 150;
    private static double _delay = 3;

    public static bool Exploring { get { if (!_loaded) LoadConfig(); return _explore; } }
    public static bool Enabled { get { if (!_loaded) LoadConfig(); return _moneyball; } }

    private static void LoadConfig()
    {
        _loaded = true;
        try
        {
            if (!File.Exists(ConfigPath)) File.WriteAllText(ConfigPath, DefaultConfig, new UTF8Encoding(false));
            foreach (var raw in File.ReadAllLines(ConfigPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var k = line.Substring(0, eq).Trim().ToLowerInvariant();
                var v = line.Substring(eq + 1).Trim().ToLowerInvariant();
                switch (k)
                {
                    case "exploration": _explore = v is "1" or "oui" or "true"; break;
                    case "moneyball": _moneyball = v is "1" or "oui" or "true"; break;
                    case "par_ecran": if (int.TryParse(v, out var n) && n > 0) _perScreen = n; break;
                    case "attente": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0) _delay = d; break;
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Moneyball : réglages : " + e.Message); }
        Plugin.L.LogMessage((_moneyball ? "Moneyball : ACTIF (fiche joueur : attributs remplacés par les notes). " : "Moneyball : désactivé. ") +
                            (_explore ? $"Exploration ACTIVE (structure des écrans écrite dans exports/ecrans, {_perScreen} fois par écran au plus)."
                                      : "Exploration désactivée."));
    }

    // ── Écrans à enregistrer ──
    private sealed class Pending { public SI.Bindable.Panel Panel; public DateTime Due; public string Why; }
    private static readonly Dictionary<IntPtr, Pending> Queue = new();
    private static readonly Dictionary<string, int> DoneByScreen = new(StringComparer.Ordinal);
    private static int _files;

    // Appelé par les crochets quand un écran s'ouvre ou devient actif
    public static void OnPanel(SI.Bindable.Panel p, string why)
    {
        if (p == null) return;
        if (_moneyball) { try { OnReportPanel(p); } catch (Exception e) { Counters.Note("Moneyball.OnReportPanel", e); } }
        if (!_explore) return;
        try
        {
            var key = p.Pointer;
            var due = DateTime.Now.AddSeconds(_delay);
            if (Queue.TryGetValue(key, out var q)) { q.Due = due; q.Why = why; return; }   // repoussé : on attend la fin des changements
            Queue[key] = new Pending { Panel = p, Due = due, Why = why };
            Plugin.L.LogInfo($"Moneyball : écran {why} : {Describe(p)}");
        }
        catch (Exception e) { Counters.Note("Moneyball.OnPanel", e); }
    }

    public static void Tick()
    {
        if (_moneyball) { try { TickReports(); } catch (Exception e) { Counters.Note("Moneyball.TickReports", e); } }
        if (!_explore || Queue.Count == 0) return;
        var now = DateTime.Now;
        List<IntPtr> ready = null;
        foreach (var kv in Queue) if (kv.Value.Due <= now) (ready ??= new()).Add(kv.Key);
        if (ready == null) return;
        foreach (var k in ready)
        {
            var q = Queue[k];
            Queue.Remove(k);
            try { Dump(q.Panel, q.Why); }
            catch (Exception e) { Plugin.L.LogWarning("Moneyball : enregistrement de l'écran : " + e.Message); }
        }
    }

    private static string ScreenName(SI.Bindable.Panel p)
    {
        string n = Try(() => p.PanelID?.name);
        if (string.IsNullOrEmpty(n)) n = Try(() => p.InstanceName);
        if (string.IsNullOrEmpty(n)) n = Try(() => p.name);
        return string.IsNullOrEmpty(n) ? "inconnu" : n;
    }

    private static string Describe(SI.Bindable.Panel p) =>
        $"« {ScreenName(p)} » (id {Try(() => p.PanelID?.ID.ToString())}, titre {Try(() => p.PanelID?.Title.ToString())}, " +
        $"instance {Try(() => p.InstanceName)}, état {Try(() => p.CurrentState.ToString())}, " +
        $"uxml {Try(() => p.visualTreeAssetSource?.name)})";

    private static void Dump(SI.Bindable.Panel p, string why)
    {
        if (Try(() => p.panel == null ? "x" : "") == "x") return;   // écran déjà fermé
        var screen = ScreenName(p);
        DoneByScreen.TryGetValue(screen, out var done);
        if (done >= _perScreen || _files >= _maxFiles) return;
        DoneByScreen[screen] = done + 1;
        _files++;

        var dir = Path.Combine(Plugin.ExportDir, "ecrans");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"FM27_Ecran_{Safe(screen)}_{Plugin.Stamp}.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"# Écran {Describe(p)}");
        sb.AppendLine($"# Enregistré {_delay:F0} s après : {why} ; date {DateTime.Now:yyyy-MM-dd HH:mm:ss} ; date du jeu {GameDate.Text}");
        sb.AppendLine("# Ligne = profondeur (indentation), type, #nom, .classes, [uxml:modèle], [largeur x hauteur], [masqué], « texte », {liaisons}");
        int count = 0, texts = 0;
        Walk(p, 0, null, sb, ref count, ref texts);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        Plugin.L.LogMessage($"Moneyball : écran « {screen} » enregistré ({count} éléments, {texts} textes) -> {path}");
    }

    private const int MaxElements = 30000, MaxDepth = 150;

    private static void Walk(VisualElement e, int depth, string parentUxml, StringBuilder sb, ref int count, ref int texts)
    {
        if (e == null || count >= MaxElements) return;
        count++;
        IntPtr ptr = e.Pointer;
        IntPtr klass = IL2CPP.il2cpp_object_get_class(ptr);
        sb.Append(' ', depth * 2).Append(ClassName(klass));

        string name = Try(() => e.name);
        if (!string.IsNullOrEmpty(name)) sb.Append(" #").Append(name);
        try
        {
            var cl = e.classList;
            for (int i = 0; cl != null && i < cl.Count; i++) sb.Append(" .").Append(cl[i]);
        }
        catch { }
        string uxml = Try(() => e.visualTreeAssetSource?.name);
        if (!string.IsNullOrEmpty(uxml) && uxml != parentUxml) sb.Append(" [uxml:").Append(uxml).Append(']');
        try
        {
            var r = e.layout;
            if (r.width > 0 || r.height > 0) sb.Append($" [{r.width:F0}x{r.height:F0}]");
        }
        catch { }
        try { if (e.resolvedStyle.display == DisplayStyle.None) sb.Append(" [masqué]"); } catch { }
        try
        {
            var t = e.TryCast<TextElement>();
            if (t != null)
            {
                var s = t.text;
                if (!string.IsNullOrEmpty(s))
                {
                    texts++;
                    s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
                    if (s.Length > 120) s = s.Substring(0, 120) + "…";
                    sb.Append(" « ").Append(s).Append(" »");
                }
            }
        }
        catch { }
        AppendBindings(ptr, klass, sb);
        sb.AppendLine();

        if (depth >= MaxDepth) return;
        int n = 0;
        try { n = e.hierarchy.childCount; } catch { }
        for (int i = 0; i < n && count < MaxElements; i++)
        {
            VisualElement c = null;
            try { c = e.hierarchy[i]; } catch { }
            Walk(c, depth + 1, string.IsNullOrEmpty(uxml) ? parentUxml : uxml, sb, ref count, ref texts);
        }
    }

    // ── Liaisons : champs BindingPath (chemin « binding.xxx ») et textes utiles des classes du jeu ──
    // Lecture directe de la mémoire (mêmes règles que Colonne.SetSortPath) ; liste des champs gardée par classe.
    private sealed class Slot { public string Name; public int Offset; }
    private static readonly Dictionary<IntPtr, List<Slot>> SlotsByClass = new();
    private static int _pathInStruct = int.MinValue;

    private static List<Slot> Slots(IntPtr klass)
    {
        if (SlotsByClass.TryGetValue(klass, out var list)) return list;
        list = new List<Slot>();
        if (_pathInStruct == int.MinValue)
        {
            IntPtr pf = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<BindingPath>.NativeClassPtr, "m_path");
            _pathInStruct = pf == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(pf) - 2 * IntPtr.Size;
        }
        for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            var kn = ClassName(k);
            if (kn is "VisualElement" or "Focusable" or "CallbackEventHandler" or "Object" or "TextElement") break;
            IntPtr iter = IntPtr.Zero, f;
            while ((f = IL2CPP.il2cpp_class_get_fields(k, ref iter)) != IntPtr.Zero)
            {
                if ((IL2CPP.il2cpp_field_get_flags(f) & 0x10) != 0) continue;   // champ statique
                var fname = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(f)) ?? "";
                var tname = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_field_get_type(f))) ?? "";
                int off = (int)IL2CPP.il2cpp_field_get_offset(f);
                if (tname == "SI.Bindable.BindingPath" && _pathInStruct >= 0) list.Add(new Slot { Name = fname, Offset = off + _pathInStruct });
                else if (tname == "System.String" && Interesting(fname)) list.Add(new Slot { Name = fname, Offset = off });
            }
        }
        SlotsByClass[klass] = list;
        return list;
    }

    private static bool Interesting(string field)
    {
        var f = field.ToLowerInvariant();
        return f.Contains("binding") || f.Contains("path") || f.Contains("template") || f.Contains("uxml") || f.Contains("property")
               || f.Contains("parameter") || f.Contains("key");
    }

    private static unsafe void AppendBindings(IntPtr obj, IntPtr klass, StringBuilder sb)
    {
        List<Slot> slots;
        try { slots = Slots(klass); } catch { return; }
        bool first = true;
        foreach (var s in slots)
        {
            string v = null;
            try
            {
                IntPtr str = *(IntPtr*)((byte*)obj + s.Offset);
                if (str != IntPtr.Zero) v = IL2CPP.Il2CppStringToManaged(str);
            }
            catch { }
            if (string.IsNullOrEmpty(v)) continue;
            sb.Append(first ? " {" : "; ").Append(s.Name.TrimStart('_').Replace("k__BackingField", "").TrimEnd('_', '>').TrimStart('<'))
              .Append('=').Append(v.Length > 160 ? v.Substring(0, 160) + "…" : v);
            first = false;
        }
        if (!first) sb.Append('}');
    }

    private static readonly Dictionary<IntPtr, string> ClassNames = new();
    private static string ClassName(IntPtr klass)
    {
        if (klass == IntPtr.Zero) return "?";
        if (ClassNames.TryGetValue(klass, out var n)) return n;
        n = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "?";
        ClassNames[klass] = n;
        return n;
    }

    private static string Try(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length > 60 ? sb.ToString(0, 60) : sb.ToString();
    }
}

// Crochets de l'exploration (installés seulement avec exploration=1)
[HarmonyPatch]
internal static class MoneyballPatches
{
    // Changement d'état d'un écran (Inactive -> Initialising -> Binding -> Active)
    [HarmonyPatch(typeof(SI.Bindable.Panel), nameof(SI.Bindable.Panel.SetState))]
    [HarmonyPostfix]
    private static void SetStatePostfix(SI.Bindable.Panel __instance)
    {
        using var perf = Perf.Measure();
        try
        {
            if (__instance != null && __instance.CurrentState == SI.Bindable.Panel.State.Active) Moneyball.OnPanel(__instance, "actif");
        }
        catch (Exception e) { Counters.Note("Moneyball.SetState", e); }
    }

    // Affichage d'un écran (au cas où le changement d'état n'est pas vu)
    [HarmonyPatch(typeof(SI.Bindable.Panel), nameof(SI.Bindable.Panel.Show))]
    [HarmonyPostfix]
    private static void ShowPostfix(SI.Bindable.Panel __instance)
    {
        using var perf = Perf.Measure();
        try { Moneyball.OnPanel(__instance, "affiché"); }
        catch (Exception e) { Counters.Note("Moneyball.Show", e); }
    }
}
