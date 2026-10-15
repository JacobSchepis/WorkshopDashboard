# Automation Timesheet Logging

Running reference for **what this app actually logs as JMS timesheet entries, and where**.
Check this file instead of hunting through pages for `TimesheetLoggingService` calls — every
call site gets a row in the table below when it's added.

## Why this exists

`TimesheetLoggingService` posts real entries into JMS's own per-job labour log
(`AddJobTimesheet` — the same one technicians fill in by hand), so time that automation
effectively saves shows up as genuine, auditable hours rather than living only in this app.
The number logged isn't "time saved" — it's **"how long this task should have taken done
manually."** As more of the app's automated actions get instrumented, the day's logged total
grows past a literal 8 hours, reflecting real output instead of just clock time.

## How it works (the policy, in one place)

- **Always ADMIN labour type, $0 charge rate.** This is about proving hours/output, not
  billing — see `TimesheetLoggingService`.
- **Technician is always fixed to one person** (a dedicated automation user in JMS — see the
  `TechnicianUserId`/`TechnicianName` constants in `TimesheetLoggingService`), regardless of
  who's actually logged into WorkshopDashboard. This app's automation is credited to that one
  person's output specifically.
- **Three modes:**
  - `LogForJobAsync` / `LogForJobsAsync` — against one or more real JMS jobs, the same
    hours and notes on each.
  - `LogVariableForJobsAsync` — one entry per job, each with its own hours and notes (e.g. a
    quote email where each job's time depends on what was included for it). Most call sites
    below use this one.
  - `LogAdminTaskAsync` — against a fixed "admin job" (configured via
    `TimesheetSettingsStore`) for work not tied to any specific job.
- **All free text goes into JMS's internal notes field** (`description_internal`) only. The
  customer-facing `description` field is left blank, since job summaries can be printed for
  customers and shouldn't reference internal tooling.
- **Every entry's notes are stamped with the real wall-clock time** the entry was actually
  logged, since the duration itself is a synthetic estimate, not literal elapsed time.
- **Entries never overlap.** JMS rejects overlapping `labour` rows for the same
  technician/day, so entries are chained sequentially starting at 8am (or wherever the
  technician's day already left off — see `TimeSheetService.GetNextAvailableStartTimeAsync`).
- **Equipment is attached automatically when the job has an active unit.** Each job's
  `job_equipment` row(s) with `active_status = 't'` are looked up (via `JobEquipmentLookup`)
  and the first one's `equipment_id` is included on the entry — callers never pass this
  themselves.
- **Optional dedupe via `taskKey`.** Pass a stable key (e.g.
  `"waiting-workshop-tracking-check"`) for actions that can be triggered repeatedly in a day —
  the first call that day posts to JMS, every later call that day with the same key is a
  no-op (`TimeSheetLogResult.AlreadyLoggedToday`). Checked against `TimesheetTaskLogStore`
  before anything is posted.

See `src/Services/Jobs/TimesheetLoggingService.cs` for the full API and header comment.

## "Hours logged today" counter

`TimesheetLoggingService.GetHoursLoggedTodayAsync()` (shown on the Dev Tools > Timesheet Test
page) sums `labour.timesheet_duration` straight from JMS. `labour.start_date` comes back from
raw SQL about 10 hours behind the site's displayed local time (confirmed by comparing a known
entry's raw value against what JMS showed for it — AEST storage/display mismatch), so instead
of proper timezone conversion, the query just shifts the local day's boundaries back by that
same fixed 10 hours (see `JmsQueries.LabourHoursForTodayQuery`). Not DST-aware — deliberately
approximate, since it only needs to hold up for a normal working day.

## Call sites

| Page / trigger | Mode | Task key | Estimated hours | Notes content |
|---|---|---|---|---|
| `WaitingQuoteDetail.razor` — Send (any of the 3 modes: Custom / Predefined / Per job contact) | `LogVariableForJobsAsync` — one entry per job in that particular email, each with its own hours | none (every genuine send should log; no once-per-day dedupe) | Per job: 1 min base + 1 min per extra action that applied (quote PDF attached / job has an approved PO / "Include lifetime repair spend" was on for the send) | `"Job update — quote sent with summary to {recipient}. Also included: {quote PDF attached, PO included (...), lifetime repair spend included}."` (extras list only what actually applied) |
| `InternationalShipping.razor` ("Repair Request" page) — Book Shipment | `LogVariableForJobsAsync` — one entry per selected job, covering the whole workflow that led up to booking (not one entry per Generate button — booking implies the paperwork was already done) | none (a second booking for a job is real repeated work) | Per job: 5 min booking + 3 min per checked equipment item on that job (Equipment tab only — the AU Jobs tab has no equipment/customs step, so it's just the 5 min) | Always lists "Repair request form generated", "Shipping tracking number and unit cost for shipment uploaded to services", plus (Equipment tab only) "DHL customs declaration generated" and "Shipment CSV for DHL booking created" — not conditional on which buttons were actually clicked this session, since booking implies the whole workflow happened. No tracking number or cost values themselves (those already live on the freight service line JMS shows on the job) |
| `BulkJobCreate.razor` — Create Jobs | `LogVariableForJobsAsync` — one entry per successfully created job, only when 2+ jobs were created in the batch | none (a later batch is real repeated work) | 2 min flat per job | `"Job created in bulk batch of N."` + `" Equipment attached: {Model} (SN: {Serial}), ..."` for whichever equipment actually attached successfully to that job (omitted if none) |
| `ShippingChecks.razor` — page load (`GetStuff`) | `LogAdminTaskAsync` — one summary entry, not tied to a job | `"shipping-checks-daily"` — once per day no matter how many times the page is loaded | 2 min per job loaded across all 3 tables (Waiting Arrival + Waiting Outward Shipping + Unit Departed) | `"Loaded N jobs, A waiting arrival, O waiting outward shipping, D unit departed.\nProvided P tracking details.\nM jobs did not have tracking details or failed.\nInformation provided to {viewer}."` — viewer is whoever's actually logged into WorkshopDashboard (resolved via `CurrentUserResolver`, falling back to the bare auth username), distinct from the fixed technician every entry is attributed to |
| `QuoteSubmissionService.SubmitAsync` — the shared submit every AI quoting page (Dorado/Apex/Bayside/Cobalt/Ember/Falcon) funnels through | `LogForJobAsync` — one entry for the job being quoted | none (a resubmission is real repeated work) | 5 min flat | `"Quote services created"` |

A "Test mode — don't send from Scopes" checkbox on `WaitingQuoteDetail.razor` suppresses the
real Gmail send (and its always-on CC to scopes@) entirely, but **not** the timesheet logging
— so the logging can be verified against real jobs without emailing real customers.

## Adding a new call site

1. Inject `TimesheetLoggingService` into the page/service that performs the automated action.
2. Call `LogForJobAsync`/`LogForJobsAsync` (real job(s)) or `LogAdminTaskAsync` (general work)
   with your notes text and estimated hours.
3. If the action can be re-triggered by the user in the same day without doing new work (e.g.
   clicking a button twice), pass a `taskKey` so it only logs once per day.
4. Add a row to the table above — page/trigger, mode, task key (if any), how the estimated
   hours are computed, and what the notes actually say.
