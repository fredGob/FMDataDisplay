// Calcul des notes dans le jeu, et automatiquement chaque semaine de jeu.
//
//   GameDate : date du jeu, lue en écoutant les canaux de l'interface (CurrentGameDate…).
//   Anchor   : nœuds de l'interface auxquels accrocher nos lectures. Tout nœud « Person » affiché convient
//              (liste, effectif, fiche joueur) ; sinon on essaie un nœud qui ne disparaît pas (« Human »…).
//   NoteAuto : après chaque extraction complète (F9 ou automatique), calcul des notes (NoteEngine, sur un fil
//              à part) et des étoiles d'entraînement des préparateurs (StaffEngine), puis rechargement des colonnes. Chaque semaine de jeu (jour réglable), quand la partie est
//              arrêtée depuis quelques secondes, l'extraction est lancée toute seule.
//
// Réglages : BepInEx/config/fm27explorer_notes.txt (créé au premier lancement).
// État      : BepInEx/config/fm27_notes_etat.txt (date du jeu de la dernière extraction réussie).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FM27Note;
using SI.Bindable.Reference.Core;
using SI.Interop;
using DateTime = System.DateTime;
using TypedValue = SI.Core.TypedValue;

namespace FM27Explorer;

// ─────────────────────────────────────────────────────────────────────────────
// Date du jeu
// ─────────────────────────────────────────────────────────────────────────────
internal static class GameDate
{
    // Sources utilisées pour décider (par ordre de préférence) ; les autres sont seulement notées dans le log
    private static readonly string[] Sources = { "CurrentGameDate", "CurrentGameDateString" };
    private static readonly string[] Discovery = { "HumanCalendarDate", "FixturesCurrentDate", "TransferListCurrentDate" };

    private static Dictionary<uint, string> _ids;
    private static readonly Dictionary<ulong, string> UiKeys = new();     // canal de l'interface -> propriété
    private static readonly Dictionary<string, string> Latest = new();    // propriété -> dernière valeur
    private static readonly HashSet<string> Logged = new();

    public static DateTime? Date { get; private set; }
    public static string Text { get; private set; } = "";
    public static string Source { get; private set; } = "";
    public static DateTime LastChange { get; private set; } = DateTime.MinValue;
    public static bool Watching => UiKeys.Count > 0;

    public static void OnUiOpenChannel(InteropReference reference, uint prop, ulong key)
    {
        if (_ids == null)
        {
            _ids = new Dictionary<uint, string>();
            try
            {
                foreach (var kv in PropertyIdentifierSet.Instance.m_idToInfo)
                    if (kv.Value?.Name is string n && (Sources.Contains(n) || Discovery.Contains(n))) _ids[kv.Key] = n;
            }
            catch (Exception e) { Plugin.L.LogWarning("Date du jeu : identifiants : " + e.Message); }
        }
        if (!_ids.TryGetValue(prop, out var name)) return;
        UiKeys[key] = name;
        if (Logged.Add("canal " + name))
        {
            string r = "?";
            try { r = Names.Describe(reference); } catch { }
            Plugin.L.LogMessage($"Date du jeu : l'interface lit « {name} » ({r}).");
        }
    }

    public static void OnUiCloseChannel(ulong key) => UiKeys.Remove(key);

    public static void OnValue(ulong key, TypedValue v)
    {
        if (!UiKeys.TryGetValue(key, out var name)) return;
        string s;
        try { s = Extract.Cell(v); } catch { return; }
        if (string.IsNullOrEmpty(s)) return;
        if (Logged.Add("valeur " + name)) Plugin.L.LogMessage($"Date du jeu : « {name} » = « {s} »" + (Parse(s) is DateTime d ? $" -> {d:yyyy-MM-dd}" : " (format non reconnu)"));
        if (!Sources.Contains(name)) return;
        Latest[name] = s;

        // Meilleure source lisible ; à défaut, le texte brut sert au moins à voir que la date change
        DateTime? best = null;
        string src = null, text = null;
        foreach (var n in Sources)
            if (Latest.TryGetValue(n, out var t) && Parse(t) is DateTime pd) { best = pd; src = n; text = t; break; }
        if (src == null)
        {
            src = Sources.First(Latest.ContainsKey);
            text = Latest[src];
        }
        if (text == Text) return;
        Text = text; Source = src; Date = best; LastChange = DateTime.Now;
        if (best == null && Logged.Add("illisible"))
            Plugin.L.LogWarning($"Date du jeu illisible (« {text} ») : le calcul automatique ne peut pas choisir le jour. F9 marche toujours.");
    }

