// Colonnes « Note » ajoutées aux tableaux du jeu (une par profil si on veut).
//
// Les tableaux de FM27 (SI.Bindable.StreamedTable) tirent leurs colonnes d'un catalogue (TableTemplate,
// chargé depuis les bundles) : une entrée RowData par colonne possible (identifiant, titre, modèle de
// cellule, liaison vers une propriété, tri). Une vue = la liste des colonnes affichées.
//
//   1. Inventaire : à chaque catalogue chargé, ses colonnes sont écrites dans exports/FM27_Tableau_<nom>.tsv.
//   2. Ajout : chaque colonne déclarée dans BepInEx/config/fm27explorer_colonne.txt est une copie d'une
//      colonne « modèle » du jeu (nombre entier, tri par l'interface) : même cellule, même largeur.
//      Mode « propriétés » (proprietes=1, par défaut depuis 0.12.0) : la colonne est liée à une propriété à nous
//      (binding.fm27note…), déclarée au jeu au démarrage. Quand une cellule la demande, on demande au moteur la
//      propriété du modèle à la place (il ne connaît pas la nôtre) et seules ces cellules reçoivent nos notes :
//      la vraie colonne modèle n'est plus touchée ailleurs dans le jeu.
//      Ancien mode (proprietes=0) : liaisons du modèle telles quelles ; toutes les valeurs de la propriété modèle
//      sont remplacées (y compris dans la vraie colonne, sur les fiches…).
//   3. Affichage : les valeurs envoyées à nos cellules sont remplacées par la note de la personne (champ choisi
//      dans BepInEx/config/fm27_notes.tsv pour les joueurs, fm27_staff.tsv pour les étoiles d'entraînement des
//      préparateurs ; les deux fichiers sont écrits par le calcul après F9). Titre et infobulle remplacés aussi.
//   4. Tri : sur nos colonnes, chaque valeur de tri est remplacée par la note du joueur de la ligne.
//   5. Filtre : dans les longues listes (base de données), les joueurs sous un seuil de note sont retirés.
//   6. Menu « insérer une colonne » : catégorie avec nos colonnes.
//
// Touches : F10 relit les fichiers ; F11 ajoute toutes nos colonnes au tableau affiché ; Maj/Ctrl+F11 : seuil ±5.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Bindable.Reference.Core;
using SI.Interop;
using TypedValue = SI.Core.TypedValue;

namespace FM27Explorer;

internal static class Colonne
{
    private static string ConfigPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27explorer_colonne.txt");
    private static string NotesPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27_notes.tsv");
    private static string StaffPath => Path.Combine(BepInEx.Paths.ConfigPath, "fm27_staff.tsv");

    // Une colonne à nous
    private sealed class ColDef
    {
        public string Id, Title, Field, Source, Hint;
        public int FieldIndex = -1;          // colonne du fichier de notes
        public string Property;              // propriété du modèle (déduite de sa liaison) : demandée au moteur
        public uint PropertyId;
        public string SortPath;              // chemin de tri local du modèle (minuscules)
        public string OwnName;               // notre propriété : la colonne est liée à binding.<OwnName>
        public uint OwnId;                   // son identifiant (déclaré au jeu par EnsureProperties)
        public bool OwnBound;                // la liaison de la colonne pointe bien sur notre propriété
    }

    private static readonly List<ColDef> Defs = new();
    private static readonly Dictionary<uint, ColDef> ByProperty = new();   // ancien mode : propriété du modèle -> colonne
    private static bool _active, _propertiesResolved;

    // Mode « propriétés » : nos propriétés déclarées au jeu (nom -> identifiant, gardé d'un rechargement à l'autre)
    private static bool _own = true, _ownPending;
    private static readonly Dictionary<string, uint> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, ColDef> ByOwnId = new();
    private static Dictionary<string, uint> _propertyIds;                   // nom -> identifiant (propriétés du jeu)
    private static int _ownOpened, _ownNodes, _ownBatchFixed, _hintsReplaced;
    public static int AcceptHits;
    public static bool OwnMode => _own;
    private const uint OwnPrefix = 0x4637;                                  // nos identifiants : 'F7xx'
    private static string _category = "Notes FM27";
    private static string _filterField = "note";
    private static int _minNote;                 // filtre : notes inférieures retirées (0 = pas de filtre)
    private static int _minRows = 300;           // filtre seulement les listes plus longues (base de données, pas l'effectif)

    // Notes : dref -> valeurs (NaN = pas de note pour ce champ)
    private static readonly Dictionary<int, float[]> Notes = new();
    private static string[] _fields = { "note" };

    // Affichage : clé de canal de l'interface -> (dref, colonne)
    private static readonly Dictionary<ulong, (int dref, ColDef def)> KeyToCell = new();
    private static int _replaced;

    private static readonly HashSet<string> Dumped = new();

    // ── Réglages ───────────────────────────────────────────────────────────

    public static void Reload()
    {
        _active = false; _minNote = 0; _minRows = 300; _category = "Notes FM27"; _filterField = "note"; _own = true;
        Defs.Clear();
        string legacySource = null, legacyId = "fm27-note", legacyTitle = "Note";
        try
        {
            if (File.Exists(ConfigPath))
            {
                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "actif": _active = v == "1"; break;
                        case "colonne":   // id|titre|champ du fichier de notes|colonne modèle[|infobulle]
                            var p = v.Split('|').Select(x => x.Trim()).ToArray();
                            if (p.Length >= 4)
                                Defs.Add(new ColDef { Id = p[0], Title = p[1], Field = p[2], Source = p[3], Hint = p.Length >= 5 && p[4].Length > 0 ? p[4] : null });
                            break;
                        case "proprietes": _own = v != "0"; break;
                        case "categorie": _category = v; break;
                        case "filtre_min": int.TryParse(v, out _minNote); break;
                        case "filtre_lignes_min": int.TryParse(v, out _minRows); break;
                        case "filtre_champ": _filterField = v; break;
                        // ancien format (une seule colonne)
                        case "source": legacySource = v; break;
                        case "id": legacyId = v; break;
                        case "titre": legacyTitle = v; break;
                    }
                }
            }
            if (Defs.Count == 0 && legacySource != null)
                Defs.Add(new ColDef { Id = legacyId, Title = legacyTitle, Field = "note", Source = legacySource });

