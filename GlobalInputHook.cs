using System.Diagnostics;
using System.Runtime.InteropServices;

namespace New_Macro_Recorder_Project;

public sealed class GlobalInputHook : IDisposable
{
    public event Action<MacroAction>? Input;
    private readonly HookProc _mouseProc, _keyProc;
    private IntPtr _mouse, _key;
    public static volatile bool Suppress;
    private const int WhMouseLl = 14, WhKeyboardLl = 13;
    public GlobalInputHook() { _mouseProc = MouseCallback; _keyProc = KeyCallback; }
    public void Start()
    {
        if (_mouse != IntPtr.Zero) return;
        using var p = Process.GetCurrentProcess();
        using var m = p.MainModule!;
        var h = GetModuleHandle(m.ModuleName);
        _mouse = SetWindowsHookEx(WhMouseLl, _mouseProc, h, 0);
        _key = SetWindowsHookEx(WhKeyboardLl, _keyProc, h, 0);
        if (_mouse == IntPtr.Zero || _key == IntPtr.Zero) { Stop(); throw new InvalidOperationException("Windows could not install the global input hooks. Try running with the same integrity level as the target app."); }
    }
    public void Stop()
    {
        if (_mouse != IntPtr.Zero) UnhookWindowsHookEx(_mouse);
        if (_key != IntPtr.Zero) UnhookWindowsHookEx(_key);
        _mouse = _key = IntPtr.Zero;
    }
    private IntPtr MouseCallback(int code, IntPtr wp, IntPtr lp)
    {
        if (code >= 0 && !Suppress)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lp);
            int message = wp.ToInt32();
            if (message == 0x020A)
                Input?.Invoke(new MacroAction { Kind = ActionKind.MouseWheel, X = m.pt.x, Y = m.pt.y, Count = unchecked((short)(m.mouseData >> 16)) });
            else
            {
                var kind = message switch { 0x0200 => ActionKind.MouseMove, 0x0201 or 0x0204 or 0x0207 or 0x020B => ActionKind.MouseDown, 0x0202 or 0x0205 or 0x0208 or 0x020C => ActionKind.MouseUp, _ => (ActionKind?)null };
                if (kind is { } k) Input?.Invoke(new MacroAction { Kind = k, X = m.pt.x, Y = m.pt.y, Value = message is 0x0204 or 0x0205 ? "Right" : message is 0x0207 or 0x0208 ? "Middle" : message is 0x020B or 0x020C ? "XButton" : "Left" });
            }
        }
        return CallNextHookEx(_mouse, code, wp, lp);
    }
    private IntPtr KeyCallback(int code, IntPtr wp, IntPtr lp)
    {
        if (code >= 0 && !Suppress)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lp);
            int msg = wp.ToInt32();
            if (msg is 0x0100 or 0x0104 or 0x0101 or 0x0105)
                Input?.Invoke(new MacroAction { Kind = msg is 0x0100 or 0x0104 ? ActionKind.KeyDown : ActionKind.KeyUp, Value = ((Keys)k.vkCode).ToString() });
        }
        return CallNextHookEx(_key, code, wp, lp);
    }
    public void Dispose() => Stop();
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? name);
}
