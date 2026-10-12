# 2. Read through a shared service account; write as the user

**Status:** Accepted — replaced an earlier design where each page used the logged-in user's
own JMS session for everything.

## Context

The first version gave every page the logged-in user's JMS session, keyed by their
`PHPSESSID` cookie. That caused three problems:

- **Kiosks and background jobs had no user.** The wall TVs and the overnight report worker
  needed JMS data with nobody logged in.
- **Session expiry logged people out.** When JMS expired a session, every page that user had
  open failed, and they had to log in again.
- **JMS outages blanked the app.** With JMS down, every page showed nothing, even though most
  of what people looked at hadn't changed since the last time they loaded it.

## Decision

Split by *whose login* a call needs, and give each side its own lifetime:

- **Reads** go through `JmsReadSession`, one app-wide singleton logged in as a dedicated
  service account. It returns a `CachedJmsReader`, which always asks JMS live but keeps the
  last good result of every query and serves it when JMS can't answer. Identical in-flight
  queries share one request. While the uptime monitor says JMS is down, no live attempt is
  made, so pages load from the cache immediately instead of waiting out timeouts.
- **Writes** go through `JmsUserSession` (per circuit) → `JmsUserSessionPool` (one JMS
  session per username, shared across that person's tabs). JMS then records the right
  person against every change. When JMS expires a session, the pool logs the same client
  back in with the user's stored password (encrypted with ASP.NET Data Protection) instead
  of logging them out.
- The client interfaces encode the split: `IJmsReader` is what the service account may do;
  `IJmsDataClient` adds what must run as a person.

## Consequences

- Kiosks, background workers and pages all read the same way, and a JMS outage degrades the
  app to "slightly stale, with a banner" rather than "empty".
- Session expiry is invisible to users.
- Reads aren't scoped to the user's JMS permissions. That's only acceptable while the app's
  users are staff who can already see this data in JMS; a system with per-user visibility
  rules would need reads to run as the user too.
- The app stores users' JMS passwords (encrypted, reversible). The key ring lives in
  `App_Data/keys`, protected by DPAPI on Windows. Losing it costs everyone one re-entry of
  their password, nothing more.
- Cached results can be up to 24 hours old during a long outage. Polling code that must never
  see an old answer uses `PostUncachedQueryForCsvAsync`.
