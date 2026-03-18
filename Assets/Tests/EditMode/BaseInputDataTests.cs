using NUnit.Framework;
using UnityInputSyncerClient;
using UnityInputSyncerClient.Tests;

namespace Tests.EditMode
{
    public class BaseInputDataTests
    {
        [Test]
        public void GetData_DeserializesToExpectedType()
        {
            var originalData = new TestInputData { action = "move", value = 42 };
            var input = new TestInput(originalData);

            var deserialized = input.GetData<TestInputData>();

            Assert.AreEqual("move", deserialized.action);
            Assert.AreEqual(42, deserialized.value);
        }

        [Test]
        public void IsTypeOf_ReturnsTrue_ForMatchingType()
        {
            var input = new TestInput(new { });
            Assert.IsTrue(input.IsTypeOf("test-input"));
        }

        [Test]
        public void IsTypeOf_ReturnsFalse_ForNonMatchingType()
        {
            var input = new TestInput(new { });
            Assert.IsFalse(input.IsTypeOf("other-type"));
            Assert.IsFalse(input.IsTypeOf(""));
            Assert.IsFalse(input.IsTypeOf("Test-Input")); // case-sensitive
        }

        [Test]
        public void JoinInput_Type_IsUserJoin()
        {
            Assert.AreEqual("user-join", JoinInput.Type);

            var joinInput = new JoinInput(new JoinInput.JoinInputData { userId = "player-1" });
            Assert.AreEqual("user-join", joinInput.type);
            Assert.IsTrue(joinInput.IsTypeOf("user-join"));
        }

        [Test]
        public void JoinInput_GetData_ReturnsJoinInputData()
        {
            var joinData = new JoinInput.JoinInputData { userId = "player-1" };
            var joinInput = new JoinInput(joinData);

            var deserialized = joinInput.GetData();

            Assert.AreEqual("player-1", deserialized.userId);
        }

        [Test]
        public void Constructor_WithExpectedCastTimeMs_SetsField()
        {
            var input = new TestInput(new { }, 500);

            Assert.AreEqual(500, input.expectedCastTimeMs);
        }

        [Test]
        public void Constructor_WithForceCast_SetsFields()
        {
            var input = new TestInput(new { }, 300, true);

            Assert.AreEqual(300, input.expectedCastTimeMs);
            Assert.IsTrue(input.forceCast);
        }
    }
}