    private static readonly (string prefix, int month)[] Months =
    {
        ("janv", 1), ("jan", 1), ("févr", 2), ("fevr", 2), ("fév", 2), ("feb", 2), ("mars", 3), ("mar", 3),
        ("avr", 4), ("apr", 4), ("mai", 5), ("may", 5), ("juin", 6), ("jun", 6), ("juil", 7), ("jul", 7),
        ("août", 8), ("aout", 8), ("aug", 8), ("sept", 9), ("sep", 9), ("oct", 10), ("nov", 11), ("déc", 12), ("dec", 12),
    };

    // Jour de l'année compté à partir de 1 (1 = 1er janvier) : vérifié le 3 nov. 2026 (code 265694976 = mardi 16 févr. 2027)
    internal static int DayOffset = 1;

    // Formats acceptés : date FM « GameDate { Data1 = N, Type = FmdateSiDate } » (N = année << 17 | jour de l'année << 8),
    // 2026-11-03, 03/11/2026 (jour/mois), « lun. 3 nov. 2026 », « 3 November 2026 », « November 3, 2026 »
    internal static DateTime? Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var low = s.Trim().ToLowerInvariant();
        var fm = Regex.Match(low, @"data1\s*=\s*(\d+)");
        if (fm.Success && long.TryParse(fm.Groups[1].Value, out var code))
        {
            int year = (int)(code >> 17), dayOfYear = (int)((code >> 8) & 0x1FF);
            if (year < 1900 || year > 2200 || dayOfYear > 366) return null;
            try { return new DateTime(year, 1, 1).AddDays(dayOfYear - DayOffset); } catch { return null; }
        }
        var m = Regex.Match(low, @"(\d{4})-(\d{1,2})-(\d{1,2})");
        if (m.Success) return Make(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        m = Regex.Match(low, @"(\d{1,2})[/.](\d{1,2})[/.](\d{4})");
        if (m.Success) return Make(m.Groups[3].Value, m.Groups[2].Value, m.Groups[1].Value);
        m = Regex.Match(low, @"(\d{1,2})(?:er|st|nd|rd|th)?\s+([^\s\d.,]+)\.?,?\s+(\d{4})");
        if (m.Success && Month(m.Groups[2].Value) is int mo1) return Make(m.Groups[3].Value, mo1.ToString(), m.Groups[1].Value);
        m = Regex.Match(low, @"([^\s\d.,]+)\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})");
        if (m.Success && Month(m.Groups[1].Value) is int mo2) return Make(m.Groups[3].Value, mo2.ToString(), m.Groups[2].Value);
        return null;
    }

    private static int? Month(string word)
    {
        foreach (var (prefix, month) in Months)
            if (word.StartsWith(prefix, StringComparison.Ordinal)) return month;
        return null;
    }

