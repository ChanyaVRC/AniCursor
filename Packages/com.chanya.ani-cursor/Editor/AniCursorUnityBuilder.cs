#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
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
    private const string DefaultTextureProperty = "_MainTex";
    private const string DefaultStProperty = "material._MainTex_ST";
    private const string DefaultDisplayPath = "CursorDisplay";
    private const float ClipFrameRate = 60f;

    internal sealed class BuildSpec
    {
        public string ManifestFile;
        public string PrefabAsset;
        public string FbxAsset;
        public string AtlasSourceFile;
        public string AtlasAsset;
        public string MaterialAsset;
        public string ControllerAsset;
        public string ClipsFolder;
        public string IconsFolder;
        public string ParameterName;
        public int DefaultValue;
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
        public string Source;
        public string AssetName;
        public string ClipAssetPath;
        public int Value;
        public List<UvStep> Steps = new List<UvStep>();
    }

    internal static string Build(BuildSpec spec)
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (!File.Exists(spec.ManifestFile))
            throw new FileNotFoundException("Animation manifest was not found.", spec.ManifestFile);

        JObject manifest;
        try
        {
            manifest = JObject.Parse(File.ReadAllText(spec.ManifestFile));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Invalid animation manifest: " + spec.ManifestFile, ex);
        }

        ValidateSpec(spec);
        ValidateManifest(manifest);
        EnsureAssetFolder(Path.GetDirectoryName(spec.AtlasAsset)?.Replace('\\', '/'));
        EnsureAssetFolder(Path.GetDirectoryName(spec.MaterialAsset)?.Replace('\\', '/'));
        EnsureAssetFolder(Path.GetDirectoryName(spec.ControllerAsset)?.Replace('\\', '/'));
        EnsureAssetFolder(spec.ClipsFolder);

        CopyAtlas(spec);
        var texture = AniCursorImportSettings.ConfigureAtlas(spec.AtlasAsset);
        AniCursorImportSettings.ConfigureModel(spec.FbxAsset);
        AniCursorImportSettings.ConfigureMenuIcons(
            ResolveGeneratedIconPaths(manifest, spec.IconsFolder), 32);

        var alphaCutoff = ResolveAlphaCutoff(manifest);
        var material = CreateOrUpdateMaterial(
            spec.MaterialAsset, texture, DefaultTextureProperty, alphaCutoff);

        var tracks = ParseTracks(manifest, spec.ClipsFolder);
        if (tracks.Count == 0) throw new InvalidDataException("Manifest contains no cursor animation tracks.");

        var defaultTrack = tracks.FirstOrDefault(track => track.Value == spec.DefaultValue) ?? tracks[0];
        if (defaultTrack.Steps.Count == 0)
            throw new InvalidDataException("Default cursor track contains no animation steps.");
        SetMaterialDefaultCell(material, DefaultTextureProperty, defaultTrack.Steps[0].Cell);
        var displayMesh = ResolveCursorDisplayMesh(spec.FbxAsset);

        var prefabRoot = PrefabUtility.LoadPrefabContents(spec.PrefabAsset);
        if (prefabRoot == null) throw new InvalidOperationException("Could not load prefab: " + spec.PrefabAsset);

        try
        {
            ConfigureCursorDisplay(prefabRoot, displayMesh, material);

            var clips = new List<AnimationClip>(tracks.Count);
            foreach (var track in tracks)
                clips.Add(CreateOrUpdateClip(track));

            var controller = CreateOrUpdateController(spec.ControllerAsset, spec.ParameterName,
                spec.DefaultValue, tracks, clips);

            ConfigureMergeAnimator(prefabRoot, controller);
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, spec.PrefabAsset);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefabRoot);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        return "Built " + tracks.Count + " looping cursor clips for one shared CursorDisplay, " +
               "controller, cutout material, atlas, and MA Merge Animator. Parameter: " +
               spec.ParameterName + ".";
    }

    private static void ValidateSpec(BuildSpec spec)
    {
        spec.ManifestFile = Path.GetFullPath(Required(spec.ManifestFile, nameof(spec.ManifestFile)));
        spec.AtlasSourceFile = Path.GetFullPath(Required(spec.AtlasSourceFile, nameof(spec.AtlasSourceFile)));
        spec.PrefabAsset = RequireAssetPath(spec.PrefabAsset, nameof(spec.PrefabAsset));
        spec.FbxAsset = RequireAssetPath(spec.FbxAsset, nameof(spec.FbxAsset));
        spec.AtlasAsset = RequireAssetPath(spec.AtlasAsset, nameof(spec.AtlasAsset));
        spec.MaterialAsset = RequireAssetPath(spec.MaterialAsset, nameof(spec.MaterialAsset));
        spec.ControllerAsset = RequireAssetPath(spec.ControllerAsset, nameof(spec.ControllerAsset));
        spec.ClipsFolder = RequireAssetPath(spec.ClipsFolder, nameof(spec.ClipsFolder)).TrimEnd('/');
        spec.IconsFolder = RequireAssetPath(spec.IconsFolder, nameof(spec.IconsFolder)).TrimEnd('/');
        spec.ParameterName = Required(spec.ParameterName, nameof(spec.ParameterName));
        if (spec.DefaultValue < 0 || spec.DefaultValue > 255)
            throw new InvalidDataException("DefaultValue must be between 0 and 255.");
        if (!File.Exists(spec.AtlasSourceFile))
            throw new FileNotFoundException("Atlas PNG was not found.", spec.AtlasSourceFile);
    }

    private static void ValidateManifest(JObject manifest)
    {
        if (manifest.Value<int?>("schema_version") != 3)
            throw new InvalidDataException("Only ANI Cursor manifest schema_version 3 is supported.");
        if (!(manifest["atlas"] is JObject))
            throw new InvalidDataException("Manifest atlas object is required.");
        if (!(manifest["geometry"] is JObject))
            throw new InvalidDataException("Manifest geometry object is required.");
        if (!(manifest["cursors"] is JArray))
            throw new InvalidDataException("Manifest cursors array is required.");
    }

    private static void CopyAtlas(BuildSpec spec)
    {
        var destination = AssetPathToFilePath(spec.AtlasAsset);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ProjectRoot);
        if (!PathsEqual(spec.AtlasSourceFile, destination))
            File.Copy(spec.AtlasSourceFile, destination, true);
    }

    private static IEnumerable<string> ResolveGeneratedIconPaths(JObject manifest, string assetFolder)
    {
        var cursors = manifest["cursors"] as JArray
                      ?? throw new InvalidDataException("Manifest contains no cursors.");
        var folder = assetFolder.Replace('\\', '/').TrimEnd('/');

        foreach (var cursor in cursors.OfType<JObject>())
        {
            var icon = cursor["icon"] as JObject
                       ?? throw new InvalidDataException("Manifest cursor icon object is required.");
            var relativePath = icon.Value<string>("file");
            var fileName = Path.GetFileName(
                (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidDataException("Manifest cursor icon path is empty.");
            yield return folder + "/" + fileName;
        }
    }

    private static Material CreateOrUpdateMaterial(
        string materialPath,
        Texture2D atlas,
        string textureProperty,
        float alphaCutoff)
    {
        var shader = Shader.Find("Unlit/Transparent Cutout");
        if (shader == null)
            throw new InvalidOperationException("Required shader was not found: Unlit/Transparent Cutout");

        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(shader) { name = Path.GetFileNameWithoutExtension(materialPath) };
            AssetDatabase.CreateAsset(material, materialPath);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
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

    private static float ResolveAlphaCutoff(JObject manifest)
    {
        var geometry = manifest["geometry"] as JObject
                       ?? throw new InvalidDataException("Manifest geometry object is required.");
        var cutoff = geometry.Value<float?>("cutoff")
                     ?? throw new InvalidDataException("Manifest geometry.cutoff is required.");
        return Mathf.Clamp01(cutoff);
    }

    private static List<CursorTrack> ParseTracks(JObject manifest, string clipsFolder)
    {
        var atlas = (JObject)manifest["atlas"];
        var cells = ParseAtlasCells(atlas);
        var cursorArray = (JArray)manifest["cursors"];
        var tracks = cursorArray.OfType<JObject>()
            .Select(cursor => ParseTrack(cursor, cells, clipsFolder))
            .OrderBy(track => track.Value)
            .ToList();

        var usedValues = new HashSet<int>();
        foreach (var track in tracks)
        {
            if (!usedValues.Add(track.Value))
                throw new InvalidDataException("Duplicate cursor selection value: " + track.Value);
            if (track.Steps.Count == 0)
                throw new InvalidDataException("Cursor has no animation steps: " + track.Source);
        }
        return tracks;
    }

    private static CursorTrack ParseTrack(
        JObject cursor,
        IReadOnlyDictionary<string, UvCell> cells,
        string clipsFolder)
    {
        var binding = cursor["binding"] as JObject
                      ?? throw new InvalidDataException("Manifest cursor.binding is required.");
        var source = Required(cursor.Value<string>("source"), "cursor.source");
        var assetName = Required(binding.Value<string>("asset_name"), "cursor.binding.asset_name");
        var value = binding.Value<int?>("parameter_value")
                    ?? throw new InvalidDataException("cursor.binding.parameter_value is required.");
        if (value < 0 || value > 255)
            throw new InvalidDataException("Cursor parameter value must be between 0 and 255: " + value);

        var track = new CursorTrack
        {
            Source = source,
            AssetName = assetName,
            ClipAssetPath = clipsFolder + "/" + SanitizeFileName(assetName) + ".anim",
            Value = value
        };
        ParseSteps(cursor, cells, track.Steps);
        return track;
    }

    private static Dictionary<string, UvCell> ParseAtlasCells(JObject atlas)
    {
        var frames = atlas["frames"] as JArray
                     ?? throw new InvalidDataException("Manifest atlas.frames is required.");
        var result = new Dictionary<string, UvCell>(StringComparer.Ordinal);
        foreach (var frame in frames.OfType<JObject>())
        {
            var frameId = Required(frame.Value<string>("frame_id"), "atlas.frames[].frame_id");
            var st = frame["main_tex_st"] as JArray;
            if (st == null || st.Count != 4)
                throw new InvalidDataException("atlas.frames[].main_tex_st must contain four numbers.");
            if (result.ContainsKey(frameId))
                throw new InvalidDataException("Duplicate atlas frame_id: " + frameId);
            result.Add(frameId, new UvCell
            {
                Scale = new Vector2(st[0].Value<float>(), st[1].Value<float>()),
                Offset = new Vector2(st[2].Value<float>(), st[3].Value<float>())
            });
        }
        return result;
    }

    private static void ParseSteps(
        JObject cursor,
        IReadOnlyDictionary<string, UvCell> cells,
        ICollection<UvStep> output)
    {
        var steps = cursor["steps"] as JArray
                    ?? throw new InvalidDataException("Manifest cursor.steps is required.");
        foreach (var step in steps.OfType<JObject>())
        {
            var frameId = Required(step.Value<string>("frame_id"), "cursor.steps[].frame_id");
            if (!cells.TryGetValue(frameId, out var cell))
                throw new InvalidDataException("Animation step references unknown frame_id: " + frameId);
            var time = step.Value<float?>("start_time_seconds")
                       ?? throw new InvalidDataException("cursor.steps[].start_time_seconds is required.");
            var duration = step.Value<float?>("duration_seconds")
                           ?? throw new InvalidDataException("cursor.steps[].duration_seconds is required.");
            if (time < 0f || duration <= 0f)
                throw new InvalidDataException("Animation step time must be non-negative and duration positive.");
            output.Add(new UvStep { Time = time, Duration = duration, Cell = cell });
        }
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
            throw new InvalidDataException(
                "Cursor FBX Mesh must be named CursorDisplay; found " + mesh.name + ".");

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

    private static void ConfigureCursorDisplay(
        GameObject prefabRoot,
        Mesh displayMesh,
        Material material)
    {
        var display = prefabRoot.transform.Find(DefaultDisplayPath);
        if (display == null || display.parent != prefabRoot.transform)
            throw new InvalidDataException("MA prefab must contain direct child CursorDisplay.");
        if (display.localPosition.sqrMagnitude > 0.0000001f ||
            Quaternion.Angle(display.localRotation, Quaternion.identity) > 0.001f ||
            (display.localScale - Vector3.one).sqrMagnitude > 0.0000001f)
            throw new InvalidDataException("CursorDisplay must use identity local transform.");

        var filter = display.GetComponent<MeshFilter>()
                     ?? throw new InvalidDataException("CursorDisplay requires MeshFilter.");
        var renderer = display.GetComponent<MeshRenderer>()
                       ?? throw new InvalidDataException("CursorDisplay requires MeshRenderer.");
        var allRenderers = prefabRoot.GetComponentsInChildren<Renderer>(true);
        if (allRenderers.Length != 1 || allRenderers[0] != renderer)
            throw new InvalidDataException("MA prefab must contain exactly one CursorDisplay renderer.");

        var boneProxy = display.GetComponent<ModularAvatarBoneProxy>()
                        ?? throw new InvalidDataException("CursorDisplay requires MA Bone Proxy.");
        if (boneProxy.boneReference != HumanBodyBones.RightHand ||
            boneProxy.attachmentMode != BoneProxyAttachmentMode.AsChildKeepWorldPose ||
            boneProxy.matchScale || !string.IsNullOrEmpty(boneProxy.subPath))
            throw new InvalidDataException(
                "CursorDisplay Bone Proxy must target RightHand with AsChildKeepWorldPose.");

        filter.sharedMesh = displayMesh;
        renderer.sharedMaterials = new[] { material };
        EditorUtility.SetDirty(filter);
        EditorUtility.SetDirty(renderer);
    }

    private static AnimationClip CreateOrUpdateClip(CursorTrack track)
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

        clip.frameRate = ClipFrameRate;
        clip.wrapMode = WrapMode.Loop;

        var duration = track.Steps.Max(s => s.Time + Mathf.Max(0f, s.Duration));
        if (duration <= 0f) duration = track.Steps.Max(s => s.Time) + (1f / 60f);

        // CursorDisplay uses normalized 0..1 frame UVs. Every state therefore
        // writes the absolute atlas cell to the same renderer; no track-local
        // base cell and no mesh/object activation curve is involved.
        SetSteppedCurve(clip, DefaultStProperty + ".x", track.Steps,
            step => step.Cell.Scale.x, duration);
        SetSteppedCurve(clip, DefaultStProperty + ".y", track.Steps,
            step => step.Cell.Scale.y, duration);
        SetSteppedCurve(clip, DefaultStProperty + ".z", track.Steps,
            step => step.Cell.Offset.x, duration);
        SetSteppedCurve(clip, DefaultStProperty + ".w", track.Steps,
            step => step.Cell.Offset.y, duration);

        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = true;
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
        string property,
        IList<UvStep> steps,
        Func<UvStep, float> value,
        float duration)
    {
        var keys = new List<Keyframe>(steps.Count + 2);
        if (steps[0].Time > 0f) keys.Add(new Keyframe(0f, value(steps[0])));
        foreach (var step in steps) keys.Add(new Keyframe(step.Time, value(step)));

        var finalValue = value(steps[0]);
        if (keys.Count == 0 || duration > keys[keys.Count - 1].time + 0.000001f)
            keys.Add(new Keyframe(duration, finalValue));
        else
            keys[keys.Count - 1] = new Keyframe(duration, finalValue);

        var curve = new AnimationCurve(keys.ToArray())
        {
            preWrapMode = WrapMode.ClampForever,
            postWrapMode = WrapMode.Loop
        };
        for (var i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
        }

        var binding = EditorCurveBinding.FloatCurve(
            DefaultDisplayPath, typeof(MeshRenderer), property);
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
            var state = stateMachine.AddState(track.AssetName,
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

    private static void ConfigureMergeAnimator(GameObject prefabRoot, AnimatorController controller)
    {
        if (prefabRoot.GetComponents<ModularAvatarMergeAnimator>().Length != 0)
            throw new InvalidDataException("Fresh MA prefab unexpectedly contains Merge Animator.");
        var merge = prefabRoot.AddComponent<ModularAvatarMergeAnimator>();
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

    private static string Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException(name + " is required.");
        return value;
    }

    private static string RequireAssetPath(string value, string name)
    {
        value = Required(value, name).Replace('\\', '/');
        if (Path.IsPathRooted(value))
            throw new InvalidDataException(name + " must be an Assets/... path.");
        if (!value.StartsWith("Assets/", StringComparison.Ordinal) && value != "Assets")
            throw new InvalidDataException(name + " must be an Assets/... path: " + value);
        return value;
    }

    private static string AssetPathToFilePath(string assetPath)
    {
        assetPath = RequireAssetPath(assetPath, "assetPath");
        return Path.GetFullPath(Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool PathsEqual(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }

    private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
}

}

#endif
