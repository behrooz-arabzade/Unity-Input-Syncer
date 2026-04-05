using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityInputSyncerCore.Utils
{
    public class UnityThreadDispatcher : MonoBehaviour
    {
        private static UnityThreadDispatcher _instance;
        private static readonly Queue<Action> _executionQueue = new Queue<Action>();
        private static readonly Queue<Action> _fixedUpdateQueue = new Queue<Action>();

        public static UnityThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("UnityThreadDispatcher");
                    _instance = go.AddComponent<UnityThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
            }
        }

        private void Update()
        {
            lock (_executionQueue)
            {
                while (_executionQueue.Count > 0)
                {
                    _executionQueue.Dequeue()?.Invoke();
                }
            }
        }

        private void FixedUpdate()
        {
            lock (_fixedUpdateQueue)
            {
                while (_fixedUpdateQueue.Count > 0)
                {
                    _fixedUpdateQueue.Dequeue()?.Invoke();
                }
            }
        }

        public void Enqueue(Action action)
        {
            if (action == null)
                return;

            lock (_executionQueue)
            {
                _executionQueue.Enqueue(action);
            }
        }

        public static void RunOnMainThread(Action action)
        {
            Instance.Enqueue(action);
        }

        public static void RunOnMainThreadUpdate(Action action)
        {
            Instance.Enqueue(action);
        }

        public static void RunOnMainThreadFixedUpdate(Action action)
        {
            if (action == null)
                return;

            lock (_fixedUpdateQueue)
            {
                _fixedUpdateQueue.Enqueue(action);
            }
        }

        public static Task RunOnMainThreadAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();

            Instance.Enqueue(() =>
            {
                try
                {
                    action?.Invoke();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        public static Task<T> RunOnMainThreadAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();

            Instance.Enqueue(() =>
            {
                try
                {
                    var result = func != null ? func() : default(T);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        public static Task RunOnMainThreadUpdateAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();

            Instance.Enqueue(() =>
            {
                try
                {
                    action?.Invoke();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        public static Task<T> RunOnMainThreadUpdateAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();

            Instance.Enqueue(() =>
            {
                try
                {
                    var result = func != null ? func() : default(T);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        public static Task RunOnMainThreadFixedUpdateAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();

            lock (_fixedUpdateQueue)
            {
                _fixedUpdateQueue.Enqueue(() =>
                {
                    try
                    {
                        action?.Invoke();
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }

            return tcs.Task;
        }

        public static Task<T> RunOnMainThreadFixedUpdateAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();

            lock (_fixedUpdateQueue)
            {
                _fixedUpdateQueue.Enqueue(() =>
                {
                    try
                    {
                        var result = func != null ? func() : default(T);
                        tcs.SetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }

            return tcs.Task;
        }
    }
}