using PingPong.Api.Domain;

namespace PingPong.Api.Services;

public record H2HGame(int MatchId, DateTime PlayedAt, int WinnerId, string WinnerName, string? Score);

public record H2HResult(
    int PlayerAId, string PlayerAName, int PlayerBId, string PlayerBName,
    int GameCount, int WinsA, int WinsB,
    int StreakPlayerId, int StreakLength,
    double EloProbabilityA, double HistoryProbabilityA, double WinProbabilityA, double WinProbabilityB,
    IReadOnlyList<H2HGame> Games);

public static class HeadToHead
{
    /// <summary>
    /// Sannsynlighet = vektet snitt av ELO-forventning og glattet innbyrdes seiersrate.
    /// Jo flere innbyrdes kamper, jo mer teller historikken (n / (n + 5)).
    /// Laplace-glatting ((w+1)/(n+2)) hindrer 0 % / 100 % etter få kamper.
    /// </summary>
    public static H2HResult Compute(Player a, Player b, IReadOnlyList<Match> gamesOldestFirst)
    {
        int n = gamesOldestFirst.Count;
        int winsA = gamesOldestFirst.Count(m => m.WinnerId == a.Id);
        int winsB = n - winsA;

        double elo = Elo.Expected(a.Rating, b.Rating);
        double hist = (winsA + 1.0) / (n + 2.0);
        double weight = n / (n + 5.0);
        double pA = weight * hist + (1 - weight) * elo;

        int streakLen = 0, streakPlayer = 0;
        if (n > 0)
        {
            streakPlayer = gamesOldestFirst[^1].WinnerId;
            for (int i = n - 1; i >= 0 && gamesOldestFirst[i].WinnerId == streakPlayer; i--) streakLen++;
        }

        var games = gamesOldestFirst.Select(m => new H2HGame(
            m.Id, m.PlayedAt, m.WinnerId, m.WinnerId == a.Id ? a.Name : b.Name,
            m.Player1Score is null ? null :
                m.Player1Id == a.Id ? $"{m.Player1Score}-{m.Player2Score}" : $"{m.Player2Score}-{m.Player1Score}"
        )).Reverse().ToList();

        return new H2HResult(a.Id, a.Name, b.Id, b.Name, n, winsA, winsB, streakPlayer, streakLen,
            Math.Round(elo, 4), Math.Round(hist, 4), Math.Round(pA, 4), Math.Round(1 - pA, 4), games);
    }
}
