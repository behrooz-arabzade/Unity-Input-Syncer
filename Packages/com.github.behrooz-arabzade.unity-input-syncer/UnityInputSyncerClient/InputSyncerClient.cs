using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using UnityEngine;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using UnityInputSyncerCore;
using UnityInputSyncerCore.Utils;
using Debug = UnityEngine.Debug;

namespace UnityInputSyncerClient
{
    public class InputSyncerClient : IDisposable
    {
        private InputSyncerClientOptions Options;
        public IClientDriver Driver;
        private InputSyncerState InputSyncerState = new InputSyncerState();
        public InputSyncerClient(IClientDriver driver, InputSyncerClientOptions options = null)
        {
            Options = options ?? new InputSyncerClientOptions();

            if (!Options.Mock)
            {
                if (driver == null)
                    throw new ArgumentNullException(nameof(driver), "Driver cannot be null when Mock mode is disabled.");
                Driver = driver;
                Driver.OnConnected += () => OnConnected?.Invoke();
                Driver.OnReconnected += () => OnReconnected?.Invoke();
                Driver.OnError += (msg) => OnError?.Invoke(msg);
                Driver.OnDisconnected += (reason) => OnDisconnected?.Invoke(reason);
            }

            InputSyncerState.OnStepMissed += OnStepMissed;

            RegisterOnSyncerEvents();
        }

        public async Task<bool> ConnectAsync()
        {
            if (Options.Mock)
            {
                RunMockInterval();
                return true;
            }

            if (Driver == null)
            {
                Debug.LogError("ConnectionDriver is not set. Cannot connect.");
                return false;
            }

            return await Driver.ConnectAsync();
        }

        public async Task DisconnectAsync()
        {
            if (CancellationTokenSource != null)
            {
                CancellationTokenSource.Cancel();
                CancellationTokenSource.Dispose();
                CancellationTokenSource = null;
            }

            if (Driver != null && Driver.IsConnected)
            {
                await Driver.DisconnectAsync();
            }
        }

        private CancellationTokenSource CancellationTokenSource;
        private ConcurrentQueue<BaseInputData> ReadyInputToSend = new ConcurrentQueue<BaseInputData>();
        private void RunMockInterval()
        {
            _ = UnityThreadDispatcher.Instance;
            CancellationTokenSource = new CancellationTokenSource();
            var token = CancellationTokenSource.Token;

            int stepIntervalMs = Options.StepIntervalMs;

            int remainingInterval = 0;

            Task.Run(async () =>
            {
                int step = 0;
                var stopwatch = new Stopwatch();
                while (!token.IsCancellationRequested)
                {
                    stopwatch.Restart();

                    await Task.Delay(remainingInterval);

                    await UnityThreadDispatcher.RunOnMainThreadAsync(() =>
                    {
                        List<object> inputList = new List<object>();
                        int index = 0;
                        while (ReadyInputToSend.TryDequeue(out var input))
                        {
                            input.index = index++;
                            input.userId = input.forceUserId ?? Options.MockCurrentUserId;
                            inputList.Add(input);
                        }

                        var stepInputs = new StepInputs
                        {
                            step = step,
                            inputs = inputList
                        };
                        InputSyncerState.AddStepInputs(new List<StepInputs> { stepInputs });

                        HandleMatchStarted();
                    });

                    step++;

                    int elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                    remainingInterval = stepIntervalMs - elapsedMs;
                    if (remainingInterval < 0) remainingInterval = 0;
                }
            }, token);
        }

        private void OnStepMissed()
        {
            Driver?.Emit(InputSyncerEvents.MATCH_USER_REQUEST_ALL_STEPS_EVENT);
        }

        private void OnStepsReceived(List<StepInputs> stepsData)
        {
            InputSyncerState.AddStepInputs(stepsData);
            HandleMatchStarted();
        }

        private void OnAllStepReceived(AllStepInputs stepsData)
        {
            InputSyncerState.AddAllStepInputs(stepsData.steps, Convert.ToInt32(stepsData.lastSentStep));
            HandleMatchStarted();
        }

