// FM27 — étoiles d'entraînement des préparateurs, calculées à partir de leurs attributs de coaching.
//
// Formule communautaire FM26 (FMScout, FMInside), vérifiée le 3 nov. 2026 sur le calculateur FMScout FM26
// (10 essais en ne changeant qu'un attribut à la fois) :
//   points = attribut principal × 6 + attribut secondaire × 3 + (Autorité + Détermination + Motivation) × 2
//   gardiens et physique : attribut × 9 + (Autorité + Détermination + Motivation) × 2
//   étoiles = points / 60 + 0,5 ; le jeu arrondit à la demi-étoile inférieure, 5 étoiles au plus (270 points).
// Valable pour un préparateur affecté à un seul domaine : le jeu baisse ses étoiles s'il en a plusieurs.
// À vérifier dans FM27 sur ses propres préparateurs (écran d'affectation de l'entraînement).

using System;
using System.Collections.Generic;
using System.Linq;

namespace FM27Note;

public sealed class StaffCategory
{
    public string Key, Name, Main, Second;
    public int MainWeight, SecondWeight;

    public StaffCategory(string key, string name, string main, string second, int mainWeight = 6, int secondWeight = 3)
    {
        Key = key; Name = name; Main = main; Second = second; MainWeight = mainWeight; SecondWeight = secondWeight;
    }
}

public sealed class StaffData
{
    public int Index;
    public string Name, ClubId, Job;
    public double Age;
    public Dictionary<string, double> Attr = new();
    public bool Masked;                 // au moins un attribut connu seulement par une fourchette
    public double[] Points;             // par domaine (ordre de StaffEngine.Categories), NaN si attribut manquant
    public double BestPoints = double.NaN;
    public int BestCategory = -1;
}

public static class StaffEngine
{
    // Ordre des colonnes du fichier lu par le jeu
    public static readonly StaffCategory[] Categories =
    {
        new("gb", "Gardiens", "GoalkeepingCoaching", null, 9, 0),
        new("dtac", "Défense tactique", "Defending", "Tactical"),
        new("dtec", "Défense technique", "Defending", "Technical"),
        new("ptac", "Possession tactique", "Possession", "Tactical"),
        new("ptec", "Possession technique", "Possession", "Technical"),
        new("atac", "Attaque tactique", "Attacking", "Tactical"),
        new("atec", "Attaque technique", "Attacking", "Technical"),
        new("phy", "Physique", "CoachingFitness", null, 9, 0),
        new("cpa", "Coups de pied arrêtés", "SetPieces", "TacticalKnowledge"),
    };

    public static readonly string[] Mental = { "Authority", "Determination", "Motivating" };
    public const int MentalWeight = 2;

    // Attributs nécessaires au calcul
    public static readonly string[] Attributes =
        Categories.SelectMany(c => new[] { c.Main, c.Second }).Where(a => a != null).Concat(Mental).Distinct().ToArray();

    public const double PointsPerStar = 60, FiveStars = 270;

    // Étoiles « continues » (sans arrondi, peuvent dépasser 5 : marge avant de perdre la 5e étoile)
    public static double Stars(double points) => points / PointsPerStar + 0.5;

    // Étoiles telles que le jeu les affiche : demi-étoile inférieure, entre 0,5 et 5
    public static double GameStars(double points) => Math.Clamp(Math.Floor(Stars(points) * 2) / 2, 0.5, 5);

    public static double Points(StaffCategory c, Func<string, double?> attr)
    {
        double mental = 0;
        foreach (var m in Mental)
        {
            if (attr(m) is not double v) return double.NaN;
            mental += v;
        }
        if (attr(c.Main) is not double main) return double.NaN;
        double second = 0;
        if (c.Second != null)
        {
            if (attr(c.Second) is not double s) return double.NaN;
            second = s;
        }
        return main * c.MainWeight + second * c.SecondWeight + mental * MentalWeight;
    }

    public static void Compute(IEnumerable<StaffData> staff)
    {
        foreach (var s in staff)
        {
            s.Points = new double[Categories.Length];
            s.BestPoints = double.NaN;
            s.BestCategory = -1;
            // Présidents, dirigeants, médecins… : tous les attributs de coaching à 0 -> pas d'étoiles (case vide)
            if (Attributes.All(a => !s.Attr.TryGetValue(a, out var v) || v <= 0))
            {
                Array.Fill(s.Points, double.NaN);
                continue;
            }
            for (int i = 0; i < Categories.Length; i++)
            {
                double p = Points(Categories[i], a => s.Attr.TryGetValue(a, out var v) ? v : null);
                s.Points[i] = p;
                if (!double.IsNaN(p) && (double.IsNaN(s.BestPoints) || p > s.BestPoints)) { s.BestPoints = p; s.BestCategory = i; }
            }
        }
    }
}
