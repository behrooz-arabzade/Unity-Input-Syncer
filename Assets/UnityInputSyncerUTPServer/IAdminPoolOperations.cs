using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UnityInputSyncerUTPServer
{
    public class AdminInstanceInfo
    {
        public string Id;
        public ushort Port;
        public string State;
        public int PlayerCount;
        public int JoinedPlayerCount;
        public bool MatchStarted;
        public bool MatchFinished;
        public DateTime CreatedAt;
    }

    public class AdminCreateInstanceRequest
    {
        public int? MaxPlayers;
        public float? StepIntervalSeconds;
        public bool? AutoStartWhenFull;
        public bool? AllowLateJoin;
        public bool? SendStepHistoryOnLateJoin;
    }

    public class AdminPoolStats
    {
        public int TotalInstances;
        public int AvailableSlots;
        public int IdleCount;
        public int WaitingCount;
        public int InMatchCount;
        public int FinishedCount;
    }

    public class AdminResponse
    {
        public int StatusCode;
        public string Body;

        public AdminResponse(int statusCode, string body)
        {
            StatusCode = statusCode;
            Body = body;
        }
    }

    public interface IAdminPoolOperations
    {
        Task<AdminInstanceInfo> CreateInstanceAsync(AdminCreateInstanceRequest request);
        Task<bool> DestroyInstanceAsync(string id);
        Task<AdminInstanceInfo> GetInstanceAsync(string id);
        Task<List<AdminInstanceInfo>> GetAllInstancesAsync();
        Task<AdminPoolStats> GetPoolStatsAsync();
    }
}
