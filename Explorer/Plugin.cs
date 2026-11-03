// FM27 Explorer — plugin BepInEx d'exploration du moteur de Football Manager 27.
//
// But : comprendre comment l'interface Unity demande les données (joueurs, attributs,
// stats, recherches) au moteur natif (game_plugin.dll), afin de pouvoir ensuite
// lire ces données nous-mêmes, sans passer par le tableau limité à 10 000 lignes.
//
//   F8 : écrit le dictionnaire des identifiants (propriétés, références, contextes)
//   F7 : démarre / arrête l'enregistrement des canaux ouverts par l'interface
//
// Fichiers écrits dans : Documents/Sports Interactive/Football Manager 27/exports/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using FM.GamePlugin;
using HarmonyLib;
using SI.Bindable.Reference.Core;
using SI.Interop;
using DateTime = System.DateTime;
using TypedValue = SI.Core.TypedValue;
using UnityEngine;

namespace FM27Explorer;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "fr.visiondujeu.fm27explorer";
    public const string Name = "FM27 Explorer";
    public const string Version = "0.13.0";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        Colonne.Reload();   // avant les crochets : le mode des colonnes décide de ceux à installer
        PatchEach(new Harmony(Guid), typeof(Patches), "canaux");
        // Crochets de diagnostic (compteurs, enregistrement F7, lecture F6) : appelés pour chaque réponse du moteur,
        // ils coûtent cher sur les longues listes ; seulement avec touches_dev=1
        if (NoteAuto.DevKeys)
        {
            PatchEach(new Harmony(Guid + ".dev"), typeof(DevPatches), "diagnostic (touches_dev=1)");
        }
        PatchEach(new Harmony(Guid + ".colonne"), typeof(ColonnePatches), "tableaux");
        if (Colonne.OwnMode)
        {
            PatchEach(new Harmony(Guid + ".proprietes"), typeof(ProprietePatches), "propriétés à nous");
        }
        // Mode Moneyball : fiche joueur (moneyball=1) et structure des écrans (exploration=1), réglages dans fm27explorer_moneyball.txt
        if (Moneyball.Exploring || Moneyball.Enabled)
        {
            PatchEach(new Harmony(Guid + ".moneyball"), typeof(MoneyballPatches), "Moneyball (écrans)");
        }

        AddComponent<ExplorerBehaviour>();
        L.LogMessage($"{Name} {Version} chargé. F9 = extraction complète + calcul des notes et des étoiles des préparateurs " +
                     "(écran avec des joueurs ; automatique chaque semaine de jeu, voir fm27explorer_notes.txt). " +
                     (NoteAuto.DevKeys
                         ? "Touches de développement actives : F8 = dump des identifiants, F7 = enregistrement des canaux, F6 = lecture directe de joueurs, " +
                           "F5 = compteurs, F4 = test d'affichage, F3 = balayage, F2 = club/équipe/compétition, F10 = relire les réglages des colonnes, " +
                           "F11 = ajouter les colonnes au tableau affiché, Maj+F11 / Ctrl+F11 = seuil du filtre +5 / -5."
                         : "Autres touches désactivées (touches_dev=1 dans fm27explorer_notes.txt pour les réactiver)."));
    }

    // Crochets installés un par un : un crochet refusé n'empêche pas les autres (PatchAll s'arrête à la 1re erreur)
    private static void PatchEach(Harmony h, Type t, string label)
    {
        int ok = 0;
        var failed = new List<string>();
        foreach (var m in AccessTools.GetDeclaredMethods(t))
        {
            var infos = HarmonyMethodExtensions.GetFromMethod(m);
            if (infos == null || infos.Count == 0) continue;
            var info = HarmonyMethod.Merge(infos);
            if (info.declaringType == null || info.methodName == null) continue;
            bool prefix = m.IsDefined(typeof(HarmonyPrefix), false), postfix = m.IsDefined(typeof(HarmonyPostfix), false);
            if (!prefix && !postfix) continue;
            try
            {
                var original = info.argumentTypes != null
                    ? AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes)
                    : AccessTools.Method(info.declaringType, info.methodName);
                if (original == null) throw new Exception("méthode introuvable");
                if (prefix) h.Patch(original, prefix: new HarmonyMethod(m));
                else h.Patch(original, postfix: new HarmonyMethod(m));
                ok++;
            }
            catch (Exception e) { failed.Add($"{info.declaringType.Name}.{info.methodName} ({m.Name}) : {e.GetBaseException().Message}"); }
        }
        if (failed.Count == 0) L.LogInfo($"Crochets « {label} » : {ok} installés.");
        else L.LogError($"Crochets « {label} » : {ok} installés, {failed.Count} REFUSÉS : {string.Join(" | ", failed)}");
    }

    internal static string ExportDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Sports Interactive", "Football Manager 27", "exports");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    internal static string Stamp => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
}

// ─────────────────────────────────────────────────────────────────────────────
// Gestion du clavier (ancienne API Input, avec repli sur le nouvel Input System)
// ─────────────────────────────────────────────────────────────────────────────
internal static class Keys
{
    private static bool _useInputSystem;

    // Touche de modification maintenue (Maj, Ctrl)
    public static bool Held(KeyCode legacy1, KeyCode legacy2, Func<UnityEngine.InputSystem.Keyboard, UnityEngine.InputSystem.Controls.ButtonControl> modern)
    {
        if (!_useInputSystem)
        {
            try { return Input.GetKey(legacy1) || Input.GetKey(legacy2); }
            catch (Exception) { _useInputSystem = true; }
        }
        var kb = UnityEngine.InputSystem.Keyboard.current;
        return kb != null && modern(kb).isPressed;
    }

    public static bool Pressed(KeyCode legacy, Func<UnityEngine.InputSystem.Keyboard, UnityEngine.InputSystem.Controls.KeyControl> modern)
    {
        if (!_useInputSystem)
        {
            try { return Input.GetKeyDown(legacy); }
            catch (Exception)
            {
                _useInputSystem = true;
                Plugin.L.LogInfo("Ancienne API Input indisponible, bascule sur Unity Input System.");
            }
        }
        var kb = UnityEngine.InputSystem.Keyboard.current;
        return kb != null && modern(kb).wasPressedThisFrame;
    }
}

