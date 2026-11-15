using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Input;
using JriksSoundboard.Core.Wheel;

namespace JriksSoundboard.Core.Tests;

public class WheelControllerTests
{
    private readonly Guid airhorn = Guid.NewGuid();
    private readonly Guid bruh = Guid.NewGuid();
    private readonly Guid wow = Guid.NewGuid();
    private readonly List<(WheelCloseReason Reason, SlotSnapshot? Slot)> closes = [];
    private readonly WheelController controller;
    private TimeSpan now = TimeSpan.Zero;

    public WheelControllerTests()
    {
        controller = new WheelController(() => now)
        {
            Options = new WheelOptions(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(350), Deadzone: 60, Radius: 240),
            Board = BuildBoard(),
        };
        controller.Closed += (_, reason, slot) => closes.Add((reason, slot));
    }

    // Memes (Mouse 4): right = Airhorn, left = Bruh, down = folder "Reactions" (up = Wow).
    // Callouts (Mouse 5): up = Bruh.
    private BoardSnapshot BuildBoard()
    {
        var board = new SoundBoard();
        board.Sounds.Add(new SoundItem { Id = airhorn, Name = "Airhorn", FilePath = "a.wav" });
        board.Sounds.Add(new SoundItem { Id = bruh, Name = "Bruh", FilePath = "b.wav" });
        board.Sounds.Add(new SoundItem { Id = wow, Name = "Wow", FilePath = "w.wav" });

        var memes = new WheelDefinition { Name = "Memes", Hotkey = VirtualKeys.Mouse4 };
        memes.Root.Slots[2].AssignSound(airhorn);
        memes.Root.Slots[6].AssignSound(bruh);
        memes.Root.Slots[4].MakeFolder("Reactions");
        memes.Root.Slots[4].Folder!.Slots[0].AssignSound(wow);

        var callouts = new WheelDefinition { Name = "Callouts", Hotkey = VirtualKeys.Mouse5 };
        callouts.Root.Slots[0].AssignSound(bruh);

        board.Wheels.Add(memes);
        board.Wheels.Add(callouts);
        return BoardSnapshot.From(board);
    }

    [Fact]
    public void Hotkey_opens_its_wheel_and_unbound_keys_are_ignored()
    {
        Assert.False(controller.ButtonDown(0x47)); // G
        Assert.False(controller.IsOpen);

        Assert.True(controller.ButtonDown(VirtualKeys.Mouse4));
        Assert.True(controller.IsOpen);
        Assert.Equal("Memes", controller.GetState().Wheel!.Name);
    }

    [Fact]
    public void Flick_and_release_chooses_the_sound_in_that_direction()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(120, 0);
        controller.ButtonUp(VirtualKeys.Mouse4);

