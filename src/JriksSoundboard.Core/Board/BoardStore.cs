using JriksSoundboard.Core.Settings;

namespace JriksSoundboard.Core.Board;

/// <summary>Loads/saves the library + wheels (board.json).</summary>
public static class BoardStore
{
    /// <summary>Returns a default board (one empty wheel on Mouse 4) when the file doesn't exist yet.</summary>
    public static SoundBoard Load(string path)
    {
        if (!File.Exists(path))
        {
            return SoundBoard.CreateDefault();
        }

        SoundBoard board = JsonFile.Read<SoundBoard>(path);
        board.Normalize();
        return board;
    }

    public static void Save(SoundBoard board, string path) => JsonFile.WriteAtomic(path, board);
}
