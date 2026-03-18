using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace UnityInputSyncerUTPServer
{
    public class AdminController
    {
        private readonly IAdminPoolOperations pool;
        private readonly string authToken;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
        };

        public AdminController(IAdminPoolOperations pool, string authToken = "")
        {
            this.pool = pool;
            this.authToken = authToken ?? "";
        }

        public bool ValidateAuth(string authorizationHeader)
        {
            if (string.IsNullOrEmpty(authToken))
                return true;

            if (string.IsNullOrEmpty(authorizationHeader))
                return false;

            if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return false;

            var token = authorizationHeader.Substring("Bearer ".Length).Trim();
            return token == authToken;
        }

        public async Task<AdminResponse> HandleRequestAsync(string method, string path, string body)
        {
            // Normalize path
            path = path?.TrimEnd('/') ?? "";

            if (path == "/api/instances")
            {
                switch (method?.ToUpperInvariant())
                {
                    case "GET":
                        return await HandleListInstances();
                    case "POST":
                        return await HandleCreateInstance(body);
                    default:
                        return MethodNotAllowed();
                }
            }

            if (path.StartsWith("/api/instances/") && path.Length > "/api/instances/".Length)
            {
                var id = path.Substring("/api/instances/".Length);

                switch (method?.ToUpperInvariant())
                {
                    case "GET":
                        return await HandleGetInstance(id);
                    case "DELETE":
                        return await HandleDeleteInstance(id);
                    default:
                        return MethodNotAllowed();
                }
            }

            if (path == "/api/stats")
            {
                if (method?.ToUpperInvariant() == "GET")
                    return await HandleGetStats();
                return MethodNotAllowed();
            }

            return NotFound();
        }

        private async Task<AdminResponse> HandleCreateInstance(string body)
        {
            AdminCreateInstanceRequest request = null;
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    request = JsonConvert.DeserializeObject<AdminCreateInstanceRequest>(body, JsonSettings);
                }
                catch (JsonException)
                {
                    return new AdminResponse(400, Serialize(new { error = "Invalid JSON body" }));
                }
            }

            try
            {
                var instance = await pool.CreateInstanceAsync(request);
                return new AdminResponse(201, Serialize(instance));
            }
            catch (InvalidOperationException ex)
            {
                return new AdminResponse(409, Serialize(new { error = ex.Message }));
            }
        }

        private async Task<AdminResponse> HandleListInstances()
        {
            var instances = await pool.GetAllInstancesAsync();
            return new AdminResponse(200, Serialize(instances));
        }

        private async Task<AdminResponse> HandleGetInstance(string id)
        {
            var instance = await pool.GetInstanceAsync(id);
            if (instance == null)
                return NotFound();
            return new AdminResponse(200, Serialize(instance));
        }

        private async Task<AdminResponse> HandleDeleteInstance(string id)
        {
            var destroyed = await pool.DestroyInstanceAsync(id);
            if (!destroyed)
                return NotFound();
            return new AdminResponse(200, Serialize(new { success = true }));
        }

        private async Task<AdminResponse> HandleGetStats()
        {
            var stats = await pool.GetPoolStatsAsync();
            return new AdminResponse(200, Serialize(stats));
        }

        private static AdminResponse NotFound()
        {
            return new AdminResponse(404, Serialize(new { error = "Not found" }));
        }

        private static AdminResponse MethodNotAllowed()
        {
            return new AdminResponse(405, Serialize(new { error = "Method not allowed" }));
        }

        private static string Serialize(object obj)
        {
            return JsonConvert.SerializeObject(obj, JsonSettings);
        }
    }
}
