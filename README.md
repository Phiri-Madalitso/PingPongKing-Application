# PingPongKing-Application
Application to see history of ping pong games and the current scoreboard between players.

## Kjør

```
cd src/PingPong.Api
dotnet run
```
Åpne URL-en som skrives ut (f.eks. http://localhost:5026). Databasen (`pingpong.db`, SQLite) opprettes automatisk.

## MVP
- Spillere med ELO-rating (start 1000, K=32) og leaderboard
- Registrer kamper (valgfri dato og stilling) – rating og `RatingHistory` oppdateres
- Innbyrdes oppgjør med datoer, seiersrekke, årsfilter (`/api/head-to-head?playerA=1&playerB=2&year=2026`)
- Vinnersannsynlighet = vektet snitt av ELO og glattet innbyrdes seiersrate

## Neste steg
Kø, turneringer (Tournament/TournamentEntry), JWT + roller, SQL Server.
