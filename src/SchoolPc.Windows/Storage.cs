using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchoolPc.Windows;

public static class Storage
{
    internal static string? TestRoot { get; set; }
    public static string Root => TestRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchoolPcManager");
    public static string PathFor(string name) { Directory.CreateDirectory(Root); return Path.Combine(Root, name); }
    public static void Save<T>(string name, T value)
    {
        byte[] encrypted = UserSecret.Protect(JsonSerializer.SerializeToUtf8Bytes(value));
        string path = PathFor(name), temp = path + ".new";
        File.WriteAllBytes(temp, encrypted); File.Move(temp, path, true);
    }
    public static T? Load<T>(string name) => File.Exists(PathFor(name))
        ? JsonSerializer.Deserialize<T>(UserSecret.Unprotect(File.ReadAllBytes(PathFor(name)))) : default;
    public static void Audit(string role, string text)
    {
        // No keystrokes, screen contents, filenames, credentials, or clipboard contents are logged.
        try { File.AppendAllText(PathFor($"{role}-audit.log"), $"{DateTimeOffset.Now:O} {text.Replace('\n', ' ').Replace('\r', ' ')}\n", Encoding.UTF8); }
        catch (IOException) { }
    }
}

public static class UserSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        Blob input = new() { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "SchoolPcManager", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
}
