using System.Collections.Generic;

namespace UnityInputSyncerClient.Examples.ServerSimulation
{
    /// <summary>
    /// Input sent by clients to move their player on the grid.
    /// </summary>
    public class MoveInput : BaseInputData
    {
        public static string Type => "move";
        public override string type { get => Type; set { } }

        public MoveInput(MoveInputData data) : base(data) { }
    }

    public class MoveInputData
    {
        public int dx;
        public int dy;
    }

    /// <summary>
    /// Authoritative game state broadcast by the server after each step.
    /// </summary>
    public class SimulationGameState
    {
        public int step;
        public Dictionary<string, PlayerPosition> players;
    }

    public class PlayerPosition
    {
        public int x;
        public int y;
    }
}
