using System;
using System.Collections.Generic;
using System.IO;
using ScamSim.Core;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace ScamSim.EditorTools
{
    /// <summary>
    /// Builds the project's generated assets from code, so the scene and UI settings can be
    /// recreated from a clean clone and reviewed as code instead of YAML. Every method is safe to run again.
    ///   unity run . -- -executeMethod ScamSim.EditorTools.ProjectSetup.CreateMainScene
    ///   unity build . --target StandaloneWindows64 --execute-method ScamSim.EditorTools.ProjectSetup.BuildWindows -o Builds/Windows/ScamSim.exe
    /// </summary>
    public static class ProjectSetup
    {
        public const string UiFolder = "Assets/UI";
        public const string ThemePath = UiFolder + "/ScamSimTheme.tss";
        public const string PanelSettingsPath = UiFolder + "/ScamSimPanelSettings.asset";
        public const string StylesPath = UiFolder + "/ScamSim.uss";
        public const string IntroPath = UiFolder + "/Intro.uxml";
        public const string CallPath = UiFolder + "/Call.uxml";
        public const string DebriefPath = UiFolder + "/Debrief.uxml";
        public const string NewsPath = UiFolder + "/News.uxml";
        public const string LocalizationPath = "Assets/Localization/th.json";
        public const string ScenarioPath = "Assets/Scenarios/police_scripted.json";
        public const string ScammerPromptPath = "Assets/Prompts/scammer_police.txt";
        public const string LessonsPromptPath = "Assets/Prompts/lessons.txt";
        public const string ScenePath = "Assets/Scenes/Main.unity";
        public const string DefaultWindowsOutput = "Builds/Windows/ScamSim.exe";

        public const string ProductName = "ScamSim";
        public const string Version = "0.1.0";

        /// <summary>
        /// The game is a 16:9 visual-novel stage laid out at 1422x800 (ScamSim.uss .stage). "Expand"
        /// scales by the smaller of width/1422 and height/800, so the panel is never smaller than the
        /// stage and any window shape shows the whole stage with dark bars ("Shrink" uses the larger
        /// ratio and crops the stage in a wide browser window). A 1080p screen shows everything 1.35x
        /// larger (the smallest 18px text becomes 24px), which older players need.
        /// </summary>
        public static readonly Vector2Int ReferenceResolution = new Vector2Int(1422, 800);

        /// <summary>The Windows build opens in a 16:9 window the player can resize.</summary>
        static readonly Vector2Int WindowSize = new Vector2Int(1600, 900);

        /// <summary>Our page template (Assets/WebGLTemplates/ScamSim): the canvas fills the browser window.</summary>
        public const string WebGLTemplate = "PROJECT:ScamSim";

        public const string DefaultWebGLOutput = "Builds/WebGL";
        static readonly Color Background = new Color32(16, 19, 33, 255);

        [MenuItem("ScamSim/Create UI Settings")]
        public static PanelSettings EnsurePanelSettings()
        {
            Directory.CreateDirectory(UiFolder);
            if (!File.Exists(ThemePath))
            {
                File.WriteAllText(ThemePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(ThemePath);
            }

            ThemeStyleSheet theme = Load<ThemeStyleSheet>(ThemePath);
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(settings, PanelSettingsPath);
            }

            settings.themeStyleSheet = theme;
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = ReferenceResolution;
            settings.screenMatchMode = PanelScreenMatchMode.Expand;
            settings.match = 1f;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return settings;
        }

        /// <summary>Window size and template for both players: a resizable 16:9 window, a full-page canvas.</summary>
        static void ApplyPlayerSettings()
        {
            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = Version;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = WindowSize.x;
            PlayerSettings.defaultScreenHeight = WindowSize.y;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.WebGL.template = WebGLTemplate;
        }

        /// <summary>
        /// Re-imports the scene and character pictures with ArtImport's settings. New files get them on
        /// their own; run this after changing ArtImport.
        /// </summary>
        [MenuItem("ScamSim/Reimport Art")]
        public static void ReimportArt()
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string folder in ArtImport.Folders)
                {
                    if (!AssetDatabase.IsValidFolder(folder)) continue;
                    foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
                        AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid), ImportAssetOptions.ForceUpdate);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        [MenuItem("ScamSim/Create Main Scene")]
        public static void CreateMainScene()
        {
            // From the menu, give the developer the chance to keep unsaved scene edits first.
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            ApplyPlayerSettings();
            EnsurePanelSettings();
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // NewScene unloads assets nothing references, so load the panel settings only after it.
            PanelSettings panelSettings = Load<PanelSettings>(PanelSettingsPath);

            var cameraObject = new GameObject("Main Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.orthographic = true;
            cameraObject.tag = "MainCamera";

            var sessionObject = new GameObject("GameSession");
            var document = sessionObject.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            var session = sessionObject.AddComponent<GameSession>();

            var serialized = new SerializedObject(session);
            Set(serialized, "document", document);
            Set(serialized, "styles", Load<StyleSheet>(StylesPath));
            Set(serialized, "localization", Load<TextAsset>(LocalizationPath));
            Set(serialized, "scenario", Load<TextAsset>(ScenarioPath));
            Set(serialized, "scammerPrompt", Load<TextAsset>(ScammerPromptPath));
            Set(serialized, "lessonsPrompt", Load<TextAsset>(LessonsPromptPath));
            Set(serialized, "introLayout", Load<VisualTreeAsset>(IntroPath));
            Set(serialized, "callLayout", Load<VisualTreeAsset>(CallPath));
            Set(serialized, "debriefLayout", Load<VisualTreeAsset>(DebriefPath));
            Set(serialized, "newsLayout", Load<VisualTreeAsset>(NewsPath));
            serialized.ApplyModifiedPropertiesWithoutUndo();

            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Could not save " + ScenePath);

            // Main goes first (it is the scene the player starts in); other build scenes are kept.
            var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
            foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
            {
                if (existing.path != ScenePath) scenes.Add(existing);
            }
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectSetup] Wrote " + ScenePath);
        }

        /// <summary>Windows build of Main.unity. Honors -buildOutput from `unity build -o`.</summary>
        public static void BuildWindows()
        {
            ApplyPlayerSettings();
            Build(BuildTarget.StandaloneWindows64, ArgumentAfter("-buildOutput") ?? DefaultWindowsOutput);
        }

        /// <summary>
        /// WebGL build for the demo link (PRD 3). This method owns the web compression settings:
        /// Gzip with the decompression fallback, so the files work on any static host without special
        /// server headers (GitHub Pages included). The page is our own template: the canvas fills the
        /// window and the game keeps its 16:9 stage inside. The web build never reads config.json, so
        /// it always plays the offline scenario.
        /// </summary>
        public static void BuildWebGL()
        {
            ApplyPlayerSettings();
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            Build(BuildTarget.WebGL, ArgumentAfter("-buildOutput") ?? DefaultWebGLOutput);
        }

        static void Build(BuildTarget target, string output)
        {
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = target,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log("[ProjectSetup] Build " + summary.result + ": " + output + " (" + summary.totalErrors + " errors, "
                      + summary.totalWarnings + " warnings, " + summary.totalSize / (1024 * 1024) + " MB)");
            if (summary.result != BuildResult.Succeeded && Application.isBatchMode) EditorApplication.Exit(1);
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new IOException("Missing " + typeof(T).Name + " at " + path);
            return asset;
        }

        static void Set(SerializedObject serialized, string field, UnityEngine.Object value)
        {
            SerializedProperty property = serialized.FindProperty(field);
            if (property == null) throw new InvalidOperationException("GameSession has no serialized field '" + field + "'");
            property.objectReferenceValue = value;
        }

        static string ArgumentAfter(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == flag) return args[i + 1];
            }
            return null;
        }
    }
}
