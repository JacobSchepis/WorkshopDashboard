# 3. Queue JMS writes in a database outbox

**Status:** Accepted

## Context

With reads surviving JMS outages ([ADR 0002](0002-shared-read-session-per-user-writes.md)),
writes were the remaining failure: a technician who'd just priced a quote, or marked five jobs
worked, got an error and had to come back later and redo it. JMS outages were common enough that
the app has a page just for tracking them.

Two properties made a naive retry unsafe:

- **Order matters.** JMS prints a quote's lines in the order they were created; a retried
  first line landing after the second scrambles the customer's quote.
- **"Failed" isn't always failed.** A timeout or a 5xx may come after JMS has already saved
  the row. Blindly retrying adds a second quote line or a second timesheet entry.

## Decision

Writes that can wait (`JmsWrite` subclasses: service lines, status changes, timesheet
entries, notes) go through `JmsWriteQueue.SubmitAsync`:

- If JMS is up and this user has nothing queued, **send it now** — the normal case, no
  behaviour change.
- Otherwise **save it to the `JmsOutbox` table** (serialised write, username, attempts,
  next-attempt time) and tell the caller it's queued. The page shows that; `/jms-pending`
  lists everything waiting.
  - JMS down, or the user's session can't be established → queue.
  - The user already has queued writes → queue behind them, preserving order.
  - The send failed in a way JMS might not have seen (timeout, 5xx) → queue with
    `NeedsCheck = true`.
- A refusal from JMS itself (4xx) isn't an outage — it's returned as a failure straight away
  and not queued.

`JmsWriteQueueWorker` drains the table per user, in id order, as that user, with backoff. A
`NeedsCheck` item first asks the write whether it's already applied
(`JmsWrite.AlreadyAppliedAsync` — e.g. "is there already a service line on this job with this
exact description, created in the last few minutes?") and is deleted without sending if so.
The worker wakes when something is queued, when JMS comes back up, and every 20 seconds.

## Consequences

- People can keep working through an outage; their changes appear in JMS a minute after it
  comes back.
- Writes are at-least-once at the transport level but effectively once in JMS, as long as
  each write type can answer "am I already applied?". New write types have to implement that
  check or accept the risk of a duplicate after a timeout.
- A queued write can't return data the caller needs straight away (a new job id, say), so
  only fire-and-forget writes are queueable. Job creation, uploads and logins still need JMS
  to be up.
- Something that was valid when queued may be refused by the time it's sent (the job was
  closed in the meantime). Those are marked failed and shown on `/jms-pending` with JMS's
  reason; transient errors back off up to 15 minutes and give up after 50 attempts.
