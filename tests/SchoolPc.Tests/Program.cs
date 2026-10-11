using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SchoolPc.Core;
using Channel = SchoolPc.Core.Channel;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected rejection"); }
static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return; } throw new Exception("Expected rejection"); }
int count = 0;
async Task Test(string name, Func<Task> action) { await action(); Console.WriteLine("PASS " + name); count++; }

await Test("packet framing, truncation and size bounds", async () =>
{
    using var memory = new MemoryStream(); using var writer = new Channel(memory);
    await writer.SendAsync(new("message", Text: "한글과 🖥️"), CancellationToken.None); memory.Position = 0;
    var packet = await writer.ReadAsync(CancellationToken.None); Assert(packet.Text == "한글과 🖥️", "UTF8 round trip");
    foreach (int size in new[] { -1, 0, Channel.MaxPacket + 1 })
    {
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, size);
        using var invalid = new Channel(new MemoryStream(prefix)); await RejectAsync(async () => _ = await invalid.ReadAsync(CancellationToken.None));
    }
    var truncated = new byte[5]; BinaryPrimitives.WriteInt32BigEndian(truncated, 30);
    using var shortStream = new Channel(new MemoryStream(truncated)); await RejectAsync(async () => _ = await shortStream.ReadAsync(CancellationToken.None));
});
await Test("concurrent writers do not interleave packets", async () =>
{
    using var memory = new MemoryStream(); using var channel = new Channel(memory);
    await Task.WhenAll(Enumerable.Range(0, 150).Select(i => channel.SendAsync(new("message", Text: i.ToString()), CancellationToken.None)));
    memory.Position = 0; HashSet<string> values = [];
    for (int i = 0; i < 150; i++) values.Add((await channel.ReadAsync(CancellationToken.None)).Text);
    Assert(values.Count == 150, "lost or corrupt packet");
});
await Test("credential comparison and registration validation", () =>
{
    string hash = Security.HashToken(new string('a', 64));
    Assert(Security.EqualHash(hash, hash.ToLowerInvariant()), "case handling");
    Assert(!Security.EqualHash(hash, Security.HashToken(new string('b', 64))), "wrong token allowed");
    Assert(!Security.EqualHash(hash, "invalid"), "invalid hash allowed");
    var profile = new Pairing("교실 PC", "192.168.0.2", 45832, hash, new string('A', 64)); profile.Validate();
    Reject(() => (profile with { Port = 0 }).Validate()); Reject(() => (profile with { Token = "bad" }).Validate());
    return Task.CompletedTask;
});
await Test("file names reject traversal, streams and Windows device names", () =>
{
    foreach (string name in new[] { "../bad", "..\\bad", "C:\\bad", "file:stream", ".", "..", "CON.txt", "LPT1", "COM9.foo", "file.", "bad\0x" }) Reject(() => Security.SafeName(name));
    Assert(Security.SafeName("수업 자료.pdf") == "수업 자료.pdf", "normal name"); return Task.CompletedTask;
});
await Test("file transfer round trip and source stream consistency", async () =>
{
    string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(170001); string input = Path.Combine(root, "source.bin"); File.WriteAllBytes(input, bytes);
        using var memory = new MemoryStream(); using var channel = new Channel(memory); await FileSender.SendAsync(channel, input, CancellationToken.None); memory.Position = 0;
        using var receiver = new FileReceiver(Path.Combine(root, "received"));
        receiver.Begin(await channel.ReadAsync(CancellationToken.None)); string saved = "";
        while (memory.Position < memory.Length)
        {
            var p = await channel.ReadAsync(CancellationToken.None); if (p.Type == "file-chunk") receiver.Chunk(p); else saved = receiver.End(p);
        }
        Assert(File.ReadAllBytes(saved).SequenceEqual(bytes), "corrupted file");
        Assert(!Directory.EnumerateFiles(Path.Combine(root, "received")).Any(p => p.EndsWith(".part")), "left partial file");
    }
    finally { Directory.Delete(root, true); }
});
await Test("selected receive folders and in-progress folder changes", () =>
{
    string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    try
    {
        using var receiver = new FileReceiver(Path.Combine(root, "default"));
        string selected = Path.Combine(root, "selected"); receiver.SetFolder(selected);
        string id = Guid.NewGuid().ToString("N"); byte[] bytes = [1, 2, 3];
        receiver.Begin(new("file-begin", Id: id, Text: "note.bin", Size: bytes.Length, Hash: Security.Hash(bytes)));
        Reject(() => receiver.SetFolder(Path.Combine(root, "other")));
        receiver.Chunk(new("file-chunk", Id: id, Size: 0, Data: bytes));
        string saved = receiver.End(new("file-end", Id: id));
        Assert(Path.GetDirectoryName(saved) == selected && File.ReadAllBytes(saved).SequenceEqual(bytes), "selected folder ignored");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    return Task.CompletedTask;
});
await Test("file receiver rejects oversized, out-of-order and corrupt transfers", () =>
{
    string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); string id = Guid.NewGuid().ToString("N");
    var begin = new Packet("file-begin", id, "a.bin", Size: 3, Hash: Security.Hash([1, 2, 3]));
    try
    {
        using (var receiver = new FileReceiver(folder))
        {
            Reject(() => receiver.Begin(begin with { Size = FileReceiver.MaxFile + 1 }));
            receiver.Begin(begin); Reject(() => receiver.Begin(begin));
            Reject(() => receiver.Chunk(new("file-chunk", id, Size: 1, Data: [1])));
            receiver.Chunk(new("file-chunk", id, Size: 0, Data: [1, 2, 4])); Reject(() => receiver.End(new("file-end", id)));
        }
        Assert(!Directory.EnumerateFiles(folder).Any(), "partial file survived abort");
        using var empty = new FileReceiver(folder); empty.Begin(begin with { Id = Guid.NewGuid().ToString("N"), Size = 0, Hash = Security.Hash([]) });
    }
    finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Test("updater validates repository, version and GitHub digest", () =>
{
    string name = "SchoolPc.Agent.exe";
    object Build(string url, string digest, bool prerelease = false) => new { tag_name = "v1.2.3", draft = false, prerelease, body = "notes", assets = new[] { new { name, browser_download_url = url, digest } } };
    string url = $"https://github.com/{ReleaseInfo.Repository}/releases/download/v1.2.3/{name}";
    string digest = "sha256:" + Security.Hash([1]);
    Assert(ReleaseInfo.Parse(JsonSerializer.Serialize(Build(url, digest)), name).Version == "v1.2.3", "valid release");
    Reject(() => ReleaseInfo.Parse(JsonSerializer.Serialize(Build("https://evil.example/a.exe", digest)), name));
    Reject(() => ReleaseInfo.Parse(JsonSerializer.Serialize(Build(url, "")), name));
    Reject(() => ReleaseInfo.Parse(JsonSerializer.Serialize(Build(url, digest, true)), name));
    Reject(() => ReleaseInfo.ParseVersion("v1.2.3-beta")); return Task.CompletedTask;
});
await Test("real TLS channel with pinned certificate and invalid pin rejection", async () =>
{
    using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    // Windows Schannel requires a persisted private key when acting as TLS server.
    using var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, ""), "", OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet : X509KeyStorageFlags.EphemeralKeySet);
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token); using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, deadline.Token);
            using var channel = new Channel(tls); var p = await channel.ReadAsync(deadline.Token); await channel.SendAsync(p, deadline.Token);
        });
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            using var tls = new SslStream(socket.GetStream(), false, (_, c, _, _) => c != null && Security.EqualHash(Security.Hash(c.GetRawCertData()), Security.Hash(cert.RawData)));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "test" }, deadline.Token);
            using var channel = new Channel(tls); await channel.SendAsync(new("message", Text: "TLS works"), deadline.Token);
            Assert((await channel.ReadAsync(deadline.Token)).Text == "TLS works", "TLS round trip");
        }
        await server;
        var badServer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token); using var tls = new SslStream(socket.GetStream());
            try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, deadline.Token); } catch (AuthenticationException) { } catch (IOException) { }
        });
        using var badClient = new TcpClient(); await badClient.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        using var badTls = new SslStream(badClient.GetStream(), false, (_, _, _, _) => false);
        bool rejected = false; try { await badTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "test" }, deadline.Token); } catch (AuthenticationException) { rejected = true; }
        Assert(rejected, "invalid certificate accepted"); badClient.Dispose(); await badServer;
    }
    finally { listener.Stop(); }
});
await Test("Wake-on-LAN magic packet, MAC validation, broadcast and legacy registration", () =>
{
    var packet = WakeOnLan.CreatePacket("02-11-22-33-44-55");
    Assert(packet.Length == 102 && packet.Take(6).All(x => x == 255), "magic packet header");
    byte[] mac = [2, 0x11, 0x22, 0x33, 0x44, 0x55];
    for (int i = 0; i < 16; i++) Assert(packet.Skip(6 + i * 6).Take(6).SequenceEqual(mac), "MAC repetition");
    foreach (string value in new[] { "bad", "00-00-00-00-00-00", "FF-FF-FF-FF-FF-FF", "01-11-22-33-44-55" }) Reject(() => WakeOnLan.CreatePacket(value));
    Assert(WakeOnLan.Broadcast(IPAddress.Parse("192.168.5.42"), IPAddress.Parse("255.255.255.0")).ToString() == "192.168.5.255", "subnet broadcast");
    var legacy = new Pairing("PC", "192.168.5.42", 45832, new string('A', 64), new string('B', 64));
    using var oldJson = JsonDocument.Parse(JsonSerializer.Serialize(legacy));
    var withoutMac = JsonSerializer.Serialize(oldJson.RootElement.EnumerateObject().Where(p => p.Name != "Mac").ToDictionary(p => p.Name, p => p.Value));
    var restored = JsonSerializer.Deserialize<Pairing>(withoutMac)!; restored.Validate(); Assert(restored.Mac == "", "legacy registration compatibility");
    return Task.CompletedTask;
});
Console.WriteLine($"All {count} tests passed.");
