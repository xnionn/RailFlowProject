# RailFlow — Deployment Guide

This guide covers running RailFlow locally, publishing it, packaging it as a Docker image, configuring it and the limits of the current design.
RailFlow is a .NET 10 Blazor **Interactive Server** app: the UI runs on the server and talks to the browser over a persistent SignalR (WebSocket) connection.

---

## 1. Requirements

| Tool | Version | Needed for |
| :--- | :--- | :--- |
| .NET SDK | 10.0 (tested with 10.0.404) | local run, tests, publish |
| Docker Engine / Docker Desktop | any recent version with BuildKit | container image (optional) |
| Browser | current Chrome, Edge, Firefox or Safari | using the app (WebSockets must be allowed) |

---

## 2. Local run (development)

```bash
dotnet test                                  # unit, integration and end-to-end tests
dotnet run --project src/RailFlow.Web        # http://localhost:5163
```

- `dotnet run` uses the `http` profile from `src/RailFlow.Web/Properties/launchSettings.json` (`ASPNETCORE_ENVIRONMENT=Development`, port 5163). Use `--launch-profile https` for `https://localhost:7003` with the .NET dev certificate.
- The rail network is read from `src/RailFlow.Web/Data/*.csv`. Bookings are written to `src/RailFlow.Web/App_Data/` (`bookings.json`, `bookings.log`); the folder is created on the first booking and is ignored by git.
- To test with an empty booking history without touching your own data, point the app at a temporary folder:

  ```powershell
  $env:Storage__BookingDirectory = "$env:TEMP\railflow-qa"; dotnet run --project src/RailFlow.Web
  ```

  ```bash
  Storage__BookingDirectory=/tmp/railflow-qa dotnet run --project src/RailFlow.Web
  ```

---

## 3. Publish (framework-dependent)

```bash
dotnet publish src/RailFlow.Web/RailFlow.Web.csproj -c Release -o ./publish
cd publish
ASPNETCORE_HTTP_PORTS=8080 dotnet RailFlow.Web.dll      # PowerShell: $env:ASPNETCORE_HTTP_PORTS=8080; dotnet RailFlow.Web.dll
```

- The output contains `RailFlow.Web.dll`, `appsettings.json`, `wwwroot/`, the static-asset manifest and `Data/stations.csv` + `Data/connections.csv` (copied by the `<None Update="Data\*.csv" ...>` item in the csproj).
- **Start the app from inside the publish folder.** The content root is the current working directory, so `Data/`, `wwwroot/` and the default `App_Data/` are resolved relative to it. Started from another folder, the app cannot find `Data/` and silently falls back to the built-in network (visible in the **Network** tab as a rejected `loader` line).
- `./publish/` is already ignored by `.gitignore`.
- The target machine needs the ASP.NET Core 10 runtime.

---

## 4. Docker

The repository contains a multi-stage `Dockerfile` and a `.dockerignore`:

| Stage | Base image | What it does |
| :--- | :--- | :--- |
| `build` | `mcr.microsoft.com/dotnet/sdk:10.0` | copies `RailFlow.slnx` and the three `src/*.csproj` files, restores `RailFlow.Web` (cached layer), copies `src/`, publishes in Release to `/app/publish` |
| `runtime` | `mcr.microsoft.com/dotnet/aspnet:10.0` | copies the publish output to `/app`, creates `/app/data` owned by the non-root `app` user (UID `$APP_UID` = 1654), declares it as a `VOLUME`, listens on 8080, runs as `app` |

The test project is not copied into the image (`tests/` is in `.dockerignore`); run `dotnet test` before building.
There is no `HEALTHCHECK` instruction, because the `aspnet` runtime image contains neither `curl` nor `wget`. Point the platform's HTTP probe at `GET /healthz` instead (returns `200 Healthy`; see section 7).

### 4.1 Build and run

