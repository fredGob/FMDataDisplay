// FM27 — calcul de la note de performance d'un joueur.
//
// Fichier autonome (aucune dépendance au jeu) : utilisé par l'outil de test hors jeu (Note/Outil)
// et, plus tard, inclus tel quel dans le plugin.
//
// Principe (repris de FM26, voir DataAnalyser/FM26_dataAnalyser.html) :
//   1. Profils du joueur (GK, CB, FB, DM, CM, AM, WG, ST) d'après ses postes.
//   2. Pour chaque profil, un pool de référence : joueurs de ce profil, dans les championnats forts,
//      avec assez de minutes. Il sert d'étalon.
//   3. Chaque stat est tirée vers la moyenne du pool selon les minutes jouées :
//         stat' = (minutes × stat + K × moyenne) / (minutes + K)
//      → un joueur avec peu de minutes reste proche de la moyenne, sans seuil strict.
//   4. Chaque stat est ramenée sur 0–100 entre le 1er et le 95e centile (bornée), calculés championnat par
//      championnat (joueurs ≥ 450 min) ; un championnat de n joueurs est mêlé à l'échelle du pool global
//      avec le poids n / (n + 20), pour ne pas dépendre de 5 joueurs dans les petits championnats.
//   5. Note brute = moyenne pondérée des stats (poids du profil, réglés sur le CA équivalent le 3 nov. 2026 :
//      voir Profiles), puis équilibrage des profils : chaque profil
//      est recalé pour que ses réguliers des grands championnats (≥ 900 min) aient la même médiane et le même
//      95e centile que la moyenne des profils (sinon la meilleure note favorise milieux, ailiers et gardiens).
//      Choix du 3 nov. 2026 (comparatif : Outil --variantes).
//   6. Note finale = note brute × facteur championnat, calculé d'après la seule réputation du championnat dans le
//      jeu (décision de Fred, 3 nov. 2026 : le CA équivalent ne sert qu'à étalonner, pas au calcul) :
//         facteur = 1 − ((200 − Reputation) / 150)²   (au moins 0,05)
//      Premier League 0,99 ; Ligue 1 0,97 ; Brésil 0,92 ; MLS 0,84 ; Ligue 2 0,75 ; Ligue 3 0,48 ; National 0,25.
//      Équipes de jeunes rangées à part (réputation 0) : 0,05. Comparatif : Outil --facteurs.
//      (Avant, du 2 au 3 nov. : niveau = CA équivalent moyen des titulaires, facteur 1 − ((max − niveau) / 80)².)
//   7. La note affichée est la meilleure parmi les profils du joueur.
//
// Matchs non détaillés : dans beaucoup de petits championnats, le jeu ne calcule que des stats
// simplifiées (distance, récupérations, pressings... à 0 ; passes générées). Pour ces joueurs, on
// n'utilise que les stats comparables (BasicStats) ; les autres comptent comme « dans la moyenne ».
// Leur note FM, plus haute et plus dispersée, est recalée sur celle des matchs détaillés.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FM27Note;

// Facteur championnat (3 nov. 2026, décision de Fred : le niveau d'un championnat = sa réputation dans le jeu, rien d'autre ;
// le CA équivalent ne sert qu'à étalonner le modèle, avec l'outil, sur une partie aux attributs visibles)
public enum LeagueFactorMode
{
    ReputationLineaire,     // 0,20 + 0,80 × Reputation / 200 (formule FM26)
    ReputationQuadratique,  // 1 − ((ReputationTop − Reputation) / ReputationRange)², au moins MinLeagueFactor
    CaEquivalent,           // ancienne méthode (niveau = CA équivalent moyen des titulaires) : comparaison seulement
}

public sealed class NoteSettings
{
    public double ShrinkMinutes = 1000;    // K : minutes « fictives » à la moyenne du pool (≈ 11 matchs)
    public double RefMinMinutes = 500;     // pool de référence : minutes minimum
    public double RefMinStrength = 15;     // pool de référence : force minimum du championnat (0–20)
    // Choisi le 3 nov. 2026 (Outil --facteurs sur la partie de test) : quadratique R = 150, corrélation avec le CA
    // équivalent 0,797 (ancienne méthode par le CA : 0,805 ; linéaire FM26 : 0,737), top 100 et stars quasi inchangés
    public LeagueFactorMode LeagueMode = LeagueFactorMode.ReputationQuadratique;
    public double ReputationTop = 200, ReputationRange = 150;   // mode quadratique
    public double LeagueFloor = 0.20;      // mode linéaire : facteur d'une réputation nulle
    public double LevelRange = 80;         // facteur = 1 − ((niveau max − niveau) / LevelRange)^LevelPower
    public double LevelPower = 2;
    public double MinLeagueFactor = 0.05;
    public double LevelMinMinutes = 450;   // joueurs comptés pour mesurer le niveau d'un championnat
    public int LevelMinPlayers = 30;
    public double LowPercentile = 1;       // borne basse de l'échelle 0–100
    public double HighPercentile = 95;     // borne haute
    // Mise à l'échelle 0–100 de chaque stat :
    //   Lineaire : entre le 1er et le 95e centile du pool (méthode FM26) ;
    //   Rang     : centile du joueur dans le pool (0 = le plus bas, 100 = le plus haut) ;
    //   Ecart    : 50 + 20 × écarts-types au-dessus de la moyenne (borné à 0–100).
    public ScaleMode Scale = ScaleMode.Lineaire;
    // Échelle calculée championnat par championnat (mêlée à l'échelle globale si le championnat a peu de joueurs)
    public bool PerLeague = true;          // choisi le 3 nov. 2026
    public double LeaguePrior = 20;        // poids, en joueurs, de l'échelle globale dans ce mélange
    public double LeagueRefMinMinutes = 450;   // joueurs du championnat servant d'échelle
    // Équilibrage des profils (même médiane et même 95e centile pour tous les profils, mesurés sur les
    // joueurs du pool de référence ayant au moins BalanceMinMinutes)
    public bool BalanceProfiles = true;    // choisi le 3 nov. 2026
    // Échelle d'affichage : note brute visée pour la médiane et le 95e centile des réguliers des grands championnats
    // (NaN = moyenne des profils, échelle « naturelle » de la moyenne des stats)
    // Choix du 3 nov. 2026 : « un peu plus large » (≈ 160 joueurs ≥ 80, ≈ 13 ≥ 90 ; naturelle ≈ 55,5 / 77,3)
    public double BalanceTarget50 = 55, BalanceTarget95 = 80;
    // Contexte d'équipe : les stats de volume sont ramenées à une possession de 50 %.
    //   défense : × (0,5 / (1 − possession))^TeamAdjustDefense   (équipe qui domine = peu de duels à jouer)
    //   ballon  : × (0,5 / possession)^TeamAdjustBallon          (équipe dominée = peu de ballons à toucher)
    // Possession estimée par le volume de passes de l'équipe comparé à la moyenne de son championnat.
    public double TeamAdjustDefense = 0;
    public double TeamAdjustBallon = 0;
    public double PossessionMin = 0.30, PossessionMax = 0.70;
    public double TeamMinMatches = 3;      // matchs (détaillés) minimum pour estimer la possession d'une équipe
    // Contexte d'équipe, stat par stat : chaque stat de volume du joueur × (moyenne du championnat / total de son
    // équipe pour cette stat)^TeamRelative. Ex. dégagements d'un défenseur de Lorient (équipe qui dégage beaucoup)
    // revus à la baisse, interceptions d'un défenseur du PSG (équipe qui intercepte beaucoup) aussi.
    public double TeamRelative = 0.75;     // choisi le 3 nov. 2026
    public bool TeamRelativeExtra = true;  // choisi le 3 nov. 2026 ; inclure aussi distance, sprints et % passes (style de jeu de l'équipe)
    public double BalanceMinMinutes = 900;
    public double NoteMinMinutes = 200;    // choisi le 3 nov. 2026 : en dessous, note = 0 (pas assez de temps de jeu)
    public int MinPoolSize = 30;           // en dessous, on élargit le pool (force puis minutes)
    public double CalibMinMinutes = 450;   // recalage de la note FM des matchs non détaillés :
    public double CalibMinStrength = 6;    //   joueurs comparés dans les championnats de force 6 à 12,
    public double CalibMaxStrength = 12;   //   où les deux sortes de joueurs existent
}

