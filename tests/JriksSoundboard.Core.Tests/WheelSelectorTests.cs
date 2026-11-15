using JriksSoundboard.Core.Wheel;

namespace JriksSoundboard.Core.Tests;

public class WheelSelectorTests
{
    // Screen convention: +x right, +y DOWN (raw mouse deltas).
    [Theory]
    [InlineData(0, -100, 0)]    // up
    [InlineData(70, -70, 1)]    // up-right
    [InlineData(100, 0, 2)]     // right
    [InlineData(70, 70, 3)]     // down-right
    [InlineData(0, 100, 4)]     // down
    [InlineData(-70, 70, 5)]    // down-left
    [InlineData(-100, 0, 6)]    // left
    [InlineData(-70, -70, 7)]   // up-left
    public void Eight_slice_directions_map_clockwise_from_up(double x, double y, int expected)
    {
        Assert.Equal(expected, WheelSelector.SliceForVector(x, y, 8));
    }

    [Theory]
    [InlineData(22.4, 0)]   // just inside the "up" slice (±22.5°)
    [InlineData(22.6, 1)]   // just past the boundary
    [InlineData(337.6, 0)]  // wraps back to up from the left side
    [InlineData(337.4, 7)]
    public void Slice_boundaries_are_centred_on_each_direction(double angleDegrees, int expected)
    {
        double rad = angleDegrees * Math.PI / 180;
        double x = Math.Sin(rad) * 100;
        double y = -Math.Cos(rad) * 100;
        Assert.Equal(expected, WheelSelector.SliceForVector(x, y, 8));
    }

    [Fact]
    public void Twelve_slices_put_right_at_slice_three()
    {
        Assert.Equal(3, WheelSelector.SliceForVector(100, 0, 12));
    }

    [Fact]
    public void Movement_inside_deadzone_selects_nothing()
    {
        var wheel = new WheelSelector(8, deadzone: 60, radius: 240);
        wheel.Begin();
        wheel.AddDelta(30, -20);

        Assert.Null(wheel.GetState().SelectedSlice);
        Assert.Null(wheel.End());
    }

    [Fact]
    public void Flick_past_deadzone_selects_direction_on_release()
    {
        var wheel = new WheelSelector(8, deadzone: 60, radius: 240);
        wheel.Begin();
        wheel.AddDelta(90, 0);

        Assert.Equal(2, wheel.End());
        Assert.False(wheel.IsActive);
    }

    [Fact]
    public void Stick_is_clamped_so_reversing_direction_is_quick()
    {
        var wheel = new WheelSelector(8, deadzone: 60, radius: 240);
        wheel.Begin();
        wheel.AddDelta(5000, 0);           // huge flick right
        Assert.Equal(240, wheel.GetState().X, precision: 6);

        wheel.AddDelta(-480, 0);           // only one radius' worth back the other way…
        Assert.Equal(6, wheel.End());      // …is enough to land on "left"
    }

    [Fact]
    public void Begin_recentres_the_stick()
    {
        var wheel = new WheelSelector();
        wheel.Begin();
        wheel.AddDelta(200, 200);
        wheel.End();

        wheel.Begin();
        WheelState state = wheel.GetState();
        Assert.Equal(0, state.X);
        Assert.Equal(0, state.Y);
        Assert.Null(state.SelectedSlice);
    }

    [Fact]
    public void Movement_is_ignored_while_wheel_is_closed()
    {
        var wheel = new WheelSelector();
        wheel.AddDelta(500, 0);
        wheel.Begin();
        Assert.Equal(0, wheel.GetState().X);
    }

    [Fact]
    public void Cancel_discards_the_selection()
    {
        var wheel = new WheelSelector();
        wheel.Begin();
        wheel.AddDelta(0, -300);
        wheel.Cancel();
        Assert.Null(wheel.End());
    }

    [Fact]
    public void Configure_rejects_radius_not_larger_than_deadzone()
    {
        var wheel = new WheelSelector();
        Assert.Throws<ArgumentOutOfRangeException>(() => wheel.Configure(100, 100));
    }

    [Fact]
    public void Slice_count_is_limited()
    {
        var wheel = new WheelSelector();
        Assert.Throws<ArgumentOutOfRangeException>(() => wheel.SliceCount = 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => wheel.SliceCount = 17);
    }

    [Theory]
    [InlineData(0, 8, "Up")]
    [InlineData(3, 8, "Down-Right")]
    [InlineData(1, 4, "Right")]
    public void Describes_slices_with_compass_names(int slice, int count, string expected)
    {
        Assert.Equal(expected, WheelSelector.DescribeSlice(slice, count));
    }
}