            LoadNotes();
            foreach (var d in Defs)
                d.FieldIndex = Array.FindIndex(_fields, f => string.Equals(f, d.Field, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) { Plugin.L.LogError("Colonne : lecture des réglages : " + e.Message); }

        // Nos propriétés : identifiants déjà déclarés retrouvés tout de suite (pas de trou entre deux rechargements)
        ByOwnId.Clear();
        foreach (var d in Defs)
        {
            d.OwnName = OwnNameFor(d.Id);
            if (Registered.TryGetValue(d.OwnName, out var id)) { d.OwnId = id; ByOwnId[id] = d; }
        }
        _ownPending = _own && Defs.Any(d => d.OwnId == 0);

        // Cellules déjà affichées : rattachées aux nouvelles définitions (sinon elles garderaient les valeurs du moteur)
        foreach (var k in KeyToCell.Keys.ToList())
        {
            var nd = Defs.FirstOrDefault(d => d.Id == KeyToCell[k].def.Id);
            if (nd == null) KeyToCell.Remove(k); else KeyToCell[k] = (KeyToCell[k].dref, nd);
        }
        FirstCells.Clear();   // diagnostic des premières cellules refait après F10
        _propertiesResolved = false;
        List<TableTemplate> templates;
        lock (Templates) templates = Templates.ToList();
        foreach (var t in templates)
        {
            try { Inject(t, t.name); } catch (Exception e) { Plugin.L.LogWarning("Colonne : réapplication : " + e.Message); }
        }
        Plugin.L.LogMessage($"Colonne : actif={_active}, mode {(_own ? "propriétés à nous" : "ancien (porteuses)")}, {Defs.Count} colonne(s) [{string.Join(", ", Defs.Select(d => $"{d.Title}={d.Field}{(d.FieldIndex < 0 ? "(champ absent !)" : "")}<{d.Source}"))}], " +
                            $"filtre {_filterField} ≥ {_minNote} (listes > {_minRows} lignes), {Notes.Count} personnes notées, champs : {string.Join(",", _fields)}.");
    }

