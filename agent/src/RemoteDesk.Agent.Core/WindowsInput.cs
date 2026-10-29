using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesk.Agent.Core;

/// <summary>Ввод через SendInput: как будто это настоящие мышь и клавиатура.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInput : IInputSink
{
    const uint InputMouse = 0, InputKeyboard = 1;
    const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
    const uint MiddleDown = 0x0020, MiddleUp = 0x0040, WheelFlag = 0x0800, HWheelFlag = 0x1000;
    const uint KeyExtended = 0x0001, KeyUp = 0x0002, KeyUnicode = 0x0004;

    public void Move(int dx, int dy) => Send(Mouse(MouseMove, dx, dy, 0));

    public void Button(MouseButton button, bool down) => Send(Mouse(button switch
    {
        MouseButton.Left => down ? LeftDown : LeftUp,
        MouseButton.Right => down ? RightDown : RightUp,
        _ => down ? MiddleDown : MiddleUp,
    }, 0, 0, 0));

    public void Wheel(int vertical, int horizontal)
    {
        if (vertical != 0)
            Send(Mouse(WheelFlag, 0, 0, vertical));
        if (horizontal != 0)
            Send(Mouse(HWheelFlag, 0, 0, horizontal));
    }

    public void Key(ushort vk, bool down)
    {
        var flags = (down ? 0 : KeyUp) | (Keys.IsExtended(vk) ? KeyExtended : 0);
        var scan = (ushort)MapVirtualKeyW(vk, 0);
        Send(Keyboard(vk, scan, flags));
    }

    public void Text(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var c in text.ReplaceLineEndings("\n"))
        {
            // Enter, Tab и Backspace — настоящими клавишами: так их понимают все программы.
            if (c is '\n' or '\t' or '\b')
            {
                var vk = (ushort)(c switch { '\n' => 0x0D, '\t' => 0x09, _ => 0x08 });
                var scan = (ushort)MapVirtualKeyW(vk, 0);
                inputs.Add(Keyboard(vk, scan, 0));
                inputs.Add(Keyboard(vk, scan, KeyUp));
                continue;
            }
            // Unicode-ввод не зависит от раскладки на ПК; суррогатные пары идут двумя событиями, как и положено.
            inputs.Add(Keyboard(0, c, KeyUnicode));
            inputs.Add(Keyboard(0, c, KeyUnicode | KeyUp));
        }
        Send([.. inputs]);
    }

    static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0)
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static INPUT Mouse(uint flags, int dx, int dy, int data) => new()
    {
        type = InputMouse,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } },
    };

    static INPUT Keyboard(ushort vk, ushort scan, uint flags) => new()
    {
        type = InputKeyboard,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKeyW(uint code, uint mapType);
}
