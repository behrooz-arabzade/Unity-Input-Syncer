namespace UnityInputSyncerClient.Tests
{
    public class TestInput : BaseInputData
    {
        public static string TypeName => "test-input";
        public override string type { get => TypeName; set { } }

        public TestInput(object data) : base(data) { }
        public TestInput(object data, int expectedCastTimeMs) : base(data, expectedCastTimeMs) { }
        public TestInput(object data, int expectedCastTimeMs, bool forceCast) : base(data, expectedCastTimeMs, forceCast) { }
    }

    public class TestInputData
    {
        public string action;
        public int value;
    }
}
