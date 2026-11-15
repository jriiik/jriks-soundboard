using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Input;
using JriksSoundboard.Core.Runtime;
using JriksSoundboard.Core.Settings;

namespace JriksSoundboard.Core.Tests;

/// <summary>Runtime logic that doesn't need audio devices (the runtime is never started here).</summary>
public sealed class SoundboardRuntimeTests : IDisposable
{
    private const int KeyG = 0x47;
    private const int KeyH = 0x48;

    private readonly SoundBoard board = SoundBoard.CreateDefault(); // "Wheel 1" on Mouse 4
    private readonly AppSettings settings = new();
    private readonly SoundboardRuntime runtime;

    public SoundboardRuntimeTests()
    {
        runtime = new SoundboardRuntime(settings, board, "unused-settings.json", "unused-board.json") { PersistenceEnabled = false };
    }

    public void Dispose() => runtime.Dispose();

    [Fact]
    public void Cancel_key_may_share_a_key_with_a_wheel()
    {
        Assert.Null(runtime.FindHotkeyConflict(VirtualKeys.Mouse4, HotkeyRole.CancelWheel));
    }

    [Fact]
    public void Wheel_key_may_equal_the_cancel_key()
    {
        settings.CancelKey = KeyG;
        var second = new WheelDefinition { Name = "Second" };
        board.Wheels.Add(second);

        Assert.Null(runtime.FindHotkeyConflict(KeyG, HotkeyRole.Wheel, second.Id));
    }

    [Fact]
    public void Two_wheels_cannot_share_a_key()
    {
        var second = new WheelDefinition { Name = "Second" };
        board.Wheels.Add(second);

        Assert.NotNull(runtime.FindHotkeyConflict(VirtualKeys.Mouse4, HotkeyRole.Wheel, second.Id));
        Assert.Null(runtime.FindHotkeyConflict(VirtualKeys.Mouse4, HotkeyRole.Wheel, board.Wheels[0].Id)); // its own key
    }

    [Fact]
    public void Stop_key_and_cancel_key_must_differ()
    {
        settings.CancelKey = KeyG;
        Assert.NotNull(runtime.FindHotkeyConflict(KeyG, HotkeyRole.StopAll));

        settings.StopKey = KeyH;
        Assert.NotNull(runtime.FindHotkeyConflict(KeyH, HotkeyRole.CancelWheel));
    }

    [Fact]
    public void Left_mouse_is_never_allowed()
    {
        Assert.NotNull(runtime.FindHotkeyConflict(VirtualKeys.MouseLeft, HotkeyRole.CancelWheel));
        Assert.NotNull(runtime.FindHotkeyConflict(VirtualKeys.MouseLeft, HotkeyRole.Wheel));
    }

    [Fact]
    public void Loading_a_configuration_refills_the_same_objects()
    {
        var loadedBoard = new SoundBoard();
        var sound = new SoundItem { Name = "nope", FilePath = "nope.wav" };
        loadedBoard.Sounds.Add(sound);
        var wheel = new WheelDefinition { Name = "Loaded", Hotkey = VirtualKeys.Mouse5 };
        wheel.Root.Slots[2].AssignSound(sound.Id);
        loadedBoard.Wheels.Add(wheel);
        var loadedSettings = new AppSettings { Deadzone = 90, CancelKey = KeyG };
        var soundsCollection = board.Sounds;

        runtime.ReplaceConfiguration(loadedSettings, loadedBoard);

        Assert.Same(soundsCollection, runtime.Board.Sounds); // UI bindings keep working
        Assert.Same(settings, runtime.Settings);
        Assert.Equal("Loaded", Assert.Single(runtime.Board.Wheels).Name);
        Assert.Equal(90, runtime.Settings.Deadzone);
        Assert.Equal(KeyG, runtime.Wheels.Options.CancelKey);
        Assert.Equal("nope", runtime.Wheels.Board.WheelForKey(VirtualKeys.Mouse5)!.Root.Slots[2].Label);
    }
}
