using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private static string sessionResolvedNodeExe;
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

    private GUIStyle _sectionPanelStyle;
    private GUIStyle _sectionTitleStyle;
    private GUIStyle _statValueStyle;
    private GUIStyle _statLabelStyle;
    private GUIStyle _consoleRichStyle;

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
        BeginSectionPanel();
        GUILayout.Label("Server Control", SectionTitleStyle);

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

        EditorGUILayout.Space(6);
        DrawServerStatusRow();

        EndSectionPanel();
    }

    private void DrawServerStatusRow()
    {
        string statusText;
        Color dotColor;
        if (isInstalling)
        {
            statusText = "Installing dependencies…";
            dotColor = new Color(0.95f, 0.75f, 0.35f);
        }
        else if (isBuilding)
        {
            statusText = "Building…";
            dotColor = new Color(0.95f, 0.75f, 0.35f);
        }
        else if (IsServerRunning())
        {
            statusText = "Running";
            dotColor = new Color(0.35f, 0.82f, 0.45f);
        }
        else
        {
            statusText = "Stopped";
            dotColor = new Color(0.85f, 0.35f, 0.35f);
        }

        EditorGUILayout.BeginHorizontal();
        var dotRect = GUILayoutUtility.GetRect(10, 18, GUILayout.Width(10));
        EditorGUI.DrawRect(new Rect(dotRect.x, dotRect.y + 5, 8, 8), dotColor);
        EditorGUILayout.LabelField("Status", GUILayout.Width(52));
        GUILayout.Label(statusText, EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        if (IsServerRunning())
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(18);
            EditorGUILayout.LabelField("Address", GUILayout.Width(52));
            var addr = $"http://localhost:{port}";
            if (GUILayout.Button(new GUIContent(addr, "Open in browser"), EditorStyles.linkLabel))
                Application.OpenURL(addr);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
    }

    private void DrawMonitoringSection()
    {
        BeginSectionPanel();
        GUILayout.Label("Monitoring & pool", SectionTitleStyle);

        if (!string.IsNullOrEmpty(pollError))
            EditorGUILayout.HelpBox(pollError, MessageType.Warning);

        if (latestStats == null)
        {
            EditorGUILayout.LabelField("Polling admin API…", EditorStyles.miniLabel);
            EndSectionPanel();
            return;
        }

        int capacity = Mathf.Max(1, latestStats.totalInstances + latestStats.availableSlots);
        float usedFrac = capacity > 0 ? (float)latestStats.totalInstances / capacity : 0f;

        EditorGUILayout.LabelField("Pool capacity", EditorStyles.miniBoldLabel);
        var barRect = EditorGUILayout.GetControlRect(false, 20);
        EditorGUI.ProgressBar(barRect, usedFrac,
            $"{latestStats.totalInstances} / {capacity} instances · {latestStats.availableSlots} slots free");

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Instance states", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawStatChip("Idle", latestStats.idleCount, new Color(0.55f, 0.62f, 0.75f));
        DrawStatChip("Waiting", latestStats.waitingCount, new Color(0.95f, 0.75f, 0.35f));
        DrawStatChip("In match", latestStats.inMatchCount, new Color(0.35f, 0.78f, 0.55f));
        DrawStatChip("Finished", latestStats.finishedCount, new Color(0.65f, 0.65f, 0.68f));
        EditorGUILayout.EndHorizontal();

        if (latestStats.resourceUsage != null)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Process memory", EditorStyles.miniBoldLabel);
            var ru = latestStats.resourceUsage;
            double scale = Math.Max(Math.Max(ru.heapUsedBytes, ru.rssBytes), 1.0);
            DrawMemoryMeter("Heap (Node)", ru.heapUsedBytes, scale);
            DrawMemoryMeter("RSS", ru.rssBytes, scale);
            EditorGUILayout.LabelField($"Logical CPUs: {ru.processorCount}", EditorStyles.miniLabel);
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Instances", EditorStyles.miniBoldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Create Instance", GUILayout.Width(120)))
            CreateInstance();
        EditorGUILayout.EndHorizontal();

        if (latestStats.instances != null && latestStats.instances.Length > 0)
        {
            DrawInstanceTableHeader();
            instancesScroll = EditorGUILayout.BeginScrollView(instancesScroll, GUILayout.MaxHeight(160));
            foreach (var inst in latestStats.instances)
                DrawInstanceRow(inst);
            EditorGUILayout.EndScrollView();
        }
        else
        {
            EditorGUILayout.HelpBox("No match instances yet. Create one to allocate a slot from the pool.", MessageType.Info);
        }

        EndSectionPanel();
    }

    private void DrawInstanceTableHeader()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("ID", EditorStyles.miniBoldLabel, GUILayout.Width(80));
        GUILayout.Label("State", EditorStyles.miniBoldLabel, GUILayout.Width(100));
        GUILayout.Label("Players", EditorStyles.miniBoldLabel, GUILayout.Width(55));
        GUILayout.Label("Joined", EditorStyles.miniBoldLabel, GUILayout.Width(50));
        GUILayout.Label("Step", EditorStyles.miniBoldLabel, GUILayout.Width(45));
        GUILayout.Label("Uptime", EditorStyles.miniBoldLabel, GUILayout.Width(60));
        GUILayout.Label("", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();
    }

    private void DrawInstanceRow(InstanceInfo inst)
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

    private void DrawConsoleSection()
    {
        BeginSectionPanel();
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Console output", SectionTitleStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Clear", GUILayout.Width(50)))
            consoleLines.Clear();
        EditorGUILayout.EndHorizontal();

        consoleScroll = EditorGUILayout.BeginScrollView(consoleScroll, GUILayout.Height(140));
        if (consoleLines.Count > 0)
        {
            lock (consoleLines)
            {
                foreach (var line in consoleLines)
                {
                    string rich = AnsiToUnityRichText(line);
                    GUILayout.Label(rich, ConsoleRichStyle);
                }
            }
        }
        else
        {
            EditorGUILayout.LabelField("(no output yet)", EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
        EndSectionPanel();
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

            if (TryGetNodeExecutableForServer(out var nodeExe))
            {
                psi.FileName = nodeExe;
                psi.Arguments = "dist/main.js";
            }
            else
                ApplyNodeScriptCommand(psi, "dist/main.js");

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

    private GUIStyle SectionPanelStyle
    {
        get
        {
            if (_sectionPanelStyle == null)
            {
                _sectionPanelStyle = new GUIStyle(EditorStyles.helpBox);
                _sectionPanelStyle.padding = new RectOffset(12, 12, 10, 10);
                _sectionPanelStyle.margin = new RectOffset(0, 0, 0, 2);
            }
            return _sectionPanelStyle;
        }
    }

    private GUIStyle SectionTitleStyle
    {
        get
        {
            if (_sectionTitleStyle == null)
            {
                _sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel);
                _sectionTitleStyle.margin = new RectOffset(0, 0, 2, 8);
            }
            return _sectionTitleStyle;
        }
    }

    private GUIStyle StatValueStyle
    {
        get
        {
            if (_statValueStyle == null)
            {
                _statValueStyle = new GUIStyle(EditorStyles.boldLabel);
                _statValueStyle.fontSize = EditorStyles.boldLabel.fontSize + 4;
                _statValueStyle.alignment = TextAnchor.MiddleCenter;
            }
            return _statValueStyle;
        }
    }

    private GUIStyle StatLabelStyle
    {
        get
        {
            if (_statLabelStyle == null)
            {
                _statLabelStyle = new GUIStyle(EditorStyles.miniLabel);
                _statLabelStyle.alignment = TextAnchor.MiddleCenter;
                _statLabelStyle.normal.textColor = EditorGUIUtility.isProSkin
                    ? new Color(0.65f, 0.65f, 0.68f)
                    : new Color(0.35f, 0.35f, 0.38f);
            }
            return _statLabelStyle;
        }
    }

    private GUIStyle ConsoleRichStyle
    {
        get
        {
            if (_consoleRichStyle == null)
            {
                _consoleRichStyle = new GUIStyle(EditorStyles.miniLabel);
                _consoleRichStyle.richText = true;
                _consoleRichStyle.wordWrap = false;
                _consoleRichStyle.margin = new RectOffset(0, 0, 0, 1);
            }
            return _consoleRichStyle;
        }
    }

    private void BeginSectionPanel()
    {
        EditorGUILayout.BeginVertical(SectionPanelStyle);
    }

    private void EndSectionPanel()
    {
        EditorGUILayout.EndVertical();
    }

    private void DrawStatChip(string label, int value, Color accent)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinWidth(64), GUILayout.ExpandWidth(true));
        var prev = GUI.contentColor;
        GUI.contentColor = accent;
        GUILayout.Label(value.ToString(), StatValueStyle);
        GUI.contentColor = prev;
        GUILayout.Label(label, StatLabelStyle);
        EditorGUILayout.EndVertical();
    }

    private static void DrawMemoryMeter(string label, double valueBytes, double scaleBytes)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(100));
        Rect r = EditorGUILayout.GetControlRect(false, 18, GUILayout.ExpandWidth(true));
        float t = scaleBytes > 0 ? Mathf.Clamp01((float)(valueBytes / scaleBytes)) : 0f;
        EditorGUI.ProgressBar(r, t, FormatBytes(valueBytes));
        EditorGUILayout.EndHorizontal();
    }

    private static string AnsiToUnityRichText(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";
        if (input.IndexOf('\u001b', StringComparison.Ordinal) < 0)
            return EscapeUnityRichTextLiteral(input);

        var sb = new StringBuilder(input.Length + 32);
        bool bold = false;
        bool colorOpen = false;
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c == '\x1b' && i + 1 < input.Length && input[i + 1] == '[')
            {
                int start = i + 2;
                int j = start;
                while (j < input.Length)
                {
                    char ch = input[j];
                    if (ch >= '@' && ch <= '~')
                    {
                        j++;
                        break;
                    }
                    j++;
                }
                if (j > start)
                {
                    char cmd = input[j - 1];
                    if (cmd == 'm')
                    {
                        string code = input.Substring(start, j - 1 - start);
                        ApplySgrToRichText(sb, ref bold, ref colorOpen, code);
                    }
                }
                i = j;
                continue;
            }

            if (c == '\x1b' && i + 1 < input.Length && input[i + 1] == ']')
            {
                int j = i + 2;
                while (j < input.Length)
                {
                    if (input[j] == '\a')
                    {
                        j++;
                        break;
                    }
                    if (input[j] == '\x1b' && j + 1 < input.Length && input[j + 1] == '\\')
                    {
                        j += 2;
                        break;
                    }
                    j++;
                }
                i = j;
                continue;
            }

            AppendRichTextChar(sb, c);
            i++;
        }

        if (colorOpen)
            sb.Append("</color>");
        if (bold)
            sb.Append("</b>");
        return sb.ToString();
    }

    private static void AppendRichTextChar(StringBuilder sb, char c)
    {
        if (c == '<')
            sb.Append("\\<");
        else
            sb.Append(c);
    }

    private static string EscapeUnityRichTextLiteral(string input)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('<') < 0)
            return input;
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
            AppendRichTextChar(sb, c);
        return sb.ToString();
    }

    private static void ApplySgrToRichText(StringBuilder sb, ref bool bold, ref bool colorOpen, string code)
    {
        int[] parts = ParseSemicolonNumbers(code);
        if (parts.Length == 0)
        {
            CloseSgrRichText(sb, ref bold, ref colorOpen);
            return;
        }

        for (int idx = 0; idx < parts.Length;)
        {
            int n = parts[idx];
            if (n == 0)
            {
                CloseSgrRichText(sb, ref bold, ref colorOpen);
                idx++;
                continue;
            }
            if (n == 1)
            {
                if (!bold)
                {
                    sb.Append("<b>");
                    bold = true;
                }
                idx++;
                continue;
            }
            if (n == 22 || n == 21)
            {
                if (bold)
                {
                    sb.Append("</b>");
                    bold = false;
                }
                idx++;
                continue;
            }
            if (n == 39 || n == 49)
            {
                if (colorOpen && n == 39)
                {
                    sb.Append("</color>");
                    colorOpen = false;
                }
                idx++;
                continue;
            }
            if (n >= 30 && n <= 37)
            {
                OpenColor(sb, ref colorOpen, AnsiFg4Bit(n - 30, false));
                idx++;
                continue;
            }
            if (n >= 90 && n <= 97)
            {
                OpenColor(sb, ref colorOpen, AnsiFg4Bit(n - 90, true));
                idx++;
                continue;
            }
            if (n == 38 && idx + 2 < parts.Length && parts[idx + 1] == 5)
            {
                OpenColor(sb, ref colorOpen, Ansi256ToHex(parts[idx + 2]));
                idx += 3;
                continue;
            }
            if (n == 38 && idx + 4 < parts.Length && parts[idx + 1] == 2)
            {
                OpenColor(sb, ref colorOpen, RgbToHex(parts[idx + 2], parts[idx + 3], parts[idx + 4]));
                idx += 5;
                continue;
            }
            idx++;
        }
    }

    private static void CloseSgrRichText(StringBuilder sb, ref bool bold, ref bool colorOpen)
    {
        if (colorOpen)
        {
            sb.Append("</color>");
            colorOpen = false;
        }
        if (bold)
        {
            sb.Append("</b>");
            bold = false;
        }
    }

    private static void OpenColor(StringBuilder sb, ref bool colorOpen, string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return;
        if (colorOpen)
            sb.Append("</color>");
        sb.Append("<color=#").Append(hex).Append(">");
        colorOpen = true;
    }

    private static int[] ParseSemicolonNumbers(string code)
    {
        if (string.IsNullOrEmpty(code))
            return Array.Empty<int>();
        var segs = code.Split(';');
        var nums = new List<int>(segs.Length);
        foreach (var seg in segs)
        {
            if (int.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                nums.Add(v);
        }
        return nums.ToArray();
    }

    private static string RgbToHex(int r, int g, int b)
    {
        r = Mathf.Clamp(r, 0, 255);
        g = Mathf.Clamp(g, 0, 255);
        b = Mathf.Clamp(b, 0, 255);
        return (r << 16 | g << 8 | b).ToString("x6", CultureInfo.InvariantCulture);
    }

    private static string AnsiFg4Bit(int i, bool bright)
    {
        string[] dark =
        {
            "3C3C3C", "E06C75", "98C379", "E5C07B", "61AFEF", "C678DD", "56B6C2", "ABB2BF"
        };
        string[] light =
        {
            "5C6370", "FF7B85", "B5E890", "FFDA6A", "82CFFF", "D898FF", "7FE9F5", "FFFFFF"
        };
        if (i < 0 || i > 7)
            return "ABB2BF";
        return bright ? light[i] : dark[i];
    }

    private static string Ansi256ToHex(int n)
    {
        if (n < 0)
            return "ABB2BF";
        if (n < 16)
            return n < 8 ? AnsiFg4Bit(n, false) : AnsiFg4Bit(n - 8, true);
        if (n >= 16 && n <= 231)
        {
            int x = n - 16;
            int r = x / 36;
            int g = (x % 36) / 6;
            int b = x % 6;
            int ri = r > 0 ? 55 + r * 40 : 0;
            int gi = g > 0 ? 55 + g * 40 : 0;
            int bi = b > 0 ? 55 + b * 40 : 0;
            return RgbToHex(ri, gi, bi);
        }
        if (n >= 232 && n <= 255)
        {
            int v = 8 + (n - 232) * 10;
            return RgbToHex(v, v, v);
        }
        return "ABB2BF";
    }

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

    private static bool TryGetNodeExecutableForServer(out string nodeExe)
    {
        var user = EditorPrefs.GetString("SocketIOServer_NodePath", "");
        if (!string.IsNullOrEmpty(user) && File.Exists(user))
        {
            nodeExe = user;
            return true;
        }

        if (!string.IsNullOrEmpty(sessionResolvedNodeExe) && File.Exists(sessionResolvedNodeExe))
        {
            nodeExe = sessionResolvedNodeExe;
            return true;
        }

        var probed = ProbeNodeExecutableFromLoginShell();
        if (!string.IsNullOrEmpty(probed) && File.Exists(probed))
        {
            sessionResolvedNodeExe = probed;
            nodeExe = probed;
            return true;
        }

        nodeExe = null;
        return false;
    }

    private static string ProbeNodeExecutableFromLoginShell()
    {
#if UNITY_EDITOR_WIN
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c where node",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(20000);
                if (p.ExitCode != 0) return null;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = line.Trim();
                    if (t.Length > 0 && File.Exists(t))
                        return t;
                }
            }
        }
        catch
        {
            return null;
        }
        return null;
#else
        try
        {
            var shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-ilc \"command -v node\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(20000);
                if (p.ExitCode != 0) return null;
                var line = output.Trim().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (line.Length == 0) return null;
                var path = line[0].Trim();
                return path.Length > 0 && File.Exists(path) ? path : null;
            }
        }
        catch
        {
            return null;
        }
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
