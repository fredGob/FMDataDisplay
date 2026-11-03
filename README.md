# FMDataDisplay — notes de performance des joueurs dans Football Manager 27

Plugin BepInEx pour **Football Manager 27** qui lit les statistiques de **tous les joueurs** directement dans le
moteur du jeu, calcule une **note de performance (0-100)** pour chaque joueur et l'affiche dans de **vraies
colonnes du jeu** (effectif, recherche de joueurs), triables comme les autres.

*English: a BepInEx plugin for FM27 that reads every player's stats from the game engine, computes a 0-100
performance rating per position profile, and shows it in sortable in-game columns. Documentation is in French.*

## Quickstart

1. **BepInEx** : télécharger [BepInEx 6 IL2CPP win-x64 (build 791)](https://builds.bepinex.dev/projects/bepinex_be)
   et le dézipper dans le dossier de FM27 (Steam › clic droit sur FM27 › *Gérer* › *Parcourir les fichiers locaux*).
   Linux/Proton : option de lancement `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
2. **Premier lancement** : lancer le jeu une fois jusqu'au menu (long : BepInEx se prépare), puis quitter.
3. **Plugin** : télécharger `FMDataDisplay-0.12.0.zip` dans la
   [dernière release](https://github.com/fredGob/FMDataDisplay/releases/latest) et le dézipper dans le dossier du jeu.
4. **Vues** : copier le contenu du dossier `vues/` du zip (sauf `fmf/`) dans
   `AppData/LocalLow/Sports Interactive/Football Manager 27/` (chemins détaillés [plus bas](#3-ajouter-les-vues-avec-les-colonnes)).
5. **En jeu** : charger une partie, ouvrir l'effectif, attendre le calcul (≈ 3 min, ou appuyer sur **F9**), puis
   choisir la vue **Notes FM27**.

Le détail de chaque étape est dans [Installation](#installation).

## Ce que fait le plugin

- **Colonnes de notes** : `Note` (meilleure note du joueur) et une note par profil : `Note GB`, `Note DC`,
  `Note LAT`, `Note MDC`, `Note MC`, `Note MOC`, `Note AIL`, `Note BT`.
- **Colonnes de staff** : étoiles d'entraînement calculées pour chaque préparateur (`Entr.`, `Ent. GB`, `DéfTac`,
  `DéfTec`, `PosTac`, `PosTec`, `AttTac`, `AttTec`, `Ent. Phy`, `Ent. CPA`), en dixièmes d'étoile (30 = 3 étoiles).
- **Extraction complète de la base** : tous les joueurs ayant joué au moins 1 minute, sans la limite de
  10 000 résultats de la recherche du jeu.
- **Mise à jour automatique** : chaque semaine de jeu (le lundi par défaut), le plugin relit la base et recalcule
  les notes, sans bloquer la partie (≈ 3 min en arrière-plan). **F9** lance le calcul à la main.
- **Fichiers pour le recrutement** : un tableau de tous les joueurs (notes, stats, salaire, valeur, fin de contrat,
  attributs), à ouvrir dans un tableur.

### Comment la note est calculée (en bref)

- 8 profils de poste (GB, DC, LAT, MDC, MC, MOC, AIL, BT), chacun avec ses statistiques et leurs poids
  (stats par 90 minutes : passes progressives, duels, tirs, possessions gagnées/perdues…).
- Chaque stat est comparée aux joueurs du même championnat, puis corrigée par le **niveau du championnat**
  (d'après sa réputation dans le jeu) et par le **style de l'équipe** (une équipe qui a le ballon gonfle certaines stats).
- Joueurs de moins de **200 minutes** : note 0 (pas assez de données).
- Football masculin et féminin notés séparément.
- La note ne dépend que des statistiques réelles des matchs, pas des attributs ni du niveau actuel (CA).

Le code du calcul est dans [`Note/NoteEngine.cs`](Note/NoteEngine.cs).

## Prérequis

- **Football Manager 27** (Steam, Windows ou Linux avec Proton).
- **BepInEx 6** version IL2CPP (testé avec **6.0.0-be.791**).

## Installation

### 1. Installer BepInEx dans FM27

1. Télécharger **BepInEx 6 Bleeding Edge, Unity IL2CPP, Windows x64** (build 791 ou plus récente) :
   <https://builds.bepinex.dev/projects/bepinex_be>
   (fichier `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791+….zip`).
2. Ouvrir le dossier du jeu : sur Steam, clic droit sur FM27 › *Gérer* › *Parcourir les fichiers locaux*.
3. Dézipper le contenu de l'archive **dans ce dossier**, à côté de `fm.exe` :
   ```
   Football Manager 27/
   ├── BepInEx/
   ├── dotnet/
   ├── doorstop_config.ini
   ├── winhttp.dll
   ├── fm.exe
   └── ...
   ```
4. **Linux / Steam Deck (Proton) uniquement** : dans Steam, clic droit sur FM27 › *Propriétés* ›
   *Options de lancement*, mettre :
   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```
5. Lancer le jeu une première fois, attendre le menu principal, puis quitter. Le premier lancement est
   **long** (plusieurs minutes) : BepInEx prépare ses fichiers.
   Si `BepInEx/LogOutput.log` existe après ce lancement, BepInEx fonctionne.

### 2. Installer le plugin (sans compiler)

1. Télécharger **`FMDataDisplay-0.12.0.zip`** dans la
   [dernière release](https://github.com/fredGob/FMDataDisplay/releases/latest).
2. Le dézipper **dans le dossier du jeu** (le même que pour BepInEx). Il contient :
   ```
   BepInEx/plugins/FM27Explorer.dll        ← le plugin
   BepInEx/config/fm27explorer_colonne.txt ← les colonnes (indispensable)
   BepInEx/config/fm27explorer_extraction.txt
   BepInEx/config/fm27explorer_notes.txt
   vues/                                   ← à copier à l'étape 3
   ```

| Réglage | Rôle |
|---|---|
| `fm27explorer_colonne.txt` | Déclare les colonnes (titre, infobulle, profil affiché). **Indispensable** pour voir les colonnes. |
| `fm27explorer_extraction.txt` | Propriétés lues en plus pendant l'extraction (attributs, salaire, valeur…). |
| `fm27explorer_notes.txt` | Calcul automatique : `auto`, jour de la semaine, nombre d'extractions gardées, staff. |

La DLL de la release est compilée pour la version actuelle de FM27 : après une mise à jour du jeu, il peut
falloir la recompiler (voir plus bas).

### 3. Ajouter les vues avec les colonnes

Les colonnes du plugin ne figurent pas dans le menu « insérer une colonne » du jeu : on les affiche avec des
**vues** toutes prêtes, dans le dossier `vues/` (du zip ou du dépôt). Copier chaque sous-dossier dans le dossier de
préférences du jeu (fusionner avec les dossiers existants) :

- Windows : `C:\Users\<vous>\AppData\LocalLow\Sports Interactive\Football Manager 27\`
- Linux (Proton) : `~/.local/share/Steam/steamapps/compatdata/4530370/pfx/drive_c/users/steamuser/AppData/LocalLow/Sports Interactive/Football Manager 27/`

| Sous-dossier | Vue | Écran |
|---|---|---|
| `FirstTeamSquadAndYouthTeamSquadCollection/` | **Notes FM27** | Effectif |
| `PlayerSearchResultsViewCollection/` | **Notes FM27** | Recherche de joueurs |
| `NonPlayerSearchResultsViewCollection/` | **CalculAuto** | Recherche de staff |

Le jeu les relit au démarrage. Autre possibilité : importer les fichiers `.fmf` de `vues/fmf/` depuis le menu
des vues du jeu (*Importer une vue*).

### Compiler soi-même (facultatif)

Il faut le **SDK .NET** (8 ou plus récent) et avoir lancé le jeu une fois avec BepInEx (dossier `BepInEx/interop`).

```bash
git clone https://github.com/fredGob/FMDataDisplay.git
cd FMDataDisplay/Explorer
dotnet build -c Release
```

La compilation copie `FM27Explorer.dll` dans `BepInEx/plugins/` du jeu. Le chemin du jeu par défaut est celui de
Steam sous Linux (`~/.local/share/Steam/steamapps/common/Football Manager 27`). Ailleurs, l'indiquer :

```bash
# Windows
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Football Manager 27"
```

Copier ensuite les fichiers de [`config/`](config/) dans `BepInEx/config/` du jeu.

## Utilisation

1. Lancer le jeu et charger une partie.
2. Aller sur un écran **qui affiche des joueurs** (effectif, recherche, fiche joueur) : le plugin s'en sert pour
   interroger le moteur du jeu.
3. Le premier calcul part tout seul au chargement (ou appuyer sur **F9**). Il dure environ 3 minutes,
   la partie reste utilisable pendant ce temps. Un second appui sur F9 l'arrête.
4. Choisir la vue **Notes FM27** dans l'effectif ou la recherche de joueurs : les colonnes de notes sont remplies
   et se trient d'un clic.
5. Ensuite, les notes se mettent à jour seules **chaque lundi de jeu** (réglable dans `fm27explorer_notes.txt`).

**F9 est le seul raccourci du plugin.** Les touches de développement (F2–F8, F10, F11) ne sont actives qu'avec
`touches_dev=1` dans `fm27explorer_notes.txt`.

### Fichiers produits

Dans `Documents/Sports Interactive/Football Manager 27/exports/` :

| Fichier | Contenu |
|---|---|
| `FM27_Recrutement.tsv` | Un joueur par ligne : nom, âge, club, championnat, minutes, notes par profil, salaire, valeur, fin de contrat, attributs. Réécrit à chaque calcul. |
| `FM27_Recrutement_staff.tsv` | Étoiles d'entraînement calculées pour le staff. |
| `FM27_Joueurs_<date>.tsv`, `FM27_Clubs_…`, `FM27_Competitions_…` | Extraction brute (les 12 dernières sont gardées). |

Et dans `BepInEx/config/` : `fm27_notes.tsv` et `fm27_staff.tsv` (valeurs affichées dans les colonnes).

Les fichiers `.tsv` s'ouvrent dans un tableur (séparateur : tabulation).

## Limites connues

- **Début de saison** : peu de minutes jouées, donc notes peu fiables et beaucoup de joueurs à 0.
- **Attributs masqués** : dans une vraie partie, les attributs mal connus de vos recruteurs sont lus en fourchette
  (ils ne servent pas à la note, seulement au fichier de recrutement).
- **Gardiens** : note moins fiable que pour les joueurs de champ.
- La recherche du jeu reste plafonnée à 10 000 résultats : resserrer les critères pour voir tous les joueurs voulus.
  Le filtre du plugin (`filtre_min=40` dans `fm27explorer_colonne.txt`) retire des grandes listes les joueurs
  notés sous 40 ; mettre `filtre_min=0` pour le désactiver.
- Projet en cours de développement : une mise à jour du jeu peut casser le plugin.

## Contenu du dépôt

| Dossier | Contenu |
|---|---|
| [`Explorer/`](Explorer/) | Le plugin BepInEx (`FM27Explorer.dll`) : extraction, calcul automatique, colonnes. |
| [`Note/`](Note/) | Moteur de note (`NoteEngine.cs`, `StaffEngine.cs`…), compilé avec le plugin. |
| [`config/`](config/) | Réglages à copier dans `BepInEx/config/`. |
| [`vues/`](vues/) | Vues du jeu avec les colonnes du plugin. |

## Dépannage

- **Rien ne se passe** : vérifier `BepInEx/LogOutput.log` ; le plugin y écrit « FM27 Explorer … chargé ».
  Sous Linux, vérifier l'option de lancement `WINEDLLOVERRIDES`.
- **Colonnes vides** : faire un calcul (F9) depuis un écran avec des joueurs, et vérifier que
  `fm27explorer_colonne.txt` contient `actif=1`.
- **« F9 : il faut d'abord afficher un écran avec des joueurs »** : ouvrir l'effectif ou une recherche, puis F9.
- **Erreur de compilation sur `BepInEx/interop`** : lancer le jeu une fois avec BepInEx pour générer ce dossier,
  ou corriger `GameDir`.
- **Le jeu plante ou les colonnes restent vides après une mise à jour de FM27** : recompiler le plugin.
