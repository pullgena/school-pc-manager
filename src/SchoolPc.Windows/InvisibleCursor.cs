namespace SchoolPc.Windows;

public static class InvisibleCursor
{
    public static Cursor Create()
    {
        // A standard 32-bit CUR with transparent pixels and an entirely transparent AND mask.
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)1);
        writer.Write((byte)32); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(4264); writer.Write(22);
        writer.Write(40); writer.Write(32); writer.Write(64); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(4224); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(new byte[4096]); writer.Write(Enumerable.Repeat((byte)255, 128).ToArray());
        data.Position = 0; return new Cursor(data);
    }
}