    // fm27-note-gk -> fm27notegk (lettres et chiffres seulement : le chemin de liaison est découpé sur les points)
    private static string OwnNameFor(string id)
    {
        var s = new string((id ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return s.StartsWith("fm27") ? s : "fm27" + s;
    }

    private static string FourCC(uint id) =>
        new string(new[] { (char)(id >> 24), (char)((id >> 16) & 0xFF), (char)((id >> 8) & 0xFF), (char)(id & 0xFF) });

    // ── Mode « propriétés » ───────────────────────────────────────────────

    // Appelé à chaque image (fil principal) tant que nos propriétés ne sont pas déclarées au jeu.
    public static void EnsureProperties()
    {
        if (!_ownPending) return;
        PropertyIdentifierSet set;
        Il2CppSystem.Collections.Generic.Dictionary<uint, PropertyIDInfo> map;
        try { set = PropertyIdentifierSet.Instance; map = set?.m_idToInfo; }
        catch { return; }
        if (map == null || map.Count < 1000) return;   // le jeu n'a pas encore déclaré les siennes
        _ownPending = false;
        var log = new List<string>();
        for (int i = 0; i < Defs.Count; i++)
        {
            var d = Defs[i];
            if (d.OwnId != 0) continue;
            if (Registered.TryGetValue(d.OwnName, out var known)) { d.OwnId = known; ByOwnId[known] = d; continue; }
            uint id = ((uint)'F' << 24) | ((uint)'7' << 16) | ((uint)('a' + i / 26) << 8) | (uint)('a' + i % 26);
            while (map.ContainsKey(id)) id++;
            try { set.Register(new PropertyID { id = id }, d.OwnName, "Person", null, d.Title, d.Hint, null, 0); }
            catch (Exception e) { Plugin.L.LogWarning($"Colonne : déclaration de {d.OwnName} : {e.Message}"); continue; }
            if (!map.ContainsKey(id)) { Plugin.L.LogWarning($"Colonne : {d.OwnName} non déclarée (identifiant {FourCC(id)} absent ensuite)."); continue; }
            Registered[d.OwnName] = id;
            d.OwnId = id;
            ByOwnId[id] = d;
            log.Add($"{d.OwnName}='{FourCC(id)}'");
        }
        Plugin.L.LogMessage($"Colonne : {log.Count} propriété(s) à nous déclarée(s) au jeu ({map.Count} propriétés en tout) : {string.Join(", ", log)}.");

        // Diagnostic : ce que le jeu répond pour notre propriété et pour une propriété ordinaire
        try
        {
            var d0 = Defs.FirstOrDefault(d => d.OwnId != 0);
            if (d0 == null) return;
            uint carrier = CarrierId(d0);
            string Kind(uint p)
            {
                try { var k = FM.UI.PersonReference.GetPropertyTypeInternal(p); return $"{k.Kind}/{(k.Type == null ? "-" : k.Type.Name)}{(k.IsList ? "[]" : "")}"; }
                catch (Exception e) { return "erreur " + e.Message; }
            }
            bool accOwn = FM.UI.PersonReference.AcceptsPropertyInternal(d0.OwnId), accCarrier = FM.UI.PersonReference.AcceptsPropertyInternal(carrier);
            string name = "?";
            try { name = set.GetInfo(d0.OwnId)?.Name ?? "(aucune)"; } catch { }
            Plugin.L.LogMessage($"Colonne : diagnostic {d0.OwnName} : nom relu « {name} », accepté par une personne = {accOwn} (vrai grâce à notre crochet), " +
                                $"type = {Kind(d0.OwnId)} ; propriété modèle {Names.Property(carrier)} : acceptée = {accCarrier}, type = {Kind(carrier)}.");
        }
        catch (Exception e) { Plugin.L.LogWarning("Colonne : diagnostic des propriétés : " + e.Message); }
    }

    public static bool IsOwn(uint property) => (property >> 16) == OwnPrefix && ByOwnId.ContainsKey(property);

    // Propriété demandée au moteur à la place de la nôtre : celle du modèle (repli : l'âge)
    private static uint CarrierId(ColDef d)
    {
        if (d.PropertyId != 0) return d.PropertyId;
        try
        {
            if (_propertyIds == null)
            {
                var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in PropertyIdentifierSet.Instance.m_idToInfo)
                    if (kv.Value?.Name is string n && !byName.ContainsKey(n)) byName[n] = kv.Key;
                _propertyIds = byName;
            }
            if (d.Property != null && _propertyIds.TryGetValue(d.Property, out var id)) return d.PropertyId = id;
            if (_propertyIds.TryGetValue("Age", out var age)) return age;   // modèle pas encore vu : valeur remplacée de toute façon
        }
        catch (Exception e) { Counters.Note("Colonne.CarrierId", e); }
        return 0x31352020;   // 'Age' (identifiant relevé dans le jeu)
    }

    // Nœud déclaré au moteur avec notre propriété (AddNode) : on lui donne la propriété du modèle
    public static void SubstituteNode(ref uint property)
    {
        if ((property >> 16) != OwnPrefix || !ByOwnId.TryGetValue(property, out var d)) return;
        property = CarrierId(d);
        _ownNodes++;
    }

    // Lot de canaux envoyé directement au moteur (sans passer par OpenChannel) qui contient une de nos propriétés :
    // propriété du modèle à la place, et cellule suivie comme les autres (vu le 3 nov. : Note GB, en lot)
    public static unsafe void SubstituteBatch(InteropReference reference, Il2CppSystem.Collections.Generic.List<FM.GamePlugin.ChannelData> channels)
    {
        if (ByOwnId.Count == 0 || channels == null) return;
        int dref = -1;
        for (int i = 0; i < channels.Count; i++)
        {
            var c = channels[i];
            if ((c.property >> 16) != OwnPrefix || !ByOwnId.TryGetValue(c.property, out var d)) continue;
            *(uint*)&c = CarrierId(d);   // champ « property » (en lecture seule dans l'interop), à l'offset 0
            channels[i] = c;
            if (dref < 0) dref = DrefOfReference(reference);
            if (_active && dref > 0) KeyToCell[c.key] = (dref, d);
            if (_ownBatchFixed++ < 3)
                Plugin.L.LogInfo($"Colonne : {d.OwnName} dans un lot envoyé au moteur (corrigé), personne {(dref > 0 ? dref - Probe.PersonDrefBase : -1)}.");
        }
    }

    private static int DrefOfReference(InteropReference reference)
    {
        if (reference is null) return 0;
        try
        {
            var ids = reference.m_id;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i].m_id == Probe.KeyDref) return ids[i].m_value;
        }
        catch { }
        return 0;
    }

    // Liaisons du modèle avec notre propriété à la place de la sienne.
    // NB : on ne passe ni par le setter RemapInfo.to ni par List<RemapInfo>.Add (Il2CppInterop) : le premier copie
    // le chemin sans prévenir le ramasse-miettes du jeu, le second transmet mal une structure non « blittable »
    // (cause probable de l'échec du 2 nov. : la cellule affichait « FM.UI.PersonReference »). La liste est copiée par
    // le jeu lui-même, puis le texte du chemin est écrit directement dans son tableau, avec la barrière d'écriture.
    private static readonly List<IntPtr> Pinned = new();

    // Chemin de tri local d'une colonne (RowData.m_sortBinding.m_path), écrit avec la barrière d'écriture ; null si réussi
    private static unsafe string SetSortPath(TableTemplate.RowData row, string path)
    {
        IntPtr rowPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(row);
        IntPtr sortField = IL2CPP.GetIl2CppField(IL2CPP.il2cpp_object_get_class(rowPtr), "m_sortBinding");
        IntPtr pathField = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<BindingPath>.NativeClassPtr, "m_path");
        if (sortField == IntPtr.Zero || pathField == IntPtr.Zero) return "champs internes introuvables";
        // RowData est une classe (décalage compté depuis l'objet) ; BindingPath une structure (décalage compté depuis l'objet emballé)
        int offset = (int)IL2CPP.il2cpp_field_get_offset(sortField) + (int)IL2CPP.il2cpp_field_get_offset(pathField) - 2 * IntPtr.Size;
        IntPtr str = IL2CPP.ManagedStringToIl2Cpp(path);
        lock (Pinned) Pinned.Add(IL2CPP.il2cpp_gchandle_new(str, false));
        IL2CPP.il2cpp_gc_wbarrier_set_field(rowPtr, (IntPtr)((byte*)rowPtr + offset), str);
        string back = Try(() => row.m_sortBinding.Path);
        return string.Equals(back, path, StringComparison.Ordinal) ? null : $"chemin relu « {back} »";
    }

    private static unsafe Il2CppSystem.Collections.Generic.List<RemapInfo> OwnBindings(TableTemplate.RowData src, ColDef d, out string why)
    {
        why = null;
        var orig = src.m_valueBindings;
        if (orig == null || orig.Count == 0) { why = "modèle sans liaison"; return null; }
        if (string.IsNullOrEmpty(d.Property)) { why = "propriété du modèle inconnue"; return null; }
        string target = "binding." + d.Property, mine = "binding." + d.OwnName;
        int index = -1;
        for (int i = 0; i < orig.Count; i++)
            if (string.Equals(Try(() => orig[i].to.Path), target, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        if (index < 0) { why = $"liaison {target} introuvable"; return null; }

        var list = orig.GetRange(0, orig.Count);   // copie faite par le jeu (structures recopiées proprement)
        if (list == null || list.Count != orig.Count) { why = "copie de la liste incomplète"; return null; }

        IntPtr listPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(list);
        IntPtr listClass = IL2CPP.il2cpp_object_get_class(listPtr);
        IntPtr itemsField = IL2CPP.GetIl2CppField(listClass, "_items");
        IntPtr remapClass = Il2CppClassPointerStore<RemapInfo>.NativeClassPtr;
        IntPtr toField = IL2CPP.GetIl2CppField(remapClass, "to");
        IntPtr pathField = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<BindingPath>.NativeClassPtr, "m_path");
        if (itemsField == IntPtr.Zero || toField == IntPtr.Zero || pathField == IntPtr.Zero) { why = "champs internes introuvables"; return null; }
        IntPtr arr = *(IntPtr*)((byte*)listPtr + IL2CPP.il2cpp_field_get_offset(itemsField));
        if (arr == IntPtr.Zero) { why = "tableau interne vide"; return null; }
        uint align = 0;
        int elemSize = IL2CPP.il2cpp_class_value_size(remapClass, ref align);
        int arrElemSize = IL2CPP.il2cpp_array_element_size(IL2CPP.il2cpp_object_get_class(arr));
        if (elemSize <= 0 || elemSize != arrElemSize) { why = $"taille d'élément incohérente ({elemSize} / {arrElemSize})"; return null; }
        int header = 2 * IntPtr.Size;   // décalages des champs d'une structure : comptés depuis l'objet « emballé »
        int offset = (int)IL2CPP.il2cpp_field_get_offset(toField) - header + (int)IL2CPP.il2cpp_field_get_offset(pathField) - header;
        if (offset < 0 || offset + IntPtr.Size > elemSize) { why = $"décalage incohérent ({offset})"; return null; }

        byte* slot = (byte*)arr + 4 * IntPtr.Size + index * elemSize + offset;
        IntPtr str = IL2CPP.ManagedStringToIl2Cpp(mine);
        lock (Pinned) { Pinned.Add(IL2CPP.il2cpp_gchandle_new(str, false)); Pinned.Add(IL2CPP.il2cpp_gchandle_new(listPtr, false)); }
        IL2CPP.il2cpp_gc_wbarrier_set_field(arr, (IntPtr)slot, str);

        // Vérification : relu par le jeu
        string back = Try(() => list[index].to.Path);
        if (!string.Equals(back, mine, StringComparison.Ordinal)) { why = $"chemin relu « {back} »"; return null; }
        return list;
    }

    // Notes des joueurs puis étoiles des préparateurs : les champs des deux fichiers sont mis bout à bout
    private static void LoadNotes()
    {
        Notes.Clear();
        var files = new List<(string[] fields, List<(int idx, string[] cells)> rows)>();
        foreach (var path in new[] { NotesPath, StaffPath })
        {
            if (!File.Exists(path)) continue;
            string[] fields = null;
            var rows = new List<(int, string[])>();
            foreach (var line in File.ReadLines(path))
            {
                var c = line.Split('\t');
                if (fields == null)
                {
                    fields = c.Length > 0 && c[0] == "index" ? c.Skip(1).ToArray() : new[] { "note" };   // ancien format : index, note
                    if (c[0] == "index") continue;
                }
                if (c.Length >= 2 && int.TryParse(c[0], out var idx)) rows.Add((idx, c));
            }
            if (fields != null) files.Add((fields, rows));
        }
        _fields = files.Count > 0 ? files.SelectMany(f => f.fields).ToArray() : new[] { "note" };
        int offset = 0;
        foreach (var (fields, rows) in files)
        {
            foreach (var (idx, c) in rows)
            {
                int dref = Probe.PersonDrefBase + idx;
                if (!Notes.TryGetValue(dref, out var vals))
                {
                    vals = new float[_fields.Length];
                    Array.Fill(vals, float.NaN);
                    Notes[dref] = vals;
                }
                for (int i = 0; i < fields.Length; i++)
                    if (i + 1 < c.Length && float.TryParse(c[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) vals[offset + i] = f;
            }
            offset += fields.Length;
        }
    }

    private static float NoteOf(int dref, int fieldIndex) =>
        fieldIndex >= 0 && Notes.TryGetValue(dref, out var v) && fieldIndex < v.Length ? v[fieldIndex] : float.NaN;

    // Appelé à chaque lot de valeurs reçu (fil principal) : y a-t-il des cellules à nous à remplir ?
    public static bool Displaying => _active && KeyToCell.Count > 0;

    // Ancien mode, ou colonne dont la liaison n'a pas pu être changée : propriété du modèle -> colonne
    // (toutes les valeurs de cette propriété sont alors remplacées, y compris hors de nos colonnes)
    private static void ResolveCarriers()
    {
        if (_propertiesResolved) return;
        _propertiesResolved = true;
        ByProperty.Clear();
        try
        {
            foreach (var d in Defs)
            {
                if (_own && d.OwnBound) continue;
                uint id = d.Property != null ? CarrierId(d) : 0;
                if (id != 0 && d.PropertyId == id) ByProperty[id] = d;
            }
            if (ByProperty.Count > 0)
                Plugin.L.LogMessage((_own ? "Colonne : ⚠ colonnes restées sur l'ancien mode (porteuses) : " : "Colonne : porteuses ") +
                                    string.Join(", ", ByProperty.Values.Select(d => $"{d.Title} -> {d.Property} ({d.PropertyId})")));
        }
        catch (Exception e) { Plugin.L.LogWarning("Colonne : propriétés : " + e.Message); }
    }

    // ── 1 + 2 : catalogue chargé ──────────────────────────────────────────

    private static readonly List<TableTemplate> Templates = new();

    public static void OnTemplate(StreamedTable table, TableTemplate tpl, string where)
    {
        if (tpl == null) return;
        lock (Templates)
        {
            bool known = false;
            foreach (var t in Templates) if (t.Pointer == tpl.Pointer) { known = true; break; }
            if (!known) Templates.Add(tpl);
        }
        // Le nom Unity n'est lisible que sur le fil principal : à défaut, on nomme d'après les colonnes
        string name = null;
        try { name = tpl.name; } catch { }
        if (string.IsNullOrEmpty(name))
        {
            try
            {
                var rows = tpl.m_rowData;
                name = rows != null && rows.Length > 0 ? $"{rows.Length}col_{rows[0]?.m_id}" : "vide";
            }
            catch { name = "inconnu"; }
        }
        try { Dump(tpl, name, table); } catch (Exception e) { Counters.Note("Colonne.Dump", e); }
        try { Inject(tpl, name); } catch (Exception e) { Plugin.L.LogError($"Colonne : ajout dans « {name} » ({where}) : {e}"); }
    }

    private static void Dump(TableTemplate tpl, string name, StreamedTable table)
    {
        lock (Dumped) { if (!Dumped.Add(name)) return; }
        var rows = tpl.m_rowData;
        var path = Path.Combine(Plugin.ExportDir, $"FM27_Tableau_{Safe(name)}.tsv");
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        string tableName = "";
        try { tableName = table?.name ?? ""; } catch { }
        w.WriteLine($"# catalogue « {name} », tableau « {tableName} », {rows?.Length ?? 0} colonnes possibles");
        try
        {
            var pars = tpl.m_parameters;
            if (pars != null)
                for (int i = 0; i < pars.Count; i++) w.WriteLine($"# paramètre : {pars[i].name}");
        }
        catch { }
        w.WriteLine("id\ttitre_cle\ttitre\tliaison_titre\ttri_local\ttri_moteur\ttri_croissant\tlargeur\tclasse\tliaisons_valeurs\tmodele_cellule");
        if (rows == null) return;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r == null) continue;
            string tKey = Try(() => r.m_translationID.Text), tText = Try(() => r.m_translationID.TranslatedString);
            string label = Try(() => r.m_headerLabelBinding?.Path), sort = Try(() => r.m_sortBinding?.Path);
            uint plugin = 0; try { plugin = r.m_sortBindingPlugin; } catch { }
            string uxml = Try(() => r._Uxml_k__BackingField?.name);
            if (string.IsNullOrEmpty(uxml)) uxml = Try(() => r.m_uxmlRef?.AssetGUID);
            w.WriteLine(string.Join("\t", r.m_id, tKey, tText, label, sort,
                plugin == 0 ? "" : $"{plugin} ({Names.Property(plugin)})", r.m_sortAscending,
                r.m_defaultWidth.ToString(CultureInfo.InvariantCulture), r.m_cellClass, Bindings(r).Replace('\t', ' '), uxml));
        }
        Plugin.L.LogMessage($"Colonne : catalogue « {name} » ({rows.Length} colonnes) -> {path}");
    }

    // Ajoute (ou met à jour) nos colonnes dans un catalogue qui contient leurs colonnes porteuses.
    // NB : les liaisons sont réutilisées telles quelles (écrire un BindingPath dans une RemapInfo neuve perd
    // le chemin : la cellule affiche alors « FM.UI.PersonReference »).
    private static void Inject(TableTemplate tpl, string name)
    {
        if (!_active || Defs.Count == 0) return;
        var old = tpl.m_rowData;
        if (old == null) return;
        var byId = new Dictionary<string, TableTemplate.RowData>();
        for (int i = 0; i < old.Length; i++)
            if (old[i]?.m_id is string id && !byId.ContainsKey(id)) byId[id] = old[i];

        var added = new List<TableTemplate.RowData>();
        var log = new List<string>();
        foreach (var d in Defs)
        {
            if (!byId.TryGetValue(d.Source, out var src)) continue;
            d.SortPath = Try(() => src.m_sortBinding?.Path).ToLowerInvariant();
            var to = Try(() => src.m_valueBindings[0].to?.Path);
            d.Property = to.StartsWith("binding.", StringComparison.OrdinalIgnoreCase) ? to.Substring(8) : null;

            if (byId.TryGetValue(d.Id, out var mine))
            {
                Copy(src, mine);
                log.Add($"{d.Title} (mise à jour){Bind(src, mine, d)}");
                continue;
            }
            var row = new TableTemplate.RowData();
            row.m_id = d.Id;
            Copy(src, row);
            added.Add(row);
            byId[d.Id] = row;
            log.Add($"{d.Title} <- {d.Source} ({d.Property}){Bind(src, row, d)}");
        }
        if (log.Count == 0) return;
        _propertiesResolved = false;

        if (added.Count > 0)
        {
            var arr = new Il2CppReferenceArray<TableTemplate.RowData>(old.Length + added.Count);
            for (int i = 0; i < old.Length; i++) arr[i] = old[i];
            for (int i = 0; i < added.Count; i++) arr[old.Length + i] = added[i];
            tpl.m_rowData = arr;
            var lookup = tpl.m_rowDataLookup;      // peut ne pas être encore construit
            if (lookup != null)
                foreach (var r in added) if (!lookup.ContainsKey(r.m_id)) lookup.Add(r.m_id, r);
        }
        Plugin.L.LogMessage($"Colonne : catalogue « {name} » : {string.Join(", ", log)}.");
    }

    // Mode « propriétés » : la colonne est liée à notre propriété au lieu de celle du modèle
    private static string Bind(TableTemplate.RowData src, TableTemplate.RowData row, ColDef d)
    {
        if (!_own) return "";
        string why;
        Il2CppSystem.Collections.Generic.List<RemapInfo> own;
        try { own = OwnBindings(src, d, out why); }
        catch (Exception e) { own = null; why = e.Message; }
        if (own != null)
        {
            row.m_valueBindings = own;
            d.OwnBound = true;
            // Tri : le jeu demande, ligne par ligne, la valeur du chemin de tri de la colonne. Avec le chemin du modèle,
            // il triait sur les vraies valeurs du modèle (« Buts Jnr », presque toutes à 0 : tri sans effet, 3 nov.).
            // Avec notre propriété, ces demandes passent par OpenChannel comme les cellules et reçoivent nos notes.
            string sortWhy;
            try { sortWhy = SetSortPath(row, "binding." + d.OwnName); } catch (Exception e) { sortWhy = e.Message; }
            if (sortWhy == null) { d.SortPath = ("binding." + d.OwnName).ToLowerInvariant(); return $" -> binding.{d.OwnName} (tri compris)"; }
            return $" -> binding.{d.OwnName} (⚠ tri resté sur le modèle : {sortWhy})";
        }
        return $" (⚠ liaison du modèle gardée : {why})";
    }

    private static void Copy(TableTemplate.RowData src, TableTemplate.RowData row)
    {
        row.m_headerLabelBinding = src.m_headerLabelBinding;
        row.m_hintLabelBinding = src.m_hintLabelBinding;
        row.m_translationID = src.m_translationID;
        row.m_translationArgs = src.m_translationArgs;
        row.m_hintID = src.m_hintID;
        row.m_hintArgs = src.m_hintArgs;
        row.m_sortBinding = src.m_sortBinding;
        row.m_sortBindingPlugin = 0u;          // tri local (par l'interface), sur nos notes
        row.m_sortAscending = false;           // meilleures notes d'abord
        row.m_defaultWidth = src.m_defaultWidth;
        row.m_minimumWidth = src.m_minimumWidth;
        row.m_allowWidthAutoResizing = src.m_allowWidthAutoResizing;
        row.m_autoSizeCellPadding = src.m_autoSizeCellPadding;
        row.m_cellClass = src.m_cellClass;
        row.m_valueBindings = src.m_valueBindings;
        row.m_headerValueBindings = src.m_headerValueBindings;
        row.m_enableFooter = src.m_enableFooter;
        row.m_footerValueBindings = src.m_footerValueBindings;
        row.m_uxmlRef = src.m_uxmlRef;
        row.m_headerUxmlRef = src.m_headerUxmlRef;
        row.m_footerUxmlRef = src.m_footerUxmlRef;
        row.m_cellShortcuts = src.m_cellShortcuts;
        try { row._Uxml_k__BackingField = src._Uxml_k__BackingField; } catch { }
        try { row._HeaderUxml_k__BackingField = src._HeaderUxml_k__BackingField; } catch { }
        try { row._FooterUxml_k__BackingField = src._FooterUxml_k__BackingField; } catch { }
    }

    // ── 3 : affichage des notes ───────────────────────────────────────────

    // Canal ouvert par une cellule. Notre propriété : on demande au moteur celle du modèle (property est modifié)
    // et la cellule est suivie. Ancien mode : toute cellule de la propriété modèle est suivie.
    public static void OnUiOpenChannel(InteropReference reference, ref uint property, ulong key)
    {
        ColDef def;
        if ((property >> 16) == OwnPrefix && ByOwnId.TryGetValue(property, out def))
        {
            property = CarrierId(def);   // toujours, même colonnes inactives : le moteur ne connaît pas notre propriété
            _ownOpened++;
            if (!_active) return;
        }
        else
        {
            if (!_active || Notes.Count == 0) return;
            ResolveCarriers();
            if (ByProperty.Count == 0 || !ByProperty.TryGetValue(property, out def)) return;
        }
        if (reference is null) return;
        try
        {
            var ids = reference.m_id;
            int dref = 0;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i].m_id == Probe.KeyDref) { dref = ids[i].m_value; break; }
            // Diagnostic : premières cellules de chaque colonne (type de référence, valeur trouvée ou non)
            FirstCells.TryGetValue(def.Id, out var seen);
            if (seen < 3)
            {
                FirstCells[def.Id] = seen + 1;
                string r = "?";
                try { r = Names.Describe(reference); } catch { }
                float n = NoteOf(dref, def.FieldIndex);
                Plugin.L.LogInfo($"Colonne « {def.Title} » : cellule {r}, personne {dref - Probe.PersonDrefBase} -> {(float.IsNaN(n) ? "pas de valeur" : n.ToString("F1", CultureInfo.InvariantCulture))}");
            }
            if (dref != 0) KeyToCell[key] = (dref, def);
        }
        catch (Exception e) { Counters.Note("Colonne.OnUiOpenChannel", e); }
    }

    private static readonly Dictionary<string, int> FirstCells = new();

    public static void OnUiCloseChannel(ulong key) => KeyToCell.Remove(key);

    public static void MaybeReplace(ulong key, Il2CppSystem.Collections.Generic.List<TypedValue> values, int i)
    {
        if (!KeyToCell.TryGetValue(key, out var cell)) return;
        float note = NoteOf(cell.dref, cell.def.FieldIndex);
        // Profil non concerné : valeur nulle (comme une stat absente). NB : un texte vide est refusé par une cellule
        // numérique, qui garde alors le contenu de la ligne précédente (cellules recyclées au défilement).
        values[i] = float.IsNaN(note) ? TypedValue.Null : TypedValue.Create((int)Math.Round(note));
        _replaced++;
    }

    public static string Status() =>
        $"{KeyToCell.Count} cellules suivies, {_replaced} valeurs remplacées, {_sortReplaced} valeurs de tri remplacées, {Tables.Count} tableaux connus, menu : {_menuAdded}" +
        (_own ? $" ; propriétés à nous : {ByOwnId.Count} déclarées, {Defs.Count(d => d.OwnBound)} colonnes liées, {_ownOpened} canaux et {_ownNodes} nœuds redirigés, " +
                $"{AcceptHits} acceptations forcées, {_ownBatchFixed} lots corrigés, {_hintsReplaced} infobulles" : " ; ancien mode (porteuses)");

    // ── 4 : tri ───────────────────────────────────────────────────────────
    private static readonly Dictionary<IntPtr, string> SortPaths = new();
    private static int _sortLogged, _sortReplaced;

    public static void OnSortPath(StreamedListView view, string path)
    {
        if (view == null) return;
        SortPaths[view.Pointer] = path?.ToLowerInvariant();
        _sortLogged = 0;
        if (path != null && path.Contains("fm27", StringComparison.OrdinalIgnoreCase) && _sortPathLogged++ < 5)
            Plugin.L.LogInfo($"Tri demandé sur « {path} ».");
    }
    private static int _sortPathLogged;

    public static void OnSortCleared(StreamedListView view) { if (view != null) SortPaths.Remove(view.Pointer); }

    // Notre colonne sur laquelle le tableau est trié (demandé au tableau ; repli : chemin de tri mémorisé)
    private static ColDef SortedDef(StreamedListView view)
    {
        if (view == null) return null;
        try
        {
            var t = view.TryCast<StreamedTable>();
            string col = t?.SortedColumn;
            if (!string.IsNullOrEmpty(col)) return Defs.FirstOrDefault(d => d.Id == col);
        }
        catch { }
        return SortPaths.TryGetValue(view.Pointer, out var path) && path != null ? Defs.FirstOrDefault(d => d.SortPath == path) : null;
    }

    private static bool SortAscending(StreamedTable t)
    {
        try
        {
            var l = t.m_sortedColumns;
            if (l != null && l.Count > 0) return l[0].Item2;
        }
        catch { }
        return false;
    }

    // Valeur de tri de la ligne « index » si le tableau est trié sur une de nos colonnes, sinon null
    public static TypedValue SortValueFor(StreamedListView view, int index, TypedValue original)
    {
        if (!_active || view == null || index < 0) return null;
        var def = SortedDef(view);
        if (def == null) return null;
        int dref;
        try { dref = DrefOf(view.GetItemFromSource(index)); }
        catch (Exception e) { Counters.Note("Colonne.SortValueFor", e); return null; }
        float note = NoteOf(dref, def.FieldIndex);
        _sortReplaced++;
        if (_sortLogged++ < 3)
            Plugin.L.LogInfo($"  tri « {def.Title} » : ligne {index}, joueur {dref - Probe.PersonDrefBase} -> {note}");
        return TypedValue.Create(float.IsNaN(note) ? -1 : (int)Math.Round(note));
    }

    private static int DrefOf(Il2CppSystem.Object item)
    {
        if (item == null) return 0;
        InteropReference r = null;
        try
        {
            var tv = item.TryCast<TypedValue>();
            var o = tv != null ? tv.Get() : item;
            r = o?.TryCast<InteropReference>();
        }
        catch { }
        if (r == null) return 0;
        var ids = r.m_id;
        for (int i = 0; i < ids.Count; i++)
            if (ids[i].m_id == Probe.KeyDref) return ids[i].m_value;
        return 0;
    }

    // ── 5 : filtre ────────────────────────────────────────────────────────

    // Dernière liste complète reçue par chaque tableau (pour refiltrer quand le seuil change)
    private static readonly Dictionary<IntPtr, Il2CppSystem.Collections.IList> LastLists = new();

    // Maj+F11 / Ctrl+F11 : seuil du filtre ±5, mémorisé dans le fichier de réglage, liste refiltrée
    public static void AdjustFilter(int delta)
    {
        if (!_active) { Plugin.L.LogWarning("Filtre : colonnes inactives."); return; }
        _minNote = Math.Clamp(_minNote + delta, 0, 100);
        try
        {
            var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath).ToList() : new List<string>();
            int i = lines.FindIndex(l => l.TrimStart().StartsWith("filtre_min="));
            if (i >= 0) lines[i] = "filtre_min=" + _minNote; else lines.Add("filtre_min=" + _minNote);
            File.WriteAllLines(ConfigPath, lines);
        }
        catch (Exception e) { Plugin.L.LogWarning("Filtre : enregistrement du seuil : " + e.Message); }
        int refreshed = 0;
        foreach (var t in Tables)
        {
            try
            {
                if (t.panel == null || !LastLists.TryGetValue(t.Pointer, out var original)) continue;
                t.SetList(original);   // repasse par le filtre avec le nouveau seuil
                refreshed++;
            }
            catch (Exception e) { Plugin.L.LogWarning("Filtre : rafraîchissement : " + e.Message); }
        }
        Plugin.L.LogMessage($"Filtre : seuil = {_minNote}{(_minNote == 0 ? " (désactivé)" : "")}, {refreshed} liste(s) refiltrée(s).");
    }

    public static Il2CppSystem.Collections.Generic.List<TypedValue> Filter(StreamedTable table, Il2CppSystem.Collections.IList list)
    {
        if (!_active || list == null || table == null || Defs.Count == 0) return null;
        int total;
        try { total = list.Cast<Il2CppSystem.Collections.ICollection>().Count; } catch { return null; }
        if (total <= _minRows) return null;
        // Tableau qui connaît nos colonnes (joueurs ou staff selon le catalogue)
        bool Has(ColDef d) { try { return table.EntryForColumn(d.Id) != null; } catch { return false; } }
        var mine = Defs.Where(Has).ToList();
        if (mine.Count == 0) return null;
        LastLists[table.Pointer] = list;
        // Les longues listes (recherche) viennent du moteur, déjà triées par lui : si le tableau est trié sur
        // une de nos colonnes, c'est nous qui les rangeons par note.
        var sortDef = SortedDef(table);
        int field = Array.FindIndex(_fields, f => string.Equals(f, _filterField, StringComparison.OrdinalIgnoreCase));
        // Le filtre ne s'applique qu'aux tableaux qui ont une colonne de ce champ (la note des joueurs, pas le staff)
        bool filter = _minNote > 0 && field >= 0 && mine.Any(d => d.FieldIndex == field);
        if (!filter && sortDef == null) return null;
        var kept = new List<(TypedValue tv, float key)>();
        int persons = 0;
        for (int i = 0; i < total; i++)
        {
            var item = list[i];
            int dref = DrefOf(item);
            var tv = item?.TryCast<TypedValue>();
            if (tv == null) continue;
            if (dref == 0) { kept.Add((tv, float.NaN)); continue; }
            persons++;
            if (filter)
            {
                float note = NoteOf(dref, field);
                if (float.IsNaN(note) || note < _minNote) continue;
            }
            kept.Add((tv, sortDef != null ? NoteOf(dref, sortDef.FieldIndex) : 0));
        }
        if (persons == 0) return null;
        bool asc = false;
        if (sortDef != null)
        {
            asc = SortAscending(table);
            // sans note (NaN) toujours en fin de liste ; tri stable
            var ordered = asc
                ? kept.Select((k, i) => (k, i)).OrderBy(x => float.IsNaN(x.k.key) ? 1 : 0).ThenBy(x => x.k.key).ThenBy(x => x.i)
                : kept.Select((k, i) => (k, i)).OrderBy(x => float.IsNaN(x.k.key) ? 1 : 0).ThenByDescending(x => x.k.key).ThenBy(x => x.i);
            kept = ordered.Select(x => x.k).ToList();
        }
        var result = new Il2CppSystem.Collections.Generic.List<TypedValue>();
        foreach (var k in kept) result.Add(k.tv);
        string tname = "";
        try { tname = table.name; } catch { }
        Plugin.L.LogMessage($"Liste : tableau « {tname} », {total} lignes -> {result.Count}" +
                            (filter ? $" ({_filterField} ≥ {_minNote})" : "") +
                            (sortDef != null ? $", rangées par « {sortDef.Title} » ({(asc ? "croissant" : "décroissant")})" : "") + ".");
        return result;
    }

    // ── 6 : menu « insérer une colonne » ──────────────────────────────────
    private static int _menuAdded;

    public static void AddToMenu(StreamedTableViewSettingsCollection coll)
    {
        if (!_active || coll == null || Defs.Count == 0) return;
        var data = coll.m_contextMenuData;
        if (data == null) { MenuDiag(coll, "m_contextMenuData vide", null); return; }
        // seulement les menus de tableaux de joueurs, et une seule fois
        var leaves = new List<string>();
        foreach (var e in data) Walk(e, id => leaves.Add(id));
        bool done = leaves.Contains(Defs[0].Id);
        bool players = leaves.Any(id => id == "age" || id == "name" || id == "average-rating" || id == "height" || Defs.Any(d => d.Source == id));
        MenuDiag(coll, $"{data.Length} catégories, {leaves.Count} colonnes, joueurs={players}, déjà fait={done}", data);
        if (!players || done) return;

        var cat = new SIDropdownElement<string>();
        cat.DisplayValue = new SI.Translation.TranslationID(0, _category);
        cat.ChildList = new Il2CppSystem.Collections.Generic.List<SIDropdownElement<string>>();
        foreach (var d in Defs)
        {
            var leaf = new SIDropdownElement<string>();
            leaf.DisplayValue = new SI.Translation.TranslationID(0, d.Title);
            leaf.PropertyValue = d.Id;
            cat.ChildList.Add(leaf);
        }
        var arr = new Il2CppReferenceArray<SIDropdownElement<string>>(data.Length + 1);
        for (int i = 0; i < data.Length; i++) arr[i] = data[i];
        arr[data.Length] = cat;
        coll.m_contextMenuData = arr;
        _menuAdded++;
        string label = Try(() => cat.DisplayValue.TranslatedString);
        Plugin.L.LogMessage($"Menu : catégorie « {_category} » ajoutée ({Defs.Count} colonnes) ; libellé affiché = « {label} ».");
    }

    private static readonly HashSet<IntPtr> MenuSeen = new();

    private static void MenuDiag(StreamedTableViewSettingsCollection coll, string what, Il2CppReferenceArray<SIDropdownElement<string>> data)
    {
        if (!MenuSeen.Add(coll.Pointer)) return;
        string name = "?";
        try { name = coll.name; } catch { }
        var sb = new StringBuilder();
        try
        {
            for (int i = 0; data != null && i < data.Length && i < 12; i++)
            {
                var e = data[i];
                string label = Try(() => e.DisplayValue.TranslatedString), v = Try(() => e.PropertyValue);
                int n = 0; try { n = e.ChildList?.Count ?? 0; } catch { }
                sb.Append($" [{label}|{v}|{n} enfants]");
            }
        }
        catch { }
        Plugin.L.LogMessage($"Menu : collection « {name} » : {what}.{sb}");
    }

    public static void OnPopulateMenu(Il2CppSystem.Collections.Generic.List<string> ids)
    {
        if (_populateLogged++ >= 3) return;
        var sb = new StringBuilder();
        try { for (int i = 0; ids != null && i < ids.Count && i < 10; i++) sb.Append(ids[i]).Append(' '); } catch { }
        Plugin.L.LogMessage($"Menu : PopulateContextMenuColumns, {ids?.Count ?? 0} identifiants : {sb}");
    }
    private static int _populateLogged;

    private static void Walk(SIDropdownElement<string> e, Action<string> onLeaf)
    {
        if (e == null) return;
        string v = null;
        try { v = e.PropertyValue; } catch { }
        if (!string.IsNullOrEmpty(v)) onLeaf(v);
        var children = e.ChildList;
        if (children == null) return;
        for (int i = 0; i < children.Count; i++) Walk(children[i], onLeaf);
    }

    // ── Tableaux affichés (F11 : ajouter notre 1re colonne au tableau à l'écran) ──
    private static readonly List<StreamedTable> Tables = new();

    public static void Remember(StreamedTable t)
    {
        if (t == null) return;
        foreach (var x in Tables) if (x.Pointer == t.Pointer) return;
        Tables.Add(t);
        if (Tables.Count > 30) Tables.RemoveAt(0);
    }

    // F11 : ajoute toutes nos colonnes (dans l'ordre du fichier de réglage) au tableau affiché.
    // On peut ensuite retirer celles qu'on ne veut pas et enregistrer l'affichage dans le jeu.
    public static void AddToVisibleTables()
    {
        if (!_active || Defs.Count == 0) { Plugin.L.LogWarning("F11 : colonnes inactives (actif=1 dans " + ConfigPath + ", puis F10)."); return; }
        int tablesDone = 0, visible = 0;
        for (int i = Tables.Count - 1; i >= 0; i--)
        {
            var t = Tables[i];
            try
            {
                if (t.panel == null) continue;
                visible++;
                int n = 0;
                for (int k = 0; k < Defs.Count; k++)
                {
                    var id = Defs[k].Id;
                    TableTemplate.RowData entry = null;
                    try { entry = t.EntryForColumn(id); } catch { }
                    if (entry == null) continue;   // colonne d'un autre catalogue (joueurs / staff)
                    try { t.RemoveColumn(id); } catch { }
                    t.AddColumn(id, 1 + n);
                    n++;
                }
                if (n == 0) continue;
                tablesDone++;
                Plugin.L.LogMessage($"F11 : {n} colonne(s) ajoutée(s) au tableau « {t.name} ».");
            }
            catch (Exception e) { Plugin.L.LogWarning($"F11 : tableau {i} : {e.Message}"); }
        }
        Plugin.L.LogMessage($"F11 : {visible} tableaux affichés, colonnes ajoutées à {tablesDone}.");
    }

    // Titre de nos colonnes
    public static void OnHeaderText(string columnId, ref string text)
    {
        if (!_active || columnId == null) return;
        foreach (var d in Defs) if (d.Id == columnId) { text = d.Title; return; }
    }

    // Infobulle de nos colonnes (sinon : celle de la colonne modèle, ex. « Matchs en sélection »)
    public static void OnHintText(string columnId, ref string text)
    {
        if (!_active || columnId == null) return;
        foreach (var d in Defs)
        {
            if (d.Id != columnId) continue;
            string mine = d.Hint ?? $"{d.Title} (calculée par FM27 Explorer)";
            if (_hintsReplaced++ < 3) Plugin.L.LogInfo($"Colonne « {d.Title} » : infobulle « {text} » -> « {mine} »");
            text = mine;
            return;
        }
    }

    private static string Bindings(TableTemplate.RowData r)
    {
        var sb = new StringBuilder();
        try
        {
            var l = r.m_valueBindings;
            for (int j = 0; l != null && j < l.Count; j++) sb.Append(l[j].from).Append("->").Append(Try(() => l[j].to?.Path)).Append("; ");
        }
        catch (Exception e) { sb.Append("err:" + e.Message); }
        return sb.ToString().Trim();
    }

    private static string Try(Func<string> f) { try { return f() ?? ""; } catch { return "err"; } }
    private static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
}

