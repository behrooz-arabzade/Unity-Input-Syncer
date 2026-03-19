using UnityEditor;
using UnityEngine;
using UnityInputSyncerUTPServer;

public class TicTacToeServerWindow : EditorWindow
{
    private ushort port = 7777;
    private int maxPlayers = 2;
    private float stepInterval = 0.1f;
    private bool autoStartWhenFull = true;

    private InputSyncerServer server;
    private int connectedCount;
    private int joinedCount;
    private string matchState = "Waiting";

    [MenuItem("Window/Input Syncer/TicTacToe Server")]
    public static void ShowWindow()
    {
        GetWindow<TicTacToeServerWindow>("TicTacToe Server");
    }

    void OnEnable()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        StopServer();
    }

    private void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode)
            StopServer();
    }

    void OnGUI()
    {
        GUILayout.Label("Server Configuration", EditorStyles.boldLabel);

        port = (ushort)EditorGUILayout.IntField("Port", port);
        maxPlayers = EditorGUILayout.IntField("Max Players", maxPlayers);
        stepInterval = EditorGUILayout.FloatField("Step Interval (s)", stepInterval);
        autoStartWhenFull = EditorGUILayout.Toggle("Auto Start When Full", autoStartWhenFull);

        EditorGUILayout.Space();

        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to start the server.", MessageType.Warning);
            GUI.enabled = false;
        }

        if (server == null)
        {
            if (GUILayout.Button("Start Server"))
                StartServer();
        }
        else
        {
            if (GUILayout.Button("Stop Server"))
                StopServer();
        }

        GUI.enabled = true;

        EditorGUILayout.Space();
        GUILayout.Label("Status", EditorStyles.boldLabel);

        EditorGUILayout.LabelField("Server Running", server != null ? "Yes" : "No");

        if (server != null)
        {
            EditorGUILayout.LabelField("Connected Players", connectedCount.ToString());
            EditorGUILayout.LabelField("Joined Players", joinedCount.ToString());
            EditorGUILayout.LabelField("Match State", matchState);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Address", $"127.0.0.1:{port}");
        }
    }

    private void StartServer()
    {
        var options = new InputSyncerServerOptions
        {
            Port = port,
            MaxPlayers = maxPlayers,
            StepIntervalSeconds = stepInterval,
            AutoStartWhenFull = autoStartWhenFull,
        };

        server = new InputSyncerServer(options);

        server.OnPlayerConnected += _ =>
        {
            connectedCount = server.GetPlayerCount();
            Repaint();
        };
        server.OnPlayerDisconnected += _ =>
        {
            connectedCount = server.GetPlayerCount();
            joinedCount = server.GetJoinedPlayerCount();
            Repaint();
        };
        server.OnPlayerJoined += _ =>
        {
            joinedCount = server.GetJoinedPlayerCount();
            Repaint();
        };
        server.OnMatchStarted += () =>
        {
            matchState = "In Progress";
            Repaint();
        };
        server.OnMatchFinished += () =>
        {
            matchState = "Finished";
            Repaint();
        };

        server.Start();
        matchState = "Waiting";
        connectedCount = 0;
        joinedCount = 0;
        Repaint();
    }

    private void StopServer()
    {
        if (server != null)
        {
            server.Dispose();
            server = null;
            matchState = "Waiting";
            connectedCount = 0;
            joinedCount = 0;
            Repaint();
        }
    }
}