```bash
docker build -t railflow:latest .

docker volume create railflow-data
docker run -d --name railflow \
  -p 8080:8080 \
  -v railflow-data:/app/data \
  railflow:latest

curl "http://localhost:8080/api/route-quote?from=LONDON&to=PARIS&passengers=1"
docker logs railflow

docker stop railflow && docker rm railflow     # bookings stay in the railflow-data volume
```

PowerShell uses a backtick instead of `\` for line continuation, or put the `docker run` command on one line.

Optional extras:

```bash
# Use your own network files (read-only)
-v "$(pwd)/my-network:/app/network:ro" -e Storage__NetworkDataDirectory=/app/network

# Replace a demo password hash (see section 5.2)
-e 'DemoUsers__1__PasswordHash=PBKDF2-SHA256.100000.<salt>.<hash>'
```

### 4.2 Bind mounts instead of a named volume

A named volume inherits the ownership of `/app/data` from the image, so it is writable straight away.
With a bind mount on a **Linux** host (`-v ./railflow-data:/app/data`) the host folder must be writable by UID 1654:

```bash
mkdir -p railflow-data && sudo chown 1654:1654 railflow-data
```

Docker Desktop on Windows and macOS maps permissions for bind mounts, so this step is normally not needed there.
If the folder is not writable, the app still starts; the **Bookings** tab then shows a `Storage error for 'bookings.json': ...` message when a booking is saved.

### 4.3 Verification status of the Docker steps

> **Not yet verified on this machine.** Docker (and Podman / WSL distributions) were not installed on the development machine when this guide was written (2026-11-12), so the image has not been built or run.
> What *was* checked: the exact restore and publish commands from the `build` stage were run on a copy of the build context that excludes everything listed in `.dockerignore`, and they succeeded with `Data/*.csv` in the output (see section 9).
> Before the demo, run section 4.1 on a machine with Docker and record the result below.

| Check | Command | Result | Date / tester |
| :--- | :--- | :--- | :--- |
| Image builds | `docker build -t railflow:latest .` | | |
| Container starts, logs show `Now listening on: http://[::]:8080` | `docker run ...` / `docker logs railflow` | | |
| API answers | `curl "http://localhost:8080/api/route-quote?from=LONDON&to=PARIS&passengers=1"` | | |
| UI loads and is interactive (tabs switch) | browser at `http://localhost:8080` | | |
| Process runs as non-root | `docker exec railflow id` (expect `uid=1654(app)`) | | |
| Booking survives `docker rm` + new `docker run` with the same volume | book, re-create container, open **Bookings** | | |

---

## 5. Configuration

All settings can be given in `appsettings.json`, as environment variables (`:` becomes `__`), or as command-line arguments (`--Storage:BookingDirectory=/path`).

### 5.1 Settings

| Setting / environment variable | Default | Docker image value | Purpose |
| :--- | :--- | :--- | :--- |
| `Storage__BookingDirectory` | `{content root}/App_Data` | `/app/data` (volume) | Folder for `bookings.json` and `bookings.log`. Created if missing. |
| `Storage__NetworkDataDirectory` | `{content root}/Data` | `/app/Data` (from the image) | Folder with `stations.csv` and `connections.csv`. If the files are missing or unreadable, the built-in network is used and the reason is shown in the **Network** tab. |
| `ASPNETCORE_HTTP_PORTS` | (launch profile: 5163) | `8080` | HTTP port(s) Kestrel listens on. |
| `ASPNETCORE_ENVIRONMENT` | `Production` (`Development` via launch profile) | `Production` | `Development` shows detailed errors; `Production` enables the `/Error` page and HSTS. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `false` | not set | Set to `true` behind a TLS-terminating reverse proxy so the app sees the original scheme and client IP (section 6.2). |
| `AllowedHosts` | `*` | `*` | Restrict to the public host name in production, e.g. `AllowedHosts=railflow.example.org`. |
| `DemoUsers__{n}__UserName` / `DisplayName` / `Role` / `PasswordHash` | two demo accounts (below) | same | Sign-in accounts. `Role` is `Passenger` or `Dispatcher`. |
| `Security__LoginAttemptsPerMinute` | `10` | `10` | Sign-in attempts allowed per client address per minute; more are answered with `429 Too Many Requests`. Behind a proxy, enable forwarded headers so the real client address is used. |
| `Logging__LogLevel__Default` | `Information` | `Information` | Log verbosity (logs go to the console / `docker logs`). Failed searches are logged as errors with the exception; storage failures, rejected CSV lines and engineering-works changes as warnings or information. |

`dotnet run` without a launch profile starts in `Production`; then the CSS and Blazor scripts are only served from
publish output. For a quick local run use the launch profile (`dotnet run --project src/RailFlow.Web`) or set
`ASPNETCORE_ENVIRONMENT=Development`.

Note: inside the container the booking folder is `/app/data` (lower case) and the network folder is `/app/Data` (upper case, from the publish output). They are different folders on Linux.

### 5.2 Demo accounts

| Index | User name | Password (demo only) | Display name | Role |
| :--- | :--- | :--- | :--- | :--- |
| 0 | `passenger` | `passenger-demo` | Demo Passenger | `Passenger` |
| 1 | `dispatcher` | `dispatcher-demo` | Demo Dispatcher | `Dispatcher` |

Only hashes are stored, in the format `PBKDF2-SHA256.{iterations}.{salt base64}.{hash base64}` (PBKDF2 with SHA-256, 16-byte salt, 32-byte hash, 100,000 iterations by default).
Hashes are created by `RailFlow.Infrastructure.Security.PasswordHasher.Hash(password)`.

**Because the demo passwords are published in the README, replace both hashes before exposing the app outside a classroom.**

To create a hash, save this .NET 10 file-based program **outside the repository** (for example `C:\temp\hash-password.cs`) and point `#:project` at your clone:

```csharp
#:project C:/path/to/RailFlow/src/RailFlow.Infrastructure/RailFlow.Infrastructure.csproj
using RailFlow.Infrastructure.Security;

var password = args.Length > 0 ? args[0] : throw new ArgumentException("usage: dotnet run hash-password.cs -- <password>");
var hash = PasswordHasher.Hash(password);
Console.WriteLine(hash);
Console.Error.WriteLine($"verifies: {PasswordHasher.Verify(password, hash)}");
```

```bash
dotnet run hash-password.cs -- "a-new-strong-password"
```

Then either edit `PasswordHash` in `appsettings.json`, or override it without changing the file:

```bash
DemoUsers__1__PasswordHash='PBKDF2-SHA256.100000.<salt>.<hash>'    # index 1 = dispatcher
```

After overriding, the old demo password no longer works for that account. Wrap the value in single quotes in a shell: it contains `+`, `/` and `=`.
Accounts can also be added (`DemoUsers__2__UserName`, ...). In a real deployment the hashes would come from a secret store (environment secrets, Docker/Kubernetes secrets, Azure Key Vault) rather than from `appsettings.json`.

---

## 6. HTTPS, reverse proxies and cookies

### 6.1 `UseHttpsRedirection` in a plain-HTTP container

`Program.cs` calls `app.UseHttpsRedirection()`. The middleware only redirects when it can determine an HTTPS port (from `HttpsRedirectionOptions.HttpsPort`, the `HTTPS_PORT` / `ASPNETCORE_HTTPS_PORT` / `ASPNETCORE_HTTPS_PORTS` settings, or an `https://` address Kestrel is bound to).
The image only sets `ASPNETCORE_HTTP_PORTS=8080`, so no HTTPS port is known: the middleware logs one warning and lets the request through over HTTP.

This was confirmed with the Release publish output running in `Production` on plain HTTP (2026-11-12): `GET /` returned `200` with no `Location` header, and the log contained:

```
warn: Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3]
      Failed to determine the https port for redirect.
```

The warning is harmless. Do **not** set `ASPNETCORE_HTTPS_PORT(S)` on the container unless Kestrel really serves TLS, otherwise browsers are redirected to a port that is not listening.
`UseHsts()` only adds the HSTS header to HTTPS responses, so it has no effect on plain HTTP.

### 6.2 Behind a reverse proxy (recommended for anything public)

Terminate TLS in a reverse proxy (nginx, Caddy, Traefik, Azure App Service / Container Apps front end) and forward to port 8080. The proxy must:

1. **Forward WebSocket upgrades.** Blazor Server and `/hubs/traffic` need them (otherwise SignalR falls back to long polling, which is slower and less reliable). nginx example:

   ```nginx
   location / {
       proxy_pass         http://127.0.0.1:8080;
       proxy_http_version 1.1;
       proxy_set_header   Upgrade $http_upgrade;
       proxy_set_header   Connection $connection_upgrade;   # map $http_upgrade -> "upgrade" / "close"
       proxy_set_header   Host $host;
       proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
       proxy_set_header   X-Forwarded-Proto $scheme;
       proxy_read_timeout 100s;
   }
   ```

2. **Send `X-Forwarded-Proto`**, and the container should run with `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. The app then knows the original request was HTTPS, so the `RailFlow.Auth` cookie is issued with the `Secure` flag (the cookie policy is "same as request") and HSTS is sent. No code change is needed for this; the variable turns on the built-in forwarded-headers middleware.
   Only enable it when the container is reachable through the proxy alone, because it trusts forwarded headers from any address.

### 6.3 Data-protection keys

The sign-in cookie and the antiforgery tokens (login form, **Sign out** form) are protected with ASP.NET Core data-protection keys. `Program.cs` stores them in a `keys` folder inside the booking directory (`AddDataProtection().PersistKeysToFileSystem(...)`), so in the container they live in the `/app/data` volume next to the bookings:

- `docker restart` and `docker rm` + `docker run` with the same volume both keep them, so users stay signed in after a re-deploy;
- deleting the volume removes the keys as well as the bookings; everybody is then signed out.

Locally the keys are in `src/RailFlow.Web/App_Data/keys`, which is ignored by git. For more than one instance, see section 8.

---

## 7. Operations checklist

| Task | How |
| :--- | :--- |
| Smoke test | `GET /api/route-quote?from=LONDON&to=PARIS&passengers=1` returns `200` and `"status":"Something"`; `GET /api/bookings` without signing in returns `401`. |
| Liveness / readiness probe | `GET /healthz` returns `200` with body `Healthy`. |
| Logs | Console output (`docker logs railflow`). |
| Backup | Copy `bookings.json` and `bookings.log` from the booking folder / volume, e.g. `docker run --rm -v railflow-data:/data -v "$(pwd)":/backup alpine tar czf /backup/railflow-data.tgz -C /data .` |
| Reset demo data | Stop the app and delete the two booking files (or use a fresh volume). |
| Reset dispatcher toggles | Engineering works and simulated failures live in memory: restart the app, or untick them on the **Dispatcher** tab. |
| Update | Build a new image, `docker stop` / `docker rm` the old container, `docker run` the new one with the same volume(s). Open browser sessions show the reconnect dialog and reload. |

---

## 8. Scaling limits (single instance only)

RailFlow is designed and tested for **one server instance**. Running several replicas behind a load balancer would give wrong or inconsistent behaviour today, for these reasons:

| State | Where it lives | Effect with more than one instance |
| :--- | :--- | :--- |
| Engineering works and simulated failures | `OperationsControls`, an in-memory singleton | Each instance has its own toggles; a dispatcher change only affects users connected to the same instance. Lost on restart. |
| Live activity / advisories | controller event + SignalR `/hubs/traffic` on the same process | Only clients of the same instance receive them. |
| Route cache (memoized BFS) | in-memory `ConcurrentDictionary` per process | Harmless (each instance warms its own cache), but cache statistics differ per instance. |
| Undo/redo history, current role | `ImmutableAppStateContainer`, scoped to one Blazor circuit | Lost when the circuit ends (server restart, reconnect failure). |
| Bookings | `bookings.json` / `bookings.log` on the local file system | Writes are serialised by an in-process lock only, and the JSON journal is rewritten as a whole (temp file + move); two instances sharing one volume can overwrite each other's bookings. Separate volumes give each instance a different history. |
| Data-protection keys | file folder in the data volume (section 6.3) | A cookie issued by one instance cannot be read by another. |

In addition, **Blazor Server needs sticky sessions**: a circuit lives in the memory of the instance that created it, so every request and the WebSocket of one browser tab must reach the same instance (ARR affinity / session affinity on the load balancer), and WebSockets must be enabled end to end.

The single instance can still be scaled *up* (more CPU/RAM). Each open tab holds a circuit in server memory, so memory use grows with the number of concurrent users.

### What scaling out would require

1. **A shared database** for bookings (e.g. PostgreSQL or SQL Server behind the existing `IBookingJournal` interface) instead of local files.
2. **Shared dispatcher state**: store engineering works and failure toggles in the database or Redis, and publish changes through a message channel instead of an in-process event.
3. **A SignalR backplane** (Redis backplane via `AddStackExchangeRedis`, or Azure SignalR Service) so `/hubs/traffic` messages reach clients on every instance. Azure SignalR Service can also carry the Blazor circuits.
4. **Persisted, shared data-protection keys** (`AddDataProtection().PersistKeysToStackExchangeRedis(...)` or to a shared file system / blob storage, with a common application name) so cookies and antiforgery tokens work on every instance.
5. **Sticky sessions** on the load balancer for Blazor Server circuits.
6. Real user accounts in a database or an external identity provider instead of `DemoUsers` in configuration.

---

## 9. Verification log

What was actually executed while preparing this guide (Windows 11, .NET SDK 10.0.404, commit `d2ecbf6` plus uncommitted working-tree changes, 2026-11-12). Builds used a private `--artifacts-path` / a copied folder so the IDE's `bin/` and `obj/` were not touched.

| # | Check | Outcome |
| :--- | :--- | :--- |
| 1 | `dotnet publish src/RailFlow.Web/RailFlow.Web.csproj -c Release` | Succeeded. Output contains `Data/stations.csv` (410 B), `Data/connections.csv` (896 B), `appsettings.json`, `wwwroot/` and `RailFlow.Web.staticwebassets.endpoints.json`. |
| 2 | Dockerfile build stage simulated on a copy of `RailFlow.slnx` + `src/` without `bin/`, `obj/`, `App_Data/`: `dotnet restore src/RailFlow.Web/RailFlow.Web.csproj`, then `dotnet publish ... -c Release -o out/publish --no-restore /p:UseAppHost=false` | Succeeded; `Data/*.csv` present; no `RailFlow.Web.exe` app host (as intended). |
| 3 | Published app started in `Production` on plain HTTP (port 18093), `GET /api/route-quote?from=LONDON&to=PARIS&passengers=1` | `200`, `{"status":"Something","origin":"LONDON","destination":"PARIS","distanceKm":460,"finalPrice":101.20,"currency":"€","hasEngineeringWorks":false}` |
| 4 | Same, `from=LONDON&to=ATLANTIS` | `404`, `{"status":"Nothing","message":"No route or station found."}` |
| 5 | `GET /api/bookings` without a cookie | `401` |
| 6 | `GET /` and `GET /login` over HTTP | both `200`, no redirect; HTTPS-redirection warning logged once (section 6.1) |
| 7 | Sign-in by form POST: `passenger` / `passenger-demo`, then `GET /api/bookings` | `302` to `/`, `RailFlow.Auth` cookie set; API returned `403` |
| 8 | Sign-in with a wrong password | page re-rendered with "Unknown user name or wrong password.", no cookie |
| 9 | Dispatcher hash overridden with `DemoUsers__1__PasswordHash` (hash created by the program in 5.2); sign-in with the new password, then `GET /api/bookings` | `302` + cookie; API returned `200`. The old `dispatcher-demo` password was rejected. |
| 10 | Hash program from section 5.2 | Printed a `PBKDF2-SHA256.100000.…` hash; `verifies: True` |
| 11 | `docker --version` | **Docker not installed** — Docker steps in section 4 not yet verified. |
