using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using UnityInputSyncerCore.Utils;

namespace UnityInputSyncerUTPServer
{
    public class AdminPoolOperations : IAdminPoolOperations
    {
        private readonly InputSyncerServerPool pool;

        public AdminPoolOperations(InputSyncerServerPool pool)
        {
            this.pool = pool;
        }

        public bool RequireMatchUserDataOnCreate => pool.RequireMatchUserDataOnCreate;

        public Task<AdminInstanceInfo> CreateInstanceAsync(AdminCreateInstanceRequest request)
        {
            return UnityThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                InputSyncerServerOptions overrides = null;
                if (request != null)
                {
                    overrides = new InputSyncerServerOptions();
                    if (request.MaxPlayers.HasValue)
                        overrides.MaxPlayers = request.MaxPlayers.Value;
                    if (request.StepIntervalSeconds.HasValue)
                        overrides.StepIntervalSeconds = request.StepIntervalSeconds.Value;
                    if (request.AutoStartWhenFull.HasValue)
                        overrides.AutoStartWhenFull = request.AutoStartWhenFull.Value;
                    if (request.AllowLateJoin.HasValue)
                        overrides.AllowLateJoin = request.AllowLateJoin.Value;
                    if (request.SendStepHistoryOnLateJoin.HasValue)
                        overrides.SendStepHistoryOnLateJoin = request.SendStepHistoryOnLateJoin.Value;
                    MatchAccessCreateValidation.MapToOptions(request, overrides);
                    AdminMatchContextValidation.MapMatchContextToOptions(request, overrides);
                }

                var instance = pool.CreateInstance(overrides);
                return MapToInfo(instance);
            });
        }

        public Task<bool> DestroyInstanceAsync(string id)
        {
            return UnityThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                var instance = pool.GetInstance(id);
                if (instance == null)
                    return false;

                pool.DestroyInstance(id);
                return true;
            });
        }

        public Task<AdminInstanceInfo> GetInstanceAsync(string id)
        {
            return UnityThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                var instance = pool.GetInstance(id);
                return instance != null ? MapToInfo(instance) : null;
            });
        }

        public Task<List<AdminInstanceInfo>> GetAllInstancesAsync()
        {
            return UnityThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                return pool.GetAllInstances().Select(MapToInfo).ToList();
            });
        }

        public Task<AdminPoolStats> GetPoolStatsAsync()
        {
            return UnityThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                var all = pool.GetAllInstances();

                long workingSetBytes = 0;
                try
                {
                    workingSetBytes = Process.GetCurrentProcess().WorkingSet64;
                }
                catch
                {
                    // May not be available on all platforms
                }

                return new AdminPoolStats
                {
                    TotalInstances = all.Count,
                    AvailableSlots = pool.GetAvailableSlots(),
                    IdleCount = all.Count(i => i.State == ServerInstanceState.Idle),
                    WaitingCount = all.Count(i => i.State == ServerInstanceState.WaitingForPlayers),
                    InMatchCount = all.Count(i => i.State == ServerInstanceState.InMatch),
                    FinishedCount = all.Count(i => i.State == ServerInstanceState.Finished),
                    Instances = all.Select(MapToInfo).ToList(),
                    ResourceUsage = new AdminResourceUsage
                    {
                        ManagedMemoryBytes = GC.GetTotalMemory(false),
                        WorkingSetBytes = workingSetBytes,
                        ProcessorCount = Environment.ProcessorCount,
                    },
                };
            });
        }

        private AdminInstanceInfo MapToInfo(ServerInstance instance)
        {
            var host = pool.PoolOptions.PublicHost?.Trim() ?? "";
            var info = new AdminInstanceInfo
            {
                Id = instance.Id,
                Port = instance.Port,
                State = instance.State.ToString(),
                PlayerCount = instance.Server.GetPlayerCount(),
                JoinedPlayerCount = instance.Server.GetJoinedPlayerCount(),
                MatchStarted = instance.Server.IsMatchStarted,
                MatchFinished = instance.Server.IsMatchFinished,
                CreatedAt = instance.CreatedAt,
                CurrentStep = instance.Server.GetState().CurrentStep,
                UptimeSeconds = (DateTime.UtcNow - instance.CreatedAt).TotalSeconds,
                MatchAccess = MatchAccessCreateValidation.AccessModeToApiString(
                    instance.Server.GetOptions().MatchAccess),
                AllowedMatchTokenCount = instance.Server.GetOptions().AllowedMatchTokens?.Count ?? 0,
            };

            info.ClientConnection = new AdminClientConnectionInfo
            {
                Transport = "utp",
                MatchId = instance.Id,
                Host = host,
                Port = instance.Port,
                SocketIoUrl = null,
                MatchGatewayPath = null,
            };
            info.ServerUrl = string.IsNullOrEmpty(host) ? null : $"{host}:{instance.Port}";
            return info;
        }
    }
}
