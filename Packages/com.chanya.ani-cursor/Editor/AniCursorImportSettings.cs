#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Chanya.AniCursor
{

/// <summary>Owns every Unity importer setting required by generated ANI cursors.</summary>
internal static class AniCursorImportSettings
{
    private const ImportAssetOptions ImportOptions =
        ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport;

    internal static void ConfigureModel(string assetPath)
    {
        Apply<ModelImporter>(assetPath, "FBX", importer =>
        {
            importer.importAnimation = false;
            importer.importBlendShapes = false;
            importer.importBlendShapeNormals = ModelImporterNormals.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.globalScale = 1f;
            importer.useFileUnits = true;
            importer.bakeAxisConversion = false;
            importer.isReadable = true;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.weldVertices = false; // Preserve closed shells at diagonal pixel contacts.
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
        }, importer =>
            importer.isReadable &&
            !importer.importAnimation &&
            !importer.importBlendShapes &&
            importer.importBlendShapeNormals == ModelImporterNormals.None &&
            !importer.importCameras &&
            !importer.importLights &&
            Mathf.Approximately(importer.globalScale, 1f) &&
            importer.useFileUnits &&
            !importer.bakeAxisConversion &&
            importer.meshCompression == ModelImporterMeshCompression.Off &&
            !importer.weldVertices &&
            importer.materialImportMode == ModelImporterMaterialImportMode.None);
    }

    internal static Texture2D ConfigureAtlas(string assetPath)
    {
        var maxSize = 0;
        Apply<TextureImporter>(assetPath, "Atlas", importer =>
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null) throw new InvalidOperationException("Atlas import failed: " + assetPath);
            maxSize = Mathf.Clamp(
                Mathf.NextPowerOfTwo(Mathf.Max(texture.width, texture.height)), 32, 8192);

            ConfigurePointTexture(importer, false, maxSize);
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;
        }, importer =>
            IsPointTexture(importer, false, maxSize) &&
            importer.textureCompression == TextureImporterCompression.Uncompressed &&
            !importer.crunchedCompression);

        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath)
               ?? throw new InvalidOperationException("Atlas import failed: " + assetPath);
    }

    internal static void ConfigureMenuIcons(IEnumerable<string> assetPaths, int size)
    {
        foreach (var path in assetPaths
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            ConfigureMenuIcon(path, size);
    }

    private static void ConfigureMenuIcon(string assetPath, int size)
    {
        var maxSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(32, size)), 32, 256);
        Apply<TextureImporter>(assetPath, "Menu icon", importer =>
        {
            ConfigurePointTexture(importer, true, maxSize);
            var platform = importer.GetDefaultPlatformTextureSettings();
            platform.maxTextureSize = maxSize;
            platform.textureCompression = TextureImporterCompression.Compressed;
            platform.crunchedCompression = false;
            importer.SetPlatformTextureSettings(platform);
        }, importer =>
        {
            var platform = importer.GetDefaultPlatformTextureSettings();
            return IsPointTexture(importer, true, maxSize) &&
                   platform.maxTextureSize == maxSize &&
                   platform.textureCompression == TextureImporterCompression.Compressed &&
                   !platform.crunchedCompression;
        });
    }

    private static void ConfigurePointTexture(
        TextureImporter importer,
        bool alphaIsTransparency,
        int maxSize)
    {
        importer.textureType = TextureImporterType.Default;
        importer.textureShape = TextureImporterShape.Texture2D;
        importer.sRGBTexture = true;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = alphaIsTransparency;
        importer.mipmapEnabled = false;
        importer.streamingMipmaps = false;
        importer.isReadable = false;
        importer.filterMode = FilterMode.Point;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.maxTextureSize = maxSize;
    }

    private static bool IsPointTexture(
        TextureImporter importer,
        bool alphaIsTransparency,
        int maxSize)
    {
        return importer.textureType == TextureImporterType.Default &&
               importer.textureShape == TextureImporterShape.Texture2D &&
               importer.sRGBTexture &&
               importer.alphaSource == TextureImporterAlphaSource.FromInput &&
               importer.alphaIsTransparency == alphaIsTransparency &&
               !importer.mipmapEnabled &&
               !importer.streamingMipmaps &&
               !importer.isReadable &&
               importer.filterMode == FilterMode.Point &&
               importer.wrapMode == TextureWrapMode.Clamp &&
               importer.npotScale == TextureImporterNPOTScale.None &&
               importer.maxTextureSize == maxSize;
    }

    private static void Apply<T>(
        string assetPath,
        string label,
        Action<T> configure,
        Func<T, bool> validate) where T : AssetImporter
    {
        AssetDatabase.ImportAsset(assetPath, ImportOptions);
        var importer = AssetImporter.GetAtPath(assetPath) as T;
        if (importer == null) throw new InvalidOperationException(label + " import failed: " + assetPath);

        configure(importer);
        importer.SaveAndReimport();

        var verified = AssetImporter.GetAtPath(assetPath) as T;
        if (verified == null || !validate(verified))
            throw new InvalidOperationException(label + " importer settings were overridden: " + assetPath);
    }
}

}

#endif
