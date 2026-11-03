// F9 : extraction complète des joueurs, pour concevoir la note hors du jeu.
//
//   1. Balayage de toute la base (UniqueId, IsPlayer, Minutes)  -> joueurs avec au moins 1 minute, et non-joueurs
//   2. Lecture d'environ 65 propriétés pour chacun de ces joueurs -> FM27_Joueurs_<date>.tsv
//   3. Attributs de coaching des non-joueurs (préparateurs)       -> FM27_Staff_<date>.tsv (réglage preparateurs=1)
//   4. Clubs rencontrés (nom, réputation, championnat)           -> FM27_Clubs_<date>.tsv
//   5. Compétitions rencontrées (nom, réputation, étoiles, pays)  -> FM27_Competitions_<date>.tsv
//   6. Pays de ces compétitions (nom)
//
// Nos nœuds sont accrochés à ceux de l'interface : il faut un écran avec des joueurs (liste, effectif, fiche),
// ou un nœud permanent validé (voir Auto.cs). Si plus aucun joueur n'est affiché, l'extraction se met en pause.
// F9 pendant l'extraction = arrêt. À la fin, les notes sont calculées (NoteAuto) et les colonnes rechargées.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SI.Bindable.Reference.Core;
using SI.Interop;
using DateTime = System.DateTime;
using TypedValue = SI.Core.TypedValue;

namespace FM27Explorer;

internal static class Extract
{
    private const ulong KeyBase = 0x7E40_0000_0000_0000UL;
    public static bool Owns(ulong key) => key >= KeyBase && key < KeyBase + 0x0040_0000_0000_0000UL; // [0x7E40.., 0x7E80..)
    public static bool Active { get; private set; }

    private const int MaxIndex = 300_000;
    private const int StopAfterEmpty = 15_000;
    private const int MinChannels = 1_000, MaxChannels = 12_000;

    // Propriétés lues pour chaque joueur
    // Propriétés de base, complétées par BepInEx/config/fm27explorer_extraction.txt (une par ligne, # = commentaire)
    private static string ExtraPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27explorer_extraction.txt");
    private static string[] PlayerProps = BaseProps;

    public static readonly string[] BaseProps =
    {
        // Profil
        "UniqueId", "Name", "Age", "PositionCombinedString", "Nation", "Club", "Team", "Division",
        "CurrentAbilityStarRange", "PotentialAbilityStarRange",
        // Totaux de la saison
        "Appearances", "Minutes", "AverageRating", "Goals", "Assists", "TeamGoals", "TeamConceded",
        // Par 90 min (sélection de Fred)
        "AssistsPer90", "BlocksPer90", "ChancesCreatedPer90", "ClearancesPer90", "DistanceRanPer90",
        "DribblesMadePer90", "ExpectedAssistsPer90", "ExpectedGoalsPreventedPer90", "GoalsPer90",
        "HeaderAttemptsPer90", "HeadersCompletedPer90", "HeadersLostPer90", "InterceptionsPer90",
        "KeyHeadersPer90", "KeyTacklesPer90", "NonPenaltyExpectedGoalsPer90", "OpenPlayCrossesCompletedPer90",
        "OpenPlayKeyPassesPer90", "PassCompletedPer90", "PossessionLostPer90", "PossessionWonPer90",
        "PressuresAttemptedPer90", "PressuresCompletedPer90", "ProgressivePassesPer90", "ShotsOnTargetPer90",
        "ShotsPer90", "SprintsPer90", "TacklesCompletedPer90", "TotalSavesPer90",
        // Pourcentages (sélection de Fred)
        "GoalsPerShotPercentage", "HeaderCompletionPercentage", "OpenPlayCrossCompletionPercentage",
        "PassCompletionPercentage", "ShotsOnTargetPercentage", "TackleCompletionPercentage",
        "TotalExpectedSavePercentage", "TotalSavePercentage", "InjuriesPercentageMissed",
        // En plus, pour garder de la souplesse dans la formule
        "KeyPassesPer90", "ConcededPer90", "CleanSheetsPer90", "ExpectedGoalsPer90", "PassAttemptsPer90",
        "CrossAttemptsPer90", "OpenPlayCrossesAttemptedPer90", "TeamGoalsPer90", "TeamConcededPer90",
    };

