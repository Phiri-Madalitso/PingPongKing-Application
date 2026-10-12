using Microsoft.EntityFrameworkCore;
using PingPong.Api.Domain;
using PingPong.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
// Bytt til .UseSqlServer(...) for SQL Server (legg til pakken Microsoft.EntityFrameworkCore.SqlServer).
builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=pingpong.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// ---------- Spillere ----------
api.MapGet("/players", async (AppDb db) =>
    await db.Players.OrderByDescending(p => p.Rating).Select(p => new { p.Id, p.Name, p.Rating }).ToListAsync());

api.MapPost("/players", async (PlayerIn input, AppDb db) =>
{
    var name = input.Name?.Trim();
    if (string.IsNullOrEmpty(name) || name.Length > 50) return Results.BadRequest("Navn er påkrevd (maks 50 tegn).");
    if (await db.Players.AnyAsync(p => p.Name == name)) return Results.Conflict("Spilleren finnes allerede.");
    var p = new Player { Name = name, Email = input.Email?.Trim() };
    db.Players.Add(p);
    await db.SaveChangesAsync();
    return Results.Created($"/api/players/{p.Id}", new { p.Id, p.Name, p.Rating });
});

// ---------- Kamper ----------
api.MapGet("/matches", async (AppDb db) =>
    await db.Matches.OrderByDescending(m => m.PlayedAt).Take(100).Select(m => new
    {
        m.Id, m.PlayedAt,
        Player1 = m.Player1!.Name, Player2 = m.Player2!.Name, Winner = m.Winner!.Name,
        m.Player1Score, m.Player2Score
    }).ToListAsync());

api.MapPost("/matches", async (MatchIn input, AppDb db) =>
{
    if (input.Player1Id == input.Player2Id) return Results.BadRequest("En kamp trenger to ulike spillere.");
    if (input.WinnerId != input.Player1Id && input.WinnerId != input.Player2Id)
        return Results.BadRequest("Vinneren må være en av de to spillerne.");
    if (input.Player1Score is < 0 || input.Player2Score is < 0)
        return Results.BadRequest("Poeng kan ikke være negative.");

    var p1 = await db.Players.FindAsync(input.Player1Id);
    var p2 = await db.Players.FindAsync(input.Player2Id);
    if (p1 is null || p2 is null) return Results.NotFound("Ukjent spiller.");

    var playedAt = (input.PlayedAt ?? DateTime.UtcNow).ToUniversalTime();
    var match = new Match
    {
        Player1Id = p1.Id, Player2Id = p2.Id, WinnerId = input.WinnerId,
        Player1Score = input.Player1Score, Player2Score = input.Player2Score, PlayedAt = playedAt
    };

    var winner = input.WinnerId == p1.Id ? p1 : p2;
    var loser = winner == p1 ? p2 : p1;
    (winner.Rating, loser.Rating) = Elo.Update(winner.Rating, loser.Rating);

    db.Matches.Add(match);
    await db.SaveChangesAsync(); // gir match.Id
    db.RatingHistory.AddRange(
        new RatingHistory { PlayerId = winner.Id, MatchId = match.Id, RatingAfter = winner.Rating, RecordedAt = playedAt },
        new RatingHistory { PlayerId = loser.Id, MatchId = match.Id, RatingAfter = loser.Rating, RecordedAt = playedAt });
    await db.SaveChangesAsync();

    return Results.Created($"/api/matches/{match.Id}", new { match.Id, WinnerRating = winner.Rating, LoserRating = loser.Rating });
});

// ---------- Innbyrdes oppgjør + sannsynlighet ----------
api.MapGet("/head-to-head", async (int playerA, int playerB, int? year, AppDb db) =>
{
    var a = await db.Players.FindAsync(playerA);
    var b = await db.Players.FindAsync(playerB);
    if (a is null || b is null || a.Id == b.Id) return Results.BadRequest("Velg to ulike, eksisterende spillere.");

    var q = db.Matches.Where(m =>
        (m.Player1Id == a.Id && m.Player2Id == b.Id) || (m.Player1Id == b.Id && m.Player2Id == a.Id));
    if (year is int y)
    {
        var from = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddYears(1);
        q = q.Where(m => m.PlayedAt >= from && m.PlayedAt < to);
    }

    var games = await q.OrderBy(m => m.PlayedAt).ThenBy(m => m.Id).ToListAsync();
    return Results.Ok(HeadToHead.Compute(a, b, games));
});

app.Run();

record PlayerIn(string? Name, string? Email);
record MatchIn(int Player1Id, int Player2Id, int WinnerId, int? Player1Score, int? Player2Score, DateTime? PlayedAt);