        public float LatencyMs => Driver?.LatencyMs ?? -1f;

        public Action OnConnected { get; set; }
        public Action OnReconnected { get; set; }
        public Action<string> OnError { get; set; }
        public Action<string> OnDisconnected { get; set; }

        /// <summary>Server-driven match end; argument is <c>on-finish</c> JSON <c>reason</c>.</summary>
        public Action<string> OnMatchFinishedWithReason { get; set; }

        /// <summary>Per-player session finish from server (<c>userId</c>, <c>data</c> payload).</summary>
        public Action<string, JToken> OnPlayerSessionFinish { get; set; }

        /// <summary>Admin-defined match context (<c>matchId</c>, <c>matchData</c>, all <c>users</c>) from <c>on-match-context</c>.</summary>
        public Action<InputSyncerMatchContext> OnMatchContext { get; set; }

        /// <summary>Last received match context; set before <see cref="OnMatchContext"/> is invoked.</summary>
        public InputSyncerMatchContext LastMatchContext { get; private set; }

        private bool OnMatchStartedInvoked = false;
        public Action OnMatchStarted { get; set; }
        public void HandleMatchStarted()
        {
            if (!OnMatchStartedInvoked)
            {
                OnMatchStarted?.Invoke();
                OnMatchStartedInvoked = true;
            }
        }

        private void RegisterOnSyncerEvents()
        {
            if (Options.Mock) return;

            Driver.On(InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT, (response) =>
            {
                var eventData = Driver.GetData<List<StepInputs>>(response);
                OnStepsReceived(eventData);
            });

            Driver.On(InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT, (response) =>
            {
                var eventData = Driver.GetData<AllStepInputs>(response);
                OnAllStepReceived(eventData);
            });

            Driver.On(InputSyncerEvents.INPUT_SYNCER_START_EVENT, (response) =>
            {
                HandleMatchStarted();
            });

            Driver.On(InputSyncerEvents.INPUT_SYNCER_MATCH_CONTEXT_EVENT, (response) =>
            {
                try
                {
                    var jo = Driver.GetData<JObject>(response);
                    if (jo == null)
                        return;
                    var ctx = new InputSyncerMatchContext
                    {
                        MatchId = jo["matchId"]?.ToString(),
                        MatchData = jo["matchData"],
                        Users = jo["users"] as JObject ?? new JObject(),
                    };
                    LastMatchContext = ctx;
                    OnMatchContext?.Invoke(ctx);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"OnMatchContext parse failed: {ex.Message}");
                }
            });

            Driver.On(InputSyncerEvents.INPUT_SYNCER_FINISH_EVENT, (response) =>
            {
                string reason = InputSyncerFinishReasons.Completed;
                try
                {
                    var jo = Driver.GetData<JObject>(response);
                    if (jo != null && jo["reason"] != null)
                        reason = jo["reason"].ToString();
                }
                catch
                {
                    /* keep default */
                }
                OnMatchFinishedWithReason?.Invoke(reason);
            });