    // Préparateurs : attributs du calcul des étoiles d'entraînement (FM27Note.StaffEngine) + de quoi les reconnaître
    private static readonly string[] StaffBaseProps =
        new[] { "UniqueId", "Name", "Age", "Club", "JobString" }
        .Concat(FM27Note.StaffEngine.Attributes)
        .Concat(new[] { "WorkingWithYoungster", "PeopleManagement", "NonPlayerCurrentAbility", "NonPlayerPotentialAbility" })
        .ToArray();
    private static string[] StaffProps = StaffBaseProps;

    private static readonly string[] ScanProps = { "UniqueId", "IsPlayer", "Minutes" };
    private static readonly string[] ClubProps = { "Name", "Reputation", "Division" };
    private static readonly string[] CompProps = { "Name", "ShortName", "Reputation", "ReputationStars", "Nation" };
    private static readonly string[] NationProps = { "Name" };
    // Fin de contrat (0.12.0) : le joueur donne son contrat (FullContract, une référence « contract »), le contrat sa
    // date de fin (EndDate) ; comme la colonne « Expire le » du jeu (binding.fullcontract.enddate)
    private const string ContractProp = "FullContract";
    private static readonly string[] ContractProps = { "EndDate" };
    private static readonly Dictionary<int, InteropReference> ContractRefs = new();   // index du joueur -> son contrat
    private static readonly Dictionary<int, string> ContractEnds = new();
    private static bool _contracts, _capturingContracts;
    private static int _contractCol = -1;

    private sealed class Phase
    {
        public string Label;
        public string[] Props;
        public uint[] PropIds;
        public List<int> Items;                     // index de personne, ou dref de club/compétition/pays
        public Func<int, InteropReference> MakeRef;
        public Func<ulong> Parent;                  // nœud de l'interface auquel accrocher les nôtres
        public Action<int, string[]> OnRow;         // appelée pour chaque élément, à la fin de son lot
        public Action OnEnd;                        // fin de la phase (fermeture du fichier écrit)
        public Func<string> Summary;                // bilan ajouté au message de fin de phase
        public bool Scan;                           // s'arrête après beaucoup d'index vides
        public int Pos;
    }

    private static readonly Queue<Func<Phase>> Pending = new();
    private static Phase _phase;
    private static ulong _nextKey = KeyBase;
    private static readonly Dictionary<ulong, (int item, int prop)> Slots = new();
    private static readonly Dictionary<int, string[]> Rows = new();
    private static int _from, _to, _opened, _answered, _channels, _batchNo, _emptyRun, _missingTotal;
    private static bool _inFlight;
    private static ulong _batchParent;
    private static DateTime? _pausedSince;
    private static DateTime _start, _phaseStart, _batchStart, _lastValueAt;

    // Résultats
    private static readonly List<int> Targets = new();
    private static readonly List<int> StaffTargets = new();       // non-joueurs
    private static int _valid, _players;
    private static StreamWriter _playersOut, _staffOut;
    private static string _stamp, _playersPath, _staffPath;
    private static int _playersWritten, _staffWritten, _staffEmpty;
    private static readonly Dictionary<string, Dictionary<int, InteropReference>> SeenRefs = new()
    {
        ["club"] = new(), ["comp"] = new(), ["nation"] = new(), ["team"] = new(),
    };
    private static readonly Dictionary<int, string[]> Clubs = new(), Comps = new(), Nations = new();

    private static Dictionary<string, uint> _nameToId;
    private static readonly Dictionary<uint, string> IdName = new();

    // ── Démarrage / arrêt ──────────────────────────────────────────────────

    public static void Toggle()
    {
        if (Active) { Finish("arrêt manuel (F9)"); return; }
        Start(auto: false);
    }

