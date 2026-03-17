using System;
using System.Collections.Generic;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace UnityInputSyncerCore.Utils
{
    public static class PlayerLoopHook
    {
        static readonly List<Action> callbacks = new();

        public static void Register(Action tick)
        {
            if (callbacks.Count == 0)
                Inject();

            callbacks.Add(tick);
        }

        public static void Unregister(Action tick)
        {
            callbacks.Remove(tick);
        }

        static void Inject()
        {
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
                    list.CopyTo(newList, 0);

                    newList[list.Length] = new PlayerLoopSystem
                    {
                        type = typeof(PlayerLoopHook),
                        updateDelegate = fn
                    };

                    root.subSystemList[i].subSystemList = newList;
                    return;
                }
            }
        }
    }
}