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

            if (!options.Mock)
            {
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
        private Queue<BaseInputData> ReadyInputToSend = new Queue<BaseInputData>();
        private void RunMockInterval()
        {
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
                try { Driver.DisconnectAsync().GetAwaiter().GetResult(); }
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