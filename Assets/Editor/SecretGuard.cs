using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace ScamSim.EditorTools
{
    /// <summary>
    /// Stops any build while a config.json sits under StreamingAssets: Unity copies that folder into
    /// every player build, so the API key inside would ship with the game. config.json belongs at the
    /// project root (see config.example.json).
    /// </summary>
    public class SecretGuard : IPreprocessBuildWithReport
    {
        public const string ForbiddenPath = "Assets/StreamingAssets/config.json";

        public int callbackOrder
        {
            get { return 0; }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (File.Exists(ForbiddenPath))
                throw new BuildFailedException(ForbiddenPath + " would ship your API key inside the build. "
                                               + "Move it to the project root as config.json.");
        }
    }
}
