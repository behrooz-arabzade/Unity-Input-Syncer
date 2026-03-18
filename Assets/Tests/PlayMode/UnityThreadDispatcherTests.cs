using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityInputSyncerCore.Utils;

namespace Tests.PlayMode
{
    public class UnityThreadDispatcherTests
    {
        [Test]
        public void Instance_CreatesGameObject()
        {
            var instance = UnityThreadDispatcher.Instance;
            Assert.IsNotNull(instance);
            Assert.IsNotNull(instance.gameObject);
        }

        [Test]
        public void Enqueue_NullAction_DoesNotThrow()
        {
            var instance = UnityThreadDispatcher.Instance;
            Assert.DoesNotThrow(() => instance.Enqueue(null));
        }

        [UnityTest]
        public IEnumerator RunOnMainThread_ExecutesOnNextUpdate()
        {
            bool executed = false;

            UnityThreadDispatcher.RunOnMainThread(() => executed = true);

            // Should not execute immediately
            // Wait one frame for Update() to tick
            yield return null;

            Assert.IsTrue(executed, "Action should have executed on main thread Update");
        }

        [UnityTest]
        public IEnumerator RunOnMainThreadAsync_CompletesTask()
        {
            bool executed = false;

            var task = UnityThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                executed = true;
            });

            yield return null; // Wait for Update

            Assert.IsTrue(task.IsCompleted, "Task should be completed");
            Assert.IsTrue(executed, "Action should have executed");
        }

        [UnityTest]
        public IEnumerator RunOnMainThreadAsync_PropagatesException()
        {
            var task = UnityThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                throw new InvalidOperationException("test error");
            });

            yield return null; // Wait for Update

            Assert.IsTrue(task.IsFaulted, "Task should be faulted");
            Assert.IsNotNull(task.Exception);
            Assert.IsInstanceOf<InvalidOperationException>(task.Exception.InnerException);
        }

        [UnityTest]
        public IEnumerator RunOnMainThreadFixedUpdate_Executes()
        {
            bool executed = false;

            UnityThreadDispatcher.RunOnMainThreadFixedUpdate(() => executed = true);

            // Wait for a few FixedUpdate ticks
            float elapsed = 0f;
            while (!executed && elapsed < 2f)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
            }

            Assert.IsTrue(executed, "Action should have executed in FixedUpdate");
        }

        [UnityTest]
        public IEnumerator RunOnMainThreadAsync_Generic_ReturnsValue()
        {
            var task = UnityThreadDispatcher.RunOnMainThreadAsync<int>(() => 42);

            yield return null; // Wait for Update

            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual(42, task.Result);
        }

        [UnityTest]
        public IEnumerator RunOnMainThread_FromBackgroundThread_ExecutesOnMainThread()
        {
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            int executionThreadId = -1;

            var tcs = new TaskCompletionSource<bool>();

            Task.Run(() =>
            {
                UnityThreadDispatcher.RunOnMainThread(() =>
                {
                    executionThreadId = Thread.CurrentThread.ManagedThreadId;
                    tcs.SetResult(true);
                });
            });

            float elapsed = 0f;
            while (!tcs.Task.IsCompleted && elapsed < 3f)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            Assert.AreEqual(mainThreadId, executionThreadId,
                "Action dispatched from background thread should execute on main thread");
        }

        [UnityTest]
        public IEnumerator RunOnMainThreadFixedUpdateAsync_CompletesTask()
        {
            var task = UnityThreadDispatcher.RunOnMainThreadFixedUpdateAsync(() => { });

            float elapsed = 0f;
            while (!task.IsCompleted && elapsed < 2f)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
            }

            Assert.IsTrue(task.IsCompleted, "FixedUpdate async task should complete");
        }

        [UnityTest]
        public IEnumerator RunOnMainThreadFixedUpdateAsync_Generic_ReturnsValue()
        {
            var task = UnityThreadDispatcher.RunOnMainThreadFixedUpdateAsync<string>(() => "hello");

            float elapsed = 0f;
            while (!task.IsCompleted && elapsed < 2f)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
            }

            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual("hello", task.Result);
        }
    }
}
