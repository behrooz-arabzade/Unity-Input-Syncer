namespace UnityInputSyncerClient.Examples.TicTacToe
{
    public class TicTacToeReadyInput : BaseInputData
    {
        public static string Type => "ttt-ready";
        public override string type { get => Type; set { } }

        public TicTacToeReadyInput() : base(new { }) { }
    }

    public class TicTacToeMoveInput : BaseInputData
    {
        public static string Type => "ttt-move";
        public override string type { get => Type; set { } }

        public TicTacToeMoveInput(TicTacToeMoveData data) : base(data) { }
    }

    public class TicTacToeMoveData
    {
        public int row;
        public int col;
    }
}
