#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Chanya.AniCursor
{
    [Serializable]
    internal sealed class AniCursorToolPaths
    {
        public string PythonPath = string.Empty;
        public string AniExtractRoot = string.Empty;
        public string BlenderPath = string.Empty;
        public string LogoTracerPath = string.Empty;

        public bool HasPython => AniCursorEnvironment.IsValidPython(PythonPath);
        public bool HasAniExtract => AniCursorEnvironment.IsValidAniExtractRoot(AniExtractRoot);
        public bool HasBlender => AniCursorEnvironment.IsValidBlender(BlenderPath);
        public bool HasLogoTracer => AniCursorEnvironment.IsValidLogoTracerZip(LogoTracerPath);
        public bool IsComplete => HasPython && HasAniExtract && HasBlender && HasLogoTracer;
    }

    /// <summary>
    /// Stores user-selected tool locations and fills missing values from the host environment.
    /// No project, avatar, user-name, or drive-specific paths are embedded here.
    /// </summary>
    internal static class AniCursorEnvironment
    {
        private const string PrefPrefix = "Chanya.AniCursor.ToolPaths.";

        public static AniCursorToolPaths LoadAndDetect(AniCursorToolPaths current = null)
        {
            var saved = LoadSaved();
            var environmentRoots = ReadEnvironment(
                "ANI_CURSOR_ANI_EXTRACT_ROOT", "ANI_EXTRACT_ROOT").ToArray();
            var environmentPython = ReadEnvironment(
                "ANI_CURSOR_PYTHON", "ANI_EXTRACT_PYTHON", "PYTHON").ToArray();

            var root = FirstValid(
                IsValidAniExtractRoot,
                One(saved.AniExtractRoot),
                One(current == null ? null : current.AniExtractRoot),
                environmentRoots,
                environmentPython.Select(DeriveAniExtractRoot),
                CommonAniExtractRoots(),
                FindOnPath("ani-extract.exe", "ani-extract").Select(DeriveAniExtractRoot));

            var paths = new AniCursorToolPaths
            {
                AniExtractRoot = root,
                PythonPath = FirstValid(
                    IsValidPython,
                    One(saved.PythonPath),
                    One(current == null ? null : current.PythonPath),
                    environmentPython,
                    PythonForRoot(root),
                    CommonPythonPaths(),
                    FindOnPath("python.exe", "python3.exe", "python", "python3", "py.exe")),
                BlenderPath = FirstValid(
                    IsValidBlender,
                    One(saved.BlenderPath),
                    One(current == null ? null : current.BlenderPath),
                    ReadEnvironment("ANI_CURSOR_BLENDER", "BLENDER_PATH", "BLENDER"),
                    CommonBlenderPaths(),
                    FindOnPath("blender.exe", "blender")),
                LogoTracerPath = FirstValid(
                    IsValidLogoTracerZip,
                    One(saved.LogoTracerPath),
                    One(current == null ? null : current.LogoTracerPath),
                    ReadEnvironment(
                        "ANI_CURSOR_LOGO_TRACER", "LOGO_TRACER_ZIP",
                        "LOGOTRACER_ZIP", "LOGOTRACER_PATH"),
                    CommonLogoTracerPaths(),
                    LogoTracerOnPath())
            };

            Save(paths);
            return paths;
        }

        public static void Save(AniCursorToolPaths paths)
        {
            if (paths == null) return;
            EditorPrefs.SetString(PrefPrefix + nameof(paths.PythonPath), Clean(paths.PythonPath));
            EditorPrefs.SetString(PrefPrefix + nameof(paths.AniExtractRoot), Clean(paths.AniExtractRoot));
            EditorPrefs.SetString(PrefPrefix + nameof(paths.BlenderPath), Clean(paths.BlenderPath));
            EditorPrefs.SetString(PrefPrefix + nameof(paths.LogoTracerPath), Clean(paths.LogoTracerPath));
        }

        public static string DescribeMissing(AniCursorToolPaths paths)
        {
            if (paths == null) return "Python、ani-extract、Blender、Logo Tracer 1.21";
            var missing = new List<string>();
            if (!paths.HasPython) missing.Add("Python");
            if (!paths.HasAniExtract) missing.Add("ani-extract root");
            if (!paths.HasBlender) missing.Add("Blender");
            if (!paths.HasLogoTracer) missing.Add("Logo Tracer 1.21 ZIP");
            return string.Join("、", missing.ToArray());
        }

        public static bool IsValidPython(string path)
        {
            if (!IsFile(path)) return false;
            var name = Path.GetFileNameWithoutExtension(Clean(path));
            return name.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "py", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsValidAniExtractRoot(string path)
        {
            if (!IsDirectory(path)) return false;
            path = Clean(path);
            return File.Exists(Path.Combine(path, "src", "ani_extract", "__init__.py"));
        }

        public static bool IsValidBlender(string path)
        {
            if (!IsFile(path)) return false;
            return string.Equals(
                Path.GetFileNameWithoutExtension(Clean(path)),
                "blender",
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsValidLogoTracerZip(string path)
        {
            if (!IsFile(path) || !string.Equals(
                    Path.GetExtension(Clean(path)), ".zip", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                using (var stream = File.OpenRead(Clean(path)))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var entryName = entry.FullName.Replace('\\', '/');
                        if (!entryName.EndsWith("/__init__.py", StringComparison.OrdinalIgnoreCase) ||
                            entry.Length > 1024 * 1024)
                            continue;

                        using (var reader = new StreamReader(entry.Open()))
                        {
                            var source = reader.ReadToEnd();
                            var hasMarker = source.IndexOf("Logo-Tracer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            source.IndexOf("logo_tracer_props", StringComparison.OrdinalIgnoreCase) >= 0;
                            var hasVersion = Regex.IsMatch(
                                source,
                                @"[""']version[""']\s*:\s*\(\s*[""']?1[""']?\s*,\s*[""']?21[""']?",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                            if (hasMarker && hasVersion) return true;
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException || exception is InvalidDataException ||
                exception is UnauthorizedAccessException)
            {
                return false;
            }
            return false;
        }

        private static AniCursorToolPaths LoadSaved()
        {
            return new AniCursorToolPaths
            {
                PythonPath = EditorPrefs.GetString(PrefPrefix + nameof(AniCursorToolPaths.PythonPath), string.Empty),
                AniExtractRoot = EditorPrefs.GetString(PrefPrefix + nameof(AniCursorToolPaths.AniExtractRoot), string.Empty),
                BlenderPath = EditorPrefs.GetString(PrefPrefix + nameof(AniCursorToolPaths.BlenderPath), string.Empty),
                LogoTracerPath = EditorPrefs.GetString(PrefPrefix + nameof(AniCursorToolPaths.LogoTracerPath), string.Empty)
            };
        }

        private static string FirstValid(
            Func<string, bool> validator,
            params IEnumerable<string>[] candidateGroups)
        {
            foreach (var candidate in candidateGroups.SelectMany(group => group ?? Enumerable.Empty<string>()))
            {
                var clean = Clean(candidate);
                if (validator(clean)) return Path.GetFullPath(clean);
            }
            return string.Empty;
        }

        private static IEnumerable<string> ReadEnvironment(params string[] names)
        {
            foreach (var name in names)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
        }

        private static IEnumerable<string> CommonAniExtractRoots()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var searchRoots = new[] { projectRoot, documents };

            foreach (var searchRoot in searchRoots.Where(IsDirectory))
            {
                yield return Path.Combine(searchRoot, "ani-extract");
                yield return Path.Combine(searchRoot, "Tools", "ani-extract");

                foreach (var child in SafeDirectories(searchRoot))
                {
                    if (string.Equals(Path.GetFileName(child), "ani-extract", StringComparison.OrdinalIgnoreCase))
                        yield return child;
                    yield return Path.Combine(child, "ani-extract");
                }
            }
        }

        private static IEnumerable<string> PythonForRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) yield break;
            yield return Path.Combine(root, ".venv", "Scripts", "python.exe");
            yield return Path.Combine(root, "venv", "Scripts", "python.exe");
            yield return Path.Combine(root, ".venv", "bin", "python");
            yield return Path.Combine(root, "venv", "bin", "python");
        }

        private static IEnumerable<string> CommonPythonPaths()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (var path in PythonForRoot(projectRoot)) yield return path;

            var localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python");
            foreach (var directory in SafeDirectories(localPrograms).OrderByDescending(value => value))
                yield return Path.Combine(directory, "python.exe");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var directory in SafeDirectories(programFiles, "Python*").OrderByDescending(value => value))
                yield return Path.Combine(directory, "python.exe");

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(profile, "scoop", "apps", "python", "current", "python.exe");
            yield return "/usr/local/bin/python3";
            yield return "/usr/bin/python3";
        }

        private static IEnumerable<string> CommonBlenderPaths()
        {
            var programFiles = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var root in programFiles)
            {
                var blenderFoundation = Path.Combine(root, "Blender Foundation");
                foreach (var directory in SafeDirectories(blenderFoundation).OrderByDescending(value => value))
                    yield return Path.Combine(directory, "blender.exe");
            }

            foreach (var steamRoot in SteamRoots())
                yield return Path.Combine(steamRoot, "steamapps", "common", "Blender", "blender.exe");

            yield return "/Applications/Blender.app/Contents/MacOS/Blender";
            yield return "/usr/local/bin/blender";
            yield return "/usr/bin/blender";
        }

        private static IEnumerable<string> SteamRoots()
        {
            var defaults = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
            }.Where(IsDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            foreach (var root in defaults) yield return root;
            foreach (var root in defaults)
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                string contents;
                try { contents = File.ReadAllText(vdf); }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException) { continue; }

                foreach (Match match in Regex.Matches(contents, @"""path""\s+""([^""]+)"""))
                    yield return match.Groups[1].Value.Replace("\\\\", "\\");
            }
        }

        private static IEnumerable<string> CommonLogoTracerPaths()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var roots = new[]
            {
                Path.Combine(profile, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(projectRoot, "Tools"),
                projectRoot
            };

            foreach (var root in roots.Where(IsDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var zip in SafeFiles(root, "*.zip")
                         .OrderByDescending(SafeLastWriteTimeUtc))
                if (Path.GetFileName(zip).IndexOf("LogoTracer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Path.GetFileName(zip).IndexOf("Logo-Tracer", StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return zip;
        }

        private static IEnumerable<string> LogoTracerOnPath()
        {
            foreach (var directory in PathDirectories())
            foreach (var zip in SafeFiles(directory, "*.zip"))
                if (Path.GetFileName(zip).IndexOf("LogoTracer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Path.GetFileName(zip).IndexOf("Logo-Tracer", StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return zip;
        }

        private static IEnumerable<string> FindOnPath(params string[] names)
        {
            foreach (var directory in PathDirectories())
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) yield return candidate;
            }
        }

        private static IEnumerable<string> PathDirectories()
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Clean)
                .Where(IsDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string DeriveAniExtractRoot(string path)
        {
            path = Clean(path);
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            for (var index = 0; index < 6 && !string.IsNullOrWhiteSpace(directory); index++)
            {
                if (IsValidAniExtractRoot(directory)) return Path.GetFullPath(directory);
                directory = Path.GetDirectoryName(directory);
            }
            return string.Empty;
        }

        private static IEnumerable<string> One(string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }

        private static IEnumerable<string> SafeDirectories(string path, string pattern = "*")
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path, pattern) : Array.Empty<string>(); }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeFiles(string path, string pattern)
        {
            try { return Directory.Exists(path) ? Directory.GetFiles(path, pattern) : Array.Empty<string>(); }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        private static DateTime SafeLastWriteTimeUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private static bool IsFile(string path)
        {
            try { return File.Exists(Clean(path)); }
            catch { return false; }
        }

        private static bool IsDirectory(string path)
        {
            try { return Directory.Exists(Clean(path)); }
            catch { return false; }
        }

        private static string Clean(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        }
    }
}

#endif
