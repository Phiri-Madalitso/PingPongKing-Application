namespace PingPong.Api.Domain;

public class Player
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public int Rating { get; set; } = Elo.StartRating;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Match
{
    public int Id { get; set; }
    public int Player1Id { get; set; }
    public int Player2Id { get; set; }
    public int WinnerId { get; set; }
    public int? Player1Score { get; set; }
    public int? Player2Score { get; set; }
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;

    public Player? Player1 { get; set; }
    public Player? Player2 { get; set; }
    public Player? Winner { get; set; }
}

public class RatingHistory
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public int MatchId { get; set; }
    public int RatingAfter { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>En spiller i køen. De to første i JoinedAt-rekkefølge spiller nå.</summary>
public class QueueEntry
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public Player? Player { get; set; }
}
