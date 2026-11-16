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

        /// <summary>Screens are laid out for a 1920x1080 window and scaled by height, so text keeps its size.</summary>
        static readonly Vector2Int ReferenceResolution = new Vector2Int(1920, 1080);
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
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 1f;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return settings;
        }

        [MenuItem("ScamSim/Create Main Scene")]
        public static void CreateMainScene()
        {
            // From the menu, give the developer the chance to keep unsaved scene edits first.
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = Version;

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
            string output = ArgumentAfter("-buildOutput") ?? DefaultWindowsOutput;
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
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
