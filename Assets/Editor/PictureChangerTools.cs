using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools to:
// 1) List where PictureChanger is used and report texture sizes
// 2) Create per-size folders under Assets/PictureChanger/<WxH>/
// 3) Bind PictureChanger.textures arrays from those folders
public static class PictureChangerTools
{
    private const string RootFolder = "Assets/PictureChanger";
    private const string DefaultRandomFolder = "Assets/sameR&D/Picture"; // 指定の入力元
    internal static bool IsBulkOp { get; private set; }
    private const string GeneratedFolderName = "Generated";
    private const string CompressedFolderName = "Compressed1023"; // 長辺<1024の圧縮先
    private const int MaxLongSideLessThan = 1023; // 長辺の最大値(厳密に未満)

    [MenuItem("Tools/PictureChanger/Scan usages and sizes")] 
    public static void ScanUsagesAndSizes()
    {
        var entries = new List<PCEntry>();

        // Prefabs
        foreach (var path in AssetDatabase.FindAssets("t:Prefab").Select(AssetDatabase.GUIDToAssetPath))
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            foreach (var pc in go.GetComponentsInChildren<PictureChanger>(true))
            {
                entries.Add(BuildEntry(pc, path, isScene:false));
            }
        }

        // Scenes
        var currentScenePath = EditorSceneManager.GetActiveScene().path;
        foreach (var scenePath in AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath))
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var pc in root.GetComponentsInChildren<PictureChanger>(true))
                    {
                        entries.Add(BuildEntry(pc, scenePath, isScene:true));
                    }
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }

        Directory.CreateDirectory(RootFolder);
        var reportPath = Path.Combine(RootFolder, "picture_changer_report.txt");
        using (var sw = new StreamWriter(reportPath, false))
        {
            sw.WriteLine("PictureChanger usages and sizes");
            sw.WriteLine(DateTime.Now.ToString("u"));
            sw.WriteLine();
            foreach (var e in entries.OrderBy(e => e.LocationPath).ThenBy(e => e.ObjectPath))
            {
                sw.WriteLine($"- {(e.IsScene ? "Scene" : "Prefab")}: {e.LocationPath}");
                sw.WriteLine($"  GameObject: {e.ObjectPath}");
                sw.WriteLine($"  Size group: {e.SizeGroup}");
                if (e.Sizes.Count > 0)
                {
                    sw.WriteLine($"  Textures: {string.Join(", ", e.Sizes.Select(s => s.w + "x" + s.h))}");
                }
                else
                {
                    sw.WriteLine("  Textures: <none>");
                }
                sw.WriteLine();
            }
        }

        Debug.Log($"[PictureChanger] Report written: {reportPath}");

        // Ensure folders for each detected size group
        foreach (var group in entries.Select(e => e.SizeGroup).Where(g => !string.IsNullOrEmpty(g) && g != "Mixed").Distinct())
        {
            EnsureSizeFolder(group);
        }

        // Extra: warn P_PictureFrame verticals with unknown size
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.SizeGroup) || e.SizeGroup == "Unknown" || e.SizeGroup == "Mixed")
            {
                if (e.ObjectPath.Contains("P_PictureFrame"))
                {
                    Debug.LogWarning($"[PictureChanger] Size unknown for {e.ObjectPath} in {e.LocationPath}. Will default to portrait 1080x1920 during random-assign.");
                }
            }
        }
    }

    // Removed legacy binding/random assign from arbitrary folders. We now operate only via Compressed1023 workflow.

    [MenuItem("Tools/PictureChanger/Random assign VRChat images (resize, scenes)")]
    public static void RandomAssignVRChatImagesResized()
    {
        var folder = DefaultRandomFolder;
        if (!AssetDatabase.IsValidFolder(folder))
        {
            Debug.LogWarning($"[PictureChanger] Folder not found: {folder}");
            return;
        }

        IsBulkOp = true; // guard asset postprocessors
        try
        {
            var rng = new System.Random(Environment.TickCount);
            var lib = LoadTextureLibrary(folder, nameMustContain: "VRChat");
            if (lib.Count == 0)
            {
                Debug.LogWarning($"[PictureChanger] No Texture2D assets containing 'VRChat' in name under {folder}");
                return;
            }

            // Phase 1: collect required sizes
            var requiredSizes = new HashSet<(int w, int h)>();
            var scenePaths = AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            foreach (var scenePath in scenePaths)
            {
                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                try
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var pc in root.GetComponentsInChildren<PictureChanger>(true))
                        {
                            var pathH = GetHierarchyPath(pc.transform);
                            if (!pathH.Contains("P_PictureFrame")) continue;
                            var entry = BuildEntry(pc, scenePath, isScene: true);
                            if (TryParseSize(entry.SizeGroup, out var w, out var h))
                            {
                                requiredSizes.Add((w, h));
                            }
                            else
                            {
                                // Fallback: infer portrait/landscape from renderer bounds
                                var portrait = IsPortrait(pc);
                                var fw = portrait ? 1080 : 1920;
                                var fh = portrait ? 1920 : 1080;
                                requiredSizes.Add((fw, fh));
                            }
                        }
                    }
                }
                finally
                {
                    EditorSceneManager.CloseScene(scene, removeScene: true);
                }
            }

            // Phase 2: generate compressed assets (long side < 1024) for all sources
            AssetDatabase.StartAssetEditing();
            try
            {
                var compFolder = $"{RootFolder}/{CompressedFolderName}";
                EnsureFolderPath(compFolder);
                int genPortrait = 0, genLandscape = 0, skipped = 0;
                foreach (var src in lib)
                {
                    var baseName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(src.Tex));
                    var (nw, nh) = GetScaledSize(src.W, src.H, MaxLongSideLessThan);
                    var fileName = $"{Sanitize(baseName)}_{nw}x{nh}.png";
                    var outPath = Path.Combine(compFolder, fileName).Replace('\\','/');
                    if (File.Exists(outPath)) { skipped++; continue; } // already exists
                    var resized = ResizeTexture(src.Tex, nw, nh);
                    var bytes = resized.EncodeToPNG();
                    File.WriteAllBytes(outPath, bytes);
                    UnityEngine.Object.DestroyImmediate(resized);
                    AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
                    if (nh > nw) genPortrait++; else genLandscape++;
                }
                Debug.Log($"[PictureChanger] Compressed1023 generated: portrait={genPortrait}, landscape={genLandscape}, skipped-existing={skipped}");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            // Phase 3: assign randomly per scene with unique selection per orientation
            int updated = 0;
            foreach (var scenePath in scenePaths)
            {
                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                var sceneDirty = false;
                try
                {
                    // Build orientation-specific pools once per scene
                    var compFolder = $"{RootFolder}/{CompressedFolderName}";
                    var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { compFolder });
                    var texPaths = guids.Select(AssetDatabase.GUIDToAssetPath).ToArray();
                    var allPool = texPaths.Select(p => AssetDatabase.LoadAssetAtPath<Texture2D>(p)).Where(t => t != null).ToList();
                    var poolPortrait = allPool.Where(t => t.height > t.width).OrderBy(_ => rng.Next()).ToList();
                    var poolLandscape = allPool.Where(t => t.width >= t.height).OrderBy(_ => rng.Next()).ToList();
                    int idxPortrait = 0, idxLandscape = 0;

                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var pc in root.GetComponentsInChildren<PictureChanger>(true))
                        {
                            var pathH = GetHierarchyPath(pc.transform);
                            if (!pathH.Contains("P_PictureFrame")) continue;

                            bool portrait = IsPortrait(pc);
                            int desired = (pc.textures != null && pc.textures.Length > 0) ? pc.textures.Length : 1;
                            // Ensure uniqueness within each PictureChanger
                            var preferredPool = portrait ? poolPortrait : poolLandscape;
                            var pickedUnique = new List<Texture2D>();
                            // Take from preferred pool without replacement
                            int available = preferredPool.Count - (portrait ? idxPortrait : idxLandscape);
                            int toTake = Mathf.Min(desired, Math.Max(available, 0));
                            for (int k = 0; k < toTake; k++)
                            {
                                Texture2D tex;
                                if (portrait)
                                    tex = preferredPool[idxPortrait++];
                                else
                                    tex = preferredPool[idxLandscape++];
                                pickedUnique.Add(tex);
                            }
                            // If still short, fill from the other pool without replacement
                            if (pickedUnique.Count < desired)
                            {
                                var otherPool = portrait ? poolLandscape : poolPortrait;
                                int otherIdx = portrait ? idxLandscape : idxPortrait;
                                int otherAvail = (otherPool.Count - otherIdx);
                                int need = desired - pickedUnique.Count;
                                int take = Mathf.Min(need, Math.Max(otherAvail, 0));
                                for (int k = 0; k < take; k++)
                                {
                                    var tex = otherPool[portrait ? idxLandscape++ : idxPortrait++];
                                    if (!pickedUnique.Contains(tex)) pickedUnique.Add(tex);
                                }
                            }
                            // If still短い: 最低限1枚は割当（プールが空ならスキップ）
                            if (pickedUnique.Count == 0) continue;
                            // 過大要求はユニーク数に合わせて縮小
                            int finalCount = pickedUnique.Count;
                            var so = new SerializedObject(pc);
                            var sp = so.FindProperty("textures");
                            sp.arraySize = finalCount;
                            for (int i = 0; i < finalCount; i++)
                                sp.GetArrayElementAtIndex(i).objectReferenceValue = pickedUnique[i];
                            so.ApplyModifiedPropertiesWithoutUndo();
                            sceneDirty = true;
                        }
                    }
                }
                finally
                {
                    if (sceneDirty)
                    {
                        updated++;
                        EditorSceneManager.MarkSceneDirty(scene);
                        EditorSceneManager.SaveScene(scene);
                    }
                    EditorSceneManager.CloseScene(scene, removeScene: true);
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[PictureChanger] Random-assigned VRChat* images (resized) to {updated} scene asset(s).");
        }
        finally
        {
            IsBulkOp = false;
        }
    }

    [MenuItem("Tools/PictureChanger/Clean unreferenced compressed images")] 
    public static void CleanUnreferencedCompressed()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Gather all referenced textures from PictureChanger
        foreach (var scenePath in AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath))
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var pc in root.GetComponentsInChildren<PictureChanger>(true))
                    {
                        if (pc.textures == null) continue;
                        foreach (var t in pc.textures)
                        {
                            if (t == null) continue;
                            var p = AssetDatabase.GetAssetPath(t);
                            if (!string.IsNullOrEmpty(p)) referenced.Add(p.Replace('\\','/'));
                        }
                    }
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }
        foreach (var path in AssetDatabase.FindAssets("t:Prefab").Select(AssetDatabase.GUIDToAssetPath))
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            foreach (var pc in go.GetComponentsInChildren<PictureChanger>(true))
            {
                if (pc.textures == null) continue;
                foreach (var t in pc.textures)
                {
                    if (t == null) continue;
                    var p = AssetDatabase.GetAssetPath(t);
                    if (!string.IsNullOrEmpty(p)) referenced.Add(p.Replace('\\','/'));
                }
            }
        }

        // Find all compressed textures
        var compressedRoot = AssetDatabase.IsValidFolder(RootFolder) ? $"{RootFolder}/{CompressedFolderName}" : null;

        int deleted = 0;
        AssetDatabase.StartAssetEditing();
        try
        {
            if (!string.IsNullOrEmpty(compressedRoot) && AssetDatabase.IsValidFolder(compressedRoot))
            {
                var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { compressedRoot });
                foreach (var g in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(g).Replace('\\','/');
                    if (!referenced.Contains(p))
                    {
                        AssetDatabase.DeleteAsset(p);
                        deleted++;
                    }
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        Debug.Log($"[PictureChanger] Cleaned {deleted} unreferenced compressed textures.");
    }

    private class TexInfo
    {
        public Texture2D Tex;
        public int W;
        public int H;
        public string Group => $"{W}x{H}";
    }

    private static List<TexInfo> LoadTextureLibrary(string folder, string nameMustContain = null)
    {
        var list = new List<TexInfo>();
        var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
        foreach (var g in guids)
        {
            var p = AssetDatabase.GUIDToAssetPath(g);
            var t2 = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
            if (t2 == null) continue;
            if (!string.IsNullOrEmpty(nameMustContain))
            {
                var file = Path.GetFileNameWithoutExtension(p);
                if (file == null || file.IndexOf(nameMustContain, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }
            list.Add(new TexInfo { Tex = t2, W = t2.width, H = t2.height });
        }
        return list;
    }

    // (removed) AssignRandomTo: legacy per-component assignment from arbitrary library.

    // (removed) AssignRandomResizedTo: superseded by Compressed1023 pipeline.

    private static Texture2D ResizeTexture(Texture src, int width, int height)
    {
        var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var prev = RenderTexture.active;
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return tex;
    }

    private static (int w, int h) GetScaledSize(int srcW, int srcH, int maxLong)
    {
        if (srcW <= 0 || srcH <= 0) return (Mathf.Clamp(srcW,1,maxLong-1), Mathf.Clamp(srcH,1,maxLong-1));
        var longSide = Mathf.Max(srcW, srcH);
        if (longSide < maxLong) return (srcW, srcH);
        float scale = (maxLong - 0.5f) / longSide; // ensure結果がmaxLong未満
        int nw = Mathf.Max(1, Mathf.FloorToInt(srcW * scale));
        int nh = Mathf.Max(1, Mathf.FloorToInt(srcH * scale));
        nw = Mathf.Min(nw, maxLong - 1);
        nh = Mathf.Min(nh, maxLong - 1);
        return (nw, nh);
    }

    private static void EnsureFolderPath(string folder)
    {
        // folder like Assets/PictureChanger/1920x1080/Generated
        var parts = folder.Replace('\\','/').Split('/');
        var cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            var next = parts[i];
            var combined = string.IsNullOrEmpty(cur) ? next : cur + "/" + next;
            if (!AssetDatabase.IsValidFolder(combined))
            {
                AssetDatabase.CreateFolder(cur, next);
            }
            cur = combined;
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static bool TryParseSize(string sizeGroup, out int w, out int h)
    {
        w = h = 0;
        if (string.IsNullOrEmpty(sizeGroup)) return false;
        var parts = sizeGroup.Split('x');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h);
    }

    private static bool IsPortrait(PictureChanger pc)
    {
        // Decide orientation from the two largest extents of the target's geometry (width/height on the plane)
        if (pc == null || pc.targetObject == null) return true;
        // 1) Name heuristic takes priority when available
        var t = pc.targetObject.transform;
        if (TryGetPortraitByNameHeuristic(t, out var byName)) return byName;
        var mr = pc.targetObject.GetComponentInChildren<MeshRenderer>(true);
        if (mr == null)
        {
            // Fallback: compare lossyScale (robust to rotations where Y is larger than X)
            var s = t.lossyScale;
            var dims = new[] { Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z) };
            Array.Sort(dims);
            float width = dims[1];
            float height = dims[2];
            return height >= width;
        }

        // Prefer mesh-based measurement in world units
        var mf = mr.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            var ls = t.lossyScale;
            var local = mf.sharedMesh.bounds.size;
            var world = new Vector3(Mathf.Abs(local.x * ls.x), Mathf.Abs(local.y * ls.y), Mathf.Abs(local.z * ls.z));
            var dims = new[] { world.x, world.y, world.z };
            Array.Sort(dims); // dims[2] = largest, dims[1] = second largest => treat as plane height/width
            float width = dims[1];
            float height = dims[2];
            return height >= width;
        }
        else
        {
            var b = mr.bounds.size;
            var dims = new[] { b.x, b.y, b.z };
            Array.Sort(dims);
            float width = dims[1];
            float height = dims[2];
            return height >= width;
        }
    }

    private static bool TryGetPortraitByNameHeuristic(Transform t, out bool isPortrait)
    {
        isPortrait = true;
        string[] portraitKeys = { "vertical", "portrait", "縦" };
        string[] landscapeKeys = { "horizontal", "landscape", "横" };
        Transform cur = t;
        while (cur != null)
        {
            var name = cur.name.ToLowerInvariant();
            if (portraitKeys.Any(k => name.Contains(k))) { isPortrait = true; return true; }
            if (landscapeKeys.Any(k => name.Contains(k))) { isPortrait = false; return true; }
            cur = cur.parent;
        }
        return false;
    }

    private static PCEntry BuildEntry(PictureChanger pc, string locationPath, bool isScene)
    {
        var sizes = new List<(int w, int h)>();
        if (pc.textures != null)
        {
            foreach (var t in pc.textures)
            {
                if (t is Texture2D t2)
                {
                    sizes.Add((t2.width, t2.height));
                }
                else if (t != null)
                {
                    // Fallback via Texture importer
                    var path = AssetDatabase.GetAssetPath(t);
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (tex != null) sizes.Add((tex.width, tex.height));
                }
            }
        }

        // If no textures, try targetObject's renderer textures (mainTexture or any texture property)
        if (sizes.Count == 0 && pc.targetObject != null)
        {
            var mr = pc.targetObject.GetComponentInChildren<MeshRenderer>(true);
            if (mr != null)
            {
                // mainTexture first
                var mt = mr.sharedMaterial != null ? mr.sharedMaterial.mainTexture as Texture2D : null;
                if (mt != null) sizes.Add((mt.width, mt.height));
                // then search any texture properties on all sharedMaterials
                if (sizes.Count == 0)
                {
                    var mats = mr.sharedMaterials;
                    foreach (var mat in mats)
                    {
                        if (mat == null || mat.shader == null) continue;
                        int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                        for (int i = 0; i < propCount; i++)
                        {
                            if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                            var propName = ShaderUtil.GetPropertyName(mat.shader, i);
                            var tex = mat.GetTexture(propName) as Texture2D;
                            if (tex != null)
                            {
                                sizes.Add((tex.width, tex.height));
                                break;
                            }
                        }
                        if (sizes.Count > 0) break;
                    }
                }
            }
        }

        string sizeGroup = "";
        if (sizes.Count == 0)
        {
            sizeGroup = "Unknown";
        }
        else if (sizes.Select(s => s.w).Distinct().Count() == 1 && sizes.Select(s => s.h).Distinct().Count() == 1)
        {
            var s = sizes[0];
            sizeGroup = $"{s.w}x{s.h}";
        }
        else
        {
            sizeGroup = "Mixed";
        }

        return new PCEntry
        {
            IsScene = isScene,
            LocationPath = locationPath,
            ObjectPath = GetHierarchyPath(pc.transform),
            SizeGroup = sizeGroup,
            Sizes = sizes
        };
    }

    private static string GetHierarchyPath(Transform t)
    {
        var stack = new Stack<string>();
        while (t != null)
        {
            stack.Push(t.name);
            t = t.parent;
        }
        return string.Join("/", stack);
    }

    private static void EnsureSizeFolder(string group)
    {
        if (!AssetDatabase.IsValidFolder(RootFolder))
        {
            AssetDatabase.CreateFolder("Assets", "PictureChanger");
        }
        var subPath = $"{RootFolder}/{group}";
        if (!AssetDatabase.IsValidFolder(subPath))
        {
            AssetDatabase.CreateFolder(RootFolder, group);
            Debug.Log($"[PictureChanger] Created folder: {subPath}");
        }
    }

    private static bool BindFromFolder(PictureChanger pc)
    {
        var entry = BuildEntry(pc, "", isScene: false);
        if (string.IsNullOrEmpty(entry.SizeGroup) || entry.SizeGroup == "Unknown" || entry.SizeGroup == "Mixed")
        {
            return false; // cannot infer size folder
        }
        EnsureSizeFolder(entry.SizeGroup);

        var folder = $"{RootFolder}/{entry.SizeGroup}";
        var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
        var texPaths = guids.Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var textures = texPaths.Select(p => AssetDatabase.LoadAssetAtPath<Texture>(p)).Where(t => t != null).ToArray();

        var so = new SerializedObject(pc);
        var sp = so.FindProperty("textures");
        sp.arraySize = textures.Length;
        for (int i = 0; i < textures.Length; i++)
        {
            sp.GetArrayElementAtIndex(i).objectReferenceValue = textures[i];
        }
        so.ApplyModifiedPropertiesWithoutUndo();

        return true;
    }

    private class PCEntry
    {
        public bool IsScene;
        public string LocationPath;
        public string ObjectPath;
        public string SizeGroup;
        public List<(int w, int h)> Sizes = new List<(int w, int h)>();
    }
}

