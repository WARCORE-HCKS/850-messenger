namespace Area850.Net;

public sealed class Member
{
    public required string Id { get; init; }
    public required string Callsign { get; set; }
    public required string Net { get; set; }
    public bool Mic { get; set; } = true;
    public bool Cam { get; set; }
    public bool Talking { get; set; }
}

public sealed class ChatLine
{
    public required string FromId { get; init; }
    public required string From { get; init; }
    public required string Text { get; init; }
    public required string Kind { get; init; } // say, join, leave, whisper, system
    public string? To { get; init; }
    public DateTime At { get; init; } = DateTime.UtcNow;
}
