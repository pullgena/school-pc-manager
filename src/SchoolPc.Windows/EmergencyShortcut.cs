namespace SchoolPc.Windows;

public sealed record EmergencyShortcut(int Key = (int)Keys.F12, bool Control = true, bool Alt = true, bool Shift = false)
{
    public static readonly EmergencyShortcut Default = new();
    public static readonly Keys[] AllowedKeys = Enumerable.Range((int)Keys.F1, 24)
        .Concat(Enumerable.Range((int)Keys.A, 26)).Concat(Enumerable.Range((int)Keys.D0, 10))
        .Select(k => (Keys)k).ToArray();

    public bool Valid => AllowedKeys.Contains((Keys)Key) && (Control ? 1 : 0) + (Alt ? 1 : 0) + (Shift ? 1 : 0) >= 2;
    public bool Matches(int key, bool control, bool alt, bool shift) =>
        Valid && Key == key && Control == control && Alt == alt && Shift == shift;
    public override string ToString() => string.Join("+", new[] { Control ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, ((Keys)Key).ToString() }.Where(s => s != null));
}
