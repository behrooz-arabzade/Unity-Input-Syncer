using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tests.Helpers;
using UnityInputSyncerUTPServer;

namespace Tests.EditMode
{
    public class AdminControllerTests
    {
        private MockAdminPoolOperations mockPool;
        private AdminController controller;

        [SetUp]
        public void SetUp()
        {
            mockPool = new MockAdminPoolOperations();
        }

        private AdminController CreateController(string authToken = "")
        {
            controller = new AdminController(mockPool, authToken);
            return controller;
        }

        // =========================================================
        // Auth Tests
        // =========================================================

        [Test]
        public void ValidateAuth_NoTokenConfigured_AlwaysPasses()
        {
            var ctrl = CreateController("");
            Assert.IsTrue(ctrl.ValidateAuth(null));
            Assert.IsTrue(ctrl.ValidateAuth(""));
            Assert.IsTrue(ctrl.ValidateAuth("Bearer anything"));
        }

        [Test]
        public void ValidateAuth_ValidToken_ReturnsTrue()
        {
            var ctrl = CreateController("secret");
            Assert.IsTrue(ctrl.ValidateAuth("Bearer secret"));
        }

        [Test]
        public void ValidateAuth_InvalidToken_ReturnsFalse()
        {
            var ctrl = CreateController("secret");
            Assert.IsFalse(ctrl.ValidateAuth("Bearer wrong"));
        }

        [Test]
        public void ValidateAuth_MissingHeader_ReturnsFalse()
        {
            var ctrl = CreateController("secret");
            Assert.IsFalse(ctrl.ValidateAuth(null));
            Assert.IsFalse(ctrl.ValidateAuth(""));
        }

        [Test]
        public void ValidateAuth_MalformedHeader_ReturnsFalse()
        {
            var ctrl = CreateController("secret");
            Assert.IsFalse(ctrl.ValidateAuth("Basic secret"));
            Assert.IsFalse(ctrl.ValidateAuth("secret"));
            Assert.IsFalse(ctrl.ValidateAuth("Token secret"));
        }

        // =========================================================
        // POST /api/instances
        // =========================================================

        [Test]
        public async Task PostInstances_CreatesWithDefaults()
        {
            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("POST", "/api/instances", null);

            Assert.AreEqual(201, response.StatusCode);
            Assert.AreEqual(1, mockPool.CreateCallCount);
            Assert.IsNull(mockPool.LastCreateRequest);
        }

        [Test]
        public async Task PostInstances_CreatesWithOverrides()
        {
            var ctrl = CreateController();
            var body = "{\"maxPlayers\":4,\"stepIntervalSeconds\":0.05}";
            var response = await ctrl.HandleRequestAsync("POST", "/api/instances", body);

            Assert.AreEqual(201, response.StatusCode);
            Assert.IsNotNull(mockPool.LastCreateRequest);
            Assert.AreEqual(4, mockPool.LastCreateRequest.MaxPlayers);
            Assert.AreEqual(0.05f, mockPool.LastCreateRequest.StepIntervalSeconds);
        }

        [Test]
        public async Task PostInstances_Returns201WithInstanceJson()
        {
            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("POST", "/api/instances", null);

            Assert.AreEqual(201, response.StatusCode);
            var json = JObject.Parse(response.Body);
            Assert.IsNotNull(json["id"]);
            Assert.IsNotNull(json["port"]);
            Assert.AreEqual("Idle", json["state"].ToString());
        }

        [Test]
        public async Task PostInstances_PoolFull_Returns409()
        {
            mockPool.ThrowOnCreate = true;
            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("POST", "/api/instances", null);

            Assert.AreEqual(409, response.StatusCode);
            var json = JObject.Parse(response.Body);
            Assert.IsNotNull(json["error"]);
        }

        // =========================================================
        // GET /api/instances
        // =========================================================

        [Test]
        public async Task GetInstances_ReturnsAll()
        {
            mockPool.Instances.Add(new AdminInstanceInfo { Id = "a", Port = 8001, State = "Idle" });
            mockPool.Instances.Add(new AdminInstanceInfo { Id = "b", Port = 8002, State = "InMatch" });

            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("GET", "/api/instances", null);

            Assert.AreEqual(200, response.StatusCode);
            var arr = JArray.Parse(response.Body);
            Assert.AreEqual(2, arr.Count);
        }

