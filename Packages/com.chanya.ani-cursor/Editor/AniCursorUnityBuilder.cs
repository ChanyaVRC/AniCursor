#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Chanya.AniCursor
{

/// <summary>
/// Builds the Unity/Modular Avatar portion of an animated cursor package.
///
/// The geometry generator owns the FBX and the manifest. This editor tool owns:
/// transparent cutout atlas import settings, the shared material, stepped UV
/// AnimationClips for one shared CursorDisplay renderer, the FX AnimatorController,
/// explicit menu values, a direct keep-world-pose MA Bone Proxy, and MA Merge
/// Animator integration.
/// </summary>
public static class AniCursorUnityBuilder
{
    private const string DefaultParameterName = "AniCursor_Select";
    private const string DefaultTextureProperty = "_MainTex";
    private const string LegacyRendererParent =
        "RightHandAnchor/Position_Adjust/CursorObjects";
    private const string LegacyDisplayPath =
        LegacyRendererParent + "/CursorDisplay";
    private const string DefaultDisplayPath = "CursorDisplay";

    private sealed class BuildPaths
    {
        public string RequestFile;
        public string ManifestFile;
        public string PrefabAsset;
        public string FbxAsset;
        public string AtlasSourceFile;
        public string AtlasAsset;
        public string MaterialAsset;
        public string ControllerAsset;
        public string ClipsFolder;
        public string IconsFolder;
        public string ResultFile;
    }

    private sealed class UvCell
    {
        public Vector2 Scale;
        public Vector2 Offset;
    }

    private sealed class UvStep
    {
        public float Time;
        public float Duration;
        public UvCell Cell;
    }

    private sealed class CursorTrack
    {
        public string Id;
        public string Source;
        public string ObjectName;
        public string BindingPath;
        public string MenuItemPath;
        public string ClipAssetPath;
        public int Value;
        public int MaterialSlot;
        public bool Loop;
        public List<UvStep> Steps = new List<UvStep>();
        public Type RendererType;
    }

    /// <summary>
    /// Optional batch-mode entry point. The interactive pipeline calls
    /// <see cref="BuildFromRequestFile"/> directly.
    /// </summary>
    public static void BuildFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var requestPath = ReadCommandLineValue(args, "-aniCursorBuildRequest");
        if (string.IsNullOrWhiteSpace(requestPath))
            throw new ArgumentException("-aniCursorBuildRequest <BuildRequest.json> is required.");

        try
        {
            var summary = BuildFromRequestFile(requestPath, true);
            Debug.Log("[AniCursorUnityBuilder] " + summary);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            throw;
        }
    }

    public static string BuildFromRequestFile(string requestPath, bool writeResult = true)
    {
        var requestFile = ResolveFilePath(requestPath, null);
        if (!File.Exists(requestFile))
            throw new FileNotFoundException("BuildRequest.json was not found.", requestFile);

        JObject request;
        try
        {
            request = JObject.Parse(File.ReadAllText(requestFile));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Invalid BuildRequest.json: " + requestFile, ex);
        }

        var paths = ResolveBuildPaths(request, requestFile);
        try
        {
            var summary = Build(request, paths);
            if (writeResult) WriteResult(paths.ResultFile, true, summary, paths, null);
            return summary;
        }
        catch (Exception ex)
        {
            if (writeResult) WriteResult(paths.ResultFile, false, ex.Message, paths, ex);
            throw;
        }
    }

    private static string Build(JObject request, BuildPaths paths)
    {
        if (!File.Exists(paths.ManifestFile))
            throw new FileNotFoundException("Animation manifest was not found.", paths.ManifestFile);

        JObject manifest;
        try
        {
            manifest = JObject.Parse(File.ReadAllText(paths.ManifestFile));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Invalid animation manifest: " + paths.ManifestFile, ex);
        }

        EnsureAssetFolder(Path.GetDirectoryName(paths.AtlasAsset)?.Replace('\\', '/'));
        EnsureAssetFolder(Path.GetDirectoryName(paths.MaterialAsset)?.Replace('\\', '/'));
        EnsureAssetFolder(Path.GetDirectoryName(paths.ControllerAsset)?.Replace('\\', '/'));
        EnsureAssetFolder(paths.ClipsFolder);

        CopyAtlas(paths, manifest);
        var texture = AniCursorImportSettings.ConfigureAtlas(paths.AtlasAsset);
        AniCursorImportSettings.ConfigureModel(paths.FbxAsset);
        AniCursorImportSettings.ConfigureMenuIcons(
            ResolveGeneratedIconPaths(manifest, paths.IconsFolder), 32);

        var textureProperty = ReadString(request, DefaultTextureProperty,
            "textureProperty", "texture_property", "mainTextureProperty");
        var stProperty = NormalizeStProperty(ReadUnityString(request, null,
            "atlasStProperty", "atlas_st_property", "stProperty", "st_property"), textureProperty);
        var clipFrameRate = ReadUnityFloat(request, 60f,
            "clipFrameRate", "clip_frame_rate", "frameRate", "frame_rate");
        if (clipFrameRate <= 0f) clipFrameRate = 60f;

        var parameterName = ReadString(request, DefaultParameterName,
            "parameterName", "parameter_name", "selectionParameter");
        var defaultValue = ReadInt(request, 0, "defaultValue", "default_value", "defaultSelection");
        var strict = ReadBool(request, true, "strict", "strictValidation", "strict_validation");
        var alphaCutoff = ResolveAlphaCutoff(request, manifest);
        var material = CreateOrUpdateMaterial(
            request, paths.MaterialAsset, texture, textureProperty, alphaCutoff);
        var displayPath = NormalizeBindingPath(ReadString(
            request,
            DefaultDisplayPath,
            "displayPath",
            "display_path",
            "rendererPath",
            "renderer_path"));

        var tracks = ParseTracks(request, manifest, paths, defaultValue);
        if (tracks.Count == 0) throw new InvalidDataException("Manifest contains no cursor animation tracks.");
        foreach (var track in tracks)
        {
            track.BindingPath = displayPath;
            track.RendererType = typeof(MeshRenderer);
        }

        var defaultTrack = tracks.FirstOrDefault(track => track.Value == defaultValue) ?? tracks[0];
        if (defaultTrack.Steps.Count == 0)
            throw new InvalidDataException("Default cursor track contains no animation steps.");
        SetMaterialDefaultCell(material, textureProperty, defaultTrack.Steps[0].Cell);
        var displayMesh = ResolveCursorDisplayMesh(paths.FbxAsset);

        var prefabRoot = PrefabUtility.LoadPrefabContents(paths.PrefabAsset);
        if (prefabRoot == null) throw new InvalidOperationException("Could not load prefab: " + paths.PrefabAsset);

        try
        {
            UnpackNestedCursorGeometry(prefabRoot);

            // Legacy prefabs used one renderer and one MA Object Toggle per track.
            // Resolve their menu-item ownership before removing those managed objects.
            var matchedItems = SetExplicitMenuValues(
                prefabRoot, parameterName, defaultValue, tracks);
            if (strict && matchedItems.Count != tracks.Count)
                throw new InvalidOperationException(
                    "Matched " + matchedItems.Count + " MA menu items for " + tracks.Count +
                    " cursor tracks.");
            RemoveManagedObjectToggles(matchedItems, tracks);
            EnsureSharedCursorDisplay(
                prefabRoot, displayPath, displayMesh, material, tracks, strict);

            var clips = new List<AnimationClip>(tracks.Count);
            foreach (var track in tracks)
                clips.Add(CreateOrUpdateClip(track, stProperty, clipFrameRate));

            var controller = CreateOrUpdateController(paths.ControllerAsset, parameterName,
                defaultValue, tracks, clips);

            UpsertSelectionParameter(prefabRoot, parameterName, defaultValue);
            ConfigureMergeAnimator(prefabRoot, controller);
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, paths.PrefabAsset);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefabRoot);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        return "Built " + tracks.Count + " looping cursor clips for one shared CursorDisplay, " +
               "controller, cutout material, atlas, and MA Merge Animator. Parameter: " +
               parameterName + ".";
    }

    private static BuildPaths ResolveBuildPaths(JObject request, string requestFile)
    {
        var requestDirectory = Path.GetDirectoryName(requestFile);
        var manifestValue = ReadPathValue(request, "manifestPath", "manifest_path", "manifest");
        var manifestFile = ResolveFilePath(Required(manifestValue, "manifestPath"), requestDirectory);

        var prefabAsset = RequireAssetPath(Required(ReadPathValue(request,
            "prefabPath", "prefab_path", "prefab"), "prefabPath"), "prefabPath");
        var fbxAsset = RequireAssetPath(Required(ReadPathValue(request,
            "fbxPath", "fbx_path", "modelPath", "model_path", "fbx"), "fbxPath"), "fbxPath");
        var atlasAsset = RequireAssetPath(Required(ReadPathValue(request,
            "atlasAssetPath", "atlas_asset_path", "atlasOutputPath", "atlas_output_path"),
            "atlasAssetPath"), "atlasAssetPath");
        var materialAsset = RequireAssetPath(Required(ReadPathValue(request,
            "materialPath", "material_path", "material"), "materialPath"), "materialPath");
        var controllerAsset = RequireAssetPath(Required(ReadPathValue(request,
            "controllerPath", "controller_path", "animatorControllerPath"), "controllerPath"),
            "controllerPath");
        var clipsFolder = RequireAssetPath(Required(ReadPathValue(request,
            "clipsFolder", "clips_folder", "animationFolder", "animation_folder"), "clipsFolder"),
            "clipsFolder");
        var iconsFolder = RequireAssetPath(Required(ReadPathValue(request,
            "iconsFolder", "icons_folder", "menuIconsFolder", "menu_icons_folder"), "iconsFolder"),
            "iconsFolder");

        var atlasSourceValue = ReadPathValue(request,
            "atlasSourcePath", "atlas_source_path", "atlasSource", "atlas_source", "atlas");
        var resultValue = ReadPathValue(request, "resultPath", "result_path");
        var resultFile = string.IsNullOrWhiteSpace(resultValue)
            ? Path.Combine(requestDirectory ?? ProjectRoot, "BuildResult.json")
            : ResolveFilePath(resultValue, requestDirectory);

        return new BuildPaths
        {
            RequestFile = requestFile,
            ManifestFile = manifestFile,
            PrefabAsset = prefabAsset,
            FbxAsset = fbxAsset,
            AtlasSourceFile = string.IsNullOrWhiteSpace(atlasSourceValue)
                ? null
                : ResolveFilePath(atlasSourceValue, requestDirectory),
            AtlasAsset = atlasAsset,
            MaterialAsset = materialAsset,
            ControllerAsset = controllerAsset,
            ClipsFolder = clipsFolder.TrimEnd('/'),
            IconsFolder = iconsFolder.TrimEnd('/'),
            ResultFile = resultFile
        };
    }

    private static void CopyAtlas(BuildPaths paths, JObject manifest)
    {
        var source = paths.AtlasSourceFile;
        if (string.IsNullOrWhiteSpace(source))
        {
            var atlasNode = FindObject(manifest, "atlas", "textureAtlas", "texture_atlas");
            var manifestAtlas = ReadString(atlasNode, null,
                "file", "path", "source", "sourcePath", "source_path");
            if (!string.IsNullOrWhiteSpace(manifestAtlas))
                source = ResolveFilePath(manifestAtlas, Path.GetDirectoryName(paths.ManifestFile));
        }

        var destination = AssetPathToFilePath(paths.AtlasAsset);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ProjectRoot);

        if (!string.IsNullOrWhiteSpace(source))
        {
            if (!File.Exists(source)) throw new FileNotFoundException("Atlas PNG was not found.", source);
            if (!PathsEqual(source, destination)) File.Copy(source, destination, true);
        }
        else if (!File.Exists(destination))
        {
            throw new FileNotFoundException(
                "No atlasSourcePath was provided and the destination atlas does not exist.", destination);
        }

    }

    private static IEnumerable<string> ResolveGeneratedIconPaths(JObject manifest, string assetFolder)
    {
        var cursors = FindArray(manifest, "cursors", "animations", "tracks")
                      ?? throw new InvalidDataException("Manifest contains no cursors.");
        var folder = assetFolder.Replace('\\', '/').TrimEnd('/');

        foreach (var cursor in cursors.OfType<JObject>())
        {
            var icon = FindObject(cursor, "icon");
            var relativePath = ReadString(icon, null, "file", "path");
            var fileName = Path.GetFileName(
                (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidDataException("Manifest cursor icon path is empty.");
            yield return folder + "/" + fileName;
        }
    }

    private static Material CreateOrUpdateMaterial(
        JObject request,
        string materialPath,
        Texture2D atlas,
        string textureProperty,
        float alphaCutoff)
    {
        var shaderName = ReadString(
            request, "Unlit/Transparent Cutout", "shader", "shaderName");
        var requestedShader = Shader.Find(shaderName);
        var fallbackShader = Shader.Find("Standard");
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            var shader = requestedShader ?? fallbackShader;
            if (shader == null) throw new InvalidOperationException("No usable cursor shader was found.");

            material = new Material(shader) { name = Path.GetFileNameWithoutExtension(materialPath) };
            AssetDatabase.CreateAsset(material, materialPath);
        }
        else if (requestedShader != null && material.shader != requestedShader)
        {
            // This generated material is owned by the cursor pipeline. Migrate
            // temporary opaque builds back to the signature-shell cutout shader.
            material.shader = requestedShader;
        }
        else if (material.shader == null && fallbackShader != null)
        {
            material.shader = fallbackShader;
        }

        if (!material.HasProperty(textureProperty))
            throw new InvalidOperationException(
                "Material shader does not expose texture property " + textureProperty + ".");

        material.SetTexture(textureProperty, atlas);
        ConfigureCutout(material, alphaCutoff);
        material.doubleSidedGI = true;
        material.enableInstancing = true;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void ConfigureCutout(Material material, float alphaCutoff)
    {
        alphaCutoff = Mathf.Clamp01(alphaCutoff);
        if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", alphaCutoff);

        if (material.shader != null &&
            string.Equals(material.shader.name, "Standard", StringComparison.Ordinal))
        {
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 1f);
            if (material.HasProperty("_SrcBlend"))
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            if (material.HasProperty("_DstBlend"))
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 1);
            material.EnableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        material.SetOverrideTag("RenderType", "TransparentCutout");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
    }

    private static void SetMaterialDefaultCell(
        Material material,
        string textureProperty,
        UvCell cell)
    {
        material.SetTextureScale(textureProperty, cell.Scale);
        material.SetTextureOffset(textureProperty, cell.Offset);
        EditorUtility.SetDirty(material);
    }

    private static float ResolveAlphaCutoff(JObject request, JObject manifest)
    {
        var requested = ReadNullableFloat(
            request, "alphaCutoff", "alpha_cutoff", "cutoff");
        if (!requested.HasValue)
            requested = ReadNullableFloat(
                FindObject(request, "unity"), "alphaCutoff", "alpha_cutoff", "cutoff");

        var geometry = FindObject(manifest, "geometry");
        if (!requested.HasValue)
            requested = ReadNullableFloat(
                geometry, "cutoff", "alphaCutoff", "alpha_cutoff");
        if (requested.HasValue) return Mathf.Clamp01(requested.Value);

        var threshold = ReadNullableInt(
            geometry, "alphaThreshold", "alpha_threshold", "threshold");
        if (!threshold.HasValue)
        {
            var firstCursor = FindArray(manifest, "cursors", "animations", "tracks")?
                .OfType<JObject>()
                .FirstOrDefault();
            threshold = ReadNullableInt(
                FindObject(firstCursor, "unionMask", "union_mask"),
                "alphaThreshold",
                "alpha_threshold",
                "threshold");
        }

        return Mathf.Clamp((threshold ?? 128) / 255f, 0f, 1f);
    }

    private static List<CursorTrack> ParseTracks(
        JObject request,
        JObject manifest,
        BuildPaths paths,
        int defaultValue)
    {
        var atlas = FindObject(manifest, "atlas", "textureAtlas", "texture_atlas") ?? manifest;
        var atlasWidth = ReadInt(atlas, ReadInt(manifest, 0, "atlasWidth", "atlas_width"),
            "width", "pixelWidth", "pixel_width");
        var atlasHeight = ReadInt(atlas, ReadInt(manifest, 0, "atlasHeight", "atlas_height"),
            "height", "pixelHeight", "pixel_height");
        var cells = ParseAtlasCells(atlas, atlasWidth, atlasHeight);

        var cursorArray = FindArray(manifest, "cursors", "animations", "tracks");
        if (cursorArray == null) throw new InvalidDataException("Manifest has no cursors/animations array.");

        var manifestCursors = cursorArray.OfType<JObject>().ToList();
        var bindingArray = FindArray(request, "cursorBindings", "cursor_bindings", "bindings", "cursors");
        var bindings = bindingArray == null
            ? new List<JObject>()
            : bindingArray.OfType<JObject>().ToList();

        var tracks = new List<CursorTrack>();
        var usedValues = new HashSet<int>();

        if (bindings.Count > 0)
        {
            foreach (var binding in bindings)
            {
                var cursor = FindMatchingCursor(manifestCursors, binding);
                if (cursor == null)
                    throw new InvalidDataException("No manifest cursor matched binding: " + binding);
                tracks.Add(ParseTrack(cursor, binding, cells, paths, defaultValue));
            }
        }
        else
        {
            foreach (var cursor in manifestCursors)
                tracks.Add(ParseTrack(cursor, null, cells, paths, defaultValue));
        }

        foreach (var track in tracks)
        {
            if (!usedValues.Add(track.Value))
                throw new InvalidDataException("Duplicate cursor selection value: " + track.Value);
            if (track.Steps.Count == 0)
                throw new InvalidDataException("Cursor has no animation steps: " + track.Source);
        }

        return tracks.OrderBy(t => t.Value).ToList();
    }

    private static CursorTrack ParseTrack(
        JObject cursor,
        JObject binding,
        Dictionary<string, UvCell> cells,
        BuildPaths paths,
        int defaultValue)
    {
        var manifestBinding = FindObject(cursor, "binding", "unityBinding", "unity_binding");
        var id = FirstNonEmpty(
            ReadString(binding, null, "cursorId", "cursor_id", "id"),
            ReadString(manifestBinding, null, "cursorId", "cursor_id", "id"),
            ReadString(cursor, null, "cursorId", "cursor_id", "id", "name"));
        var source = FirstNonEmpty(
            ReadString(binding, null, "source", "sourceFile", "source_file"),
            ReadString(manifestBinding, null, "source", "sourceFile", "source_file"),
            ReadString(cursor, null, "source", "sourceFile", "source_file", "name"), id);
        var objectName = FirstNonEmpty(
            ReadString(binding, null, "objectName", "object_name", "rendererObject"),
            ReadString(manifestBinding, null, "objectName", "object_name", "rendererObject"),
            ReadString(cursor, null, "objectName", "object_name", "rendererObject"),
            InferObjectName(source));
        if (string.IsNullOrWhiteSpace(objectName))
            throw new InvalidDataException("Cursor objectName could not be inferred: " + source);

        var value = ReadNullableInt(binding, "value", "menuValue", "menu_value", "selectionValue")
                    ?? ReadNullableInt(manifestBinding, "parameterValue", "parameter_value", "value",
                        "menuValue", "menu_value", "selectionValue")
                    ?? ReadNullableInt(cursor, "value", "menuValue", "menu_value", "selectionValue")
                    ?? DefaultValueForObject(objectName, defaultValue);

        var materialSlot = ReadNullableInt(binding, "materialSlot", "material_slot")
                           ?? ReadNullableInt(manifestBinding, "materialSlot", "material_slot")
                           ?? ReadNullableInt(cursor, "materialSlot", "material_slot")
                           ?? 0;
        if (materialSlot < 0)
            throw new InvalidDataException("material_slot cannot be negative: " + source);

        var bindingPath = FirstNonEmpty(
            ReadString(binding, null, "bindingPath", "binding_path", "rendererPath", "renderer_path"),
            ReadString(manifestBinding, null,
                "bindingPath", "binding_path", "rendererPath", "renderer_path"),
            ReadString(cursor, null, "bindingPath", "binding_path", "rendererPath", "renderer_path"),
            DefaultDisplayPath);

        var clipName = FirstNonEmpty(
            ReadString(binding, null, "clipName", "clip_name", "assetName", "asset_name"),
            ReadString(manifestBinding, null, "clipName", "clip_name", "assetName", "asset_name"),
            ReadString(cursor, null, "clipName", "clip_name", "assetName", "asset_name"),
            "AniCursor_" + objectName.Replace("Cursor_", string.Empty));
        var clipPath = FirstNonEmpty(
            ReadString(binding, null, "clipPath", "clip_path"),
            ReadString(manifestBinding, null, "clipPath", "clip_path"),
            ReadString(cursor, null, "clipPath", "clip_path"),
            paths.ClipsFolder + "/" + SanitizeFileName(clipName) + ".anim");
        clipPath = RequireAssetPath(clipPath, "clipPath");

        var loopNode = FindObject(cursor, "loop");
        var loop = ReadBool(binding,
            ReadBool(loopNode, ReadBool(cursor, true, "loop", "loopTime", "loop_time"),
                "unity_loop_time", "loopTime", "enabled"),
            "loop", "loopTime", "loop_time");

        var track = new CursorTrack
        {
            Id = id,
            Source = source,
            ObjectName = objectName,
            BindingPath = NormalizeBindingPath(bindingPath),
            MenuItemPath = FirstNonEmpty(
                ReadString(binding, null, "menuItemPath", "menu_item_path"),
                ReadString(manifestBinding, null, "menuItemPath", "menu_item_path")),
            ClipAssetPath = clipPath,
            Value = value,
            MaterialSlot = materialSlot,
            Loop = loop
        };

        ParseSteps(cursor, cells, track.Steps);
        return track;
    }

    private static Dictionary<string, UvCell> ParseAtlasCells(JObject atlas, int atlasWidth, int atlasHeight)
    {
        var result = new Dictionary<string, UvCell>(StringComparer.OrdinalIgnoreCase);
        var array = FindArray(atlas, "frames", "cells", "entries", "regions");
        if (array == null) return result;

        var index = 0;
        foreach (var obj in array.OfType<JObject>())
        {
            var id = FirstNonEmpty(ReadString(obj, null,
                "frameId", "frame_id", "id", "name", "key"), index.ToString(CultureInfo.InvariantCulture));
            var cell = ParseUvCell(obj, atlasWidth, atlasHeight);
            if (cell != null)
            {
                result[id] = cell;
                result[index.ToString(CultureInfo.InvariantCulture)] = cell;
            }
            index++;
        }

        return result;
    }

    private static void ParseSteps(JObject cursor, Dictionary<string, UvCell> cells, List<UvStep> output)
    {
        var array = FindArray(cursor, "steps", "keyframes", "frames", "playbackSteps", "playback_steps");
        if (array == null) return;

        var jiffiesPerSecond = ReadFloat(cursor, 60f,
            "jiffiesPerSecond", "jiffies_per_second", "ticksPerSecond", "ticks_per_second");
        if (jiffiesPerSecond <= 0f) jiffiesPerSecond = 60f;

        var cumulative = 0f;
        foreach (var token in array)
        {
            if (!(token is JObject step)) continue;

            var time = ReadTimeSeconds(step, cumulative, jiffiesPerSecond,
                "time", "startSeconds", "start_seconds", "startMs", "start_ms",
                "startJiffy", "start_jiffy", "startTick", "start_tick");
            var duration = ReadDurationSeconds(step, jiffiesPerSecond);
            var cell = ParseUvCell(step, 0, 0);

            if (cell == null)
            {
                var frameId = ReadString(step, null,
                    "frameId", "frame_id", "cellId", "cell_id", "atlasIndex", "atlas_index", "index");
                if (!string.IsNullOrWhiteSpace(frameId)) cells.TryGetValue(frameId, out cell);
            }

            if (cell == null)
                throw new InvalidDataException("Animation step has no UV cell: " + step);

            output.Add(new UvStep { Time = time, Duration = duration, Cell = cell });
            cumulative = Mathf.Max(cumulative, time + duration);
        }

        output.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private static UvCell ParseUvCell(JObject obj, int atlasWidth, int atlasHeight)
    {
        var mainTexSt = FindToken(obj, "mainTexSt", "main_tex_st") as JArray;
        if (mainTexSt != null && mainTexSt.Count >= 4)
        {
            return new UvCell
            {
                Scale = new Vector2(mainTexSt[0].Value<float>(), mainTexSt[1].Value<float>()),
                Offset = new Vector2(mainTexSt[2].Value<float>(), mainTexSt[3].Value<float>())
            };
        }

        var uv = FindObject(obj, "uvRectUnity", "uv_rect_unity", "uvRect", "uv_rect", "uv");
        if (uv != null)
        {
            var uMin = ReadNullableFloat(uv, "uMin", "u_min", "x", "left");
            var vMin = ReadNullableFloat(uv, "vMin", "v_min", "y", "bottom");
            var uMax = ReadNullableFloat(uv, "uMax", "u_max", "right");
            var vMax = ReadNullableFloat(uv, "vMax", "v_max", "top");
            var width = ReadNullableFloat(uv, "width", "w");
            var height = ReadNullableFloat(uv, "height", "h");

            if (uMin.HasValue && vMin.HasValue && (uMax.HasValue || width.HasValue) &&
                (vMax.HasValue || height.HasValue))
            {
                var sx = uMax.HasValue ? uMax.Value - uMin.Value : width.Value;
                var sy = vMax.HasValue ? vMax.Value - vMin.Value : height.Value;
                return new UvCell
                {
                    Scale = new Vector2(sx, sy),
                    Offset = new Vector2(uMin.Value, vMin.Value)
                };
            }
        }

        var scale = ReadVector2(obj, "scale", "uvScale", "uv_scale", "textureScale", "texture_scale");
        var offset = ReadVector2(obj, "offset", "uvOffset", "uv_offset", "textureOffset", "texture_offset");
        if (scale.HasValue && offset.HasValue)
            return new UvCell { Scale = scale.Value, Offset = offset.Value };

        var pixel = FindObject(obj, "pixelRectTopLeft", "pixel_rect_top_left", "pixelRect", "pixel_rect");
        if (pixel != null && atlasWidth > 0 && atlasHeight > 0)
        {
            var x = ReadFloat(pixel, 0f, "x", "left");
            var y = ReadFloat(pixel, 0f, "y", "top");
            var w = ReadFloat(pixel, 0f, "width", "w");
            var h = ReadFloat(pixel, 0f, "height", "h");
            if (w > 0f && h > 0f)
            {
                return new UvCell
                {
                    Scale = new Vector2(w / atlasWidth, h / atlasHeight),
                    Offset = new Vector2(x / atlasWidth, 1f - ((y + h) / atlasHeight))
                };
            }
        }

        return null;
    }

    private static Mesh ResolveCursorDisplayMesh(string fbxAssetPath)
    {
        var meshes = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath).OfType<Mesh>().ToList();
        if (meshes.Count != 1)
            throw new InvalidDataException(
                "Cursor FBX must contain exactly one Mesh subasset; found " +
                meshes.Count + ": " + fbxAssetPath);

        var mesh = meshes[0];
        if (!string.Equals(mesh.name, "CursorDisplay", StringComparison.Ordinal))
            Debug.LogWarning(
                "[AniCursorUnityBuilder] The only FBX Mesh subasset is named " + mesh.name +
                "; expected CursorDisplay. It will still be used.");

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
        if (model == null)
            throw new InvalidDataException("Cursor FBX has no imported model root: " + fbxAssetPath);

        var renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length != 1)
            throw new InvalidDataException(
                "Cursor FBX must contain exactly one renderer object: " +
                fbxAssetPath);

        var display = renderers[0].transform;
        var rendererIsModelRoot = display == model.transform;
        if (!rendererIsModelRoot && display.parent != model.transform)
            throw new InvalidDataException(
                "The imported FBX renderer must be on the model root or its direct child.");

        var filter = display.GetComponent<MeshFilter>();
        var importedMesh = filter != null ? filter.sharedMesh : null;
        var skinned = renderers[0] as SkinnedMeshRenderer;
        if (skinned != null) importedMesh = skinned.sharedMesh;
        if (importedMesh != mesh)
            throw new InvalidDataException(
                "Imported CursorDisplay renderer is not bound to the only FBX Mesh subasset.");

        return mesh;
    }

    private static void EnsureSharedCursorDisplay(
        GameObject prefabRoot,
        string displayPath,
        Mesh displayMesh,
        Material material,
        IEnumerable<CursorTrack> tracks,
        bool strict)
    {
        var display = prefabRoot.transform.Find(displayPath);
        if (display == null)
        {
            var legacyDisplay = prefabRoot.transform.Find(LegacyDisplayPath);
            if (legacyDisplay != null)
            {
                // Preserve the authored world pose while removing the generated
                // RightHandAnchor / Position_Adjust / CursorObjects wrappers.
                legacyDisplay.SetParent(prefabRoot.transform, true);
                display = legacyDisplay;
            }
            else
            {
                display = FindOrCreateTransform(prefabRoot.transform, displayPath);
            }
        }

        display.name = "CursorDisplay";
        // Unit and axis conversion are baked into the static FBX mesh.
        display.localPosition = Vector3.zero;
        display.localRotation = Quaternion.identity;
        display.localScale = Vector3.one;
        display.gameObject.SetActive(true);

        RemoveLegacyCursorGeometry(prefabRoot.transform, tracks, display);
        var legacyCursorObjects = prefabRoot.transform.Find(LegacyRendererParent);
        if (legacyCursorObjects != null) RemoveLegacyWrapperGeometry(legacyCursorObjects);

        foreach (var incompatible in display.GetComponents<Renderer>()
                     .Where(renderer => !(renderer is MeshRenderer)).ToArray())
            UnityEngine.Object.DestroyImmediate(incompatible, true);

        var filter = display.GetComponent<MeshFilter>();
        if (filter == null) filter = display.gameObject.AddComponent<MeshFilter>();
        var rendererComponent = display.GetComponent<MeshRenderer>();
        if (rendererComponent == null) rendererComponent = display.gameObject.AddComponent<MeshRenderer>();

        filter.sharedMesh = displayMesh;
        rendererComponent.sharedMaterials = new[] { material };

        var boneProxy = display.GetComponent<ModularAvatarBoneProxy>();
        if (boneProxy == null) boneProxy = display.gameObject.AddComponent<ModularAvatarBoneProxy>();
        boneProxy.boneReference = HumanBodyBones.RightHand;
        boneProxy.subPath = string.Empty;
        boneProxy.attachmentMode = BoneProxyAttachmentMode.AsChildKeepWorldPose;
        boneProxy.matchScale = false;

        RemoveLegacyAttachmentHierarchy(prefabRoot);

        EditorUtility.SetDirty(display);
        EditorUtility.SetDirty(filter);
        EditorUtility.SetDirty(rendererComponent);
        EditorUtility.SetDirty(boneProxy);

        var managedNames = new HashSet<string>(
            tracks.Select(track => track.ObjectName), StringComparer.Ordinal);
        var remainingManagedRenderers = prefabRoot
            .GetComponentsInChildren<Renderer>(true)
            .Where(renderer => renderer.transform != display && managedNames.Contains(renderer.name))
            .ToList();
        if (remainingManagedRenderers.Count > 0)
        {
            var message = "Legacy cursor renderers remain after shared-display migration: " +
                          string.Join(", ", remainingManagedRenderers.Select(renderer => renderer.name));
            if (strict) throw new InvalidOperationException(message);
            Debug.LogWarning("[AniCursorUnityBuilder] " + message);
        }
    }

    private static void RemoveLegacyWrapperGeometry(Transform cursorObjects)
    {
        // CursorObjects was a generated FBX wrapper. Its render components are
        // always legacy after the direct CursorDisplay migration.
        foreach (var renderer in cursorObjects.GetComponents<MeshRenderer>())
            UnityEngine.Object.DestroyImmediate(renderer, true);
        foreach (var renderer in cursorObjects.GetComponents<SkinnedMeshRenderer>())
            UnityEngine.Object.DestroyImmediate(renderer, true);
        foreach (var filter in cursorObjects.GetComponents<MeshFilter>())
            UnityEngine.Object.DestroyImmediate(filter, true);
    }

    private static void RemoveLegacyCursorGeometry(
        Transform managedRoot,
        IEnumerable<CursorTrack> tracks,
        Transform display)
    {
        var managedNames = new HashSet<string>(
            tracks.Select(track => track.ObjectName), StringComparer.Ordinal);
        var candidates = managedRoot.GetComponentsInChildren<Transform>(true)
            .Where(transform => transform != managedRoot && transform != display &&
                                managedNames.Contains(transform.name))
            .OrderByDescending(transform => GetTransformDepth(transform))
            .ToList();

        foreach (var candidate in candidates)
        {
            foreach (var renderer in candidate.GetComponents<Renderer>())
                UnityEngine.Object.DestroyImmediate(renderer, true);
            foreach (var filter in candidate.GetComponents<MeshFilter>())
                UnityEngine.Object.DestroyImmediate(filter, true);

            // Delete only a fully managed, now-empty leaf. If the user attached
            // another component or child, keep that object and its MA setup.
            if (candidate.childCount == 0 &&
                candidate.GetComponents<Component>().All(component => component is Transform))
                UnityEngine.Object.DestroyImmediate(candidate.gameObject, true);
        }
    }

    private static void RemoveLegacyAttachmentHierarchy(GameObject prefabRoot)
    {
        var handAnchor = prefabRoot.transform.Find("RightHandAnchor");
        if (handAnchor != null)
        {
            foreach (var proxy in handAnchor.GetComponents<ModularAvatarBoneProxy>())
                UnityEngine.Object.DestroyImmediate(proxy, true);
        }

        var paths = new[]
        {
            LegacyRendererParent,
            "RightHandAnchor/Position_Adjust",
            "RightHandAnchor"
        };
        foreach (var path in paths)
        {
            var wrapper = prefabRoot.transform.Find(path);
            if (wrapper == null || wrapper.childCount != 0) continue;
            if (wrapper.GetComponents<Component>().All(component => component is Transform))
                UnityEngine.Object.DestroyImmediate(wrapper.gameObject, true);
        }
    }

    private static int GetTransformDepth(Transform transform)
    {
        var depth = 0;
        for (var current = transform; current != null; current = current.parent) depth++;
        return depth;
    }

    private static Transform FindOrCreateTransform(Transform root, string relativePath)
    {
        var current = root;
        foreach (var segment in relativePath.Replace('\\', '/').Split('/'))
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            var child = current.Find(segment);
            if (child == null)
            {
                var childObject = new GameObject(segment);
                childObject.transform.SetParent(current, false);
                child = childObject.transform;
            }
            current = child;
        }
        return current;
    }

    private static void UnpackNestedCursorGeometry(GameObject prefabRoot)
    {
        var candidates = new[]
        {
            prefabRoot.transform.Find(DefaultDisplayPath),
            prefabRoot.transform.Find(LegacyRendererParent)
        };
        var unpacked = new HashSet<GameObject>();
        foreach (var candidate in candidates.Where(candidate => candidate != null))
        {
            var nestedInstanceRoot =
                PrefabUtility.GetNearestPrefabInstanceRoot(candidate.gameObject);
            if (nestedInstanceRoot == null || nestedInstanceRoot == prefabRoot ||
                !unpacked.Add(nestedInstanceRoot))
                continue;

            PrefabUtility.UnpackPrefabInstance(
                nestedInstanceRoot,
                PrefabUnpackMode.Completely,
                InteractionMode.AutomatedAction);
        }
    }

    private static AnimationClip CreateOrUpdateClip(
        CursorTrack track,
        string stProperty,
        float frameRate)
    {
        EnsureAssetFolder(Path.GetDirectoryName(track.ClipAssetPath)?.Replace('\\', '/'));

        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(track.ClipAssetPath);
        if (clip == null)
        {
            clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(track.ClipAssetPath) };
            AssetDatabase.CreateAsset(clip, track.ClipAssetPath);
        }

        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            AnimationUtility.SetEditorCurve(clip, binding, null);
        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            AnimationUtility.SetObjectReferenceCurve(clip, binding, null);

        clip.frameRate = frameRate;
        clip.wrapMode = track.Loop ? WrapMode.Loop : WrapMode.ClampForever;

        var duration = track.Steps.Max(s => s.Time + Mathf.Max(0f, s.Duration));
        if (duration <= 0f) duration = track.Steps.Max(s => s.Time) + (1f / 60f);

        // CursorDisplay uses normalized 0..1 frame UVs. Every state therefore
        // writes the absolute atlas cell to the same renderer; no track-local
        // base cell and no mesh/object activation curve is involved.
        SetSteppedCurve(clip, track.BindingPath, track.RendererType, stProperty + ".x", track.Steps,
            step => step.Cell.Scale.x, duration, track.Loop);
        SetSteppedCurve(clip, track.BindingPath, track.RendererType, stProperty + ".y", track.Steps,
            step => step.Cell.Scale.y, duration, track.Loop);
        SetSteppedCurve(clip, track.BindingPath, track.RendererType, stProperty + ".z", track.Steps,
            step => step.Cell.Offset.x, duration, track.Loop);
        SetSteppedCurve(clip, track.BindingPath, track.RendererType, stProperty + ".w", track.Steps,
            step => step.Cell.Offset.y, duration, track.Loop);

        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = track.Loop;
        settings.loopBlend = false;
        settings.loopBlendOrientation = false;
        settings.loopBlendPositionY = false;
        settings.loopBlendPositionXZ = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        EditorUtility.SetDirty(clip);
        return clip;
    }

    private static void SetSteppedCurve(
        AnimationClip clip,
        string path,
        Type rendererType,
        string property,
        IList<UvStep> steps,
        Func<UvStep, float> value,
        float duration,
        bool loop)
    {
        var keys = new List<Keyframe>(steps.Count + 2);
        if (steps[0].Time > 0f) keys.Add(new Keyframe(0f, value(steps[0])));
        foreach (var step in steps) keys.Add(new Keyframe(step.Time, value(step)));

        var finalValue = loop ? value(steps[0]) : value(steps[steps.Count - 1]);
        if (keys.Count == 0 || duration > keys[keys.Count - 1].time + 0.000001f)
            keys.Add(new Keyframe(duration, finalValue));
        else
            keys[keys.Count - 1] = new Keyframe(duration, finalValue);

        var curve = new AnimationCurve(keys.ToArray())
        {
            preWrapMode = WrapMode.ClampForever,
            postWrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever
        };
        for (var i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
        }

        var binding = EditorCurveBinding.FloatCurve(path, rendererType, property);
        AnimationUtility.SetEditorCurve(clip, binding, curve);
    }

    private static AnimatorController CreateOrUpdateController(
        string controllerPath,
        string parameterName,
        int defaultValue,
        IList<CursorTrack> tracks,
        IList<AnimationClip> clips)
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
        if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);

        foreach (var parameter in controller.parameters.ToArray()) controller.RemoveParameter(parameter);
        while (controller.layers.Length > 0) controller.RemoveLayer(0);

        controller.AddParameter(new AnimatorControllerParameter
        {
            name = parameterName,
            type = AnimatorControllerParameterType.Int,
            defaultInt = defaultValue
        });
        controller.AddLayer("ANI Cursor Animation");

        var layer = controller.layers[0];
        layer.defaultWeight = 1f;
        layer.blendingMode = AnimatorLayerBlendingMode.Override;
        var stateMachine = layer.stateMachine;
        var states = new Dictionary<int, AnimatorState>();

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var state = stateMachine.AddState(track.ObjectName,
                new Vector3(320f + (i % 4) * 230f, 40f + (i / 4) * 80f, 0f));
            state.motion = clips[i];
            state.speed = 1f;
            state.writeDefaultValues = false;
            states.Add(track.Value, state);
        }

        if (!states.TryGetValue(defaultValue, out var defaultState)) defaultState = states.Values.First();
        stateMachine.defaultState = defaultState;

        foreach (var track in tracks)
        {
            var transition = stateMachine.AddAnyStateTransition(states[track.Value]);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.offset = 0f;
            transition.exitTime = 0f;
            transition.canTransitionToSelf = false;
            transition.interruptionSource = TransitionInterruptionSource.None;
            transition.orderedInterruption = true;
            transition.AddCondition(AnimatorConditionMode.Equals, track.Value, parameterName);
        }

        controller.layers = new[] { layer };
        EditorUtility.SetDirty(stateMachine);
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static HashSet<ModularAvatarMenuItem> SetExplicitMenuValues(
        GameObject prefabRoot,
        string parameterName,
        int defaultValue,
        IList<CursorTrack> tracks)
    {
        var matchedItems = new HashSet<ModularAvatarMenuItem>();
        var menuItems = prefabRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true);

        foreach (var track in tracks)
        {
            ModularAvatarMenuItem item = null;
            if (!string.IsNullOrWhiteSpace(track.MenuItemPath))
            {
                var menuTransform = prefabRoot.transform.Find(NormalizeBindingPath(track.MenuItemPath));
                if (menuTransform != null) item = menuTransform.GetComponent<ModularAvatarMenuItem>();
            }

            if (item == null)
            {
                item = menuItems.FirstOrDefault(candidate => MenuItemTargetsObject(candidate, track.ObjectName));
            }

            if (item == null)
            {
                // Idempotent rebuild after the legacy ObjectToggle has already
                // been removed: the generated Int value uniquely identifies it.
                item = menuItems.FirstOrDefault(candidate =>
                    candidate.Control != null &&
                    candidate.Control.parameter != null &&
                    string.Equals(
                        candidate.Control.parameter.name, parameterName, StringComparison.Ordinal) &&
                    Mathf.Approximately(candidate.Control.value, track.Value));
            }

            if (item == null)
            {
                Debug.LogWarning("[AniCursorUnityBuilder] MA Menu Item not found for " + track.ObjectName);
                continue;
            }

            if (item.Control == null)
                item.Control = new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control();
            if (item.Control.parameter == null)
                item.Control.parameter =
                    new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.Parameter();

            item.Control.parameter.name = parameterName;
            item.Control.value = track.Value;
            item.automaticValue = false;
            item.isDefault = track.Value == defaultValue;
            EditorUtility.SetDirty(item);
            matchedItems.Add(item);
        }

        return matchedItems;
    }

    private static bool MenuItemTargetsObject(ModularAvatarMenuItem item, string objectName)
    {
        var toggles = item.GetComponents<ModularAvatarObjectToggle>();
        foreach (var toggle in toggles)
        foreach (var toggledObject in toggle.Objects ?? new List<ToggledObject>())
        {
            var reference = toggledObject.Object;
            if (reference == null) continue;
            var path = (reference.referencePath ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (path.EndsWith("/" + objectName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, objectName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void RemoveManagedObjectToggles(
        IEnumerable<ModularAvatarMenuItem> menuItems,
        IEnumerable<CursorTrack> tracks)
    {
        var managedNames = new HashSet<string>(
            tracks.Select(track => track.ObjectName), StringComparer.OrdinalIgnoreCase);

        foreach (var item in menuItems)
        foreach (var toggle in item.GetComponents<ModularAvatarObjectToggle>())
        {
            var retained = (toggle.Objects ?? new List<ToggledObject>())
                .Where(entry => !IsManagedToggleTarget(toggle, entry, managedNames))
                .ToList();
            if (retained.Count == 0)
            {
                UnityEngine.Object.DestroyImmediate(toggle, true);
            }
            else if (retained.Count != toggle.Objects.Count)
            {
                toggle.Objects = retained;
                EditorUtility.SetDirty(toggle);
            }
        }
    }

    private static bool IsManagedToggleTarget(
        ModularAvatarObjectToggle owner,
        ToggledObject entry,
        ISet<string> managedNames)
    {
        var reference = entry.Object;
        if (reference == null) return false;

        var target = reference.Get(owner);
        if (target != null && managedNames.Contains(target.name)) return true;

        var path = (reference.referencePath ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        return managedNames.Any(name =>
            string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
    }

    private static void ConfigureMergeAnimator(GameObject prefabRoot, AnimatorController controller)
    {
        // Multiple independent systems may legitimately put MA Merge Animator
        // components on the same prefab root. Only components already assigned
        // to this generated controller belong to this builder.
        var owned = prefabRoot.GetComponents<ModularAvatarMergeAnimator>()
            .Where(component => component.animator == controller)
            .ToList();
        if (owned.Count == 0)
            owned.Add(prefabRoot.AddComponent<ModularAvatarMergeAnimator>());

        var merge = owned[0];
        foreach (var duplicate in owned.Skip(1))
            UnityEngine.Object.DestroyImmediate(duplicate, true);

        merge.animator = controller;
        merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
        merge.deleteAttachedAnimator = true;
        merge.pathMode = MergeAnimatorPathMode.Relative;
        merge.matchAvatarWriteDefaults = true;
        merge.relativePathRoot = new AvatarObjectReference();
        merge.layerPriority = 0;
        merge.mergeAnimatorMode = MergeAnimatorMode.Append;
        EditorUtility.SetDirty(merge);
    }

    private static void UpsertSelectionParameter(
        GameObject prefabRoot,
        string parameterName,
        int defaultValue)
    {
        var parameters = prefabRoot.GetComponent<ModularAvatarParameters>();
        if (parameters == null) parameters = prefabRoot.AddComponent<ModularAvatarParameters>();
        if (parameters.parameters == null) parameters.parameters = new List<ParameterConfig>();

        var matches = new List<int>();
        for (var index = 0; index < parameters.parameters.Count; index++)
        {
            var config = parameters.parameters[index];
            if (!config.isPrefix && string.Equals(
                    config.nameOrPrefix, parameterName, StringComparison.Ordinal))
                matches.Add(index);
        }

        var updated = new ParameterConfig
        {
            nameOrPrefix = parameterName,
            remapTo = string.Empty,
            internalParameter = true,
            isPrefix = false,
            syncType = ParameterSyncType.Int,
            localOnly = false,
            defaultValue = defaultValue,
            saved = true,
            hasExplicitDefaultValue = true
        };

        if (matches.Count == 0)
        {
            parameters.parameters.Add(updated);
        }
        else
        {
            parameters.parameters[matches[0]] = updated;
            for (var matchIndex = matches.Count - 1; matchIndex >= 1; matchIndex--)
                parameters.parameters.RemoveAt(matches[matchIndex]);
        }
        EditorUtility.SetDirty(parameters);
    }

    private static JObject FindMatchingCursor(IEnumerable<JObject> cursors, JObject binding)
    {
        var candidates = new[]
        {
            ReadString(binding, null, "cursorId", "cursor_id", "id"),
            ReadString(binding, null, "source", "sourceFile", "source_file"),
            ReadString(binding, null, "name"),
            ReadString(binding, null, "objectName", "object_name")
        }.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();

        foreach (var cursor in cursors)
        {
            var values = new[]
            {
                ReadString(cursor, null, "cursorId", "cursor_id", "id"),
                ReadString(cursor, null, "source", "sourceFile", "source_file"),
                ReadString(cursor, null, "name"),
                ReadString(cursor, null, "objectName", "object_name")
            }.Where(v => !string.IsNullOrWhiteSpace(v));

            if (values.Any(v => candidates.Any(c =>
                    string.Equals(v, c, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(v), Path.GetFileName(c), StringComparison.OrdinalIgnoreCase))))
                return cursor;
        }

        return null;
    }

    private static string InferObjectName(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var name = SanitizeFileName(Path.GetFileNameWithoutExtension(source)).Replace(" ", "_");
        return string.IsNullOrWhiteSpace(name) ? null : "Cursor_" + name;
    }

    private static int DefaultValueForObject(string objectName, int fallback)
    {
        return fallback;
    }

    private static float ReadTimeSeconds(
        JObject obj,
        float fallback,
        float jiffiesPerSecond,
        params string[] ignored)
    {
        var seconds = ReadNullableFloat(obj, "time", "startSeconds", "start_seconds", "timeSeconds",
            "time_seconds", "startTimeSeconds", "start_time_seconds");
        if (seconds.HasValue) return seconds.Value;
        var ms = ReadNullableFloat(obj, "startMs", "start_ms", "timeMs", "time_ms");
        if (ms.HasValue) return ms.Value / 1000f;
        var ticks = ReadNullableFloat(obj,
            "startJiffy", "start_jiffy", "startJiffies", "start_jiffies", "startTick", "start_tick");
        return ticks.HasValue ? ticks.Value / jiffiesPerSecond : fallback;
    }

    private static float ReadDurationSeconds(JObject obj, float jiffiesPerSecond)
    {
        var seconds = ReadNullableFloat(obj, "duration", "durationSeconds", "duration_seconds");
        if (seconds.HasValue) return Mathf.Max(0f, seconds.Value);
        var ms = ReadNullableFloat(obj, "durationMs", "duration_ms");
        if (ms.HasValue) return Mathf.Max(0f, ms.Value / 1000f);
        var ticks = ReadNullableFloat(obj,
            "durationJiffies", "duration_jiffies", "durationTicks", "duration_ticks");
        return ticks.HasValue ? Mathf.Max(0f, ticks.Value / jiffiesPerSecond) : 0f;
    }

    private static void WriteResult(
        string resultFile,
        bool success,
        string message,
        BuildPaths paths,
        Exception exception)
    {
        try
        {
            var result = new JObject
            {
                ["success"] = success,
                ["message"] = message ?? string.Empty,
                ["timestampUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["prefabPath"] = paths?.PrefabAsset ?? string.Empty,
                ["fbxPath"] = paths?.FbxAsset ?? string.Empty,
                ["controllerPath"] = paths?.ControllerAsset ?? string.Empty,
                ["materialPath"] = paths?.MaterialAsset ?? string.Empty,
                ["atlasAssetPath"] = paths?.AtlasAsset ?? string.Empty
            };
            if (exception != null) result["exception"] = exception.ToString();

            Directory.CreateDirectory(Path.GetDirectoryName(resultFile) ?? ProjectRoot);
            File.WriteAllText(resultFile, result.ToString(Formatting.Indented));
            if (TryFilePathToAssetPath(resultFile, out var resultAsset))
                AssetDatabase.ImportAsset(resultAsset, ImportAssetOptions.ForceUpdate);
        }
        catch (Exception writeException)
        {
            Debug.LogWarning("[AniCursorUnityBuilder] Could not write BuildResult.json: " + writeException.Message);
        }
    }

    private static void EnsureAssetFolder(string assetFolder)
    {
        if (string.IsNullOrWhiteSpace(assetFolder) || assetFolder == "Assets") return;
        assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
        if (!assetFolder.StartsWith("Assets/", StringComparison.Ordinal))
            throw new ArgumentException("Folder must be under Assets: " + assetFolder);
        if (AssetDatabase.IsValidFolder(assetFolder)) return;

        var parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
        EnsureAssetFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
    }

    private static string ReadPathValue(JObject root, params string[] names)
    {
        var value = ReadString(root, null, names);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var paths = FindObject(root, "paths", "outputs", "outputPaths", "output_paths");
        return ReadString(paths, null, names);
    }

    private static string ReadUnityString(JObject request, string fallback, params string[] names)
    {
        var value = ReadString(request, null, names);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        return ReadString(FindObject(request, "unity"), fallback, names);
    }

    private static float ReadUnityFloat(JObject request, float fallback, params string[] names)
    {
        var value = ReadNullableFloat(request, names);
        if (value.HasValue) return value.Value;
        return ReadFloat(FindObject(request, "unity"), fallback, names);
    }

    private static string NormalizeStProperty(string value, string textureProperty)
    {
        value = string.IsNullOrWhiteSpace(value)
            ? "material." + textureProperty + "_ST"
            : value.Trim().TrimEnd('.');

        if (!value.StartsWith("material.", StringComparison.Ordinal))
            value = "material." + value;
        if (!value.EndsWith("_ST", StringComparison.Ordinal)) value += "_ST";
        return value;
    }

    private static JToken FindToken(JToken token, params string[] names)
    {
        if (!(token is JObject obj)) return null;
        foreach (var property in obj.Properties())
        foreach (var name in names)
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private static JObject FindObject(JToken token, params string[] names)
    {
        return FindToken(token, names) as JObject;
    }

    private static JArray FindArray(JToken token, params string[] names)
    {
        return FindToken(token, names) as JArray;
    }

    private static string ReadString(JToken token, string fallback, params string[] names)
    {
        var value = FindToken(token, names);
        return value == null || value.Type == JTokenType.Null ? fallback : value.ToString();
    }

    private static bool ReadBool(JToken token, bool fallback, params string[] names)
    {
        var value = FindToken(token, names);
        if (value == null || value.Type == JTokenType.Null) return fallback;
        if (value.Type == JTokenType.Boolean) return value.Value<bool>();
        if (bool.TryParse(value.ToString(), out var result)) return result;
        if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number != 0;
        return fallback;
    }

    private static int ReadInt(JToken token, int fallback, params string[] names)
    {
        return ReadNullableInt(token, names) ?? fallback;
    }

    private static int? ReadNullableInt(JToken token, params string[] names)
    {
        var value = FindToken(token, names);
        if (value == null || value.Type == JTokenType.Null) return null;
        if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;
        if (float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            return Mathf.RoundToInt(f);
        return null;
    }

    private static float ReadFloat(JToken token, float fallback, params string[] names)
    {
        return ReadNullableFloat(token, names) ?? fallback;
    }

    private static float? ReadNullableFloat(JToken token, params string[] names)
    {
        var value = FindToken(token, names);
        if (value == null || value.Type == JTokenType.Null) return null;
        if (float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        return null;
    }

    private static Vector2? ReadVector2(JToken token, params string[] names)
    {
        var value = FindToken(token, names);
        if (value is JArray array && array.Count >= 2)
            return new Vector2(array[0].Value<float>(), array[1].Value<float>());
        if (value is JObject obj)
        {
            var x = ReadNullableFloat(obj, "x", "u", "width", "w");
            var y = ReadNullableFloat(obj, "y", "v", "height", "h");
            if (x.HasValue && y.HasValue) return new Vector2(x.Value, y.Value);
        }
        return null;
    }

    private static string Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException(name + " is required.");
        return value;
    }

    private static string RequireAssetPath(string value, string name)
    {
        value = Required(value, name).Replace('\\', '/');
        if (Path.IsPathRooted(value))
        {
            if (!TryFilePathToAssetPath(value, out value))
                throw new InvalidDataException(name + " must be under this Unity project's Assets folder.");
        }
        if (!value.StartsWith("Assets/", StringComparison.Ordinal) && value != "Assets")
            throw new InvalidDataException(name + " must be an Assets/... path: " + value);
        return value;
    }

    private static string ResolveFilePath(string path, string relativeTo)
    {
        path = Required(path, "path").Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        if (path.StartsWith("Assets" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase))
            return AssetPathToFilePath(path.Replace(Path.DirectorySeparatorChar, '/'));
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(relativeTo ?? ProjectRoot, path));
    }

    private static string AssetPathToFilePath(string assetPath)
    {
        assetPath = RequireAssetPath(assetPath, "assetPath");
        return Path.GetFullPath(Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool TryFilePathToAssetPath(string filePath, out string assetPath)
    {
        var full = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var assetsRoot = Path.GetFullPath(Application.dataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Equals(assetsRoot, StringComparison.OrdinalIgnoreCase))
        {
            assetPath = "Assets";
            return true;
        }
        var prefix = assetsRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            assetPath = null;
            return false;
        }
        assetPath = "Assets/" + full.Substring(prefix.Length).Replace('\\', '/');
        return true;
    }

    private static bool PathsEqual(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBindingPath(string value)
    {
        value = Required(value, "bindingPath").Replace('\\', '/').Trim('/');
        return value;
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string ReadCommandLineValue(IList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
}

}

#endif
