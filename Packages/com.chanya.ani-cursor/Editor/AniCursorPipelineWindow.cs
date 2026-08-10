#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using nadena.dev.modular_avatar.core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Chanya.AniCursor
{

/// <summary>
/// Reusable one-click ANI -> Logo Tracer FBX -> animated Modular Avatar prefab pipeline.
///
/// The Python and Blender stages remain ordinary command-line tools so this window can
/// be reused with any folder of ANI files.  Unity owns importer settings, menu wiring,
/// animation generation, and MA integration.
/// </summary>
public sealed class AniCursorPipelineWindow : EditorWindow
{
    private const string DefaultOutputBase = "Assets/AniCursorGenerated";
    private const string DefaultOutputRoot = DefaultOutputBase + "/AniCursor";
    private const string PrepareScriptRelative = "Tools~/Pipeline/prepare_ani_cursor.py";
    private const string BlenderScriptRelative = "Tools~/Pipeline/build_ani_cursor_blender.py";
    private const string CursorDisplayName = "CursorDisplay";

    [SerializeField] private string _sourceFolder = "";
    [SerializeField] private string _outputRoot = DefaultOutputRoot;
    [SerializeField] private string _packageStem = "AniCursor";
    [SerializeField] private string _menuLabel = "ANI Cursor";
    [SerializeField] private string _parameterName = "AniCursor_Select";
    [SerializeField] private int _defaultValue;
    [SerializeField] private string _presetPath = "";

    [SerializeField] private string _pythonPath = "";
    [SerializeField] private string _aniExtractRoot = "";
    [SerializeField] private string _blenderPath = "";
    [SerializeField] private string _logoTracerPath = "";
    [SerializeField] private int _frameSize = 32;
    [SerializeField] private int _alphaThreshold = 128;
    [SerializeField] private float _worldSize = 0.12f;
    [SerializeField] private float _thickness = 0.004f;

    [SerializeField] private bool _updateExistingPrefab = true;
    [SerializeField] private bool _useCustomOutputPaths;
    [SerializeField] private string _prefabAssetPath = DefaultOutputRoot + "/AniCursor_MA.prefab";
    [SerializeField] private string _fbxAssetPath =
        DefaultOutputRoot + "/AniCursorMeshes.fbx";
    [SerializeField] private string _atlasAssetPath =
        DefaultOutputRoot + "/Textures/AniCursorAtlas.png";
    [SerializeField] private string _materialAssetPath =
        DefaultOutputRoot + "/AniCursor.mat";
    [SerializeField] private string _controllerAssetPath =
        DefaultOutputRoot + "/Animations/AniCursor_Animation.controller";
    [SerializeField] private string _clipsAssetFolder =
        DefaultOutputRoot + "/Animations/Clips";
    [SerializeField] private string _iconsAssetFolder =
        DefaultOutputRoot + "/Textures/Icons";

    [SerializeField] private bool _showToolPaths;
    [SerializeField] private bool _showOutputPaths;
    [SerializeField] private bool _showAdvanced;
    [SerializeField] private string _autoConfiguredSource = "";
    [SerializeField] private Vector2 _scroll;
    [SerializeField] private string _status = "準備完了";

    private sealed class CursorDefinition
    {
        public string AssetName;
        public string Label;
        public string IconFile;
        public int MenuOrder;
        public int Value;
    }

    private sealed class OutputTransaction
    {
        private sealed class Entry
        {
            public string Original;
            public string Backup;
            public bool Existed;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly HashSet<string> _captured =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _committed;

        public string BackupRoot { get; }

        public OutputTransaction(string backupRoot)
        {
            BackupRoot = backupRoot;
            Directory.CreateDirectory(BackupRoot);
        }

        public void CaptureAsset(string assetPath)
        {
            var absolute = AssetPathToAbsolute(assetPath);
            CaptureFile(absolute);
            CaptureFile(absolute + ".meta");
        }

        private void CaptureFile(string file)
        {
            file = Path.GetFullPath(file);
            if (!_captured.Add(file)) return;

            var entry = new Entry
            {
                Original = file,
                Existed = File.Exists(file),
                Backup = Path.Combine(BackupRoot,
                    _entries.Count.ToString("D4", CultureInfo.InvariantCulture) + ".bak")
            };
            if (entry.Existed) File.Copy(entry.Original, entry.Backup, true);
            _entries.Add(entry);
        }

        public void Commit()
        {
            _committed = true;
        }

        public void Rollback()
        {
            if (_committed) return;

            AssetDatabase.StartAssetEditing();
            try
            {
                // Files are restored before .meta files because each asset was
                // captured in that order. AssetDatabase refresh occurs only after
                // the complete pair has returned to its pre-build state.
                foreach (var entry in _entries)
                {
                    if (entry.Existed)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(entry.Original) ?? ProjectRoot);
                        File.Copy(entry.Backup, entry.Original, true);
                    }
                    else if (File.Exists(entry.Original))
                    {
                        File.Delete(entry.Original);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
    }

    [MenuItem("Tools/ANI Cursor/ANI to Modular Avatar")]
    private static void OpenWindow()
    {
        var window = GetWindow<AniCursorPipelineWindow>("ANI Cursor");
        window.minSize = new Vector2(560f, 430f);
        window.Show();
    }

    /// <summary>Headless entry point used by release and clean-project tests.</summary>
    public static void BuildFromCommandLine()
    {
        var arguments = Environment.GetCommandLineArgs();
        var source = ReadCommandLineValue(arguments, "-aniCursorSource");
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("-aniCursorSource <folder> is required.");

        var window = CreateInstance<AniCursorPipelineWindow>();
        try
        {
            window._sourceFolder = source;
            window.ConfigureFromSource(true);

            var output = ReadCommandLineValue(arguments, "-aniCursorOutputRoot");
            var stem = ReadCommandLineValue(arguments, "-aniCursorStem");
            var preset = ReadCommandLineValue(arguments, "-aniCursorPreset");
            if (!string.IsNullOrWhiteSpace(output)) window._outputRoot = output;
            if (!string.IsNullOrWhiteSpace(stem)) window._packageStem = stem;
            if (!string.IsNullOrWhiteSpace(preset)) window._presetPath = preset;
            window.ApplyDerivedOutputPaths();

            var summary = window.BuildPipeline();
            Debug.Log("[AniCursorPipeline] " + summary);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Object.DestroyImmediate(window);
        }
    }

    private void OnEnable()
    {
        DetectAndRememberTools(false);
        ConfigureFromSource(false);
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("ANI Cursor → Modular Avatar", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "ANI フォルダを選んで生成するだけです。32×32 atlas、厚み付きFBX、" +
            "AnimationClip、FX、Modular Avatar prefabをまとめて作成します。",
            MessageType.Info);

        EditorGUILayout.Space(6f);
        DrawAniDropZone();
        var previousSource = _sourceFolder;
        DrawPathField("ANI folder", ref _sourceFolder, true, false);
        if (!PathsEqualSafe(previousSource, _sourceFolder)) ConfigureFromSource(true);

        var aniCount = CountAniFiles(_sourceFolder);
        if (aniCount > 0)
        {
            EditorGUILayout.HelpBox(
                aniCount + " 個の ANI を検出\n出力: " + _prefabAssetPath,
                MessageType.None);
        }
        else if (!string.IsNullOrWhiteSpace(_sourceFolder))
        {
            EditorGUILayout.HelpBox("選択したフォルダに *.ani がありません。", MessageType.Warning);
        }

        var toolPaths = CurrentToolPaths();
        var missingTools = AniCursorEnvironment.DescribeMissing(toolPaths);
        if (!string.IsNullOrWhiteSpace(missingTools))
        {
            EditorGUILayout.HelpBox(missingTools, MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("外部ツールを自動検出")) DetectAndRememberTools(true);
                if (!toolPaths.HasLogoTracer && GUILayout.Button("LogoTracer ZIPを選択"))
                {
                    var selected = EditorUtility.OpenFilePanel("LogoTracer 1.21 ZIP", ResolvePickerStart(
                        _logoTracerPath), "zip");
                    if (!string.IsNullOrWhiteSpace(selected))
                    {
                        _logoTracerPath = selected;
                        SaveToolPaths();
                    }
                }
            }
        }

        EditorGUILayout.Space(12f);
        var canBuild = !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                       aniCount > 0 && CurrentToolPaths().IsComplete;
        using (new EditorGUI.DisabledScope(!canBuild))
        {
            if (GUILayout.Button("MA Prefabを生成", GUILayout.Height(46f)))
            {
                try
                {
                    _status = BuildPipeline();
                    ShowNotification(new GUIContent("ANI cursor build complete"));
                }
                catch (Exception exception)
                {
                    _status = "失敗: " + exception.Message;
                    Debug.LogException(exception);
                    EditorUtility.DisplayDialog("ANI cursor build failed", exception.Message, "OK");
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }

        _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "詳細設定", true);
        if (_showAdvanced) DrawAdvancedSettings();

        EditorGUILayout.Space(8f);
        EditorGUILayout.HelpBox(_status, MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    private void DrawAdvancedSettings()
    {
        using (new EditorGUI.IndentLevelScope())
        {
            DrawPathField("Output under Assets", ref _outputRoot, true, true);
            _packageStem = EditorGUILayout.TextField("Package stem", _packageStem);
            _menuLabel = EditorGUILayout.TextField("Menu label", _menuLabel);
            _parameterName = EditorGUILayout.TextField("Shared int parameter", _parameterName);
            _defaultValue = EditorGUILayout.IntField("Default value", _defaultValue);
            DrawPathField("Optional preset JSON", ref _presetPath, false, false, "json");

            _alphaThreshold = EditorGUILayout.IntSlider("Alpha threshold", _alphaThreshold, 1, 255);
            _worldSize = EditorGUILayout.FloatField("Width / height (m)", _worldSize);
            _thickness = EditorGUILayout.FloatField("Thickness (m)", _thickness);

            _updateExistingPrefab = EditorGUILayout.ToggleLeft(
                "既存 prefab を更新", _updateExistingPrefab);
            _useCustomOutputPaths = EditorGUILayout.ToggleLeft(
                "出力パスを個別指定", _useCustomOutputPaths);
            if (!_useCustomOutputPaths) ApplyDerivedOutputPaths();

            _showOutputPaths = EditorGUILayout.Foldout(_showOutputPaths, "出力パス", true);
            if (_showOutputPaths)
            {
                using (new EditorGUI.IndentLevelScope())
                using (new EditorGUI.DisabledScope(!_useCustomOutputPaths))
                {
                    _prefabAssetPath = EditorGUILayout.TextField("Prefab", _prefabAssetPath);
                    _fbxAssetPath = EditorGUILayout.TextField("FBX", _fbxAssetPath);
                    _atlasAssetPath = EditorGUILayout.TextField("Atlas PNG", _atlasAssetPath);
                    _materialAssetPath = EditorGUILayout.TextField("Material", _materialAssetPath);
                    _controllerAssetPath = EditorGUILayout.TextField("FX controller", _controllerAssetPath);
                    _clipsAssetFolder = EditorGUILayout.TextField("Animation clips", _clipsAssetFolder);
                    _iconsAssetFolder = EditorGUILayout.TextField("Menu icons", _iconsAssetFolder);
                }
            }

            _showToolPaths = EditorGUILayout.Foldout(_showToolPaths, "外部ツール", true);
            if (_showToolPaths)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawPathField("Python", ref _pythonPath, false, false, "exe");
                    DrawPathField("ani-extract root", ref _aniExtractRoot, true, false);
                    DrawPathField("Blender", ref _blenderPath, false, false, "exe");
                    DrawPathField("LogoTracer zip", ref _logoTracerPath, false, false, "zip");
                    if (GUILayout.Button("この外部ツール設定を保存")) SaveToolPaths();
                }
            }
        }
    }

    private void DrawAniDropZone()
    {
        var rect = GUILayoutUtility.GetRect(0f, 64f, GUILayout.ExpandWidth(true));
        GUI.Box(rect, "ANIフォルダをここへドロップ", EditorStyles.helpBox);
        var current = Event.current;
        if (!rect.Contains(current.mousePosition) ||
            (current.type != EventType.DragUpdated && current.type != EventType.DragPerform)) return;

        var folder = ResolveDraggedAniFolder(DragAndDrop.paths);
        DragAndDrop.visualMode = folder == null ? DragAndDropVisualMode.Rejected : DragAndDropVisualMode.Copy;
        if (current.type == EventType.DragPerform && folder != null)
        {
            DragAndDrop.AcceptDrag();
            _sourceFolder = folder;
            ConfigureFromSource(true);
            GUI.FocusControl(null);
        }
        current.Use();
    }

    private static string ResolveDraggedAniFolder(IEnumerable<string> paths)
    {
        var resolved = new List<string>();
        foreach (var value in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var full = Path.GetFullPath(value);
            if (Directory.Exists(full)) resolved.Add(full);
            else if (File.Exists(full) && string.Equals(Path.GetExtension(full), ".ani",
                         StringComparison.OrdinalIgnoreCase))
                resolved.Add(Path.GetDirectoryName(full));
        }
        return resolved.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? resolved.First(value => !string.IsNullOrWhiteSpace(value))
            : null;
    }

    private void ConfigureFromSource(bool force)
    {
        if (!Directory.Exists(_sourceFolder)) return;
        var full = Path.GetFullPath(_sourceFolder).TrimEnd('\\', '/');
        if (!force && PathsEqualSafe(full, _autoConfiguredSource)) return;

        var folderName = Path.GetFileName(full);
        var stem = SafeFileStem(folderName, "AniCursor");
        _packageStem = stem;
        _menuLabel = string.IsNullOrWhiteSpace(folderName) ? "ANI Cursor" : folderName;
        _parameterName = "AniCursor_" + StableSourceId(full);
        _outputRoot = DefaultOutputBase + "/" + stem;
        _presetPath = File.Exists(Path.Combine(full, "AniCursorPreset.json"))
            ? Path.Combine(full, "AniCursorPreset.json")
            : string.Empty;
        _defaultValue = 0;
        _updateExistingPrefab = true;
        _useCustomOutputPaths = false;
        _autoConfiguredSource = full;
        ApplyDerivedOutputPaths();
    }

    private static string StableSourceId(string sourceFolder)
    {
        unchecked
        {
            uint hash = 2166136261;
            var files = Directory.EnumerateFiles(sourceFolder, "*.ani", SearchOption.AllDirectories)
                .Select(path => Path.GetFullPath(path).Substring(sourceFolder.Length).TrimStart('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var value in files)
            foreach (var character in value.ToUpperInvariant())
            {
                hash ^= character;
                hash *= 16777619;
            }
            return hash.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    private static int CountAniFiles(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.ani", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private AniCursorToolPaths CurrentToolPaths()
    {
        return new AniCursorToolPaths
        {
            PythonPath = _pythonPath,
            AniExtractRoot = _aniExtractRoot,
            BlenderPath = _blenderPath,
            LogoTracerPath = _logoTracerPath
        };
    }

    private void DetectAndRememberTools(bool notify)
    {
        var detected = AniCursorEnvironment.LoadAndDetect(CurrentToolPaths());
        _pythonPath = detected.PythonPath;
        _aniExtractRoot = detected.AniExtractRoot;
        _blenderPath = detected.BlenderPath;
        _logoTracerPath = detected.LogoTracerPath;
        AniCursorEnvironment.Save(detected);
        if (notify)
        {
            _status = detected.IsComplete
                ? "外部ツールをすべて検出しました。"
                : AniCursorEnvironment.DescribeMissing(detected);
            Repaint();
        }
    }

    private void SaveToolPaths()
    {
        AniCursorEnvironment.Save(CurrentToolPaths());
        _status = "外部ツール設定を保存しました。";
    }

    private static bool PathsEqualSafe(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left ?? string.Empty).TrimEnd('\\', '/'),
                Path.GetFullPath(right ?? string.Empty).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void DrawPathField(
        string label,
        ref string value,
        bool folder,
        bool mustBeInAssets,
        string extension = "")
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            value = EditorGUILayout.TextField(label, value);
            if (!GUILayout.Button("...", GUILayout.Width(34f))) return;

            var start = ResolvePickerStart(value);
            var selected = folder
                ? EditorUtility.OpenFolderPanel(label, start, string.Empty)
                : EditorUtility.OpenFilePanel(label, start, extension);
            if (string.IsNullOrWhiteSpace(selected)) return;

            if (mustBeInAssets)
            {
                if (!TryAbsoluteToAssetPath(selected, out var assetPath))
                {
                    EditorUtility.DisplayDialog("Assets folder required",
                        "出力先は、この Unity project の Assets 以下を選択してください。", "OK");
                    return;
                }
                value = assetPath;
            }
            else
            {
                value = selected.Replace('/', Path.DirectorySeparatorChar);
            }
        }
    }

    private void ApplyDerivedOutputPaths()
    {
        var root = NormalizeAssetPath(_outputRoot).TrimEnd('/');
        var stem = SafeFileStem(_packageStem, "AniCursor");
        _prefabAssetPath = root + "/" + stem + "_MA.prefab";
        _fbxAssetPath = root + "/" + stem + "Meshes.fbx";
        _atlasAssetPath = root + "/Textures/" + stem + "Atlas.png";
        _materialAssetPath = root + "/" + stem + ".mat";
        _controllerAssetPath = root + "/Animations/" + stem + "_Animation.controller";
        _clipsAssetFolder = root + "/Animations/" + stem + "_Clips";
        _iconsAssetFolder = root + "/Textures/" + stem + "_Icons";
    }

    private string BuildPipeline()
    {
        if (!_useCustomOutputPaths) ApplyDerivedOutputPaths();
        DetectAndRememberTools(false);
        ValidateSettings();
        SaveToolPaths();

        var prefabFileExists = File.Exists(AssetPathToAbsolute(_prefabAssetPath));
        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(_prefabAssetPath);
        var prefabExists = prefabAsset != null;
        if (prefabFileExists && !prefabExists)
            throw new InvalidOperationException(
                "Prefab file exists but Unity could not import it: " + _prefabAssetPath);
        if (!_updateExistingPrefab && prefabExists)
        {
            throw new InvalidOperationException(
                "既存 prefab の上書きは禁止されています。Package stem を変更するか、" +
                "「既存 prefab を更新」を ON にしてください: " + _prefabAssetPath);
        }

        var projectRoot = ProjectRoot;
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var stagingRoot = Path.Combine(projectRoot, "Library", "AniCursorPipeline", timestamp);
        var preparedRoot = Path.Combine(stagingRoot, "Prepared");
        Directory.CreateDirectory(stagingRoot);

        var prepareScript = ResolveBundledPipelineFile(PrepareScriptRelative);
        var blenderScript = ResolveBundledPipelineFile(BlenderScriptRelative);
        var presetFile = ResolveOptionalFile(_presetPath);

        EditorUtility.DisplayProgressBar("ANI Cursor", "ANI を 32x32 PNG に抽出中…", 0.1f);
        var prepareArguments = new List<string>
        {
            prepareScript,
            Path.GetFullPath(_sourceFolder),
            "--workspace", stagingRoot,
            "--output", preparedRoot,
            "--ani-extract-root", Path.GetFullPath(_aniExtractRoot),
            "--size", _frameSize.ToString(CultureInfo.InvariantCulture),
            "--atlas-size", "auto",
            "--alpha-threshold", _alphaThreshold.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(presetFile))
        {
            prepareArguments.Add("--preset");
            prepareArguments.Add(presetFile);
        }
        RunProcess(_pythonPath, prepareArguments, projectRoot, 5 * 60 * 1000, "ani-extract");

        var manifestFile = Path.Combine(preparedRoot, "manifest.json");
        var manifest = JObject.Parse(File.ReadAllText(manifestFile));
        if (manifest.Value<int?>("schema_version") != 3)
            throw new InvalidDataException("Only ANI Cursor manifest schema_version 3 is supported.");
        var geometry = manifest["geometry"] as JObject
                       ?? throw new InvalidDataException("Manifest geometry object is required.");
        geometry["world_size_m"] = _worldSize;
        geometry["thickness_m"] = _thickness;
        File.WriteAllText(manifestFile, manifest.ToString(Formatting.Indented));
        var definitions = ParseCursorDefinitions(manifest);
        ValidateManifestDefinitions(definitions, manifest, preparedRoot, _defaultValue);

        var stagingFbx = Path.Combine(stagingRoot, SafeFileStem(_packageStem, "AniCursor") + "Meshes.fbx");
        var blenderReport = Path.Combine(stagingRoot, "blender_report.json");
        EditorUtility.DisplayProgressBar("ANI Cursor", "Logo Tracer で一枚板 FBX を生成中…", 0.35f);
        RunProcess(
            _blenderPath,
            new[]
            {
                "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
                "--python", blenderScript, "--", manifestFile, _logoTracerPath, stagingFbx, blenderReport
            },
            projectRoot,
            15 * 60 * 1000,
            "Blender / Logo Tracer");

        EditorUtility.DisplayProgressBar("ANI Cursor", "生成 FBX と prefab binding を事前検証中…", 0.58f);
        PreflightGeneratedFbx(stagingFbx);

        var transaction = CreateOutputTransaction(stagingRoot, preparedRoot, definitions);
        try
        {
            EditorUtility.DisplayProgressBar("ANI Cursor", "FBX・atlas・menu icon を Unity に取り込み中…", 0.68f);
            CopyGeneratedAssets(preparedRoot, stagingFbx);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AniCursorImportSettings.ConfigureModel(_fbxAssetPath);

            // The output prefab is tool-owned. Rebuilding its contents keeps the
            // prefab GUID while reconciling added, removed, or renamed ANI tracks.
            CreateGenericModularAvatarPrefab(
                manifest,
                _prefabAssetPath,
                _fbxAssetPath,
                _iconsAssetFolder,
                _menuLabel,
                _parameterName,
                _defaultValue);

            EditorUtility.DisplayProgressBar("ANI Cursor", "AnimationClip・FX・MA Merge Animator を生成中…", 0.86f);
            var unitySummary = AniCursorUnityBuilder.Build(new AniCursorUnityBuilder.BuildSpec
            {
                ManifestFile = manifestFile,
                PrefabAsset = _prefabAssetPath,
                FbxAsset = _fbxAssetPath,
                AtlasSourceFile = Path.Combine(preparedRoot, "atlas.png"),
                AtlasAsset = _atlasAssetPath,
                MaterialAsset = _materialAssetPath,
                ControllerAsset = _controllerAssetPath,
                ClipsFolder = _clipsAssetFolder,
                IconsFolder = _iconsAssetFolder,
                ParameterName = _parameterName,
                DefaultValue = _defaultValue
            });
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var count = manifest.Value<int>("cursor_count");
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(_prefabAssetPath);
            EditorGUIUtility.PingObject(Selection.activeObject);
            transaction.Commit();
            return count + " 個の ANI を生成完了。" + unitySummary + "\nPrefab: " + _prefabAssetPath;
        }
        catch (Exception buildException)
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception rollbackException)
            {
                Debug.LogException(rollbackException);
                throw new AggregateException(
                    "Build failed and rollback also failed. Backup: " + transaction.BackupRoot,
                    buildException,
                    rollbackException);
            }
            throw;
        }
    }

    private void CopyGeneratedAssets(string preparedRoot, string stagingFbx)
    {
        EnsureAssetParent(_fbxAssetPath);
        EnsureAssetFolder(_iconsAssetFolder);

        File.Copy(stagingFbx, AssetPathToAbsolute(_fbxAssetPath), true);

        var sourceIcons = Path.Combine(preparedRoot, "icons");
        foreach (var source in Directory.EnumerateFiles(sourceIcons, "*.png", SearchOption.TopDirectoryOnly))
        {
            var destinationAsset = NormalizeAssetPath(_iconsAssetFolder).TrimEnd('/') + "/" + Path.GetFileName(source);
            File.Copy(source, AssetPathToAbsolute(destinationAsset), true);
        }
    }

    private OutputTransaction CreateOutputTransaction(
        string stagingRoot,
        string preparedRoot,
        IEnumerable<CursorDefinition> definitions)
    {
        var transaction = new OutputTransaction(Path.Combine(stagingRoot, "RollbackBackup"));
        transaction.CaptureAsset(_prefabAssetPath);
        transaction.CaptureAsset(_fbxAssetPath);
        transaction.CaptureAsset(_atlasAssetPath);
        transaction.CaptureAsset(_materialAssetPath);
        transaction.CaptureAsset(_controllerAssetPath);

        foreach (var source in Directory.EnumerateFiles(
                     Path.Combine(preparedRoot, "icons"), "*.png", SearchOption.TopDirectoryOnly))
        {
            transaction.CaptureAsset(
                NormalizeAssetPath(_iconsAssetFolder).TrimEnd('/') + "/" + Path.GetFileName(source));
        }

        foreach (var definition in definitions)
        {
            var clipPath = NormalizeAssetPath(_clipsAssetFolder).TrimEnd('/') + "/" +
                           SafeFileStem(definition.AssetName, "Cursor") + ".anim";
            transaction.CaptureAsset(clipPath);
        }

        return transaction;
    }

    private static void ValidateManifestDefinitions(
        IList<CursorDefinition> definitions,
        JObject manifest,
        string preparedRoot,
        int defaultValue)
    {
        if (definitions.Count == 0)
            throw new InvalidDataException("Manifest contains no cursor definitions.");
        var declaredCount = manifest.Value<int?>("cursor_count")
                            ?? throw new InvalidDataException("Manifest cursor_count is required.");
        if (declaredCount != definitions.Count)
            throw new InvalidDataException(
                "Manifest cursor_count does not match cursors: " + declaredCount + " != " + definitions.Count);

        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new HashSet<int>();
        var preparedFull = Path.GetFullPath(preparedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.AssetName) || !assets.Add(definition.AssetName))
                throw new InvalidDataException("Manifest has an empty or duplicate asset_name: " +
                                               definition.AssetName);
            if (!values.Add(definition.Value))
                throw new InvalidDataException("Manifest has a duplicate parameter value: " + definition.Value);
            if (definition.Value < 0 || definition.Value > 255)
                throw new InvalidDataException("VRChat Int parameter values must be 0..255: " + definition.Value);
            var iconRelative = (definition.IconFile ?? string.Empty)
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var iconFull = Path.GetFullPath(Path.Combine(preparedRoot, iconRelative));
            if (!iconFull.StartsWith(preparedFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(iconFull))
                throw new InvalidDataException("Manifest icon is missing or outside prepared output: " +
                                               definition.IconFile);
        }

        if (!values.Contains(defaultValue))
            throw new InvalidDataException("Default cursor value is not present in manifest: " + defaultValue);
        if (!File.Exists(Path.Combine(preparedRoot, "atlas.png")))
            throw new FileNotFoundException("Prepared atlas is missing.", Path.Combine(preparedRoot, "atlas.png"));
    }

    private static void PreflightGeneratedFbx(string stagingFbx)
    {
        if (!File.Exists(stagingFbx))
            throw new FileNotFoundException("Blender did not produce an FBX.", stagingFbx);

        EnsureAssetFolder(DefaultOutputBase);
        var temporaryAsset = AssetDatabase.GenerateUniqueAssetPath(
            DefaultOutputBase + "/__AniCursorPreflight_" + Guid.NewGuid().ToString("N") + ".fbx");
        var temporaryFile = AssetPathToAbsolute(temporaryAsset);
        File.Copy(stagingFbx, temporaryFile, false);

        try
        {
            AniCursorImportSettings.ConfigureModel(temporaryAsset);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(temporaryAsset);
            if (model == null) throw new InvalidDataException("Generated FBX has no model root.");
            var meshAssets = AssetDatabase.LoadAllAssetsAtPath(temporaryAsset).OfType<Mesh>().ToList();
            if (meshAssets.Count != 1)
                throw new InvalidDataException(
                    "Generated FBX must contain exactly one Mesh subasset; found " + meshAssets.Count + ".");

            var transforms = EnumerateTransforms(model.transform).ToList();
            var rendererTransforms = transforms
                .Where(transform => transform.GetComponent<Renderer>() != null)
                .ToList();
            if (rendererTransforms.Count != 1)
                throw new InvalidDataException(
                    "Generated FBX must contain exactly one renderer object.");

            var display = rendererTransforms[0];
            if (display != model.transform && display.parent != model.transform)
                throw new InvalidDataException(
                    "Generated FBX renderer must be on the model root or its direct child.");
            var renderer = display.GetComponent<Renderer>();
            Mesh mesh = null;
            var meshFilter = display.GetComponent<MeshFilter>();
            if (meshFilter != null) mesh = meshFilter.sharedMesh;
            var skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null) mesh = skinned.sharedMesh;
            if (mesh == null || mesh != meshAssets[0] || mesh.vertexCount == 0)
                throw new InvalidDataException(
                    "Generated CursorDisplay has no valid Mesh subasset.");
        }
        finally
        {
            if (!AssetDatabase.DeleteAsset(temporaryAsset))
            {
                if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
                if (File.Exists(temporaryFile + ".meta")) File.Delete(temporaryFile + ".meta");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }
    }

    private static IEnumerable<Transform> EnumerateTransforms(Transform root)
    {
        yield return root;
        foreach (Transform child in root)
        foreach (var descendant in EnumerateTransforms(child))
            yield return descendant;
    }

    private static void CreateGenericModularAvatarPrefab(
        JObject manifest,
        string prefabAssetPath,
        string modelAssetPath,
        string iconsAssetFolder,
        string menuLabel,
        string parameterName,
        int defaultValue)
    {
        var definitions = ParseCursorDefinitions(manifest);
        if (definitions.Count == 0)
            throw new InvalidDataException("Manifest contains no cursors.");

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
        if (model == null)
            throw new InvalidOperationException("Imported FBX model was not found: " + modelAssetPath);

        EnsureAssetParent(prefabAssetPath);
        var rootName = Path.GetFileNameWithoutExtension(prefabAssetPath);
        var root = new GameObject(rootName);

        try
        {
            root.AddComponent<ModularAvatarMenuInstaller>();
            ConfigureSubMenu(root.AddComponent<ModularAvatarMenuItem>(),
                string.IsNullOrWhiteSpace(menuLabel) ? rootName : menuLabel,
                null);

            var parameters = root.AddComponent<ModularAvatarParameters>();
            parameters.parameters = new List<ParameterConfig>
            {
                new ParameterConfig
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
                }
            };

            var modelInstance = PrefabUtility.InstantiatePrefab(model) as GameObject;
            if (modelInstance == null)
                throw new InvalidOperationException("Could not instantiate imported FBX: " + modelAssetPath);
            modelInstance.transform.SetParent(root.transform, false);
            PrefabUtility.UnpackPrefabInstance(
                modelInstance,
                PrefabUnpackMode.Completely,
                InteractionMode.AutomatedAction);

            var importedRenderers = modelInstance.GetComponentsInChildren<Renderer>(true);
            if (importedRenderers.Length != 1)
                throw new InvalidOperationException(
                    "Imported FBX must contain exactly one renderer.");
            var display = NormalizeImportedCursorDisplay(
                root, modelInstance, importedRenderers[0]);
            if (display.transform.parent != root.transform)
                throw new InvalidOperationException(
                    "FBX CursorDisplay must be a direct child of the MA prefab root.");
            // Unit and axis conversion are baked into the static FBX mesh.
            // CursorDisplay therefore always has an identity local transform.
            display.transform.localPosition = Vector3.zero;
            display.transform.localRotation = Quaternion.identity;
            display.transform.localScale = Vector3.one;
            display.SetActive(true);
            var boneProxy = display.GetComponent<ModularAvatarBoneProxy>();
            if (boneProxy == null) boneProxy = display.AddComponent<ModularAvatarBoneProxy>();
            boneProxy.boneReference = HumanBodyBones.RightHand;
            boneProxy.subPath = string.Empty;
            boneProxy.attachmentMode = BoneProxyAttachmentMode.AsChildKeepWorldPose;
            boneProxy.matchScale = false;

            if (definitions.Count <= 7)
            {
                foreach (var definition in definitions)
                    CreateMenuItem(root.transform, definition, iconsAssetFolder, parameterName, defaultValue);
            }
            else
            {
                var groupCount = Mathf.CeilToInt(definitions.Count / 7f);
                for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
                {
                    var groupStart = groupIndex * 7;
                    var groupItems = definitions.Skip(groupStart).Take(7).ToList();
                    var group = NewChild(root.transform,
                        groupCount == 1 ? "カーソル" : "カーソル " + (groupIndex + 1));
                    var groupIcon = LoadIcon(iconsAssetFolder, groupItems[0].IconFile);
                    ConfigureSubMenu(group.AddComponent<ModularAvatarMenuItem>(), group.name, groupIcon);

                    foreach (var definition in groupItems)
                        CreateMenuItem(group.transform, definition, iconsAssetFolder, parameterName, defaultValue);
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabAssetPath);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static GameObject NormalizeImportedCursorDisplay(
        GameObject prefabRoot,
        GameObject modelInstance,
        Renderer importedRenderer)
    {
        var importedObject = importedRenderer.gameObject;
        if (importedObject == modelInstance)
        {
            importedObject.name = CursorDisplayName;
            return importedObject;
        }

        if (importedObject.transform.parent != modelInstance.transform)
            throw new InvalidOperationException(
                "Imported FBX renderer must be on the model root or its direct child.");

        // Flatten the imported model wrapper while preserving the combined
        // importer transform on the visible object.
        importedObject.transform.SetParent(prefabRoot.transform, true);
        importedObject.name = CursorDisplayName;
        Object.DestroyImmediate(modelInstance);
        return importedObject;
    }

    private static void CreateMenuItem(
        Transform parent,
        CursorDefinition definition,
        string iconsAssetFolder,
        string parameterName,
        int defaultValue)
    {
        var itemObject = NewChild(parent, definition.Label);
        var item = itemObject.AddComponent<ModularAvatarMenuItem>();
        item.Control = new VRCExpressionsMenu.Control
        {
            name = definition.Label,
            icon = LoadIcon(iconsAssetFolder, definition.IconFile),
            type = VRCExpressionsMenu.Control.ControlType.Toggle,
            parameter = new VRCExpressionsMenu.Control.Parameter { name = parameterName },
            value = definition.Value
        };
        item.MenuSource = SubmenuSource.Children;
        item.label = definition.Label;
        item.isSynced = true;
        item.isSaved = true;
        item.isDefault = definition.Value == defaultValue;
        item.automaticValue = false;
    }

    private static void ConfigureSubMenu(ModularAvatarMenuItem item, string label, Texture2D icon)
    {
        item.Control = new VRCExpressionsMenu.Control
        {
            name = label,
            icon = icon,
            type = VRCExpressionsMenu.Control.ControlType.SubMenu,
            parameter = new VRCExpressionsMenu.Control.Parameter(),
            value = 1f
        };
        item.MenuSource = SubmenuSource.Children;
        item.label = label;
        item.isSynced = true;
        item.isSaved = true;
        item.isDefault = false;
        item.automaticValue = false;
    }

    private static List<CursorDefinition> ParseCursorDefinitions(JObject manifest)
    {
        var cursorArray = manifest["cursors"] as JArray
                          ?? throw new InvalidDataException("Manifest cursors array is required.");
        var result = new List<CursorDefinition>();
        foreach (var cursor in cursorArray.OfType<JObject>())
        {
            var binding = cursor["binding"] as JObject
                          ?? throw new InvalidDataException("Manifest cursor.binding is required.");
            var icon = cursor["icon"] as JObject
                       ?? throw new InvalidDataException("Manifest cursor.icon is required.");
            result.Add(new CursorDefinition
            {
                AssetName = RequiredJsonString(binding, "asset_name"),
                Label = RequiredJsonString(binding, "menu_label"),
                IconFile = RequiredJsonString(icon, "file"),
                MenuOrder = RequiredJsonInt(binding, "menu_order"),
                Value = RequiredJsonInt(binding, "parameter_value")
            });
        }

        return result.OrderBy(item => item.MenuOrder).ThenBy(item => item.Value).ToList();
    }

    private static Texture2D LoadIcon(string iconsAssetFolder, string manifestIconPath)
    {
        var fileName = Path.GetFileName((manifestIconPath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        return AssetDatabase.LoadAssetAtPath<Texture2D>(
            NormalizeAssetPath(iconsAssetFolder).TrimEnd('/') + "/" + fileName);
    }

    private static GameObject NewChild(Transform parent, string name)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child;
    }

    private void ValidateSettings()
    {
        RequireDirectory(_sourceFolder, "ANI folder");
        if (!Directory.EnumerateFiles(_sourceFolder, "*.ani", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("ANI folder に *.ani がありません: " + _sourceFolder);
        if (!AniCursorEnvironment.IsValidPython(_pythonPath))
            throw new FileNotFoundException("Python が見つかりません。", _pythonPath);
        if (!AniCursorEnvironment.IsValidAniExtractRoot(_aniExtractRoot))
            throw new DirectoryNotFoundException("ani-extract root が見つかりません: " + _aniExtractRoot);
        if (!AniCursorEnvironment.IsValidBlender(_blenderPath))
            throw new FileNotFoundException("Blender が見つかりません。", _blenderPath);
        if (!AniCursorEnvironment.IsValidLogoTracerZip(_logoTracerPath))
            throw new InvalidDataException("LogoTracer 1.21 ZIP を指定してください: " + _logoTracerPath);
        RequireFile(ResolveBundledPipelineFile(PrepareScriptRelative), "prepare_ani_cursor.py");
        RequireFile(ResolveBundledPipelineFile(BlenderScriptRelative), "build_ani_cursor_blender.py");

        _outputRoot = RequireAssetPath(_outputRoot, "Output root");
        _prefabAssetPath = RequireAssetPathWithExtension(_prefabAssetPath, ".prefab", "Prefab");
        _fbxAssetPath = RequireAssetPathWithExtension(_fbxAssetPath, ".fbx", "FBX");
        _atlasAssetPath = RequireAssetPathWithExtension(_atlasAssetPath, ".png", "Atlas");
        _materialAssetPath = RequireAssetPathWithExtension(_materialAssetPath, ".mat", "Material");
        _controllerAssetPath = RequireAssetPathWithExtension(
            _controllerAssetPath, ".controller", "FX controller");
        _clipsAssetFolder = RequireAssetPath(_clipsAssetFolder, "Animation clips");
        _iconsAssetFolder = RequireAssetPath(_iconsAssetFolder, "Menu icons");

        if (string.IsNullOrWhiteSpace(_packageStem))
            throw new InvalidDataException("Package stem を入力してください。");
        if (string.IsNullOrWhiteSpace(_parameterName))
            throw new InvalidDataException("Shared int parameter を入力してください。");
        if (_frameSize != 32)
            throw new InvalidDataException("この pixel cursor pipeline の Unity 出力は 32x32 固定です。");
        if (_worldSize <= 0f) throw new InvalidDataException("Width / height は 0 より大きくしてください。");
        if (_thickness <= 0f) throw new InvalidDataException("Thickness は 0 より大きくしてください。");
    }

    private static void RunProcess(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        int timeoutMilliseconds,
        string label)
    {
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(" ", arguments.Select(QuoteProcessArgument).ToArray()),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using (var process = new Process { StartInfo = startInfo })
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data != null) output.AppendLine(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data != null) errors.AppendLine(eventArgs.Data);
            };

            if (!process.Start()) throw new InvalidOperationException(label + " を起動できませんでした。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(); }
                catch { /* The process may have exited between the timeout and Kill. */ }
                throw new TimeoutException(label + " が制限時間内に終了しませんでした。");
            }
            process.WaitForExit();

            var combined = output + errors.ToString();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(label + " failed (exit " + process.ExitCode + ").\n" +
                                                    Tail(combined, 6000));
            Debug.Log("[AniCursorPipeline] " + label + " complete.\n" + Tail(combined, 4000));
        }
    }

    private static string QuoteProcessArgument(string value)
    {
        if (value == null) return "\"\"";
        if (value.Length > 0 && value.All(character =>
                !char.IsWhiteSpace(character) && character != '"')) return value;

        // Windows CommandLineToArgvW quoting: ordinary backslashes are kept;
        // only runs before a quote (and the closing quote) are doubled.
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static string Tail(string value, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters) return value ?? string.Empty;
        return "…" + value.Substring(value.Length - maxCharacters);
    }

    private static string ResolveOptionalFile(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? AssetPathToAbsolute(value)
            : Path.GetFullPath(value);
        RequireFile(path, "Preset JSON");
        return path;
    }

    private static string ResolveBundledPipelineFile(string relativePath)
    {
        var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
            typeof(AniCursorPipelineWindow).Assembly);
        if (package != null)
        {
            return Path.GetFullPath(Path.Combine(package.resolvedPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        var scriptGuids = AssetDatabase.FindAssets("AniCursorPipelineWindow t:MonoScript");
        foreach (var guid in scriptGuids)
        {
            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!assetPath.EndsWith("/Editor/AniCursorPipelineWindow.cs",
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
            var editorDirectory = Path.GetDirectoryName(AssetPathToAbsolute(assetPath));
            var root = Directory.GetParent(editorDirectory ?? string.Empty)?.FullName;
            if (!string.IsNullOrWhiteSpace(root))
                return Path.GetFullPath(Path.Combine(root,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        throw new FileNotFoundException("ANI Cursor package root could not be resolved: " + relativePath);
    }

    private static void EnsureAssetParent(string assetPath)
    {
        var parent = Path.GetDirectoryName(NormalizeAssetPath(assetPath))?.Replace('\\', '/');
        EnsureAssetFolder(parent);
    }

    private static void EnsureAssetFolder(string assetFolder)
    {
        assetFolder = RequireAssetPath(assetFolder, "Asset folder").TrimEnd('/');
        Directory.CreateDirectory(AssetPathToAbsolute(assetFolder));
    }

    private static string RequireAssetPathWithExtension(string value, string extension, string label)
    {
        value = RequireAssetPath(value, label);
        if (!string.Equals(Path.GetExtension(value), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(label + " は " + extension + " にしてください: " + value);
        return value;
    }

    private static string RequireAssetPath(string value, string label)
    {
        value = NormalizeAssetPath(value).TrimEnd('/');
        if (Path.IsPathRooted(value))
        {
            if (!TryAbsoluteToAssetPath(value, out value))
                throw new InvalidDataException(label + " はこの project の Assets 以下にしてください。");
        }
        if (value != "Assets" && !value.StartsWith("Assets/", StringComparison.Ordinal))
            throw new InvalidDataException(label + " は Assets/... path にしてください: " + value);
        return value;
    }

    private static string NormalizeAssetPath(string value)
    {
        return (value ?? string.Empty).Trim().Trim('"').Replace('\\', '/');
    }

    private static string AssetPathToAbsolute(string assetPath)
    {
        assetPath = RequireAssetPath(assetPath, "Asset path");
        return Path.GetFullPath(Path.Combine(ProjectRoot,
            assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool TryAbsoluteToAssetPath(string path, out string assetPath)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var assets = Path.GetFullPath(Application.dataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, assets, StringComparison.OrdinalIgnoreCase))
        {
            assetPath = "Assets";
            return true;
        }
        var prefix = assets + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            assetPath = null;
            return false;
        }
        assetPath = "Assets/" + full.Substring(prefix.Length).Replace('\\', '/');
        return true;
    }

    private static string ResolvePickerStart(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ProjectRoot;
        try
        {
            var path = NormalizeAssetPath(value).StartsWith("Assets", StringComparison.OrdinalIgnoreCase)
                ? AssetPathToAbsolute(value)
                : Path.GetFullPath(value);
            return Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? ProjectRoot;
        }
        catch
        {
            return ProjectRoot;
        }
    }

    private static string SafeFileStem(string value, string fallback)
    {
        value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static void RequireFile(string path, string label)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(label + " が見つかりません。", path);
    }

    private static void RequireDirectory(string path, string label)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(label + " が見つかりません: " + path);
    }

    private static string RequiredJsonString(JObject obj, string name)
    {
        var value = obj.Value<string>(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Manifest field is required: " + name);
        return value;
    }

    private static int RequiredJsonInt(JObject obj, string name)
    {
        return obj.Value<int?>(name)
               ?? throw new InvalidDataException("Manifest field is required: " + name);
    }

    private static string ReadCommandLineValue(IList<string> arguments, string flag)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
            if (string.Equals(arguments[index], flag, StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        return null;
    }

    private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
}

}

#endif
