using System;
using UnityInputSyncerCore;

namespace UnityInputSyncerUTPServer
{
    public class InputSyncerServerOptions
    {
        public ushort Port = 7777;
        public float HeartbeatTimeout = 15f;
        public int MaxPlayers = 2;
        public bool AutoStartWhenFull = false;
        public float StepIntervalSeconds = 0.1f;
        public bool AllowLateJoin = false;
        public bool SendStepHistoryOnLateJoin = true;

        /// <summary>When true (default), all joined players must send user-finish before FinishMatch.</summary>
        public bool QuorumUserFinishEndsMatch = true;

        /// <summary>Max UTF-8 bytes for serialized session-finish data payload.</summary>
        public int SessionFinishMaxPayloadBytes = 4096;

        /// <summary>When false, only the finishing client receives on-player-session-finish.</summary>
        public bool SessionFinishBroadcast = true;

        /// <summary>When true, ignore gameplay input after player-session-finish for that connection.</summary>
        public bool RejectInputAfterSessionFinish = false;

        /// <summary>When AllowLateJoin and lobby is partial, finish match after this many seconds (0 = disabled).</summary>
        public float AbandonMatchTimeoutSeconds = 0f;

        /// <summary>Pool instance id for reward hooks; optional for single dedicated server.</summary>
        public string MatchInstanceId = "";

        public RewardOutcomeDeliveryMode RewardOutcomeDelivery = RewardOutcomeDeliveryMode.ClientToAdmin;

        public Action<RewardPerUserHookContext> OnRewardHookPerUser;
        public Action<RewardMatchHookContext> OnRewardHookMatch;
    }
}
