using JriksSoundboard.Core.Input;

namespace JriksSoundboard.Core.Tests;

public class RawInputListenerTests
{
    [Fact]
    public void Starts_and_stops_cleanly()
    {
        using var listener = new RawInputListener();

        listener.Start();

        Assert.True(listener.IsRunning);
        Assert.False(listener.IsDown(VirtualKeys.Mouse4));
    }

    [Fact]
    public void Cannot_start_twice()
    {
        using var listener = new RawInputListener();
        listener.Start();

        Assert.Throws<InvalidOperationException>(listener.Start);
    }

    [Fact]
    public void Cannot_start_after_dispose()
    {
        var listener = new RawInputListener();
        listener.Dispose();

        Assert.Throws<ObjectDisposedException>(listener.Start);
    }

    [Theory]
    [InlineData(VirtualKeys.Mouse4, "Mouse 4")]
    [InlineData(VirtualKeys.Mouse5, "Mouse 5")]
    [InlineData(0x47, "G")]
    [InlineData(0x72, "F3")]
    [InlineData(VirtualKeys.None, "(none)")]
    public void Key_names_are_readable(int vk, string expected)
    {
        Assert.Equal(expected, VirtualKeys.GetName(vk));
    }

    [Theory]
    [InlineData(0x10, 0x2A, false, 0xA0)] // left shift
    [InlineData(0x10, 0x36, false, 0xA1)] // right shift
    [InlineData(0x11, 0x1D, true, 0xA3)]  // right ctrl (E0)
    [InlineData(0x12, 0x38, false, 0xA4)] // left alt
    public void Modifiers_are_split_into_left_and_right(int vk, ushort makeCode, bool extended, int expected)
    {
        Assert.Equal(expected, VirtualKeys.Normalize(vk, makeCode, extended));
    }
}
