using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityInputSyncerClient.Drivers;

namespace UnityInputSyncerClient.Examples.TicTacToe
{
    public class TicTacToeExample : MonoBehaviour
    {
        [SerializeField] private string serverIp = "127.0.0.1";
        [SerializeField] private ushort serverPort = 7777;
        [SerializeField] private string userId = "player-1";

        private enum GameState
        {
            Menu,
            Connecting,
            WaitingForMatch,
            WaitingForReady,
            Playing,
            GameOver
        }

        private GameState state = GameState.Menu;
        private InputSyncerClient client;
        private InputSyncerState syncerState;
        private TicTacToeBoard board;
        private int nextStepToProcess;

        // Player assignment: first ready = X, second = O
        private List<string> readyPlayers = new List<string>();
        private string xPlayerId;
        private string oPlayerId;
        private CellState mySymbol;

        private string statusMessage = "";

        void FixedUpdate()
        {
            if (syncerState == null)
                return;

            while (syncerState.HasStep(nextStepToProcess))
            {
                var inputs = syncerState.GetInputsForStep(nextStepToProcess);
                foreach (var rawInput in inputs)
                {
                    JObject input;
                    if (rawInput is JObject jObj)
                        input = jObj;
                    else
                        input = JObject.FromObject(rawInput);

                    string inputType = input.Value<string>("type");
                    string inputUserId = input.Value<string>("userId");

                    if (inputType == TicTacToeReadyInput.Type)
                    {
                        if (!readyPlayers.Contains(inputUserId))
                            readyPlayers.Add(inputUserId);

                        if (readyPlayers.Count >= 2)
                        {
                            xPlayerId = readyPlayers[0];
                            oPlayerId = readyPlayers[1];
                            mySymbol = userId == xPlayerId ? CellState.X : CellState.O;
                            board = new TicTacToeBoard();
                            state = GameState.Playing;
                        }
                    }
                    else if (inputType == TicTacToeMoveInput.Type)
                    {
                        if (board == null)
                            continue;

                        CellState player = inputUserId == xPlayerId ? CellState.X : CellState.O;
                        var data = input["data"] as JObject;
                        if (data == null)
                            continue;

                        int row = data.Value<int>("row");
                        int col = data.Value<int>("col");
                        board.TryPlaceMove(row, col, player);

                        if (board.Result != GameResult.InProgress)
                            state = GameState.GameOver;
                    }
                }
                nextStepToProcess++;
            }
        }

        void OnGUI()
        {
            switch (state)
            {
                case GameState.Menu:
                    DrawMenu();
                    break;
                case GameState.Connecting:
                    GUI.Label(new Rect(20, 20, 400, 30), $"Connecting to {serverIp}:{serverPort}...");
                    break;
                case GameState.WaitingForMatch:
                    GUI.Label(new Rect(20, 20, 400, 30), "Connected! Waiting for opponent...");
                    break;
                case GameState.WaitingForReady:
                    GUI.Label(new Rect(20, 20, 400, 30), "Match started! Preparing game...");
                    break;
                case GameState.Playing:
                    DrawPlaying();
                    break;
                case GameState.GameOver:
                    DrawGameOver();
                    break;
            }

            if (!string.IsNullOrEmpty(statusMessage))
                GUI.Label(new Rect(20, Screen.height - 40, 500, 30), statusMessage);
        }

        private void DrawMenu()
        {
            float x = 20, y = 20, w = 200, h = 25, spacing = 30;

            GUI.Label(new Rect(x, y, w, h), "Server IP:");
            y += spacing;
            serverIp = GUI.TextField(new Rect(x, y, w, h), serverIp);
            y += spacing;

            GUI.Label(new Rect(x, y, w, h), "Server Port:");
            y += spacing;
            string portStr = GUI.TextField(new Rect(x, y, w, h), serverPort.ToString());
            if (ushort.TryParse(portStr, out var parsedPort))
                serverPort = parsedPort;
            y += spacing;

            GUI.Label(new Rect(x, y, w, h), "User ID:");
            y += spacing;
            userId = GUI.TextField(new Rect(x, y, w, h), userId);
            y += spacing + 10;

            if (GUI.Button(new Rect(x, y, w, h), "Connect"))
                Connect();
        }

