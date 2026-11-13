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

## Results

Measured on 13 Nov 2026 (.NET SDK 10.0.404, Release build, coverlet 6.0.4, ReportGenerator 5.6.0).

**165 tests, all passing** (93 unit tests + 40 component tests + 32 integration tests). Build: 0 warnings, 0 errors.

### Coverage per assembly

| Assembly | Line coverage | Branch coverage |
|---|---|---|
| RailFlow.Core | 88.7% | 76.3% |
| RailFlow.Infrastructure | 91.6% | 74.6% |
| RailFlow.Web | 81.3% | 72.8% |
| **Overall** | **86.5% (1743 of 2015)** | **74.5% (644 of 864)** |

Method coverage overall: 89.5% (504 of 563).

For comparison, the first version of the test suite (67 unit tests, no component or integration tests) gave
50.0% line / 35.1% branch coverage overall, with RailFlow.Web at 0%.

What is still not covered, and why:

- `Pages/Home.razor` (46% lines): the page that wires the tabs together. Every tab is tested on its own with bUnit,
  and the integration tests check the prerendered page, but clicking through the whole page over a live circuit
  would need a browser test (e.g. Playwright).
- `Pages/Error.razor` (0%): only shown for an unhandled exception in production mode.
- `Memoization` helpers, `Maybe<T>` base members and parts of `Combinators` in RailFlow.Core: general helpers from
  the course labs; the app uses the memoized route search (covered) but not every overload.

## Test files

| File | Tests | What it covers |
|---|---|---|
| `RailFlowFunctionalTests.cs` | 14 | Monad laws for Maybe, Something/Nothing/Error handling without duplicate logging, async Bind, combinators (Fork, Compose, Transduce), a curried CSV parser, stack-safe loops, shortest path with cached lookups, the end-to-end route-pricing pipeline and the immutable state container with undo/redo. |
| `ResultAndPipelineTests.cs` | 8 | `Result<T>` (Bind, Map, Ensure, Partition, conversion from Maybe), `Either` and left-to-right function pipelining/composition. |
| `MonadsAndCombinatorsTests.cs` | 6 | Reader (environment read only on Run), State (state threaded through Bind without mutation), Tap, Alt / AltMaybe and a partially applied logger. |
| `RecursionAndLazyTests.cs` | 6 | Recursive timetable, the trampoline (deep recursion without stack overflow) and lazy sequences. |
| `PlannerValidationTests.cs` | 11 | The validation gate: trimmed stations become a `ValidRouteQuery`; missing station, same station (any case) and passenger counts outside 1–50 become the other cases of the union. |
| `SimulationAndPricingTests.cs` | 9 | Journey time with stops, train position at a given minute, simulation arriving when the timetable says, the engineering-works surcharge only on affected kilometres, and the advisory naming the affected sections. |
| `BookingWorkflowTests.cs` | 13 | Booking workflow Validate → ApplyPromo → Charge with a fake payment gateway, receipt equality/ordering, the receipt line codec, ticket export formats and LINQ booking reports. |
| `StorageAndCsvTests.cs` | 11 | Text and JSON booking journals on a real temp folder (append without duplicates, corrupt lines, invalid JSON as Failure), the CSV network import with rejected lines, the loader fallback to the built-in network, and price → book → store → read back through the other format. |
| `AuthenticationTests.cs` | 15 | PBKDF2 password hashing, the demo user store (same message for unknown user and wrong password) and role/claim mapping. |
| `ComponentTests.cs` | 18 | bUnit tests for `StatCard`, `PipelineSteps` (status labels, numbering, selected step, click callback) and `MaybeView` (Something/Nothing/Error templates, placeholder, retry button only for a dispatcher). |
| `TabComponentTests.cs` | 22 | bUnit tests for every tab: the Dispatcher tab is locked for passengers (and a toggle call from a passenger changes nothing), passengers see only their own bookings, the amount to pay updates for a promo code before payment, a booking charges once, the Network tab marks connections under works, the timetable follows the departure time, the Lab tab marks rejected CSV lines and serves repeated searches from the cache, course notes are hidden by default, and the station fields show names but store codes. |
| `TrafficHubTests.cs` | 3 | SignalR hub over the in-memory server: a guest can connect but not toggle works, a passenger cannot broadcast, a dispatcher's change reaches every client. |
| `WebApiIntegrationTests.cs` | 29 | The real app over HTTP: home, login and health pages; `/api/route-quote` (200 with a price, 404 for an unknown station, 400 for missing parameters, case-insensitive codes); the real login form with antiforgery token; `/api/bookings` and ticket export (401 anonymous, 403 passenger, 200 dispatcher); 401/403 answered as status codes, not HTML pages; open-redirect protection of the login return URL; logout rejected without an antiforgery token; ticket download as PlainText, Csv and Json, unknown id → 404, unsupported format → 400. |
