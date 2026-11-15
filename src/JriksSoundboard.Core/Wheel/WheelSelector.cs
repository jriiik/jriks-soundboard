namespace JriksSoundboard.Core.Wheel;

/// <summary>Snapshot of the wheel for drawing: virtual stick position and the slice it points at.</summary>
public readonly record struct WheelState(bool IsActive, double X, double Y, int? SelectedSlice, int SliceCount, double Deadzone, double Radius);

/// <summary>
/// Turns raw mouse movement into a wheel selection, like a virtual joystick.
/// <para>
/// While the wheel key is held, mouse deltas move a virtual "stick" that is clamped to
/// <see cref="Radius"/>, so you can change your mind by moving the other way. Inside
/// <see cref="Deadzone"/> nothing is selected (release there = cancel). Outside it, the
/// direction picks a slice: slice 0 is centred straight up and the rest go clockwise.
/// </para>
/// <para>Thread-safe: input arrives on the raw-input thread, the UI reads <see cref="GetState"/>.</para>
/// </summary>
public sealed class WheelSelector
{
    public const int MinSlices = 2;
    public const int MaxSlices = 16;

    private readonly object gate = new();
    private int sliceCount;
    private double deadzone;
    private double radius;
    private double x;
    private double y;
    private bool active;

    public WheelSelector(int sliceCount = 8, double deadzone = 60, double radius = 240)
    {
        SliceCount = sliceCount;
        Configure(deadzone, radius);
    }

    public int SliceCount
    {
        get { lock (gate) return sliceCount; }
        set
        {
            if (value is < MinSlices or > MaxSlices)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Slice count must be {MinSlices}-{MaxSlices}.");
            }
            lock (gate) sliceCount = value;
        }
    }

    /// <summary>Distance (raw mouse counts) the stick must travel before anything is selected.</summary>
    public double Deadzone { get { lock (gate) return deadzone; } }

    /// <summary>Maximum stick distance. Smaller = quicker to reverse direction.</summary>
    public double Radius { get { lock (gate) return radius; } }

    public bool IsActive { get { lock (gate) return active; } }

    public void Configure(double newDeadzone, double newRadius)
    {
        if (newDeadzone < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newDeadzone), "Deadzone cannot be negative.");
        }
        if (newRadius <= newDeadzone)
        {
            throw new ArgumentOutOfRangeException(nameof(newRadius), "Radius must be larger than the deadzone.");
        }
        lock (gate)
        {
            deadzone = newDeadzone;
            radius = newRadius;
            ClampLocked();
        }
    }

    /// <summary>Wheel key pressed: recentre the stick and start tracking.</summary>
    public void Begin()
    {
        lock (gate)
        {
            x = 0;
            y = 0;
            active = true;
        }
    }

    /// <summary>Raw mouse delta (screen convention: +y is down).</summary>
    public void AddDelta(int dx, int dy)
    {
        lock (gate)
        {
            if (!active)
            {
                return;
            }
            x += dx;
            y += dy;
            ClampLocked();
        }
    }

    /// <summary>Wheel key released: returns the chosen slice, or null when released in the deadzone.</summary>
    public int? End()
    {
        lock (gate)
        {
            int? result = active ? SelectedLocked() : null;
            active = false;
            return result;
        }
    }

    /// <summary>Abandon the current selection without choosing anything.</summary>
    public void Cancel()
    {
        lock (gate) active = false;
    }

    public WheelState GetState()
    {
        lock (gate)
        {
            return new WheelState(active, x, y, active ? SelectedLocked() : null, sliceCount, deadzone, radius);
        }
    }

    /// <summary>Which slice a direction points at. Slice 0 is centred straight up; slices go clockwise.</summary>
    public static int SliceForVector(double vx, double vy, int sliceCount)
    {
        double angle = AngleDegrees(vx, vy);
        double width = 360.0 / sliceCount;
        int slice = (int)Math.Floor((angle + width / 2) / width);
        return slice % sliceCount;
    }

    /// <summary>Clockwise angle from straight up, 0..360 (screen convention: +y is down).</summary>
    public static double AngleDegrees(double vx, double vy)
    {
        double degrees = Math.Atan2(vx, -vy) * 180.0 / Math.PI;
        return degrees < 0 ? degrees + 360.0 : degrees;
    }

    /// <summary>Centre angle of a slice, clockwise from straight up.</summary>
    public static double SliceCenterDegrees(int slice, int sliceCount) => slice * 360.0 / sliceCount;

    /// <summary>Friendly direction name, e.g. "Up-Right" for an 8-slice wheel.</summary>
    public static string DescribeSlice(int slice, int sliceCount)
    {
        string[] compass8 = ["Up", "Up-Right", "Right", "Down-Right", "Down", "Down-Left", "Left", "Up-Left"];
        string[] compass4 = ["Up", "Right", "Down", "Left"];
        return sliceCount switch
        {
            8 => compass8[slice],
            4 => compass4[slice],
            _ => $"Slice {slice + 1} ({SliceCenterDegrees(slice, sliceCount):0}°)",
        };
    }

    private int? SelectedLocked()
    {
        double distance = Math.Sqrt(x * x + y * y);
        return distance < deadzone ? null : SliceForVector(x, y, sliceCount);
    }

    private void ClampLocked()
    {
        double distance = Math.Sqrt(x * x + y * y);
        if (distance > radius)
        {
            double scale = radius / distance;
            x *= scale;
            y *= scale;
        }
    }
}
