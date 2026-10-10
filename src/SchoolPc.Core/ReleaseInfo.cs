using System.Text.Json;

namespace SchoolPc.Core;

public sealed record ReleaseAsset(string Version, string Url, string Hash, string Notes, string Name);
public static class ReleaseInfo
{
    public const string Repository = "pullgena/school-pc-manager";
    public static Version ParseVersion(string text)
    {
        if (!Version.TryParse(text.TrimStart('v'), out var version) || version.Build < 0 || version.Major < 0)
            throw new InvalidDataException("릴리스 버전은 v1.2.3 형식이어야 합니다.");
        return version;
    }
    public static ReleaseAsset Parse(string json, string name)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("정식 릴리스가 아닙니다.");
        string version = root.GetProperty("tag_name").GetString()!; _ = ParseVersion(version);
        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString() == name);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("이 프로그램의 업데이트 파일이 아직 없습니다.");
        string url = asset.GetProperty("browser_download_url").GetString()!;
        string prefix = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(version)}/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal) || !url.EndsWith('/' + name, StringComparison.Ordinal))
            throw new InvalidDataException("업데이트 다운로드 주소가 올바르지 않습니다.");
        string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (!digest.StartsWith("sha256:") || !Security.ValidHash(digest[7..]))
            throw new InvalidDataException("GitHub SHA-256 검증 정보가 없습니다. 릴리스 파일 업로드 완료 후 다시 시도해 주세요.");
        return new(version, url, digest[7..], root.GetProperty("body").GetString() ?? "", name);
    }
}
