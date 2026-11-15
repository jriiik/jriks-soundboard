using static JriksSoundboard.Core.Input.NativeMethods;

namespace JriksSoundboard.Core.Input;

/// <summary>
/// Hotkeys are stored as Windows virtual-key codes. Mouse buttons have VK codes too,
/// so one int covers both keyboard keys and mouse buttons.
/// </summary>
public static class VirtualKeys
{
    public const int None = 0;
    public const int MouseLeft = 0x01;
    public const int MouseRight = 0x02;
    public const int MouseMiddle = 0x04;
    public const int Mouse4 = 0x05;
    public const int Mouse5 = 0x06;
    public const int Escape = 0x1B;

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const ushort RightShiftScanCode = 0x36;

    public static bool IsMouseButton(int vk) => vk is MouseLeft or MouseRight or MouseMiddle or Mouse4 or Mouse5;

    /// <summary>Turns generic Shift/Ctrl/Alt into their left/right variants so binds are precise.</summary>
    public static int Normalize(int vk, ushort makeCode, bool extended) => vk switch
    {
        VK_SHIFT => makeCode == RightShiftScanCode ? VK_RSHIFT : VK_LSHIFT,
        VK_CONTROL => extended ? VK_RCONTROL : VK_LCONTROL,
        VK_MENU => extended ? VK_RMENU : VK_LMENU,
        _ => vk,
    };

    public static string GetName(int vk)
    {
        switch (vk)
        {
            case None: return "(none)";
            case MouseLeft: return "Mouse Left";
            case MouseRight: return "Mouse Right";
            case MouseMiddle: return "Mouse Middle";
            case Mouse4: return "Mouse 4";
            case Mouse5: return "Mouse 5";
            case VK_LSHIFT: return "Left Shift";
            case VK_RSHIFT: return "Right Shift";
            case VK_LCONTROL: return "Left Ctrl";
            case VK_RCONTROL: return "Right Ctrl";
            case VK_LMENU: return "Left Alt";
            case VK_RMENU: return "Right Alt";
        }

        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)vk).ToString();
        }
        if (vk is >= 0x70 and <= 0x87)
        {
            return "F" + (vk - 0x6F);
        }
        if (vk is >= 0x60 and <= 0x69)
        {
            return "Num " + (vk - 0x60);
        }

        string? fromWindows = GetKeyNameFromWindows(vk);
        return string.IsNullOrWhiteSpace(fromWindows) ? $"Key 0x{vk:X2}" : fromWindows;
    }

    private static string? GetKeyNameFromWindows(int vk)
    {
        uint scanCode = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        if (scanCode == 0)
        {
            return null;
        }

        int lParam = (int)(scanCode << 16);
        if (IsExtendedKey(vk))
        {
            lParam |= 1 << 24;
        }

        var name = new char[64];
        int length = GetKeyNameText(lParam, name, name.Length);
        return length > 0 ? new string(name, 0, length) : null;
    }

    // Navigation keys share scan codes with the numpad; the extended bit picks the right name.
    private static bool IsExtendedKey(int vk) =>
        vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x6F or 0x90;
}
