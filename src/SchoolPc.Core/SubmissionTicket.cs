namespace SchoolPc.Core;

/// <summary>Admission and count limits for one explicitly requested submission.</summary>
public sealed class SubmissionTicket
{
    public string Id { get; }
    public string Title { get; }
    public int MaxFiles { get; }
    public long Deadline { get; }
    public int Expected { get; private set; }
    public int Completed { get; private set; }
    public bool Cancelled { get; private set; }
    public bool Finished => Expected > 0 && Completed == Expected;
    public SubmissionTicket(string id, string title, int seconds, int maxFiles, long now)
    {
        if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(title) || title.Length > 100 || seconds is < 1 or > 10800 || maxFiles is < 1 or > 50)
            throw new InvalidDataException("과제 이름, 제한 시간(최대 180분), 파일 개수(1~50개)를 확인해 주세요.");
        Id = id; Title = title; MaxFiles = maxFiles; Deadline = now + seconds * 1000L;
    }
    public void Start(int files, long now)
    {
        if (Cancelled || Expected != 0 || now >= Deadline || files < 1 || files > MaxFiles) throw new InvalidDataException("제출 시간이 끝났거나 제출 파일 개수가 잘못되었습니다.");
        Expected = files;
    }
    public void CheckFile()
    {
        if (Cancelled || Expected == 0 || Completed >= Expected) throw new InvalidDataException("현재 과제에 더 이상 파일을 제출할 수 없습니다.");
    }
    public void CompleteFile() { CheckFile(); Completed++; }
    public void Cancel() => Cancelled = true;
}