// Rebind PictureChanger when new textures are imported.
public class PictureChangerAssetPostprocessor : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (PictureChangerTools.IsBulkOp) return;
        if (string.IsNullOrEmpty(assetPath)) return;
        var p = assetPath.Replace('\\','/');
        if (!p.StartsWith("Assets/PictureChanger/", StringComparison.OrdinalIgnoreCase)) return;

        var importer = (TextureImporter)assetImporter;
        // Generated 配下は強めに圧縮し、ミップマップをオフに
        if (p.IndexOf("/Generated/", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("/Compressed1023/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.crunchedCompression = true;
            importer.compressionQuality = 50; // 0-100 中程度
            // 上限サイズの安全網。必要に応じて個別で上げられる
            if (importer.maxTextureSize > 2048) importer.maxTextureSize = 2048;
        }
    }

    void OnPostprocessTexture(Texture2D texture)
    {
        if (texture == null) return;
        if (PictureChangerTools.IsBulkOp || EditorApplication.isUpdating || EditorApplication.isCompiling)
            return; // avoid re-entrancy during bulk ops or refresh
        var group = $"{texture.width}x{texture.height}";
        var folder = $"Assets/PictureChanger/{group}";
        // Only trigger if imported into the matching folder
        if (!assetPath.Replace('\\','/').StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return;

        // Update prefabs
        foreach (var path in AssetDatabase.FindAssets("t:Prefab").Select(AssetDatabase.GUIDToAssetPath))
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            foreach (var pc in go.GetComponentsInChildren<PictureChanger>(true))
            {
                if (TryMatchGroup(pc, group))
                {
                    // Bind and mark dirty
                    if (Bind(pc, group)) EditorUtility.SetDirty(go);
                }
            }
        }

        // Update scenes (loaded additively and saved)
        foreach (var scenePath in AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath))
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
            var dirty = false;
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var pc in root.GetComponentsInChildren<PictureChanger>(true))
                    {
                        if (TryMatchGroup(pc, group))
                        {
                            if (Bind(pc, group)) dirty = true;
                        }
                    }
                }
            }
            finally
            {
                if (dirty)
                {
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                }
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }
    }

    private static bool TryMatchGroup(PictureChanger pc, string group)
    {
        // infer group from current textures or target main texture
        var sizes = new List<(int w, int h)>();
        if (pc.textures != null)
        {
            foreach (var t in pc.textures)
            {
                if (t is Texture2D t2) sizes.Add((t2.width, t2.height));
            }
        }
        if (sizes.Count == 0 && pc.targetObject != null)
        {
            var mr = pc.targetObject.GetComponent<MeshRenderer>();
            var tex = mr != null ? mr.sharedMaterial?.mainTexture as Texture2D : null;
            if (tex != null) sizes.Add((tex.width, tex.height));
        }
        if (sizes.Count == 0) return false;
        var g = (sizes.Select(s => s.w).Distinct().Count() == 1 && sizes.Select(s => s.h).Distinct().Count() == 1)
            ? $"{sizes[0].w}x{sizes[0].h}" : "Mixed";
        return g == group;
    }

    private static bool Bind(PictureChanger pc, string group)
    {
        var folder = $"Assets/PictureChanger/{group}";
        var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
        var texPaths = guids.Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var textures = texPaths.Select(p => AssetDatabase.LoadAssetAtPath<Texture>(p)).Where(t => t != null).ToArray();
        var so = new SerializedObject(pc);
        var sp = so.FindProperty("textures");
        sp.arraySize = textures.Length;
        for (int i = 0; i < textures.Length; i++)
            sp.GetArrayElementAtIndex(i).objectReferenceValue = textures[i];
        so.ApplyModifiedPropertiesWithoutUndo();
        return true;
    }
}