    public static DateTime? StartGameDate { get; private set; }   // date du jeu au lancement de l'extraction

    public static bool Start(bool auto)
    {
        if (Active) return false;
        if (Sweep.Active || Probe.Active || RefProbe.Active || Anchor.Testing) { Plugin.L.LogWarning("F9 : une autre lecture est en cours, attends qu'elle finisse."); return false; }
        if (Probe.Subsystem == null || Probe.PersonTemplate == null || Anchor.PersonParent == 0 || Anchor.GenericParent == 0)
        {
            Plugin.L.LogWarning("F9 : il faut d'abord afficher un écran avec des joueurs (effectif, liste, fiche joueur).");
            return false;
        }

        PlayerProps = BaseProps;
        try
        {
            if (File.Exists(ExtraPath))
            {
                var extra = File.ReadAllLines(ExtraPath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#"));
                PlayerProps = BaseProps.Concat(extra).Distinct().ToArray();
                Plugin.L.LogMessage($"F9 : {PlayerProps.Length - BaseProps.Length} propriétés en plus lues dans {ExtraPath}");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("F9 : lecture de " + ExtraPath + " : " + e.Message); }
        _pausedSince = null;

        _nameToId = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var kv in PropertyIdentifierSet.Instance.m_idToInfo)
            if (kv.Value?.Name is string n && !_nameToId.ContainsKey(n)) _nameToId[n] = kv.Key;
        var unknownExtra = PlayerProps.Except(BaseProps).Where(p => !_nameToId.ContainsKey(p)).ToList();
        if (unknownExtra.Count > 0)
        {
            Plugin.L.LogWarning("F9 : propriétés inconnues ignorées : " + string.Join(", ", unknownExtra));
            PlayerProps = PlayerProps.Except(unknownExtra).ToArray();
        }
        _contracts = _nameToId.ContainsKey(ContractProp) && ContractProps.All(_nameToId.ContainsKey);
        if (_contracts && !PlayerProps.Contains(ContractProp)) PlayerProps = PlayerProps.Append(ContractProp).ToArray();
        if (!_contracts) Plugin.L.LogWarning("F9 : propriétés de contrat introuvables, fins de contrat non lues.");
        var missing = PlayerProps.Concat(ScanProps).Concat(ClubProps).Concat(CompProps).Where(p => !_nameToId.ContainsKey(p)).Distinct().ToList();
        if (missing.Count > 0) { Plugin.L.LogError("F9 : propriétés inconnues : " + string.Join(", ", missing)); return false; }

        // Préparateurs : sans les attributs du calcul, on ne les lit pas (le reste de l'extraction continue)
        bool staff = NoteAuto.StaffEnabled;
        StaffProps = StaffBaseProps.Where(_nameToId.ContainsKey).ToArray();
        var staffMissing = StaffBaseProps.Except(StaffProps).ToList();
        if (staffMissing.Count > 0) Plugin.L.LogWarning("F9 : propriétés de préparateurs inconnues : " + string.Join(", ", staffMissing));
        if (staff && FM27Note.StaffEngine.Attributes.Any(a => !_nameToId.ContainsKey(a)))
        {
            Plugin.L.LogWarning("F9 : attributs de coaching introuvables, préparateurs non lus.");
            staff = false;
        }

        Targets.Clear(); StaffTargets.Clear(); Clubs.Clear(); Comps.Clear(); Nations.Clear(); ContractRefs.Clear(); ContractEnds.Clear();
        foreach (var d in SeenRefs.Values) d.Clear();
        _valid = _players = _playersWritten = _staffWritten = _staffEmpty = _missingTotal = 0;
        _stamp = Plugin.Stamp;
        _playersPath = Path.Combine(Plugin.ExportDir, $"FM27_Joueurs_{_stamp}.tsv");
        _staffPath = staff ? FM27Note.NoteData.StaffPath(Plugin.ExportDir, _stamp) : null;

        Pending.Clear();
        Pending.Enqueue(MakeScanPhase);
        Pending.Enqueue(MakePlayersPhase);
        if (staff) Pending.Enqueue(MakeStaffPhase);
        Pending.Enqueue(() => RefPhase("clubs", "club", ClubProps, Clubs));
        Pending.Enqueue(() => RefPhase("compétitions", "comp", CompProps, Comps));
        Pending.Enqueue(() => RefPhase("pays", "nation", NationProps, Nations));
        if (_contracts) Pending.Enqueue(MakeContractPhase);
        _phase = null;
        _inFlight = false;
        _start = DateTime.Now;
        StartGameDate = GameDate.Date;
        Active = true;
        Plugin.L.LogMessage($"F9 : extraction complète DÉMARRÉE{(auto ? " (automatique)" : "")} (≈ 2 à 3 min). Le jeu reste utilisable ; " +
                            "garde un écran avec des joueurs si possible. F9 pour arrêter.");
        return true;
    }

    private static Phase MakeScanPhase() => new()
    {
        Label = "balayage de la base",
        Props = ScanProps,
        Items = Enumerable.Range(0, MaxIndex).ToList(),
        MakeRef = idx => Probe.MakePersonReference(Probe.PersonDrefBase + idx),
        Parent = () => Anchor.PersonParent,
        Scan = true,
        OnRow = (idx, row) =>
        {
            bool valid = row != null && long.TryParse(row[0], out var u) && u > 0;
            if (!valid) { _emptyRun++; return; }
            _emptyRun = 0; _valid++;
            if (row[1] != "True") { StaffTargets.Add(idx); return; }   // non-joueur (préparateur, recruteur, dirigeant…)
            _players++;
            if (Num(row[2]) >= 1) Targets.Add(idx);
        },
    };

    private static Phase MakePlayersPhase()
    {
        _playersOut = new StreamWriter(_playersPath, false, new UTF8Encoding(true));
        _playersOut.WriteLine("index\t" + string.Join("\t", PlayerProps));
        _contractCol = Array.IndexOf(PlayerProps, ContractProp);
        _capturingContracts = _contracts && _contractCol >= 0;
        return new Phase
        {
            Label = $"lecture de {Targets.Count} joueurs",
            Props = PlayerProps,
            Items = Targets.ToList(),
            MakeRef = idx => Probe.MakePersonReference(Probe.PersonDrefBase + idx),
            Parent = () => Anchor.PersonParent,
            OnRow = (idx, row) =>
            {
                _playersOut.WriteLine(idx + "\t" + string.Join("\t", row.Select(c => c ?? "")));
                _playersWritten++;
            },
            OnEnd = () => { _playersOut?.Dispose(); _playersOut = null; _capturingContracts = false; },
        };
    }

    // Contrats des joueurs (références gardées pendant la lecture des joueurs) : date de fin
    private static Phase MakeContractPhase() => new()
    {
        Label = $"{ContractRefs.Count} contrats",
        Props = ContractProps,
        Items = ContractRefs.Keys.OrderBy(k => k).ToList(),
        MakeRef = idx => ContractRefs[idx],
        Parent = () => Anchor.GenericParent,
        OnRow = (idx, row) => ContractEnds[idx] = row[0] ?? "",
        Summary = () => $" {ContractEnds.Count(kv => kv.Value.Length > 0)} dates de fin lues sur {ContractRefs.Count}.",
    };

    // Valeur « FullContract » d'un joueur : copie immédiate de la référence (les valeurs reçues sont recyclées)
    private static void CaptureContract(int idx, TypedValue v)
    {
        if (v is null || v.IsNull) return;
        InteropReference r = null;
        try { r = v.Get()?.TryCast<InteropReference>(); } catch { }
        if (r == null) return;
        var ids = r.m_id;
        var list = new Il2CppSystem.Collections.Generic.List<InteropReference.Pair>();
        for (int i = 0; i < ids.Count; i++) list.Add(new InteropReference.Pair(ids[i].m_id, ids[i].m_value));
        ContractRefs[idx] = new FM.UI.ContractReference(list);
    }

    // Non-joueurs : on garde ceux qui ont des attributs de coaching (préparateurs, entraîneurs, recruteurs…)
    private static Phase MakeStaffPhase()
    {
        _staffOut = new StreamWriter(_staffPath, false, new UTF8Encoding(true));
        _staffOut.WriteLine("index\t" + string.Join("\t", StaffProps));
        var coaching = FM27Note.StaffEngine.Attributes.Select(a => Array.IndexOf(StaffProps, a)).Where(i => i >= 0).ToArray();
        return new Phase
        {
            Label = $"lecture de {StaffTargets.Count} non-joueurs (préparateurs)",
            Props = StaffProps,
            Items = StaffTargets.ToList(),
            MakeRef = idx => Probe.MakePersonReference(Probe.PersonDrefBase + idx),
            Parent = () => Anchor.PersonParent,
            OnRow = (idx, row) =>
            {
                if (!coaching.Any(i => !string.IsNullOrEmpty(row[i]))) { _staffEmpty++; return; }
                _staffOut.WriteLine(idx + "\t" + string.Join("\t", row.Select(c => c ?? "")));
                _staffWritten++;
            },
            OnEnd = () => { _staffOut?.Dispose(); _staffOut = null; },
            Summary = () => $" {_staffWritten} avec des attributs de coaching, {_staffEmpty} sans.",
        };
    }

    private static Phase RefPhase(string label, string type, string[] props, Dictionary<int, string[]> store)
    {
        var refs = SeenRefs[type];
        return new Phase
        {
            Label = $"{refs.Count} {label}",
            Props = props,
            Items = refs.Keys.OrderBy(k => k).ToList(),
            MakeRef = dref => refs[dref],
            Parent = () => Anchor.GenericParent,
            OnRow = (dref, row) => store[dref] = row,
        };
    }

    // ── Boucle (appelée à chaque frame) ────────────────────────────────────

    public static void Tick()
    {
        if (!Active) return;
        try
        {
            if (_phase == null && !NextPhase()) return;
            if (!_inFlight) { OpenBatch(); return; }

            var now = DateTime.Now;
            bool complete = _answered >= _opened;
            bool idle = (now - _lastValueAt).TotalSeconds > 2;
            bool timeout = (now - _batchStart).TotalSeconds > 20;
            if (!complete && !idle && !timeout) return;
            CloseBatch(complete, (now - _batchStart).TotalSeconds);
        }
        catch (Exception e)
        {
            Plugin.L.LogError("F9 : " + e);
            Finish("erreur : " + e.Message);
        }
    }

    private static bool NextPhase()
    {
        if (Pending.Count == 0) { Finish("terminé"); return false; }
        _phase = Pending.Dequeue()();
        _phase.PropIds = _phase.Props.Select(p => _nameToId[p]).ToArray();
        _phase.Pos = 0;
        _emptyRun = 0; _batchNo = 0;
        _channels = Math.Max(MinChannels, 3_000);
        _phaseStart = DateTime.Now;
        Plugin.L.LogMessage($"F9 : phase « {_phase.Label} » ({_phase.Items.Count} éléments × {_phase.Props.Length} propriétés)");
        return true;
    }

    private static void OpenBatch()
    {
        var parentKey = _phase.Parent();
        if (parentKey == 0)
        {
            // Plus aucun joueur affiché : pause, reprise dès qu'un écran avec des joueurs revient
            if (_pausedSince == null)
            {
                _pausedSince = DateTime.Now;
                Plugin.L.LogMessage("F9 : PAUSE, plus aucun joueur affiché. Affiche un écran avec des joueurs pour reprendre.");
            }
            else if ((DateTime.Now - _pausedSince.Value).TotalMinutes > 10) Finish("abandon : aucun joueur affiché depuis 10 min");
            return;
        }
        if (_pausedSince != null) { _pausedSince = null; Plugin.L.LogMessage("F9 : reprise."); }
        _batchParent = parentKey;

        bool done = _phase.Pos >= _phase.Items.Count
                    || (_phase.Scan && _emptyRun >= StopAfterEmpty && _phase.Pos > 1000);
        if (done) { EndPhase(); return; }

        int perItem = _phase.Props.Length;
        int count = Math.Max(1, _channels / perItem);
        _from = _phase.Pos;
        _to = Math.Min(_from + count, _phase.Items.Count);
        _batchNo++;
        Slots.Clear(); Rows.Clear();
        _opened = _answered = 0;
        var parent = new SI.Bindable.Bindings.Key(parentKey);
        for (int i = _from; i < _to; i++)
        {
            int item = _phase.Items[i];
            var reference = _phase.MakeRef(item);
            for (int p = 0; p < perItem; p++)
            {
                var key = _nextKey++;
                Slots[key] = (item, p);
                var pid = new PropertyID { id = _phase.PropIds[p] };
                Probe.Subsystem.AddNode(new SI.Bindable.Bindings.Key(key), parent, pid);
                Probe.Subsystem.OpenChannel(reference, pid, new SI.Bindable.Bindings.Key(key));
                _opened++;
            }
        }
        _batchStart = _lastValueAt = DateTime.Now;
        _inFlight = true;
    }

    private static void CloseBatch(bool complete, double seconds)
    {
        ulong first = _nextKey - (ulong)_opened;
        for (ulong k = first; k < _nextKey; k++)
        {
            try { Probe.Subsystem.CloseChannel(new SI.Bindable.Bindings.Key(k)); } catch { }
            try { Probe.Subsystem.RemoveNode(new SI.Bindable.Bindings.Key(k)); } catch { }
        }

        // Lot incomplet parce que son nœud parent a disparu (changement d'écran) : on le relit
        if (!complete && !Anchor.IsAlive(_batchParent))
        {
            Plugin.L.LogMessage($"  F9 « {_phase.Label} » : lot {_batchNo} interrompu par un changement d'écran, relu.");
            _inFlight = false;
            return;
        }

        for (int i = _from; i < _to; i++)
        {
            int item = _phase.Items[i];
            Rows.TryGetValue(item, out var row);
            _phase.OnRow(item, row ?? new string[_phase.Props.Length]);
        }
        _playersOut?.Flush();
        _staffOut?.Flush();

        int missing = _opened - _answered;
        _missingTotal += missing;
        if (!complete || _batchNo % 10 == 0)
            Plugin.L.LogMessage($"  F9 « {_phase.Label} » : {_to}/{_phase.Items.Count}, lot {_batchNo} : {_answered}/{_opened} réponses en {seconds:F1}s" +
                                (complete ? "" : $" (INCOMPLET : {missing} sans réponse)"));

        _phase.Pos = _to;
        _inFlight = false;
        if (complete && seconds < 2 && _channels < MaxChannels) _channels = Math.Min(MaxChannels, _channels * 2);
        else if (!complete && _channels > MinChannels) _channels = Math.Max(MinChannels, _channels / 2);
    }

    private static void EndPhase()
    {
        var t = (DateTime.Now - _phaseStart).TotalSeconds;
        string extra = _phase.Scan
            ? $" {_valid} personnes, {_players} joueurs, {Targets.Count} avec au moins 1 minute, {StaffTargets.Count} non-joueurs."
            : _phase.Summary?.Invoke() ?? "";
        Plugin.L.LogMessage($"F9 : phase « {_phase.Label} » finie en {t:F0}s.{extra}");
        _phase.OnEnd?.Invoke();
        _phase = null;
    }

    private static void Finish(string reason)
    {
        if (_inFlight)
        {
            try { CloseBatch(_answered >= _opened, (DateTime.Now - _batchStart).TotalSeconds); } catch { }
        }
        Active = false;
        _phase = null;
        Pending.Clear();
        _playersOut?.Dispose(); _playersOut = null;
        _staffOut?.Dispose(); _staffOut = null;

        try { WriteRefFiles(); } catch (Exception e) { Plugin.L.LogError("F9 : écriture clubs/compétitions : " + e); }

        var t = (DateTime.Now - _start).TotalSeconds;
        Plugin.L.LogMessage($"F9 : extraction {reason.ToUpperInvariant()} en {t:F0}s. {_playersWritten} joueurs écrits, " +
                            (_staffPath != null ? $"{_staffWritten} préparateurs, " : "") + $"{Clubs.Count} clubs, {Comps.Count} compétitions, {Nations.Count} pays, " +
                            $"{_missingTotal} valeurs sans réponse. -> {_playersPath}");
        try { NoteAuto.OnExtractDone(_stamp, reason == "terminé" && _playersWritten > 0); }
        catch (Exception e) { Plugin.L.LogError("Notes : " + e); }
    }

    private static void WriteRefFiles()
    {
        if (ContractRefs.Count > 0)
        {
            var path = Path.Combine(Plugin.ExportDir, $"FM27_Contrats_{_stamp}.tsv");
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine("index\tEndDate\tfin_contrat");
            foreach (var (idx, raw) in ContractEnds.OrderBy(k => k.Key))
                w.WriteLine($"{idx}\t{raw}\t{(GameDate.Parse(raw) is DateTime d ? d.ToString("yyyy-MM-dd") : "")}");
        }
        if (Clubs.Count > 0)
        {
            var path = Path.Combine(Plugin.ExportDir, $"FM27_Clubs_{_stamp}.tsv");
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine("dref\t" + string.Join("\t", ClubProps));
            foreach (var (dref, row) in Clubs.OrderBy(k => k.Key))
                w.WriteLine(dref + "\t" + string.Join("\t", row.Select(c => c ?? "")));
        }
        if (Comps.Count > 0)
        {
            var path = Path.Combine(Plugin.ExportDir, $"FM27_Competitions_{_stamp}.tsv");
            using var w = new StreamWriter(path, false, new UTF8Encoding(true));
            w.WriteLine("dref\t" + string.Join("\t", CompProps) + "\tNationName");
            int nationCol = Array.IndexOf(CompProps, "Nation");
            foreach (var (dref, row) in Comps.OrderBy(k => k.Key))
            {
                string nationName = "";
                if (int.TryParse(row[nationCol], out var nd) && Nations.TryGetValue(nd, out var nrow)) nationName = nrow[0] ?? "";
                w.WriteLine(dref + "\t" + string.Join("\t", row.Select(c => c ?? "")) + "\t" + nationName);
            }
        }
    }

    // ── Réception des valeurs (hook OnChannelDataChanged) ──────────────────

    public static void OnValue(ulong key, TypedValue v)
    {
        if (!Slots.TryGetValue(key, out var slot)) return;
        Slots.Remove(key);
        string s;
        try { s = Cell(v); } catch { s = "err"; }
        if (_capturingContracts && slot.prop == _contractCol) { try { CaptureContract(slot.item, v); } catch { } }
        if (!Rows.TryGetValue(slot.item, out var row)) Rows[slot.item] = row = new string[_phase?.Props.Length ?? 1];
        if (slot.prop < row.Length) row[slot.prop] = s;
        _answered++;
        _lastValueAt = DateTime.Now;
    }

    // Valeur -> texte de cellule :
    //   stat {PropertyValue=x, Count=n}  -> x
    //   fourchette {MinValue=a, MaxValue=b} -> a/b
    //   référence (club, compétition...) -> son dref (et on la garde pour les phases suivantes)
    //   texte -> sans le lien caché ni les balises
    internal static string Cell(TypedValue v)
    {
        if (v is null || v.IsNull) return "";
        Il2CppSystem.Object o = null;
        try { o = v.Get(); } catch { }
        if (o != null)
        {
            SI.Core.Record rec = null;
            try { rec = o.TryCast<SI.Core.Record>(); } catch { }
            if (rec != null) return RecordCell(rec);

            InteropReference r = null;
            try { r = o.TryCast<InteropReference>(); } catch { }
            if (r != null) return RefCell(r);

            // Liste (ex : AbilityProgressHistory) -> éléments séparés par |
            Il2CppSystem.Collections.Generic.List<TypedValue> list = null;
            try { list = o.TryCast<Il2CppSystem.Collections.Generic.List<TypedValue>>(); } catch { }
            if (list != null)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < list.Count && i < 30; i++)
                {
                    if (i > 0) sb.Append('|');
                    sb.Append(Cell(list[i]).Replace('|', '/'));
                }
                return sb.ToString();
            }
        }
        return Clean(v.AsString());
    }

    private static string FieldName(uint id)
    {
        if (!IdName.TryGetValue(id, out var n)) IdName[id] = n = Names.Property(id);
        return n;
    }

    private static string RecordCell(SI.Core.Record rec)
    {
        string pv = null, mn = null, mx = null, amn = null, amx = null, tvs = null, tvmax = null, asking = null;
        foreach (var kv in rec)
        {
            switch (FieldName(kv.Key))
            {
                case "PropertyValue": pv = kv.Value?.AsString(); break;
                case "MinValue": mn = kv.Value?.AsString(); break;
                case "MaxValue": mx = kv.Value?.AsString(); break;
                case "AttributeMinValue": amn = kv.Value?.AsString(); break;
                case "AttributeMaxValue": amx = kv.Value?.AsString(); break;
                case "ActualValue": pv ??= kv.Value?.AsString(); break;   // ScoutingKnowledgeLevel
                case "TransferValueRangeString": tvs = kv.Value?.AsString(); break;   // valeur de transfert (≈ 600 caractères
                case "TransferValueRangeMaxValue": tvmax = kv.Value?.AsString(); break; // en tout : on ne garde que ces 3 champs)
                case "AskingPrice": asking = kv.Value?.AsString(); break;
            }
        }
        if (tvs != null) return $"{Clean(tvs)};{tvmax};{asking}";   // « 201M € - 263M € ;valeur haute (£);prix demandé (£, -1 = aucun) »
        if (pv != null && (amn != null || amx != null)) return $"{Clean(pv)};{amn};{amx}";   // attribut : valeur;min;max
        if (pv != null) return Clean(pv);
        if (mn != null || mx != null) return $"{mn}/{mx}";
        var sb = new StringBuilder();
        foreach (var kv in rec)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(FieldName(kv.Key)).Append('=').Append(Clean(kv.Value?.AsString()));
        }
        return sb.ToString();
    }

    private static string RefCell(InteropReference r)
    {
        int dref = 0;
        var ids = r.m_id;
        for (int i = 0; i < ids.Count; i++)
            if (ids[i].m_id == Probe.KeyDref) { dref = ids[i].m_value; break; }
        var type = Names.TypeOf(r);
        if (dref != 0 && SeenRefs.TryGetValue(type, out var seen) && !seen.ContainsKey(dref))
        {
            // Les valeurs reçues sont recyclées par le jeu : copie immédiate des identifiants
            var list = new Il2CppSystem.Collections.Generic.List<InteropReference.Pair>();
            for (int i = 0; i < ids.Count; i++) list.Add(new InteropReference.Pair(ids[i].m_id, ids[i].m_value));
            InteropReference copy = type switch
            {
                "club" => new FM.UI.ClubReference(list),
                "comp" => new FM.UI.CompReference(list),
                "nation" => new FM.UI.NationReference(list),
                "team" => new FM.UI.TeamReference(list),
                _ => null,
            };
            if (copy != null) seen[dref] = copy;
        }
        return dref.ToString(CultureInfo.InvariantCulture);
    }

    // Les noms arrivent sous la forme  \x01<lien en base64>\x02Nom affiché\x01
    private static readonly Regex Link = new("\u0001[^\u0002]*\u0002");
    private static readonly Regex Tags = new("<[^>]+>");
    private static readonly Regex Ctrl = new("[\u0000-\u001F]+");

    private static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (s.IndexOf('\u0001') >= 0) s = Link.Replace(s, "");
        if (s.IndexOf('<') >= 0) s = Tags.Replace(s, "");
        return Ctrl.Replace(s, " ").Trim();
    }

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
