using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Input;

namespace JriksSoundboard.Core.Tests;

public sealed class BoardTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "jriks-board-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Default_board_has_one_wheel_on_mouse4()
    {
        SoundBoard board = SoundBoard.CreateDefault();

        WheelDefinition wheel = Assert.Single(board.Wheels);
        Assert.Equal(VirtualKeys.Mouse4, wheel.Hotkey);
        Assert.Equal(8, wheel.Root.SliceCount);
    }

    [Fact]
    public void Shrinking_keeps_sounds_pointing_the_same_way()
    {
        WheelPage page = WheelPage.Create(8);
        Guid up = Fill(page, 0), upRight = Fill(page, 1), right = Fill(page, 2), down = Fill(page, 4), left = Fill(page, 6);

        Assert.Equal(1, page.CountSlotsLostByResize(4));
        page.Resize(4);

        Assert.Equal(up, page.Slots[0].SoundId);
        Assert.Equal(right, page.Slots[1].SoundId);
        Assert.Equal(down, page.Slots[2].SoundId);
        Assert.Equal(left, page.Slots[3].SoundId);
        Assert.DoesNotContain(page.Slots, s => s.SoundId == upRight); // the diagonal didn't fit
    }

    [Fact]
    public void Growing_spreads_sounds_to_matching_directions()
    {
        WheelPage page = WheelPage.Create(4);
        Guid up = Fill(page, 0), right = Fill(page, 1), down = Fill(page, 2), left = Fill(page, 3);

        Assert.Equal(0, page.CountSlotsLostByResize(8));
        page.Resize(8);

        Assert.Equal(up, page.Slots[0].SoundId);
        Assert.Equal(right, page.Slots[2].SoundId);
        Assert.Equal(down, page.Slots[4].SoundId);
        Assert.Equal(left, page.Slots[6].SoundId);
        Assert.Equal(4, page.Slots.Count(s => s.IsEmpty));
    }

    [Fact]
    public void Normalize_repairs_dangling_references_and_nested_folders()
    {
        var board = new SoundBoard();
        var sound = new SoundItem { Name = "Real", FilePath = Path.Combine(tempDir, "nope.wav") };
        board.Sounds.Add(sound);
        var wheel = new WheelDefinition();
        wheel.Root.Slots[0].AssignSound(Guid.NewGuid());   // points at a sound that doesn't exist
        wheel.Root.Slots[1].AssignSound(sound.Id);
        wheel.Root.Slots[2].MakeFolder("Outer");
        wheel.Root.Slots[2].Folder!.Slots[0].MakeFolder("Too deep");
        board.Wheels.Add(wheel);

        board.Normalize();

        Assert.True(wheel.Root.Slots[0].IsEmpty);
        Assert.Equal(sound.Id, wheel.Root.Slots[1].SoundId);
        Assert.True(wheel.Root.Slots[2].Folder!.Slots[0].IsEmpty);
        Assert.True(sound.IsMissing);
        Assert.True(sound.IsOnWheel);
    }

    [Fact]
    public void Remove_sound_clears_every_slot_that_used_it()
    {
        var board = SoundBoard.CreateDefault();
        var sound = new SoundItem { Name = "Bruh" };
        board.Sounds.Add(sound);
        WheelPage root = board.Wheels[0].Root;
        root.Slots[0].AssignSound(sound.Id);
        root.Slots[3].MakeFolder("F");
        root.Slots[3].Folder!.Slots[1].AssignSound(sound.Id);

        Assert.Equal(2, board.CountUses(sound.Id));
        board.RemoveSound(sound);

        Assert.Empty(board.Sounds);
        Assert.True(root.Slots[0].IsEmpty);
        Assert.True(root.Slots[3].Folder!.Slots[1].IsEmpty);
    }

    [Fact]
    public void Snapshot_resolves_labels_and_finds_wheels_by_key()
    {
        var board = SoundBoard.CreateDefault();
        var sound = new SoundItem { Name = "Airhorn", FilePath = "a.wav", Volume = 0.5f };
        board.Sounds.Add(sound);
        board.Wheels[0].Root.Slots[2].AssignSound(sound.Id);
        board.Wheels[0].Root.Slots[4].MakeFolder("Reactions");

        BoardSnapshot snapshot = BoardSnapshot.From(board);

        WheelSnapshot wheel = snapshot.WheelForKey(VirtualKeys.Mouse4)!;
        Assert.Equal("Airhorn", wheel.Root.Slots[2].Label);
        Assert.Equal("Reactions", wheel.Root.Slots[4].Label);
        Assert.Equal(SlotKind.Empty, wheel.Root.Slots[0].Kind);
        Assert.Equal(0.5f, snapshot.Sounds[sound.Id].Volume);
        Assert.Null(snapshot.WheelForKey(VirtualKeys.None));
    }

    [Fact]
    public void Store_round_trips_wheels_folders_and_sounds()
    {
        string path = Path.Combine(tempDir, "board.json");
        var board = SoundBoard.CreateDefault();
        var sound = new SoundItem { Name = "Wow", FilePath = @"C:\sounds\wow.mp3", Volume = 1.3f, DurationSeconds = 1.25 };
        board.Sounds.Add(sound);
        board.Wheels[0].Name = "Memes";
        board.Wheels[0].Root.Slots[4].MakeFolder("Reactions", sliceCount: 6);
        board.Wheels[0].Root.Slots[4].Folder!.Slots[0].AssignSound(sound.Id);

        BoardStore.Save(board, path);
        SoundBoard loaded = BoardStore.Load(path);

        Assert.Contains("\"Folder\"", File.ReadAllText(path)); // enums stored as readable names
        WheelDefinition wheel = Assert.Single(loaded.Wheels);
        Assert.Equal("Memes", wheel.Name);
        WheelSlot folder = wheel.Root.Slots[4];
        Assert.Equal(SlotKind.Folder, folder.Kind);
        Assert.Equal(6, folder.Folder!.SliceCount);
        Assert.Equal(sound.Id, folder.Folder.Slots[0].SoundId);
        SoundItem loadedSound = Assert.Single(loaded.Sounds);
        Assert.Equal(1.3f, loadedSound.Volume);
        Assert.Equal("1.3s", loadedSound.DurationText);
        Assert.True(loadedSound.IsOnWheel);
    }

    [Fact]
    public void Missing_board_file_gives_the_default_board()
    {
        SoundBoard board = BoardStore.Load(Path.Combine(tempDir, "missing.json"));
        Assert.Single(board.Wheels);
    }

    private static Guid Fill(WheelPage page, int index)
    {
        Guid id = Guid.NewGuid();
        page.Slots[index].AssignSound(id);
        return id;
    }
}