public enum ScaleMode { Lineaire, Rang, Ecart }

// Phase de jeu d'une stat de volume (pour le contexte d'équipe) ; Aucune = pourcentage, but, note…
public enum Phase { Aucune, Defense, Ballon }

public sealed class StatDef
{
    public string Key;         // propriété FM27
    public string Label;       // libellé court
    public bool Inverted;      // plus bas = mieux
    public string Base;        // pour un pourcentage : stat qui dit s'il y a eu des tentatives
    public Phase Phase;        // stat de volume liée à la possession de l'équipe
}

public sealed class PlayerData
{
    public int Index;
    public string Name = "";
    public string Positions = "";
    public double Minutes;
    public double LeagueStrength;                               // 0–20 (Reputation de la compétition / 10)
    public string LeagueId = "";                                // identifiant de la compétition (dref)
    public double LeagueLevel = double.NaN;                     // niveau réel : CA équivalent moyen des joueurs réguliers
    public bool Detailed = true;                                // false = stats simplifiées (match non détaillé)
    public string TeamId = "";                                  // équipe (dref)
    public string ClubId = "";                                  // club (dref)
    public double TeamPossession = double.NaN;                  // possession estimée de son équipe (0–1)
    public double DefenseAdjust = 1, BallAdjust = 1;            // corrections de contexte d'équipe (stats de volume)
    public Dictionary<string, double> TeamStatFactor;           // correction stat par stat (contexte d'équipe), ou null
    public Dictionary<string, double> Stats = new();            // stat absente = pas de valeur
    public Dictionary<string, string> Text;                     // textes bruts gardés pour le fichier de recrutement (étoiles, valeur…)
    public bool? Female;                                        // IsPersonFemale (null si l'extraction ne le lit pas)
    public bool MaskedAttributes;                               // attributs connus seulement par une fourchette (milieu retenu)
    public List<string> Profiles = new();                       // rempli par le moteur

    // Résultats
    public Dictionary<string, ProfileScore> Scores = new();     // profil -> note (style « Complet »)
    public string BestProfile;
    public double BestScore = double.NaN;
}

public sealed class ProfileScore
{
    public double Raw;                                          // 0–100, avant facteur championnat
    public double Final;                                        // après facteur championnat
    public Dictionary<string, double> Components = new();       // stat -> 0–100
    public Dictionary<string, double> Info = new();             // stats « pour info » (InfoStats) -> 0–100, hors note
}

public sealed class PoolInfo
{
    public string Profile;
    public int Size;
    public double MinStrength, MinMinutes;
    public Dictionary<string, (double mean, double lo, double hi)> Bounds = new();
}

public static class NoteEngine
{
    // Stats de volume par phase de jeu (déclarées avant le catalogue, qui s'en sert)
    private static readonly HashSet<string> DefenseVolume = new()
    {
        "ClearancesPer90", "BlocksPer90", "InterceptionsPer90", "TacklesCompletedPer90", "KeyTacklesPer90",
        "HeadersCompletedPer90", "HeaderAttemptsPer90", "HeadersLostPer90", "KeyHeadersPer90", "PossessionWonPer90",
        "PressuresCompletedPer90", "PressuresAttemptedPer90", "TotalSavesPer90",
    };
    private static readonly HashSet<string> BallVolume = new()
    {
        "PassCompletedPer90", "ProgressivePassesPer90", "OpenPlayKeyPassesPer90", "ExpectedAssistsPer90",
        "ChancesCreatedPer90", "OpenPlayCrossesAttemptedPer90", "OpenPlayCrossesCompletedPer90", "DribblesMadePer90",
        "PossessionLostPer90",
    };
    // Stats liées au style de l'équipe sans être des volumes de possession : corrigées seulement par TeamRelative
    private static readonly string[] TeamStyleStats = { "DistanceRanPer90", "SprintsPer90", "PassCompletionPercentage" };

    private static Phase PhaseOf(string key) =>
        DefenseVolume.Contains(key) ? Phase.Defense : BallVolume.Contains(key) ? Phase.Ballon : Phase.Aucune;