        [Test]
        public async Task GetInstances_Empty_ReturnsEmptyArray()
        {
            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("GET", "/api/instances", null);

            Assert.AreEqual(200, response.StatusCode);
            var arr = JArray.Parse(response.Body);
            Assert.AreEqual(0, arr.Count);
        }

        // =========================================================
        // GET /api/instances/{id}
        // =========================================================

        [Test]
        public async Task GetInstanceById_Found_Returns200()
        {
            mockPool.Instances.Add(new AdminInstanceInfo { Id = "abc-123", Port = 8001, State = "WaitingForPlayers" });

            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("GET", "/api/instances/abc-123", null);

            Assert.AreEqual(200, response.StatusCode);
            var json = JObject.Parse(response.Body);
            Assert.AreEqual("abc-123", json["id"].ToString());
        }

        [Test]
        public async Task GetInstanceById_NotFound_Returns404()
        {
            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("GET", "/api/instances/nonexistent", null);

            Assert.AreEqual(404, response.StatusCode);
        }

        // =========================================================
        // DELETE /api/instances/{id}
        // =========================================================

        [Test]
        public async Task DeleteInstance_Found_Returns200()
        {
            mockPool.Instances.Add(new AdminInstanceInfo { Id = "del-1", Port = 8001, State = "Idle" });

            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("DELETE", "/api/instances/del-1", null);

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual(1, mockPool.DestroyCallCount);
        }

        [Test]
        public async Task DeleteInstance_NotFound_Returns404()
        {
            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("DELETE", "/api/instances/nonexistent", null);

            Assert.AreEqual(404, response.StatusCode);
        }

        // =========================================================
        // GET /api/stats
        // =========================================================

        [Test]
        public async Task GetStats_ReturnsStats()
        {
            mockPool.Stats = new AdminPoolStats
            {
                TotalInstances = 3,
                AvailableSlots = 7,
                IdleCount = 1,
                WaitingCount = 1,
                InMatchCount = 1,
                FinishedCount = 0,
            };

            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("GET", "/api/stats", null);

            Assert.AreEqual(200, response.StatusCode);
            var json = JObject.Parse(response.Body);
            Assert.AreEqual(3, json["totalInstances"].Value<int>());
            Assert.AreEqual(7, json["availableSlots"].Value<int>());
            Assert.AreEqual(1, json["idleCount"].Value<int>());
        }

        // =========================================================
        // Routing
        // =========================================================

        [Test]
        public async Task UnknownPath_Returns404()
        {
            var ctrl = CreateController();
            var response = await ctrl.HandleRequestAsync("GET", "/api/unknown", null);

            Assert.AreEqual(404, response.StatusCode);
        }

        [Test]
        public async Task WrongMethod_Returns405()
        {
            var ctrl = CreateController();

            var response = await ctrl.HandleRequestAsync("PUT", "/api/instances", null);
            Assert.AreEqual(405, response.StatusCode);

            response = await ctrl.HandleRequestAsync("POST", "/api/instances/some-id", null);
            Assert.AreEqual(405, response.StatusCode);

            response = await ctrl.HandleRequestAsync("DELETE", "/api/stats", null);
            Assert.AreEqual(405, response.StatusCode);
        }

        [Test]
        public async Task AuthFailure_ControllerDoesNotEnforceAuth()
        {
            // Auth is checked externally (by AdminHttpServer), not by the controller.
            // The controller always processes the request. This test verifies that
            // ValidateAuth correctly rejects, but HandleRequestAsync still works.
            var ctrl = CreateController("secret");
            Assert.IsFalse(ctrl.ValidateAuth("Bearer wrong"));

            // Controller still processes the request regardless of auth
            var response = await ctrl.HandleRequestAsync("GET", "/api/instances", null);
            Assert.AreEqual(200, response.StatusCode);
        }
    }
}
