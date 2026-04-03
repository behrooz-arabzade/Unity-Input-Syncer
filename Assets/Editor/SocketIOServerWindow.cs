using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public class SocketIOServerWindow : EditorWindow
{
    // -------------------------
    // Configuration fields
    // -------------------------
    private ushort port = 3000;
    private int maxPlayers = 2;
    private float stepInterval = 0.1f;
    private bool autoStartWhenFull = true;
    private bool allowLateJoin = false;
    private bool sendHistoryOnLateJoin = true;
    private string adminAuthToken = "";
    private int maxInstances = 10;
    private bool autoRecycle = true;
    private float idleTimeout = 0f;

    private bool showConfig = true;

    // -------------------------
    // Process state
    // -------------------------
    private Process serverProcess;
    private Process buildProcess;
    private Process installProcess;
    private bool isBuilding;
    private bool isInstalling;
    private readonly List<string> consoleLines = new List<string>();
    private const int MaxConsoleLines = 200;
    private Vector2 consoleScroll;

    // -------------------------
    // Monitoring state
    // -------------------------
    private PoolStats latestStats;
    private double lastPollTime;
    private const double PollIntervalSeconds = 2.0;
    private UnityWebRequest activeStatsRequest;
    private UnityWebRequest activeActionRequest;
    private Vector2 instancesScroll;
    private string pollError;

    // -------------------------
    // Paths
    // -------------------------
    private string ServerDir => Path.GetFullPath(Path.Combine(Application.dataPath, "UnityInputSyncerSocketIOServer"));

    [MenuItem("Window/Input Syncer/Socket.IO Server")]
    public static void ShowWindow()
    {
        GetWindow<SocketIOServerWindow>("Socket.IO Server");
    }

    void OnEnable()
    {
        LoadPrefs();
        EditorApplication.update += OnEditorUpdate;
    }

    void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        SavePrefs();
        StopServer();
        StopInstall();
        StopBuild();
        DisposeRequest(ref activeStatsRequest);
        DisposeRequest(ref activeActionRequest);
    }

    // -------------------------
    // EDITOR UPDATE (polling)
    // -------------------------

    private void OnEditorUpdate()
    {
        CheckProcessExited();
        CheckInstallExited();
        CheckBuildExited();
        PollStats();
        ProcessStatsResponse();
        ProcessActionResponse();
    }

    // -------------------------
    // IMGUI
    // -------------------------

    void OnGUI()
    {
        DrawConfigSection();
        EditorGUILayout.Space(4);
        DrawControlSection();
        EditorGUILayout.Space(4);

        if (IsServerRunning())
        {
            DrawMonitoringSection();
            EditorGUILayout.Space(4);
        }

        DrawConsoleSection();
    }

    private void DrawConfigSection()
    {
        showConfig = EditorGUILayout.Foldout(showConfig, "Server Configuration", true, EditorStyles.foldoutHeader);
        if (!showConfig) return;

        EditorGUI.indentLevel++;
        bool wasEnabled = GUI.enabled;
        GUI.enabled = !IsServerRunning() && !isBuilding && !isInstalling;

        port = (ushort)EditorGUILayout.IntField("Port", port);
        maxPlayers = EditorGUILayout.IntField("Max Players", maxPlayers);
        stepInterval = EditorGUILayout.FloatField("Step Interval (s)", stepInterval);
        autoStartWhenFull = EditorGUILayout.Toggle("Auto Start When Full", autoStartWhenFull);
        allowLateJoin = EditorGUILayout.Toggle("Allow Late Join", allowLateJoin);
        sendHistoryOnLateJoin = EditorGUILayout.Toggle("Send History on Late Join", sendHistoryOnLateJoin);

        EditorGUILayout.Space(2);
        GUILayout.Label("Pool", EditorStyles.miniBoldLabel);
        maxInstances = EditorGUILayout.IntField("Max Instances", maxInstances);
        autoRecycle = EditorGUILayout.Toggle("Auto Recycle on Finish", autoRecycle);
        idleTimeout = EditorGUILayout.FloatField("Idle Timeout (s)", idleTimeout);

        EditorGUILayout.Space(2);
        GUILayout.Label("Admin", EditorStyles.miniBoldLabel);
        adminAuthToken = EditorGUILayout.TextField("Auth Token", adminAuthToken);

        GUI.enabled = wasEnabled;
        EditorGUI.indentLevel--;
    }

    private void DrawControlSection()
    {
        GUILayout.Label("Server Control", EditorStyles.boldLabel);

        bool hasNodeModules = Directory.Exists(Path.Combine(ServerDir, "node_modules"));
        bool hasDist = Directory.Exists(Path.Combine(ServerDir, "dist"));

        if (!hasNodeModules)
            EditorGUILayout.HelpBox("node_modules/ not found. Click Install Dependencies (uses your login shell on macOS/Linux so npm from nvm/Homebrew/etc. is found).", MessageType.Warning);
        else if (!hasDist && !isBuilding && !isInstalling)
            EditorGUILayout.HelpBox("dist/ not found. Click Build to compile the server.", MessageType.Info);

        EditorGUILayout.BeginHorizontal();

        bool canRunTooling = !IsServerRunning() && !isBuilding && !isInstalling;

        GUI.enabled = canRunTooling;
        if (GUILayout.Button("Install Dependencies", GUILayout.Width(150)))
            StartInstall();

        GUI.enabled = canRunTooling && hasNodeModules;
        if (GUILayout.Button("Build", GUILayout.Width(60)))
            StartBuild();

        if (!IsServerRunning())
        {
            GUI.enabled = canRunTooling && hasNodeModules && hasDist;
            if (GUILayout.Button("Start Server"))
                StartServer();
        }
        else
        {
            GUI.enabled = true;
            if (GUILayout.Button("Stop Server"))
                StopServer();
        }
        GUI.enabled = true;

        EditorGUILayout.EndHorizontal();

        string status = "Stopped";
        if (isInstalling) status = "Installing dependencies...";
        else if (isBuilding) status = "Building...";
        else if (IsServerRunning()) status = "Running";

        EditorGUILayout.LabelField("Status", status);

        if (IsServerRunning())
            EditorGUILayout.LabelField("Address", $"http://localhost:{port}");
    }

    private void DrawMonitoringSection()
    {
        GUILayout.Label("Monitoring", EditorStyles.boldLabel);

        if (!string.IsNullOrEmpty(pollError))
            EditorGUILayout.HelpBox(pollError, MessageType.Warning);

        if (latestStats == null)
        {
            EditorGUILayout.LabelField("Waiting for data...");
            return;
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Instances", $"{latestStats.totalInstances} / {latestStats.totalInstances + latestStats.availableSlots}");
        EditorGUILayout.LabelField("Available Slots", latestStats.availableSlots.ToString());
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Idle", latestStats.idleCount.ToString());
        EditorGUILayout.LabelField("Waiting", latestStats.waitingCount.ToString());
        EditorGUILayout.LabelField("In Match", latestStats.inMatchCount.ToString());
        EditorGUILayout.LabelField("Finished", latestStats.finishedCount.ToString());
        EditorGUILayout.EndHorizontal();

        if (latestStats.resourceUsage != null)
        {
            var ru = latestStats.resourceUsage;
            EditorGUILayout.LabelField("Memory",
                $"Heap: {FormatBytes(ru.heapUsedBytes)}  RSS: {FormatBytes(ru.rssBytes)}  CPUs: {ru.processorCount}");
        }

        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Instances", EditorStyles.miniBoldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Create Instance", GUILayout.Width(120)))
            CreateInstance();
        EditorGUILayout.EndHorizontal();

        if (latestStats.instances != null && latestStats.instances.Length > 0)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("ID", EditorStyles.miniLabel, GUILayout.Width(80));
            GUILayout.Label("State", EditorStyles.miniLabel, GUILayout.Width(100));
            GUILayout.Label("Players", EditorStyles.miniLabel, GUILayout.Width(55));
            GUILayout.Label("Joined", EditorStyles.miniLabel, GUILayout.Width(50));
            GUILayout.Label("Step", EditorStyles.miniLabel, GUILayout.Width(45));
            GUILayout.Label("Uptime", EditorStyles.miniLabel, GUILayout.Width(60));
            GUILayout.Label("", GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            instancesScroll = EditorGUILayout.BeginScrollView(instancesScroll, GUILayout.MaxHeight(150));
            foreach (var inst in latestStats.instances)
            {
                EditorGUILayout.BeginHorizontal();
                string shortId = inst.id.Length > 8 ? inst.id.Substring(0, 8) : inst.id;
                GUILayout.Label(shortId, EditorStyles.miniLabel, GUILayout.Width(80));
                GUILayout.Label(inst.state, EditorStyles.miniLabel, GUILayout.Width(100));
                GUILayout.Label(inst.playerCount.ToString(), EditorStyles.miniLabel, GUILayout.Width(55));
                GUILayout.Label(inst.joinedPlayerCount.ToString(), EditorStyles.miniLabel, GUILayout.Width(50));
                GUILayout.Label(inst.currentStep.ToString(), EditorStyles.miniLabel, GUILayout.Width(45));
                GUILayout.Label($"{inst.uptimeSeconds:F0}s", EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button("Delete", EditorStyles.miniButton, GUILayout.Width(50)))
                    DeleteInstance(inst.id);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
        else
        {
            EditorGUILayout.LabelField("No instances running.");
        }
    }

    private void DrawConsoleSection()
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Console Output", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(50)))
            consoleLines.Clear();
        EditorGUILayout.EndHorizontal();

        consoleScroll = EditorGUILayout.BeginScrollView(consoleScroll, GUILayout.Height(120));
        if (consoleLines.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var line in consoleLines)
                sb.AppendLine(line);
            EditorGUILayout.TextArea(sb.ToString(), EditorStyles.miniLabel, GUILayout.ExpandHeight(true));
        }
        else
        {
            EditorGUILayout.LabelField("(no output)");
        }
        EditorGUILayout.EndScrollView();
    }

    // -------------------------
    // PROCESS: NPM INSTALL
    // -------------------------

    private void StartInstall()
    {
        if (isInstalling || isBuilding) return;

        isInstalling = true;
        AppendConsole("[Editor] Running npm install...");

        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = ServerDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            ApplyNpmCommand(psi, "install");

            installProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            installProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) AppendConsole("[npm install] " + e.Data);
            };
            installProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) AppendConsole("[npm install] [stderr] " + e.Data);
            };
            installProcess.Start();
            installProcess.BeginOutputReadLine();
            installProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendConsole($"[Editor] npm install failed to start: {ex.Message}");
            isInstalling = false;
            installProcess = null;
        }

        Repaint();
    }

    private void CheckInstallExited()
    {
        if (installProcess == null || !isInstalling) return;

        try
        {
            if (installProcess.HasExited)
            {
                int code = installProcess.ExitCode;
                AppendConsole($"[Editor] npm install finished (exit code {code})");
                installProcess.Dispose();
                installProcess = null;
                isInstalling = false;
                Repaint();
            }
        }
        catch
        {
            installProcess = null;
            isInstalling = false;
        }
    }

    private void StopInstall()
    {
        if (installProcess != null)
        {
            try { if (!installProcess.HasExited) installProcess.Kill(); } catch { }
            installProcess.Dispose();
            installProcess = null;
        }
        isInstalling = false;
    }

    // -------------------------
    // PROCESS: BUILD
    // -------------------------

    private void StartBuild()
    {
        if (isBuilding || isInstalling) return;

        isBuilding = true;
        AppendConsole("[Editor] Building server (npm run build)...");

        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = ServerDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            ApplyNpmCommand(psi, "run build");

            buildProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            buildProcess.OutputDataReceived += (s, e) => { if (e.Data != null) AppendConsole(e.Data); };
            buildProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendConsole("[stderr] " + e.Data); };
            buildProcess.Start();
            buildProcess.BeginOutputReadLine();
            buildProcess.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendConsole($"[Editor] Build failed to start: {ex.Message}");
            isBuilding = false;
            buildProcess = null;
        }

        Repaint();
    }

    private void CheckBuildExited()
    {
        if (buildProcess == null || !isBuilding) return;

        try
        {
            if (buildProcess.HasExited)
            {
                int code = buildProcess.ExitCode;
                AppendConsole($"[Editor] Build finished (exit code {code})");
                buildProcess.Dispose();
                buildProcess = null;
                isBuilding = false;
                Repaint();
            }
        }
        catch
        {
            buildProcess = null;
            isBuilding = false;
        }
    }

    private void StopBuild()
    {
        if (buildProcess != null)
        {
            try { if (!buildProcess.HasExited) buildProcess.Kill(); } catch { }
            buildProcess.Dispose();
            buildProcess = null;
        }
        isBuilding = false;
    }

    // -------------------------
    // PROCESS: SERVER
    // -------------------------

    private void StartServer()
    {
        if (IsServerRunning() || isBuilding || isInstalling) return;

        SavePrefs();
        AppendConsole($"[Editor] Starting server on port {port}...");

        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = ServerDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var resolvedNode = EditorPrefs.GetString("SocketIOServer_NodePath", "");
            if (!string.IsNullOrEmpty(resolvedNode) && File.Exists(resolvedNode))
            {
                psi.FileName = resolvedNode;
                psi.Arguments = "dist/main.js";
            }
            else
            {
                // Match ApplyNpmCommand: Unity's process PATH often omits nvm/Homebrew; login shell finds node.
                ApplyNodeScriptCommand(psi, "dist/main.js");
            }

            psi.EnvironmentVariables["INPUT_SYNCER_PORT"] = port.ToString();
            psi.EnvironmentVariables["INPUT_SYNCER_MAX_PLAYERS"] = maxPlayers.ToString();
            psi.EnvironmentVariables["INPUT_SYNCER_STEP_INTERVAL"] = stepInterval.ToString(System.Globalization.CultureInfo.InvariantCulture);
            psi.EnvironmentVariables["INPUT_SYNCER_AUTO_START_WHEN_FULL"] = autoStartWhenFull ? "true" : "false";
            psi.EnvironmentVariables["INPUT_SYNCER_ALLOW_LATE_JOIN"] = allowLateJoin ? "true" : "false";
            psi.EnvironmentVariables["INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN"] = sendHistoryOnLateJoin ? "true" : "false";
            psi.EnvironmentVariables["INPUT_SYNCER_ADMIN_AUTH_TOKEN"] = adminAuthToken;
            psi.EnvironmentVariables["INPUT_SYNCER_MAX_INSTANCES"] = maxInstances.ToString();
            psi.EnvironmentVariables["INPUT_SYNCER_AUTO_RECYCLE"] = autoRecycle ? "true" : "false";
            psi.EnvironmentVariables["INPUT_SYNCER_IDLE_TIMEOUT"] = idleTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture);

            serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            serverProcess.OutputDataReceived += (s, e) => { if (e.Data != null) AppendConsole(e.Data); };
            serverProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendConsole("[stderr] " + e.Data); };
            serverProcess.Start();
            serverProcess.BeginOutputReadLine();
            serverProcess.BeginErrorReadLine();

            latestStats = null;
            pollError = null;
            lastPollTime = 0;
        }
        catch (Exception ex)
        {
            AppendConsole($"[Editor] Failed to start server: {ex.Message}");
            serverProcess = null;
        }

        Repaint();
    }

    private void StopServer()
    {
        if (serverProcess == null) return;

        AppendConsole("[Editor] Stopping server...");
        try
        {
            if (!serverProcess.HasExited)
            {
                serverProcess.Kill();
                serverProcess.WaitForExit(3000);
            }
        }
        catch { }

        serverProcess.Dispose();
        serverProcess = null;
        latestStats = null;
        pollError = null;
        DisposeRequest(ref activeStatsRequest);
        DisposeRequest(ref activeActionRequest);
        Repaint();
    }

    private void CheckProcessExited()
    {
        if (serverProcess == null) return;

        try
        {
            if (serverProcess.HasExited)
            {
                int code = serverProcess.ExitCode;
                AppendConsole($"[Editor] Server process exited (code {code})");
                serverProcess.Dispose();
                serverProcess = null;
                latestStats = null;
                Repaint();
            }
        }
        catch
        {
            serverProcess = null;
            latestStats = null;
        }
    }

    private bool IsServerRunning()
    {
        if (serverProcess == null) return false;
        try { return !serverProcess.HasExited; }
        catch { serverProcess = null; return false; }
    }

    // -------------------------
    // ADMIN API POLLING
    // -------------------------

    private void PollStats()
    {
        if (!IsServerRunning()) return;
        if (activeStatsRequest != null) return;

        if (EditorApplication.timeSinceStartup - lastPollTime < PollIntervalSeconds) return;
        lastPollTime = EditorApplication.timeSinceStartup;

        string url = $"http://localhost:{port}/api/stats";
        activeStatsRequest = UnityWebRequest.Get(url);

        if (!string.IsNullOrEmpty(adminAuthToken))
            activeStatsRequest.SetRequestHeader("Authorization", $"Bearer {adminAuthToken}");

        activeStatsRequest.SendWebRequest();
    }

    private void ProcessStatsResponse()
    {
        if (activeStatsRequest == null || !activeStatsRequest.isDone) return;

        if (activeStatsRequest.result == UnityWebRequest.Result.Success)
        {
            try
            {
                latestStats = JsonUtility.FromJson<PoolStats>(activeStatsRequest.downloadHandler.text);
                pollError = null;
            }
            catch (Exception ex)
            {
                pollError = $"JSON parse error: {ex.Message}";
            }
        }
        else
        {
            pollError = activeStatsRequest.error;
        }

        DisposeRequest(ref activeStatsRequest);
        Repaint();
    }

    // -------------------------
    // ADMIN API ACTIONS
    // -------------------------

    private void CreateInstance()
    {
        if (activeActionRequest != null) return;

        string url = $"http://localhost:{port}/api/instances";
        activeActionRequest = new UnityWebRequest(url, "POST");
        activeActionRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
        activeActionRequest.downloadHandler = new DownloadHandlerBuffer();
        activeActionRequest.SetRequestHeader("Content-Type", "application/json");

        if (!string.IsNullOrEmpty(adminAuthToken))
            activeActionRequest.SetRequestHeader("Authorization", $"Bearer {adminAuthToken}");

        activeActionRequest.SendWebRequest();
        AppendConsole("[Editor] Creating instance...");
    }

    private void DeleteInstance(string id)
    {
        if (activeActionRequest != null) return;

        string url = $"http://localhost:{port}/api/instances/{id}";
        activeActionRequest = UnityWebRequest.Delete(url);
        activeActionRequest.downloadHandler = new DownloadHandlerBuffer();

        if (!string.IsNullOrEmpty(adminAuthToken))
            activeActionRequest.SetRequestHeader("Authorization", $"Bearer {adminAuthToken}");

        activeActionRequest.SendWebRequest();
        AppendConsole($"[Editor] Deleting instance {id.Substring(0, Math.Min(8, id.Length))}...");
    }

    private void ProcessActionResponse()
    {
        if (activeActionRequest == null || !activeActionRequest.isDone) return;

        if (activeActionRequest.result == UnityWebRequest.Result.Success)
            AppendConsole("[Editor] Action succeeded");
        else
            AppendConsole($"[Editor] Action failed: {activeActionRequest.error}");

        DisposeRequest(ref activeActionRequest);
        lastPollTime = 0;
        Repaint();
    }

    // -------------------------
    // CONSOLE
    // -------------------------

    private void AppendConsole(string line)
    {
        lock (consoleLines)
        {
            consoleLines.Add(line);
            while (consoleLines.Count > MaxConsoleLines)
                consoleLines.RemoveAt(0);
        }
        consoleScroll = new Vector2(0, float.MaxValue);
    }

    // -------------------------
    // PREFS
    // -------------------------

    private void SavePrefs()
    {
        EditorPrefs.SetInt("SocketIOServer_Port", port);
        EditorPrefs.SetInt("SocketIOServer_MaxPlayers", maxPlayers);
        EditorPrefs.SetFloat("SocketIOServer_StepInterval", stepInterval);
        EditorPrefs.SetBool("SocketIOServer_AutoStartWhenFull", autoStartWhenFull);
        EditorPrefs.SetBool("SocketIOServer_AllowLateJoin", allowLateJoin);
        EditorPrefs.SetBool("SocketIOServer_SendHistoryOnLateJoin", sendHistoryOnLateJoin);
        EditorPrefs.SetString("SocketIOServer_AdminAuthToken", adminAuthToken);
        EditorPrefs.SetInt("SocketIOServer_MaxInstances", maxInstances);
        EditorPrefs.SetBool("SocketIOServer_AutoRecycle", autoRecycle);
        EditorPrefs.SetFloat("SocketIOServer_IdleTimeout", idleTimeout);
    }

    private void LoadPrefs()
    {
        port = (ushort)EditorPrefs.GetInt("SocketIOServer_Port", 3000);
        maxPlayers = EditorPrefs.GetInt("SocketIOServer_MaxPlayers", 2);
        stepInterval = EditorPrefs.GetFloat("SocketIOServer_StepInterval", 0.1f);
        autoStartWhenFull = EditorPrefs.GetBool("SocketIOServer_AutoStartWhenFull", true);
        allowLateJoin = EditorPrefs.GetBool("SocketIOServer_AllowLateJoin", false);
        sendHistoryOnLateJoin = EditorPrefs.GetBool("SocketIOServer_SendHistoryOnLateJoin", true);
        adminAuthToken = EditorPrefs.GetString("SocketIOServer_AdminAuthToken", "");
        maxInstances = EditorPrefs.GetInt("SocketIOServer_MaxInstances", 10);
        autoRecycle = EditorPrefs.GetBool("SocketIOServer_AutoRecycle", true);
        idleTimeout = EditorPrefs.GetFloat("SocketIOServer_IdleTimeout", 0f);
    }

    // -------------------------
    // HELPERS
    // -------------------------

    private static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:F0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    // Unity's environment often lacks PATH entries for npm (nvm/Homebrew); login+interactive shell fixes that on Unix.
    private static void ApplyNpmCommand(ProcessStartInfo psi, string npmArguments)
    {
#if UNITY_EDITOR_WIN
        psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        psi.Arguments = "/c npm " + npmArguments;
#else
        var shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
        psi.FileName = shell;
        var escaped = npmArguments.Replace("\\", "\\\\").Replace("\"", "\\\"");
        psi.Arguments = "-ilc \"npm " + escaped + "\"";
#endif
    }

    private static void ApplyNodeScriptCommand(ProcessStartInfo psi, string relativeScriptPath)
    {
#if UNITY_EDITOR_WIN
        psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        psi.Arguments = "/c node " + relativeScriptPath;
#else
        var shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
        psi.FileName = shell;
        var escaped = relativeScriptPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        psi.Arguments = "-ilc \"node " + escaped + "\"";
#endif
    }

    private static void DisposeRequest(ref UnityWebRequest request)
    {
        if (request != null)
        {
            request.Dispose();
            request = null;
        }
    }

    // -------------------------
    // JSON DTOs
    // -------------------------

    [Serializable]
    private class InstanceInfo
    {
        public string id;
        public string state;
        public string createdAt;
        public int playerCount;
        public int joinedPlayerCount;
        public int currentStep;
        public bool matchStarted;
        public bool matchFinished;
        public float uptimeSeconds;
    }

    [Serializable]
    private class ResourceUsage
    {
        public double heapUsedBytes;
        public double rssBytes;
        public int processorCount;
    }

    [Serializable]
    private class PoolStats
    {
        public int totalInstances;
        public int availableSlots;
        public int idleCount;
        public int waitingCount;
        public int inMatchCount;
        public int finishedCount;
        public InstanceInfo[] instances;
        public ResourceUsage resourceUsage;
    }
}
