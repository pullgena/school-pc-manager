using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchoolPc.Core;

public sealed record Packet(string Type, string Id = "", string Text = "", int X = 0, int Y = 0,
    int Value = 0, bool Enabled = false, long Size = 0, string Hash = "", byte[]? Data = null);

/// <summary>Length-prefixed TLS payloads. Exactly one reader; concurrent writers are serialized.</summary>
public sealed class Channel(Stream stream) : IDisposable
{
    public const int MaxPacket = 8 * 1024 * 1024;
    private readonly SemaphoreSlim sendLock = new(1);
    public async Task SendAsync(Packet packet, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (body.Length > MaxPacket) throw new InvalidDataException("패킷이 너무 큽니다.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await sendLock.WaitAsync(deadline.Token);
        try
        {
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(prefix, body.Length);
            await stream.WriteAsync(prefix, deadline.Token);
            await stream.WriteAsync(body, deadline.Token);
            await stream.FlushAsync(deadline.Token);
        }
        finally { sendLock.Release(); }
    }
    public async Task<Packet> ReadAsync(CancellationToken ct)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        int size = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (size <= 0 || size > MaxPacket) throw new InvalidDataException("잘못된 패킷 크기입니다.");
        byte[] body = new byte[size];
        await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize<Packet>(body) ?? throw new InvalidDataException("빈 패킷입니다.");
    }
    public void Dispose() => stream.Dispose();
}

public sealed record Pairing(string Name, string Host, int Port, string Fingerprint, string Token, string Mac = "")
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Port is < 1024 or > 65535)
            throw new InvalidDataException("이름, 주소 또는 포트를 확인해 주세요.");
        if (!Security.ValidHash(Fingerprint) || Token.Length != 64 || !Token.All(Uri.IsHexDigit))
            throw new InvalidDataException("올바른 등록 파일이 아닙니다.");
        if (!string.IsNullOrWhiteSpace(Mac)) _ = WakeOnLan.ParseMac(Mac);
    }
}

public static class Security
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string HashToken(string token) => Hash(Encoding.UTF8.GetBytes(token));
    public static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    public static bool EqualHash(string a, string b) => ValidHash(a) && ValidHash(b) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b));
    public static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name != Path.GetFileName(name) ||
            name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.Any(char.IsControl))
            throw new InvalidDataException("허용되지 않는 파일 이름입니다.");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9'))
            throw new InvalidDataException("Windows 예약 파일 이름입니다.");
        return name;
    }
}