        private void DrawPlaying()
        {
            float gridX = 20, gridY = 60, cellSize = 80;

            bool isMyTurn = board.CurrentTurn == mySymbol;
            string turnText = isMyTurn
                ? $"Your turn ({mySymbol})"
                : $"Opponent's turn ({board.CurrentTurn})";
            GUI.Label(new Rect(20, 20, 300, 30), turnText);

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    var rect = new Rect(gridX + c * cellSize, gridY + r * cellSize, cellSize - 4, cellSize - 4);
                    string label = board.Cells[r, c] == CellState.Empty ? ""
                        : board.Cells[r, c].ToString();

                    if (board.Cells[r, c] == CellState.Empty && isMyTurn)
                    {
                        if (GUI.Button(rect, label))
                        {
                            client.SendInput(new TicTacToeMoveInput(
                                new TicTacToeMoveData { row = r, col = c }));
                        }
                    }
                    else
                    {
                        GUI.Box(rect, label);
                    }
                }
            }

            GUI.Label(new Rect(20, gridY + 3 * cellSize + 10, 300, 20),
                $"You are: {mySymbol} | X: {xPlayerId} | O: {oPlayerId}");
        }

        private void DrawGameOver()
        {
            string resultText;
            if (board.Result == GameResult.Draw)
            {
                resultText = "Draw!";
            }
            else
            {
                bool iWon = (board.Result == GameResult.XWins && mySymbol == CellState.X) ||
                            (board.Result == GameResult.OWins && mySymbol == CellState.O);
                resultText = iWon ? "You Win!" : "You Lose!";
            }

            float gridX = 20, gridY = 60, cellSize = 80;

            GUI.Label(new Rect(20, 20, 300, 30), resultText);

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    var rect = new Rect(gridX + c * cellSize, gridY + r * cellSize, cellSize - 4, cellSize - 4);
                    string label = board.Cells[r, c] == CellState.Empty ? ""
                        : board.Cells[r, c].ToString();
                    GUI.Box(rect, label);
                }
            }

            if (GUI.Button(new Rect(20, gridY + 3 * cellSize + 20, 200, 30), "Back to Menu"))
                ResetToMenu();
        }

        private async void Connect()
        {
            state = GameState.Connecting;
            statusMessage = "";

            var driverOptions = new UTPDriverOptions
            {
                Ip = serverIp,
                Port = serverPort,
                Payload = new Dictionary<string, string>
                {
                    { "matchId", "ttt-match" },
                    { "userId", userId },
                },
            };

            var driver = new UTPClientDriver(driverOptions);
            client = new InputSyncerClient(driver);
            syncerState = client.GetState();

            client.OnMatchStarted = () =>
            {
                client.SendInput(new TicTacToeReadyInput());
                state = GameState.WaitingForReady;
            };

            client.OnError = msg => statusMessage = $"Error: {msg}";
            client.OnDisconnected = reason =>
            {
                statusMessage = $"Disconnected: {reason}";
                if (state != GameState.GameOver)
                    ResetToMenu();
            };

            bool connected = await client.ConnectAsync();
            if (connected)
            {
                client.JoinMatch(userId);
                state = GameState.WaitingForMatch;
            }
            else
            {
                statusMessage = "Failed to connect.";
                state = GameState.Menu;
            }
        }

        private void ResetToMenu()
        {
            client?.Dispose();
            client = null;
            syncerState = null;
            board = null;
            readyPlayers.Clear();
            xPlayerId = null;
            oPlayerId = null;
            nextStepToProcess = 0;
            statusMessage = "";
            state = GameState.Menu;
        }

        void OnDestroy()
        {
            client?.Dispose();
        }
    }
}
