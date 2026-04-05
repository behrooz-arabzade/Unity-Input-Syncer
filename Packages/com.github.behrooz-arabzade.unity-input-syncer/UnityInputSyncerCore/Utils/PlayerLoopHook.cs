using System;
using System.Collections.Generic;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace UnityInputSyncerCore.Utils
{
    public static class PlayerLoopHook
    {
        static readonly List<Action> callbacks = new();
        static bool injected;

        public static void Register(Action tick)
        {
            if (!injected)
                Inject();

            callbacks.Add(tick);
        }

        public static void Unregister(Action tick)
        {
            callbacks.Remove(tick);
        }

        static void Inject()
        {
            injected = true;

            var loop = PlayerLoop.GetCurrentPlayerLoop();

            Insert(ref loop, typeof(Update), () =>
            {
                for (int i = 0; i < callbacks.Count; i++)
                    callbacks[i]?.Invoke();
            });

            PlayerLoop.SetPlayerLoop(loop);
        }

        static void Insert(ref PlayerLoopSystem root, Type target, PlayerLoopSystem.UpdateFunction fn)
        {
            for (int i = 0; i < root.subSystemList.Length; i++)
            {
                if (root.subSystemList[i].type == target)
                {
                    var list = root.subSystemList[i].subSystemList;
                    var newList = new PlayerLoopSystem[list.Length + 1];

                    // Insert at position 0 so the hook ticks before coroutines resume
                    newList[0] = new PlayerLoopSystem
                    {
                        type = typeof(PlayerLoopHook),
                        updateDelegate = fn
                    };
                    list.CopyTo(newList, 1);

                    root.subSystemList[i].subSystemList = newList;
                    return;
                }
            }
        }
    }
}