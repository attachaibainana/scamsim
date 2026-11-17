using UnityEditor;
using UnityEngine;

namespace ScamSim.EditorTools
{
    /// <summary>
    /// Import settings for the scene and character pictures (made by tools/art/gen.ps1 + process.py).
    /// They are shown as UI Toolkit background images, so: no mipmaps, original size kept (no
    /// power-of-two stretch), and compressed with crunch so the web download stays small.
    /// Runs on every import of a file in these folders; ProjectSetup.ReimportArt applies changes.
    /// </summary>
    public class ArtImport : AssetPostprocessor
    {
        public const string BackgroundFolder = "Assets/Art/Backgrounds";
        public const string CharacterFolder = "Assets/Art/Characters";
        public static readonly string[] Folders = { BackgroundFolder, CharacterFolder };

        const int MaxSize = 2048;
        const int CrunchQuality = 75;

        void OnPreprocessTexture()
        {
            bool background = assetPath.StartsWith(BackgroundFolder + "/");
            bool character = assetPath.StartsWith(CharacterFolder + "/");
            if (!background && !character) return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = MaxSize;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.crunchedCompression = true;
            importer.compressionQuality = CrunchQuality;
            importer.alphaSource = character ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            // Fills the colour of clear pixels from their neighbours, so scaled edges get no dark fringe.
            importer.alphaIsTransparency = character;
        }
    }
}
