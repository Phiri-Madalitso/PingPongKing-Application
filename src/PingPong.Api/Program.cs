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
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    // Midlertidig til vi tar i bruk EF-migrasjoner: EnsureCreated legger ikke til nye tabeller i en eksisterende DB.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Queue" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Queue" PRIMARY KEY AUTOINCREMENT,
            "PlayerId" INTEGER NOT NULL,
            "JoinedAt" TEXT NOT NULL,
            CONSTRAINT "FK_Queue_Players_PlayerId" FOREIGN KEY ("PlayerId") REFERENCES "Players" ("Id") ON DELETE CASCADE);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Queue_PlayerId" ON "Queue" ("PlayerId");
        """);
}

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
    var r = await MatchService.RecordAsync(db, p1, p2, input.WinnerId, input.Player1Score, input.Player2Score, playedAt);
    return Results.Created($"/api/matches/{r.Match.Id}", new { r.Match.Id, r.WinnerRating, r.LoserRating });
});

api.MapDelete("/matches/last", async (AppDb db) =>
{
    var undone = await MatchService.UndoLastAsync(db);
    return undone is null ? Results.NotFound("Ingen kamper å angre.") : Results.NoContent();
});

// ---------- Kø: de to første spiller, taperen går bakerst, vinneren blir stående ----------
api.MapGet("/queue", async (AppDb db) =>
    await db.Queue.OrderBy(q => q.JoinedAt).ThenBy(q => q.Id)
        .Select(q => new { q.PlayerId, q.Player!.Name, q.Player.Rating, q.JoinedAt }).ToListAsync());

api.MapPost("/queue", async (QueueIn input, AppDb db) =>
{
    if (await db.Players.FindAsync(input.PlayerId) is null) return Results.NotFound("Ukjent spiller.");
    if (await db.Queue.AnyAsync(q => q.PlayerId == input.PlayerId)) return Results.Conflict("Du står allerede i køen.");
    db.Queue.Add(new QueueEntry { PlayerId = input.PlayerId });
    await db.SaveChangesAsync();
    return Results.NoContent();
});

api.MapDelete("/queue/{playerId:int}", async (int playerId, AppDb db) =>
{
    var e = await db.Queue.FirstOrDefaultAsync(q => q.PlayerId == playerId);
    if (e is null) return Results.NotFound();
    db.Queue.Remove(e);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

api.MapPost("/queue/result", async (QueueResultIn input, AppDb db) =>
{
    var front = await db.Queue.OrderBy(q => q.JoinedAt).ThenBy(q => q.Id).Take(2).ToListAsync();
    if (front.Count < 2) return Results.BadRequest("Det må stå to spillere i køen for å spille.");
    var winnerEntry = front.FirstOrDefault(q => q.PlayerId == input.WinnerId);
    if (winnerEntry is null) return Results.BadRequest("Vinneren spiller ikke akkurat nå.");
    var loserEntry = front.First(q => q != winnerEntry);

    var p1 = (await db.Players.FindAsync(front[0].PlayerId))!;
    var p2 = (await db.Players.FindAsync(front[1].PlayerId))!;
    if (input.WinnerScore is < 0 || input.LoserScore is < 0) return Results.BadRequest("Poeng kan ikke være negative.");
    // Scorene (antall games) kommer per rolle; kobles til riktig spiller her.
    var p1Wins = p1.Id == input.WinnerId;
    int? s1 = p1Wins ? input.WinnerScore : input.LoserScore;
    int? s2 = p1Wins ? input.LoserScore : input.WinnerScore;
    var r = await MatchService.RecordAsync(db, p1, p2, input.WinnerId, s1, s2, DateTime.UtcNow);

    // Taperen går bakerst: ny rad med nytt tidspunkt. Vinneren beholder plassen sin.
    db.Queue.Remove(loserEntry);
    db.Queue.Add(new QueueEntry { PlayerId = loserEntry.PlayerId });
    await db.SaveChangesAsync();
    return Results.Ok(new { r.WinnerRating, r.LoserRating });
});

// ---------- Adresser andre kan bruke fra mobil på samme nett ----------
api.MapGet("/info", (HttpRequest req) =>
{
    var port = req.Host.Port ?? 80;
    var urls = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Select(a => $"http://{a.Address}:{port}")
        .ToList();
    return new { urls };
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
record QueueIn(int PlayerId);
record QueueResultIn(int WinnerId, int? WinnerScore = null, int? LoserScore = null);
record MatchIn(int Player1Id, int Player2Id, int WinnerId, int? Player1Score, int? Player2Score, DateTime? PlayedAt);
