using System.Runtime.InteropServices;

namespace SchoolPc.Admin;

// Captures only physical Windows keys/chords while the remote viewport has focus.
// Other apps and injected keys are never intercepted; hook data is never persisted.
internal sealed class RemoteWindowsKeys : IDisposable
{
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Data { public uint Code, Scan, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    private readonly Func<bool> active;
    private readonly Action<int, bool> send;
    private readonly Hook callback;
    private readonly IntPtr hook;
    private readonly HashSet<int> captured = [];
    public RemoteWindowsKeys(Func<bool> active, Action<int, bool> send)
    {
        this.active = active; this.send = send; callback = Handle;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
    }
    private IntPtr Handle(int code, IntPtr message, IntPtr pointer)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<Data>(pointer);
            bool down = (long)message is 0x100 or 0x104;
            int key = (int)data.Code;
            if ((data.Flags & 0x10) == 0 && (active() || captured.Contains(key)))
            {
                bool chord = captured.Contains(0x5B) || captured.Contains(0x5C);
                if (key is 0x5B or 0x5C || chord || captured.Contains(key))
                {
                    if (down) captured.Add(key); else captured.Remove(key);
                    if (active()) send(key, down);
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(hook, code, message, pointer);
    }
    public void Dispose() { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); captured.Clear(); }
}
