namespace UnityInputSyncerClient.Examples.TicTacToe
{
    public enum CellState
    {
        Empty,
        X,
        O
    }

    public enum GameResult
    {
        InProgress,
        XWins,
        OWins,
        Draw
    }

    public class TicTacToeBoard
    {
        public CellState[,] Cells = new CellState[3, 3];
        public CellState CurrentTurn = CellState.X;
        public GameResult Result = GameResult.InProgress;

        public bool TryPlaceMove(int row, int col, CellState player)
        {
            if (Result != GameResult.InProgress)
                return false;

            if (row < 0 || row > 2 || col < 0 || col > 2)
                return false;

            if (Cells[row, col] != CellState.Empty)
                return false;

            if (player != CurrentTurn)
                return false;

            Cells[row, col] = player;
            Result = CheckResult();
            CurrentTurn = CurrentTurn == CellState.X ? CellState.O : CellState.X;
            return true;
        }

        public GameResult CheckResult()
        {
            // Check rows
            for (int r = 0; r < 3; r++)
            {
                if (Cells[r, 0] != CellState.Empty &&
                    Cells[r, 0] == Cells[r, 1] && Cells[r, 1] == Cells[r, 2])
                    return Cells[r, 0] == CellState.X ? GameResult.XWins : GameResult.OWins;
            }

            // Check columns
            for (int c = 0; c < 3; c++)
            {
                if (Cells[0, c] != CellState.Empty &&
                    Cells[0, c] == Cells[1, c] && Cells[1, c] == Cells[2, c])
                    return Cells[0, c] == CellState.X ? GameResult.XWins : GameResult.OWins;
            }

            // Check diagonals
            if (Cells[0, 0] != CellState.Empty &&
                Cells[0, 0] == Cells[1, 1] && Cells[1, 1] == Cells[2, 2])
                return Cells[0, 0] == CellState.X ? GameResult.XWins : GameResult.OWins;

            if (Cells[0, 2] != CellState.Empty &&
                Cells[0, 2] == Cells[1, 1] && Cells[1, 1] == Cells[2, 0])
                return Cells[0, 2] == CellState.X ? GameResult.XWins : GameResult.OWins;

            // Check draw (all cells filled)
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    if (Cells[r, c] == CellState.Empty)
                        return GameResult.InProgress;

            return GameResult.Draw;
        }
    }
}
