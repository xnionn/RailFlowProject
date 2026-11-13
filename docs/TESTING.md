# Testing RailFlow

All tests live in `tests/RailFlow.Tests` (xUnit). There are three kinds:

- **Unit tests** for the functional core and the infrastructure services (pure functions, records, Result/Maybe pipelines).
- **Component tests** for the shared Blazor components, rendered in memory with [bUnit](https://bunit.dev).
- **Integration / end-to-end tests** that start the real web app in memory with `WebApplicationFactory<Program>`
  and call it over HTTP: same `Program.cs`, middleware, cookie sign-in, antiforgery, minimal API endpoints and Razor pages.

The tests are deterministic and never write into the repository. Storage tests and the web app under test use
fresh folders in the system temp directory (`railflow-tests-*`, `railflow-web-tests-*`) that are deleted when the test class is disposed.
The web tests point `Storage:BookingDirectory` at that temp folder; the network CSV files are read from `src/RailFlow.Web/Data`.

## Running the tests

From the repository root:

```bash
dotnet test RailFlow.slnx -c Release
```

Run one group only:

```bash
dotnet test RailFlow.slnx --filter "FullyQualifiedName~ComponentTests"
dotnet test RailFlow.slnx --filter "FullyQualifiedName~WebApiIntegrationTests"
```

If the app is already running from an IDE and holds the default `bin/obj` folders, build into a separate folder:

```bash
dotnet test RailFlow.slnx -c Release --artifacts-path ./.artifacts-tests
```

## Coverage report

Coverage is collected with `coverlet.collector` (already referenced by the test project) and turned into a readable
report with ReportGenerator:

```bash
# 1. run the tests and write Cobertura XML into ./TestResults/<guid>/coverage.cobertura.xml
dotnet test RailFlow.slnx -c Release --collect:"XPlat Code Coverage" --results-directory ./TestResults

# 2. install ReportGenerator once
dotnet tool install -g dotnet-reportgenerator-globaltool

# 3. build the report (HTML for browsing, text/markdown summaries for quick reading)
reportgenerator -reports:"./TestResults/*/coverage.cobertura.xml" -targetdir:"./coverage-report" -reporttypes:"Html;TextSummary;MarkdownSummary"
```

Open `coverage-report/index.html` in a browser, or read `coverage-report/Summary.txt`.
`TestResults/` and `coverage-report/` are build output and should not be committed.

## Continuous integration

`.github/workflows/ci.yml` builds and runs the whole suite on Ubuntu, Windows and macOS for every push and pull
request, then builds the Docker image, starts it and waits for `/healthz` to answer.

## Results

Measured on 13 Nov 2026 (.NET SDK 10.0.404, Release build, coverlet 6.0.4, ReportGenerator 5.6.0).

**198 tests, all passing** (107 unit tests + 44 component tests + 47 integration tests). Build: 0 warnings, 0 errors.

### Coverage per assembly

| Assembly | Line coverage | Branch coverage |
|---|---|---|
| RailFlow.Core | 93.8% | 81.6% |
| RailFlow.Infrastructure | 92.6% | 76.7% |
| RailFlow.Web | 80.6% | 70.3% |
| **Overall** | **88.1% (1948 of 2211)** | **75.0% (777 of 1035)** |

Method coverage overall: 91.8% (549 of 598).

For comparison, the first version of the test suite (67 unit tests, no component or integration tests) gave
50.0% line / 35.1% branch coverage overall, with RailFlow.Web at 0%.

What is still not covered, and why:

- `Pages/Home.razor` (47% lines): the page that wires the tabs together. Every tab is tested on its own with bUnit,
  and the integration tests check the prerendered page (including the tab named in the address), but clicking
  through the whole page over a live circuit would need a browser test (e.g. Playwright).
- `Pages/Error.razor` (0%): only shown for an unhandled exception in production mode.
- `AppLog` (70%): the log messages for failures that the tests do not provoke through the UI.

## Test files

| File | Tests | What it covers |
| :--- | :--- | :--- |
| `RailFlowFunctionalTests.cs` | 14 | Monad laws for Maybe, Something/Nothing/Error handling without duplicate logging, async Bind, combinators (Fork, Compose, Transduce), a curried CSV parser, stack-safe loops, shortest path with cached lookups, the end-to-end route-pricing pipeline and the immutable state container with undo/redo. |
| `ResultAndPipelineTests.cs` | 8 | `Result<T>` (Bind, Map, Ensure, Partition, conversion from Maybe), `Either` and left-to-right function pipelining/composition. |
| `MonadsAndCombinatorsTests.cs` | 6 | Reader, State, Tap, Alt / AltMaybe and a partially applied logger. |
| `MemoizationTests.cs` | 5 | `Memoize` and `MemoizeBy` run the function once per key, `MemoizeAsync` does not cache a failure, `Flatten` keeps the case, `MapAsync`. |
| `RecursionAndLazyTests.cs` | 6 | Recursive timetable, the trampoline (deep recursion without stack overflow) and lazy sequences. |
| `PlannerValidationTests.cs` | 11 | The validation gate (`ValidRouteQuery` and the other cases of the union) and the loop benchmark agreeing in every row. |
| `SimulationAndPricingTests.cs` | 15 | Journey time with stops, train position at a given minute, simulation arriving when the timetable says, the pure `Tariff` (return share, hotel tiers, surcharge only on affected kilometres) the engineering-works advisory and the activity feed starting from the works in force. |
| `BookingWorkflowTests.cs` | 16 | Booking workflow with a fake payment gateway (a travel date in the past is refused before any charge), receipt equality/ordering, the receipt line codec (owner and travel date, still reading old 11-field lines), ticket export formats and LINQ booking reports. |
| `StorageAndCsvTests.cs` | 11 | Text and JSON booking journals on a real temp folder, the CSV network import with rejected lines, the loader fallback and price → book → store → read back. |
| `AuthenticationTests.cs` | 15 | PBKDF2 password hashing, the demo user store (same message for unknown user and wrong password) and role/claim mapping. |
| `ComponentTests.cs` | 18 | bUnit tests for `StatCard`, `PipelineSteps` (each status has its own label, selection, click callback) and `MaybeView`. |
| `TabComponentTests.cs` | 26 | bUnit tests for every tab: dispatcher controls locked for passengers, a passenger sees their account's bookings in a new session but nobody else's, the amount to pay before payment, one charge per search even after a re-price, no payment when the booking file is damaged, the timetable follows date and departure (with +1d after midnight), the Lab cache and CSV check, course notes hidden by default, station fields show names but store codes. |
| `TrafficHubTests.cs` | 3 | SignalR hub over the in-memory server: a guest can connect but not toggle works, a passenger cannot broadcast, a dispatcher's change reaches every client. |
| `WebApiIntegrationTests.cs` | 44 | The real app over HTTP: pages and the tab named in the address; `/api/route-quote` (quote, 404, 400 for invalid input, problem details for a missing or non-numeric parameter, case-insensitive codes); the friendly 404 page and a bare 404 under `/api`; the login form with antiforgery token and the rate limit (429); `/api/bookings` and ticket export (401 / 403 / 200); open-redirect protection; logout without token rejected; ticket download in three formats. |
