using Microsoft.EntityFrameworkCore;
using PingPong.Api.Domain;

namespace PingPong.Api.Services;

public record RecordResult(Match Match, int WinnerRating, int LoserRating);

public static class MatchService
{
    /// <summary>Lagrer kampen og oppdaterer ELO + RatingHistory. Forutsetter validert input.</summary>
    public static async Task<RecordResult> RecordAsync(AppDb db, Player p1, Player p2, int winnerId,
        int? score1, int? score2, DateTime playedAt)
    {
        var match = new Match
        {
            Player1Id = p1.Id, Player2Id = p2.Id, WinnerId = winnerId,
            Player1Score = score1, Player2Score = score2, PlayedAt = playedAt
        };
        var winner = winnerId == p1.Id ? p1 : p2;
        var loser = winner == p1 ? p2 : p1;
        (winner.Rating, loser.Rating) = Elo.Update(winner.Rating, loser.Rating);

        db.Matches.Add(match);
        await db.SaveChangesAsync(); // gir match.Id
        db.RatingHistory.AddRange(
            new RatingHistory { PlayerId = winner.Id, MatchId = match.Id, RatingAfter = winner.Rating, RecordedAt = playedAt },
            new RatingHistory { PlayerId = loser.Id, MatchId = match.Id, RatingAfter = loser.Rating, RecordedAt = playedAt });
        await db.SaveChangesAsync();
        return new RecordResult(match, winner.Rating, loser.Rating);
    }

    /// <summary>Angrer den sist registrerte kampen og setter ratingen tilbake.</summary>
    public static async Task<Match?> UndoLastAsync(AppDb db)
    {
        var match = await db.Matches.OrderByDescending(m => m.Id).FirstOrDefaultAsync();
        if (match is null) return null;

        foreach (var pid in new[] { match.Player1Id, match.Player2Id })
        {
            var previous = await db.RatingHistory
                .Where(r => r.PlayerId == pid && r.MatchId != match.Id)
                .OrderByDescending(r => r.Id).Select(r => (int?)r.RatingAfter).FirstOrDefaultAsync();
            (await db.Players.FindAsync(pid))!.Rating = previous ?? Elo.StartRating;
        }
        db.RatingHistory.RemoveRange(db.RatingHistory.Where(r => r.MatchId == match.Id));
        db.Matches.Remove(match);
        await db.SaveChangesAsync();
        return match;
    }
}
