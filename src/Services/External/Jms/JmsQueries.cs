using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Services.Shipping;

namespace WorkshopDashboard.Services.External.Jms;

// ═══════════════════════════════════════════════════════════════════════════
//  JmsQueries
//
//  Every raw SQL string sent to the JMS query endpoint (Jms.PostQueryForCsvAsync),
//  in one place. Pages build parameters (job ids, company ids, status names,
//  exclusion clauses) and call a method here to get back the query text — they
//  never assemble SQL themselves. This does NOT execute anything; callers still
//  run the returned string through their own IJmsDataClient.
//
//  Query text is carried over verbatim from where it used to live (comments
//  below note the originating page) except where several pages built the exact
//  same query — those were collapsed into one shared method.
// ═══════════════════════════════════════════════════════════════════════════

public static class JmsQueries
{
    // ── Scope job clause ─────────────────────────────────────────────────────
    //
    // The single definition of "is this a scope (workshop) job" — see
    // ScopeJobStore for where the exclusion lists come from. Every query that
    // lists workshop jobs (Dashboard, WaitingWorkshopAssignment/Processing,
    // WaitingQuoteConfirmation, StaleJobsFiltered, WaitingQuoteReport) builds
    // its WHERE clause up to some point and then appends this — it always
    // starts with "AND", so it's meant to be tacked onto the end of an
    // existing WHERE, never used as the WHERE clause's first condition.
    //
    // Assumes the query aliases the job table as `j`. Model exclusion needs
    // one of two shapes depending on whether the query has already collapsed
    // to one row per job:
    //  - jobLevelModelExclusion: false (default) — the query GROUP BYs job_id
    //    (or is otherwise already one-row-per-job), so filtering the row on
    //    `m.model_id` (assumed already LEFT JOINed) is enough.
    //  - jobLevelModelExclusion: true — the query returns MULTIPLE rows per
    //    job (e.g. WaitingQuoteReport's one-row-per-quote shape), so an
    //    `m.model_id` filter would only drop the one row for the excluded
    //    equipment and leave the job's other rows behind. A NOT EXISTS across
    //    job_equipment instead drops the job entirely if ANY of its equipment
    //    is an excluded model.

    // Scope, defined ONCE. These were previously inlined into each query and drifted
    // apart: the counts query scoped on (34, 16) while the stale query scoped on 16
    // alone, so the Stale card was always drawn from a smaller set than the overdue
    // badges below it.
    public static readonly int[] WorkshopStatusIds = { 34, 16 };

    public static string ScopeJobClause(ScopeJobExclusions exclusions, bool jobLevelModelExclusion = false)
    {
        var clause = @"
            AND EXISTS (
                SELECT 1 FROM job_status_history jshw
                WHERE jshw.job_id = j.job_id AND jshw.job_status_id IN (" + string.Join(", ", WorkshopStatusIds) + @")
            )";

        // NOT IN () is a syntax error, and NOT IN silently drops NULLs a LEFT JOIN
        // produces for equipment-less jobs, so every branch below guards both.

        if (exclusions.JobIds.Count > 0)
            clause += " AND j.job_id NOT IN (" + string.Join(", ", exclusions.JobIds) + ")";

        if (exclusions.CompanyIds.Count > 0)
            clause += " AND (j.owner_company_id IS NULL OR j.owner_company_id NOT IN (" + string.Join(", ", exclusions.CompanyIds) + "))";

        if (exclusions.ModelIds.Count > 0)
        {
            clause += jobLevelModelExclusion
                ? $@" AND NOT EXISTS (
                    SELECT 1 FROM job_equipment jex
                    JOIN equipment eex ON eex.equipment_id = jex.equipment_id
                    WHERE jex.job_id = j.job_id
                    AND eex.model_id IN ({string.Join(", ", exclusions.ModelIds)})
                )"
                : " AND (m.model_id IS NULL OR m.model_id NOT IN (" + string.Join(", ", exclusions.ModelIds) + "))";
        }

