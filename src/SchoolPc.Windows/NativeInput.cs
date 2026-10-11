using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SchoolPc.Windows;

public sealed class NativeInput : IDisposable
{
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Code, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct PointData { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public PointData Point; public uint Mouse, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Code, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    private readonly Hook keyboardCallback, mouseCallback;
    private readonly IntPtr keyboardHook, mouseHook;
    private readonly HashSet<int> localPressed = [];
    private readonly HashSet<int> remotePressed = [];
    private readonly HashSet<int> remoteButtons = [];
    private bool mouseLocked, keyboardLocked, remoteControlBlocked;
    private long expires;
    public event Action? Emergency;
    public bool MouseLocked => remoteControlBlocked || (mouseLocked && Environment.TickCount64 < expires);
    public bool KeyboardLocked => remoteControlBlocked || (keyboardLocked && Environment.TickCount64 < expires);
    public long Remaining => remoteControlBlocked || expires == long.MaxValue ? -1 : Math.Max(0, (expires - Environment.TickCount64 + 999) / 1000);
    public NativeInput()
    {
        keyboardCallback = KeyboardProc; mouseCallback = MouseProc;
        keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
        mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
    }
    public void SetLocks(bool mouse, bool keyboard, int seconds = 60)
    {
        if ((mouse && mouseHook == IntPtr.Zero) || (keyboard && keyboardHook == IntPtr.Zero))
            throw new Win32Exception("입력 잠금 훅을 설치하지 못했습니다.");
        mouseLocked = mouse; keyboardLocked = keyboard;
        expires = seconds == 0 ? long.MaxValue : Environment.TickCount64 + Math.Clamp(seconds, 1, 120) * 1000L;
    }
    public void SetRemoteControlBlock(bool enabled)
    {
        if (enabled && (mouseHook == IntPtr.Zero || keyboardHook == IntPtr.Zero))
            throw new Win32Exception("입력 잠금 훅을 설치하지 못했습니다.");
        remoteControlBlocked = enabled;
    }
    public void UnlockManual() { mouseLocked = keyboardLocked = false; expires = 0; }
    public void Unlock() { remoteControlBlocked = false; UnlockManual(); }
    private IntPtr KeyboardProc(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(data);
            if ((key.Flags & 0x10) == 0)
            {
                int vk = (int)key.Code;
                if (((long)message) is 0x100 or 0x104) localPressed.Add(vk); else localPressed.Remove(vk);
                bool ctrl = localPressed.Overlaps([0x11, 0xA2, 0xA3]);
                bool alt = localPressed.Overlaps([0x12, 0xA4, 0xA5]);
                if (vk == 0x7B && ctrl && alt)
                {
                    Unlock(); Emergency?.Invoke();
                    return new IntPtr(1);
                }
                if (KeyboardLocked) return new IntPtr(1);
            }
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }
    private IntPtr MouseProc(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && MouseLocked && (Marshal.PtrToStructure<MouseData>(data).Flags & 1) == 0) return new IntPtr(1);
        return CallNextHookEx(mouseHook, code, message, data);
    }
    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception("현재 화면에 입력할 수 없습니다. Windows 잠금 화면이나 관리자 권한 창을 확인해 주세요.");
    }
    public void Move(int x, int y)
    {
        if (x is < 0 or > 65535 || y is < 0 or > 65535) throw new InvalidDataException("잘못된 좌표입니다.");
        Send(new() { Type = 0, Union = new() { Mouse = new() { X = x, Y = y, Flags = 0x8001 } } });
    }
    public void Button(int button, bool down)
    {
        uint flag = (button, down) switch { (1, true) => 0x2, (1, false) => 0x4, (2, true) => 0x8, (2, false) => 0x10, (3, true) => 0x20, (3, false) => 0x40, _ => throw new InvalidDataException("잘못된 마우스 버튼입니다.") };
        Send(new() { Type = 0, Union = new() { Mouse = new() { Flags = flag } } });
        if (down) remoteButtons.Add(button); else remoteButtons.Remove(button);
    }
    public void Wheel(int delta) => Send(new() { Type = 0, Union = new() { Mouse = new() { Data = unchecked((uint)Math.Clamp(delta, -1200, 1200)), Flags = 0x800 } } });
    public void Key(int vk, bool down)
    {
        if (vk is < 1 or > 255) throw new InvalidDataException("잘못된 키입니다.");
        bool extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5;
        Send(new() { Type = 1, Union = new() { Keyboard = new() { Code = (ushort)vk, Flags = (down ? 0u : 2u) | (extended ? 1u : 0u) } } });
        if (down) remotePressed.Add(vk); else remotePressed.Remove(vk);
    }
    public void Text(string text)
    {
        if (text.Length is < 1 or > 256 || text.Any(char.IsControl)) throw new InvalidDataException("잘못된 입력 문자입니다.");
        var entries = new Input[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            entries[i * 2] = new() { Type = 1, Union = new() { Keyboard = new() { Scan = text[i], Flags = 4 } } };
            entries[i * 2 + 1] = new() { Type = 1, Union = new() { Keyboard = new() { Scan = text[i], Flags = 6 } } };
        }
        if (SendInput((uint)entries.Length, entries, Marshal.SizeOf<Input>()) != entries.Length)
            throw new Win32Exception("현재 화면에 문자를 입력할 수 없습니다.");
    }
    public void ReleaseRemote()
    {
        foreach (int key in remotePressed.ToArray()) { try { Key(key, false); } catch (Win32Exception) { } }
        foreach (int button in remoteButtons.ToArray()) { try { Button(button, false); } catch (Win32Exception) { } }
        remotePressed.Clear(); remoteButtons.Clear();
    }
    public static void LockWindows()
    {
        if (!LockWorkStation()) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() { Unlock(); ReleaseRemote(); if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook); if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook); }
}
