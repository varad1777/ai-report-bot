CREATE EXTENSION IF NOT EXISTS timescaledb;

-- ========== RELATIONAL (metadata) ==========

CREATE TABLE assets (
  asset_id     SERIAL PRIMARY KEY,
  name         TEXT NOT NULL UNIQUE,          -- e.g. Boiler Feed Pump Motor
  asset_type   TEXT NOT NULL DEFAULT 'Motor',
  location     TEXT,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE signals (
  signal_id           SERIAL PRIMARY KEY,
  asset_id            INT NOT NULL REFERENCES assets(asset_id),
  name                TEXT NOT NULL,                  -- e.g. Winding_Temp_U
  unit                TEXT,                           -- °C, mm/s, A, V, rpm...
  min_value           DOUBLE PRECISION NOT NULL DEFAULT 0,       -- Warning Min (0)
  max_value           DOUBLE PRECISION NOT NULL DEFAULT 10,      -- Warning Max (10)
  critical_min_value  DOUBLE PRECISION NOT NULL DEFAULT -2,      -- Critical Min (-2)
  critical_max_value  DOUBLE PRECISION NOT NULL DEFAULT 12,      -- Critical Max (12)
  UNIQUE (asset_id, name)
);
CREATE INDEX ON signals (asset_id);

CREATE TABLE events (
  event_id     BIGSERIAL PRIMARY KEY,
  asset_id     INT NOT NULL REFERENCES assets(asset_id),
  signal_id    INT NOT NULL REFERENCES signals(signal_id),
  event_type   TEXT NOT NULL,                 -- 'WARNING_HIGH_EXCURSION' / 'CRITICAL_HIGH_EXCURSION' / etc.
  start_time   TIMESTAMPTZ NOT NULL,          -- signal crossed its limit
  end_time     TIMESTAMPTZ NOT NULL,          -- signal came back inside the range
  peak_value   DOUBLE PRECISION,              -- highest (or lowest) value during the event
  threshold    DOUBLE PRECISION,              -- the warning (10/0) or critical (12/-2) limit that was crossed
  severity     TEXT NOT NULL DEFAULT 'WARNING' -- 'WARNING' / 'CRITICAL'
);
CREATE INDEX ON events (asset_id, start_time DESC);
CREATE INDEX ON events (signal_id, start_time DESC);

CREATE TABLE alerts (
  alert_id         BIGSERIAL PRIMARY KEY,
  asset_id         INT NOT NULL REFERENCES assets(asset_id),
  signal_id        INT NOT NULL REFERENCES signals(signal_id),
  triggered_at     TIMESTAMPTZ NOT NULL,
  trigger_value    DOUBLE PRECISION NOT NULL,
  threshold_value  DOUBLE PRECISION NOT NULL, -- the warning (10/0) or critical (12/-2) limit that was crossed
  severity         TEXT NOT NULL,             -- 'WARNING' / 'CRITICAL'
  status           TEXT NOT NULL,             -- 'OPEN' / 'ACKNOWLEDGED' / 'RESOLVED'
  acknowledged_at  TIMESTAMPTZ,
  resolved_at      TIMESTAMPTZ
);
CREATE INDEX ON alerts (asset_id, triggered_at DESC);

-- ========== TIMESCALE (raw signal data) ==========
-- No FK to signals on purpose: a FK check on every row makes bulk loading of ~30M rows much slower.

CREATE TABLE signal_data (
  time       TIMESTAMPTZ      NOT NULL,
  signal_id  INT              NOT NULL,
  value      DOUBLE PRECISION NOT NULL
);

SELECT create_hypertable('signal_data', 'time', chunk_time_interval => INTERVAL '7 days');

CREATE INDEX ON signal_data (signal_id, time DESC);

ALTER TABLE signal_data SET (
  timescaledb.compress,
  timescaledb.compress_segmentby = 'signal_id',
  timescaledb.compress_orderby   = 'time DESC'
);
SELECT add_compression_policy('signal_data', INTERVAL '7 days');