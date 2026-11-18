using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using org.kumagee;

namespace org.kumagee.EditorTools
{
    // One-click wiring for the CardTheme prefab instances in the shop canvas.
    // A row's GameObject name is the card-back sprite suffix: a row named
    // "GradientStars" maps to Solitaire/CardBacks/White/Cardback_13_GradientStars.png,
    // and the NN in that filename is the CardBack enum value (CardBackAtlasBuilder
    // fills atlas column NN from the same file, and CardLogic maps enum value to
    // column). Resolving through the file index instead of the enum name is what
    // makes rows whose names differ from the enum members work ("GradientStars" ->
    // GradientStar, "Sakura" -> SakuraLines).
    //
    // rarity is deliberately not touched: it is per-row design data, not derivable
    // from the name.
    public static class CardThemeRowSetup
    {
        private const string WhiteDir = "Assets/Solitaire/CardBacks/White";
        private const string DarkDir = "Assets/Solitaire/CardBacks/Dark";
        private const string FilePrefix = "Cardback_";

        private class BackSource
        {
            public int Index;
            public string WhitePath;
        }

        [MenuItem("Tools/Casino/Setup CardTheme Rows From Names")]
        public static void SetupFromNames()
        {
            Dictionary<string, BackSource> bySuffix = BuildSuffixMap();
            if (bySuffix.Count == 0)
            {
                Debug.LogError("CardThemeRowSetup: no " + FilePrefix + "*_*.png in " + WhiteDir);
                return;
            }

            CardTheme[] themes = UnityEngine.Object.FindObjectsOfType<CardTheme>(true);
            if (themes.Length == 0)
            {
                Debug.LogWarning("CardThemeRowSetup: no CardTheme instances in the open scenes.");
                return;
            }

            Gacha[] gachas = UnityEngine.Object.FindObjectsOfType<Gacha>(true);
            Gacha gacha = gachas.Length == 1 ? gachas[0] : null;
            if (gachas.Length > 1)
            {
                Debug.LogWarning("CardThemeRowSetup: " + gachas.Length
                    + " Gacha behaviours found; gacha references left as-is.");
            }

            List<KeyValuePair<int, GameObject>> rows = new List<KeyValuePair<int, GameObject>>();
            List<string> skipped = new List<string>();

            foreach (CardTheme theme in themes.OrderBy(t => t.name, StringComparer.Ordinal))
            {
                BackSource source;
                if (!bySuffix.TryGetValue(theme.name, out source))
                {
                    skipped.Add(theme.name);
                    continue;
                }
                if (ApplyToRow(theme, source, gacha))
                {
                    rows.Add(new KeyValuePair<int, GameObject>(source.Index, theme.gameObject));
                }
                else
                {
                    skipped.Add(theme.name);
                }
            }

            if (gacha != null && rows.Count > 0)
            {
                WireGacha(gacha, rows);
            }

            EditorSceneManager.MarkAllScenesDirty();

            rows.Sort((a, b) => a.Key.CompareTo(b.Key));
            string report = "CardThemeRowSetup: wired " + rows.Count + " row(s): "
                + string.Join(", ", rows.Select(r => r.Value.name + "=" + (CardBack)r.Key).ToArray());
            if (skipped.Count > 0)
            {
                report += ". Skipped (no matching card-back sprite): " + string.Join(", ", skipped.ToArray());
            }
            if (gacha != null && rows.Count > 0)
            {
                report += ". Gacha.cardThemeObjects set to " + rows.Count + " row(s).";
            }
            Debug.Log(report);
        }

        // Maps each sprite suffix ("Blue", "GradientStars", ...) to its enum index
        // and source file. The index comes from the filename, not from parsing the
        // enum, because some suffixes are not valid enum member names.
        private static Dictionary<string, BackSource> BuildSuffixMap()
        {
            Dictionary<string, BackSource> map = new Dictionary<string, BackSource>();
            foreach (string path in Directory.GetFiles(WhiteDir, FilePrefix + "*.png"))
            {
                string file = Path.GetFileNameWithoutExtension(path);
                string body = file.Substring(FilePrefix.Length);
                int sep = body.IndexOf('_');
                if (sep <= 0) continue;
                int index;
                if (!int.TryParse(body.Substring(0, sep), out index)) continue;
                if (!Enum.IsDefined(typeof(CardBack), index)) continue;
                map[body.Substring(sep + 1)] = new BackSource { Index = index, WhitePath = path };
            }
            return map;
        }

