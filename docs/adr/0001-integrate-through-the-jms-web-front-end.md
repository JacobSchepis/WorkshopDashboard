# 1. Integrate with JMS through its own web front end

**Status:** Accepted

## Context

Everything the workshop does lives in its job management system (JMS): a PHP application
over MySQL, hosted by its vendor. It has no API, no webhooks and no database access for
customers. What it does have, like any web app, is a set of pages and forms — and one of
those pages is an equipment export that takes a SQL `SELECT` and returns the result as CSV.

The tools the workshop needed were mostly read-heavy (queues, dashboards, reports, customer
summaries) with a smaller set of writes (adding quote lines, changing statuses, logging time,
uploading documents, creating jobs).

Options considered:

1. **An API or database access from the vendor.** Not available.
2. **Scrape the rendered HTML pages.** Brittle — every layout change breaks a parser, and
   most screens show far less than the app needed.
3. **Use the SQL export page for reads and replay the site's own form posts for writes.**

## Decision

Option 3. `JmsClient` logs in like a browser (session cookie), sends SQL to the export page
for every read, and for writes posts the same form fields JMS's own pages post.

Guard rails around it:

- **One place for SQL.** Every statement is a method on `JmsQueries`; nothing else builds
  SQL. Reviewing what the app can ask JMS means reading one file.
- **One place for URLs.** Every JMS page the app touches is a named endpoint in
  `JmsOptions` (configuration), and the raw senders on `JmsClient` are private — every call
  is a named, typed method.
- **Reads only through the query page.** The one exception is a single `UPDATE` (re-linking
  a purchase-order line to a different job, which no JMS form can do), run as the logged-in
  user and read straight back to confirm.
- **Where JMS's response can't be trusted, writes are checked by reading back.** JMS can
  answer a rejected form post with a 200 and the same form again; the timesheet logger, for
  example, compares the job's labour rows before and after.

## Consequences

- The app could be built at all, without waiting on the vendor, and it can answer questions
  JMS's own screens can't (cross-job queues, ageing, per-customer roll-ups) because it has
  the full schema to query.
- The app depends on JMS's schema and form fields, which the vendor can change without
  notice. Keeping every query and endpoint in one place each keeps the blast radius of such
  a change small, and the read-back checks catch a form that stops working.
- The query page accepts any SQL, so the app holds more access than it uses. Reads run under
  a dedicated service account (see [ADR 0002](0002-shared-read-session-per-user-writes.md))
  so they're attributable and can be revoked on their own.
- SQL is built as strings — the endpoint has no parameters. Values are integers or escaped
  strings; keeping that escaping in one helper matters, and is the first thing to check in a
  review of `JmsQueries`.
- Every JMS call is an HTTP round trip with JMS's latency and availability, which drove
  [ADR 0002](0002-shared-read-session-per-user-writes.md) and
  [ADR 0003](0003-queue-writes-in-an-outbox.md).
