using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using org.kumagee;

namespace org.kumagee.EditorTools
{
    // Rebuilds the card-back atlases (Materials/cardback.png and
    // Materials/cardback_dark.png) from the per-design sources in
    // Solitaire/CardBacks/White. The atlas is a grid of CardLogic.BackAtlasColumns
    // x CardLogic.BackAtlasRows cells, like the face atlas; sources fill cells
    // left to right, top row first (cell row 0 is the top of the image, matching
    // CardLogic's -row * cellY texture offset). Cells past the source count keep
    // their current pixels, because they are authored only inside the atlas (the
    // joker backs) and have no source file. Every source is inverted into
    // CardBacks/Dark regardless of how many there are; only the first
    // columns*rows cells land in the atlas. The dark pass inverts each source's
    // RGB into CardBacks/Dark and the dark atlas.
    public static class CardBackAtlasBuilder
    {
        private const string WhiteDir = "Assets/Solitaire/CardBacks/White";
        private const string DarkDir = "Assets/Solitaire/CardBacks/Dark";
        private const string WhiteAtlasPath = "Assets/Solitaire/Materials/cardback.png";
        private const string DarkAtlasPath = "Assets/Solitaire/Materials/cardback_dark.png";
        private const string FilePrefix = "Cardback_";

        [MenuItem("Tools/Solitaire/Build Card Back Atlases")]
        public static void Build()
        {
            List<Texture2D> temp = new List<Texture2D>();
            try
            {
                string[] sources = SourceFiles(WhiteDir);
                if (sources.Length == 0)
                {
                    Debug.LogError("CardBackAtlasBuilder: no " + FilePrefix + "*.png in " + WhiteDir);
                    return;
                }
                Texture2D[] whites = new Texture2D[sources.Length];
                for (int i = 0; i < sources.Length; i++)
                {
                    whites[i] = Load(sources[i]);
                    temp.Add(whites[i]);
                    if (whites[i] == null)
                    {
                        Debug.LogError("CardBackAtlasBuilder: failed to read " + sources[i]);
                        return;
                    }
                    if (i > 0 && (whites[i].width != whites[0].width || whites[i].height != whites[0].height))
                    {
                        Debug.LogError("CardBackAtlasBuilder: " + sources[i] + " is "
                            + whites[i].width + "x" + whites[i].height + ", expected "
                            + whites[0].width + "x" + whites[0].height);
                        return;
                    }
                }

                Texture2D[] darks = new Texture2D[whites.Length];
                for (int i = 0; i < whites.Length; i++)
                {
                    darks[i] = Invert(whites[i]);
                    temp.Add(darks[i]);
                }

                WriteAtlas(WhiteAtlasPath, whites, temp);
                WriteAtlas(DarkAtlasPath, darks, temp);

                for (int i = 0; i < sources.Length; i++)
                {
                    string name = Path.GetFileName(sources[i]);
                    WritePng(Path.Combine(DarkDir, name.Insert(name.Length - 4, "_dark")), darks[i]);
                }

                AssetDatabase.SaveAssets();
                Debug.Log("CardBackAtlasBuilder: wrote " + Math.Min(sources.Length, CardLogic.BackAtlasColumns * CardLogic.BackAtlasRows)
                    + " cells into " + WhiteAtlasPath + " and " + DarkAtlasPath + ", plus "
                    + sources.Length + " dark sources in " + DarkDir);
            }
            finally
            {
                foreach (Texture2D tex in temp)
                {
                    if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                }
            }
        }

        private static string[] SourceFiles(string dir)
        {
            return Directory.GetFiles(dir, FilePrefix + "*.png")
                .OrderBy(p => Path.GetFileNameWithoutExtension(p).Substring(FilePrefix.Length), StringComparer.Ordinal)
                .ToArray();
        }

        // Reads the PNG bytes straight off disk: the sources import with
        // isReadable off, so AssetDatabase.LoadAssetAtPath would not give pixels.
        private static Texture2D Load(string path)
        {
            if (!File.Exists(path)) return null;
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path)))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }

        // Alpha is the card silhouette and must survive the inversion; the
        // existing dark sources are byte-identical to RGB-only inversion.
        private static Texture2D Invert(Texture2D source)
        {
            Texture2D result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            Color[] pixels = source.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color(1f - pixels[i].r, 1f - pixels[i].g, 1f - pixels[i].b, pixels[i].a);
            }
            result.SetPixels(pixels);
            result.Apply();
            return result;
        }

        private static void WriteAtlas(string path, Texture2D[] cells, List<Texture2D> temp)
        {
            int cellW = cells[0].width;
            int cellH = cells[0].height;
            int cols = CardLogic.BackAtlasColumns;
            int rows = CardLogic.BackAtlasRows;
            Texture2D atlas = new Texture2D(cols * cellW, rows * cellH, TextureFormat.RGBA32, false);
            temp.Add(atlas);

            Texture2D existing = Load(path);
            if (existing != null)
            {
                temp.Add(existing);
                if (existing.width == atlas.width && existing.height == atlas.height)
                {
                    atlas.SetPixels(existing.GetPixels());
                }
                else
                {
                    atlas.SetPixels(new Color[atlas.width * atlas.height]);
                }
            }
            else
            {
                atlas.SetPixels(new Color[atlas.width * atlas.height]);
            }

            // Cells beyond the grid cannot be placed; their dark sources are
            // still written from Build. Row 0 is the top of the image, matching
            // CardLogic's -row * cellY texture offset.
            int count = Math.Min(cells.Length, cols * rows);
            for (int i = 0; i < count; i++)
            {
                atlas.SetPixels((i % cols) * cellW, (rows - 1 - i / cols) * cellH, cellW, cellH, cells[i].GetPixels());
            }
            atlas.Apply();

            WritePng(path, atlas);
        }

        private static void WritePng(string path, Texture2D tex)
        {
            File.WriteAllBytes(path, tex.EncodeToPNG());
            EnsureMeta(path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }

        // A brand-new PNG gets Unity's default importer (textureType Default,
        // no alpha-as-transparency); copying a sibling's meta with a fresh guid
        // keeps it consistent with the rest of the folder.
        private static void EnsureMeta(string assetPath)
        {
            string metaPath = assetPath + ".meta";
            if (File.Exists(metaPath)) return;

            string siblingMeta = Directory.GetFiles(Path.GetDirectoryName(assetPath), "*.png.meta")
                .FirstOrDefault();
            if (siblingMeta == null) return;

            string meta = new Regex(@"guid: [0-9a-f]{32}").Replace(
                File.ReadAllText(siblingMeta),
                "guid: " + GUID.Generate().ToString(),
                1);
            File.WriteAllText(metaPath, meta);
        }
    }
}
