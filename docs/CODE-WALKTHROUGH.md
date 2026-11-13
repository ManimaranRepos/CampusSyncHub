# Code Walkthrough

A guided tour of the CampusSync Hub source: what each file does, how a registration flows through the system, and why the code is written the way it is.

## 1. The big picture

```
Student registers in the SIS
        │
        ▼
Ellucian Ethos publishes a change notification  ──►  MockEthos.Api  (/consume)
        │
        ▼
CampusSync Hub polls /consume every few seconds  ──►  SyncWorker
        │
        ▼
For each "section-registrations created" event   ──►  RegistrationSyncProcessor
   1. look up the student and section in Ethos
   2. enroll in LMS        ─┐
   3. create library card   ├─►  TargetSystems.Api
   4. send welcome email   ─┘
   5. record the result in SQLite
        │
        ▼
Blazor dashboard updates live over SignalR       ──►  Home.razor
```

## 2. Solution layout

```
CampusSyncHub.sln
├── src/
│   ├── MockEthos.Api/            Fake Ellucian Ethos (auth, resources, change feed)
│   ├── TargetSystems.Api/        Fake LMS, library and email services
│   └── CampusSync.Hub/           The product
│       ├── Ethos/                Talking to Ethos
│       ├── Targets/              Talking to LMS / library / email
│       ├── Sync/                 The sync engine
│       ├── Data/                 Database (EF Core + SQLite)
│       ├── Components/Pages/     Blazor dashboard
│       └── Program.cs            Wiring: DI, resilience, logging, endpoints
├── tests/
│   ├── CampusSync.Tests/         Unit tests (fast, no network)
│   └── CampusSync.E2ETests/      Playwright tests (real processes + real browser)
└── .github/workflows/ci.yml      GitHub Actions pipeline
```

## 3. `src/MockEthos.Api`: a stand-in for Ellucian Ethos

A real Ethos tenant needs a university licence, so this project imitates the parts of the Ethos Integration contract the Hub relies on.

| Endpoint | What it does | Real Ethos equivalent |
|---|---|---|
| `POST /auth` | Checks `Authorization: Bearer <api-key>`, returns a 5-minute JWT as plain text | Identical flow |
| `GET /api/persons/{id}` | Student record: names, school email, Banner ID | EEDM `persons` |
| `GET /api/sections/{id}` | Course section: code, title, dates | EEDM `sections` |
| `GET /api/section-registrations/{id}` | Which student is in which section | EEDM `section-registrations` |
| `GET /consume?lastProcessedID=N` | Change notifications with id > N | Ethos change-notification feed |
| `POST /simulate/registration` | Demo helper: creates a random registration and publishes its notification | Not in real Ethos |

Key class: **`EthosStore`**. Seeds 20 students and 6 courses, keeps resources in `ConcurrentDictionary`s, and appends notifications with increasing ids. A notification looks like:

```json
{
  "id": 12,
  "published": "2026-11-13T02:00:00Z",
  "resource": { "name": "section-registrations", "id": "…guid…", "version": "application/vnd.hedtech.integration.v12+json" },
  "operation": "created",
  "contentType": "resource-representation",
  "content": { "registrant": { "id": "…" }, "section": { "id": "…" }, "status": { "registrationStatus": "registered" } }
}
```

## 4. `src/TargetSystems.Api`: the downstream systems

Three tiny services in one app:

- `POST /lms/enrollments`: **idempotent**. The same student + section twice returns `200` instead of creating a duplicate.
- `POST /library/accounts`: creates a library card number once per student.
- `POST /notify/email`: stores the message in an in-memory outbox and logs it.

`CHAOS_RATE` (0.0–1.0) makes that share of requests return **503**, which is how the demo proves the retry logic.

## 5. `src/CampusSync.Hub`: the product

### 5.1 `Ethos/EthosClient.cs`

- **Models** (`EthosPerson`, `EthosSection`, `SectionRegistration`, `EthosNotification`): C# records holding only the Ethos fields we use. Helper properties such as `EthosPerson.SchoolEmail` pick the right value out of Ethos's nested arrays.
- **`EthosTokenProvider`** (singleton): exchanges the API key for a JWT and reuses it until 1 minute before expiry. A `SemaphoreSlim` ensures only one request fetches a new token when several run at once.
  > Why a singleton? Typed `HttpClient`s are transient. When the cache lived inside `EthosClient`, every poll created a new client and fetched a new token. Serilog logs exposed this; the fix cut token requests from one every 5 seconds to one every 4 minutes.
- **`EthosClient`**: `ConsumeAsync` reads the change feed; `GetAsync<T>` reads a resource. Adds the bearer token and the versioned `Accept` header to every call.

### 5.2 `Targets/TargetGateway.cs`

One typed `HttpClient` with three methods: `EnrollInLmsAsync`, `EnsureLibraryAccountAsync`, `SendEmailAsync`. It contains **no retry code**. Retries are configured once in `Program.cs` and applied to every call automatically.

### 5.3 `Sync/SyncWorker.cs`: the heartbeat

A `BackgroundService` driven by a `PeriodicTimer` (default every 5 s):

1. Read the checkpoint (last processed notification id) from the database.
2. Call `/consume?lastProcessedID=<checkpoint>`.
3. Hand each notification to the processor, **in id order**.
4. Save the new checkpoint **after every message**.

