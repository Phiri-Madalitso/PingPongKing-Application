namespace PingPong.Api.Domain;

public static class Elo
{
    public const int StartRating = 1000;
    public const int K = 32;

    /// <summary>Forventet score (0..1) for spiller A mot B.</summary>
    public static double Expected(int ratingA, int ratingB) =>
        1.0 / (1.0 + Math.Pow(10, (ratingB - ratingA) / 400.0));

    public static (int winnerNew, int loserNew) Update(int winner, int loser)
    {
        var delta = (int)Math.Round(K * (1 - Expected(winner, loser)));
        return (winner + delta, loser - delta);
    }
}
