// FM27 — lecture d'une extraction F9 (fichiers FM27_Joueurs/Clubs/Competitions_<date>.tsv) et écriture des notes
// pour le jeu (BepInEx/config/fm27_notes.tsv).
//
// Partagé par l'outil hors jeu (Note/Outil) et par le plugin (calcul automatique après F9).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FM27Note;

public sealed class ExportData
{
    public string Stamp;
    public List<PlayerData> Players = new();
    public Dictionary<string, (string name, double strength, string nation)> Comps = new();   // dref -> compétition
    public Dictionary<string, string> Clubs = new();                                           // dref -> nom
    public Dictionary<string, string> TeamNames = new();                                       // équipe -> nom du club
    public Dictionary<string, string> Nations = new();                                         // pays (dref) -> nom, d'après les compétitions
    public Dictionary<PlayerData, (string club, string comp, double age, double stars)> Info = new();

    // Nom d'une compétition, y compris les pseudo-championnats d'équipes de jeunes
    public string LeagueName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "?";
        bool youth = id.EndsWith(NoteEngine.YouthSuffix, StringComparison.Ordinal);
        var baseId = youth ? id.Substring(0, id.Length - NoteEngine.YouthSuffix.Length) : id;
        return (Comps.TryGetValue(baseId, out var c) ? c.name : "?") + (youth ? " (jeunes)" : "");
    }
}