public class ExplorerBehaviour : MonoBehaviour
{
    public ExplorerBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        Perf.EndFrame();
        using var perf = Perf.Measure();
        try
        {
            // F9 seule par défaut ; les autres touches (outils de développement) laissent leurs raccourcis au jeu,
            // sauf avec touches_dev=1 dans fm27explorer_notes.txt
            if (NoteAuto.DevKeys)
            {
                if (Keys.Pressed(KeyCode.F8, k => k.f8Key)) Dumper.DumpIdentifiers();
                if (Keys.Pressed(KeyCode.F7, k => k.f7Key)) Capture.Toggle();
                if (Keys.Pressed(KeyCode.F6, k => k.f6Key)) Probe.Start();
                if (Keys.Pressed(KeyCode.F4, k => k.f4Key)) Display.Toggle();
                if (Keys.Pressed(KeyCode.F5, k => k.f5Key)) Plugin.L.LogMessage("F5 compteurs : " + Counters.Snapshot() + " | " + NoteAuto.Status());
                if (Keys.Pressed(KeyCode.F3, k => k.f3Key)) Sweep.Toggle();
                if (Keys.Pressed(KeyCode.F2, k => k.f2Key)) RefProbe.Start();
                if (Keys.Pressed(KeyCode.F10, k => k.f10Key)) { Colonne.Reload(); Plugin.L.LogMessage("Colonne : " + Colonne.Status()); }
                if (Keys.Pressed(KeyCode.F11, k => k.f11Key))
                {
                    if (Keys.Held(KeyCode.LeftShift, KeyCode.RightShift, k => k.shiftKey)) Colonne.AdjustFilter(+5);
                    else if (Keys.Held(KeyCode.LeftControl, KeyCode.RightControl, k => k.ctrlKey)) Colonne.AdjustFilter(-5);
                    else Colonne.AddToVisibleTables();
                }
            }
            Colonne.EnsureProperties();
            Probe.Tick();
            Sweep.Tick();
            RefProbe.Tick();
            if (Keys.Pressed(KeyCode.F9, k => k.f9Key)) Extract.Toggle();
            Extract.Tick();
            NoteAuto.Tick();
            Moneyball.Tick();
        }
        catch (Exception e)
        {
            Plugin.L.LogError("Update : " + e);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Noms lisibles des identifiants
// ─────────────────────────────────────────────────────────────────────────────
internal static class Names
{
    public static string Property(uint id)
    {
        try { return PropertyIdentifierSet.Instance.GetInfo(id)?.Name ?? $"#{id}"; }
        catch { return $"#{id}"; }
    }

    public static string Reference(uint id)
    {
        try { return ReferenceIdentifierSet.Instance.GetInfo(id)?.Name ?? $"#{id}"; }
        catch { return $"#{id}"; }
    }

    // Ex : "Person{Person=12345}"
    public static string Describe(InteropReference r)
    {
        if (r == null) return "(null)";
        var sb = new StringBuilder();
        try { sb.Append(Reference(r.ID.id)); } catch { sb.Append("?"); }
        sb.Append('{');
        try
        {
            var ids = r.m_id;
            if (ids != null)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    var p = ids[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append(Reference(p.m_id)).Append('=').Append(p.m_value);
                }
            }
        }
        catch (Exception e) { sb.Append("err:" + e.GetType().Name); }
        sb.Append('}');
        return sb.ToString();
    }

    public static string TypeOf(InteropReference r)
    {
        if (r == null) return "(null)";
        try { return Reference(r.ID.id); } catch { return "?"; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// F8 : dump des registres d'identifiants
// ─────────────────────────────────────────────────────────────────────────────
internal static class Dumper
{
    public static void DumpIdentifiers()
    {
        var path = Path.Combine(Plugin.ExportDir, $"FM27_Identifiers_{Plugin.Stamp}.tsv");
        int total = 0;
        using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            w.WriteLine("kind\tid\tflag\tname");
            total += DumpSet(w, "property", PropertyIdentifierSet.Instance?.m_idToInfo);
            total += DumpSet(w, "reference", ReferenceIdentifierSet.Instance?.m_idToInfo);
            total += DumpSet(w, "context", ContextIdentifierSet.Instance?.m_idToInfo);
        }
        Plugin.L.LogMessage($"F8 : {total} identifiants écrits dans {path}");
        DumpPersonProperties();
    }

    // Liste exhaustive des propriétés qu'accepte une personne, avec leur type et description
    private static void DumpPersonProperties()
    {
        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<PropertyID>();
            FM.UI.PersonReference.GetPropertiesInternal(list);
            var path = Path.Combine(Plugin.ExportDir, $"FM27_PersonProperties_{Plugin.Stamp}.tsv");
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine("id\tname\tkind\tdescription");
            var rows = new List<(string name, string line)>();
            for (int i = 0; i < list.Count; i++)
            {
                var id = list[i].id;
                string kind = "", desc = "";
                try { kind = FM.UI.PersonReference.GetPropertyTypeInternal(id).ToString(); } catch { }
                try { desc = (FM.UI.PersonReference.GetPropertyDescriptionInternal(id) ?? "").Replace('\t', ' ').Replace('\n', ' '); } catch { }
                var name = Names.Property(id);
                rows.Add((name, $"{id}\t{name}\t{kind}\t{desc}"));
            }
            foreach (var r in rows.OrderBy(r => r.name, StringComparer.Ordinal)) w.WriteLine(r.line);
            Plugin.L.LogMessage($"F8 : {list.Count} propriétés de personne écrites dans {path}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError("Propriétés de personne : " + e);
        }
    }

    private static int DumpSet<TInfo>(StreamWriter w, string kind,
        Il2CppSystem.Collections.Generic.Dictionary<uint, TInfo> dict) where TInfo : IdentifierInfo
    {
        if (dict == null)
        {
            Plugin.L.LogWarning($"Registre '{kind}' introuvable (null).");
            return 0;
        }
        var rows = new List<(uint id, int flag, string name)>();
        foreach (var kv in dict)
        {
            var info = kv.Value;
            string name = null; int flag = 0;
            try { name = info?.Name; flag = info?._Flag_k__BackingField ?? 0; } catch { }
            rows.Add((kv.Key, flag, name ?? ""));
        }
        foreach (var r in rows.OrderBy(r => r.name, StringComparer.Ordinal))
            w.WriteLine($"{kind}\t{r.id}\t{r.flag}\t{r.name}");
        Plugin.L.LogInfo($"  {kind} : {rows.Count}");
        return rows.Count;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// F7 : enregistrement des canaux ouverts par l'interface
// ─────────────────────────────────────────────────────────────────────────────
internal static class Capture
{
    private const int MaxTimeline = 60000;

    public static bool Active { get; private set; }

    private static DateTime _start;
    // (type de référence, propriété) -> nombre d'ouvertures + premier exemple
    private static readonly Dictionary<string, (int count, string example)> Combos = new();
    private static readonly List<string> Timeline = new();

    public static void Toggle()
    {
        if (!Active)
        {
            Combos.Clear();
            Timeline.Clear();
            _start = DateTime.Now;
            Active = true;
            Plugin.L.LogMessage("F7 : enregistrement des canaux DÉMARRÉ. Navigue dans le jeu, puis F7 pour arrêter.");
        }
        else
        {
            Active = false;
            Write();
        }
    }

    public static void Record(string source, InteropReference reference, uint property, ulong channelKey = 0)
    {
        if (!Active) return;
        try
        {
            var type = Names.TypeOf(reference);
            var prop = Names.Property(property);
            var key = type + "\t" + prop;
            if (Combos.TryGetValue(key, out var c)) Combos[key] = (c.count + 1, c.example);
            else Combos[key] = (1, Names.Describe(reference));

            Raw(source, $"key=0x{channelKey:X16}\t{Names.Describe(reference)}\t{prop}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError("Record : " + e.Message);
        }
    }

    // Ligne brute dans la chronologie
    public static void Raw(string source, string text)
    {
        if (!Active || Timeline.Count >= MaxTimeline) return;
        var t = (DateTime.Now - _start).TotalSeconds;
        Timeline.Add($"{t:F3}\t{source}\t{text}");
    }

    private static void Write()
    {
        var stamp = Plugin.Stamp;
        var dir = Plugin.ExportDir;

        var combosPath = Path.Combine(dir, $"FM27_Channels_{stamp}.tsv");
        using (var w = new StreamWriter(combosPath, false, new UTF8Encoding(true)))
        {
            w.WriteLine("reference_type\tproperty\tcount\texample_reference");
            foreach (var kv in Combos.OrderBy(k => k.Key, StringComparer.Ordinal))
                w.WriteLine($"{kv.Key}\t{kv.Value.count}\t{kv.Value.example}");
        }

        var timelinePath = Path.Combine(dir, $"FM27_ChannelTimeline_{stamp}.tsv");
        using (var w = new StreamWriter(timelinePath, false, new UTF8Encoding(true)))
        {
            w.WriteLine("t_seconds\tsource\tdetails");
            foreach (var line in Timeline) w.WriteLine(line);
        }

        Plugin.L.LogMessage($"F7 : enregistrement ARRÊTÉ. {Combos.Count} combinaisons, {Timeline.Count} événements.");
        Plugin.L.LogMessage($"  -> {combosPath}");
        Plugin.L.LogMessage($"  -> {timelinePath}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// F6 : lecture directe de joueurs dans la base, par index "dref"
// ─────────────────────────────────────────────────────────────────────────────
// Une référence de personne ressemble à : person{'110 '=28, 'Guid'=<session>, 'dref'=0x01000000+index}
// On recopie le modèle d'une vraie référence vue passer (pour avoir le bon Guid de session),
// on remplace 'dref', on ouvre nos propres canaux, et on intercepte les réponses du moteur.
internal static class Probe
{
    public const uint KeyType = 0x31313020; // '110 '
    public const uint KeyGuid = 0x47756964; // 'Guid'
    public const uint KeyDref = 0x64726566; // 'dref'
    public const int PersonDrefBase = 0x01000000;
    private const ulong OurKeyBase = 0x7E00_0000_0000_0000UL;

    // Ce que l'interface nous a appris en passant
    internal static GameInteropSubsystem Subsystem;
    internal static List<(uint id, int value)> PersonTemplate;
    internal static readonly HashSet<int> SeenPersonDrefs = new();

    // Liste modifiable sans recompiler : BepInEx/config/fm27explorer_sonde.txt (une propriété par ligne, # = commentaire)
    private static string[] Props = DefaultProps;
    private static string SondePath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27explorer_sonde.txt");

    private static readonly string[] DefaultProps =
    {
        "UniqueId", "Name", "Age", "DateOfBirth", "Nation", "Club", "Team", "Division",
        "PositionCombinedString", "IsPlayer", "IsRetired",
        "Wage", "ContextContractWeeklyWage", "TransferValueRange",
        "Appearances", "Minutes", "Goals", "AverageRating",
        "AttributeFinishing", "AttributeTackling", "AttributePassing", "AttributePace", "AttributeReflexes",
        "ExpectedGoalsPer90", "KeyPassesPer90", "InterceptionsPer90", "PressuresCompletedPer90", "DistanceRanPer90",
        "CurrentAbilityStarRange", "PotentialAbilityStarRange",
        "PlayerCurrentAbility", "PlayerPotentialAbility", "PerceivedPotentialAbility",
        "AbilityCentreBack", "AbilityForward",
    };

    private static readonly int[] ProbeIndexes = BuildIndexes();

    private static int[] BuildIndexes()
    {
        var l = new List<int>();
        for (int i = 0; i < 100; i++) l.Add(i);          // tout début de la base
        for (int i = 0; i < 50; i++) l.Add(20000 + i);    // milieu
        for (int i = 0; i < 50; i++) l.Add(150000 + i);   // loin (au-delà de 10 000 ?)
        return l.ToArray();
    }

    public static bool Active { get; private set; }

    // clé -> (index de ligne, nom de propriété)
    private static readonly Dictionary<ulong, (int row, string prop)> Pending = new();
    private static readonly Dictionary<(int row, string prop), (string value, string type)> Values = new();
    private static readonly List<(int row, string label, int dref)> Rows = new();
    private static readonly Dictionary<string, int> TypeCount = new();
    private static ulong _nextKey = OurKeyBase;
    private static DateTime _startedAt, _lastValueAt;
    private static int _received, _foreignBatches;

    public static bool IsOurs(ulong key) => key >= OurKeyBase && key < OurKeyBase + 0x0100_0000_0000_0000UL;

    public static void Start()
    {
        if (Active) { Plugin.L.LogWarning("F6 : une lecture est déjà en cours."); return; }
        if (Subsystem == null || PersonTemplate == null)
        {
            Plugin.L.LogWarning("F6 : pas encore de modèle de référence joueur. Ouvre d'abord un écran qui affiche des joueurs, puis réessaie.");
            return;
        }

        var nameToId = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var kv in PropertyIdentifierSet.Instance.m_idToInfo)
        {
            var n = kv.Value?.Name;
            if (n != null && !nameToId.ContainsKey(n)) nameToId[n] = kv.Key;
        }

        Props = DefaultProps;
        try
        {
            if (File.Exists(SondePath))
            {
                var fromFile = File.ReadAllLines(SondePath).Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#")).Distinct().ToArray();
                if (fromFile.Length > 0) Props = new[] { "Name" }.Concat(fromFile.Where(p => p != "Name")).ToArray();
                Plugin.L.LogMessage($"F6 : {Props.Length} propriétés lues dans {SondePath}");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("F6 : lecture de " + SondePath + " : " + e.Message); }
        var unknown = Props.Where(p => !nameToId.ContainsKey(p)).ToList();
        if (unknown.Count > 0) Plugin.L.LogWarning("F6 : propriétés inconnues ignorées : " + string.Join(", ", unknown));

        Pending.Clear(); Values.Clear(); Rows.Clear(); TypeCount.Clear();
        _received = 0; _foreignBatches = 0;
        HandlerCalls = HandlerKeys = OurKeysSentToEngine = 0;
        Counters.SendDataResponseKeys = Counters.OurKeysFromEngine = Counters.InternalListItems = 0;
        Plugin.L.LogMessage("  compteurs avant F6 : " + Counters.Snapshot());

        // UN SEUL joueur : on accroche nos nœuds enfants au nœud "Person" de la fiche ouverte
        // (créé et complété par l'interface), mais on ouvre les canaux avec la référence
        // d'un AUTRE joueur, vu plus tôt dans une liste.
        if (UiPersonNodeKey == 0)
        {
            Plugin.L.LogWarning("F6 : aucun nœud 'Person' de l'interface connu. Ouvre une fiche joueur, puis réessaie.");
            return;
        }
        if (SeenDrefsInOrder.Count == 0)
        {
            Plugin.L.LogWarning("F6 : aucun joueur vu dans une liste. Affiche une liste de joueurs, puis ouvre une fiche, puis réessaie.");
            return;
        }
        // Les 5 premières personnes vues à l'écran, sauf le manager humain (toi)
        var targets = SeenDrefsInOrder.Where(d => d != HumanDref).Take(5).ToList();
        Nodes.Clear(); PersonNodes.Clear();
        int opened = 0;
        for (int row = 0; row < targets.Count; row++)
        {
            int target = targets[row];
            Rows.Add((row, "joueur vu à l'écran", target));
            var reference = MakePersonReference(target);
            foreach (var p in Props)
            {
                if (!nameToId.TryGetValue(p, out var pid)) continue;
                var key = _nextKey++;
                Pending[key] = (row, p);
                Subsystem.AddNode(new SI.Bindable.Bindings.Key(key), new SI.Bindable.Bindings.Key(UiPersonNodeKey), new PropertyID { id = pid });
                Nodes.Add(key);
                Subsystem.OpenChannel(reference, new PropertyID { id = pid }, new SI.Bindable.Bindings.Key(key));
                opened++;
            }
        }
        Plugin.L.LogMessage($"  cibles : {string.Join(", ", targets.Select(t => t - PersonDrefBase))} (manager exclu : {HumanDref - PersonDrefBase}), " +
                            $"nœud parent de l'interface = 0x{UiPersonNodeKey:X16}");
        var drefs = Rows;

        _startedAt = _lastValueAt = DateTime.Now;
        Active = true;
        Plugin.L.LogMessage($"F6 : lecture directe lancée : {drefs.Count} joueur x {Props.Length} propriétés = {opened} canaux.");
    }

    // Il faut la vraie classe FM.UI.PersonReference : c'est elle qui déclare au système
    // de bindings les propriétés acceptées par une personne (AcceptsProperty, ID...).
    internal static InteropReference MakePersonReference(int dref)
    {
        var list = new Il2CppSystem.Collections.Generic.List<InteropReference.Pair>();
        foreach (var (id, value) in PersonTemplate)
            list.Add(new InteropReference.Pair(id, id == KeyDref ? dref : value));
        return new FM.UI.PersonReference(list);
    }

    internal static ulong UiParentKey;          // parent d'un nœud "Person" de l'interface
    internal static ulong UiPersonNodeKey;      // dernier nœud "Person" créé par l'interface
    internal static readonly Dictionary<string, ulong> UiTypedNodes = new();   // "Club" -> nœud de l'interface
    public const uint KeyHuman = 0x4868756D;    // 'Hhum'
    internal static int HumanDref;
    internal static readonly List<int> SeenDrefsInOrder = new();
    private static readonly List<ulong> Nodes = new(), PersonNodes = new();

    // Diagnostic
    internal static int HandlerCalls, HandlerKeys, OurKeysSentToEngine;

    // Appelé depuis le hook sur InteropDataHandler.OnChannelDataChanged
    public static void OnValue(ulong key, TypedValue v)
    {
        if (!Pending.TryGetValue(key, out var slot)) return;
        RefProbe.Remember(slot.prop, v);
        string s, t;
        try { s = ValueText.Format(v, -3); } catch (Exception e) { s = "err:" + e.GetType().Name; }
        try { t = v?.DataType?.Name ?? "?"; } catch { t = "?"; }
        s = (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        bool first = !Values.ContainsKey(slot);
        Values[slot] = (s, t);
        _lastValueAt = DateTime.Now;
        if (!first) return;
        var tk = slot.prop + "|" + t;
        TypeCount[tk] = TypeCount.TryGetValue(tk, out var c) ? c + 1 : 1;
        _received++;
    }

    public static void NoteForeignBatch() => _foreignBatches++;

    public static void Tick()
    {
        if (!Active) return;
        var now = DateTime.Now;
        // Fin : 8 s sans nouvelle valeur, ou 30 s au total
        if ((now - _lastValueAt).TotalSeconds < 8 && (now - _startedAt).TotalSeconds < 30) return;
        Active = false;

        foreach (var key in Pending.Keys)
        {
            try { Subsystem.CloseChannel(new SI.Bindable.Bindings.Key(key)); } catch { }
        }
        foreach (var key in Nodes) { try { Subsystem.RemoveNode(new SI.Bindable.Bindings.Key(key)); } catch { } }
        foreach (var key in PersonNodes) { try { Subsystem.RemoveNode(new SI.Bindable.Bindings.Key(key)); } catch { } }
        // Résultat par groupe
        foreach (var (row, _, dref) in Rows)
        {
            Plugin.L.LogMessage($"  ── personne index {dref - PersonDrefBase} ──");
            foreach (var p in Props)
                if (Values.TryGetValue((row, p), out var v)) Plugin.L.LogMessage($"    {p} = {v.value}  [{v.type}]");
        }

        var path = Path.Combine(Plugin.ExportDir, $"FM27_Probe_{Plugin.Stamp}.tsv");
        using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            w.WriteLine("label\tdref\tindex\t" + string.Join("\t", Props));
            foreach (var (row, label, dref) in Rows)
            {
                var cells = Props.Select(p => Values.TryGetValue((row, p), out var v) ? v.value : "");
                w.WriteLine($"{label}\t{dref}\t{dref - PersonDrefBase}\t" + string.Join("\t", cells));
            }
            w.WriteLine();
            w.WriteLine("# types reçus (propriété|type : nombre)");
            foreach (var kv in TypeCount.OrderBy(k => k.Key)) w.WriteLine($"# {kv.Key}\t{kv.Value}");
        }
        Plugin.L.LogMessage($"F6 : terminé. {_received}/{Pending.Count} valeurs reçues en {(now - _startedAt).TotalSeconds:F1}s " +
                            $"(lots mixtes avec l'interface : {_foreignBatches}). -> {path}");
        Plugin.L.LogMessage($"  diagnostic : nos canaux transmis au moteur = {OurKeysSentToEngine}, " +
                            $"réponses du moteur pendant la lecture = {HandlerCalls} lots / {HandlerKeys} clés.");
        Plugin.L.LogMessage("  compteurs après F6 : " + Counters.Snapshot());
        try
        {
            var r = MakePersonReference(PersonDrefBase + 820);
            Plugin.L.LogMessage($"  diagnostic : notre référence = {r.GetIl2CppType().FullName}, type '{Names.TypeOf(r)}', " +
                                $"accepte AttributeFinishing = {AcceptsByName(r, "AttributeFinishing")}");
        }
        catch (Exception e) { Plugin.L.LogWarning("  diagnostic référence : " + e.Message); }
    }

    private static bool AcceptsByName(InteropReference r, string prop)
    {
        foreach (var kv in PropertyIdentifierSet.Instance.m_idToInfo)
            if (kv.Value?.Name == prop) return r.AcceptsProperty(kv.Key);
        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Hooks Harmony
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch]
internal static class Patches
{
    // Ouverture d'un canal unique (couche Unity)
    [HarmonyPatch(typeof(GameInteropSubsystem), nameof(GameInteropSubsystem.OpenChannel))]
    [HarmonyPrefix]
    private static void OpenChannelPrefix(GameInteropSubsystem __instance, InteropReference reference, ref PropertyID identifier,
        SI.Bindable.Bindings.Key key)
    {
        using var perf = Perf.Measure();
        if (Probe.IsOurs(key.m_key)) return; // nos propres demandes
        Probe.Subsystem ??= __instance;
        // En premier : une cellule de nos colonnes demande notre propriété -> propriété du modèle envoyée au moteur
        try { Colonne.OnUiOpenChannel(reference, ref identifier.id, key.m_key); } catch (Exception e) { Counters.Note("Colonne.OnUiOpenChannel", e); }
        try
        {
            // 'Hhum' = le manager humain, présent dans certaines références (ex : personsearch)
            if (Probe.HumanDref == 0 && reference != null)
            {
                var all = reference.m_id;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].m_id == Probe.KeyHuman) { Probe.HumanDref = all[i].m_value; break; }
            }
            if (reference != null && Names.TypeOf(reference) == "person")
            {
                var ids = reference.m_id;
                var tpl = new List<(uint, int)>();
                for (int i = 0; i < ids.Count; i++)
                {
                    var p = ids[i];
                    tpl.Add((p.m_id, p.m_value));
                    if (p.m_id == Probe.KeyDref && Probe.SeenPersonDrefs.Count < 5000 && Probe.SeenPersonDrefs.Add(p.m_value))
                        Probe.SeenDrefsInOrder.Add(p.m_value);
                }
                // Modèle de référence « personne » : remplacé si l'identifiant de session change (autre partie chargée)
                int Guid(List<(uint id, int value)> t) => t.FirstOrDefault(x => x.id == Probe.KeyGuid).value;
                if (Probe.PersonTemplate == null) Probe.PersonTemplate = tpl;
                else if (Guid(tpl) != 0 && Guid(tpl) != Guid(Probe.PersonTemplate))
                {
                    Probe.PersonTemplate = tpl;
                    Plugin.L.LogMessage("Nouvelle partie détectée : références des joueurs mises à jour.");
                }
            }
        }
        catch { }
        try { GameDate.OnUiOpenChannel(reference, identifier.id, key.m_key); } catch { }
        Display.OnUiOpenChannel(reference, identifier.id, key.m_key);
        Capture.Record("Subsystem.OpenChannel", reference, identifier.id, key.m_key);
    }

    // Réponses du moteur : on prend nos valeurs ; si le lot ne contient QUE nos clés,
    // on ne le transmet pas à l'interface (qui ne les connaît pas).
    [HarmonyPatch(typeof(FM.UI.InteropDataHandler), nameof(FM.UI.InteropDataHandler.OnChannelDataChanged))]
    [HarmonyPrefix]
    private static bool OnChannelDataChangedPrefix(Il2CppSystem.ReadOnlySpan<ulong> keys,
        Il2CppSystem.Collections.Generic.List<TypedValue> values)
    {
        using var perf = Perf.Measure();
        Counters.Handler++;
        if ((!Probe.Active && !Display.Active && !Sweep.Active && !RefProbe.Active && !Extract.Active && !Colonne.Displaying
             && !GameDate.Watching && !Anchor.Testing) || keys is null || values is null) return true;
        try
        {
            var raw = SpanReader.Read(keys);
            int n = Math.Min(raw.Length, values.Count);

            if (Display.Active)
                for (int i = 0; i < n; i++)
                    Display.MaybeReplace(raw[i], values, i);
            if (Colonne.Displaying)
                for (int i = 0; i < n; i++)
                    Colonne.MaybeReplace(raw[i], values, i);
            if (GameDate.Watching)
                for (int i = 0; i < n; i++)
                    GameDate.OnValue(raw[i], values[i]);

            if (!Probe.Active && !Sweep.Active && !RefProbe.Active && !Extract.Active && !Anchor.Testing) return true;
            Probe.HandlerCalls++;
            Probe.HandlerKeys += n;
            int ours = 0;
            for (int i = 0; i < n; i++)
            {
                var k = raw[i];
                if (!Probe.IsOurs(k)) continue;
                ours++;
                if (Extract.Owns(k)) Extract.OnValue(k, values[i]);
                else if (Sweep.Owns(k)) Sweep.OnValue(k, values[i]);
                else if (RefProbe.Owns(k)) RefProbe.OnValue(k, values[i]);
                else if (Anchor.Owns(k)) Anchor.OnValue(k, values[i]);
                else Probe.OnValue(k, values[i]);
            }
            if (ours == 0) return true;
            if (ours == n) return false;
            Probe.NoteForeignBatch();
        }
        catch (Exception e)
        {
            Counters.Note("OnChannelDataChanged", e);
        }
        return true;
    }

    // Ouverture d'un lot de canaux (envoi vers le moteur natif)
    [HarmonyPatch(typeof(GamePlugin), nameof(GamePlugin.OpenChannels))]
    [HarmonyPrefix]
    private static void OpenChannelsPrefix(InteropReference reference,
        Il2CppSystem.Collections.Generic.List<ChannelData> channels)
    {
        using var perf = Perf.Measure();
        if (channels == null) return;
        try { Colonne.SubstituteBatch(reference, channels); } catch (Exception e) { Counters.Note("Colonne.SubstituteBatch", e); }
        if (Probe.Active)
            for (int i = 0; i < channels.Count; i++)
                if (Probe.IsOurs(channels[i].key)) Probe.OurKeysSentToEngine++;
        if (!Capture.Active) return;
        for (int i = 0; i < channels.Count; i++)
            Capture.Record("GamePlugin.OpenChannels", reference, channels[i].property, channels[i].key);
    }

    [HarmonyPatch(typeof(GameInteropSubsystem), nameof(GameInteropSubsystem.AddNode))]
    [HarmonyPrefix]
    private static void AddNodePrefix(SI.Bindable.Bindings.Key key, SI.Bindable.Bindings.Key parentKey, ref PropertyID property)
    {
        using var perf = Perf.Measure();
        Colonne.SubstituteNode(ref property.id);   // notre propriété : le moteur reçoit celle du modèle
        if (!Probe.IsOurs(key.m_key) && !Probe.IsOurs(parentKey.m_key) && parentKey.m_key != 0)
        {
            try
            {
                var pn = Names.Property(property.id);
                Anchor.OnUiAddNode(key.m_key, parentKey.m_key, pn);
                if (pn == "Person")
                {
                    Probe.UiParentKey = parentKey.m_key;
                    Probe.UiPersonNodeKey = key.m_key;
                }
                else if (pn is "Club" or "Team" or "Nation" or "Division" or "Competition")
                    Probe.UiTypedNodes[pn] = key.m_key;
            }
            catch { }
        }
        if (Capture.Active)
            Capture.Raw("Subsystem.AddNode", $"key=0x{key.m_key:X16}\tparent=0x{parentKey.m_key:X16}\t{Names.Property(property.id)}");
    }

    [HarmonyPatch(typeof(GameInteropSubsystem), nameof(GameInteropSubsystem.RemoveNode))]
    [HarmonyPrefix]
    private static void RemoveNodePrefix(SI.Bindable.Bindings.Key key)
    {
        using var perf = Perf.Measure();
        if (key.m_key == Probe.UiPersonNodeKey) Probe.UiPersonNodeKey = 0;
        if (key.m_key == Probe.UiParentKey) Probe.UiParentKey = 0;
        try { Anchor.OnUiRemoveNode(key.m_key); } catch { }
        foreach (var kv in Probe.UiTypedNodes.Where(kv => kv.Value == key.m_key).ToList()) Probe.UiTypedNodes.Remove(kv.Key);
    }

    [HarmonyPatch(typeof(GameInteropSubsystem), nameof(GameInteropSubsystem.CloseChannel))]
    [HarmonyPrefix]
    private static void CloseChannelPrefix(SI.Bindable.Bindings.Key key)
    {
        using var perf = Perf.Measure();
        Display.OnUiCloseChannel(key.m_key);
        Colonne.OnUiCloseChannel(key.m_key);
        GameDate.OnUiCloseChannel(key.m_key);
        if (Capture.Active) Capture.Raw("Subsystem.CloseChannel", $"key=0x{key.m_key:X16}");
    }

}

// Crochets de diagnostic : seulement avec touches_dev=1 (F5 compteurs, F6 lecture directe, F7 enregistrement).
// Ils sont appelés pour chaque réponse du moteur : inutiles en jeu normal et coûteux sur les longues listes.
[HarmonyPatch]
internal static class DevPatches
{
    // ── Compteurs de diagnostic sur chaque couche du retour de données ──
    // natif -> GameReceiver.SendDataResponse -> SendDataResponseInternal -> ... -> InteropDataHandler.OnChannelDataChanged

    [HarmonyPatch(typeof(GameReceiver), nameof(GameReceiver.SendDataResponse))]
    [HarmonyPrefix]
    private static void SendDataResponsePrefix(Il2CppSystem.Span<ulong> keys)
    {
        using var perf = Perf.Measure();
        Counters.SendDataResponse++;
        if (!Probe.Active || keys is null) return;
        try
        {
            int n = keys.Length;
            Counters.SendDataResponseKeys += n;
            for (int i = 0; i < n; i++) if (Probe.IsOurs(keys[i])) Counters.OurKeysFromEngine++;
        }
        catch (Exception e) { Counters.Note("SendDataResponse", e); }
    }

    [HarmonyPatch(typeof(GameReceiver), nameof(GameReceiver.SendDataResponseInternal))]
    [HarmonyPrefix]
    private static void SendDataResponseInternalPrefix()
    {
        using var perf = Perf.Measure();
        Counters.SendDataResponseInternal++;
    }

    // Après décodage : les valeurs sont dans GameReceiver.m_resusableTypedList, dans l'ordre des clés.
    [HarmonyPatch(typeof(GameReceiver), nameof(GameReceiver.SendDataResponseInternal))]
    [HarmonyPostfix]
    private static void SendDataResponseInternalPostfix(GameReceiver __instance, Il2CppSystem.Span<ulong> keys)
    {
        using var perf = Perf.Measure();
        if (!Probe.Active || keys is null) return;
        try
        {
            var list = __instance.m_resusableTypedList;
            if (list == null) return;
            int n = Math.Min(keys.Length, list.Count);
            Counters.InternalListItems += list.Count;
            for (int i = 0; i < n; i++)
            {
                var k = keys[i];
                if (Probe.IsOurs(k)) Probe.OnValue(k, list[i]);
            }
        }
        catch (Exception e) { Counters.Note("SendDataResponseInternal", e); }
    }

    // ── Actions envoyées au moteur (arbre de bindings : AddNode, SetTarget, CloseChannel...) ──
    [HarmonyPatch(typeof(GamePlugin), nameof(GamePlugin.PerformChannelActions))]
    [HarmonyPrefix]
    private static void PerformChannelActionsPrefix(Il2CppSystem.Collections.Generic.List<ChannelAction> actions,
        Il2CppSystem.Collections.Generic.List<Il2CppSystem.ValueTuple<int, TypedValue, InteropReference,
            Il2CppSystem.Collections.Generic.List<ChannelData>>> batchedRequestDataUpdate)
    {
        using var perf = Perf.Measure();
        if (!Capture.Active) return;
        try
        {
            if (actions is not null)
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    Capture.Raw("GamePlugin.Action", $"{a.mode}\tkey=0x{a.key:X16}\tadditional=0x{a.additionalKey:X16}");
                }
            if (batchedRequestDataUpdate is not null)
                for (int i = 0; i < batchedRequestDataUpdate.Count; i++)
                {
                    var b = batchedRequestDataUpdate[i];
                    var chans = b.Item4;
                    var sb = new StringBuilder();
                    if (chans is not null)
                        for (int j = 0; j < chans.Count && j < 50; j++)
                            sb.Append($"{Names.Property(chans[j].property)}@0x{chans[j].key:X16} ");
                    string v = "";
                    try { v = b.Item2 is null ? "(null)" : b.Item2.AsString(); } catch { v = "?"; }
                    Capture.Raw("GamePlugin.BatchedUpdate", $"int={b.Item1}\tvalue={v}\tref={Names.Describe(b.Item3)}\tchannels={sb}");
                }
        }
        catch (Exception e) { Counters.Note("PerformChannelActions", e); }
    }

    [HarmonyPatch(typeof(GameInteropSubsystem), nameof(GameInteropSubsystem.SetTarget))]
    [HarmonyPrefix]
    private static void SetTargetPrefix(SI.Bindable.Bindings.Key key)
    {
        using var perf = Perf.Measure();
        if (Capture.Active) Capture.Raw("Subsystem.SetTarget", $"key=0x{key.m_key:X16}");
    }

    // Réponses : on note les clés reçues (pour relier demandes et réponses)
    [HarmonyPatch(typeof(GameReceiver), nameof(GameReceiver.SendDataResponseInternal))]
    [HarmonyPostfix]
    private static void SendDataResponseCapturePostfix(GameReceiver __instance, Il2CppSystem.Span<ulong> keys)
    {
        using var perf = Perf.Measure();
        if (!Capture.Active || keys is null) return;
        try
        {
            var list = __instance.m_resusableTypedList;
            int n = keys.Length;
            var sb = new StringBuilder();
            for (int i = 0; i < n && i < 40; i++)
            {
                string v = "";
                try { v = list is not null && i < list.Count && list[i] is not null ? list[i].AsString() : ""; } catch { v = "?"; }
                if (v != null && v.Length > 40) v = v.Substring(0, 40);
                sb.Append($"0x{keys[i]:X16}={v?.Replace('\t', ' ').Replace('\n', ' ')} | ");
            }
            Capture.Raw("Engine.Response", $"n={n}\t{sb}");
        }
        catch (Exception e) { Counters.Note("SendDataResponseCapture", e); }
    }
}

// Temps passé dans le plugin (crochets + Update) : une image de plus d'une seconde est signalée dans le log
// avec la part du plugin, pour savoir si un ralentissement vient de nous ou du jeu.
// (Le passage natif -> géré de chaque appel de crochet n'est pas compté : voir le nombre d'appels.)
internal static class Perf
{
    private static readonly double MsPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private static long _ticks, _lastFrame;
    private static int _calls, _mainThread;

    // Détail par crochet (fil principal seulement) : nom de la méthode -> appels et temps de l'image en cours
    private static readonly Dictionary<string, int> SiteIndex = new();
    private static readonly List<string> SiteNames = new();
    private static int[] _siteCalls = new int[32];
    private static long[] _siteTicks = new long[32];

    public readonly struct Scope : IDisposable
    {
        private readonly long _t0;
        private readonly int _site;
        public Scope(long t0, int site) { _t0 = t0; _site = site; }
        public void Dispose()
        {
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - _t0;
            _ticks += dt;
            if (_site >= 0) _siteTicks[_site] += dt;
        }
    }

    public static Scope Measure([System.Runtime.CompilerServices.CallerMemberName] string site = "")
    {
        _calls++;
        int s = -1;
        if (_mainThread != 0 && Environment.CurrentManagedThreadId == _mainThread)
        {
            if (!SiteIndex.TryGetValue(site, out s))
            {
                s = SiteNames.Count;
                SiteNames.Add(site);
                SiteIndex[site] = s;
                if (s >= _siteCalls.Length)
                {
                    Array.Resize(ref _siteCalls, s * 2);
                    Array.Resize(ref _siteTicks, s * 2);
                }
            }
            _siteCalls[s]++;
        }
        return new Scope(System.Diagnostics.Stopwatch.GetTimestamp(), s);
    }

    public static void EndFrame()
    {
        if (_mainThread == 0) _mainThread = Environment.CurrentManagedThreadId;   // appelé par Update : fil principal
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastFrame != 0)
        {
            double ms = (now - _lastFrame) * MsPerTick;
            if (ms >= 1000)
            {
                var top = Enumerable.Range(0, SiteNames.Count).Where(i => _siteCalls[i] > 0)
                                    .OrderByDescending(i => _siteCalls[i]).Take(8)
                                    .Select(i => $"{SiteNames[i].Replace("Prefix", "").Replace("Postfix", "+")} {_siteCalls[i]} ({_siteTicks[i] * MsPerTick:F0} ms)");
                Plugin.L.LogMessage($"Image longue : {ms / 1000:F1} s, dont {_ticks * MsPerTick:F0} ms dans le plugin " +
                                    $"({_calls} appels de crochets : {string.Join(", ", top)}).");
            }
        }
        _lastFrame = now;
        _ticks = 0;
        _calls = 0;
        Array.Clear(_siteCalls, 0, SiteNames.Count);
        Array.Clear(_siteTicks, 0, SiteNames.Count);
    }
}

internal static class Counters
{
    public static long SendDataResponse, SendDataResponseKeys, OurKeysFromEngine,
        SendDataResponseInternal, InternalListItems, Handler;

    private static readonly HashSet<string> Reported = new();

    public static void Note(string where, Exception e)
    {
        if (Reported.Add(where)) Plugin.L.LogError($"{where} : {e}");
    }

    public static string Snapshot() =>
        $"SendDataResponse={SendDataResponse} (clés lues pendant F6={SendDataResponseKeys}, dont nôtres={OurKeysFromEngine}), " +
        $"SendDataResponseInternal={SendDataResponseInternal} (valeurs décodées pendant F6={InternalListItems}), " +
        $"InteropDataHandler.OnChannelDataChanged={Handler}";
}

// ─────────────────────────────────────────────────────────────────────────────
// Lecture brute d'un Il2CppSystem.ReadOnlySpan<ulong> (struct { T* _pointer; int _length; })
// L'indexeur généré par l'interop renvoyait des valeurs incohérentes : on lit la mémoire.
// ─────────────────────────────────────────────────────────────────────────────
internal static unsafe class SpanReader
{
    private static bool _checked;

    public static ulong[] Read(Il2CppSystem.ReadOnlySpan<ulong> span)
    {
        byte* data = (byte*)Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(span.Pointer);
        ulong* p = *(ulong**)data;
        int len = *(int*)(data + IntPtr.Size);
        if (!_checked)
        {
            _checked = true;
            int official = -1;
            try { official = span.Length; } catch { }
            Plugin.L.LogInfo($"SpanReader : longueur brute={len}, longueur interop={official}, 1re clé=0x{(len > 0 && p != null ? p[0] : 0):X16}");
        }
        if (len <= 0 || len > 1_000_000 || p == null) return Array.Empty<ulong>();
        var r = new ulong[len];
        for (int i = 0; i < len; i++) r[i] = p[i];
        return r;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// F4 : test d'affichage — on remplace la valeur "Age" envoyée par le moteur à l'interface
// par un nombre fabriqué (index du joueur dans la base modulo 100).
// Si la colonne Âge change à l'écran, on sait qu'on pourra y afficher notre note.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Display
{
    public const string TargetProperty = "Age";

    public static bool Active { get; private set; }

    private static uint _targetId;
    private static readonly Dictionary<ulong, int> KeyToDref = new();
    private static int _replaced, _seenTarget;

    private static uint TargetId
    {
        get
        {
            if (_targetId != 0) return _targetId;
            foreach (var kv in PropertyIdentifierSet.Instance.m_idToInfo)
                if (kv.Value?.Name == TargetProperty) { _targetId = kv.Key; break; }
            return _targetId;
        }
    }

    public static void Toggle()
    {
        Active = !Active;
        Plugin.L.LogMessage(Active
            ? $"F4 : test d'affichage ACTIVÉ (colonne {TargetProperty}). Fais défiler la liste pour voir les nouvelles valeurs."
            : $"F4 : test d'affichage DÉSACTIVÉ. {_replaced} valeurs remplacées, {KeyToDref.Count} canaux '{TargetProperty}' suivis, {_seenTarget} vus.");
    }

    // L'interface ouvre un canal : si c'est l'âge d'une personne, on retient clé -> joueur
    public static void OnUiOpenChannel(InteropReference reference, uint property, ulong key)
    {
        try
        {
            if (reference is null || property != TargetId) return;
            _seenTarget++;
            var ids = reference.m_id;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i].m_id == Probe.KeyDref) { KeyToDref[key] = ids[i].m_value; return; }
        }
        catch (Exception e) { Counters.Note("Display.OnUiOpenChannel", e); }
    }

    public static void OnUiCloseChannel(ulong key) => KeyToDref.Remove(key);

    public static void MaybeReplace(ulong key, Il2CppSystem.Collections.Generic.List<TypedValue> values, int i)
    {
        if (!KeyToDref.TryGetValue(key, out var dref)) return;
        int fake = (dref & 0x00FFFFFF) % 100;
        values[i] = TypedValue.Create(fake);
        _replaced++;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Mise en texte d'une valeur reçue du moteur.
// Les stats/attributs arrivent en DynamicReference = Record = dictionnaire (propriété -> valeur) :
// on le déplie récursivement.
// ─────────────────────────────────────────────────────────────────────────────
internal static class ValueText
{
    private static readonly System.Text.RegularExpressions.Regex Tags = new("<[^>]+>");

    public static string Format(TypedValue v, int depth)
    {
        if (v is null) return "(null)";
        if (v.IsNull) return "(nul)";
        Il2CppSystem.Object o = null;
        try { o = v.Get(); } catch { }
        if (o != null && depth < 3)
        {
            SI.Core.Record rec = null;
            try { rec = o.TryCast<SI.Core.Record>(); } catch { }
            if (rec != null) return FormatRecord(rec, depth);

            InteropReference r = null;
            try { r = o.TryCast<InteropReference>(); } catch { }
            if (r != null) return Names.Describe(r);

            Il2CppSystem.Collections.Generic.List<TypedValue> list = null;
            try { list = o.TryCast<Il2CppSystem.Collections.Generic.List<TypedValue>>(); } catch { }
            if (list != null)
            {
                var sb = new StringBuilder($"[{list.Count} éléments] ");
                for (int i = 0; i < list.Count && i < (depth < 0 ? 40 : 8); i++) sb.Append(Format(list[i], depth + 1)).Append(" ; ");
                return sb.ToString();
            }
        }
        return Tags.Replace(v.AsString() ?? "", "");
    }

    private static string FormatRecord(SI.Core.Record rec, int depth)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in rec)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(Names.Property(kv.Key)).Append('=').Append(Format(kv.Value, depth + 1));
        }
        return sb.Append('}').ToString();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// F3 : balayage de toute la base, index par index, par lots.
// Pour chaque index : UniqueId (validité), IsPlayer, Minutes.
// Mesure le débit et compte les joueurs ; écrit FM27_Sweep_<date>.tsv au fil de l'eau.
// Nécessite une fiche joueur ouverte (on accroche nos nœuds à son nœud "Person").
// ─────────────────────────────────────────────────────────────────────────────
internal static class Sweep
{
    private const ulong KeyBase = 0x7E80_0000_0000_0000UL;
    private const int MaxIndex = 300_000;
    private const int StopAfterEmpty = 15_000;   // index consécutifs vides avant d'arrêter
    private static readonly string[] Props = { "UniqueId", "IsPlayer", "Minutes" };

    public static bool Active { get; private set; }
    public static bool Owns(ulong key) => key >= KeyBase && key < KeyBase + 0x0040_0000_0000_0000UL; // [0x7E80.., 0x7EC0..)

    private static uint[] _propIds;
    private static uint _propertyValueId;
    private static ulong _nextKey = KeyBase;
    private static int _nextIndex, _batchSize, _batchNo, _emptyRun;
    private static bool _inFlight;
    private static int _batchFrom, _batchTo, _opened, _answered;
    private static DateTime _sweepStart, _batchStart, _lastValueAt;
    private static readonly Dictionary<ulong, (int index, int prop)> Keys = new();
    private static readonly Dictionary<int, string[]> Batch = new();
    private static StreamWriter _out;
    private static string _path;
    private static int _valid, _players, _playersWithMinutes, _maxValidIndex;

    public static void Toggle()
    {
        if (Active) { Finish("arrêt manuel (F3)"); return; }
        if (Probe.Subsystem == null || Probe.PersonTemplate == null || Probe.UiPersonNodeKey == 0)
        {
            Plugin.L.LogWarning("F3 : il faut d'abord afficher une liste de joueurs, puis ouvrir une fiche joueur et la laisser ouverte.");
            return;
        }
        var nameToId = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var kv in PropertyIdentifierSet.Instance.m_idToInfo)
            if (kv.Value?.Name is string n && !nameToId.ContainsKey(n)) nameToId[n] = kv.Key;
        _propIds = Props.Select(p => nameToId[p]).ToArray();
        nameToId.TryGetValue("PropertyValue", out _propertyValueId);

        _nextIndex = 0; _batchSize = 500; _batchNo = 0; _emptyRun = 0;
        _valid = _players = _playersWithMinutes = _maxValidIndex = 0;
        _inFlight = false;
        _path = Path.Combine(Plugin.ExportDir, $"FM27_Sweep_{Plugin.Stamp}.tsv");
        _out = new StreamWriter(_path, false, new UTF8Encoding(true));
        _out.WriteLine("index\tunique_id\tis_player\tminutes");
        _sweepStart = DateTime.Now;
        Active = true;
        Plugin.L.LogMessage("F3 : balayage DÉMARRÉ. Laisse la fiche joueur ouverte. F3 pour arrêter.");
    }

    public static void OnValue(ulong key, TypedValue v)
    {
        if (!Keys.TryGetValue(key, out var slot)) return;
        Keys.Remove(key);
        string s = null;
        try
        {
            if (v is not null && !v.IsNull)
                s = slot.prop == 2 ? Number(v) : v.AsString();
        }
        catch { s = "err"; }
        if (!Batch.TryGetValue(slot.index, out var row)) Batch[slot.index] = row = new string[Props.Length];
        row[slot.prop] = s;
        _answered++;
        _lastValueAt = DateTime.Now;
    }

    // Les stats arrivent en Record {PropertyValue=..., Count=...}
    private static string Number(TypedValue v)
    {
        var o = v.Get();
        var rec = o?.TryCast<SI.Core.Record>();
        if (rec != null)
            return rec.ContainsKey(_propertyValueId) ? rec[_propertyValueId]?.AsString() : null;
        return v.AsString();
    }

    public static void Tick()
    {
        if (!Active) return;
        try
        {
            if (!_inFlight) { OpenBatch(); return; }

            var now = DateTime.Now;
            bool complete = _answered >= _opened;
            bool idle = (now - _lastValueAt).TotalSeconds > 3;
            bool timeout = (now - _batchStart).TotalSeconds > 30;
            if (!complete && !idle && !timeout) return;
            CloseBatch(complete, (now - _batchStart).TotalSeconds);
        }
        catch (Exception e)
        {
            Finish("erreur : " + e.Message);
        }
    }

    private static void OpenBatch()
    {
        if (Probe.UiPersonNodeKey == 0) { Finish("la fiche joueur a été fermée"); return; }
        if (_nextIndex >= MaxIndex) { Finish($"index maximum {MaxIndex} atteint"); return; }
        if (_emptyRun >= StopAfterEmpty && _nextIndex > 1000) { Finish($"{StopAfterEmpty} index vides d'affilée : fin de la base"); return; }

        _batchFrom = _nextIndex;
        _batchTo = Math.Min(_nextIndex + _batchSize, MaxIndex);
        _batchNo++;
        Keys.Clear(); Batch.Clear();
        _opened = _answered = 0;
        var parent = new SI.Bindable.Bindings.Key(Probe.UiPersonNodeKey);
        for (int idx = _batchFrom; idx < _batchTo; idx++)
        {
            var reference = Probe.MakePersonReference(Probe.PersonDrefBase + idx);
            for (int p = 0; p < Props.Length; p++)
            {
                var key = _nextKey++;
                Keys[key] = (idx, p);
                Probe.Subsystem.AddNode(new SI.Bindable.Bindings.Key(key), parent, new PropertyID { id = _propIds[p] });
                Probe.Subsystem.OpenChannel(reference, new PropertyID { id = _propIds[p] }, new SI.Bindable.Bindings.Key(key));
                _opened++;
            }
        }
        _batchStart = _lastValueAt = DateTime.Now;
        _inFlight = true;
        Plugin.L.LogInfo($"  lot {_batchNo} : index {_batchFrom}..{_batchTo - 1} ({_opened} canaux) envoyé");
    }

    private static void CloseBatch(bool complete, double seconds)
    {
        // Nettoyage : fermer et retirer tous les canaux du lot (y compris ceux sans réponse)
        ulong first = _nextKey - (ulong)_opened;
        for (ulong k = first; k < _nextKey; k++)
        {
            try { Probe.Subsystem.CloseChannel(new SI.Bindable.Bindings.Key(k)); } catch { }
            try { Probe.Subsystem.RemoveNode(new SI.Bindable.Bindings.Key(k)); } catch { }
        }

        int validHere = 0;
        for (int idx = _batchFrom; idx < _batchTo; idx++)
        {
            Batch.TryGetValue(idx, out var row);
            var uid = row?[0];
            bool valid = uid != null && long.TryParse(uid, out var u) && u > 0;
            if (!valid) { _emptyRun++; continue; }
            _emptyRun = 0;
            validHere++; _valid++; _maxValidIndex = idx;
            bool isPlayer = row[1] == "True";
            double.TryParse(row[2] ?? "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes);
            if (isPlayer) { _players++; if (minutes >= 1) _playersWithMinutes++; }
            _out.WriteLine($"{idx}\t{uid}\t{row[1]}\t{row[2]}");
        }
        _out.Flush();

        Plugin.L.LogMessage($"  lot {_batchNo} : index {_batchFrom}..{_batchTo - 1}, {_answered}/{_opened} réponses en {seconds:F1}s " +
                            $"({(complete ? "complet" : "INCOMPLET")}), {validHere} personnes valides. " +
                            $"Total : {_valid} personnes, {_players} joueurs, {_playersWithMinutes} avec minutes ≥ 1.");

        _nextIndex = _batchTo;
        _inFlight = false;
        // Adaptation de la taille des lots
        if (complete && seconds < 2 && _batchSize < 4000) _batchSize *= 2;
        else if (!complete && _batchSize > 250) _batchSize /= 2;
    }

    private static void Finish(string reason)
    {
        if (_inFlight) CloseBatch(_answered >= _opened, (DateTime.Now - _batchStart).TotalSeconds);
        Active = false;
        _out?.Dispose(); _out = null;
        var t = (DateTime.Now - _sweepStart).TotalSeconds;
        Plugin.L.LogMessage($"F3 : balayage TERMINÉ ({reason}) en {t:F0}s. Index parcourus : 0..{_nextIndex - 1}. " +
                            $"{_valid} personnes valides (dernier index valide : {_maxValidIndex}), {_players} joueurs, " +
                            $"{_playersWithMinutes} joueurs avec minutes ≥ 1. Débit : {_nextIndex / Math.Max(t, 0.001):F0} index/s. -> {_path}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// F2 : exploration des références liées à un joueur (club, équipe, compétition, nation).
// Après un F6, on a reçu ces références comme valeurs. On demande au moteur TOUTES les
// propriétés que chacune accepte (InteropReference.GetProperties) et on écrit tout dans
// FM27_Refs_<date>.tsv, pour trouver la force du championnat et les stats d'équipe.
// ─────────────────────────────────────────────────────────────────────────────
internal static class RefProbe
{
    private const ulong KeyBase = 0x7EC0_0000_0000_0000UL;
    public static bool Owns(ulong key) => key >= KeyBase && key < KeyBase + 0x0040_0000_0000_0000UL;
    public static bool Active { get; private set; }

    // Propriétés à ne pas ouvrir : sessions d'édition, actions, debug... (effets de bord possibles)
    private static readonly System.Text.RegularExpressions.Regex Skip =
        new("Debug|Edit|Session|Action|Dialog|Popup|Request|Negotiat|Offer|Confirm|Command|Speak|Tutorial|Wizard|Picker|Selected|Selection|Dropdown|Tooltip|Screen|Panel|Menu");

    private static readonly Dictionary<string, InteropReference> Refs = new();   // "Club" -> référence
    private static readonly Dictionary<ulong, (string owner, string prop)> Pending = new();
    private static readonly List<(string owner, string prop, string type, string value)> Results = new();
    private static ulong _nextKey = KeyBase;
    private static DateTime _start, _last;
    private static int _skipped;

    // Appelé par F6 quand une valeur arrive : on garde la première référence de chaque sorte
    public static void Remember(string prop, TypedValue v)
    {
        if (prop is not ("Club" or "Team" or "Division" or "Nation") || Refs.ContainsKey(prop)) return;
        try
        {
            // Les valeurs reçues sont recyclées par le jeu : on copie tout de suite les identifiants
            // et on reconstruit une référence neuve de la bonne classe.
            var r = v?.Get()?.TryCast<InteropReference>();
            if (r == null) return;
            var list = new Il2CppSystem.Collections.Generic.List<InteropReference.Pair>();
            var ids = r.m_id;
            for (int i = 0; i < ids.Count; i++) list.Add(new InteropReference.Pair(ids[i].m_id, ids[i].m_value));
            InteropReference copy = Names.TypeOf(r) switch
            {
                "club" => new FM.UI.ClubReference(list),
                "team" => new FM.UI.TeamReference(list),
                "comp" => new FM.UI.CompReference(list),
                "nation" => new FM.UI.NationReference(list),
                _ => null,
            };
            if (copy == null) return;
            Refs[prop] = copy;
            Plugin.L.LogInfo($"  F2 : référence '{prop}' copiée : {Names.Describe(copy)}");
        }
        catch { }
    }

    public static void Start()
    {
        if (Active) { Plugin.L.LogWarning("F2 : déjà en cours."); return; }
        if (Refs.Count == 0) { Plugin.L.LogWarning("F2 : fais d'abord un F6 (fiche joueur ouverte) pour récupérer club/équipe/compétition."); return; }
        if (Probe.Subsystem == null || Probe.UiParentKey == 0) { Plugin.L.LogWarning("F2 : ouvre une fiche joueur et laisse-la ouverte."); return; }

        Pending.Clear(); Results.Clear(); _skipped = 0;
        int opened = 0;
        foreach (var (refName, r) in Refs)
        foreach (var (variant, parentKey) in Parents(refName))
        {
            var owner = refName + "/" + variant;
            Plugin.L.LogInfo($"  F2 : {owner} -> {Names.Describe(r)} ({r.GetIl2CppType().Name})");
            var parent = new SI.Bindable.Bindings.Key(parentKey);
            var props = new Il2CppSystem.Collections.Generic.List<PropertyID>();
            try { r.GetProperties(props); } catch (Exception e) { Plugin.L.LogWarning($"  F2 : GetProperties({owner}) : {e.Message}"); continue; }
            int n = 0;
            for (int i = 0; i < props.Count; i++)
            {
                var pid = props[i];
                var name = Names.Property(pid.id);
                if (Skip.IsMatch(name)) { _skipped++; continue; }
                var key = _nextKey++;
                Pending[key] = (owner, name);
                Probe.Subsystem.AddNode(new SI.Bindable.Bindings.Key(key), parent, pid);
                Probe.Subsystem.OpenChannel(r, pid, new SI.Bindable.Bindings.Key(key));
                n++; opened++;
            }
            Plugin.L.LogMessage($"  F2 : {owner} ({Names.TypeOf(r)}) : {props.Count} propriétés acceptées, {n} demandées");
        }
        _start = _last = DateTime.Now;
        Active = true;
        Plugin.L.LogMessage($"F2 : {opened} canaux ouverts ({_skipped} propriétés ignorées par prudence).");
    }

    // Parents à essayer : le conteneur générique (#0) de la fiche, et le nœud typé de l'interface s'il existe
    private static IEnumerable<(string, ulong)> Parents(string refName)
    {
        if (Probe.UiParentKey != 0) yield return ("#0", Probe.UiParentKey);
        var typed = refName == "Division" ? (Probe.UiTypedNodes.TryGetValue("Division", out var d) ? d : 0) : 0;
        if (typed == 0) Probe.UiTypedNodes.TryGetValue(refName, out typed);
        if (typed != 0) yield return ("typé", typed);
    }

    public static void OnValue(ulong key, TypedValue v)
    {
        if (!Pending.TryGetValue(key, out var slot)) return;
        Pending.Remove(key);
        string s, t;
        try { s = ValueText.Format(v, -3); } catch (Exception e) { s = "err:" + e.GetType().Name; }  // -3 : 3 niveaux de plus
        try { t = v?.DataType?.Name ?? "?"; } catch { t = "?"; }
        s = (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        // Valeurs complètes pour les structures de stats qui nous intéressent, tronquées sinon
        bool full = slot.prop is "GeneralPerformanceVisual" or "ReportStatsData" or "XGStats" or "OfficialStats"
            or "TeamStats" or "RecentPerformance" or "FactsData" or "BestXIOverview" or "SquadPositionDepthAndQualityOverview";
        if (!full && s.Length > 600) s = s.Substring(0, 600) + "…";
        Results.Add((slot.owner, slot.prop, t, s));
        _last = DateTime.Now;
    }

    public static void Tick()
    {
        if (!Active) return;
        var now = DateTime.Now;
        if (Pending.Count > 0 && (now - _last).TotalSeconds < 5 && (now - _start).TotalSeconds < 30) return;
        Active = false;

        // Nettoyage de tous nos canaux/nœuds F2
        for (ulong k = KeyBase; k < _nextKey; k++)
        {
            try { Probe.Subsystem.CloseChannel(new SI.Bindable.Bindings.Key(k)); } catch { }
            try { Probe.Subsystem.RemoveNode(new SI.Bindable.Bindings.Key(k)); } catch { }
        }

        var path = Path.Combine(Plugin.ExportDir, $"FM27_Refs_{Plugin.Stamp}.tsv");
        using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            w.WriteLine("objet\tpropriete\ttype\tvaleur");
            foreach (var r in Results.OrderBy(r => r.owner).ThenBy(r => r.prop, StringComparer.Ordinal))
                w.WriteLine($"{r.owner}\t{r.prop}\t{r.type}\t{r.value}");
        }
        Plugin.L.LogMessage($"F2 : terminé. {Results.Count} valeurs reçues, {Pending.Count} sans réponse. -> {path}");
        foreach (var g in Results.GroupBy(r => r.owner)) Plugin.L.LogMessage($"    {g.Key} : {g.Count()} valeurs");
        Pending.Clear();
    }
}
