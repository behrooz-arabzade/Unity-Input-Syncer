using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityInputSyncerCore.Utils;

namespace Tests.PlayMode
{
    public class PlayerLoopHookTests
    {
        [UnityTest]
        public IEnumerator Register_CallbackIsCalled()
        {
            int callCount = 0;
            void Tick() => callCount++;

            PlayerLoopHook.Register(Tick);

            yield return null; // Wait one frame

            PlayerLoopHook.Unregister(Tick);

            Assert.Greater(callCount, 0, "Registered callback should be called at least once");
        }

        [UnityTest]
        public IEnumerator Unregister_StopsCallback()
        {
            int callCount = 0;
            void Tick() => callCount++;

            PlayerLoopHook.Register(Tick);

            yield return null; // Let it tick at least once

            PlayerLoopHook.Unregister(Tick);
            int countAfterUnregister = callCount;

            yield return null;
            yield return null;

            // Should not have increased significantly after unregister
            Assert.LessOrEqual(callCount, countAfterUnregister + 1,
                "Callback should stop being called after unregister");
        }

        [UnityTest]
        public IEnumerator MultipleCallbacks_AllAreCalled()
        {
            int count1 = 0, count2 = 0;
            void Tick1() => count1++;
            void Tick2() => count2++;

            PlayerLoopHook.Register(Tick1);
            PlayerLoopHook.Register(Tick2);

            yield return null;

            PlayerLoopHook.Unregister(Tick1);
            PlayerLoopHook.Unregister(Tick2);

            Assert.Greater(count1, 0, "First callback should have been called");
            Assert.Greater(count2, 0, "Second callback should have been called");
        }

        [UnityTest]
        public IEnumerator SelectiveUnregister_OnlyRemovesTargetCallback()
        {
            int count1 = 0, count2 = 0;
            void Tick1() => count1++;
            void Tick2() => count2++;

            PlayerLoopHook.Register(Tick1);
            PlayerLoopHook.Register(Tick2);

            yield return null;

            int count2AtUnregister = count2;
            PlayerLoopHook.Unregister(Tick2);

            yield return null;
            yield return null;

            PlayerLoopHook.Unregister(Tick1);

            Assert.Greater(count1, count2,
                "Tick1 should have more calls since Tick2 was unregistered first");
        }
    }
}