    // ── Catalogue des stats utilisées ──────────────────────────────────────
    public static readonly Dictionary<string, StatDef> Catalog = new[]
    {
        S("AverageRating", "Note FM"),
        // Gardien
        S("ExpectedGoalsPreventedPer90", "xG évités"),
        S("TotalSavePercentage", "% arrêts", b: "TotalSavesPer90"),
        S("TotalSavesPer90", "Arrêts"),
        S("ConcededPer90", "Buts enc.", inv: true),
        S("TotalExpectedSavePercentage", "% arrêts attendu", b: "TotalSavesPer90"),
        S("CleanSheetsPer90", "Clean sheets"),
        // Défense
        S("ClearancesPer90", "Dégagements"),
        S("BlocksPer90", "Contres"),
        S("InterceptionsPer90", "Interceptions"),
        S("TacklesCompletedPer90", "Tacles"),
        S("KeyTacklesPer90", "Tacles décisifs"),
        S("HeadersCompletedPer90", "Aériens gagnés"),
        S("HeaderAttemptsPer90", "Duels aériens"),
        S("HeadersLostPer90", "Aériens perdus", inv: true),
        S("HeaderCompletionPercentage", "% aériens", b: "HeaderAttemptsPer90"),
        S("KeyHeadersPer90", "Têtes décisives"),
        S("TackleCompletionPercentage", "% tacles", b: "TacklesCompletedPer90"),
        // Récupération / perte
        S("PossessionWonPer90", "Poss. gagnées"),
        S("PossessionLostPer90", "Poss. perdues", inv: true),
        S("PressuresCompletedPer90", "Pressings réussis"),
        S("PressuresAttemptedPer90", "Pressings tentés"),
        // Passes / création
        S("PassCompletionPercentage", "% passes", b: "PassAttemptsPer90"),
        S("PassCompletedPer90", "Passes réussies"),
        S("ProgressivePassesPer90", "Passes prog."),
        S("OpenPlayKeyPassesPer90", "Passes clés"),
        S("ExpectedAssistsPer90", "xA"),
        S("ChancesCreatedPer90", "Occasions créées"),
        S("OpenPlayCrossesAttemptedPer90", "Centres tentés"),
        S("OpenPlayCrossesCompletedPer90", "Centres réussis"),
        S("OpenPlayCrossCompletionPercentage", "% centres", b: "OpenPlayCrossesAttemptedPer90"),
        S("AssistsPer90", "Passes D"),
        S("DribblesMadePer90", "Dribbles"),
        // Tir
        S("ShotsOnTargetPercentage", "% cadrés", b: "ShotsPer90"),
        S("NonPenaltyExpectedGoalsPer90", "xG (hors pen.)"),
        S("GoalsPer90", "Buts"),
        S("ShotsPer90", "Tirs"),
        S("ShotsOnTargetPer90", "Tirs cadrés"),
        S("GoalsPerShotPercentage", "% conversion", b: "ShotsPer90"),
        // Physique
        S("DistanceRanPer90", "Distance"),
        S("SprintsPer90", "Sprints"),
        // Équipe / disponibilité
        S(OnPitchGoalDiff, "Diff. buts sur le terrain"),
        S("InjuriesPercentageMissed", "% absences blessure", inv: true),
    }.ToDictionary(s => s.Key);

    // Stat calculée : buts de l'équipe − buts encaissés, par 90 min, quand le joueur est sur le terrain
    public const string OnPitchGoalDiff = "OnPitchGoalDiffPer90";

    public static void AddDerivedStats(PlayerData p)
    {
        if (p.Stats.TryGetValue("TeamGoalsPer90", out var g) && p.Stats.TryGetValue("TeamConcededPer90", out var c))
            p.Stats[OnPitchGoalDiff] = g - c;
    }

    private static StatDef S(string key, string label, bool inv = false, string b = null) =>
        new() { Key = key, Label = label, Inverted = inv, Base = b, Phase = PhaseOf(key) };


    // ── Profils et styles (poids FM26, traduits vers les stats FM27) ───────
    public static readonly string[] ProfileOrder = { "GK", "CB", "FB", "DM", "CM", "AM", "WG", "ST" };

    public static readonly Dictionary<string, string> ProfileLabels = new()
    {
        ["GK"] = "Gardien", ["CB"] = "Déf. central", ["FB"] = "Latéral", ["DM"] = "Mil. défensif",
        ["CM"] = "Milieu", ["AM"] = "Meneur", ["WG"] = "Ailier", ["ST"] = "Buteur",
    };

    private const string R = "AverageRating", XGP = "ExpectedGoalsPreventedPer90", SAVEP = "TotalSavePercentage",
        SAVES = "TotalSavesPer90", CONC = "ConcededPer90", CLR = "ClearancesPer90", BLK = "BlocksPer90",
        INT = "InterceptionsPer90", TCK = "TacklesCompletedPer90", KTCK = "KeyTacklesPer90", AERW = "HeadersCompletedPer90",
        PWON = "PossessionWonPer90", PLOST = "PossessionLostPer90", PRESS = "PressuresCompletedPer90",
        PASSP = "PassCompletionPercentage", PASSS = "PassCompletedPer90", PROG = "ProgressivePassesPer90",
        KP = "OpenPlayKeyPassesPer90", XA = "ExpectedAssistsPer90", CHC = "ChancesCreatedPer90",
        CRA = "OpenPlayCrossesAttemptedPer90", CRC = "OpenPlayCrossesCompletedPer90", DRB = "DribblesMadePer90",
        SOTP = "ShotsOnTargetPercentage", XG = "NonPenaltyExpectedGoalsPer90", GLS = "GoalsPer90",
        DIST = "DistanceRanPer90", SPR = "SprintsPer90";

    private static (string stat, double w)[] W(params (string, double)[] w) => w;