public static class NoteData
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Date (horodatage) de la dernière extraction complète du dossier, ou null
    public static string LatestStamp(string dir)
    {
        var f = Directory.GetFiles(dir, "FM27_Joueurs_*.tsv").OrderBy(x => x, StringComparer.Ordinal).LastOrDefault();
        return f == null ? null : Path.GetFileNameWithoutExtension(f).Substring("FM27_Joueurs_".Length);
    }

    public static ExportData Load(string dir, string stamp, Action<string> warn = null)
    {
        var d = new ExportData { Stamp = stamp };
        foreach (var r in ReadTsv(Path.Combine(dir, $"FM27_Competitions_{stamp}.tsv"), warn))
        {
            d.Comps[r["dref"]] = (r["Name"], (Num(r["Reputation"]) ?? 0) / 10.0, r.GetValueOrDefault("NationName", ""));
            string nat = r.GetValueOrDefault("Nation", ""), natName = r.GetValueOrDefault("NationName", "");
            if (nat != "" && natName != "" && !d.Nations.ContainsKey(nat)) d.Nations[nat] = natName;
        }
        foreach (var r in ReadTsv(Path.Combine(dir, $"FM27_Clubs_{stamp}.tsv"), warn))
            d.Clubs[r["dref"]] = r["Name"];

        // Fins de contrat (fichier écrit depuis 0.12.0 ; absent des extractions plus anciennes)
        var contracts = new Dictionary<string, string>();
        var contractPath = Path.Combine(dir, $"FM27_Contrats_{stamp}.tsv");
        if (File.Exists(contractPath))
            foreach (var r in ReadTsv(contractPath, warn))
                if (r.GetValueOrDefault("fin_contrat", "") is string fc && fc.Length > 0) contracts[r["index"]] = fc;

        foreach (var r in ReadTsv(Path.Combine(dir, $"FM27_Joueurs_{stamp}.tsv"), warn))
        {
            var p = new PlayerData
            {
                Index = int.Parse(r["index"], Inv),
                Name = r["Name"],
                Positions = r["PositionCombinedString"],
                Minutes = Num(r["Minutes"]) ?? 0,
                Stats = new Dictionary<string, double>(r.Count),   // taille connue : moins de mémoire
            };
            d.Comps.TryGetValue(r["Division"], out var comp);
            p.LeagueStrength = comp.name != null ? comp.strength : 0;
            p.LeagueId = comp.name != null ? r["Division"] : "";
            p.Detailed = (Num(r.GetValueOrDefault("DistanceRanPer90", "")) ?? 0) > 0;
            p.TeamId = r.GetValueOrDefault("Team", "");
            p.ClubId = r.GetValueOrDefault("Club", "");
            if (p.TeamId != "") d.TeamNames[p.TeamId] = d.Clubs.GetValueOrDefault(p.ClubId, "?");
            foreach (var (k, v) in r)
            {
                if (Num(v) is double x) p.Stats[k] = x;
                else if (k == "AbilityProgressHistory" && v.Length > 0 && Num(v.Split('|')[0]) is double h)
                    p.Stats["_CA"] = h / 220.0;   // vrai CA (joueurs de son club uniquement)
                else if (k.StartsWith("Attribute", StringComparison.Ordinal) && v.Contains(';'))
                {
                    // attribut "valeur;min;max" ; joueur mal connu de son recrutement : "15-20;15;20" -> milieu (17,5) ;
                    // attribut inconnu : "-;-1;-1" -> absent
                    var parts = v.Split(';');
                    double? lo = parts.Length == 3 ? Num(parts[1]) : null, hi = parts.Length == 3 ? Num(parts[2]) : null;
                    if (lo < 0 || hi < 0) { lo = hi = null; p.Stats["_AttrMasque"] = 1; p.MaskedAttributes = true; }
                    double? a = Num(parts[0]) ?? (lo.HasValue && hi.HasValue ? (lo + hi) / 2 : null);
                    if (a is double av) p.Stats[k] = av;
                    if (lo.HasValue && hi.HasValue && lo != hi) { p.Stats["_AttrMasque"] = 1; p.MaskedAttributes = true; }
                }
            }
            foreach (var k in TextKeys)
                if (r.TryGetValue(k, out var t) && t.Length > 0) (p.Text ??= new Dictionary<string, string>())[k] = t;
            if (contracts.TryGetValue(r["index"], out var endDate)) (p.Text ??= new Dictionary<string, string>())["FinContrat"] = endDate;
            if (r.TryGetValue("IsPersonFemale", out var fem) && fem.Length > 0)
                p.Female = fem.Equals("true", StringComparison.OrdinalIgnoreCase) || fem == "1" || fem.Equals("vrai", StringComparison.OrdinalIgnoreCase);
            d.Players.Add(p);
            d.Info[p] = (d.Clubs.GetValueOrDefault(p.ClubId, "?"), comp.name ?? "?", Num(r.GetValueOrDefault("Age", "")) ?? 0,
                         Stars(r.GetValueOrDefault("CurrentAbilityStarRange", "")));
        }
        return d;
    }

    // Textes bruts gardés pour le fichier de recrutement
    private static readonly string[] TextKeys =
        { "Nation", "CurrentAbilityStarRange", "PotentialAbilityStarRange", "ContextContractWeeklyWage", "TransferValueRange", "IsPersonFemale" };

    // ── Fichiers de recrutement (lisibles par un humain, un tableur ou un LLM) ──
    // Nom fixe : toujours la dernière extraction. Décrits dans FM27/ASSISTANT_RECRUTEMENT.md.

    public const string RecruitFile = "FM27_Recrutement.tsv", RecruitStaffFile = "FM27_Recrutement_staff.tsv",
                        RecruitInfoFile = "FM27_Recrutement_info.txt";

    public static readonly Dictionary<string, string> ProfileFr = new()
    {
        ["GK"] = "GB", ["CB"] = "DC", ["FB"] = "LAT", ["DM"] = "MDC", ["CM"] = "MC", ["AM"] = "MOC", ["WG"] = "AIL", ["ST"] = "BT",
    };

    // Principales stats de la saison (noms du jeu), après les notes
    private static readonly string[] RecruitStats =
    {
        "GoalsPer90", "ExpectedGoalsPer90", "NonPenaltyExpectedGoalsPer90", "ShotsPer90", "ShotsOnTargetPercentage", "GoalsPerShotPercentage",
        "AssistsPer90", "ExpectedAssistsPer90", "KeyPassesPer90", "ChancesCreatedPer90", "ProgressivePassesPer90", "PassAttemptsPer90",
        "PassCompletionPercentage", "OpenPlayCrossesCompletedPer90", "OpenPlayCrossCompletionPercentage", "DribblesMadePer90",
        "PossessionWonPer90", "PossessionLostPer90", "TacklesCompletedPer90", "TackleCompletionPercentage", "InterceptionsPer90",
        "BlocksPer90", "ClearancesPer90", "PressuresCompletedPer90", "HeadersCompletedPer90", "HeaderCompletionPercentage",
        "DistanceRanPer90", "SprintsPer90", "ConcededPer90", "CleanSheetsPer90", "TotalSavePercentage", "ExpectedGoalsPreventedPer90",
        "InjuriesPercentageMissed",
    };

    public static int WriteRecruitment(string dir, ExportData data, string gameDate, NoteSettings settings = null)
    {
        // Attributs dans l'ordre de l'extraction
        var attrs = new List<string>();
        var seen = new HashSet<string>();
        foreach (var p in data.Players)
            foreach (var k in p.Stats.Keys)
                if (k.StartsWith("Attribute", StringComparison.Ordinal) && seen.Add(k)) attrs.Add(k);

        var header = new List<string>
        {
            "index", "nom", "age", "nationalite", "club", "championnat", "pays_championnat", "reputation_championnat", "facteur_championnat", "postes",
            "sexe", "minutes", "matchs", "note_match_moyenne", "buts", "passes_d", "stats_detaillees",
            "note", "meilleur_profil",
        };
        header.AddRange(NoteEngine.ProfileOrder.Select(pr => "note_" + ProfileFr[pr]));
        // Pas de CA équivalent (choix de Fred, 3 nov. 2026) : l'assistant raisonne comme dans une vraie partie, où il manque presque toujours
        header.AddRange(new[] { "vrai_ca", "attributs_fourchette", "potentiel_percu", "etoiles_actuel", "etoiles_potentiel",
                                "salaire_semaine", "fin_contrat", "valeur_transfert", "valeur_haute", "prix_demande" });
        header.AddRange(RecruitStats);
        header.AddRange(attrs.Select(a => a.Substring("Attribute".Length)));

        string N(PlayerData p, string k, string fmt = "0.##") =>
            p.Stats.TryGetValue(k, out var v) && !double.IsNaN(v) ? v.ToString(fmt, Inv) : "";
        string T(PlayerData p, string k) =>
            p.Text != null && p.Text.TryGetValue(k, out var t) ? t.Replace('\t', ' ').Replace('\n', ' ') : "";
        string Stars(PlayerData p, string k) { var t = T(p, k); return t == "" || t.StartsWith("-1") ? "" : t; }   // -1/-1 = inconnu
        string F1(double v) => double.IsNaN(v) ? "" : v.ToString("0.0", Inv);

        int n = 0;
        var tmp = Path.Combine(dir, RecruitFile + ".tmp");
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            w.WriteLine(string.Join("\t", header));
            foreach (var p in data.Players.OrderByDescending(p => double.IsNaN(p.BestScore) ? -1 : p.BestScore))
            {
                var info = data.Info[p];
                data.Comps.TryGetValue(p.LeagueId.EndsWith(NoteEngine.YouthSuffix, StringComparison.Ordinal)
                    ? p.LeagueId.Substring(0, p.LeagueId.Length - NoteEngine.YouthSuffix.Length) : p.LeagueId, out var comp);
                var cells = new List<string>
                {
                    p.Index.ToString(Inv), p.Name, info.age.ToString("0", Inv), data.Nations.GetValueOrDefault(T(p, "Nation"), ""),
                    info.club, data.LeagueName(p.LeagueId), comp.nation ?? "", (p.LeagueStrength * 10).ToString("0", Inv),
                    NoteEngine.LeagueFactor(p, NoteEngine.TopLevel, settings ?? new NoteSettings()).ToString("0.00", Inv), p.Positions,
                    p.Female == true ? "F" : p.Female == false ? "M" : "", p.Minutes.ToString("0", Inv), N(p, "Appearances", "0"), N(p, "AverageRating", "0.00"), N(p, "Goals", "0"), N(p, "Assists", "0"),
                    p.Detailed ? "oui" : "non",
                    F1(p.BestScore), p.BestProfile != null ? ProfileFr.GetValueOrDefault(p.BestProfile, p.BestProfile) : "",
                };
                cells.AddRange(NoteEngine.ProfileOrder.Select(pr => p.Scores.TryGetValue(pr, out var s) ? F1(s.Final) : ""));
                cells.Add(p.Stats.TryGetValue("_CA", out var ca) ? F1(ca) : "");
                cells.Add(p.MaskedAttributes ? "oui" : "non");
                cells.Add(N(p, "PerceivedPotentialAbility", "0"));
                cells.Add(Stars(p, "CurrentAbilityStarRange"));
                cells.Add(Stars(p, "PotentialAbilityStarRange"));
                cells.Add(N(p, "ContextContractWeeklyWage", "0"));
                cells.Add(T(p, "FinContrat"));
                var (tvText, tvHigh, tvAsk) = TransferValue(T(p, "TransferValueRange"));
                cells.Add(tvText); cells.Add(tvHigh); cells.Add(tvAsk);
                cells.AddRange(RecruitStats.Select(k => N(p, k)));
                cells.AddRange(attrs.Select(a => N(p, a, "0.#")));
                w.WriteLine(string.Join("\t", cells));
                n++;
            }
        }
        File.Copy(tmp, Path.Combine(dir, RecruitFile), true);
        File.Delete(tmp);
        File.WriteAllText(Path.Combine(dir, RecruitInfoFile),
            $"date_jeu={gameDate}\nextraction={data.Stamp}\njoueurs={n}\necrit_le={DateTime.Now:yyyy-MM-dd HH:mm}\n" +
            $"description=voir FM27/ASSISTANT_RECRUTEMENT.md (projet FMdataExport)\n", new UTF8Encoding(false));
        return n;
    }

    // Valeur de transfert lue par F9 : « texte;valeur haute;prix demandé » (depuis 0.12.0) ou l'enregistrement complet
    // « AskingPrice=…; …; TransferValueRangeString=… » (première extraction du 3 nov.). Montants en livres ; -1 / 0 = inconnu.
    private static (string text, string high, string asking) TransferValue(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return ("", "", "");
        string text, high, asking;
        if (raw.Contains("TransferValueRangeString="))
        {
            var d = raw.Split("; ").Select(kv => kv.Split('=', 2)).Where(a => a.Length == 2).GroupBy(a => a[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            text = d.GetValueOrDefault("TransferValueRangeString", "");
            high = d.GetValueOrDefault("TransferValueRangeMaxValue", "");
            asking = d.GetValueOrDefault("AskingPrice", "");
        }
        else
        {
            var parts = raw.Split(';');
            text = parts[0]; high = parts.Length > 1 ? parts[1] : ""; asking = parts.Length > 2 ? parts[2] : "";
        }
        static string Amount(string s) => Num(s) is double v && v > 0 ? v.ToString("0", Inv) : "";
        return (text.Replace('\u00A0', ' ').Trim(), Amount(high), Amount(asking));
    }

    public static int WriteRecruitmentStaff(string dir, ExportData data, IEnumerable<StaffData> staff)
    {
        var header = new List<string> { "index", "nom", "age", "club", "fonction", "etoiles_max", "meilleur_domaine" };
        header.AddRange(StaffEngine.Categories.Select(c => "etoiles_" + c.Key));
        var attrs = StaffEngine.Attributes.Concat(new[] { "WorkingWithYoungster", "PeopleManagement" }).ToArray();
        header.AddRange(attrs);
        string St(double pts) => double.IsNaN(pts) ? "" : StaffEngine.GameStars(pts).ToString("0.0", Inv);
        int n = 0;
        var tmp = Path.Combine(dir, RecruitStaffFile + ".tmp");
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            w.WriteLine(string.Join("\t", header));
            foreach (var s in staff.Where(s => !double.IsNaN(s.BestPoints)).OrderByDescending(s => s.BestPoints))
            {
                var cells = new List<string>
                {
                    s.Index.ToString(Inv), s.Name, s.Age.ToString("0", Inv), data?.Clubs.GetValueOrDefault(s.ClubId ?? "", "") ?? "",
                    s.Job ?? "", St(s.BestPoints), s.BestCategory >= 0 ? StaffEngine.Categories[s.BestCategory].Name : "",
                };
                cells.AddRange(s.Points.Select(St));
                cells.AddRange(attrs.Select(a => s.Attr.TryGetValue(a, out var v) ? v.ToString("0", Inv) : ""));
                w.WriteLine(string.Join("\t", cells));
                n++;
            }
        }
        File.Copy(tmp, Path.Combine(dir, RecruitStaffFile), true);
        File.Delete(tmp);
        return n;
    }

    // Fichier lu par la colonne « Note » du jeu : index, meilleure note, puis une note par profil (vide si non concerné)
    public static int WriteGameNotes(string path, IEnumerable<PlayerData> players)
    {
        int n = 0;
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            w.WriteLine("index\tnote\t" + string.Join("\t", NoteEngine.ProfileOrder));
            foreach (var p in players.Where(p => !double.IsNaN(p.BestScore)))
            {
                w.WriteLine($"{p.Index}\t{p.BestScore.ToString("F1", Inv)}\t" +
                            string.Join("\t", NoteEngine.ProfileOrder.Select(pr => p.Scores.TryGetValue(pr, out var s) ? s.Final.ToString("F1", Inv) : "")));
                n++;
            }
        }
        File.Copy(tmp, path, true);   // remplacement d'un coup : le jeu ne lit jamais un fichier à moitié écrit
        File.Delete(tmp);
        return n;
    }

    // Détail des notes pour la fiche joueur (mode Moneyball du plugin) : une ligne par joueur noté (≥ NoteMinMinutes).
    // index, minutes, facteur championnat, puis un champ par profil (meilleur d'abord) :
    // PROFIL|note des stats (avant championnat)|note finale|stat:note 0–100:valeur brute;stat:…
    public static int WriteGameDetails(string path, IEnumerable<PlayerData> players, NoteSettings s)
    {
        int n = 0;
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            w.WriteLine("index\tminutes\tfacteur\tprofils (PROFIL|note stats|note finale|stat:note 0-100:valeur brute;...)");
            var sb = new StringBuilder();
            foreach (var p in players.Where(p => !double.IsNaN(p.BestScore) && p.Minutes >= s.NoteMinMinutes))
            {
                sb.Clear();
                sb.Append(p.Index).Append('\t').Append(p.Minutes.ToString("F0", Inv)).Append('\t')
                  .Append(NoteEngine.LeagueFactor(p, NoteEngine.TopLevel, s).ToString("F2", Inv));
                foreach (var (pr, sc) in p.Scores.OrderByDescending(kv => kv.Value.Final))
                {
                    sb.Append('\t').Append(pr).Append('|').Append(sc.Raw.ToString("F1", Inv)).Append('|')
                      .Append(sc.Final.ToString("F1", Inv)).Append('|');
                    bool first = true;
                    foreach (var (stat, score) in sc.Components)
                    {
                        if (double.IsNaN(score)) continue;
                        if (!first) sb.Append(';');
                        first = false;
                        sb.Append(stat).Append(':').Append(score.ToString("F0", Inv)).Append(':');
                        if (p.Stats.TryGetValue(stat, out var v) && !double.IsNaN(v)) sb.Append(v.ToString("0.###", Inv));
                    }
                }
                w.WriteLine(sb.ToString());
                n++;
            }
        }
        File.Copy(tmp, path, true);   // remplacement d'un coup : le jeu ne lit jamais un fichier à moitié écrit
        File.Delete(tmp);
        return n;
    }

    // ── Préparateurs (staff) ──────────────────────────────────────────────

    public static string StaffPath(string dir, string stamp) => Path.Combine(dir, $"FM27_Staff_{stamp}.tsv");

    // Préparateurs de l'extraction (fichier FM27_Staff_<date>.tsv, absent des extractions d'avant le 3 nov. 2026)
    public static List<StaffData> LoadStaff(string dir, string stamp, Action<string> warn = null)
    {
        var list = new List<StaffData>();
        var path = StaffPath(dir, stamp);
        if (!File.Exists(path)) { warn?.Invoke($"pas de préparateurs dans cette extraction ({Path.GetFileName(path)} absent)"); return list; }
        var unknownWords = new HashSet<string>();
        foreach (var r in ReadTsv(path, warn))
        {
            var s = new StaffData
            {
                Index = int.Parse(r["index"], Inv),
                Name = r.GetValueOrDefault("Name", ""),
                ClubId = r.GetValueOrDefault("Club", ""),
                Job = r.GetValueOrDefault("JobString", ""),
                Age = Num(r.GetValueOrDefault("Age", "")) ?? 0,
            };
            foreach (var a in StaffEngine.Attributes.Concat(new[] { "WorkingWithYoungster", "PeopleManagement", "NonPlayerCurrentAbility", "NonPlayerPotentialAbility" }))
            {
                // « Failed to get data » : le jeu n'a pas ces attributs pour cette personne (arbitres)
                if (!r.TryGetValue(a, out var v) || string.IsNullOrEmpty(v) || v == "Failed to get data") continue;
                if (StaffValue(v, out bool masked) is double x) { s.Attr[a] = x; s.Masked |= masked; }
                else if (unknownWords.Count < 10) unknownWords.Add($"{a}=« {v} »");
            }
            list.Add(s);
        }
        if (unknownWords.Count > 0) warn?.Invoke("valeurs de préparateurs illisibles : " + string.Join(", ", unknownWords));
        return list;
    }

    // Attribut de staff : nombre, "valeur;min;max", ou mot (FM26 affiche des mots : Élite = 20, Inadapté = 1-3…)
    private static readonly Dictionary<string, double> StaffWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["elite"] = 20, ["élite"] = 20, ["outstanding"] = 18.5, ["exceptionnel"] = 18.5, ["remarquable"] = 18.5,
        ["very good"] = 16, ["très bon"] = 16, ["good"] = 13, ["bon"] = 13, ["average"] = 10.5, ["moyen"] = 10.5,
        ["competent"] = 8, ["compétent"] = 8, ["reasonable"] = 5, ["raisonnable"] = 5, ["unsuited"] = 2, ["inadapté"] = 2,
    };

    private static double? StaffValue(string v, out bool masked)
    {
        masked = false;
        var parts = v.Split(';');
        if (Num(parts[0]) is double x)
        {
            masked = parts.Length == 3 && parts[1] != parts[2];
            return x;
        }
        return StaffWords.TryGetValue(v.Trim(), out var w) ? w : null;
    }

    // Fichier lu par les colonnes de préparateurs du jeu : index, meilleur domaine, puis un domaine par colonne.
    // Valeurs = étoiles × 10, tronquées (47 = 4,7 étoiles ; le jeu affiche 4,5 : demi-étoile inférieure).
    public static int WriteGameStaff(string path, IEnumerable<StaffData> staff)
    {
        int n = 0;
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            w.WriteLine("index\tentr\t" + string.Join("\t", StaffEngine.Categories.Select(c => "entr_" + c.Key)));
            foreach (var s in staff.Where(s => !double.IsNaN(s.BestPoints)))
            {
                w.WriteLine($"{s.Index}\t{Tenths(s.BestPoints)}\t" + string.Join("\t", s.Points.Select(Tenths)));
                n++;
            }
        }
        File.Copy(tmp, path, true);
        File.Delete(tmp);
        return n;

        // Tronqué (pas arrondi) : 29,7 -> 29, pour que « 30 et plus » veuille toujours dire 3 étoiles dans le jeu
        static string Tenths(double points) => double.IsNaN(points) ? "" : Math.Floor(StaffEngine.Stars(points) * 10 + 1e-9).ToString("0", Inv);
    }

    public static double? Num(string s) =>
        !string.IsNullOrEmpty(s) && double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : null;

    // "15/17" -> 16
    private static double Stars(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var vals = s.Split('/').Select(Num).Where(v => v.HasValue).Select(v => v.Value).ToList();
        return vals.Count > 0 ? vals.Average() : 0;
    }

    private static IEnumerable<Dictionary<string, string>> ReadTsv(string path, Action<string> warn)
    {
        if (!File.Exists(path)) { warn?.Invoke($"fichier absent : {Path.GetFileName(path)}"); yield break; }
        using var r = new StreamReader(path, Encoding.UTF8, true);
        var header = r.ReadLine()?.Split('\t');
        if (header == null) yield break;
        string line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var cells = line.Split('\t');
            var d = new Dictionary<string, string>(header.Length);
            for (int i = 0; i < header.Length; i++) d[header[i]] = i < cells.Length ? cells[i] : "";
            yield return d;
        }
    }
}
