using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Chanya.AniCursor.Tests.Editor
{
    public sealed class AniCursorDistributionTests
    {
        private const string PackageName = "com.chanya.ani-cursor";

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(
            new[]
            {
                ".asmdef", ".asmref", ".cs", ".json", ".md", ".ps1", ".py", ".sh",
                ".txt", ".toml", ".uss", ".uxml", ".yaml", ".yml"
            },
            StringComparer.OrdinalIgnoreCase);

        [Test]
        public void Manifest_HasExpectedVpmIdentity()
        {
            var manifest = JsonUtility.FromJson<PackageManifest>(ReadManifestText());

            Assert.That(manifest.name, Is.EqualTo(PackageName));
            Assert.That(manifest.displayName, Is.Not.Null.And.Not.Empty);
            Assert.That(manifest.version, Is.EqualTo("1.0.2"));
            Assert.That(manifest.description, Is.Not.Null.And.Not.Empty);
            Assert.That(manifest.unity, Does.StartWith("2022.3"));
        }

        [Test]
        public void Manifest_DeclaresRequiredDependencies()
        {
            var manifest = ReadManifestText();

            Assert.That(manifest, Does.Contain("\"com.unity.nuget.newtonsoft-json\": \"3.2.1\""));
            Assert.That(manifest, Does.Contain("\"com.vrchat.avatars\": \"3.10.x\""));
            Assert.That(manifest, Does.Contain("\"nadena.dev.modular-avatar\": \">=1.18.1 <2.0.0-a\""));
        }

        [Test]
        public void DistributableFiles_DoNotContainMachineOrProjectSpecificPaths()
        {
            var packageRoot = LocatePackageRoot();
            var forbiddenPaths = new[]
            {
                "c:/users/",
                "f:/vrcavatargit",
                "assets/modified/"
            };
            var violations = new List<string>();

            foreach (var path in Directory.EnumerateFiles(packageRoot.FullName, "*", SearchOption.AllDirectories))
            {
                var relativePath = NormalizePath(Path.GetRelativePath(packageRoot.FullName, path));
                if (relativePath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
                    !TextExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                var normalizedContent = NormalizePath(File.ReadAllText(path)).ToLowerInvariant();
                foreach (var forbiddenPath in forbiddenPaths.Where(normalizedContent.Contains))
                {
                    violations.Add(relativePath + " contains " + forbiddenPath);
                }
            }

            Assert.That(
                violations,
                Is.Empty,
                "Distributable files contain local-machine or project-specific paths:\n" +
                string.Join("\n", violations));
        }

        private static string ReadManifestText()
        {
            var manifestPath = Path.Combine(LocatePackageRoot().FullName, "package.json");
            Assert.That(File.Exists(manifestPath), Is.True, "Missing package.json at " + manifestPath);
            return File.ReadAllText(manifestPath);
        }

        private static DirectoryInfo LocatePackageRoot([CallerFilePath] string sourcePath = "")
        {
            var sourceRoot = FindManifestAncestor(new FileInfo(sourcePath).Directory);
            if (sourceRoot != null)
            {
                return sourceRoot;
            }

            var registeredPackage = PackageInfo.GetAllRegisteredPackages()
                .FirstOrDefault(package => string.Equals(package.name, PackageName, StringComparison.Ordinal));
            if (registeredPackage != null)
            {
                return new DirectoryInfo(registeredPackage.resolvedPath);
            }

            var packagePath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Packages", PackageName));
            if (File.Exists(Path.Combine(packagePath, "package.json")))
            {
                return new DirectoryInfo(packagePath);
            }

            Assert.Fail("Could not locate package root for " + PackageName);
            return null;
        }

        private static DirectoryInfo FindManifestAncestor(DirectoryInfo directory)
        {
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json")))
                {
                    return directory;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static string NormalizePath(string value)
        {
            var normalized = value.Replace("\\\\", "/").Replace('\\', '/');
            while (normalized.Contains("//"))
            {
                normalized = normalized.Replace("//", "/");
            }

            return normalized;
        }

        [Serializable]
        private sealed class PackageManifest
        {
            public string name;
            public string displayName;
            public string version;
            public string description;
            public string unity;
        }
    }
}