[HarmonyPatch]
internal static class ColonnePatches
{
    // Catalogue de colonnes chargé depuis les bundles (méthode sans paramètre : sûre à accrocher).
    // NB : ne pas accrocher StreamedTable.OnFactoryAssetLoaded : son paramètre ResourceHandle<TableTemplate>
    // (structure générique) est mal transmis par Il2CppInterop et fait planter le jeu (2 nov. 2026).
    [HarmonyPatch(typeof(TableTemplate), nameof(TableTemplate.OnAfterDeserialize))]
    [HarmonyPostfix]
    private static void TemplateDeserializedPostfix(TableTemplate __instance)
    {
        using var perf = Perf.Measure();
        try { Colonne.OnTemplate(null, __instance, "chargement"); }
        catch (Exception e) { Counters.Note("Colonne.TemplateDeserialized", e); }
    }

    [HarmonyPatch(typeof(StreamedTable), nameof(StreamedTable.InitialiseColumns))]
    [HarmonyPostfix]
    private static void InitialiseColumnsPostfix(StreamedTable __instance)
    {
        using var perf = Perf.Measure();
        try { Colonne.Remember(__instance); } catch (Exception e) { Counters.Note("Colonne.Remember", e); }
    }

    // Menu « insérer une colonne » : on ajoute notre catégorie avant qu'il soit construit
    [HarmonyPatch(typeof(StreamedTableViewSettingsCollection), nameof(StreamedTableViewSettingsCollection.BuildContextMenuData))]
    [HarmonyPrefix]
    private static void BuildContextMenuDataPrefix(StreamedTableViewSettingsCollection __instance)
    {
        using var perf = Perf.Measure();
        try { Colonne.AddToMenu(__instance); } catch (Exception e) { Plugin.L.LogWarning("Menu : " + e); }
    }