        private static bool ApplyToRow(CardTheme theme, BackSource source, Gacha gacha)
        {
            string whiteName = Path.GetFileName(source.WhitePath);
            // Same _dark naming rule CardBackAtlasBuilder writes the dark sources with.
            string darkPath = Path.Combine(DarkDir, whiteName.Insert(whiteName.Length - 4, "_dark"));
            Sprite light = AssetDatabase.LoadAssetAtPath<Sprite>(source.WhitePath);
            Sprite dark = File.Exists(darkPath) ? AssetDatabase.LoadAssetAtPath<Sprite>(darkPath) : null;
            if (light == null || dark == null)
            {
                Debug.LogError("CardThemeRowSetup: failed to load sprite for '" + theme.name
                    + "' (white=" + source.WhitePath + ", dark=" + darkPath + ")", theme);
                return false;
            }

            Undo.RecordObject(theme, "Setup CardTheme Row");
            SerializedObject so = new SerializedObject(theme);

            SerializedProperty buttonProp = so.FindProperty("imageButton");
            SerializedProperty previewProp = so.FindProperty("previewImage");
            SerializedProperty rarityProp = so.FindProperty("rarityImage");
            SerializedProperty gachaProp = so.FindProperty("gacha");

            // The prefab wires these itself; this only fills them when a row was
            // built by hand. Resolved by child name + component type, never by a
            // scene-wide lookup.
            if (buttonProp.objectReferenceValue == null)
            {
                buttonProp.objectReferenceValue = theme.GetComponentsInChildren<Button>(true)
                    .FirstOrDefault(b => b.gameObject.name == "Image");
            }
            if (previewProp.objectReferenceValue == null)
            {
                previewProp.objectReferenceValue = theme.GetComponentsInChildren<Image>(true)
                    .FirstOrDefault(i => i.gameObject.name == "Image");
            }
            if (rarityProp.objectReferenceValue == null)
            {
                rarityProp.objectReferenceValue = theme.GetComponentsInChildren<Image>(true)
                    .FirstOrDefault(i => i.gameObject.name == "RarityBG");
            }
            if (gachaProp.objectReferenceValue == null && gacha != null)
            {
                gachaProp.objectReferenceValue = gacha;
            }

            so.FindProperty("cardBack").intValue = source.Index;
            so.FindProperty("darkSprite").objectReferenceValue = dark;
            so.ApplyModifiedProperties();

            Image preview = previewProp.objectReferenceValue as Image;
            if (preview != null)
            {
                Undo.RecordObject(preview, "Setup CardTheme Row");
                SerializedObject imgSo = new SerializedObject(preview);
                imgSo.FindProperty("m_Sprite").objectReferenceValue = light;
                imgSo.ApplyModifiedProperties();
            }

            if (PrefabUtility.IsPartOfPrefabInstance(theme))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(theme);
                if (preview != null)
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(preview);
                }
            }
            return true;
        }

        // The gacha machine needs every row in cardThemeObjects to roll and to
        // scroll to the pulled row; the scene ships it with placeholder nulls.
        private static void WireGacha(Gacha gacha, List<KeyValuePair<int, GameObject>> rows)
        {
            Undo.RecordObject(gacha, "Wire Gacha CardTheme Rows");
            SerializedObject so = new SerializedObject(gacha);
            SerializedProperty array = so.FindProperty("cardThemeObjects");
            if (array == null || !array.isArray)
            {
                Debug.LogError("CardThemeRowSetup: no serialized 'cardThemeObjects' array on Gacha.", gacha);
                return;
            }

            List<KeyValuePair<int, GameObject>> sorted = new List<KeyValuePair<int, GameObject>>(rows);
            sorted.Sort((a, b) => a.Key.CompareTo(b.Key));
            array.arraySize = sorted.Count;
            for (int i = 0; i < sorted.Count; i++)
            {
                array.GetArrayElementAtIndex(i).objectReferenceValue = sorted[i].Value;
            }
            so.ApplyModifiedProperties();
        }
    }
}
