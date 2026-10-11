using System.Security.Cryptography;

namespace SchoolPc.Core;

/// <summary>One bounded transfer per connection; never writes an arbitrary remote path.</summary>
public sealed class FileReceiver(string folder) : IDisposable
{
    private string receiveFolder = folder;
    public void SetFolder(string folder)
    {
        if (output != null) throw new InvalidDataException("전송 중에는 저장 위치를 변경할 수 없습니다.");
        receiveFolder = Path.GetFullPath(folder);
    }
    public const long MaxFile = 256L * 1024 * 1024;
    public const int ChunkSize = 64 * 1024;
    private FileStream? output;
    private IncrementalHash? hash;
    private string id = "", expectedHash = "", temporary = "", target = "";
    private long expected, received;
    public string Begin(Packet p)
    {
        if (output != null) throw new InvalidDataException("이미 파일을 받는 중입니다.");
        string name = Security.SafeName(p.Text);
        if (p.Size < 0 || p.Size > MaxFile || !Security.ValidHash(p.Hash) || !Guid.TryParseExact(p.Id, "N", out _))
            throw new InvalidDataException("파일 크기 또는 해시가 잘못되었습니다.");
        Directory.CreateDirectory(receiveFolder);
        id = p.Id; expected = p.Size; received = 0; expectedHash = p.Hash;
        target = Path.Combine(receiveFolder, $"{Path.GetFileNameWithoutExtension(name)}-{id[..8]}{Path.GetExtension(name)}");
        temporary = Path.Combine(receiveFolder, $".{id}.part");
        output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        return id;
    }
    public void Chunk(Packet p)
    {
        if (output == null || p.Id != id || p.Size != received || p.Data == null || p.Data.Length == 0 ||
            p.Data.Length > ChunkSize || received + p.Data.Length > expected)
            throw new InvalidDataException("파일 조각 순서 또는 크기가 잘못되었습니다.");
        output.Write(p.Data); hash!.AppendData(p.Data); received += p.Data.Length;
    }
    public string End(Packet p)
    {
        if (output == null || p.Id != id || received != expected) throw new InvalidDataException("파일 전송이 완료되지 않았습니다.");
        string actual = Convert.ToHexString(hash!.GetHashAndReset());
        if (!Security.EqualHash(actual, expectedHash)) throw new InvalidDataException("파일 해시가 일치하지 않습니다.");
        output.Flush(true); output.Dispose(); output = null; hash.Dispose(); hash = null;
        File.Move(temporary, target, false); temporary = "";
        return target;
    }
    public void Dispose()
    {
        output?.Dispose(); output = null; hash?.Dispose(); hash = null;
        if (temporary.Length > 0) { try { File.Delete(temporary); } catch (IOException) { } }
        temporary = "";
    }
}

public static class FileSender
{
    public static async Task SendAsync(Channel channel, string path, CancellationToken ct, string requestId = "", string? transferId = null)
    {
        // Keep the same open stream through hashing and upload so content cannot change between them.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileReceiver.ChunkSize, true);
        if (input.Length > FileReceiver.MaxFile) throw new InvalidDataException("파일은 최대 256MB까지 전송할 수 있습니다.");
        string id = transferId ?? Guid.NewGuid().ToString("N");
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)); input.Position = 0;
        await channel.SendAsync(new("file-begin", id, Security.SafeName(Path.GetFileName(path)), Size: input.Length, Hash: hash, Request: requestId), ct);
        byte[] bytes = new byte[FileReceiver.ChunkSize]; long offset = 0;
        int count;
        while ((count = await input.ReadAsync(bytes, ct)) > 0)
        {
            await channel.SendAsync(new("file-chunk", id, Size: offset, Data: bytes[..count]), ct);
            offset += count;
        }
        await channel.SendAsync(new("file-end", id), ct);
    }
}