    // profil -> style -> poids. « Complet » = style par défaut, utilisé pour la note affichée.
    // « Complet » réglé le 3 nov. 2026 (choix de Fred, variante C) : poids qui classent le mieux, dans chaque championnat,
    // les joueurs ≥ 900 min selon leur CA équivalent, sur une partie aux attributs visibles (Outil --poids, partie de test
    // du 25/02/2027). Toutes les stats du poste (Calibrage.Candidates) sauf différence de buts sur le terrain et absences
    // sur blessure ; ≤ 30 % par stat ; arrondis à 5 %. Corrélation dans un championnat 0,26 (FM26) -> 0,41.
    // Le CA équivalent ne sert qu'à ce réglage : les poids sont fixes, la note ne dépend que des stats.
    // « FM26 » = poids d'avant (profils FM26 de Fred), gardés pour comparer.
    public static readonly Dictionary<string, Dictionary<string, (string stat, double w)[]>> Profiles = new()
    {
        ["GK"] = new()
        {
            ["Complet"] = W(("AverageRating", 30), ("ConcededPer90", 30), ("PossessionWonPer90", 30), ("CleanSheetsPer90", 10)),
            ["FM26"] = W((R, 20), (XGP, 25), (SAVEP, 20), (SAVES, 15), (CONC, 10), (PWON, 10)),
            ["Ligne"] = W((R, 10), (XGP, 25), (SAVEP, 20), (CONC, 15), (CLR, 10), (PWON, 10)),
            ["Relanceur"] = W((R, 10), (XGP, 20), (SAVEP, 15), (CONC, 15), (PASSS, 20), (PASSP, 20)),
            ["Libéro"] = W((R, 10), (XGP, 20), (SAVEP, 15), (CONC, 15), (INT, 10), (PWON, 10), (PASSP, 20)),
        },
        ["CB"] = new()
        {
            ["Complet"] = W(("PossessionWonPer90", 30), ("AverageRating", 25), ("PossessionLostPer90", 15), ("HeadersLostPer90", 10), ("ProgressivePassesPer90", 5), ("DistanceRanPer90", 5), ("SprintsPer90", 5), ("TacklesCompletedPer90", 5)),
            ["FM26"] = W((R, 20), (INT, 10), (BLK, 10), (CLR, 10), (KTCK, 15), (AERW, 25), (PWON, 10)),
            ["Stoppeur"] = W((R, 10), (INT, 10), (BLK, 10), (CLR, 20), (KTCK, 10), (AERW, 30), (TCK, 10)),
            ["Relanceur"] = W((R, 10), (PASSP, 20), (PROG, 15), (PASSS, 15), (PWON, 15), (AERW, 15), (PLOST, 10)),
            ["Excentré"] = W((R, 10), (PASSP, 20), (PROG, 15), (PWON, 15), (AERW, 15), (DIST, 15), (DRB, 10)),
        },
        ["FB"] = new()
        {
            ["Complet"] = W(("PossessionWonPer90", 30), ("AverageRating", 20), ("PossessionLostPer90", 15), ("TacklesCompletedPer90", 10), ("DistanceRanPer90", 5), ("SprintsPer90", 5), ("OpenPlayCrossesCompletedPer90", 5), ("DribblesMadePer90", 5), ("AssistsPer90", 5)),
            ["FM26"] = W((R, 20), (TCK, 15), (INT, 15), (DIST, 15), (SPR, 10), (DRB, 10), (PWON, 15)),
            ["Défensif"] = W((R, 10), (TCK, 20), (INT, 20), (DIST, 15), (SPR, 10), (PRESS, 10), (PWON, 15)),
            ["Offensif"] = W((R, 10), (PWON, 20), (CRC, 20), (DIST, 10), (SPR, 15), (KP, 10), (PROG, 15)),
            ["Intérieur"] = W((R, 10), (PWON, 20), (XA, 20), (DIST, 10), (SPR, 10), (KP, 15), (PROG, 15)),
        },
        ["DM"] = new()
        {
            ["Complet"] = W(("AverageRating", 30), ("PossessionWonPer90", 30), ("PossessionLostPer90", 15), ("OpenPlayKeyPassesPer90", 15), ("SprintsPer90", 5), ("ExpectedAssistsPer90", 5)),
            ["FM26"] = W((R, 20), (PASSP, 15), (INT, 15), (PWON, 20), (TCK, 10), (PRESS, 10), (DIST, 10)),
            ["Sentinelle"] = W((R, 10), (TCK, 15), (INT, 15), (BLK, 15), (PWON, 20), (AERW, 10), (PASSP, 15)),
            ["Meneur"] = W((R, 10), (INT, 15), (PWON, 15), (PRESS, 10), (PROG, 20), (PASSP, 15), (KP, 15)),
        },
        ["CM"] = new()
        {
            ["Complet"] = W(("PossessionWonPer90", 25), ("AverageRating", 20), ("PossessionLostPer90", 15), ("DribblesMadePer90", 10), ("NonPenaltyExpectedGoalsPer90", 10), ("GoalsPer90", 10), ("OpenPlayKeyPassesPer90", 5), ("AssistsPer90", 5)),
            ["FM26"] = W((R, 20), (PRESS, 15), (PWON, 15), (DIST, 10), (PROG, 15), (PASSP, 15), (KP, 10)),
            ["Organisateur"] = W((R, 10), (PRESS, 10), (PWON, 10), (PLOST, 10), (PROG, 20), (PASSP, 20), (KP, 20)),
            ["Box-to-Box"] = W((R, 10), (PRESS, 15), (PWON, 15), (DIST, 20), (SPR, 15), (PASSP, 15), (KP, 10)),
            ["Récupérateur"] = W((R, 10), (PRESS, 20), (PWON, 20), (DIST, 10), (TCK, 15), (PASSP, 15), (PROG, 10)),
        },
        ["AM"] = new()
        {
            ["Complet"] = W(("PossessionLostPer90", 25), ("AverageRating", 15), ("PossessionWonPer90", 15), ("OpenPlayKeyPassesPer90", 10), ("GoalsPer90", 10), ("DribblesMadePer90", 5), ("AssistsPer90", 5), ("NonPenaltyExpectedGoalsPer90", 5), ("ShotsPer90", 5), ("ShotsOnTargetPercentage", 5)),
            ["FM26"] = W((R, 20), (PROG, 15), (PASSP, 15), (KP, 15), (XA, 15), (XG, 10), (DRB, 10)),
            ["Créateur"] = W((R, 10), (CHC, 20), (XA, 20), (KP, 20), (PASSP, 10), (PROG, 10), (DRB, 10)),
            ["Attaquant"] = W((R, 10), (XA, 15), (GLS, 20), (XG, 20), (DRB, 10), (KP, 10), (SPR, 15)),
        },
        ["WG"] = new()
        {
            ["Complet"] = W(("PossessionLostPer90", 25), ("AverageRating", 20), ("ShotsOnTargetPer90", 20), ("PossessionWonPer90", 15), ("OpenPlayKeyPassesPer90", 10), ("ExpectedAssistsPer90", 5), ("GoalsPer90", 5)),
            ["FM26"] = W((R, 20), (KP, 15), (XA, 15), (DRB, 20), (SPR, 10), (GLS, 10), (CRA, 10)),
            ["Dribbleur"] = W((R, 10), (KP, 15), (XA, 15), (DRB, 20), (SPR, 15), (CRA, 10), (CRC, 15)),
            ["Créateur"] = W((R, 10), (KP, 15), (XA, 20), (DRB, 10), (PASSP, 15), (CHC, 20), (CRC, 10)),
            ["Attaquant"] = W((R, 10), (KP, 10), (XA, 10), (GLS, 20), (SPR, 10), (XG, 10), (SOTP, 10)),
        },
        ["ST"] = new()
        {
            ["Complet"] = W(("PossessionLostPer90", 20), ("GoalsPer90", 15), ("PossessionWonPer90", 15), ("AverageRating", 10), ("ShotsPer90", 10), ("ShotsOnTargetPercentage", 10), ("HeadersCompletedPer90", 10), ("AssistsPer90", 5), ("ExpectedAssistsPer90", 5)),
            ["FM26"] = W((R, 20), (GLS, 25), (XG, 10), (SOTP, 10), (XA, 10), (SPR, 15), (DRB, 10)),
            ["Finisseur"] = W((R, 10), (GLS, 30), (XG, 15), (SOTP, 10), (XA, 10), (SPR, 15), (DRB, 10)),
            ["Pivot"] = W((R, 10), (GLS, 25), (XG, 10), (AERW, 20), (XA, 10), (PRESS, 15), (KP, 10)),
            ["Créateur"] = W((R, 10), (GLS, 20), (XG, 10), (CHC, 15), (XA, 15), (KP, 15), (DRB, 15)),
        },
    };

