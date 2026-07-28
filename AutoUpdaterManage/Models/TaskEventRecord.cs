namespace AutoUpdaterManage.Models;

public sealed record TaskEventRecord(
    long Id,
    Guid RequestId,
    string Stage,
    int? Percentage,
    string Message,
    string? Detail,
    DateTime OccurredAt)
{
    public string TimeText => OccurredAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string PercentageText => Percentage.HasValue
        ? $"{Percentage.Value}%"
        : "—";
    public string DetailText => string.IsNullOrWhiteSpace(Detail)
        ? Message
        : $"{Message} — {Detail}";
}