        return clause;
    }

    // ── Overview (Overview.razor) ───────────────────────────────────────────

    // Statuses meaning the job is finished and should not count as open:
    // Completed, Invoiced and Unit Decommissioned.
    public static readonly int[] ClosedStatusIds = { 2, 3, 21 };

    /// <summary>Per-status totals + overdue counts. Inner query reduces to one row per job
    /// (excluded models stripped in the WHERE); outer query aggregates by status.
    ///
    /// "Overdue" is driven by job.last_worked_date (when a service was last added/edited
    /// on the job — see WaitingWorkshopProcessing.razor's "Mark Worked" action) rather
    /// than the job's last status-change date: a job can sit in the same status for
    /// weeks while still being actively worked, so status-change age was flagging jobs
    /// as stale that weren't. A job that has never had a service logged (last_worked_date
    /// IS NULL) counts as overdue too — untouched is at least as stale as "touched a
    /// while ago".</summary>
    public static string DashboardCountsQuery(int overdueAfterDays, ScopeJobExclusions exclusions)
    {
        var openScope = "WHERE j.job_status_id NOT IN (" + string.Join(", ", ClosedStatusIds) + ")";

        return @"
            SELECT
                t.job_status_id,
                t.job_status,
                COUNT(*) AS total,
                SUM(CASE WHEN t.last_worked IS NULL OR t.last_worked < DATE_SUB(NOW(), INTERVAL " + overdueAfterDays + @" DAY) THEN 1 ELSE 0 END) AS overdue
            FROM (
                SELECT
                    j.job_id,
                    j.job_status_id,
                    js.job_status,
                    j.last_worked_date AS last_worked
                FROM job j
                LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
                LEFT JOIN job_equipment je ON je.job_id = j.job_id AND je.active_status = 't'
                LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
                LEFT JOIN model m ON m.model_id = e.model_id
                " + openScope + @"
                " + ScopeJobClause(exclusions) + @"
                GROUP BY j.job_id, j.job_status_id, js.job_status, j.last_worked_date
            ) t
            GROUP BY t.job_status_id, t.job_status
            ORDER BY total DESC";
    }

    // ── Overview.razor (Highlighted Jobs) ───────────────────────────────────
    //
    // Same "job.notes / job_status / owner / equipment" shape as StaleJobsQuery,
    // but scoped to an explicit id list (the user's pinned jobs) rather than any
    // status/staleness condition. Callers must skip this entirely for an empty
    // id list ("IN ()" is invalid SQL) — same convention as CompanyNamesQuery.

    public static string HighlightedJobsQuery(IEnumerable<int> jobIds)
    {
        var idList = string.Join(",", jobIds);
        return $@"
            SELECT
                j.job_id,
                j.notes AS job_notes,
                js.job_status,
                c.company_name AS owner,
                GROUP_CONCAT(DISTINCT CONCAT(manu.company_name, ' ', m.model_name_no, ' (', e.serial_no, ')') SEPARATOR '; ') AS equipment
            FROM job j
            LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
            LEFT JOIN company c ON c.company_id = j.owner_company_id
            LEFT JOIN job_equipment je ON je.job_id = j.job_id AND je.active_status = 't'
            LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
            LEFT JOIN model m ON m.model_id = e.model_id
            LEFT JOIN company manu ON manu.company_id = m.manufacture_id
            WHERE j.job_id IN ({idList})
            GROUP BY j.job_id, j.notes, js.job_status, c.company_name";
    }

    // ── JobNotesService ──────────────────────────────────────────────────────
    //
    // Tracking notes are plain-text services of type NOTE (service_type_id = 11 — see
    // JmsClient.Services.cs for the full type table) whose description starts with
    // JobNotesService.TrackingPrefix ("[Tracking] "). Only those are the app's notes —
    // every other NOTE row (typed into JMS, ETA rows, the model watchlist's marker) is
    // left out. MySQL's LIKE treats '[' literally, so the prefix needs no escaping.

    public static string JobNotesQuery(IEnumerable<int> jobIds) => $@"
            SELECT s.job_id, s.service_id, s.description, s.creation_date, u.first_name, u.last_name
            FROM services s
            LEFT JOIN `user` u ON u.user_id = s.user_id
            WHERE s.job_id IN ({string.Join(",", jobIds)})
              AND s.service_type_id = 11
              AND s.deleted = 'f'
              AND s.description LIKE '[Tracking]%'
            ORDER BY s.creation_date DESC, s.service_id DESC";

    // ── BulkCreateRecall.razor ──────────────────────────────────────────────

    public static string ExistingRecallReferencesQuery(IEnumerable<string> refs)
    {
        var inList = string.Join(",", refs.Select(x => "'" + JmsSql.Escape(x) + "'"));
        return $"SELECT tga_recall_reference FROM recall WHERE tga_recall_reference IN ({inList})";
    }

    // ── BulkStatusUpdate.razor ──────────────────────────────────────────────

    public static string JobsByIdsWithEquipmentQuery(IEnumerable<int> jobIds)
    {
        var idList = string.Join(", ", jobIds);
        return $@"
            SELECT j.job_id, owner.company_name AS owner_company_name,
                   manufacturer.company_name AS manufacturer_name,
                   m.model_name_no, e.serial_no
            FROM job j
            LEFT JOIN job_equipment je ON j.job_id = je.job_id
            LEFT JOIN equipment e ON je.equipment_id = e.equipment_id
            LEFT JOIN model m ON e.model_id = m.model_id
            LEFT JOIN company manufacturer ON m.manufacture_id = manufacturer.company_id
            LEFT JOIN company owner ON j.owner_company_id = owner.company_id
            WHERE j.job_id IN ({idList})
        ";
    }

    // ── InternationalShipping.razor ─────────────────────────────────────────

    public static string EquipmentByJobIdsQuery(IEnumerable<int> jobIds)
    {
        var idList = string.Join(", ", jobIds);
        return $@"
            SELECT j.job_id, e.serial_no, owner.company_name AS owner_name, j.notes,
                   manufacturer.company_name AS manufacturer_name, m.model_id, m.model_name_no,
                   j.job_priority_id
                FROM job j
                LEFT JOIN job_equipment je ON j.job_id = je.job_id
                LEFT JOIN equipment e ON je.equipment_id = e.equipment_id
                LEFT JOIN model m ON e.model_id = m.model_id
                LEFT JOIN company manufacturer ON m.manufacture_id = manufacturer.company_id
                LEFT JOIN company owner ON j.owner_company_id = owner.company_id
                WHERE j.job_id IN ({idList})
                AND je.active_status = 't'
            ";
    }

    // ── InternationalShipping.razor (AU/Jobs tab) ───────────────────────────

    public static string JobsByIdsQuery(IEnumerable<int> jobIds)
    {
        var idList = string.Join(", ", jobIds);
        return $@"
            SELECT j.job_id, j.notes, j.job_priority_id, owner.company_name AS owner_name
                FROM job j
                LEFT JOIN company owner ON j.owner_company_id = owner.company_id
                WHERE j.job_id IN ({idList})
            ";
    }

    // ── Shared by Forms/JobListSection.razor and ShippingChecks.razor ──────
    //
    // filterHistoryToStatus: true  = only include history rows matching jobStatusId
    //                                (e.g. Waiting Outward Shipping)
    //                         false = include all history rows (e.g. Unit Departed, so
    //                                connotes from earlier statuses are visible)

    public static string JobsWithHistoryQuery(int jobStatusId, bool filterHistoryToStatus)
    {
        var historyFilter = filterHistoryToStatus ? $"AND jsh.job_status_id = {jobStatusId}" : "";

        return $@"
            SELECT j.job_id, j.notes as job_notes, u.first_name, js.job_status, jsh.notes as history_notes, jsh.creation_date, c.company_name, m.model_name_no
            FROM job j
            LEFT JOIN job_status_history jsh ON j.job_id = jsh.job_id
            LEFT JOIN job_status js ON jsh.job_status_id = js.job_status_id
            LEFT JOIN `user` u ON u.user_id = jsh.user_id
            LEFT JOIN company c ON j.owner_company_id = c.company_id
            LEFT JOIN job_equipment je ON j.job_id = je.job_id
            LEFT JOIN equipment e ON je.equipment_id = e.equipment_id
            LEFT JOIN model m ON e.model_id = m.model_id
            WHERE j.job_status_id = {jobStatusId}
            {historyFilter}";
    }

    // ── Shared by QuoteApex.razor, QuoteFalcon.razor, QuoteDorado.razor ────────────

    public static string JobEquipmentQuery(int jobId) => $@"
        SELECT je.equipment_id, e.serial_no, e.generic_name
        FROM job_equipment je
        LEFT JOIN equipment e ON je.equipment_id = e.equipment_id
        WHERE je.job_id = {jobId}
            AND je.active_status = 't'";

    public static string OwnerCompanyIdQuery(int jobId) =>
        "SELECT owner_company_id FROM job WHERE job_id = " + jobId;

    /// <summary>A job's current status id — used by TimeSheetService to resubmit the job's
    /// existing status on AddJobTimesheet's form (which requires one), so logging an
    /// automation timesheet entry never moves the job's workflow state.</summary>
    public static string JobStatusIdQuery(int jobId) =>
        "SELECT job_status_id FROM job WHERE job_id = " + jobId;

    /// <summary>Every labour row on a job, no user/date filter — diagnostic-only, used to see
    /// what's actually in the table (real user_id/start_date values) when
    /// LabourCountForJobQuery's filtered count doesn't match what the job's own timesheet page
    /// visibly shows.</summary>
    public static string LabourRowsForJobQuery(int jobId) => $@"
        SELECT labour_id, user_id, start_date, end_date, description
        FROM labour
        WHERE job_id = {jobId}
        ORDER BY labour_id";

    /// <summary>The newest timesheet entry on a job that has any text, by anyone — the rigid
    /// technician's notes on the in-house rigid quote page (QuoteInHouseRigid.razor).</summary>
    public static string LatestTimesheetNoteForJobQuery(int jobId) => $@"
        SELECT labour_id, start_date, description
        FROM labour
        WHERE job_id = {jobId}
          AND description IS NOT NULL
          AND TRIM(description) <> ''
        ORDER BY start_date DESC, labour_id DESC
        LIMIT 1";

    /// <summary>Total timesheet_duration logged by a technician on a given local day — used for
    /// TimesheetTest.razor's "hours logged today" counter. labour.start_date comes back ~10
    /// hours behind the site's displayed local time (AEST storage vs display was confirmed by
    /// comparing a known entry's raw value to what JMS showed for it), so the local day's
    /// boundaries are shifted back by that same fixed amount rather than applying timezone
    /// conversion to every row. A fixed offset (not DST-aware) is good enough for a normal
    /// 8am-4pm work day.</summary>
    public static string LabourHoursForTodayQuery(int userId, DateTime localDate)
    {
        var from = localDate.Date.AddHours(-10);
        var to = localDate.Date.AddDays(1).AddHours(-10);

        return $@"
            SELECT SUM(timesheet_duration) AS total_hours
            FROM labour
            WHERE user_id = {userId} AND start_date >= '{from:yyyy-MM-dd HH:mm:ss}' AND start_date < '{to:yyyy-MM-dd HH:mm:ss}'";
    }

    /// <summary>Owner company name for a company id already resolved via OwnerCompanyIdQuery —
    /// used by QuoteDorado.razor to label the "Owner" field once _companyId is known.</summary>
    public static string CompanyNameQuery(int companyId) =>
        $"SELECT company_name FROM company WHERE company_id = {companyId}";

    /// <summary>Every active (non-deleted) service line already on the job, joined to
    /// service_type for a human-readable type name. Feeds QuoteDorado.razor's "Services in
    /// Job" list and its "Apply Credit" picker — a credit line must match the
    /// service_type_id of whichever line it's offsetting, so the picker needs that id
    /// alongside the display name.</summary>
    public static string JobServicesQuery(int jobId) => $@"
        SELECT s.service_id, s.service_type_id, st.service_type AS service_type_name,
               s.description, s.cost_price, s.quantity, s.creation_date
        FROM services s
        LEFT JOIN service_type st ON st.service_type_id = s.service_type_id
        WHERE s.job_id = {jobId}
          AND s.deleted = 'f'
        ORDER BY s.creation_date ASC";

    // ── UploadDocument.razor ────────────────────────────────────────────────

    public static string JobInfoQuery(long jobId) => @"SELECT
                    j.job_id,
                    c.company_name,
                    j.notes            AS job_notes,
                    m.model_name_no,
                    e.serial_no,
                    jsh.job_status_id,
                    jsh.creation_date,
                    jsh.notes          AS status_notes
                FROM job j
                LEFT JOIN company c
                    ON c.company_id = j.owner_company_id
                LEFT JOIN job_equipment je
                    ON je.job_id = j.job_id
                   AND je.active_status = 't'
                LEFT JOIN equipment e
                    ON e.equipment_id = je.equipment_id
                LEFT JOIN model m
                    ON m.model_id = e.model_id
                LEFT JOIN job_status_history jsh
                    ON jsh.job_id = j.job_id
                WHERE j.job_id = " + jobId + @"
                ORDER BY jsh.creation_date ASC";

    // ── StaleJobs.razor ─────────────────────────────────────────────────────

    public static string StaleJobsQuery() => @"
            SELECT
                j.job_id,
                j.notes AS fault,
                js.job_status,
                c.company_name AS owner,
                CONCAT(manu.company_name, ' ', m.model_name_no, ' (', e.serial_no, ')') AS equipment,
                MAX(jsh.creation_date) AS last_status_change
            FROM job j
            LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
            LEFT JOIN company c ON c.company_id = j.owner_company_id
            LEFT JOIN job_equipment je ON je.job_id = j.job_id AND je.active_status = 't'
            LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
            LEFT JOIN model m ON m.model_id = e.model_id
            LEFT JOIN company manu ON manu.company_id = m.manufacture_id
            LEFT JOIN job_status_history jsh ON jsh.job_id = j.job_id
            WHERE j.job_status_id NOT IN (2, 9, 10, 30)
            AND EXISTS (
                SELECT 1 FROM job_status_history jsh2
                WHERE jsh2.job_id = j.job_id
                AND jsh2.job_status_id = 16
            )
            GROUP BY j.job_id, j.notes, js.job_status, c.company_name, e.serial_no, m.model_name_no, manu.company_name
            HAVING MAX(jsh.creation_date) < DATE_SUB(NOW(), INTERVAL 2 WEEK)
            ORDER BY last_status_change ASC";

    // ── StaleJobsFiltered.razor ─────────────────────────────────────────────
    //
    // Scope/exclusions now applied in SQL via ScopeJobClause — this used to take
    // no parameters and let the page filter excluded models out of the CSV rows
    // itself, which meant job/company exclusions weren't possible at all.

    public static string StaleJobsFilteredQuery(ScopeJobExclusions exclusions) => @"
            SELECT
                j.job_id,
                j.notes AS fault,
                js.job_status,
                c.company_name AS owner,
                m.model_id AS model_id,
                CONCAT(manu.company_name, ' ', m.model_name_no, ' (', e.serial_no, ')') AS equipment,
                MAX(jsh.creation_date) AS last_status_change
            FROM job j
            LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
            LEFT JOIN company c ON c.company_id = j.owner_company_id
            LEFT JOIN job_equipment je ON je.job_id = j.job_id AND je.active_status = 't'
            LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
            LEFT JOIN model m ON m.model_id = e.model_id
            LEFT JOIN company manu ON manu.company_id = m.manufacture_id
            LEFT JOIN job_status_history jsh ON jsh.job_id = j.job_id
            WHERE j.job_status_id NOT IN (2, 29, 30, 10)
            " + ScopeJobClause(exclusions) + @"
            GROUP BY j.job_id, j.notes, js.job_status, c.company_name, m.model_id, e.serial_no, m.model_name_no, manu.company_name
            HAVING MAX(jsh.creation_date) < DATE_SUB(NOW(), INTERVAL 1 WEEK)
            ORDER BY last_status_change ASC";

    // ── WaitingWorkshopAssignment.razor ─────────────────────────────────────
    //
    // Correlated subquery grabs the latest DHL or TNT tracking row from 'services'.
    // The raw description (e.g. "DHL: 123..." / "TNT: 456...") is parsed carrier-side
    // by the page. Kept as its own query (not merged into the other workshop-status
    // queries) because of the extra tracking_raw column.

    // TrackingService.KnownCarrierPrefixes is the single source of truth for "which
    // service rows count as tracking" — this can't quietly drift from what
    // TrackingService.ExtractTracking actually parses back out of them.
    private static string TrackingCarrierLikeClause(string column) =>
        "(" + string.Join(" OR ", TrackingService.KnownCarrierPrefixes.Select(c => $"{column} LIKE '{c}:%'")) + ")";

    public static string WaitingWorkshopAssignmentQuery(ScopeJobExclusions exclusions)
    {
        var safe = JmsSql.Escape("Waiting Workshop Assignment");

        return @"
        SELECT
            j.job_id,
            j.notes AS fault,
            js.job_status,
            c.company_name AS owner,
            GROUP_CONCAT(DISTINCT CONCAT(manu.company_name, ' ', m.model_name_no, ' (', e.serial_no, ')') SEPARATOR '; ') AS equipment,
            MAX(jsh.creation_date) AS last_status_change,
            (
                SELECT s.description
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 10
                  AND " + TrackingCarrierLikeClause("s.description") + @"
                  AND s.deleted = 'f'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS tracking_raw
        FROM job j
        LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
        LEFT JOIN company c ON c.company_id = j.owner_company_id
        LEFT JOIN job_equipment je ON je.job_id = j.job_id
        LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
        LEFT JOIN model m ON m.model_id = e.model_id
        LEFT JOIN company manu ON manu.company_id = m.manufacture_id
        LEFT JOIN job_status_history jsh ON jsh.job_id = j.job_id
        WHERE js.job_status = '" + safe + @"'
        " + ScopeJobClause(exclusions) + @"
        GROUP BY j.job_id, j.notes, js.job_status, c.company_name
        ORDER BY last_status_change ASC";
    }

    // ── WorkshopFinalisationComplete.razor ───────────────────────────────────
    //
    // The repairer has finished the repair and is shipping the unit back to the workshop — same shape
    // as WaitingWorkshopAssignmentQuery (repairer -> workshop leg) but reading service_type_id 11
    // (NOTE, $0) instead of 10 (FREIGHT): JMS isn't charging anyone for this leg, just
    // recording the tracking number the repairer emailed through.
    public static string WorkshopFinalisationCompleteQuery(ScopeJobExclusions exclusions)
    {
        var safe = JmsSql.Escape("Workshop Finalisation Complete");

        return @"
        SELECT
            j.job_id,
            j.notes AS fault,
            js.job_status,
            c.company_name AS owner,
            GROUP_CONCAT(DISTINCT CONCAT(manu.company_name, ' ', m.model_name_no, ' (', e.serial_no, ')') SEPARATOR '; ') AS equipment,
            MAX(jsh.creation_date) AS last_status_change,
            (
                SELECT s.description
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 11
                  AND " + TrackingCarrierLikeClause("s.description") + @"
                  AND s.deleted = 'f'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS tracking_raw,
            (
                -- Same row as tracking_raw above (kept as a second correlated subquery,
                -- not a join, so the LIMIT 1 stays tied to the same ORDER BY) — its
                -- service_id, so a wrongly-entered tracking note can be deleted by id
                -- rather than guessing which of the job's NOTE services it was.
                SELECT s.service_id
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 11
                  AND " + TrackingCarrierLikeClause("s.description") + @"
                  AND s.deleted = 'f'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS tracking_service_id
        FROM job j
        LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
        LEFT JOIN company c ON c.company_id = j.owner_company_id
        LEFT JOIN job_equipment je ON je.job_id = j.job_id
        LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
        LEFT JOIN model m ON m.model_id = e.model_id
        LEFT JOIN company manu ON manu.company_id = m.manufacture_id
        LEFT JOIN job_status_history jsh ON jsh.job_id = j.job_id
        WHERE js.job_status = '" + safe + @"'
        " + ScopeJobClause(exclusions) + @"
        GROUP BY j.job_id, j.notes, js.job_status, c.company_name
        ORDER BY last_status_change ASC";
    }

    // ── WaitingWorkshopProcessing.razor ─────────────────────────────────────
    //
    // Dedicated query (each of these workshop-status pages used to share one
    // WorkshopStatusJobsQuery, since removed) because this page additionally
    // needs: the date the job FIRST entered status 30 (not just its most recent
    // history row, which is what a plain MAX(jsh.creation_date) gives you),
    // job.last_worked_date for the staleness check, any ETA note recorded
    // against the job as a service_type_id = 11 row, and whether a PO has been
    // uploaded to the job's resources. ETA rows are written via
    // AddJobService (see JmsClient.Services.cs' AddFreightServiceAsync for the
    // same endpoint/form shape) with description "ETA - yyyy-MM-dd"; the latest
    // undeleted one wins, same pattern as the DHL/TNT tracking lookup above.
    // Uploaded resources (UploadDocument.razor's JobResourceUpload) land in
    // online_resource, linked to their job via online_resource_link where
    // link_table_key_name = 'job_id' and link_table_key_value = the job id; a PO
    // is one whose title, trimmed of stray leading/trailing whitespace, is "PO"
    // or "Purchase Order". job.order_number comes along too: a job paid for with
    // credit has "Credit Applied" there instead of a PO to upload.

    public static string WaitingWorkshopProcessingQuery(ScopeJobExclusions exclusions) => @"
        SELECT
            j.job_id,
            j.notes AS fault,
            js.job_status,
            c.company_name AS owner,
            GROUP_CONCAT(DISTINCT CONCAT(manu.company_name, ' ', m.model_name_no, ' (', e.serial_no, ')') SEPARATOR '; ') AS equipment,
            j.last_worked_date,
            j.order_number,
            (
                SELECT MIN(jsh2.creation_date)
                FROM job_status_history jsh2
                WHERE jsh2.job_id = j.job_id AND jsh2.job_status_id = 30
            ) AS first_set_date,
            (
                SELECT s.description
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 11
                  AND s.deleted = 'f'
                  AND s.description LIKE 'ETA - %'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS eta_raw,
            (
                SELECT s.service_id
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 11
                  AND s.deleted = 'f'
                  AND s.description LIKE 'ETA - %'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS eta_service_id,
            (
                SELECT s.description
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 11
                  AND s.deleted = 'f'
                  AND s.description LIKE 'Approval%'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS approval_raw,
            (
                SELECT s.service_id
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 11
                  AND s.deleted = 'f'
                  AND s.description LIKE 'Approval%'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS approval_service_id,
            (
                SELECT COUNT(*)
                FROM online_resource_link orl
                JOIN online_resource o ON o.online_resource_id = orl.online_resource_id
                WHERE orl.link_table_key_name = 'job_id'
                  AND orl.link_table_key_value = j.job_id
                  AND TRIM(o.title) IN ('PO', 'Purchase Order')
            ) AS po_count
        FROM job j
        LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
        LEFT JOIN company c ON c.company_id = j.owner_company_id
        LEFT JOIN job_equipment je ON je.job_id = j.job_id
        LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
        LEFT JOIN model m ON m.model_id = e.model_id
        LEFT JOIN company manu ON manu.company_id = m.manufacture_id
        WHERE j.job_status_id = 30
        " + ScopeJobClause(exclusions) + @"
        GROUP BY j.job_id, j.notes, js.job_status, c.company_name, j.last_worked_date, j.order_number
        ORDER BY first_set_date ASC";

    // ── WaitingQuoteConfirmation.razor ──────────────────────────────────────
    //
    // Dedicated query for the same reason as WaitingWorkshopProcessingQuery
    // above: it needs the date the job FIRST entered status 10 (Waiting Quote
    // Confirmation), not just its most recent history row, plus
    // job.last_worked_date for the "Mark Worked" staleness touch (see
    // WaitingWorkshopProcessing.razor's TouchLastWorkedAsync).

    public static string WaitingQuoteConfirmationQuery(ScopeJobExclusions exclusions) => @"
        SELECT
            j.job_id,
            j.notes AS fault,
            js.job_status,
            c.company_name AS owner,
            j.owner_company_id,
            GROUP_CONCAT(DISTINCT CONCAT(manu.company_name, ' ', m.model_name_no, ' (', e.serial_no, ')') SEPARATOR '; ') AS equipment,
            j.last_worked_date,
            (
                SELECT MIN(jsh2.creation_date)
                FROM job_status_history jsh2
                WHERE jsh2.job_id = j.job_id AND jsh2.job_status_id = 10
            ) AS first_set_date,
            (
                SELECT s.description
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 11
                  AND s.deleted = 'f'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS followup_raw,
            (
                SELECT s.service_id
                FROM services s
                WHERE s.job_id = j.job_id
                  AND s.service_type_id = 11
                  AND s.deleted = 'f'
                ORDER BY s.creation_date DESC
                LIMIT 1
            ) AS followup_service_id,
            (
                SELECT GROUP_CONCAT(
                    CONCAT(jq.job_quote_id, '|', jq.total_amount, '|', DATE(jq.date_created))
                    ORDER BY jq.date_created SEPARATOR ';;'
                )
                FROM job_quote jq
                WHERE jq.job_id = j.job_id AND jq.accepted = 'f'
            ) AS quotes_raw
        FROM job j
        LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
        LEFT JOIN company c ON c.company_id = j.owner_company_id
        LEFT JOIN job_equipment je ON je.job_id = j.job_id
        LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
        LEFT JOIN model m ON m.model_id = e.model_id
        LEFT JOIN company manu ON manu.company_id = m.manufacture_id
        WHERE j.job_status_id = 10
        " + ScopeJobClause(exclusions) + @"
        GROUP BY j.job_id, j.notes, js.job_status, c.company_name, j.owner_company_id, j.last_worked_date
        ORDER BY first_set_date ASC";

    /// <summary>How many live service lines on a job match this type and exact description and
    /// were created in the last few minutes (by JMS's own clock) — JmsWriteQueue's check for
    /// whether a send it didn't see the reply to actually landed.</summary>
    public static string MatchingServiceCountQuery(int jobId, int serviceTypeId, string description, int withinMinutes) => $@"
        SELECT COUNT(*) AS n
        FROM services
        WHERE job_id = {jobId}
          AND service_type_id = {serviceTypeId}
          AND deleted = 'f'
          AND description = '{JmsSql.Escape(description)}'
          AND creation_date >= NOW() - INTERVAL {withinMinutes} MINUTE";

    public static string LatestServiceIdQuery(int jobId, int serviceTypeId) => $@"
        SELECT service_id
        FROM services
        WHERE job_id = {jobId}
          AND service_type_id = {serviceTypeId}
          AND deleted = 'f'
        ORDER BY service_id DESC
        LIMIT 1";

    // ── WaitingQuoteReport (WaitingQuote.razor + WaitingQuoteDetail.razor) ──
    //
    // The query returns one row per (job × quote). Because job_quote.accepted = 'f' is
    // filtered as part of the JOIN condition (not WHERE), jobs with no unaccepted quotes
    // still appear once (with null quote fields) rather than being dropped — matches the
    // old LEFT JOIN behaviour. Contact: job.contact_name is an id into company_people.

    // approved_po_numbers / eta_raw / eta_service_id follow the same conventions as
    // WaitingWorkshopProcessingQuery above: a quote's "PO number" is job_quote.order_no,
    // and ETA is the latest undeleted "ETA - yyyy-MM-dd" service_type_id = 11 note. A quote
    // is sometimes "approved" with a placeholder PO like "requote" just to clear it out of
    // the outstanding-quote list rather than a genuine PO — those are excluded entirely
    // (not just flagged), so approved_po_numbers is every OTHER accepted quote's order_no,
    // comma-joined (a job can have more than one accepted quote). Both are cheap correlated
    // subqueries so they're safe to select for every job even though only
    // WaitingQuoteDetail.razor's "Other Open Jobs" table currently shows them (ETA only for
    // jobs actually in Waiting Workshop Processing).
    //
    // total_spent / total_spent_jobs: lifetime invoiced spend on the job's own equipment —
    // see LifetimeSpendTotalCol/LifetimeSpendJobsCol's doc comment (shared with Model
    // Watchlist's OpenScopeJobsForModelsQuery). Shown on WaitingQuoteDetail.razor's Pending
    // Quote Approval table only (not Other Open Jobs), behind a page-level toggle that also
    // gates the same column in the emailed report.
    private const string WqSelectCols = @"j.job_id, j.job_status_id, j.creation_date, j.notes AS fault,
                js.job_status,
                m.model_id,
                m.manufacture_id,
                CONCAT(m.manufacture_id, ' ', m.model_name_no, ' (', e.serial_no, ')') AS equipment,
                cp.company_people_id AS contact_id,
                TRIM(CONCAT(COALESCE(cp.first_name,''), ' ', COALESCE(cp.last_name,''))) AS contact_name,
                cp.email AS contact_email,
                jq.job_quote_id, jq.total_amount, jq.date_created AS quote_date, jq.accepted, jq.file_location,
                (
                    SELECT GROUP_CONCAT(DISTINCT jqa.order_no ORDER BY jqa.date_accepted SEPARATOR ', ')
                    FROM job_quote jqa
                    WHERE jqa.job_id = j.job_id
                      AND jqa.accepted = 't'
                      AND jqa.order_no IS NOT NULL
                      AND jqa.order_no <> ''
                      AND LOWER(jqa.order_no) NOT LIKE '%requote%'
                ) AS approved_po_numbers,
                (
                    SELECT s.description
                    FROM services s
                    WHERE s.job_id = j.job_id
                      AND s.service_type_id = 11
                      AND s.deleted = 'f'
                      AND s.description LIKE 'ETA - %'
                    ORDER BY s.creation_date DESC
                    LIMIT 1
                ) AS eta_raw,
                (
                    SELECT s.service_id
                    FROM services s
                    WHERE s.job_id = j.job_id
                      AND s.service_type_id = 11
                      AND s.deleted = 'f'
                      AND s.description LIKE 'ETA - %'
                    ORDER BY s.creation_date DESC
                    LIMIT 1
                ) AS eta_service_id,
                " + LifetimeSpendTotalCol + @" AS total_spent,
                " + LifetimeSpendJobsCol + @" AS total_spent_jobs";

    private const string WqJoins = @"LEFT JOIN job_status js    ON js.job_status_id = j.job_status_id
            LEFT JOIN job_equipment je            ON je.job_id        = j.job_id
            LEFT JOIN equipment e                 ON e.equipment_id   = je.equipment_id
            LEFT JOIN model m                     ON m.model_id       = e.model_id
            LEFT JOIN company_people cp           ON cp.company_people_id = j.contact_name
            LEFT JOIN job_quote jq                ON jq.job_id        = j.job_id AND jq.accepted = 'f'";

    public static string BuildQuoteCountsQuery(IEnumerable<int> companyIds, ScopeJobExclusions exclusions)
    {
        var idList = string.Join(",", companyIds);

        return $@"
            SELECT
                t.owner_company_id,
                SUM(CASE WHEN t.latest_quote IS NOT NULL AND DATEDIFF(CURDATE(), t.latest_quote) >= 30 THEN 1 ELSE 0 END) AS urgent_count,
                SUM(CASE WHEN t.latest_quote IS NOT NULL AND DATEDIFF(CURDATE(), t.latest_quote) BETWEEN 21 AND 29 THEN 1 ELSE 0 END) AS medium_count,
                SUM(CASE WHEN t.latest_quote IS NULL OR DATEDIFF(CURDATE(), t.latest_quote) BETWEEN 0 AND 20 THEN 1 ELSE 0 END) AS indate_count
            FROM (
                SELECT
                    j.job_id,
                    j.owner_company_id,
                    MAX(jq.date_created) AS latest_quote
                FROM job j
                LEFT JOIN job_quote jq ON jq.job_id = j.job_id AND jq.accepted = 'f'
                WHERE j.job_status_id = 10
                AND j.owner_company_id IN ({idList})
                {ScopeJobClause(exclusions, jobLevelModelExclusion: true)}
                GROUP BY j.job_id, j.owner_company_id
            ) t
            GROUP BY t.owner_company_id";
    }

    public static string BuildRepairCountsQuery(IEnumerable<int> companyIds, ScopeJobExclusions exclusions)
    {
        var idList = string.Join(",", companyIds);

        return $@"
            SELECT
                j.owner_company_id,
                COUNT(*) AS repair_count
            FROM job j
            WHERE j.job_status_id NOT IN (2, 10)
            AND j.owner_company_id IN ({idList})
            {ScopeJobClause(exclusions, jobLevelModelExclusion: true)}
            GROUP BY j.owner_company_id";
    }

    // ── ScopeReporting*.razor ────────────────────────────────────────────────
    //
    // Flat, single-level GROUP BY, matching BuildRepairCountsQuery's shape —
    // job_invoice is summed per job via a correlated scalar subquery
    // (SUM(subquery) so a job with more than one invoice row can't multiply
    // the rest of the row's aggregates), same technique used for both
    // invoiced_total and paid_total.
    //
    // job_status_id = 2 = Completed (ClosedStatusIds) confirmed against
    // job.completed_date being populated for status-2 jobs.
    //
    // Response time (BuildScopeResponseTimeQuery below) is a DELIBERATELY
    // separate query, not folded in here — it needs a two-level derived
    // table (response_start/response_end each get reused in two places, and
    // a flat SELECT can't do that without recomputing the subquery), which
    // is enough extra complexity that a bug in it shouldn't be able to take
    // out the simpler stats here too. Run both in parallel and merge in C#
    // (ScopeReportStatsParser.Merge), same as WaitingQuote.razor running its
    // quote/repair count queries independently.
    public static string BuildScopeReportStatsQuery(IEnumerable<int> companyIds, ScopeJobExclusions exclusions,
        DateTime periodStart, DateTime periodEnd)
    {
        var idList = string.Join(",", companyIds);
        var start = periodStart.ToString("yyyy-MM-dd");
        var endExclusive = periodEnd.Date.AddDays(1).ToString("yyyy-MM-dd");

        return $@"
            SELECT
                j.owner_company_id,
                COUNT(*) AS job_count,
                SUM(CASE WHEN j.job_status_id = 2 THEN 1 ELSE 0 END) AS completed_count,
                SUM(CASE WHEN j.job_status_id <> 2 THEN 1 ELSE 0 END) AS open_count,
                COALESCE(SUM((SELECT SUM(ji.total_amount) FROM job_invoice ji WHERE ji.job_id = j.job_id)), 0) AS invoiced_total,
                COALESCE(SUM((SELECT SUM(ji.paid_amount) FROM job_invoice ji WHERE ji.job_id = j.job_id)), 0) AS paid_total,
                SUM(CASE WHEN j.job_status_id = 2 AND j.completed_date IS NOT NULL
                         THEN DATEDIFF(j.completed_date, j.creation_date) ELSE 0 END) AS turnaround_days_sum,
                SUM(CASE WHEN j.job_status_id = 2 AND j.completed_date IS NOT NULL THEN 1 ELSE 0 END) AS turnaround_job_count,
                SUM(CASE WHEN j.job_status_id = 2 AND NOT EXISTS (SELECT 1 FROM job_invoice ji WHERE ji.job_id = j.job_id)
                         THEN 1 ELSE 0 END) AS closed_without_invoice_count
            FROM job j
            WHERE j.owner_company_id IN ({idList})
            AND j.creation_date >= '{start}'
            AND j.creation_date < '{endExclusive}'
            {ScopeJobClause(exclusions, jobLevelModelExclusion: true)}
            GROUP BY j.owner_company_id";
    }

    // Status ids confirmed directly against Data/DataDumps/JobStatus.csv (a dump of the
    // job_status table): 10 = Waiting Quote Confirmation, 30 = Waiting Workshop
    // Processing, 35/36/38 = the three "Quote declined (...)" statuses. Using the ids
    // directly (rather than matching job_status.job_status by name) avoids needing a
    // subquery/join against job_status at all here.
    //
    // Response time = first entered status 10 → first EITHER reached status 30 OR one
    // of the declined statuses, whichever the job actually hit, AFTER that status-10
    // entry (a job can revisit status 10 more than once; only the first full pass is
    // measured).
    private static readonly int[] ResponseEndStatusIds = { 30, 35, 36, 38 };

    public static string BuildScopeResponseTimeQuery(IEnumerable<int> companyIds, ScopeJobExclusions exclusions,
        DateTime periodStart, DateTime periodEnd)
    {
        var idList = string.Join(",", companyIds);
        var start = periodStart.ToString("yyyy-MM-dd");
        var endExclusive = periodEnd.Date.AddDays(1).ToString("yyyy-MM-dd");
        var endStatusList = string.Join(",", ResponseEndStatusIds);

        return $@"
            SELECT
                t.owner_company_id,
                SUM(CASE WHEN t.response_start IS NOT NULL AND t.response_end IS NOT NULL
                         THEN DATEDIFF(t.response_end, t.response_start) ELSE 0 END) AS response_days_sum,
                SUM(CASE WHEN t.response_start IS NOT NULL AND t.response_end IS NOT NULL THEN 1 ELSE 0 END) AS response_job_count
            FROM (
                SELECT
                    j.job_id,
                    j.owner_company_id,
                    (
                        SELECT MIN(jsh.creation_date) FROM job_status_history jsh
                        WHERE jsh.job_id = j.job_id AND jsh.job_status_id = 10
                    ) AS response_start,
                    (
                        SELECT MIN(jsh.creation_date)
                        FROM job_status_history jsh
                        WHERE jsh.job_id = j.job_id
                        AND jsh.job_status_id IN ({endStatusList})
                        AND jsh.creation_date > (
                            SELECT MIN(jsh2.creation_date) FROM job_status_history jsh2
                            WHERE jsh2.job_id = j.job_id AND jsh2.job_status_id = 10
                        )
                    ) AS response_end
                FROM job j
                WHERE j.owner_company_id IN ({idList})
                AND j.creation_date >= '{start}'
                AND j.creation_date < '{endExclusive}'
                {ScopeJobClause(exclusions, jobLevelModelExclusion: true)}
            ) t
            GROUP BY t.owner_company_id";
    }

    // ── RepairerManager.razor ────────────────────────────────────────────────
    //
    // Every scope job (open or, for backfilling historical data, recently
    // closed — not company-scoped, repairer assignment applies app-wide, not
    // per customer) along with the distinct model ids of its currently-active
    // equipment (job_equipment.active_status = 't'). Whether a job counts as
    // "assigned" is computed in C# by comparing model_ids / job_id against
    // every Repairer's ModelIds/JobIds (RepairerStore) — that can't be pushed
    // into this query since RepairerStore is a local JSON file, not something
    // the JMS database knows about.
    //
    // "Open" matches BuildRepairCountsQuery's definition: not Completed, not
    // Waiting Quote Confirmation (status 10) — a job that hasn't started
    // repair yet doesn't have a repairer to assign. "Closed" is Completed
    // (status 2) jobs whose completed_date falls in the requested trailing
    // window, so historical jobs can be backfilled a few months at a time
    // rather than needing to load everything at once.
    public static string OpenScopeJobsWithModelsQuery(ScopeJobExclusions exclusions) =>
        BuildScopeJobsWithModelsQuery("j.job_status_id NOT IN (2, 10)", exclusions, "j.creation_date ASC");

    public static string ClosedScopeJobsWithModelsQuery(ScopeJobExclusions exclusions, int monthsBack)
    {
        var cutoff = DateTime.Today.AddMonths(-Math.Max(monthsBack, 1)).ToString("yyyy-MM-dd");
        var statusFilter = $"j.job_status_id = 2 AND j.completed_date IS NOT NULL AND j.completed_date >= '{cutoff}'";
        return BuildScopeJobsWithModelsQuery(statusFilter, exclusions, "j.completed_date DESC");
    }

    // RepairerReporting.razor wants every closed job ever, not a backfill-sized window —
    // unlike RepairerManager's incremental reconciliation, a report undercounting jobs
    // just because they closed more than N months ago would be actively misleading.
    public static string AllClosedScopeJobsWithModelsQuery(ScopeJobExclusions exclusions) =>
        BuildScopeJobsWithModelsQuery("j.job_status_id = 2 AND j.completed_date IS NOT NULL", exclusions, "j.completed_date DESC");

    // sent_to_repairer: whether the job ever reached Waiting Workshop Assignment (status
    // 29) — RepairerReporting.razor uses this to tell a job actually sent out for repair
    // apart from one resolved in-house without ever being assigned to a workshop. Wrapped
    // in MAX() rather than added to GROUP BY: it's constant per job_id (doesn't depend on
    // which job_equipment row produced a given group row), so MAX() just avoids fighting
    // ONLY_FULL_GROUP_BY without changing the grouping.
    private static string BuildScopeJobsWithModelsQuery(string statusFilter, ScopeJobExclusions exclusions, string orderBy) => $@"
        SELECT
            j.job_id,
            j.owner_company_id,
            c.company_name AS owner,
            js.job_status,
            j.creation_date,
            j.completed_date,
            j.notes AS fault,
            GROUP_CONCAT(DISTINCT e.model_id SEPARATOR ',') AS model_ids,
            MAX(CASE WHEN EXISTS (
                SELECT 1 FROM job_status_history jsh29
                WHERE jsh29.job_id = j.job_id AND jsh29.job_status_id = 29
            ) THEN 1 ELSE 0 END) AS sent_to_repairer
        FROM job j
        LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
        LEFT JOIN company c ON c.company_id = j.owner_company_id
        LEFT JOIN job_equipment je ON je.job_id = j.job_id AND je.active_status = 't'
        LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
        WHERE {statusFilter}
        {ScopeJobClause(exclusions, jobLevelModelExclusion: true)}
        GROUP BY j.job_id, j.owner_company_id, c.company_name, js.job_status, j.creation_date, j.completed_date, j.notes
        ORDER BY {orderBy}";

    // ── Lifetime equipment repair spend (shared: Model Watchlist, WaitingQuoteReport) ──
    //
    // Correlated to an outer `job j` — "how much has been invoiced, and across how many jobs,
    // for this job's own currently-active equipment unit(s) over their WHOLE history" (not
    // just this job): job_invoice totals across every job those specific equipment_id rows
    // have ever been attached to (any job_equipment row for that equipment_id, active or not
    // — a unit's history doesn't stop counting just because it was later swapped off a job).
    // Deliberately job_invoice, not job_quote: a quote is a proposed price, not money that
    // actually changed hands — invoiced is what was actually billed. job_invoice is a
    // per-JOB total, not per-equipment-unit, so the inner derived table dedupes to one row
    // per (job_id[, total_amount]) before summing/counting — a job with more than one of the
    // target equipment ids on it would otherwise be double-counted.
    //
    // Bare (not GROUP BY-safe): OpenScopeJobsForModelsQuery's outer query has its own
    // GROUP BY, so it wraps each of these in MAX() itself (same reasoning as that query's
    // quoted_amount/quoted_date columns); WqSelectCols' query has no GROUP BY at all, so it
    // uses these as-is.
    private const string LifetimeSpendTotalCol = @"(
                    SELECT COALESCE(SUM(spend.total_amount), 0) FROM (
                        SELECT DISTINCT jih.job_id, jih.total_amount
                        FROM job_equipment jeh
                        JOIN job_invoice jih ON jih.job_id = jeh.job_id
                        WHERE jeh.equipment_id IN (
                            SELECT je3.equipment_id FROM job_equipment je3
                            WHERE je3.job_id = j.job_id AND je3.active_status = 't'
                        )
                    ) spend
                )";

    private const string LifetimeSpendJobsCol = @"(
                    SELECT COUNT(*) FROM (
                        SELECT DISTINCT jih.job_id
                        FROM job_equipment jeh
                        JOIN job_invoice jih ON jih.job_id = jeh.job_id
                        WHERE jeh.equipment_id IN (
                            SELECT je3.equipment_id FROM job_equipment je3
                            WHERE je3.job_id = j.job_id AND je3.active_status = 't'
                        )
                    ) spend_jobs
                )";

    // ── Model Watchlist (ModelWatchlistLookup / ModelWatchlist.razor) ─────────
    //
    // Plain-text NOTE (service_type_id 11) marker meaning "leave this job off the Model
    // Watchlist's open-opportunities list" — same "write a distinctively-prefixed type-11 row,
    // filter it back out by that prefix" convention as WaitingWorkshopProcessing's "ETA - "
    // and "Approval" markers. It's written untagged, so the tracking Notes UI (JobNotesQuery,
    // "[Tracking]" notes only) never shows it. Defined once here so the write side
    // (JobNotesService.AddUntaggedNoteAsync from ModelWatchlist.razor's Ignore button) and every read side stay in sync.
    public const string ModelWatchlistIgnoredNotePrefix = "[Model Watchlist] Ignored";

    /// <summary>Every job status (id + display name), for the "ignored statuses" picker on
    /// ModelWatchlist.razor — a small, effectively-static reference list, so no filtering or
    /// exclusions needed here.</summary>
    public static string AllJobStatusesQuery() => "SELECT job_status_id, job_status FROM job_status ORDER BY job_status_id";

    // "Models we've repaired, last 12 months": every scope job (ScopeJobClause — the workshop
    // set, minus the configured job/company/model exclusions, same definition every other
    // "scope" query on this page uses) created in the window, counted once per model via its
    // currently-active equipment. `model m` is joined (unused otherwise) purely because
    // ScopeJobClause's default model-exclusion branch filters on `m.model_id` — see that
    // method's doc comment. COUNT(DISTINCT j.job_id) rather than COUNT(*) since a job can
    // carry more than one unit of the same model, which would otherwise double-count that job
    // under its own model.
    //
    // modelIds, when given, narrows the count to just those models (no LIMIT applied, since
    // the caller already bounds the set) — used by the "Interested models" search box to show
    // a job count next to each hit, including ones outside the top-200 list below.
    public static string ModelRepairCountsQuery(DateTime periodStart, ScopeJobExclusions exclusions, IReadOnlyCollection<int>? modelIds = null)
    {
        var start = periodStart.ToString("yyyy-MM-dd");
        var modelFilter = modelIds is { Count: > 0 } ? $"AND e.model_id IN ({string.Join(",", modelIds)})" : "";
        var limit = modelIds is { Count: > 0 } ? "" : "LIMIT 200";
        return $@"
            SELECT e.model_id, COUNT(DISTINCT j.job_id) AS job_count
            FROM job j
            JOIN job_equipment je ON je.job_id = j.job_id AND je.active_status = 't'
            JOIN equipment e ON e.equipment_id = je.equipment_id
            LEFT JOIN model m ON m.model_id = e.model_id
            WHERE j.creation_date >= '{start}'
            {modelFilter}
            {ScopeJobClause(exclusions)}
            GROUP BY e.model_id
            ORDER BY job_count DESC
            {limit}";
    }

    // Every open scope job (not Completed, ScopeJobClause applied — same workshop set +
    // exclusions as ModelRepairCountsQuery above) carrying one of the watchlisted models,
    // except ones staff have explicitly dismissed via the Ignore button
    // (ModelWatchlistIgnoredNotePrefix). Unlike an earlier version of this query, a job's
    // quote/approval state is NOT filtered on here — every open scope job for a watched model
    // shows up, and Ignore is the only way to remove one, so the list can't silently hide a
    // job someone still cares about. Callers must skip this entirely for an empty model id
    // list ("IN ()" is invalid SQL), same convention as the other display-lookup queries above.
    //
    // jobLevelModelExclusion: true — this query already collapses to one row per job via
    // GROUP BY, but (like BuildScopeJobsWithModelsQuery) never joins a `model` table, only
    // `equipment e`, so ScopeJobClause's default `m.model_id` filter has no alias to bind to;
    // the job-level NOT EXISTS variant checks equipment.model_id directly instead.
    //
    // The latest job_quote row's amount/date (accepted or not) is attached via a correlated
    // subquery so the caller can tell "already quoted" (quoted_amount non-null) from "not
    // quoted yet" (null) without a second round trip.
    //
    // total_spent / total_spent_jobs: lifetime invoiced spend on the job's own currently
    // active equipment units, across EVERY job those units have ever been on (not just this
    // one) — a unit repaired several times before shows up with its full history here, which
    // is exactly what a buy-back decision needs to see. The inner derived table dedupes to one
    // row per (job_id, total_amount) before summing: job_invoice is a per-JOB total, not
    // per-equipment-unit, so a plain join would double-count a job that has more than one of
    // the target equipment ids on it.
    //
    // ignoredStatusIds: a persisted, user-managed filter (IgnoredJobStatusStore) distinct from
    // the per-job Ignore button above — that dismisses one specific job forever, this hides an
    // entire status (e.g. "Waiting Parts") from the list for everyone, until removed from the
    // filter. Applied as a plain NOT IN, same as the model-exclusion clauses elsewhere.
    public static string OpenScopeJobsForModelsQuery(IEnumerable<int> modelIds, ScopeJobExclusions exclusions, IReadOnlyCollection<int>? ignoredStatusIds = null)
    {
        var idList = string.Join(",", modelIds);
        var statusFilter = ignoredStatusIds is { Count: > 0 }
            ? $"AND j.job_status_id NOT IN ({string.Join(",", ignoredStatusIds)})"
            : "";
        return $@"
            SELECT
                j.job_id,
                j.owner_company_id,
                c.company_name AS owner,
                js.job_status,
                j.creation_date,
                j.notes AS fault,
                GROUP_CONCAT(DISTINCT e.model_id SEPARATOR ',') AS model_ids,
                GROUP_CONCAT(DISTINCT e.serial_no SEPARATOR ', ') AS serial_numbers,
                -- Wrapped in MAX() rather than left bare, same reasoning as
                -- BuildScopeJobsWithModelsQuery's sent_to_repairer column: it's constant per
                -- job_id (the correlated subquery doesn't depend on which job_equipment row
                -- produced a given group row), so MAX() just avoids fighting ONLY_FULL_GROUP_BY
                -- without changing the grouping.
                MAX((
                    SELECT jq.total_amount FROM job_quote jq
                    WHERE jq.job_id = j.job_id ORDER BY jq.date_created DESC LIMIT 1
                )) AS quoted_amount,
                MAX((
                    SELECT jq.date_created FROM job_quote jq
                    WHERE jq.job_id = j.job_id ORDER BY jq.date_created DESC LIMIT 1
                )) AS quoted_date,
                MAX({LifetimeSpendTotalCol}) AS total_spent,
                MAX({LifetimeSpendJobsCol}) AS total_spent_jobs
            FROM job j
            LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
            LEFT JOIN company c ON c.company_id = j.owner_company_id
            LEFT JOIN job_equipment je ON je.job_id = j.job_id AND je.active_status = 't'
            LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
            WHERE j.job_status_id <> 2
              AND EXISTS (
                  SELECT 1 FROM job_equipment je2
                  JOIN equipment e2 ON e2.equipment_id = je2.equipment_id
                  WHERE je2.job_id = j.job_id AND je2.active_status = 't' AND e2.model_id IN ({idList})
              )
              AND NOT EXISTS (
                  SELECT 1 FROM services sig
                  WHERE sig.job_id = j.job_id AND sig.service_type_id = 11 AND sig.deleted = 'f'
                    AND sig.description LIKE '{ModelWatchlistIgnoredNotePrefix}%'
              )
              {statusFilter}
              {ScopeJobClause(exclusions, jobLevelModelExclusion: true)}
            GROUP BY j.job_id, j.owner_company_id, c.company_name, js.job_status, j.creation_date, j.notes
            ORDER BY j.creation_date ASC";
    }

    /// <summary>Jobs waiting quote confirmation (status 10). Only unaccepted quotes are attached.</summary>
    public static string BuildQuoteJobsQuery(IEnumerable<int> companyIds, ScopeJobExclusions exclusions) =>
        BuildWaitingQuoteJobsQuery("j.job_status_id = 10", companyIds, exclusions);

    /// <summary>All other open jobs (not closed, not waiting quote).</summary>
    public static string BuildOtherJobsQuery(IEnumerable<int> companyIds, ScopeJobExclusions exclusions) =>
        BuildWaitingQuoteJobsQuery("j.job_status_id NOT IN (2, 10)", companyIds, exclusions);

    private static string BuildWaitingQuoteJobsQuery(string statusFilter, IEnumerable<int> companyIds, ScopeJobExclusions exclusions)
    {
        var idList = string.Join(",", companyIds);

        return $@"SELECT {WqSelectCols} FROM job j {WqJoins}
            WHERE {statusFilter}
            AND j.owner_company_id IN ({idList})
            {ScopeJobClause(exclusions, jobLevelModelExclusion: true)}
            ORDER BY j.job_id ASC";
    }

    // ── MondayIntegration.razor (Purchasing board <-> vendor_item_order link) ──
    //
    // One row per vendor_item_order_vuom line, header fields repeated per row —
    // callers group by vendor_item_order_id (same shape as the job/history queries
    // above, e.g. JobsWithHistoryQuery).
    //
    // delivered_qty_vuom is NOT read from vendor_item_order_vuom — that column doesn't
    // get updated when a delivery docket is created (confirmed by testing: it stays
    // static regardless of real receiving activity). The actual "received" ledger is
    // the `stock` table: one row per received unit/batch, tied back to the PO line via
    // vendor_item_order_vuom_id. Delivered = SUM(quantity) for stock rows with
    // stock_status_id = 5 (confirmed — that's the "delivered" status).

    public static string VendorItemOrdersByIdsQuery(IEnumerable<int> poIds)
    {
        var idList = string.Join(",", poIds);
        return $@"
            SELECT
                vio.vendor_item_order_id,
                os.order_status,
                vio.date_ordered,
                vio.date_expected,
                vio.date_last_delivered,
                vio.fully_paid,
                vio.fully_delivered,
                c.company_name AS vendor_name,
                v.vendor_company_id,
                v.vendor_item_order_vuom_id,
                v.master_item_id,
                v.job_no,
                v.vendor_part_code,
                v.vendor_part_description,
                v.order_qty_vuom,
                COALESCE(sd.delivered_qty, 0) AS delivered_qty_vuom,
                v.invoiced_qty_vuom,
                v.total_order_cost_sd,
                COALESCE(im.track_serial, 'f') AS track_serial
            FROM vendor_item_order vio
            LEFT JOIN order_status os ON os.order_status_id = vio.order_status_id
            LEFT JOIN vendor_item_order_vuom v ON v.vendor_item_order_id = vio.vendor_item_order_id
            LEFT JOIN company c ON c.company_id = v.vendor_company_id
            LEFT JOIN item_master im ON im.item_master_id = v.master_item_id
            LEFT JOIN (
                SELECT vendor_item_order_vuom_id, SUM(quantity) AS delivered_qty
                FROM stock
                WHERE stock_status_id = 5
                GROUP BY vendor_item_order_vuom_id
            ) sd ON sd.vendor_item_order_vuom_id = v.vendor_item_order_vuom_id
            WHERE vio.vendor_item_order_id IN ({idList})";
    }

    /// <summary>Reassigns a single PO line to a different job. Scalar UPDATE only —
    /// deliberately does not touch `job_part` (see MondayIntegration.razor's
    /// SubmitEditJobAsync for why that's out of scope for now).
    ///
    /// the Query endpoint appears to always report failure for non-SELECT queries — it
    /// presumably runs the query fine, then errors trying to format a CSV export from
    /// a result set that doesn't exist for an UPDATE. The write itself still lands.
    /// So callers should ignore this call's HtmlResponse.Success and instead confirm
    /// with VendorItemOrderVuomJobNoQuery below.</summary>
    public static string UpdateVendorItemOrderVuomJobNoQuery(int vendorItemOrderVuomId, int newJobNo) => $@"
        UPDATE vendor_item_order_vuom
        SET job_no = {newJobNo}
        WHERE vendor_item_order_vuom_id = {vendorItemOrderVuomId}";

    /// <summary>Reads back job_no for one line — used to confirm UpdateVendorItemOrderVuomJobNoQuery
    /// actually took effect, since the write endpoint's own response can't be trusted for that.</summary>
    public static string VendorItemOrderVuomJobNoQuery(int vendorItemOrderVuomId) => $@"
        SELECT job_no FROM vendor_item_order_vuom
        WHERE vendor_item_order_vuom_id = {vendorItemOrderVuomId}";

    /// <summary>Received (stock_status_id = 5) units for one PO line, with their serial
    /// numbers — used to let the user pick specific units when adding a serialized part
    /// to a job, scoped to what THIS PO actually delivered rather than the vendor-wide
    /// pool AddJobParts's own picker draws from.</summary>
    public static string StockUnitsForLineQuery(int vendorItemOrderVuomId) => $@"
        SELECT stock_id, serial_no
        FROM stock
        WHERE vendor_item_order_vuom_id = {vendorItemOrderVuomId}
        AND stock_status_id = 5";

    /// <summary>Active equipment on a job, for the Add-to-Job "Equipment" dropdown —
    /// model_name_no/serial_no live directly on `equipment`, no join to `model` needed.</summary>
    public static string EquipmentForJobQuery(int jobId) => $@"
        SELECT e.equipment_id, e.model_name_no, e.serial_no
        FROM job_equipment je
        JOIN equipment e ON e.equipment_id = je.equipment_id
        WHERE je.job_id = {jobId}
        AND je.active_status = 't'";

    // ── ScopeJobManager.razor ────────────────────────────────────────────────
    //
    // Display-only lookups so the exclusion manager can show names instead of
    // bare ids. Callers should skip these entirely for an empty id list ("IN ()"
    // is invalid SQL).

    public static string CompanyNamesQuery(IEnumerable<int> companyIds)
    {
        var idList = string.Join(",", companyIds);
        return $@"SELECT company_id, company_name FROM company WHERE company_id IN ({idList})";
    }

    /// <summary>One label per model: "{manufacturer company_name} {model_name_no}".</summary>
    public static string ModelLabelsQuery(IEnumerable<int> modelIds)
    {
        var idList = string.Join(",", modelIds);
        return $@"
            SELECT m.model_id, CONCAT(manu.company_name, ' ', m.model_name_no) AS label
            FROM model m
            LEFT JOIN company manu ON manu.company_id = m.manufacture_id
            WHERE m.model_id IN ({idList})";
    }

    /// <summary>Company name search, for the "search instead of typing an id" add flow.</summary>
    public static string CompanySearchQuery(string term)
    {
        var safe = JmsSql.EscapeLike(term);
        return $@"
            SELECT company_id, company_name
            FROM company
            WHERE company_name LIKE '%{safe}%'
            ORDER BY company_name
            LIMIT 20";
    }

    /// <summary>Same as CompanySearchQuery but limited to clients (companies with a
    /// company_client row) — for LoanEquipmentManager.razor's company pickers, where a repairer
    /// or supplier is never the right answer. our own company (company 1) is always included so
    /// a unit can be marked as back at base.</summary>
    public static string ClientSearchQuery(string term)
    {
        var safe = JmsSql.EscapeLike(term);
        return $@"
            SELECT c.company_id, c.company_name
            FROM company c
            WHERE c.company_name LIKE '%{safe}%'
              AND (c.company_id = 1 OR EXISTS (SELECT 1 FROM company_client cc WHERE cc.company_id = c.company_id))
            ORDER BY c.company_name
            LIMIT 20";
    }

    /// <summary>Model search by model_name_no, same "search instead of typing an id" flow.</summary>
    public static string ModelSearchQuery(string term)
    {
        var safe = JmsSql.EscapeLike(term);
        return $@"
            SELECT m.model_id, CONCAT(manu.company_name, ' ', m.model_name_no) AS label
            FROM model m
            LEFT JOIN company manu ON manu.company_id = m.manufacture_id
            WHERE m.model_name_no LIKE '%{safe}%'
            ORDER BY m.model_name_no
            LIMIT 20";
    }

    /// <summary>Given a job number, the job's owner company plus the models of its currently-active
    /// equipment — lets the exclusion manager's "add from job" box resolve a job the user is looking
    /// at into the company/model ids that actually drive scope exclusion. One row per active equipment
    /// item (or a single row with null equipment/model fields if the job has none); callers should
    /// de-duplicate by model_id since a job can carry several units of the same model.</summary>
    public static string JobScopeLookupQuery(int jobId) => $@"
        SELECT
            j.job_id,
            j.owner_company_id,
            c.company_name AS owner_company_name,
            m.model_id,
            CONCAT(manu.company_name, ' ', m.model_name_no) AS model_label
        FROM job j
        LEFT JOIN company c ON c.company_id = j.owner_company_id
        LEFT JOIN job_equipment je ON je.job_id = j.job_id AND je.active_status = 't'
        LEFT JOIN equipment e ON e.equipment_id = je.equipment_id
        LEFT JOIN model m ON m.model_id = e.model_id
        LEFT JOIN company manu ON manu.company_id = m.manufacture_id
        WHERE j.job_id = {jobId}";

    // ── ScopeCustomerManager.razor ───────────────────────────────────────────
    //
    // A scope customer's "main contact" is a reference to a real company_people
    // row rather than free-typed text — these resolve/search that table so the
    // contact always reflects live JMS data instead of a stale local copy.

    /// <summary>Contact search for the main-contact picker. Scoped to the given company ids when any
    /// are supplied (the normal case, once the customer has company ids attached); otherwise searches
    /// company_people by name/email across all companies.</summary>
    public static string ContactSearchQuery(string term, IEnumerable<int> companyIds)
    {
        var safe = JmsSql.EscapeLike(term);
        var ids = companyIds.ToList();
        var companyFilter = ids.Count > 0 ? $"AND cp.company_id IN ({string.Join(",", ids)})" : "";

        return $@"
            SELECT cp.company_people_id AS contact_id,
                   cp.full_name, cp.email, cp.phone_no, cp.mobile, cp.company_id, co.company_name
            FROM company_people cp
            LEFT JOIN company co ON co.company_id = cp.company_id
            WHERE cp.expired <> 't'
              {companyFilter}
              AND (cp.full_name LIKE '%{safe}%' OR cp.email LIKE '%{safe}%')
            ORDER BY cp.full_name
            LIMIT 20";
    }

    /// <summary>Display-only lookup so a stored contact id can be shown as a name/email/phone without
    /// keeping its own copy of that data — same "resolve ids to labels" idea as CompanyNamesQuery.</summary>
    public static string ContactDetailsQuery(IEnumerable<int> contactIds)
    {
        var idList = string.Join(",", contactIds);
        return $@"
            SELECT cp.company_people_id AS contact_id,
                   cp.full_name, cp.email, cp.phone_no, cp.mobile
            FROM company_people cp
            WHERE cp.company_people_id IN ({idList})";
    }

    // ── BulkJobCreate.razor ──────────────────────────────────────────────────
    //
    // CreateJob's own page fills in a batch of hidden fields via JS the
    // moment its owner-company autocomplete is picked (ship-to, addresses,
    // billing basis, standard labour rate) — these two queries pull that same
    // data straight from the tables it's sourced from, so a direct POST to
    // CreateJob can supply it without a real browser running that JS.

    /// <summary>Owner-side job-creation defaults: ship-to name/address off `company`, and the
    /// billing preferences off `company_client` (LEFT JOINed — not every company has a
    /// company_client row, only ones flagged as clients do).</summary>
    public static string OwnerJobDefaultsQuery(int companyId) => $@"
        SELECT
            c.company_name,
            c.default_ship_to,
            c.shipping_address,
            cc.standard_labour_rate,
            cc.labour_pref,
            cc.travel_pref,
            cc.part_pref
        FROM company c
        LEFT JOIN company_client cc ON cc.company_id = c.company_id
        WHERE c.company_id = {companyId}";

    /// <summary>Bill-to company's postal address, for CreateJob's billing_address field.</summary>
    public static string CompanyBillingAddressQuery(int companyId) => $@"
        SELECT company_id, address
        FROM company
        WHERE company_id = {companyId}";

    /// <summary>Name + shipping address for whichever company `company.default_ship_to` points
    /// at (it stores a company_id as text, not a name) — used when the owner's default ship-to
    /// is a different company than the owner itself.</summary>
    public static string CompanyShippingDetailsQuery(int companyId) => $@"
        SELECT company_id, company_name, shipping_address
        FROM company
        WHERE company_id = {companyId}";

    /// <summary>A contact's department id — CreateJob's own "department"/"bill_department"
    /// hidden fields are the picked contact's company_people.company_department_id, filled in by
    /// its owner_contact/bill_contact autocomplete's select handler, not left blank.</summary>
    public static string ContactDepartmentIdQuery(int contactId) => $@"
        SELECT company_people_id, company_department_id
        FROM company_people
        WHERE company_people_id = {contactId}";

    /// <summary>Equipment search for the "add equipment to job" step, by serial number — scoped to
    /// the job's owner company the same way the live AddJobEquipment's own autocomplete does
    /// (it posts `cid` alongside the search term). model_name_no is included so two units sharing a
    /// serial number (not unheard of across manufacturers) can still be told apart.</summary>
    public static string EquipmentSearchQuery(int ownerCompanyId, string term)
    {
        var safe = JmsSql.EscapeLike(term);
        return $@"
            SELECT equipment_id, serial_no, model_name_no, barcode_no
            FROM equipment
            WHERE current_company_id = {ownerCompanyId}
              AND expired <> 't'
              AND serial_no LIKE '%{safe}%'
            ORDER BY serial_no
            LIMIT 20";
    }

    // ── Workshop TV Dashboard (WorkshopStatus.razor) ────────────────────────
    //
    // A "workshop job" is any job whose job_status_history ever included
    // status 16 or 34 — the same EXISTS check ScopeJobClause uses, but
    // WITHOUT the JobIds/CompanyIds/ModelIds exclusions, since those only
    // decide which of THOSE jobs count as "scope" (A). scope_open (below)
    // reuses ScopeJobClause itself (embedded in a CASE WHEN) so this stays
    // in sync with the one real definition of "scope job" instead of
    // duplicating it — it does NOT call OpenScopeJobsWithModelsQuery, so
    // this doesn't touch or depend on that query's own (deliberately
    // different, used elsewhere — see RepairerManager.razor) NOT IN (2, 10)
    // open definition. "Open workshop jobs" on the dashboard = the
    // COMPLEMENT of A within the workshop set (w_open) — jobs that touched
    // 16/34 but got excluded from being "scope" — computed in C# as w_open
    // minus scope_open. "Open" here is just job_status_id <> 2 (closed) —
    // status 10 (waiting quote confirmation) counts as open for this
    // dashboard, unlike the NOT IN (2, 10) convention some other pages use.
    //
    // Do not change ScopeJobClause, OpenScopeJobsWithModelsQuery,
    // DashboardCountsQuery, or any other existing query for this — they're
    // already relied on elsewhere and work correctly; this is a new,
    // separate query with its own definitions.
    //
    // job.completed_date itself is a dud for most rows — roughly 90% of completed jobs never
    // had it backfilled, so filtering/aggregating on it directly silently drops most of the
    // "completed" set. CompletedDateJoin derives the real completed date from
    // job_status_history instead: the (MAX, since a job can be reopened and re-completed —
    // the most recent completion is what should count) creation_date of that job's row(s)
    // where job_status_id = 2.
    private const string CompletedDateJoin = @"
            LEFT JOIN (
                SELECT job_id, MAX(creation_date) AS completed_date
                FROM job_status_history
                WHERE job_status_id = 2
                GROUP BY job_id
            ) cd ON cd.job_id = j.job_id";

    public static string WorkshopDashboardCountsQuery(ScopeJobExclusions exclusions)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        var last30DaysStart = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
        var workshopStatusIds = string.Join(", ", WorkshopStatusIds);

        return $@"
            SELECT
                SUM(CASE WHEN j.job_status_id <> 2 THEN 1 ELSE 0 END) AS w_open,
                SUM(CASE WHEN j.job_status_id <> 2 {ScopeJobClause(exclusions, jobLevelModelExclusion: true)} THEN 1 ELSE 0 END) AS scope_open,
                SUM(CASE WHEN j.job_status_id = 34 THEN 1 ELSE 0 END) AS waiting_arrival,
                SUM(CASE WHEN j.job_status_id = 42 THEN 1 ELSE 0 END) AS unit_departed,
                SUM(CASE WHEN j.job_status_id = 2 AND cd.completed_date >= '{last30DaysStart}' THEN 1 ELSE 0 END) AS completed_last_30_days,
                SUM(CASE WHEN j.job_status_id = 2 AND cd.completed_date >= '{today}' AND cd.completed_date < '{tomorrow}' THEN 1 ELSE 0 END) AS completed_today
            FROM job j
            {CompletedDateJoin}
            WHERE EXISTS (
                SELECT 1 FROM job_status_history jshw
                WHERE jshw.job_id = j.job_id AND jshw.job_status_id IN ({workshopStatusIds})
            )
            AND (
                j.job_status_id <> 2
                OR cd.completed_date >= '{last30DaysStart}'
            )";
    }


    // ── LoanEquipmentManager.razor ──────────────────────────────────────────
    //
    // The loan fleet is every equipment row tagged barcode_no = 'LOAN-UNIT' that
    // is still registered to our own company (current_company_id = 1)
    // — that stays true even while a unit is physically out with a customer;
    // JMS tracks who currently has it via the job, not by moving the
    // equipment's owning company. A unit's "Loan Job" (the job tracking its
    // current loan period) is the most recent job that has it as active
    // equipment (job_equipment.active_status = 't') and isn't completed
    // (job_status_id <> 2) — "most recent" by job_id, which is fine since
    // it's an auto-increment PK. agreement_count > 0 means that job's
    // Resources tab has an uploaded item titled exactly "Signed Loan
    // Agreement" (same online_resource/online_resource_link pattern as the
    // PO check in WaitingWorkshopProcessingQuery above). loaned_date is the
    // date that job FIRST entered job_status_id = 20 ("Loan Unit Provided" —
    // see JobStatus.csv), i.e. when the unit actually went out, not when the
    // job was created — same first-entered-status pattern as first_set_date
    // in WaitingWorkshopProcessingQuery. manufacturer_id/manufacturer_name are
    // returned separately (not pre-concatenated into a label) so
    // LoanEquipmentManager.razor can substitute ManufacturerAbbreviationStore's
    // short form for manufacturer_id when there is one —
    // same data WaitingQuoteDetail.razor uses via WaitingQuoteReport.AbbreviateEquipment.
    public static string LoanEquipmentQuery() => @"
        SELECT
            e.equipment_id,
            e.model_id,
            manu.company_id AS manufacturer_id,
            manu.company_name AS manufacturer_name,
            m.model_name_no,
            e.serial_no,
            e.last_inspected_date,
            jl.job_id AS loan_job_id,
            (
                SELECT MIN(jsh.creation_date)
                FROM job_status_history jsh
                WHERE jsh.job_id = jl.job_id AND jsh.job_status_id = 20
            ) AS loaned_date,
            (
                SELECT COUNT(*)
                FROM online_resource_link orl
                JOIN online_resource o ON o.online_resource_id = orl.online_resource_id
                WHERE orl.link_table_key_name = 'job_id'
                  AND orl.link_table_key_value = jl.job_id
                  AND TRIM(o.title) = 'Signed Loan Agreement'
            ) AS agreement_count
        FROM equipment e
        LEFT JOIN model m ON m.model_id = e.model_id
        LEFT JOIN company manu ON manu.company_id = m.manufacture_id
        LEFT JOIN job jl ON jl.job_id = (
            SELECT je.job_id
            FROM job_equipment je
            JOIN job j2 ON j2.job_id = je.job_id
            WHERE je.equipment_id = e.equipment_id
              AND je.active_status = 't'
              AND j2.job_status_id <> 2
            ORDER BY je.job_id DESC
            LIMIT 1
        )
        WHERE e.current_company_id = 1
          AND e.barcode_no = 'LOAN-UNIT'
        ORDER BY manufacturer_name, m.model_name_no, e.serial_no";

    /// <summary>Previous owners of each loan unit, derived from its job history: every distinct
    /// job.owner_company_id the unit has been on a job for, other than our own
    /// (companies 1 and 2). Loan jobs are owned by us, so they drop out on their own —
    /// status 20 ("Loan Unit Provided") can't be used to tell them apart since it's set on the
    /// customer's repair job too. A unit can have several (e.g. passed between sites before we
    /// bought it), or none if it's only ever been on our own jobs.</summary>
    public static string LoanEquipmentPreviousOwnersQuery(IEnumerable<int> equipmentIds) => $@"
        SELECT
            je.equipment_id,
            j.owner_company_id AS company_id,
            c.company_name
        FROM job_equipment je
        JOIN job j ON j.job_id = je.job_id
        LEFT JOIN company c ON c.company_id = j.owner_company_id
        WHERE je.equipment_id IN ({string.Join(",", equipmentIds)})
          AND j.owner_company_id IS NOT NULL
          AND j.owner_company_id NOT IN (1, 2)
        GROUP BY je.equipment_id, j.owner_company_id, c.company_name";

    /// <summary>Status + quote date for the jobs a loan is covering for (LoanEquipmentOverlay.
    /// CoveringForJobIds — which job(s) the customer's own unit is in for repair on while this
    /// loan is out; not something JMS tracks, so it's a locally-entered list of job ids resolved
    /// live here). quoted_date is the first time the job hit job_status_id = 10 ("Waiting Quote
    /// Confirmation") — the loan is only valid while the job is actively being repaired, and once
    /// it's quoted the customer has 30 days to approve/decline before we start charging
    /// for the loan or asks for it back, so LoanEquipmentManager.razor uses this date as the start
    /// of that 30-day clock.</summary>
    public static string CoveringJobsQuery(IEnumerable<int> jobIds)
    {
        var idList = string.Join(",", jobIds);
        return $@"
            SELECT
                j.job_id,
                js.job_status,
                (
                    SELECT MIN(jsh.creation_date)
                    FROM job_status_history jsh
                    WHERE jsh.job_id = j.job_id AND jsh.job_status_id = 10
                ) AS quoted_date,
                (
                    SELECT GROUP_CONCAT(DISTINCT CONCAT(manu.company_name, ' ', m.model_name_no, ' (', e.serial_no, ')') SEPARATOR '; ')
                    FROM job_equipment je
                    JOIN equipment e ON e.equipment_id = je.equipment_id
                    JOIN model m ON m.model_id = e.model_id
                    LEFT JOIN company manu ON manu.company_id = m.manufacture_id
                    WHERE je.job_id = j.job_id
                ) AS equipment
            FROM job j
            LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
            WHERE j.job_id IN ({idList})";
    }

    /// <summary>Open jobs on customer-owned equipment of the given models, for
    /// LoanEquipmentManager.razor's "Customer Jobs" tab — under each loaned-out unit it lists the
    /// Loaned To customer's open jobs on units of the same model or a substitute model
    /// (LoanModelSubstituteStore), i.e. what the loan is (or could be) covering for. Scope jobs
    /// only (ScopeJobClause). "Open" = the unit is still active equipment on the job
    /// (job_equipment.active_status = 't') and the job isn't Completed/Invoiced (job_status_id
    /// 2/3). quoted_date is the first time the job hit job_status_id = 10 ("Waiting Quote
    /// Confirmation"), same as CoveringJobsQuery above. Matched on the equipment's
    /// current_company_id; the page narrows each row set to its own loan's customer.</summary>
    public static string CustomerModelJobsQuery(IEnumerable<int> modelIds, IEnumerable<int> companyIds, ScopeJobExclusions exclusions) => $@"
        SELECT
            j.job_id,
            js.job_status,
            (
                SELECT MIN(jsh.creation_date)
                FROM job_status_history jsh
                WHERE jsh.job_id = j.job_id AND jsh.job_status_id = 10
            ) AS quoted_date,
            e.model_id,
            manu.company_id AS manufacturer_id,
            manu.company_name AS manufacturer_name,
            m.model_name_no,
            e.current_company_id,
            c.company_name,
            e.serial_no
        FROM job_equipment je
        JOIN job j ON j.job_id = je.job_id
        JOIN equipment e ON e.equipment_id = je.equipment_id
        LEFT JOIN model m ON m.model_id = e.model_id
        LEFT JOIN company manu ON manu.company_id = m.manufacture_id
        LEFT JOIN company c ON c.company_id = e.current_company_id
        LEFT JOIN job_status js ON js.job_status_id = j.job_status_id
        WHERE e.model_id IN ({string.Join(",", modelIds)})
          AND e.current_company_id IN ({string.Join(",", companyIds)})
          AND je.active_status = 't'
          AND j.job_status_id NOT IN (2, 3)
          {ScopeJobClause(exclusions)}
        ORDER BY j.job_id DESC";

    // ── ServiceReport.razor ─────────────────────────────────────

    public static string InspectionIdsForJobQuery(int jobId) => $@"
        SELECT inspection_id
        FROM inspection
        WHERE job_id = {jobId}
          AND asset_condition IS NOT NULL";

    // resource_count: files uploaded against the inspection itself — same
    // online_resource_link pattern as job uploads, keyed on inspection_id.
    public static string InspectionEquipmentQuery(IEnumerable<int> inspectionIds) => $@"
        SELECT
            i.inspection_id, e.serial_no, e.asset_no, e.model_id, i.comments,
            (
                SELECT COUNT(*)
                FROM online_resource_link orl
                WHERE orl.link_table_key_name = 'inspection_id'
                  AND orl.link_table_key_value = i.inspection_id
            ) AS resource_count
        FROM inspection i
        LEFT JOIN equipment e ON e.equipment_id = i.equipment_id
        WHERE i.inspection_id IN ({string.Join(",", inspectionIds)})";

    /// <summary>The most recent inspection of one piece of equipment on one job — its comments
    /// are the reference notes on the in-house quote page (QuoteInHouse.razor).</summary>
    // Also the inspected scope's manufacturer + model ("Olympus" / "GIF-H190") — the in-house
    // flex quote's repair suggestions need the real model to pick model-specific price lines.
    public static string LatestJobEquipmentInspectionQuery(int jobId, int equipmentId) => $@"
        SELECT i.inspection_id, i.creation_date, i.comments,
               manu.company_name AS manufacturer, m.model_name_no, e.generic_name
        FROM inspection i
        LEFT JOIN equipment e ON e.equipment_id = i.equipment_id
        LEFT JOIN model m ON m.model_id = e.model_id
        LEFT JOIN company manu ON manu.company_id = m.manufacture_id
        WHERE i.job_id = {jobId}
          AND i.equipment_id = {equipmentId}
        ORDER BY i.creation_date DESC, i.inspection_id DESC
        LIMIT 1";

    /// <summary>Site name and address for the service report cover page.</summary>
    public static string CompanyCoverQuery(int companyId) => $@"
        SELECT company_name, address
        FROM company
        WHERE company_id = {companyId}";

    /// <summary>Operational status from each test equipment item's most recent inspection,
    /// plus its latest calibration certificate (equipment_certificate, furthest expiry) —
    /// used by ServiceReport to flag test gear that wasn't operational or is
    /// out of calibration.</summary>
    public static string TestEquipmentStatusQuery(IEnumerable<int> equipmentIds) => $@"
        SELECT
            e.equipment_id, e.serial_no, e.model_name_no,
            li.creation_date AS last_inspection_date,
            li.inspection_id AS last_inspection_id,
            os.operational_status,
            ec.equipment_certificate_id AS certificate_id,
            ec.filename AS certificate_filename,
            ec.expiry_date AS calibration_expiry
        FROM equipment e
        LEFT JOIN inspection li ON li.inspection_id = (
            SELECT i2.inspection_id
            FROM inspection i2
            WHERE i2.equipment_id = e.equipment_id
            ORDER BY i2.creation_date DESC
            LIMIT 1
        )
        LEFT JOIN operational_status os ON os.operational_status_id = li.operational_status_id
        LEFT JOIN equipment_certificate ec ON ec.equipment_certificate_id = (
            SELECT ec2.equipment_certificate_id
            FROM equipment_certificate ec2
            WHERE ec2.equipment_id = e.equipment_id
            ORDER BY ec2.expiry_date DESC, ec2.creation_date DESC
            LIMIT 1
        )
        WHERE e.equipment_id IN ({string.Join(",", equipmentIds)})";

    // notes ("Routine Service Job - 2026-39 --- Theatres") pre-fills the
    // cover page's job number and area; last_worked_date is the cover date.
    public static string InspectionJobMetaQuery(int jobId) => $@"
        SELECT owner_company_id, rs_report_id, notes, last_worked_date
        FROM job
        WHERE job_id = {jobId}";

    public static string InspectionTestResultsQuery(IEnumerable<int> inspectionIds) => $@"
        SELECT
            tsr.inspection_id,
            tsr.test_script_result_id                  AS result_id,
            tsr.test_script_item_id,
            tsi.test_type_id,
            tsr.test_equipment_id,
            tsr.result_value
        FROM test_script_result tsr
        JOIN test_script_item tsi ON tsi.test_script_item_id = tsr.test_script_item_id
        WHERE tsr.inspection_id IN ({string.Join(",", inspectionIds)})";

    /// <summary>Polls for the "PDF Complete" notification for a detailed inspection report job —
    /// used by InspectionDocumentBuilder while a BackgroundJobSubmission job runs.</summary>
    public static string DetailedReportNotificationQuery(int userId, int jobId, int afterNotificationId) => $@"
        SELECT notification_link, notification_message
        FROM user_notification
        WHERE user_id = {userId}
          AND user_notifications_id > {afterNotificationId}
          AND notification_title = 'PDF Complete'
          AND notification_message LIKE '%{jobId}%'
        ORDER BY user_notifications_id DESC
        LIMIT 1";

    /// <summary>Newest notification id for the user, taken just before submitting a
    /// detailed report so polling ignores "PDF Complete" notices from earlier runs.</summary>
    public static string LatestNotificationIdQuery(int userId) => $@"
        SELECT COALESCE(MAX(user_notifications_id), 0) AS max_id
        FROM user_notification
        WHERE user_id = {userId}";

    /// <summary>Newest notification id for anyone — taken just before MonthlyReportBuilder
    /// submits a Job Progress background job, so polling ignores older notices.</summary>
    public static string LatestAnyNotificationIdQuery() => @"
        SELECT COALESCE(MAX(user_notifications_id), 0) AS max_id
        FROM user_notification";

    /// <summary>The "PDF Complete" notice for a Job Progress background job. Matched on the
    /// company name in the message ("Job Progress Report for {name} has been completed, ...")
    /// rather than the user, so it doesn't need the service account's JMS user id.</summary>
    public static string JobProgressNotificationQuery(string companyName, int afterNotificationId) => $@"
        SELECT notification_link, notification_message
        FROM user_notification
        WHERE user_notifications_id > {afterNotificationId}
          AND notification_title = 'PDF Complete'
          AND notification_message LIKE 'Job Progress Report for {JmsSql.EscapeLike(companyName)} has been%'
        ORDER BY user_notifications_id DESC
        LIMIT 1";
}