    // ── Profils d'après les postes (codes français du jeu, comme FM26) ─────
    // Ex : "D (GC), AL (G)" -> CB, FB ; "MD, M (C)" -> DM, CM ; "MO (DG), BT (C)" -> WG, ST
    public static List<string> ProfilesFromPositions(string positions)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(positions)) return new List<string>();
        foreach (var group in positions.Split(','))
        {
            var g = group.Trim();
            if (g.Length == 0) continue;
            string codesPart = g, sidesPart = "C";
            int paren = g.IndexOf('(');
            if (paren >= 0)
            {
                codesPart = g.Substring(0, paren).Trim();
                sidesPart = g.Substring(paren + 1).TrimEnd(')', ' ').ToUpperInvariant();
            }
            bool centre = sidesPart.Contains('C');
            bool side = sidesPart.IndexOfAny(new[] { 'D', 'G', 'R', 'L' }) >= 0;
            foreach (var raw in codesPart.Split('/'))
            {
                switch (raw.Trim().ToUpperInvariant())
                {
                    case "GB": case "GK": set.Add("GK"); break;
                    case "D": if (centre) set.Add("CB"); if (side) set.Add("FB"); break;
                    case "AL": case "WB": set.Add("FB"); break;
                    case "MD": case "DM": set.Add("DM"); set.Add("CM"); break;
                    case "M": if (centre) set.Add("CM"); if (side) set.Add("WG"); break;
                    case "MO": case "AM": if (centre) set.Add("AM"); if (side) set.Add("WG"); break;
                    case "BT": case "ST": set.Add("ST"); break;
                }
            }
        }
        return ProfileOrder.Where(set.Contains).ToList();
    }

    // Ancien facteur, d'après la réputation seule (utilisé si le niveau réel est inconnu)
    public static double LeagueFactor(double strength, NoteSettings s) =>
        s.LeagueFloor + (1 - s.LeagueFloor) * Math.Clamp(strength, 0, 20) / 20.0;

    public static double LeagueFactor(PlayerData p, double topLevel, NoteSettings s)
    {
        if (s.LeagueMode == LeagueFactorMode.ReputationQuadratique)
        {
            double rgap = Math.Max(0, s.ReputationTop - p.LeagueStrength * 10) / s.ReputationRange;
            return Math.Max(s.MinLeagueFactor, 1 - rgap * rgap);
        }
        if (s.LeagueMode == LeagueFactorMode.ReputationLineaire || double.IsNaN(p.LeagueLevel) || double.IsNaN(topLevel))
            return LeagueFactor(p.LeagueStrength, s);
        double gap = Math.Max(0, topLevel - p.LeagueLevel) / s.LevelRange;
        return Math.Max(s.MinLeagueFactor, 1 - Math.Pow(gap, s.LevelPower));
    }

    // Remplit LeagueLevel pour chaque joueur (si les attributs sont disponibles) ; renvoie le niveau maximum
    public static double SetLeagueLevels(IList<PlayerData> players, NoteSettings s)
    {
        foreach (var p in players) p.LeagueLevel = double.NaN;
        if (s.LeagueMode != LeagueFactorMode.CaEquivalent) return double.NaN;   // facteur d'après la réputation seule
        if (!players.Any(p => p.Stats.ContainsKey("AttributePassing"))) return double.NaN;
        var levels = LeagueLevels(players, CaEquivalent.Compute, s.LevelMinMinutes, s.LevelMinPlayers);
        if (levels.Count == 0) return double.NaN;
        double min = levels.Values.Min();
        foreach (var p in players) p.LeagueLevel = levels.TryGetValue(p.LeagueId, out var l) ? l : min;
        return levels.Values.Max();
    }

    // Stats fiables aussi dans les matchs non détaillés (comparées le 2 nov. 2026 sur 15 000 joueurs
    // de championnats de même niveau). Passes, tacles et xA y sont générés autrement : exclus.
    public static readonly HashSet<string> BasicStats = new()
    {
        "AverageRating", "GoalsPer90", "AssistsPer90", "NonPenaltyExpectedGoalsPer90", "ShotsPer90",
        "ShotsOnTargetPer90", "ShotsOnTargetPercentage", "GoalsPerShotPercentage", "ConcededPer90", "CleanSheetsPer90",
        "InjuriesPercentageMissed",
    };

    // Recalage de la note FM des matchs non détaillés : note' = A × note + B
    public sealed class RatingCalibration
    {
        public double A = 1, B = 0;
        public double MeanDetailed, SdDetailed, MeanSimple, SdSimple;
        public int CountDetailed, CountSimple;
    }

    // Valeur brute d'une stat pour un joueur ; null = pas de valeur exploitable
    // (stat non suivie dans un match non détaillé, ou % sans aucune tentative).
    public static double? RawValue(PlayerData p, string stat, RatingCalibration cal = null)
    {
        if (!p.Detailed && !BasicStats.Contains(stat)) return null;
        if (!p.Stats.TryGetValue(stat, out var v) || double.IsNaN(v)) return null;
        var def = Catalog[stat];
        if (def.Base != null && (!p.Stats.TryGetValue(def.Base, out var b) || b <= 0)) return null;
        if (stat == "AverageRating")
        {
            if (v <= 0) return null;
            if (!p.Detailed && cal != null) v = cal.A * v + cal.B;
        }
        if (def.Phase == Phase.Defense) v *= p.DefenseAdjust;
        else if (def.Phase == Phase.Ballon) v *= p.BallAdjust;
        if (p.TeamStatFactor != null && p.TeamStatFactor.TryGetValue(stat, out var tf)) v *= tf;
        return v;
    }

    // Équipes de jeunes (U18, U19…) : le jeu leur donne la division de l'équipe première du club. Quand un club a
    // plusieurs équipes dans le même championnat, celle qui a le plus de minutes jouées garde le championnat ;
    // les autres passent dans un pseudo-championnat « <championnat>#jeunes », dont le niveau est mesuré à part
    // (ou estimé très bas s'il a peu de joueurs réguliers). Sans ça, un U18 de Chelsea est noté comme un joueur
    // de Premier League. Sans effet si on l'appelle plusieurs fois.
    public const string YouthSuffix = "#jeunes";

    public static void SeparateYouthTeams(IList<PlayerData> players)
    {
        foreach (var g in players.Where(p => p.LeagueId != "" && p.ClubId != "" && p.TeamId != "")
                                 .GroupBy(p => (p.LeagueId, p.ClubId)))
        {
            var teams = g.GroupBy(p => p.TeamId).ToList();
            if (teams.Count < 2) continue;
            var main = teams.OrderByDescending(t => t.Sum(p => p.Minutes)).First().Key;
            foreach (var t in teams.Where(t => t.Key != main))
                foreach (var p in t)
                {
                    p.LeagueId += YouthSuffix;
                    p.LeagueStrength = 0;
                }
        }
    }

    // Possession estimée de chaque équipe : passes tentées par match (somme des joueurs aux stats détaillées)
    // comparées à la moyenne des équipes de son championnat ; possession ≈ 0,5 × passes / moyenne (bornée).
    // Remplit TeamPossession, DefenseAdjust et BallAdjust de chaque joueur.
    public static void SetTeamContext(IList<PlayerData> players, NoteSettings s)
    {
        var teams = new Dictionary<string, (double passes, double minutes, string league)>();
        var volume = DefenseVolume.Concat(BallVolume).Concat(s.TeamRelativeExtra ? TeamStyleStats : Array.Empty<string>()).ToArray();
        var totals = new Dictionary<string, double[]>();       // équipe -> total de chaque stat de volume
        foreach (var p in players)
        {
            p.TeamPossession = double.NaN;
            p.DefenseAdjust = p.BallAdjust = 1;
            p.TeamStatFactor = null;
            if (p.Detailed && p.Minutes > 0 && p.TeamId != "")
            {
                if (!totals.TryGetValue(p.TeamId, out var tot)) totals[p.TeamId] = tot = new double[volume.Length];
                for (int i = 0; i < volume.Length; i++)
                    if (p.Stats.TryGetValue(volume[i], out var sv) && !double.IsNaN(sv)) tot[i] += sv * p.Minutes / 90;
            }
            if (!p.Detailed || p.Minutes <= 0 || p.TeamId == "" || !p.Stats.TryGetValue("PassAttemptsPer90", out var pa)) continue;
            var t = teams.GetValueOrDefault(p.TeamId, (0, 0, p.LeagueId));
            teams[p.TeamId] = (t.passes + pa * p.Minutes / 90, t.minutes + p.Minutes, t.league);
        }
        var perMatch = teams.Where(kv => kv.Value.minutes / 990 >= s.TeamMinMatches)
                            .ToDictionary(kv => kv.Key, kv => (value: kv.Value.passes / (kv.Value.minutes / 990), kv.Value.league, matches: kv.Value.minutes / 990));
        var leagueAvg = perMatch.Values.GroupBy(t => t.league)
                                .ToDictionary(g => g.Key, g => g.Sum(t => t.value * t.matches) / g.Sum(t => t.matches));
        foreach (var p in players)
        {
            if (!perMatch.TryGetValue(p.TeamId, out var t) || !leagueAvg.TryGetValue(t.league, out var avg) || avg <= 0) continue;
            double poss = Math.Clamp(0.5 * t.value / avg, s.PossessionMin, s.PossessionMax);
            p.TeamPossession = poss;
            p.DefenseAdjust = Math.Pow(0.5 / (1 - poss), s.TeamAdjustDefense);
            p.BallAdjust = Math.Pow(0.5 / poss, s.TeamAdjustBallon);
        }

        if (s.TeamRelative <= 0) return;
        // Totaux par match de chaque équipe, moyenne de son championnat, puis facteur par stat (borné 0,5–2)
        var teamPerMatch = perMatch.ToDictionary(kv => kv.Key,
            kv => totals[kv.Key].Select(v => v / kv.Value.matches).ToArray());
        var leagueMean = perMatch.GroupBy(kv => kv.Value.league).ToDictionary(g => g.Key, g =>
            Enumerable.Range(0, volume.Length).Select(i => g.Sum(kv => teamPerMatch[kv.Key][i] * kv.Value.matches)
                                                          / g.Sum(kv => kv.Value.matches)).ToArray());
        var factors = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (team, t) in perMatch)
        {
            var mean = leagueMean[t.league];
            var f = new Dictionary<string, double>();
            for (int i = 0; i < volume.Length; i++)
                if (mean[i] > 0 && teamPerMatch[team][i] > 0)
                    f[volume[i]] = Math.Clamp(Math.Pow(mean[i] / teamPerMatch[team][i], s.TeamRelative), 0.5, 2);
            factors[team] = f;
        }
        foreach (var p in players)
            if (factors.TryGetValue(p.TeamId, out var f)) p.TeamStatFactor = f;
    }

    public static RatingCalibration CalibrateRating(IList<PlayerData> players, NoteSettings s)
    {
        var cal = new RatingCalibration();
        var sample = players.Where(p => p.Minutes >= s.CalibMinMinutes && p.LeagueStrength >= s.CalibMinStrength
                                        && p.LeagueStrength <= s.CalibMaxStrength
                                        && p.Stats.TryGetValue("AverageRating", out var r) && r > 0).ToList();
        var d = sample.Where(p => p.Detailed).Select(p => p.Stats["AverageRating"]).ToList();
        var x = sample.Where(p => !p.Detailed).Select(p => p.Stats["AverageRating"]).ToList();
        cal.CountDetailed = d.Count; cal.CountSimple = x.Count;
        if (d.Count < 50 || x.Count < 50) return cal;
        (cal.MeanDetailed, cal.SdDetailed) = MeanSd(d);
        (cal.MeanSimple, cal.SdSimple) = MeanSd(x);
        if (cal.SdSimple <= 0) return cal;
        cal.A = cal.SdDetailed / cal.SdSimple;
        cal.B = cal.MeanDetailed - cal.A * cal.MeanSimple;
        return cal;
    }

    private static (double mean, double sd) MeanSd(List<double> v)
    {
        double m = v.Average();
        return (m, Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count));
    }

    // ── Calcul ─────────────────────────────────────────────────────────────

    // Stats d'un profil ramenées sur 0–100 (tirées vers la moyenne, puis mises à l'échelle du pool)
    public sealed class ProfileContext
    {
        public string Profile;
        public List<PlayerData> Members;
        public List<PlayerData> Pool;
        public PoolInfo Info;
        public Dictionary<string, Dictionary<PlayerData, double>> Components = new();
    }

    public static void Init(IList<PlayerData> players)
    {
        foreach (var p in players)
        {
            p.Profiles = ProfilesFromPositions(p.Positions);
            AddDerivedStats(p);
            p.Scores.Clear();
            p.BestProfile = null;
            p.BestScore = double.NaN;
        }
        SeparateYouthTeams(players);
    }

    public static ProfileContext Prepare(string prof, IList<PlayerData> players, IEnumerable<string> stats,
                                         NoteSettings s, RatingCalibration cal)
    {
        var members = players.Where(p => p.Profiles.Contains(prof)).ToList();
        if (members.Count == 0) return null;

        // Pool de référence (matchs détaillés uniquement), élargi si trop petit
        double minStrength = s.RefMinStrength, minMinutes = s.RefMinMinutes;
        List<PlayerData> pool;
        while (true)
        {
            pool = members.Where(p => p.Detailed && p.Minutes >= minMinutes && p.LeagueStrength >= minStrength).ToList();
            if (pool.Count >= s.MinPoolSize || (minStrength <= 0 && minMinutes <= 90)) break;
            if (minStrength > 0) minStrength = Math.Max(0, minStrength - 2);
            else minMinutes = Math.Max(90, minMinutes / 2);
        }
        if (pool.Count < 2) return null;
        var ctx = new ProfileContext
        {
            Profile = prof, Members = members, Pool = pool,
            Info = new PoolInfo { Profile = prof, Size = pool.Count, MinStrength = minStrength, MinMinutes = minMinutes },
        };

        // Championnats : joueurs servant d'échelle (assez de minutes)
        var leagues = s.PerLeague
            ? members.GroupBy(p => p.LeagueId).ToDictionary(g => g.Key, g => g.ToList())
            : new Dictionary<string, List<PlayerData>> { [""] = members };

        foreach (var stat in stats.Distinct())
        {
            bool inv = Catalog[stat].Inverted;
            // Moyenne du pool (valeurs brutes), puis échelle sur les valeurs « tirées vers la moyenne »
            var raw = pool.Select(p => RawValue(p, stat, cal)).Where(v => v.HasValue).Select(v => v.Value).ToList();
            double mean = raw.Count > 0 ? raw.Average() : 0;
            double Shrink(PlayerData p, double m) =>
                (p.Minutes * (RawValue(p, stat, cal) ?? m) + s.ShrinkMinutes * m) / (p.Minutes + s.ShrinkMinutes);
            var global = new StatScale(pool.Select(p => Shrink(p, mean)), s);
            ctx.Info.Bounds[stat] = (mean, global.Lo, global.Hi);

            var comp = new Dictionary<PlayerData, double>(members.Count);
            foreach (var (league, group) in leagues)
            {
                // Échelle du championnat, mêlée à l'échelle globale : poids n / (n + LeaguePrior)
                StatScale local = null;
                double m = mean, w = 0;
                if (s.PerLeague && league != "")
                {
                    var refs = group.Where(p => p.Minutes >= s.LeagueRefMinMinutes && RawValue(p, stat, cal).HasValue).ToList();
                    if (refs.Count >= 5)
                    {
                        w = refs.Count / (refs.Count + s.LeaguePrior);
                        m = w * refs.Average(p => RawValue(p, stat, cal).Value) + (1 - w) * mean;
                        local = new StatScale(refs.Select(p => Shrink(p, m)), s);
                    }
                }
                foreach (var p in group)
                {
                    double v = Shrink(p, m);
                    double sc = global.Score(v, inv);
                    if (local != null) sc = w * local.Score(v, inv) + (1 - w) * sc;
                    comp[p] = sc;
                }
            }
            ctx.Components[stat] = comp;
        }
        return ctx;
    }

    // Échelle 0–100 d'une stat, d'après les valeurs d'un groupe de référence
    private sealed class StatScale
    {
        private readonly double[] _sorted;
        private readonly ScaleMode _mode;
        public readonly double Lo, Hi, Mean, Sd;

        public StatScale(IEnumerable<double> values, NoteSettings s)
        {
            _sorted = values.OrderBy(v => v).ToArray();
            _mode = s.Scale;
            Lo = Percentile(_sorted, s.LowPercentile);
            Hi = Percentile(_sorted, s.HighPercentile);
            if (_sorted.Length > 0) (Mean, Sd) = MeanSd(_sorted.ToList());
        }

        public double Score(double v, bool inverted)
        {
            double sc;
            switch (_mode)
            {
                case ScaleMode.Rang:
                    if (_sorted.Length == 0) return 50;
                    int below = LowerBound(_sorted, v), upTo = UpperBound(_sorted, v);
                    sc = (below + (upTo - below) / 2.0) / _sorted.Length * 100;
                    break;
                case ScaleMode.Ecart:
                    sc = Sd > 0 ? 50 + 20 * (v - Mean) / Sd : 50;
                    break;
                default:
                    sc = Hi > Lo ? (v - Lo) / (Hi - Lo) * 100 : 50;
                    break;
            }
            if (inverted) sc = 100 - sc;
            return Math.Clamp(sc, 0, 100);
        }

        private static int LowerBound(double[] a, double v)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (a[mid] < v) lo = mid + 1; else hi = mid; }
            return lo;
        }

        private static int UpperBound(double[] a, double v)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (a[mid] <= v) lo = mid + 1; else hi = mid; }
            return lo;
        }
    }

    // Football masculin et féminin notés séparément (championnats, niveaux et échelles à part) quand l'extraction
    // indique le sexe des joueurs ; sinon, un seul calcul comme avant.
    public static void ComputeAll(IList<PlayerData> players, NoteSettings s)
    {
        var women = players.Where(p => p.Female == true).ToList();
        if (women.Count == 0) { Compute(players, s); return; }
        Compute(players.Where(p => p.Female != true).ToList(), s);
        Compute(women, s);
    }

    public static double TopLevel = double.NaN;   // niveau du meilleur championnat, après Compute
    public static (double t50, double t95) NaturalTargets;   // échelle naturelle mesurée au dernier Compute

    public static List<PoolInfo> Compute(IList<PlayerData> players, NoteSettings s, string style = "Complet")
        => Compute(players, s, out _, style);

    public static List<PoolInfo> Compute(IList<PlayerData> players, NoteSettings s, out RatingCalibration cal, string style = "Complet")
    {
        var pools = new List<PoolInfo>();
        Init(players);
        cal = CalibrateRating(players, s);
        TopLevel = SetLeagueLevels(players, s);
        SetTeamContext(players, s);

        // 1. Note brute de chaque profil (moyenne pondérée des stats 0–100)
        var ctxs = new List<ProfileContext>();
        foreach (var prof in ProfileOrder)
        {
            var weights = Weights(prof, style);
            var info = InfoFor(prof, weights);
            // Chaque stat est mise à l'échelle séparément : les stats « pour info » ne changent pas la note
            var ctx = Prepare(prof, players, weights.Select(w => w.stat).Concat(info), s, cal);
            if (ctx == null) continue;
            pools.Add(ctx.Info);
            ctxs.Add(ctx);

            double wsum = weights.Sum(w => w.w);
            foreach (var p in ctx.Members)
            {
                var ps = new ProfileScore();
                double total = 0;
                foreach (var (stat, w) in weights)
                {
                    double sc = ctx.Components[stat][p];
                    ps.Components[stat] = sc;
                    total += sc * w;
                }
                ps.Raw = total / wsum;
                foreach (var stat in info) ps.Info[stat] = ctx.Components[stat][p];
                p.Scores[prof] = ps;
            }
        }

        // 2. Équilibrage des profils : chaque profil est recalé pour que ses joueurs réguliers des grands
        //    championnats aient la même médiane et le même 95e centile que la moyenne des profils.
        //    Sinon la « meilleure note » favorise les profils généreux (milieux, ailiers) au détriment
        //    des buteurs ou des latéraux.
        if (s.BalanceProfiles && ctxs.Count > 1)
        {
            var q = new Dictionary<string, (double q50, double q95)>();
            foreach (var ctx in ctxs)
            {
                var regulars = ctx.Pool.Where(p => p.Minutes >= s.BalanceMinMinutes).ToList();
                if (regulars.Count < s.MinPoolSize) regulars = ctx.Pool;
                var raws = regulars.Select(p => p.Scores[ctx.Profile].Raw).OrderBy(v => v).ToList();
                q[ctx.Profile] = (Percentile(raws, 50), Percentile(raws, 95));
            }
            NaturalTargets = (q.Values.Average(v => v.q50), q.Values.Average(v => v.q95));
            double t50 = double.IsNaN(s.BalanceTarget50) ? NaturalTargets.t50 : s.BalanceTarget50;
            double t95 = double.IsNaN(s.BalanceTarget95) ? NaturalTargets.t95 : s.BalanceTarget95;
            foreach (var ctx in ctxs)
            {
                var (q50, q95) = q[ctx.Profile];
                double scale = q95 > q50 ? (t95 - t50) / (q95 - q50) : 1;
                foreach (var p in ctx.Members)
                {
                    var ps = p.Scores[ctx.Profile];
                    ps.Raw = Math.Clamp(t50 + (ps.Raw - q50) * scale, 0, 100);
                }
            }
        }

        // 3. Facteur championnat, puis meilleur profil
        foreach (var ctx in ctxs)
            foreach (var p in ctx.Members)
            {
                var ps = p.Scores[ctx.Profile];
                ps.Final = ps.Raw * LeagueFactor(p, TopLevel, s);
                if (double.IsNaN(p.BestScore) || ps.Final > p.BestScore)
                {
                    p.BestScore = ps.Final;
                    p.BestProfile = ctx.Profile;
                }
            }

        // 4. Pas assez de temps de jeu : note 0 (après coup : n'influence ni les pools ni l'équilibrage)
        foreach (var ctx in ctxs)
            foreach (var p in ctx.Members)
                if (p.Minutes < s.NoteMinMinutes) { p.Scores[ctx.Profile].Final = 0; p.BestScore = 0; }
        return pools;
    }

    // Stats « pour info » : notées sur 0–100 comme les autres (même pool, même échelle) mais sans poids, pour la fiche
    // joueur du mode Moneyball. Gardien : choix de Fred (3 nov. 2026) de garder les poids C (Note FM, buts encaissés,
    // possessions gagnées, clean sheets) tout en montrant ce que le gardien fait lui-même (xG évités, arrêts).
    public static readonly Dictionary<string, string[]> InfoStats = new()
    {
        ["GK"] = new[] { XGP, SAVEP, SAVES },
    };

    public static string[] InfoFor(string profile, (string stat, double w)[] weights) =>
        InfoStats.TryGetValue(profile, out var l) ? l.Where(x => weights.All(w => w.stat != x)).ToArray() : Array.Empty<string>();

    public static (string stat, double w)[] Weights(string profile, string style) =>
        Profiles[profile].TryGetValue(style, out var w) ? w : Profiles[profile]["Complet"];

    // Niveau réel des championnats : CA équivalent moyen des joueurs avec assez de minutes.
    // Championnats trop petits : estimé d'après la réputation (droite ajustée sur les autres).
    public const int MinMeasuredLeagues = 8;

    public static Dictionary<string, double> LeagueLevels(IList<PlayerData> players, Func<PlayerData, double?> ca,
                                                          double minMinutes = 450, int minPlayers = 30)
    {
        Dictionary<string, double> Measure(double minutes)
        {
            var m = new Dictionary<string, double>();
            foreach (var g in players.Where(p => p.Minutes >= minutes && p.LeagueId != "").GroupBy(p => p.LeagueId))
            {
                var v = g.Select(ca).Where(x => x.HasValue).Select(x => x.Value).ToList();
                if (v.Count >= minPlayers) m[g.Key] = v.Average();
            }
            return m;
        }
        // Début de saison (peu de championnats déjà joués) : pas de droite fiable -> aucun niveau, le facteur
        // championnat se calcule alors d'après la réputation (ancien facteur)
        var measured = Measure(minMinutes);
        if (measured.Count < MinMeasuredLeagues) return new Dictionary<string, double>();
        var strength = players.Where(p => p.LeagueId != "").GroupBy(p => p.LeagueId).ToDictionary(g => g.Key, g => g.First().LeagueStrength);
        // droite niveau = c + d × force (réputation / 10)
        var pts = measured.Select(kv => (x: strength[kv.Key], y: kv.Value)).ToList();
        double mx = pts.Average(t => t.x), my = pts.Average(t => t.y);
        double sxx = pts.Sum(t => (t.x - mx) * (t.x - mx));
        // Réputations toutes identiques : pente relevée sur la partie de test (≈ 5,8 points de niveau par point de force)
        double d = sxx > 1e-6 ? pts.Sum(t => (t.x - mx) * (t.y - my)) / sxx : 5.8;
        double c = my - d * mx;
        var levels = new Dictionary<string, double>(measured);
        foreach (var (id, st) in strength)
            if (!levels.ContainsKey(id)) levels[id] = c + d * st;
        return levels;
    }

    public static double Percentile(IList<double> sorted, double pct)
    {
        if (sorted.Count == 0) return 0;
        double idx = (sorted.Count - 1) * pct / 100.0;
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        if (hi >= sorted.Count) return sorted[sorted.Count - 1];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}