    private static DateTime? Make(string y, string mo, string d)
    {
        try { return new DateTime(int.Parse(y), int.Parse(mo), int.Parse(d)); }
        catch { return null; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Points d'accroche de nos lectures dans l'arbre des nœuds de l'interface
// ─────────────────────────────────────────────────────────────────────────────
internal static class Anchor
{
    private const ulong KeyBase = 0x7E20_0000_0000_0000UL;
    public static bool Owns(ulong key) => key >= KeyBase && key < KeyBase + 0x0020_0000_0000_0000UL; // [0x7E20.., 0x7E40..)
    private static ulong _nextKey = KeyBase;

    private static readonly List<ulong> Persons = new();                  // nœuds « Person » vivants (ordre de création)
    private static readonly Dictionary<ulong, ulong> PersonParent_ = new(); // nœud « Person » -> son parent
    private static readonly HashSet<ulong> Alive = new();                  // nœuds suivis encore vivants
    private static readonly List<ulong> Candidates = new();                // nœuds peut-être permanents (« Human » et parents)
    private static readonly HashSet<ulong> Rejected = new();

    public static ulong Permanent { get; private set; }                    // nœud permanent validé pour lire des joueurs
    private const int MaxTests = 6;                                        // essais au plus par session

    public static void OnUiAddNode(ulong key, ulong parent, string prop)
    {
        if (prop == "Person")
        {
            Persons.Add(key);
            PersonParent_[key] = parent;
            Alive.Add(key); Alive.Add(parent);
            if (Persons.Count > 5000) { PersonParent_.Remove(Persons[0]); Persons.RemoveAt(0); }
        }
        else if (prop == "Human")
        {
            Alive.Add(key); Alive.Add(parent);
            if (!Candidates.Contains(key)) Candidates.Add(key);
            if (!Candidates.Contains(parent)) Candidates.Add(parent);
        }
    }

    public static void OnUiRemoveNode(ulong key)
    {
        if (!Alive.Remove(key)) return;
        if (PersonParent_.Remove(key)) Persons.Remove(key);
        Candidates.Remove(key);
        if (key == Permanent)
        {
            Permanent = 0;
            Plugin.L.LogMessage("Notes : le nœud permanent a disparu, on en cherchera un autre.");
        }
    }

    public static bool IsAlive(ulong key) => key != 0 && Alive.Contains(key);

    // Parent pour lire des joueurs : un nœud « Person » de l'interface, sinon le nœud permanent validé
    public static ulong PersonParent
    {
        get
        {
            if (Probe.UiPersonNodeKey != 0) return Probe.UiPersonNodeKey;
            for (int i = Persons.Count - 1; i >= 0; i--)
                if (Alive.Contains(Persons[i])) return Persons[i];
            return Permanent;
        }
    }

    // Parent pour lire clubs, compétitions, pays : un conteneur de l'interface (parent d'un nœud « Person »)
    public static ulong GenericParent
    {
        get
        {
            if (Probe.UiParentKey != 0) return Probe.UiParentKey;
            for (int i = Persons.Count - 1; i >= 0; i--)
                if (Alive.Contains(Persons[i]) && PersonParent_.TryGetValue(Persons[i], out var p) && Alive.Contains(p)) return p;
            return Permanent;
        }
    }

    public static string Status() =>
        $"accroche joueurs {(PersonParent != 0 ? "OK" : "aucune")}, {Persons.Count(Alive.Contains)} nœud(s) joueur affiché(s), " +
        $"nœud permanent {(Permanent != 0 ? $"0x{Permanent:X16}" : "aucun")} ({Candidates.Count(c => Alive.Contains(c) && !Rejected.Contains(c))} candidat(s) à essayer, {Rejected.Count} refusé(s))";

    // ── Essai d'un nœud permanent : 10 joueurs × UniqueId ──
    public static bool Testing { get; private set; }
    private static ulong _testNode;
    private static readonly HashSet<ulong> TestKeys = new();
    private static int _answers, _opened;
    private static DateTime _testStart;

    // Lance l'essai du prochain candidat ; false s'il n'y a plus de candidat
    public static bool TryNextCandidate()
    {
        if (Testing) return true;
        if (Probe.Subsystem == null || Probe.PersonTemplate == null || Rejected.Count >= MaxTests) return false;
        var c = Candidates.FirstOrDefault(k => Alive.Contains(k) && !Rejected.Contains(k));
        if (c == 0) return false;
        uint pid = 0;
        foreach (var kv in PropertyIdentifierSet.Instance.m_idToInfo)
            if (kv.Value?.Name == "UniqueId") { pid = kv.Key; break; }
        if (pid == 0) return false;

        _testNode = c;
        _answers = _opened = 0;
        TestKeys.Clear();
        var parent = new SI.Bindable.Bindings.Key(c);
        for (int i = 0; i < 10; i++)
        {
            var key = _nextKey++;
            TestKeys.Add(key);
            var p = new PropertyID { id = pid };
            Probe.Subsystem.AddNode(new SI.Bindable.Bindings.Key(key), parent, p);
            Probe.Subsystem.OpenChannel(Probe.MakePersonReference(Probe.PersonDrefBase + i), p, new SI.Bindable.Bindings.Key(key));
            _opened++;
        }
        _testStart = DateTime.Now;
        Testing = true;
        Plugin.L.LogMessage($"Notes : essai d'un nœud permanent (0x{c:X16}) pour lire les joueurs sans écran de joueurs…");
        return true;
    }

    public static void OnValue(ulong key, TypedValue v)
    {
        if (TestKeys.Remove(key)) _answers++;
    }

    public static void Tick()
    {
        if (!Testing) return;
        bool done = _answers >= _opened || (DateTime.Now - _testStart).TotalSeconds > 5;
        if (!done) return;
        for (ulong k = _nextKey - (ulong)_opened; k < _nextKey; k++)
        {
            try { Probe.Subsystem.CloseChannel(new SI.Bindable.Bindings.Key(k)); } catch { }
            try { Probe.Subsystem.RemoveNode(new SI.Bindable.Bindings.Key(k)); } catch { }
        }
        TestKeys.Clear();
        Testing = false;
        if (_answers >= _opened * 8 / 10)
        {
            Permanent = _testNode;
            Plugin.L.LogMessage($"Notes : nœud permanent ACCEPTÉ ({_answers}/{_opened} réponses) : plus besoin d'écran de joueurs.");
        }
        else
        {
            Rejected.Add(_testNode);
            Plugin.L.LogMessage($"Notes : nœud permanent refusé ({_answers}/{_opened} réponses).");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Calcul des notes et déclenchement hebdomadaire
// ─────────────────────────────────────────────────────────────────────────────
internal static class NoteAuto
{
    private static string ConfigPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27explorer_notes.txt");
    private static string StatePath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27_notes_etat.txt");
    private static string NotesPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27_notes.tsv");
    private static string StaffNotesPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27_staff.tsv");

    private static bool _loaded, _auto = true;
    private static DayOfWeek _day = DayOfWeek.Monday;
    private static double _waitSeconds = 15;
    private static int _keep = 12;
    public static bool StaffEnabled { get; private set; } = true;   // lire aussi les préparateurs (≈ 30 s de plus)
    private static bool _devKeys;
    // Touches de développement (F2–F8, F10, F11) : désactivées par défaut pour laisser les raccourcis au jeu
    public static bool DevKeys { get { if (!_loaded) LoadConfig(); return _devKeys; } }
    private static DateTime? _lastDone;          // date du jeu de la dernière extraction réussie
    private static DateTime? _lastAttempt;       // date du jeu du dernier essai automatique (un essai par jour de jeu)
    private static string _waiting;              // dernier message d'attente écrit (évite de le répéter)
    private static Task<string> _task;
    private static DateTime _computeStart;

    private static readonly string[] DayNames = { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };

    private const string DefaultConfig =
@"# Calcul automatique des notes (plugin FM27 Explorer)
# auto=1 : chaque semaine de jeu, le jour choisi, quand la partie est arrêtée depuis quelques secondes, le plugin
#          relit tous les joueurs (≈ 3 min, sans rien bloquer) puis recalcule les notes et recharge les colonnes.
# auto=0 : seulement quand on appuie sur F9.
auto=1
# jour de la semaine (date du jeu) : lundi, mardi, mercredi, jeudi, vendredi, samedi, dimanche
jour=lundi
# secondes sans changement de date avant de lancer (le temps que le jeu finisse de jouer les journées)
attente=15
# nombre d'extractions gardées dans Documents/Sports Interactive/Football Manager 27/exports (0 = toutes)
garder=12
# preparateurs=1 : lit aussi le staff (≈ 30 s de plus) et calcule les étoiles d'entraînement des préparateurs
preparateurs=1
# touches_dev=1 : réactive les touches de développement (F2–F8, F10, F11). Par défaut seule F9 est utilisée,
#                 pour ne pas gêner les raccourcis du jeu. Pris en compte au lancement du jeu.
touches_dev=0
";

    public static void LoadConfig()
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
                    case "auto": _auto = v is "1" or "oui" or "true"; break;
                    case "jour": int d = Array.IndexOf(DayNames, v); if (d >= 0) _day = (DayOfWeek)d; break;
                    case "attente": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) _waitSeconds = w; break;
                    case "garder": if (int.TryParse(v, out var g)) _keep = g; break;
                    case "preparateurs": StaffEnabled = v is "1" or "oui" or "true"; break;
                    case "touches_dev": _devKeys = v is "1" or "oui" or "true"; break;
                }
            }
            if (File.Exists(StatePath))
                foreach (var line in File.ReadAllLines(StatePath))
                    if (line.StartsWith("date_jeu=") && DateTime.TryParseExact(line.Substring(9).Trim(), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var ld))
                        _lastDone = ld;
        }
        catch (Exception e) { Plugin.L.LogWarning("Notes : réglages : " + e.Message); }
        Plugin.L.LogMessage($"Notes : calcul automatique {(_auto ? $"ACTIF, chaque {DayNames[(int)_day]} (date du jeu)" : "désactivé (F9 seulement)")}" +
                            (_lastDone is DateTime l ? $", dernière extraction le {l:dd/MM/yyyy}." : ", aucune extraction encore."));
    }

    public static string Status()
    {
        string date = GameDate.Date is DateTime d ? $"{DayNames[(int)d.DayOfWeek]} {d:dd/MM/yyyy}" : (GameDate.Text != "" ? $"« {GameDate.Text} » (illisible)" : "inconnue");
        return $"notes : date du jeu {date}, auto {(_auto ? DayNames[(int)_day] : "non")}, dernière extraction " +
               (_lastDone is DateTime l ? l.ToString("dd/MM/yyyy") : "aucune") + (_task != null ? ", calcul en cours" : "") + " ; " + Anchor.Status();
    }

    public static void Tick()
    {
        if (!_loaded) LoadConfig();
        Anchor.Tick();
        if (_task != null && _task.IsCompleted) EndCompute();

        if (!_auto || _task != null || Extract.Active || Sweep.Active || Probe.Active || RefProbe.Active || Anchor.Testing) return;
        if (GameDate.Date is not DateTime date) return;
        if ((DateTime.Now - GameDate.LastChange).TotalSeconds < _waitSeconds) return;
        if (!IsDue(date) || _lastAttempt == date) return;

        if (Probe.Subsystem == null || Probe.PersonTemplate == null) { Waiting(date, "le jeu n'a encore affiché aucun joueur"); return; }
        if (Anchor.PersonParent == 0)
        {
            if (Anchor.TryNextCandidate()) return;   // essai d'un nœud permanent (résultat dans quelques secondes)
            Waiting(date, "affiche un écran avec des joueurs (effectif, liste, fiche) pour la lancer");
            return;
        }
        _lastAttempt = date;
        _waiting = null;
        Plugin.L.LogMessage($"Notes : nouvelle semaine ({DayNames[(int)date.DayOfWeek]} {date:dd/MM/yyyy}), extraction automatique lancée (≈ 3 min).");
        Extract.Start(auto: true);
    }

    private static bool IsDue(DateTime date) =>
        _lastDone is not DateTime last
        || Math.Abs((date - last).TotalDays) >= 7
        || (date.DayOfWeek == _day && date != last);

    private static void Waiting(DateTime date, string why)
    {
        var msg = $"{date:yyyy-MM-dd} {why}";
        if (_waiting == msg) return;
        _waiting = msg;
        Plugin.L.LogMessage($"Notes : extraction de la semaine en attente : {why}.");
    }

    // Appelé par Extract à la fin d'une extraction (manuelle ou automatique)
    public static void OnExtractDone(string stamp, bool complete)
    {
        if (!complete) return;
        // Date du jeu au début de la lecture (on peut continuer à jouer pendant l'extraction)
        if ((Extract.StartGameDate ?? GameDate.Date) is DateTime d)
        {
            _lastDone = d;
            try { File.WriteAllText(StatePath, $"date_jeu={d:yyyy-MM-dd}\nextraction={stamp}\n"); } catch { }
        }
        if (_task != null) { Plugin.L.LogWarning("Notes : un calcul est déjà en cours."); return; }
        var dir = Plugin.ExportDir;
        var notesPath = NotesPath;
        var staffNotesPath = StaffNotesPath;
        var detailPath = Moneyball.DetailPath;
        int keep = _keep;
        string gameDate = (Extract.StartGameDate ?? GameDate.Date) is DateTime gd ? gd.ToString("yyyy-MM-dd") : "";
        _computeStart = DateTime.Now;
        Plugin.L.LogMessage("Notes : calcul en cours (quelques secondes, le jeu reste utilisable)…");
        _task = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var warnings = new List<string>();
            var data = NoteData.Load(dir, stamp, warnings.Add);
            if (data.Players.Count == 0) throw new InvalidOperationException("aucun joueur lu dans l'extraction " + stamp);
            var settings = new NoteSettings();
            NoteEngine.ComputeAll(data.Players, settings);
            int n = NoteData.WriteGameNotes(notesPath, data.Players);
            // Détail stat par stat pour la fiche joueur (mode Moneyball)
            try { NoteData.WriteGameDetails(detailPath, data.Players, settings); }
            catch (Exception e) { warnings.Add("détail des notes : " + e.Message); }
            // Fichier lisible pour l'assistant de recrutement (exports/FM27_Recrutement.tsv, nom fixe)
            try { NoteData.WriteRecruitment(dir, data, gameDate); }
            catch (Exception e) { warnings.Add("fichier de recrutement : " + e.Message); }
            string staff = "";
            try
            {
                var list = NoteData.LoadStaff(dir, stamp, warnings.Add);
                if (list.Count > 0)
                {
                    StaffEngine.Compute(list);
                    int ns = NoteData.WriteGameStaff(staffNotesPath, list);
                    staff = $", {ns} préparateurs ({list.Count(x => x.Masked)} avec des attributs en fourchette)";
                    try { NoteData.WriteRecruitmentStaff(dir, data, list); }
                    catch (Exception e) { warnings.Add("fichier de recrutement du staff : " + e.Message); }
                }
            }
            catch (Exception e) { warnings.Add("préparateurs : " + e.Message); }
            string cleaned = Cleanup(dir, keep);
            return $"{n} joueurs notés sur {data.Players.Count}{staff} en {sw.Elapsed.TotalSeconds:F0} s" +
                   (warnings.Count > 0 ? " (" + string.Join("; ", warnings) + ")" : "") + cleaned;
        });
    }

    private static void EndCompute()
    {
        var t = _task;
        _task = null;
        if (t.IsFaulted)
        {
            Plugin.L.LogError("Notes : échec du calcul : " + t.Exception?.GetBaseException());
            return;
        }
        string next = "";
        if (_auto && _lastDone is DateTime last)
        {
            int days = ((int)_day - (int)last.DayOfWeek + 7) % 7;
            next = $" Prochaine mise à jour : {DayNames[(int)_day]} {last.AddDays(days == 0 ? 7 : days):dd/MM/yyyy}.";
        }
        Plugin.L.LogMessage("Notes : " + t.Result + ". Colonnes rechargées." + next);
        Colonne.Reload();
        Moneyball.OnNotesReloaded();
    }

    // Garde les « keep » dernières extractions (fichiers FM27_Joueurs/Staff/Clubs/Competitions_<date>.tsv)
    private static string Cleanup(string dir, int keep)
    {
        if (keep <= 0) return "";
        var stamps = Directory.GetFiles(dir, "FM27_Joueurs_*.tsv")
                              .Select(f => Path.GetFileNameWithoutExtension(f).Substring("FM27_Joueurs_".Length))
                              .OrderByDescending(s => s, StringComparer.Ordinal).Skip(keep).ToList();
        int removed = 0;
        foreach (var s in stamps)
            foreach (var prefix in new[] { "FM27_Joueurs_", "FM27_Staff_", "FM27_Clubs_", "FM27_Competitions_", "FM27_Contrats_" })
            {
                var f = Path.Combine(dir, prefix + s + ".tsv");
                try { if (File.Exists(f)) { File.Delete(f); removed++; } } catch { }
            }
        return removed > 0 ? $", {stamps.Count} ancienne(s) extraction(s) supprimée(s)" : "";
    }
}