    [HarmonyPatch(typeof(StreamedTableViewSettingsCollection), nameof(StreamedTableViewSettingsCollection.PopulateContextMenuColumns))]
    [HarmonyPrefix]
    private static void PopulateContextMenuColumnsPrefix(Il2CppSystem.Collections.Generic.List<string> __2)
    {
        using var perf = Perf.Measure();
        try { Colonne.OnPopulateMenu(__2); } catch { }
    }

    // Choix du tri local (chemin) / moteur
    [HarmonyPatch(typeof(StreamedListView), nameof(StreamedListView.SetSortingBinding), new[] { typeof(int), typeof(string), typeof(bool) })]
    [HarmonyPrefix]
    private static void SetSortingBindingPathPrefix(StreamedListView __instance, string __1)
    {
        using var perf = Perf.Measure();
        try { Colonne.OnSortPath(__instance, __1); } catch { }
    }

    [HarmonyPatch(typeof(StreamedListView), nameof(StreamedListView.SetSortingBinding), new[] { typeof(int), typeof(uint), typeof(bool) })]
    [HarmonyPrefix]
    private static void SetSortingBindingEnginePrefix(StreamedListView __instance)
    {
        using var perf = Perf.Measure();
        try { Colonne.OnSortPath(__instance, null); } catch { }
    }