            Driver.On(InputSyncerEvents.INPUT_SYNCER_PLAYER_SESSION_FINISH_EVENT, (response) =>
            {
                try
                {
                    var jo = Driver.GetData<JObject>(response);
                    if (jo == null) return;
                    string uid = jo["userId"]?.ToString() ?? "";
                    JToken data = jo["data"] ?? new JObject();
                    OnPlayerSessionFinish?.Invoke(uid, data);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"OnPlayerSessionFinish parse failed: {ex.Message}");
                }
            });
        }

        public void RegisterOnCustomEvent(string eventName, Action<ConnectionResponse> callback)
        {
            if (Options.Mock)
            {
                Debug.LogWarning("Mock mode is enabled. Custom events are not supported.");
                return;
            }

            Driver.On(eventName, callback);
        }

        public bool SendInput(BaseInputData inputData)
        {
            if (Options.Mock)
            {
                ReadyInputToSend.Enqueue(inputData);
                return true;
            }

            if (Driver == null || !Driver.IsConnected)
            {
                Debug.LogWarning("Socket is not connected. Cannot send input.");
                return false;
            }

            return Driver.Emit(InputSyncerEvents.MATCH_USER_INPUT_EVENT, new
            {
                inputData
            });

        }

        public void JoinMatch(string userId = null)
        {
            if (Options.Mock)
            {
                var joinData = new JoinInput.JoinInputData()
                {
                    userId = userId ?? Options.MockCurrentUserId,
                };

                ReadyInputToSend.Enqueue(new JoinInput(joinData)
                {
                    userId = userId ?? Options.MockCurrentUserId,
                });

                return;
            }

            Driver.Emit(InputSyncerEvents.MATCH_USER_JOIN_EVENT, new { userId });
        }

        /// <summary>Legacy quorum finish; emits <c>user-finish</c>.</summary>
        public bool SendUserFinish()
        {
            if (Options.Mock)
            {
                Debug.LogWarning("Mock mode: SendUserFinish is a no-op.");
                return true;
            }
            if (Driver == null || !Driver.IsConnected)
            {
                Debug.LogWarning("Socket is not connected. Cannot send user-finish.");
                return false;
            }
            return Driver.Emit(InputSyncerEvents.MATCH_USER_FINISH_EVENT);
        }

        /// <summary>Independent per-player session finish with optional payload (echoed by server).</summary>
        public bool SendPlayerSessionFinish(object data = null)
        {
            if (Options.Mock)
            {
                Debug.LogWarning("Mock mode: SendPlayerSessionFinish is a no-op.");
                return true;
            }
            if (Driver == null || !Driver.IsConnected)
            {
                Debug.LogWarning("Socket is not connected. Cannot send player-session-finish.");
                return false;
            }
            JToken dataToken = data == null
                ? new JObject()
                : data is JToken t ? t : JToken.FromObject(data);
            return Driver.Emit(InputSyncerEvents.MATCH_PLAYER_SESSION_FINISH_EVENT, new { data = dataToken });
        }

        public InputSyncerState GetState()
        {
            return InputSyncerState;
        }

        public void Dispose()
        {
            if (CancellationTokenSource != null)
            {
                CancellationTokenSource.Cancel();
                CancellationTokenSource.Dispose();
                CancellationTokenSource = null;
            }

            if (Driver != null && Driver.IsConnected)
            {
                try { Task.Run(() => Driver.DisconnectAsync()).Wait(TimeSpan.FromSeconds(2)); }
                catch { /* Swallow during disposal */ }
            }
        }
    }

    public class StepInputs
    {
        public int step;
        public List<object> inputs;
    }

    public class AllStepInputs
    {
        public string requestedUser;
        public List<StepInputs> steps;
        public long lastSentStep;
    }

    public abstract class BaseInputData
    {
        public abstract string type { get; set; }

        public BaseInputData(object data)
        {
            this.data = data;
        }
        public BaseInputData(object data, int expectedCastTimeMs)
        {
            this.data = data;
            this.expectedCastTimeMs = expectedCastTimeMs;
        }
        public BaseInputData(object data, int expectedCastTimeMs, bool forceCast)
        {
            this.data = data;
            this.expectedCastTimeMs = expectedCastTimeMs;
            this.forceCast = forceCast;
        }

        public string userId;
        public long requestStep;
        public long expectedCastTimeMs;
        public long remainingCastTimeMs;
        public bool forceCast;
        public bool castCanceled;
        public object data;
        public string payload;
        public long index = 0;

        public T GetData<T>()
        {
            return JObject.FromObject(data).ToObject<T>();
        }

        public string forceUserId;

        public bool IsTypeOf(string type)
        {
            return this.type == type;
        }
    }

    public class JoinInput : BaseInputData
    {
        public static string Type => "user-join";
        public override string type { get => Type; set { } }
        public JoinInput(JoinInputData data) : base(data) { }

        public JoinInputData GetData()
        {
            return JObject.FromObject(data).ToObject<JoinInputData>();
        }

        public class JoinInputData
        {
            public string userId;
        }
    }

    public class InputSyncerClientOptions
    {
        public bool Mock = false;
        public string MockCurrentUserId = "mockUserId";
        public int StepIntervalMs = 100;
    }
}