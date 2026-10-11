using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SchoolPc.Windows;

public static class DesktopCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Position { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Cursor; public Position Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public bool Icon; public uint XHotspot, YHotspot; public IntPtr Mask, Color; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    public static byte[] Jpeg(int maxWidth = 1280, int quality = 55, bool blank = false)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        using var original = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(original))
        {
            if (blank) graphics.Clear(Color.Black);
            else
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
                if (GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0 && GetIconInfo(cursor.Cursor, out var icon))
                {
                    try
                    {
                        IntPtr dc = graphics.GetHdc();
                        try { DrawIconEx(dc, cursor.Position.X - bounds.X - (int)icon.XHotspot, cursor.Position.Y - bounds.Y - (int)icon.YHotspot, cursor.Cursor, 0, 0, 0, IntPtr.Zero, 3); }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    finally { if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color); }
                }
            }
        }
        double ratio = Math.Min(1, Math.Clamp(maxWidth, 640, 1920) / (double)bounds.Width);
        using var scaled = new Bitmap(original, new Size(Math.Max(1, (int)(bounds.Width * ratio)), Math.Max(1, (int)(bounds.Height * ratio))));
        using var bytes = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 30, 85));
        scaled.Save(bytes, encoder, parameters); return bytes.ToArray();
    }
}
