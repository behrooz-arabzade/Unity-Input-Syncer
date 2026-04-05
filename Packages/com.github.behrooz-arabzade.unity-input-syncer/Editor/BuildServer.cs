using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BuildServer
{
    private const string DedicatedServerScene = "Packages/com.github.behrooz-arabzade.unity-input-syncer/Scenes/DedicatedServerScene.unity";
    private const string MultiInstanceServerScene = "Packages/com.github.behrooz-arabzade.unity-input-syncer/Scenes/MultiInstanceServerScene.unity";
    private const string OutputPath = "Builds/Server";

    [MenuItem("Build/Dedicated Server")]
    public static void BuildDedicatedServer()
    {
        var result = Build(new[] { DedicatedServerScene }, OutputPath + "/DedicatedServer");
        EditorApplication.Exit(result == BuildResult.Succeeded ? 0 : 1);
    }

    [MenuItem("Build/Multi-Instance Server")]
    public static void BuildMultiInstanceServer()
    {
        var result = Build(new[] { MultiInstanceServerScene }, OutputPath + "/MultiInstanceServer");
        EditorApplication.Exit(result == BuildResult.Succeeded ? 0 : 1);
    }

    private static BuildResult Build(string[] scenes, string outputPath)
    {
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneOSX,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.None
        };

        var report = BuildPipeline.BuildPlayer(options);
        return report.summary.result;
    }
}