    [HarmonyPatch(typeof(StreamedListView), nameof(StreamedListView.ClearSortingState))]
    [HarmonyPrefix]
    private static void ClearSortingStatePrefix(StreamedListView __instance)
    {
        using var perf = Perf.Measure();
        try { Colonne.OnSortCleared(__instance); } catch { }
    }

    // Valeur de tri d'une ligne : on rappelle la méthode d'origine avec la note (garde contre la récursion)
    [ThreadStatic] private static bool _inSort;

    [HarmonyPatch(typeof(StreamedListView), nameof(StreamedListView.ProcessSortingDataChanged))]
    [HarmonyPrefix]
    private static bool ProcessSortingDataChangedPrefix(StreamedListView __instance, SI.Bindable.Bindings.Key __0, TypedValue __1, int __2, byte __3)
    {
        if (_inSort) return true;
        TypedValue note;
        try { using (Perf.Measure()) note = Colonne.SortValueFor(__instance, __2, __1); } catch { return true; }
        if (note is null) return true;
        _inSort = true;
        try { __instance.ProcessSortingDataChanged(__0, note, __2, __3); }
        catch (Exception e) { Counters.Note("Colonne.ProcessSortingDataChanged", e); }
        finally { _inSort = false; }
        return false;
    }

    // Liste de joueurs reçue par un tableau : filtrée sur la note si demandé
    [ThreadStatic] private static bool _inSetList;

