# 5. Fake JMS at the HTTP transport for demo mode

**Status:** Accepted

## Context

To publish the app, it had to run for someone with no access to the real JMS, no OpenAI key
and no courier, Google or monday.com accounts — ideally with one command. It also had to be
the real app, not a mock-up: the interesting code is the JMS layer (sessions, caching, the
outbox) and the seventy-odd SQL queries that drive every page.

Options considered:

1. **Mock the service layer** — fake `WorkshopQueueLookup`, `DashboardLookup` and so on.
   Dozens of fakes, and none of the JMS layer or the SQL would run.
2. **Fake `IJmsReader` / `IJmsDataClient`.** Fewer fakes, but sessions, cookies, CSV
   parsing, the cache and the outbox would all be bypassed, and the SQL still wouldn't run.
3. **Fake JMS itself, underneath the real HTTP client.**

## Decision

Option 3. `JmsClient` takes a `JmsTransport` that supplies its `HttpMessageHandler`. Live,
that's the network. In demo mode it's `DemoJms`, an in-process handler that answers JMS's
endpoints the way JMS does: it hands out and checks session cookies on login, runs posted SQL,
returns CSV, accepts form posts and applies them, and serves placeholder PDFs.

The SQL runs on an in-memory SQLite database built from a copy of the JMS schema and seeded
with [Bogus](https://github.com/bchavez/Bogus) (fixed seed, so every run is the same). The
queries are MySQL, so `MySqlToSqlite` rewrites the few constructs SQLite can't parse
(`INTERVAL` arithmetic, `GROUP_CONCAT ... SEPARATOR`) and registers the MySQL functions it
lacks (`CONCAT`, `NOW`, `CURDATE`, `DATEDIFF`) — only what `JmsQueries` actually uses.

The other integrations are faked at their existing interfaces, since there's nothing deeper
worth exercising: `DemoJsonCompletionClient` (answers from the requested JSON schema),
`DemoCarrierTracker`, `DemoMailOutbox` (keeps sent mail in memory), `DemoMonday`,
`DemoCustomerCredit`.

`DemoMode` is a single configuration switch, on by default, that changes only what's
registered in DI.

## Consequences

- `dotnet run` gives the whole app — logins, every page, every query, writes that change the
  data — with no secrets.
- The demo is a test harness too. `JmsQueriesRunOnDemoTests` calls every `JmsQueries`
  method through reflection and runs it on the demo database, so a new MySQL-only construct
  fails CI, and the session/cache/outbox tests run against the same kind of fake over real
  HTTP.
- The translator is deliberately incomplete — it's a list of rewrites, not a SQL parser — and
  needs a new entry when a query uses a new MySQL-ism. The test makes that obvious.
- The seed data has to be shaped like real data for pages to look right: stalled jobs for
  the stale queue, purchase orders that match monday.com items, one deliberate mismatch for
  the reconciliation view. That's maintained by hand in `DemoSeeder`.
