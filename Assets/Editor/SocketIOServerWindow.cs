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
    /// <summary>
    /// When set, admin HTTP polling (stats / instances) uses this root instead of <c>http://localhost:{port}</c>.
    /// Use for a Socket.IO server running on a remote dev machine (e.g. <c>https://dev.example.com</c> or <c>http://192.168.1.10:3000</c>).
    /// </summary>
    private string adminApiBaseUrl = "";
    private int maxInstances = 10;
    private bool autoRecycle = true;
    private float idleTimeout = 0f;
    /// <summary>When true, Unity starts <c>dist/cluster-primary.js</c> (multi-process + one public port).</summary>
    private bool useMultiCoreCluster;
    /// <summary>0 = Node default max(1, CPU count − 1); otherwise <c>INPUT_SYNCER_WORKER_COUNT</c>.</summary>
    private int clusterWorkerCount;

    private bool showConfig = true;
    /// <summary>
    /// When true, Unity does not spawn Node; you run the server in your own terminal and this window
    /// still polls the admin HTTP API (and optionally tails INPUT_SYNCER_EDITOR_LOG if present).
    /// </summary>
    private bool runServerExternally;
    private bool showExternalTerminalTips = true;

    // -------------------------
    // Process state
    // -------------------------
    /// <summary>
    /// Shared across window instances so the Node server keeps running when Play Mode disables
    /// this EditorWindow. Domain reload clears statics, so the child PID is stored in EditorPrefs
    /// and we reattach with <see cref="Process.GetProcessById"/> after reload.
    /// </summary>
    private static Process s_sharedServerProcess;

    private const string ServerChildPidPrefKey = "SocketIOServer_ChildPid";
    private const string EditorLogPathPrefKey = "SocketIOServer_EditorLogPath";
    private const string SessionConsoleKey = "InputSyncer.SocketIOServer.ConsoleLines.v1";
    private const string ConsoleLineSeparator = "\u001e";
    private static bool s_loggedReattachNoticeThisDomain;
    private static bool s_stdioRedirectActive;
    private static long s_editorLogTailBytes;
    private static string s_editorLogCarry = "";
    private static double s_lastEditorLogPollTime;

    static SocketIOServerWindow()
    {
        // Do not call EditorPrefs or TryReattach here — Unity forbids EditorPrefs during
        // ScriptableObject / EditorWindow type initialization (.cctor). Reattach in OnEnable instead.
        EditorApplication.quitting += StopSharedServerOnEditorQuit;
    }

    private static void StopSharedServerOnEditorQuit()
    {
        StopSharedServerProcess(killMessageToConsole: false);
    }

    private static void StoreServerChildPid(int pid)
    {
        if (pid > 0)
            EditorPrefs.SetInt(ServerChildPidPrefKey, pid);
    }

    private static void ClearStoredServerChildPid()
    {
        EditorPrefs.DeleteKey(ServerChildPidPrefKey);
    }

    /// <summary>
    /// After a script/domain reload, <see cref="s_sharedServerProcess"/> is null but Node may still be running.
    /// Restore a <see cref="Process"/> handle from the persisted PID so monitoring and Stop keep working.
    /// </summary>
    /// <returns>True if a new handle was acquired from the stored PID (domain reload path).</returns>
    private static bool TryReattachToStoredServerProcess()
    {
        if (s_sharedServerProcess != null)
            return false;

        int pid = EditorPrefs.GetInt(ServerChildPidPrefKey, 0);
        if (pid <= 0)
            return false;

        try
        {
            var p = Process.GetProcessById(pid);
            p.Refresh();
            if (p.HasExited)
            {
                ClearStoredServerChildPid();
                p.Dispose();
                return false;
            }

            // Avoid PID reuse: another process may have taken this id after Node exited.
            if (!LooksLikeNodeProcess(p))
            {
                p.Dispose();
                ClearStoredServerChildPid();
                EditorApplication.delayCall += () =>
                    AppendConsoleToAllOpenWindows(
                        $"[Editor] Ignoring stored server PID {pid} (process name is not Node — likely PID reuse). Use Start Server.");
                return false;
            }

            s_sharedServerProcess = p;
            return true;
        }
        catch (ArgumentException)
        {
            ClearStoredServerChildPid();
        }
        catch (InvalidOperationException)
        {
            ClearStoredServerChildPid();
        }
        catch
        {
            ClearStoredServerChildPid();
        }

        return false;
    }

    private static bool LooksLikeNodeProcess(Process p)
    {
        try
        {
            return string.Equals(p.ProcessName, "node", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kills the shared Node process if running. Safe from any thread for the Kill/Dispose portion.</summary>
    private static void StopSharedServerProcess(bool killMessageToConsole)
    {
        TryReattachToStoredServerProcess();

        if (s_sharedServerProcess == null)
            return;

        if (killMessageToConsole)
            AppendConsoleToAllOpenWindows("[Editor] Stopping server...");

        try
        {
            if (!s_sharedServerProcess.HasExited)
            {
                s_sharedServerProcess.Kill();
                s_sharedServerProcess.WaitForExit(3000);
            }
        }
        catch { }

        try
        {
            s_sharedServerProcess.Dispose();
        }
        catch { }

        s_sharedServerProcess = null;
        ClearStoredServerChildPid();
        s_stdioRedirectActive = false;
    }
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

    private static readonly string[] MainTabNames = { "Setup", "Monitor", "Console" };
    private int mainTab;
    private Vector2 mainTabScroll;

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
        var w = GetWindow<SocketIOServerWindow>("Socket.IO Server");
        w.minSize = new Vector2(400f, 280f);
    }

    private static void PersistConsoleSnapshot(SocketIOServerWindow source)
    {
        if (source == null) return;
        lock (source.consoleLines)
        {
            SessionState.SetString(
                SessionConsoleKey,
                string.Join(ConsoleLineSeparator, source.consoleLines));
        }
    }

    private void RestoreConsoleFromSession()
    {
        var blob = SessionState.GetString(SessionConsoleKey, "");
        if (string.IsNullOrEmpty(blob)) return;
        lock (consoleLines)
        {
            consoleLines.Clear();
            foreach (var segment in blob.Split(
                         new[] { ConsoleLineSeparator }, StringSplitOptions.None))
                consoleLines.Add(segment);
            while (consoleLines.Count > MaxConsoleLines)
                consoleLines.RemoveAt(0);
        }
    }

    /// <summary>
    /// After domain reload we lose stdout pipes; Node still mirrors to INPUT_SYNCER_EDITOR_LOG — tail from EOF onward.
    /// </summary>
    private static void InitLogTailAfterReattach()
    {
        s_stdioRedirectActive = false;
        s_editorLogCarry = "";
        var logPath = EditorPrefs.GetString(EditorLogPathPrefKey, "");
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            s_editorLogTailBytes = 0;
            return;
        }

        try
        {
            s_editorLogTailBytes = new FileInfo(logPath).Length;
        }
        catch
        {
            s_editorLogTailBytes = 0;
        }
    }

    private void PollEditorServerLogTail()
    {
        bool managed = IsManagedServerRunning();
        if (managed && s_stdioRedirectActive)
            return;
        if (!managed && !runServerExternally)
            return;

        var path = EditorPrefs.GetString(EditorLogPathPrefKey, "");
        if (string.IsNullOrEmpty(path))
            path = Path.Combine(ServerDir, ".unity-editor-server-console.log");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        if (EditorApplication.timeSinceStartup - s_lastEditorLogPollTime < 0.12)
            return;
        s_lastEditorLogPollTime = EditorApplication.timeSinceStartup;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < s_editorLogTailBytes)
            {
                s_editorLogTailBytes = 0;
                s_editorLogCarry = "";
            }

            var toRead = fs.Length - s_editorLogTailBytes;
            if (toRead <= 0) return;

            fs.Seek(s_editorLogTailBytes, SeekOrigin.Begin);
            var buf = new byte[toRead];
            var read = fs.Read(buf, 0, buf.Length);
            s_editorLogTailBytes = fs.Length;
            if (read <= 0) return;

            var chunk = Encoding.UTF8.GetString(buf, 0, read);
            var full = s_editorLogCarry + chunk;
            var lastNl = full.LastIndexOf('\n');
            if (lastNl < 0)
            {
                s_editorLogCarry = full;
                return;
            }

            var emit = full.Substring(0, lastNl + 1);
            s_editorLogCarry = full.Substring(lastNl + 1);
            foreach (var raw in emit.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                EditorApplication.delayCall += () => AppendConsoleToAllOpenWindows(line);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    void OnEnable()
    {
        minSize = new Vector2(400f, 280f);
        LoadPrefs();
        RestoreConsoleFromSession();
        EditorApplication.update += OnEditorUpdate;
        bool reattached = TryReattachToStoredServerProcess();
        if (reattached)
            InitLogTailAfterReattach();
        if (reattached && s_sharedServerProcess != null && !s_loggedReattachNoticeThisDomain)
        {
            s_loggedReattachNoticeThisDomain = true;
            int pid = s_sharedServerProcess.Id;
            AppendConsole(
                $"[Editor] Reattached to Socket.IO server after domain reload (PID {pid}). " +
                "Console history was restored; new Node output is tailed from Assets/UnityInputSyncerSocketIOServer/.unity-editor-server-console.log (rebuild server after pulling latest).");
        }

        Repaint();
    }

    void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        SavePrefs();
        // Do not stop the Socket.IO server here: OnDisable runs when entering Play Mode, docking
        // changes, or domain reload — killing Node would drop every connected client.
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
        PollEditorServerLogTail();
        PollStats();
        ProcessStatsResponse();
        ProcessActionResponse();
    }

    // -------------------------
    // IMGUI
    // -------------------------

    void OnGUI()
    {
        EditorGUILayout.Space(2);
        int prevTab = mainTab;
        mainTab = GUILayout.Toolbar(mainTab, MainTabNames);
        if (prevTab != mainTab)
            SavePrefs();

        EditorGUILayout.Space(4);

        // Reserve space for tab bar + padding so the body scroll area fits the window.
        const float chrome = 46f;
        float bodyHeight = Mathf.Max(100f, position.height - chrome);

        if (mainTab == 2)
        {
            DrawConsoleSection(bodyHeight);
            return;
        }

        mainTabScroll = EditorGUILayout.BeginScrollView(mainTabScroll, GUILayout.Height(bodyHeight));
        if (mainTab == 0)
        {
            DrawConfigSection();
            EditorGUILayout.Space(4);
            DrawControlSection();
        }
        else
        {
            if (ShouldPollAdminApi())
                DrawMonitoringSection();
            else
                DrawMonitorTabPlaceholder();
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawConfigSection()
    {
        showConfig = EditorGUILayout.Foldout(showConfig, "Server Configuration", true, EditorStyles.foldoutHeader);
        if (!showConfig) return;

        EditorGUI.indentLevel++;
        bool wasEnabled = GUI.enabled;
        GUI.enabled = !IsManagedServerRunning() && !isBuilding && !isInstalling;

        port = (ushort)EditorGUILayout.IntField(
            new GUIContent(
                "Port",
                "Public HTTP/Socket.IO port. Game clients always use this URL root for every match instance; matchId selects which instance. Not a different port per instance."),
            port);
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
        bool prevCluster = useMultiCoreCluster;
        int prevWorkerCount = clusterWorkerCount;
        useMultiCoreCluster = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "Multi-core cluster (single machine)",
                "Spawns worker processes (each with its own match pool on 127.0.0.1) and a primary on Port. Clients still connect only to Port; Port+1, … are internal. Requires npm run build; entry is dist/cluster-primary.js."),
            useMultiCoreCluster);
        if (useMultiCoreCluster)
        {
            EditorGUI.indentLevel++;
            clusterWorkerCount = EditorGUILayout.IntField(
                new GUIContent(
                    "Worker count",
                    "0 = automatic max(1, logical CPU count − 1). Otherwise sets INPUT_SYNCER_WORKER_COUNT."),
                clusterWorkerCount);
            clusterWorkerCount = Mathf.Max(0, clusterWorkerCount);
            EditorGUI.indentLevel--;
        }
        if (prevCluster != useMultiCoreCluster || prevWorkerCount != clusterWorkerCount)
            SavePrefs();

        EditorGUILayout.Space(2);
        GUILayout.Label("Admin", EditorStyles.miniBoldLabel);
        adminAuthToken = EditorGUILayout.TextField("Auth Token", adminAuthToken);
        EditorGUI.BeginChangeCheck();
        adminApiBaseUrl = EditorGUILayout.TextField(
            new GUIContent(
                "API base URL",
                "Leave empty to use http://localhost:<Port> (same machine). Set when the server runs elsewhere, e.g. http://dev.myhost:3000 or https://api.staging.example.com — no trailing slash."),
            adminApiBaseUrl);
        if (EditorGUI.EndChangeCheck())
        {
            latestStats = null;
            pollError = null;
            lastPollTime = 0;
        }
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField(
                "Resolved API root",
                GetAdminApiRoot());
        }

        GUI.enabled = wasEnabled;
        EditorGUI.indentLevel--;
    }

    private void DrawControlSection()
    {
        BeginSectionPanel();
        GUILayout.Label("Server Control", SectionTitleStyle);

        EditorGUILayout.Space(2);
        bool managedRunning = IsManagedServerRunning();
        EditorGUI.BeginDisabledGroup(managedRunning);
        bool prevExternal = runServerExternally;
        runServerExternally = EditorGUILayout.ToggleLeft(
            new GUIContent(
                "External / remote server (monitor only)",
                "Unity will not start or stop Node. Use when the process runs in your terminal or on a remote dev host. Set API base URL (and Auth Token) to match that server; Port still applies to Unity-started servers and to the default localhost API URL when API base URL is empty."),
            runServerExternally);
        if (prevExternal != runServerExternally)
        {
            SavePrefs();
            latestStats = null;
            pollError = null;
            lastPollTime = 0;
        }
        EditorGUI.EndDisabledGroup();
        if (managedRunning)
            EditorGUILayout.HelpBox("Stop the Unity-started server before switching to external mode.", MessageType.Info);

        if (runServerExternally)
        {
            EditorGUILayout.Space(4);
            DrawExternalTerminalTipsSection();
        }

        bool hasNodeModules = Directory.Exists(Path.Combine(ServerDir, "node_modules"));
        bool hasDist = Directory.Exists(Path.Combine(ServerDir, "dist"));

        if (!hasNodeModules)
            EditorGUILayout.HelpBox("node_modules/ not found. Click Install Dependencies (uses your login shell on macOS/Linux so npm from nvm/Homebrew/etc. is found).", MessageType.Warning);
        else if (!hasDist && !isBuilding && !isInstalling)
            EditorGUILayout.HelpBox("dist/ not found. Click Build to compile the server.", MessageType.Info);

        EditorGUILayout.BeginHorizontal();

        bool canRunTooling = !managedRunning && !isBuilding && !isInstalling;

        GUI.enabled = canRunTooling;
        if (GUILayout.Button("Install Dependencies", GUILayout.Width(150)))
            StartInstall();

        GUI.enabled = canRunTooling && hasNodeModules;
        if (GUILayout.Button("Build", GUILayout.Width(60)))
            StartBuild();

        if (!runServerExternally)
        {
            if (!managedRunning)
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
        }
        else
        {
            GUILayout.FlexibleSpace();
        }
        GUI.enabled = true;

        EditorGUILayout.EndHorizontal();
        if (runServerExternally)
            EditorGUILayout.HelpBox(
                "Use Install / Build here if needed, then run Node from your terminal using the snippet below. " +
                "Keep Server Configuration in sync: if Multi-core cluster is enabled there, use the snippet’s npm run start:cluster (not start:prod).",
                MessageType.None);

        EditorGUILayout.Space(6);
        DrawServerStatusRow();

        EndSectionPanel();
    }

    private void DrawExternalTerminalTipsSection()
    {
        showExternalTerminalTips = EditorGUILayout.Foldout(showExternalTerminalTips, "How to start the server in a terminal", true);
        if (!showExternalTerminalTips)
            return;

        EditorGUILayout.HelpBox(
            "Use the same Port and Auth Token as on the machine where Node runs (or set API base URL to your remote admin URL and token). " +
            "Game clients use the Socket.IO URL for that host (e.g. http://localhost:<port> locally, or ws/http to your dev server — see TicTacToeExample). " +
            (useMultiCoreCluster
                ? "Multi-core cluster is enabled in Server Configuration: the snippet runs npm run start:cluster. The primary listens on Port; worker processes listen on 127.0.0.1 at Port+1, Port+2, … on that same machine—clients and admin still use Port only. " +
                  "Override worker count with Worker count or INPUT_SYNCER_WORKER_COUNT in the snippet when non-zero. "
                : "") +
            "Optional: set INPUT_SYNCER_EDITOR_LOG on the server host to mirror logs into this window’s console (see snippet; only works if that path is readable from your Mac, e.g. SSHFS or local run).",
            MessageType.None);

        EditorGUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Copy zsh/bash commands", GUILayout.Width(180)))
        {
            EditorGUIUtility.systemCopyBuffer = BuildExternalServerShellSnippet();
            ShowNotification(new GUIContent("Copied shell snippet to clipboard."));
        }
        if (GUILayout.Button("Reveal server folder", GUILayout.Width(140)))
            EditorUtility.RevealInFinder(ServerDir);
        EditorGUILayout.EndHorizontal();

        string snippetPreview = BuildExternalServerShellSnippet();
        var previewStyle = new GUIStyle(EditorStyles.textArea)
        {
            wordWrap = true,
            fontSize = EditorStyles.miniLabel.fontSize
        };
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.TextArea(snippetPreview, previewStyle, GUILayout.MinHeight(120));
        EditorGUI.EndDisabledGroup();
    }

    /// <summary>Export lines for zsh/bash; matches env vars used when Unity starts the server.</summary>
    private string BuildExternalServerShellSnippet()
    {
        string logPath = Path.Combine(ServerDir, ".unity-editor-server-console.log");
        var sb = new StringBuilder();
        sb.AppendLine("# From project folder Assets/UnityInputSyncerSocketIOServer");
        sb.AppendLine($"cd \"{ServerDir}\"");
        sb.AppendLine();
        sb.AppendLine("# Required / common options (should match this window; remote host: set API base URL for monitoring):");
        sb.AppendLine($"export INPUT_SYNCER_PORT={port}");
        sb.AppendLine($"export INPUT_SYNCER_MAX_PLAYERS={maxPlayers}");
        sb.AppendLine($"export INPUT_SYNCER_STEP_INTERVAL={stepInterval.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"export INPUT_SYNCER_AUTO_START_WHEN_FULL={(autoStartWhenFull ? "true" : "false")}");
        sb.AppendLine($"export INPUT_SYNCER_ALLOW_LATE_JOIN={(allowLateJoin ? "true" : "false")}");
        sb.AppendLine($"export INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN={(sendHistoryOnLateJoin ? "true" : "false")}");
        sb.AppendLine($"export INPUT_SYNCER_MAX_INSTANCES={maxInstances}");
        sb.AppendLine($"export INPUT_SYNCER_AUTO_RECYCLE={(autoRecycle ? "true" : "false")}");
        sb.AppendLine($"export INPUT_SYNCER_IDLE_TIMEOUT={idleTimeout.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrEmpty(adminAuthToken))
            sb.AppendLine($"export INPUT_SYNCER_ADMIN_AUTH_TOKEN=\"{adminAuthToken.Replace("\"", "\\\"")}\"");
        else
            sb.AppendLine("# export INPUT_SYNCER_ADMIN_AUTH_TOKEN=\"your-token\"  # if you use admin auth");
        sb.AppendLine();
        sb.AppendLine("# Optional: append server logs to the Unity window console (this file):");
        sb.AppendLine($"export INPUT_SYNCER_EDITOR_LOG=\"{logPath}\"");
        sb.AppendLine();
        sb.AppendLine("# After: npm install && npm run build");
        if (useMultiCoreCluster)
        {
            sb.AppendLine("# Multi-core: primary listens on INPUT_SYNCER_PORT; workers on 127.0.0.1:(PORT+1)…");
            if (clusterWorkerCount > 0)
                sb.AppendLine($"export INPUT_SYNCER_WORKER_COUNT={clusterWorkerCount}");
            sb.AppendLine("npm run start:cluster");
        }
        else
        {
            sb.AppendLine("npm run start:prod");
        }
        sb.AppendLine();
        sb.AppendLine("# Dev (rebuild on change): npm run start:dev");
        if (useMultiCoreCluster)
            sb.AppendLine("# Note: start:dev is single-process only; for multi-core after edits use: npm run build && npm run start:cluster");
        sb.AppendLine();
        sb.AppendLine("# Windows (cmd): use set VAR=value then node dist\\main.js or dist\\cluster-primary.js");
        return sb.ToString();
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
        else if (IsManagedServerRunning())
        {
            statusText = "Running (Unity process)";
            dotColor = new Color(0.35f, 0.82f, 0.45f);
        }
        else if (runServerExternally)
        {
            bool reachable = latestStats != null && string.IsNullOrEmpty(pollError);
            statusText = reachable
                ? "Monitoring external server (API OK)"
                : "Monitoring external server (waiting for API…)";
            dotColor = reachable
                ? new Color(0.35f, 0.82f, 0.45f)
                : new Color(0.95f, 0.75f, 0.35f);
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

        if (IsManagedServerRunning() || runServerExternally)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(18);
            EditorGUILayout.LabelField("Address", GUILayout.Width(52));
            var addr = GetAdminApiRoot();
            if (GUILayout.Button(new GUIContent(addr, "Open admin root in browser"), EditorStyles.linkLabel))
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

        EditorGUILayout.Space(6);
        {
            string root = GetAdminApiRoot();
            string clusterNote = useMultiCoreCluster
                ? " Multi-core: extra processes listen on localhost-only ports (Port+1, …); game builds must not use those—only this URL."
                : "";
            EditorGUILayout.HelpBox(
                "Clients connect every match to the same Socket.IO server URL—there is no separate port per instance. " +
                $"Use this root as the client “Server URL” (e.g. TicTacToe): {root}. Pass the instance ID from the table as matchId when connecting." +
                clusterNote,
                MessageType.Info);
        }

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

    private void DrawMonitorTabPlaceholder()
    {
        BeginSectionPanel();
        GUILayout.Label("Monitoring & pool", SectionTitleStyle);
        EditorGUILayout.HelpBox(
            "Nothing to poll yet. On the Setup tab, start the server from Unity, or enable “External / remote server” and set API base URL / auth to match a running server.",
            MessageType.Info);
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
        GUILayout.Label("Copy", EditorStyles.miniBoldLabel, GUILayout.Width(44));
        GUILayout.Label("", GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();
    }

    private void DrawInstanceRow(InstanceInfo inst)
    {
        EditorGUILayout.BeginHorizontal();
        string shortId = inst.id.Length > 8 ? inst.id.Substring(0, 8) : inst.id;
        string displayId = inst.id.Length > 8 ? shortId + "…" : shortId;
        var idTooltip =
            $"Instance ID (matchId):\n{inst.id}\n\nUse the same Socket.IO server URL/port for all instances; pass this id as matchId (not a different port).";
        GUILayout.Label(new GUIContent(displayId, idTooltip), EditorStyles.miniLabel, GUILayout.Width(80));
        GUILayout.Label(inst.state, EditorStyles.miniLabel, GUILayout.Width(100));
        GUILayout.Label(inst.playerCount.ToString(), EditorStyles.miniLabel, GUILayout.Width(55));
        GUILayout.Label(inst.joinedPlayerCount.ToString(), EditorStyles.miniLabel, GUILayout.Width(50));
        GUILayout.Label(inst.currentStep.ToString(), EditorStyles.miniLabel, GUILayout.Width(45));
        GUILayout.Label($"{inst.uptimeSeconds:F0}s", EditorStyles.miniLabel, GUILayout.Width(60));
        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44)))
        {
            EditorGUIUtility.systemCopyBuffer = inst.id;
            ShowNotification(new GUIContent("Copied instance ID—paste as matchId; server URL/port is unchanged for every instance."));
        }
        if (GUILayout.Button("Delete", EditorStyles.miniButton, GUILayout.Width(50)))
            DeleteInstance(inst.id);
        EditorGUILayout.EndHorizontal();
    }

    /// <param name="viewportHeight">Total height for the console panel (header + scroll area).</param>
    private void DrawConsoleSection(float viewportHeight)
    {
        BeginSectionPanel();
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Console output", SectionTitleStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Clear", GUILayout.Width(50)))
        {
            lock (consoleLines)
            {
                consoleLines.Clear();
            }

            SessionState.SetString(SessionConsoleKey, "");
        }
        EditorGUILayout.EndHorizontal();

        float scrollH = Mathf.Max(80f, viewportHeight - 52f);
        consoleScroll = EditorGUILayout.BeginScrollView(consoleScroll, GUILayout.Height(scrollH));
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
        if (runServerExternally || IsManagedServerRunning() || isBuilding || isInstalling) return;

        SavePrefs();
        AppendConsole(useMultiCoreCluster
            ? $"[Editor] Starting multi-core cluster on public port {port}…"
            : $"[Editor] Starting server on port {port}...");

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

            string serverEntry = useMultiCoreCluster ? "dist/cluster-primary.js" : "dist/main.js";
            if (TryGetNodeExecutableForServer(out var nodeExe))
            {
                psi.FileName = nodeExe;
                psi.Arguments = serverEntry;
            }
            else
                ApplyNodeScriptCommand(psi, serverEntry);

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
            if (useMultiCoreCluster && clusterWorkerCount > 0)
                psi.EnvironmentVariables["INPUT_SYNCER_WORKER_COUNT"] = clusterWorkerCount.ToString();

            var editorLogPath = Path.Combine(ServerDir, ".unity-editor-server-console.log");
            try
            {
                File.WriteAllText(
                    editorLogPath,
                    $"[{DateTime.UtcNow:O}] --- editor log mirror (INPUT_SYNCER_EDITOR_LOG) ---\n",
                    Encoding.UTF8);
            }
            catch
            {
                /* non-fatal */
            }

            EditorPrefs.SetString(EditorLogPathPrefKey, editorLogPath);
            psi.EnvironmentVariables["INPUT_SYNCER_EDITOR_LOG"] = editorLogPath;
            s_stdioRedirectActive = true;
            s_editorLogTailBytes = 0;
            s_editorLogCarry = "";

            s_sharedServerProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            s_sharedServerProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = e.Data;
                EditorApplication.delayCall += () => AppendConsoleToAllOpenWindows(line);
            };
            s_sharedServerProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = "[stderr] " + e.Data;
                EditorApplication.delayCall += () => AppendConsoleToAllOpenWindows(line);
            };
            s_sharedServerProcess.Start();
            s_sharedServerProcess.BeginOutputReadLine();
            s_sharedServerProcess.BeginErrorReadLine();

            StoreServerChildPid(s_sharedServerProcess.Id);

            latestStats = null;
            pollError = null;
            lastPollTime = 0;
        }
        catch (Exception ex)
        {
            AppendConsole($"[Editor] Failed to start server: {ex.Message}");
            s_sharedServerProcess = null;
            ClearStoredServerChildPid();
            s_stdioRedirectActive = false;
        }

        Repaint();
    }

    private void StopServer()
    {
        TryReattachToStoredServerProcess();
        if (s_sharedServerProcess == null)
            return;

        StopSharedServerProcess(killMessageToConsole: true);
        latestStats = null;
        pollError = null;
        DisposeRequest(ref activeStatsRequest);
        DisposeRequest(ref activeActionRequest);
        Repaint();
    }

    private void CheckProcessExited()
    {
        TryReattachToStoredServerProcess();
        if (s_sharedServerProcess == null)
            return;

        try
        {
            s_sharedServerProcess.Refresh();
            if (s_sharedServerProcess.HasExited)
            {
                int code = s_sharedServerProcess.ExitCode;
                AppendConsole($"[Editor] Server process exited (code {code})");
                s_sharedServerProcess.Dispose();
                s_sharedServerProcess = null;
                ClearStoredServerChildPid();
                s_stdioRedirectActive = false;
                latestStats = null;
                Repaint();
            }
        }
        catch
        {
            s_sharedServerProcess = null;
            ClearStoredServerChildPid();
            s_stdioRedirectActive = false;
            latestStats = null;
        }
    }

    /// <summary>True when Unity spawned the Node process and it is still alive.</summary>
    private bool IsManagedServerRunning()
    {
        TryReattachToStoredServerProcess();
        if (s_sharedServerProcess == null)
            return false;
        try
        {
            s_sharedServerProcess.Refresh();
            if (s_sharedServerProcess.HasExited)
            {
                try
                {
                    s_sharedServerProcess.Dispose();
                }
                catch { }

                s_sharedServerProcess = null;
                ClearStoredServerChildPid();
                s_stdioRedirectActive = false;
                return false;
            }

            return true;
        }
        catch
        {
            try
            {
                s_sharedServerProcess?.Dispose();
            }
            catch { }

            s_sharedServerProcess = null;
            ClearStoredServerChildPid();
            s_stdioRedirectActive = false;
            return false;
        }
    }

    /// <summary>Whether admin HTTP polling (stats / instances) should run.</summary>
    private bool ShouldPollAdminApi()
    {
        return IsManagedServerRunning() || runServerExternally;
    }

    private static void AppendConsoleToAllOpenWindows(string line)
    {
        var windows = Resources.FindObjectsOfTypeAll<SocketIOServerWindow>();
        if (windows == null || windows.Length == 0)
        {
            UnityEngine.Debug.Log($"[Socket.IO Server] {line}");
            return;
        }

        foreach (var w in windows)
        {
            lock (w.consoleLines)
            {
                w.consoleLines.Add(line);
                while (w.consoleLines.Count > MaxConsoleLines)
                    w.consoleLines.RemoveAt(0);
            }

            w.consoleScroll = new Vector2(0, float.MaxValue);
            w.Repaint();
        }

        if (windows.Length > 0)
            PersistConsoleSnapshot(windows[0]);
    }

    // -------------------------
    // ADMIN API POLLING
    // -------------------------

    private void PollStats()
    {
        if (!ShouldPollAdminApi()) return;
        if (activeStatsRequest != null) return;

        if (EditorApplication.timeSinceStartup - lastPollTime < PollIntervalSeconds) return;
        lastPollTime = EditorApplication.timeSinceStartup;

        string url = $"{GetAdminApiRoot()}/api/stats";
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

        string url = $"{GetAdminApiRoot()}/api/instances";
        activeActionRequest = new UnityWebRequest(url, "POST");
        // Must send explicit fields: POST "{}" used to make Nest pass all-undefined overrides and
        // wipe INPUT_SYNCER_* defaults (autoStartWhenFull / maxPlayers), so matches never auto-started.
        string createBody = JsonUtility.ToJson(new CreateInstanceApiBody
        {
            maxPlayers = maxPlayers,
            stepIntervalSeconds = stepInterval,
            autoStartWhenFull = autoStartWhenFull,
            allowLateJoin = allowLateJoin,
            sendStepHistoryOnLateJoin = sendHistoryOnLateJoin
        });
        activeActionRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(createBody));
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

        string url = $"{GetAdminApiRoot()}/api/instances/{id}";
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
        PersistConsoleSnapshot(this);
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
        EditorPrefs.SetString("SocketIOServer_AdminApiBaseUrl", adminApiBaseUrl ?? "");
        EditorPrefs.SetInt("SocketIOServer_MaxInstances", maxInstances);
        EditorPrefs.SetBool("SocketIOServer_AutoRecycle", autoRecycle);
        EditorPrefs.SetFloat("SocketIOServer_IdleTimeout", idleTimeout);
        EditorPrefs.SetBool("SocketIOServer_RunExternally", runServerExternally);
        EditorPrefs.SetBool("SocketIOServer_MultiCoreCluster", useMultiCoreCluster);
        EditorPrefs.SetInt("SocketIOServer_ClusterWorkerCount", clusterWorkerCount);
        EditorPrefs.SetInt("SocketIOServer_MainTab", mainTab);
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
        adminApiBaseUrl = EditorPrefs.GetString("SocketIOServer_AdminApiBaseUrl", "");
        maxInstances = EditorPrefs.GetInt("SocketIOServer_MaxInstances", 10);
        autoRecycle = EditorPrefs.GetBool("SocketIOServer_AutoRecycle", true);
        idleTimeout = EditorPrefs.GetFloat("SocketIOServer_IdleTimeout", 0f);
        runServerExternally = EditorPrefs.GetBool("SocketIOServer_RunExternally", false);
        useMultiCoreCluster = EditorPrefs.GetBool("SocketIOServer_MultiCoreCluster", false);
        clusterWorkerCount = Mathf.Max(0, EditorPrefs.GetInt("SocketIOServer_ClusterWorkerCount", 0));
        mainTab = Mathf.Clamp(EditorPrefs.GetInt("SocketIOServer_MainTab", 0), 0, MainTabNames.Length - 1);
    }

    // -------------------------
    // HELPERS
    // -------------------------

    /// <summary>Root URL for admin HTTP (stats, instance CRUD). No trailing slash.</summary>
    private string GetAdminApiRoot()
    {
        var s = adminApiBaseUrl?.Trim() ?? "";
        if (string.IsNullOrEmpty(s))
            return $"http://localhost:{port}";

        s = s.TrimEnd('/');
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "http://" + s;

        return s;
    }

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
    private class CreateInstanceApiBody
    {
        public int maxPlayers;
        public float stepIntervalSeconds;
        public bool autoStartWhenFull;
        public bool allowLateJoin;
        public bool sendStepHistoryOnLateJoin;
    }

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