        (WheelCloseReason reason, SlotSnapshot? slot) = Assert.Single(closes);
        Assert.Equal(WheelCloseReason.SoundChosen, reason);
        Assert.Equal(airhorn, slot!.SoundId);
        Assert.False(controller.IsOpen);
    }

    [Fact]
    public void Release_in_the_centre_cancels()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(20, 10);
        controller.ButtonUp(VirtualKeys.Mouse4);

        Assert.Equal(WheelCloseReason.ReleasedInCentre, Assert.Single(closes).Reason);
    }

    [Fact]
    public void Release_on_an_empty_slot_plays_nothing()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(0, -120); // up is empty
        controller.ButtonUp(VirtualKeys.Mouse4);

        Assert.Equal(WheelCloseReason.ReleasedOnEmptySlot, Assert.Single(closes).Reason);
    }

    [Fact]
    public void Holding_too_long_auto_cancels_and_the_late_release_does_nothing()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(120, 0);

        now = TimeSpan.FromSeconds(2.9);
        controller.Tick();
        Assert.True(controller.IsOpen);

        now = TimeSpan.FromSeconds(3.1);
        controller.Tick();
        Assert.False(controller.IsOpen);
        Assert.Equal(WheelCloseReason.TimedOut, Assert.Single(closes).Reason);

        Assert.True(controller.ButtonUp(VirtualKeys.Mouse4)); // swallowed
        Assert.Single(closes);                                // no sound chosen

        Assert.True(controller.ButtonDown(VirtualKeys.Mouse4)); // next press works normally
        Assert.True(controller.IsOpen);
    }

    [Fact]
    public void Time_remaining_counts_down_for_the_overlay_ring()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        now = TimeSpan.FromSeconds(1.5);

        Assert.Equal(0.5, controller.GetState().TimeRemainingFraction!.Value, precision: 3);
    }

    [Fact]
    public void Auto_cancel_can_be_turned_off()
    {
        controller.Options = new WheelOptions(null, TimeSpan.FromMilliseconds(350), 60, 240);
        controller.ButtonDown(VirtualKeys.Mouse4);

        now = TimeSpan.FromMinutes(1);
        controller.Tick();

        Assert.True(controller.IsOpen);
        Assert.Null(controller.GetState().TimeRemainingFraction);
    }

    [Fact]
    public void Resting_on_a_folder_opens_it_and_a_second_flick_picks_inside()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(0, 120); // down = folder

        now = TimeSpan.FromMilliseconds(200);
        controller.Tick();
        WheelViewState halfway = controller.GetState();
        Assert.Null(halfway.FolderName);
        Assert.InRange(halfway.FolderDwellProgress, 0.5, 0.65);

        now = TimeSpan.FromMilliseconds(400);
        controller.Tick();
        WheelViewState inFolder = controller.GetState();
        Assert.Equal("Reactions", inFolder.FolderName);
        Assert.Equal(0, inFolder.StickX); // recentred
        Assert.Equal(0, inFolder.StickY);

        controller.MouseMoved(0, -120); // up inside the folder = Wow
        controller.ButtonUp(VirtualKeys.Mouse4);

        (WheelCloseReason reason, SlotSnapshot? slot) = Assert.Single(closes);
        Assert.Equal(WheelCloseReason.SoundChosen, reason);
        Assert.Equal(wow, slot!.SoundId);
    }

    [Fact]
    public void Releasing_on_a_folder_before_it_opens_cancels()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(0, 120);
        controller.ButtonUp(VirtualKeys.Mouse4);

        Assert.Equal(WheelCloseReason.ReleasedOnFolder, Assert.Single(closes).Reason);
    }

    [Fact]
    public void Entering_a_folder_restarts_the_auto_cancel_timer()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);

        now = TimeSpan.FromSeconds(2.5);
        controller.MouseMoved(0, 120);
        controller.Tick();
        now = TimeSpan.FromSeconds(2.9);
        controller.Tick(); // folder opens here
        Assert.Equal("Reactions", controller.GetState().FolderName);

        now = TimeSpan.FromSeconds(5.0); // 5 s since the wheel opened, but only 2.1 s in the folder
        controller.Tick();
        Assert.True(controller.IsOpen);

        now = TimeSpan.FromSeconds(6.0);
        controller.Tick();
        Assert.False(controller.IsOpen);
        Assert.Equal(WheelCloseReason.TimedOut, Assert.Single(closes).Reason);
    }

    [Fact]
    public void Another_wheels_key_is_ignored_while_one_is_open()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);

        Assert.False(controller.ButtonDown(VirtualKeys.Mouse5));
        Assert.False(controller.ButtonUp(VirtualKeys.Mouse5));
        Assert.Equal("Memes", controller.GetState().Wheel!.Name);
        Assert.Empty(closes);
    }

    [Fact]
    public void Each_wheel_opens_with_its_own_key()
    {
        controller.ButtonDown(VirtualKeys.Mouse5);
        controller.MouseMoved(0, -120);
        controller.ButtonUp(VirtualKeys.Mouse5);

        Assert.Equal(bruh, Assert.Single(closes).Slot!.SoundId);
    }

    [Fact]
    public void Cancel_closes_the_wheel_and_swallows_the_release()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(120, 0);
        controller.Cancel();
        controller.ButtonUp(VirtualKeys.Mouse4);

        Assert.Equal(WheelCloseReason.Cancelled, Assert.Single(closes).Reason);
    }

    [Fact]
    public void Opened_event_reports_the_wheel()
    {
        WheelSnapshot? opened = null;
        controller.Opened += w => opened = w;

        controller.ButtonDown(VirtualKeys.Mouse5);

        Assert.Equal("Callouts", opened?.Name);
    }

    [Fact]
    public void Pointing_at_a_new_slice_restarts_the_auto_cancel_timer()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        now = TimeSpan.FromSeconds(2.0);
        controller.MouseMoved(120, 0); // point at "right": the full 3 s start again here
        controller.Tick();

        now = TimeSpan.FromSeconds(4.5);
        controller.Tick();
        Assert.True(controller.IsOpen);
        Assert.Equal(0.5 / 3, controller.GetState().TimeRemainingFraction!.Value, precision: 3);

        now = TimeSpan.FromSeconds(5.1);
        controller.Tick();
        Assert.False(controller.IsOpen);
        Assert.Equal(WheelCloseReason.TimedOut, Assert.Single(closes).Reason);
    }

    [Fact]
    public void Moving_back_to_the_centre_does_not_restart_the_timer()
    {
        controller.ButtonDown(VirtualKeys.Mouse4);
        now = TimeSpan.FromSeconds(1.0);
        controller.MouseMoved(120, 0);  // restart at 1.0 s
        now = TimeSpan.FromSeconds(2.0);
        controller.MouseMoved(-120, 0); // back to the centre: not a slice, no restart

        now = TimeSpan.FromSeconds(4.1);
        controller.Tick();

        Assert.False(controller.IsOpen);
    }

    [Fact]
    public void Restarting_on_hover_can_be_turned_off()
    {
        controller.Options = WheelOptions.Default with { RestartTimerOnHover = false };
        controller.ButtonDown(VirtualKeys.Mouse4);
        now = TimeSpan.FromSeconds(2.0);
        controller.MouseMoved(120, 0);

        now = TimeSpan.FromSeconds(3.1);
        controller.Tick();

        Assert.False(controller.IsOpen);
    }

    [Fact]
    public void Cancel_key_closes_the_wheel_immediately_and_plays_nothing()
    {
        controller.Options = WheelOptions.Default with { CancelKey = VirtualKeys.Mouse5 };
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(120, 0); // pointing at Airhorn

        Assert.True(controller.ButtonDown(VirtualKeys.Mouse5));
        Assert.False(controller.IsOpen);
        controller.ButtonUp(VirtualKeys.Mouse5);
        controller.ButtonUp(VirtualKeys.Mouse4); // released afterwards: nothing plays

        Assert.Equal(WheelCloseReason.CancelKeyPressed, Assert.Single(closes).Reason);
    }

    [Fact]
    public void Resting_on_a_sound_previews_it_once_and_moving_away_stops_it()
    {
        var previews = new List<SlotSnapshot?>();
        controller.PreviewChanged += previews.Add;
        controller.Options = WheelOptions.Default with { PreviewAfter = TimeSpan.FromSeconds(2) };
        controller.ButtonDown(VirtualKeys.Mouse4);
        controller.MouseMoved(120, 0); // Airhorn

        now = TimeSpan.FromSeconds(1.9);
        controller.Tick();
        Assert.Empty(previews);
        Assert.InRange(controller.GetState().PreviewProgress, 0.9, 1.0);

        now = TimeSpan.FromSeconds(2.0);
        controller.Tick();
        Assert.Equal(airhorn, Assert.Single(previews)!.SoundId);
        Assert.True(controller.GetState().IsPreviewing);

        now = TimeSpan.FromSeconds(4.5); // previewing restarted the 3 s timer, and it plays only once
        controller.Tick();
        Assert.True(controller.IsOpen);
        Assert.Single(previews);

        controller.MouseMoved(-240, 0); // over to Bruh: the preview stops
        Assert.Equal(2, previews.Count);
        Assert.Null(previews[1]);
    }

    [Fact]
    public void Empty_slices_and_folders_are_never_previewed()
    {
        var previews = new List<SlotSnapshot?>();
        controller.PreviewChanged += previews.Add;
        controller.Options = WheelOptions.Default with { FolderDwell = TimeSpan.FromSeconds(10), PreviewAfter = TimeSpan.FromSeconds(1) };
        controller.ButtonDown(VirtualKeys.Mouse4);

        controller.MouseMoved(0, -120); // empty slice (up)
        now = TimeSpan.FromSeconds(2);
        controller.Tick();
        controller.MouseMoved(0, 240);  // folder (down)
        now = TimeSpan.FromSeconds(4);
        controller.Tick();

        Assert.Empty(previews);
    }

    [Fact]
    public void Cancel_key_still_opens_its_own_wheel_when_none_is_open()
    {
        controller.Options = WheelOptions.Default with { CancelKey = VirtualKeys.Mouse5 };

        controller.ButtonDown(VirtualKeys.Mouse5); // Mouse 5 is also the "Callouts" hotkey

        Assert.Equal("Callouts", controller.GetState().Wheel!.Name);
    }
}
