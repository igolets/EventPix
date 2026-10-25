# EventPix

[![Quality gate status](https://sonarcloud.io/api/project_badges/measure?project=igolets_EventPix&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=igolets_EventPix)
[![.NET](https://github.com/igolets/EventPix/actions/workflows/dotnet.yml/badge.svg)](https://github.com/igolets/EventPix/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/igolets/EventPix/actions/workflows/github-code-scanning/codeql/badge.svg)](https://github.com/igolets/EventPix/actions/workflows/github-code-scanning/codeql)

Self-hosted, private photo & video sharing for one-off events (weddings, graduations, anniversaries).
Share a hidden link, guests sign in with an emailed code, browse tabs, upload to their own tab,
star favourites and download files, tabs, the whole library, or just the starred ones as a ZIP.

* .NET 10, SQLite, no cloud dependencies. Runs standalone (Windows/Linux) or in Docker.
* The UI theme is a plain folder of HTML templates + CSS. Change the look without touching C#.
* MIT licensed.

> Status: **scaffold**. See `AGENTS.md` for the roadmap and `docs/architecture.md` for the design.

## Quick start

```bash
dotnet run --project src/EventPix.Web -- create-event "Anna & Tom" owner@example.com
# prints the secret event URL; then start the server:
dotnet run --project src/EventPix.Web
```

Without an `Smtp:Host`, sign-in codes are written to the console (Development only).

## Docker

Prebuilt image (amd64/arm64, published by CI):

```bash
docker run -d -p 127.0.0.1:8080:8080 -v eventpix-data:/data -v eventpix-media:/media ghcr.io/igolets/eventpix:latest
```


```bash
docker compose up -d
docker compose exec eventpix dotnet EventPix.Web.dll create-event "Anna & Tom" owner@example.com
```

## Theming

Copy `themes/default` (or the dark `themes/mono`) to `themes/my-theme`, edit, set `EventPix:ActiveTheme`. See `docs/architecture.md#theming-contract`.