If Ethos is down, the error is logged, the dashboard shows "Ethos unreachable", and the next tick simply tries again.

`SyncNotifier` (same file) is a tiny event hub: the worker and processor call `Notify()`, and every open dashboard refreshes.

### 5.4 `Sync/RegistrationSyncProcessor.cs`: the business logic

`ProcessAsync(notification, isReplay)`:

| Step | Code behaviour | Why |
|---|---|---|
| Filter | Ignore anything that is not a created/replaced `section-registrations` event | Only registrations matter here |
| Idempotency check | If the id is already in `ProcessedNotifications`, return `Duplicate` | Safe if Ethos re-delivers a message |
| Enrich | Fetch the person and section from Ethos | Notifications carry only ids |
| Fan out | LMS → library → email, recording `LMS ✓ · Library ✓ · Email ✓` | Shown on the dashboard |
| Success | Write a `SyncLog`, remove any old dead letter | |
| Failure | Write a failed `SyncLog`, add or update a `DeadLetter` with the full payload | Nothing is lost |
| Always | Mark the id processed, save, notify the UI | Worker moves on; dead letters are replayed separately |

`ReplayAsync(id)` loads a dead letter's stored payload and runs `ProcessAsync` with `isReplay: true`, which skips the duplicate check. That is the dashboard's **Retry** button.

A Serilog **scope** adds `NotificationId`, `EthosResource` and `IsReplay` to every log line written during processing.

### 5.5 `Data/SyncDbContext.cs`: what is stored

| Table | Purpose |
|---|---|
| `SyncLogs` | One row per processing attempt: student, section, steps, status, error, duration |
| `DeadLetters` | Failed notifications with their JSON payload, last error and attempt count |
| `ProcessedNotifications` | Idempotency ledger: each notification id once |
| `SyncStates` | Key/value store; holds the Ethos checkpoint |

SQLite keeps the demo setup-free. Because it is EF Core, moving to SQL Server means swapping `UseSqlite` for `UseSqlServer` and the connection string.

### 5.6 `Program.cs`: how everything is wired

- **Serilog**: configured from `appsettings.json`; console plus rolling compact-JSON files; request logging for `/api` calls.
- **Resilience** (`AddStandardResilienceHandler`, Polly v8): 3 retries with exponential back-off and jitter, 5 s per attempt, 20 s total, circuit breaker. Applied to the Ethos and target clients.
- **DI**: `EthosTokenProvider` singleton, typed clients, `SyncNotifier` singleton, processor transient, worker hosted service.
- **Minimal APIs**: `GET /api/stats`, `GET /api/logs`, `POST /api/dead-letters/{id}/retry`.
- On startup, `EnsureCreated()` creates the SQLite database.

### 5.7 `Components/Pages/Home.razor`: the dashboard

An interactive Blazor Server page (`@rendermode InteractiveServer`):

- Subscribes to `SyncNotifier.Changed`; on each event it reloads from the database and calls `StateHasChanged()`. Blazor pushes the diff to the browser over the SignalR connection, so there is **no polling and no page refresh**.
- KPI cards, live sync log, dead-letter queue with **Retry**, downstream counters.
- Demo buttons call Ethos `/simulate/registration`.

## 6. Tests

### Unit tests: `tests/CampusSync.Tests`

`RegistrationSyncProcessorTests` uses an in-memory SQLite database and hand-written fakes for Ethos and the targets:

1. A registration reaches LMS, library and email, and is logged as `Success`.
2. The same notification twice: downstream systems are called once (`Duplicate`).
3. LMS failure → dead letter; replay after recovery → `Retried`, queue empty.
4. Non-registration notifications are ignored.

### End-to-end tests: `tests/CampusSync.E2ETests`

- **`CampusSyncStack`** (shared fixture) starts all three apps as real processes on free ports, with a temporary database and a 1-second poll, then launches headless Chromium.
- **`DashboardTests`** drives the real UI:
  1. Seeded registrations show `LMS ✓ · Library ✓ · Email ✓`.
  2. Clicking **Simulate** increases the counter **without reloading** (proves SignalR).
  3. Stopping the target systems → the event lands in the dead-letter queue → restarting them and clicking **Retry** recovers it.
  4. `/api/stats` agrees with the dashboard.

Assertions compare before/after values, so the tests pass in any order.

## 7. CI: `.github/workflows/ci.yml`

On every push and pull request:

1. Restore and build (Release)
2. Unit tests
3. Install Playwright Chromium, then run the E2E tests
4. Upload test results as an artifact
5. Build the Docker images (separate job)

## 8. Reliability guarantees, summarised

| Problem | Handled by |
|---|---|
| Downstream system briefly failing | Polly retries with back-off and jitter |
| Downstream system down for a long time | Dead-letter queue and replay |
| Ethos re-delivers a notification | `ProcessedNotifications` idempotency ledger, plus idempotent LMS endpoint |
| Hub restarts mid-batch | Checkpoint saved after every message |
| Ethos itself unavailable | Worker logs, shows status, retries next tick |
| Token expiry | Singleton provider refreshes 1 minute early |

## 9. Pointing at a real Ethos tenant

Set `Ethos:BaseUrl` to the tenant URL and `Ethos:ApiKey` to a real application key. Then check the Ellucian developer documentation for the exact resource versions your institution exposes and adjust the `Accept` header and record fields if needed.
