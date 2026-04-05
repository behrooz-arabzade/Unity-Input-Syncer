using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

namespace UnityInputSyncerUTPServer
{
    public class AdminClientConnectionInfo
    {
        public string Transport;
        public string MatchId;
        public string Host;
        public int Port;
        /// <summary>Base URL for Socket.IO Unity client (scheme + host + port, no path).</summary>
        public string SocketIoUrl;
        public string MatchGatewayPath;
    }

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
        public int CurrentStep;
        public double UptimeSeconds;
        public string MatchAccess;
        public int AllowedMatchTokenCount;
        /// <summary>Convenience string for clients (UTP: <c>host:port</c> when pool public host is configured).</summary>
        public string ServerUrl;
        public AdminClientConnectionInfo ClientConnection;
    }

    public class AdminCreateInstanceRequest
    {
        public int? MaxPlayers;
        public float? StepIntervalSeconds;
        public bool? AutoStartWhenFull;
        public bool? AllowLateJoin;
        public bool? SendStepHistoryOnLateJoin;
        public string MatchAccess;
        public string MatchPassword;
        public List<string> AllowedMatchTokens;
        public JObject MatchData;
        public JObject Users;
    }

    public class AdminResourceUsage
    {
        public long ManagedMemoryBytes;
        public long WorkingSetBytes;
        public int ProcessorCount;
    }

    public class AdminPoolStats
    {
        public int TotalInstances;
        public int AvailableSlots;
        public int IdleCount;
        public int WaitingCount;
        public int InMatchCount;
        public int FinishedCount;
        public List<AdminInstanceInfo> Instances;
        public AdminResourceUsage ResourceUsage;
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
        bool RequireMatchUserDataOnCreate { get; }

        Task<AdminInstanceInfo> CreateInstanceAsync(AdminCreateInstanceRequest request);
        Task<bool> DestroyInstanceAsync(string id);
        Task<AdminInstanceInfo> GetInstanceAsync(string id);
        Task<List<AdminInstanceInfo>> GetAllInstancesAsync();
        Task<AdminPoolStats> GetPoolStatsAsync();
    }
}
