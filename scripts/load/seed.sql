-- Load test data for scripts/run-k6.sh, applied to a throwaway database as the admin login. Never run it anywhere else.
-- 500 listings (XNAS T0001..T0500) with six years of weekday prices that end on the last session before today, every listing loaded
-- successfully just now, and the USD to GTQ rate the calculator needs. Plain SQL, no data of anybody.
\set ON_ERROR_STOP on
\set load_ticker '^T[0-9]{4}$'

INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at)
SELECT gen_random_uuid(), 'XNAS', 'T' || lpad(n::text, 4, '0'), 'Load test stock ' || n, 'USD', 'America/New_York', 'equity', true, now()
FROM generate_series(1, 500) AS n;

INSERT INTO marketdata.ingestion_state (stock_id, status, last_success_at, last_attempt_at)
SELECT id, 'ok', now(), now() FROM marketdata.stocks WHERE ticker ~ :'load_ticker';

WITH last AS (
    SELECT CASE extract(isodow FROM current_date) WHEN 1 THEN current_date - 3 WHEN 7 THEN current_date - 2 ELSE current_date - 1 END AS day
)
INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
SELECT s.id, d.day, a.v, a.v + 1, a.v - 1, a.v, a.v, 1000, 'twelvedata', true, now()
FROM marketdata.stocks s
CROSS JOIN last
CROSS JOIN LATERAL (
    SELECT day, row_number() OVER (ORDER BY day) AS n
    FROM generate_series((last.day - interval '6 years')::date, last.day, interval '1 day') AS g(day)
    WHERE extract(isodow FROM day) < 6) d
CROSS JOIN LATERAL (SELECT 100 + 0.5 * ((d.n + abs(hashtext(s.ticker))) % 37) + 0.01 * d.n AS v) a
WHERE s.ticker ~ :'load_ticker';

-- A rate for each of the last ten days, so there is always one on or before the day the API asks for (its day is that of Guatemala, which
-- is the day before the database's own for six hours of every UTC day).
INSERT INTO marketdata.fx_rates (base, quote, as_of_date, source, rate, ingested_at)
SELECT 'USD', 'GTQ', d::date, 'banguat', 7.75, now()
FROM generate_series(current_date - 10, current_date, interval '1 day') AS g(d);

ANALYZE marketdata.price_daily;
SELECT count(*) AS stocks, (SELECT count(*) FROM marketdata.price_daily) AS prices FROM marketdata.stocks WHERE ticker ~ :'load_ticker';
