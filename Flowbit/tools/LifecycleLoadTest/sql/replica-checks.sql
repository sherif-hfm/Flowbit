-- Read-only checks after a run with several API or Worker processes against one database.
-- PowerShell: Get-Content ./Flowbit/tools/LifecycleLoadTest/sql/replica-checks.sql | docker exec -i flowbit-load-pg psql -U flowbit -d <database>
-- Bash:       docker exec -i flowbit-load-pg psql -U flowbit -d <database> < Flowbit/tools/LifecycleLoadTest/sql/replica-checks.sql
\pset footer off

\echo 'Jobs with more than one completed attempt (expect no rows): work done twice.'
SELECT a."JobId", j."Kind", count(*) AS completed_attempts, string_agg(DISTINCT a."WorkerId", ', ') AS workers
FROM flowbit.workflow_job_attempts a
JOIN flowbit.workflow_jobs j ON j."Id" = a."JobId"
WHERE a."Status" = 'completed'
GROUP BY a."JobId", j."Kind"
HAVING count(*) > 1
ORDER BY a."JobId"
LIMIT 20;

\echo 'Job attempts by Worker process (the second part of the Worker id) and outcome:'
SELECT split_part(a."WorkerId", ':', 2) AS worker_process, a."Status" AS attempt_status, count(*) AS attempts
FROM flowbit.workflow_job_attempts a
GROUP BY 1, 2
ORDER BY 1, 2;

\echo 'Jobs that are not finished (expect only work still due in the future):'
SELECT j."Kind", j."Status", count(*) AS jobs, min(j."DueAt") AS earliest_due
FROM flowbit.workflow_jobs j
WHERE j."Status" NOT IN ('completed', 'cancelled')
GROUP BY 1, 2
ORDER BY 1, 2;

\echo 'Open incidents (expect no rows):'
SELECT i."Type", i."NodeName", count(*) AS incidents, min(i."Summary") AS example
FROM flowbit.workflow_incidents i
WHERE i."Status" = 'open'
GROUP BY 1, 2
ORDER BY 3 DESC;

\echo 'Instances by status:'
SELECT "Status", count(*) AS instances
FROM flowbit.workflow_instances
GROUP BY 1
ORDER BY 1;

\echo 'Deadlocks and rollbacks since the database statistics were last reset:'
SELECT deadlocks, xact_rollback AS rollbacks, xact_commit AS commits
FROM pg_stat_database
WHERE datname = current_database();
