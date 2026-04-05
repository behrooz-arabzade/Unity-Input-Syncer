using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityInputSyncerUTPServer;

namespace Tests.Helpers
{
    public class MockAdminPoolOperations : IAdminPoolOperations
    {
        public List<AdminInstanceInfo> Instances = new List<AdminInstanceInfo>();
        public AdminPoolStats Stats = new AdminPoolStats();
        public bool ThrowOnCreate;
        public string ThrowOnCreateMessage = "Pool is full";

        public int CreateCallCount;
        public int DestroyCallCount;
        public int GetInstanceCallCount;
        public int GetAllCallCount;
        public int GetStatsCallCount;

        public AdminCreateInstanceRequest LastCreateRequest;

        public bool RequireMatchUserDataOnCreate { get; set; }

        public Task<AdminInstanceInfo> CreateInstanceAsync(AdminCreateInstanceRequest request)
        {
            CreateCallCount++;
            LastCreateRequest = request;

            if (ThrowOnCreate)
                throw new InvalidOperationException(ThrowOnCreateMessage);

            var info = new AdminInstanceInfo
            {
                Id = Guid.NewGuid().ToString(),
                Port = (ushort)(8000 + CreateCallCount),
                State = "Idle",
                PlayerCount = 0,
                JoinedPlayerCount = 0,
                MatchStarted = false,
                MatchFinished = false,
                CreatedAt = DateTime.UtcNow,
                MatchAccess = "open",
                AllowedMatchTokenCount = 0,
            };
            if (request?.AllowedMatchTokens != null && request.AllowedMatchTokens.Count > 0)
            {
                info.AllowedMatchTokenCount = new HashSet<string>(request.AllowedMatchTokens).Count;
                info.MatchAccess = "token";
            }
            else if (!string.IsNullOrEmpty(request?.MatchPassword))
            {
                info.MatchAccess = "password";
            }
            else if (!string.IsNullOrEmpty(request?.MatchAccess))
            {
                var m = request.MatchAccess.Trim().ToLowerInvariant();
                if (m == "password") info.MatchAccess = "password";
                else if (m == "token") info.MatchAccess = "token";
                else info.MatchAccess = "open";
            }
            Instances.Add(info);
            return Task.FromResult(info);
        }

        public Task<bool> DestroyInstanceAsync(string id)
        {
            DestroyCallCount++;
            var index = Instances.FindIndex(i => i.Id == id);
            if (index < 0)
                return Task.FromResult(false);

            Instances.RemoveAt(index);
            return Task.FromResult(true);
        }

        public Task<AdminInstanceInfo> GetInstanceAsync(string id)
        {
            GetInstanceCallCount++;
            var instance = Instances.Find(i => i.Id == id);
            return Task.FromResult(instance);
        }

        public Task<List<AdminInstanceInfo>> GetAllInstancesAsync()
        {
            GetAllCallCount++;
            return Task.FromResult(new List<AdminInstanceInfo>(Instances));
        }

        public Task<AdminPoolStats> GetPoolStatsAsync()
        {
            GetStatsCallCount++;
            return Task.FromResult(Stats);
        }
    }
}
