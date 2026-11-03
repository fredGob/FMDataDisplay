// CA équivalent : niveau actuel estimé à partir des attributs (le vrai CA n'est pas lisible dans le jeu,
// sauf pour les joueurs de son propre club, via AbilityProgressHistory / 220).
//
// Pour chaque profil du joueur : moyenne pondérée des attributs (vitesse/accélération et attributs clés
// du poste lourds, coups de pied arrêtés et « caractère » légers), puis CA = A + B × moyenne.
// On garde le meilleur profil. Vérifié le 2 nov. 2026 sur 48 joueurs au CA exact (Toulouse, de 65 à 137) :
// corrélation 0,975, erreur moyenne 4 points de CA.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FM27Note;

public static class CaEquivalent
{
    public const double A = -96.4, B = 17.44;

    private static readonly Dictionary<string, double> Base = new()
    {
        ["Acceleration"] = 3, ["Pace"] = 3, ["Agility"] = 1.5, ["Balance"] = 1, ["Strength"] = 1, ["Stamina"] = 1,
        ["NaturalFitness"] = 0.5, ["JumpingReach"] = 0.5,
        ["Anticipation"] = 2, ["Composure"] = 1.5, ["Concentration"] = 1.5, ["Decisions"] = 2, ["Positioning"] = 1,
        ["Movement"] = 1, ["Vision"] = 1, ["Teamwork"] = 0.5, ["WorkRate"] = 0.5, ["Bravery"] = 0.5, ["Flair"] = 0.5,
        ["Aggression"] = 0.25, ["Determination"] = 0.25, ["Leadership"] = 0.25,
        ["Corners"] = 0.2, ["FreeKicks"] = 0.2, ["PenaltyTaking"] = 0.2, ["LongThrows"] = 0.1,
        ["FirstTouch"] = 1, ["Technique"] = 1, ["Passing"] = 1,
    };

    // Ajouts par poste (s'additionnent à la base)
    private static readonly Dictionary<string, Dictionary<string, double>> ByProfile = new()
    {
        ["CB"] = new() { ["Marking"] = 3, ["Tackling"] = 3, ["Heading"] = 3, ["JumpingReach"] = 2.5, ["Positioning"] = 3, ["Strength"] = 2, ["Concentration"] = 2 },
        ["FB"] = new() { ["Marking"] = 2, ["Tackling"] = 2.5, ["Crossing"] = 2, ["Dribbling"] = 1, ["Positioning"] = 2, ["Stamina"] = 2, ["WorkRate"] = 1 },
        ["DM"] = new() { ["Tackling"] = 3, ["Marking"] = 2, ["Passing"] = 2, ["Positioning"] = 2.5, ["FirstTouch"] = 1.5, ["Teamwork"] = 1 },
        ["CM"] = new() { ["Passing"] = 3, ["FirstTouch"] = 2, ["Technique"] = 2, ["Tackling"] = 1.5, ["Vision"] = 2, ["Dribbling"] = 1, ["LongShots"] = 0.5, ["Stamina"] = 1.5 },
        ["AM"] = new() { ["Passing"] = 2.5, ["FirstTouch"] = 2, ["Technique"] = 2, ["Dribbling"] = 2, ["Vision"] = 2.5, ["Finishing"] = 1.5, ["LongShots"] = 1, ["Movement"] = 2, ["Flair"] = 1 },
        ["WG"] = new() { ["Dribbling"] = 3, ["Crossing"] = 2, ["FirstTouch"] = 2, ["Technique"] = 2, ["Passing"] = 1, ["Finishing"] = 1, ["Movement"] = 2, ["Flair"] = 1 },
        ["ST"] = new() { ["Finishing"] = 3.5, ["FirstTouch"] = 2, ["Technique"] = 1.5, ["Dribbling"] = 1.5, ["Heading"] = 2, ["Movement"] = 3, ["Composure"] = 2, ["JumpingReach"] = 1, ["Strength"] = 1.5 },
    };

    private static readonly Dictionary<string, double> Goalkeeper = new()
    {
        ["Reflexes"] = 3, ["Handling"] = 2.5, ["OneOnOnes"] = 2, ["AerialReach"] = 2, ["CommandOfArea"] = 1.5, ["Communication"] = 1,
        ["Kicking"] = 1, ["Throwing"] = 0.5, ["RushingOut"] = 1, ["Eccentricity"] = 0.1, ["TendencyToPunch"] = 0.1,
        ["Positioning"] = 2.5, ["Concentration"] = 1.5, ["Decisions"] = 1.5, ["Anticipation"] = 1, ["Composure"] = 1,
        ["Agility"] = 2, ["Acceleration"] = 0.5, ["JumpingReach"] = 1, ["Bravery"] = 0.5, ["FirstTouch"] = 0.3, ["Passing"] = 0.5,
    };

    private static readonly Dictionary<string, Dictionary<string, double>> Weights = BuildWeights();

    private static Dictionary<string, Dictionary<string, double>> BuildWeights()
    {
        var r = new Dictionary<string, Dictionary<string, double>> { ["GK"] = Goalkeeper };
        foreach (var (prof, extra) in ByProfile)
        {
            var w = new Dictionary<string, double>(Base);
            foreach (var (a, v) in extra) w[a] = w.GetValueOrDefault(a) + v;
            r[prof] = w;
        }
        return r;
    }

    // null si les attributs ne sont pas disponibles. Les profils doivent être remplis (NoteEngine.Init).
    public static double? Compute(PlayerData p)
    {
        double best = double.NaN;
        foreach (var prof in p.Profiles)
        {
            var w = Weights[prof];
            double sum = 0, wsum = 0;
            foreach (var (attr, v) in w)
            {
                if (!p.Stats.TryGetValue("Attribute" + attr, out var a)) return null;
                sum += a * v; wsum += v;
            }
            double ca = A + B * sum / wsum;
            if (double.IsNaN(best) || ca > best) best = ca;
        }
        return double.IsNaN(best) ? null : best;
    }
}
