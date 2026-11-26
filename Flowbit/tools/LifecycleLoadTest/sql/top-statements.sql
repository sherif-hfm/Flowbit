-- The statements that took the most database time, from pg_stat_statements. The PostgreSQL
-- container must be started with `-c shared_preload_libraries=pg_stat_statements`, and the
-- extension created once per database: CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
-- Reset before a run with: SELECT pg_stat_statements_reset();
-- PowerShell: Get-Content ./Flowbit/tools/LifecycleLoadTest/sql/top-statements.sql | docker exec -i flowbit-load-pg psql -U flowbit -d <database> -v top=25
-- Bash:       docker exec -i flowbit-load-pg psql -U flowbit -d <database> -v top=25 < Flowbit/tools/LifecycleLoadTest/sql/top-statements.sql
\pset footer off
\pset format unaligned
\pset fieldsep ' | '

SELECT 'TOTAL' AS what, sum(calls) AS calls, round(sum(total_exec_time)::numeric, 0) AS exec_ms,
       count(*) AS distinct_statements
FROM pg_stat_statements s JOIN pg_database d ON d.oid = s.dbid
WHERE d.datname = current_database() AND toplevel AND query NOT ILIKE '%pg_stat_statements%';

SELECT 'BY KIND' AS what, upper(split_part(ltrim(query), ' ', 1)) AS kind, sum(calls) AS calls,
       round(sum(total_exec_time)::numeric, 0) AS exec_ms
FROM pg_stat_statements s JOIN pg_database d ON d.oid = s.dbid
WHERE d.datname = current_database() AND toplevel AND query NOT ILIKE '%pg_stat_statements%'
GROUP BY 2
ORDER BY 4 DESC
LIMIT 12;

SELECT calls,
       round(total_exec_time::numeric, 0) AS exec_ms,
       round((100 * total_exec_time / sum(total_exec_time) OVER ())::numeric, 1) AS pct_time,
       round(mean_exec_time::numeric, 2) AS mean_ms,
       round(max_exec_time::numeric, 1) AS max_ms,
       rows,
       left(regexp_replace(query, '\s+', ' ', 'g'), 230) AS statement
FROM pg_stat_statements s JOIN pg_database d ON d.oid = s.dbid
WHERE d.datname = current_database() AND toplevel AND query NOT ILIKE '%pg_stat_statements%'
ORDER BY total_exec_time DESC
LIMIT :top;
