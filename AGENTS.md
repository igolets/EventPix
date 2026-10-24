# AGENTS.md

Instructions for AI coding agents (and humans) working on **EventPix**: a self-hosted, private photo/video sharing app for events.
Read `docs/architecture.md` first; it is the source of truth for design. If code and doc disagree, fix one in the same change.

## Stack and layout

* .NET 10, ASP.NET Core Minimal APIs, EF Core + SQLite, vanilla JS (ES modules, no build step), MIT licence.
* `src/EventPix.Web/`: `Domain` (entities) · `Data` (DbContext) · `Security` (tokens, OTP, event scope) · `Storage` (blob abstraction)
  · `Media` (sniffing, thumbnails, worker) · `Notifications` (channels, worker) · `Features` (one file per endpoint group)
  · `Cli` (operator commands) · `wwwroot/app` (core JS, **no markup, no styling**).
* `themes/<name>/`: all HTML templates and CSS. `tests/EventPix.Tests/`: xUnit.

## Commands

```bash
dotnet restore
dotnet build
dotnet test
dotnet format EventPix.slnx --verify-no-changes
dotnet run --project src/EventPix.Web -- create-event "Test" me@example.com   # prints the secret URL
dotnet run --project src/EventPix.Web                                          # dev server; OTP codes are logged to console
docker compose build
```
`Directory.Build.props` bakes in strict analysis (`AnalysisLevel latest-all`, `EnforceCodeStyleInBuild`,
`TreatWarningsAsErrors`, low-severity NuGet audit), so a plain `dotnet build` is already warnings-as-errors.
`.editorconfig` holds the style/naming contract; `dotnet format EventPix.slnx` auto-fixes violations.
Run `dotnet build`, `dotnet test` and the format verification before declaring any task done.

## Non-negotiable invariants

Security (a violation is a bug even if tests pass):
1. Never put user-controlled strings in file-system paths. Storage keys are server-generated (`{eventId:N}/{mediaId:N}/...`). Go through `IMediaStorage`.
2. Media is served only through authorised endpoints that re-check `event.Id` ownership. Never add a static file provider for the media folder.
3. Every endpoint under `/api/e/{token}` goes through `WithEventScope`. Queries must filter by `EventId` from the scope, never by a client-supplied event id.
4. Determine file type from content (`FileSniffer`), never from the client Content-Type or extension. Do not accept SVG/HTML.
5. Secrets (OTP codes, tokens, SMTP password, pepper) are never logged or returned by the API. OTPs are stored only as HMAC.
6. Responses must not reveal whether an e-mail, event or media id exists (404/202 uniformity).
7. State-changing API calls require the `X-Requested-With: eventpix` header. Keep the CSP strict (`'self'` only). No inline scripts/styles, no CDNs.
8. Spawn external processes only with `ProcessStartInfo.ArgumentList`, never a shell string.
9. Rate-limit anything that sends mail or checks a secret.

Architecture:
10. **Design is detached from logic.** No HTML strings, class names for looks, colours or fonts in C# or `wwwroot/app/app.js`.
    Core JS may use only `data-tpl` / `data-ref` hooks, `data-*` state attributes and CSS custom properties. New UI = new template + CSS in `themes/default`,
    and document new hooks in `docs/architecture.md` (Theming contract). Always render data with `textContent`.
11. Media bytes, DB and code live in separate locations configured via `EventPix:StoragePath`, `EventPix:DataPath`, `EventPix:ThemesPath`.
12. Large files are streamed (upload to storage, ZIP to response). Never buffer a whole video or ZIP in memory.
13. Works on Windows and Linux: use `Path.Combine`, no hard-coded separators, no `/tmp`; use `Path.GetTempPath()`.
14. New notification channel = implement `INotificationChannel` + register. Do not special-case channels in the worker.

## Dependencies

* Only MIT / Apache-2.0 / BSD packages. Check the licence before adding and record it in the PR. No GPL/AGPL/SSPL, **no ImageSharp** (split licence).
* ffmpeg is an external executable, never a bundled/linked library.
* Versions are centralised in `Directory.Packages.props`. Prefer the BCL over a new package.
* Before release run `dotnet list package --vulnerable --include-transitive`.

## Conventions

* C# latest, nullable enabled, file-scoped namespaces, primary constructors for DI, `async` + `CancellationToken` through the stack.
* Minimal APIs grouped per feature in `Features/*Endpoints.cs`. DTOs are records, enums serialise as strings.
* No business logic in `Program.cs` beyond wiring. Time via `TimeProvider`, not `DateTime.UtcNow` (CLI excepted).
* Tests: unit-test security-relevant helpers (tokens, sniffing, storage, ZIP naming); add integration tests with `WebApplicationFactory` and a temp SQLite/storage dir.
  Every security invariant above that can be tested should have a test.
* Commits are small and imperative; update `docs/` when behaviour changes.

## Definition of done

Strict build passes (0 warnings — analyzers, style and audit are warnings-as-errors) · `dotnet format --verify-no-changes` clean · tests pass · no new invariant violations · docs updated · no secrets or sample media committed · works with the default theme and does not require editing C# to restyle.

## Roadmap (do in order; each milestone is one PR)

**M0: make it compile and run.** Fix compile errors; verify `ZipArchive.CreateAsync` / `entry.OpenAsync` signatures on the installed SDK;
add the initial EF migration and switch startup to `MigrateAsync`; add `WebApplicationFactory` smoke test (create event -> request code -> verify -> list tabs).
**M1: correctness.** EXIF orientation in `ImageVariants` (`SKCodec.EncodedOrigin`); video duration (ffprobe); `TakenUtc` from EXIF/ffprobe;
owner endpoints for tab rename/reorder/delete (delete blobs); invite list management; unsubscribe page (`/u/{id}.{sig}`) in core JS + theme template.
**M2: UX.** Upload progress bar and parallel uploads; PhotoSwipe-style pinch zoom in lightbox (vendor MIT lib into `wwwroot/lib`, still CSP-clean);
multi-select mode with "download selected" via `POST /download` (ids list) in addition to stars; pagination/infinite scroll; i18n strings in theme.
**M3: notifications.** Digest mode; Telegram deep-link subscription flow; outbox with retry; per-tab subscriptions UI.
**M4: ops.** Owner admin UI (optional, behind owner role); storage quota; antivirus hook; HEIC support decision (see open questions); health endpoint; Windows service + IIS docs; GitHub Actions (build, test, vulnerable-package check, docker publish); second theme to prove the contract.

## Things not to do

Do not add a SPA framework, Node build step, ORM other than EF Core, external DB, cloud SDKs, analytics, or third-party scripts/fonts.
Do not weaken CSP, add `unsafe-inline`, or store media under `wwwroot`. Do not log full request URLs of `/e/{token}` at Information level.