    [HarmonyPatch(typeof(StreamedTable), nameof(StreamedTable.SetList), new[] { typeof(Il2CppSystem.Collections.IList) })]
    [HarmonyPrefix]
    private static bool SetListPrefix(StreamedTable __instance, Il2CppSystem.Collections.IList __0)
    {
        if (_inSetList) return true;
        Il2CppSystem.Collections.Generic.List<TypedValue> filtered;
        try { using (Perf.Measure()) filtered = Colonne.Filter(__instance, __0); } catch (Exception e) { Counters.Note("Colonne.Filter", e); return true; }
        if (filtered == null) return true;
        _inSetList = true;
        try { __instance.SetList(filtered.Cast<Il2CppSystem.Collections.IList>()); }
        catch (Exception e) { Counters.Note("Colonne.SetList", e); }
        finally { _inSetList = false; }
        return false;
    }

    [HarmonyPatch(typeof(StreamedTable.Header), nameof(StreamedTable.Header.SetText))]
    [HarmonyPrefix]
    private static void HeaderSetTextPrefix(StreamedTable.Header __instance, ref string __0)
    {
        using var perf = Perf.Measure();
        try { Colonne.OnHeaderText(__instance.ColumnID, ref __0); } catch { }
    }

    [HarmonyPatch(typeof(StreamedTable.Header), nameof(StreamedTable.Header.SetHint))]
    [HarmonyPrefix]
    private static void HeaderSetHintPrefix(StreamedTable.Header __instance, ref string __0)
    {
        using var perf = Perf.Measure();
        try { Colonne.OnHintText(__instance.ColumnID, ref __0); } catch { }
    }
}

// Mode « propriétés » : une personne « accepte » nos propriétés (sinon l'interface pourrait refuser la liaison).
// Installé seulement avec proprietes=1.
[HarmonyPatch]
internal static class ProprietePatches
{
    [HarmonyPatch(typeof(FM.UI.PersonReference), nameof(FM.UI.PersonReference.AcceptsPropertyInternal))]
    [HarmonyPostfix]
    private static void AcceptsPropertyInternalPostfix(uint __0, ref bool __result)
    {
        using var perf = Perf.Measure();
        if (!__result && Colonne.IsOwn(__0)) { __result = true; Colonne.AcceptHits++; }
    }

    [HarmonyPatch(typeof(FM.UI.PersonReference), nameof(FM.UI.PersonReference.AcceptsProperty))]
    [HarmonyPostfix]
    private static void AcceptsPropertyPostfix(uint __0, ref bool __result)
    {
        using var perf = Perf.Measure();
        if (!__result && Colonne.IsOwn(__0)) { __result = true; Colonne.AcceptHits++; }
    }
}
