'use strict';
/**
 * Seeds Postgres + TimescaleDB with:
 *   - 5 motor assets, 120 signals each (600 signals total)
 *   - Thresholds for all signals:
 *       Warning Band : [0.0, 10.0]  (min_value = 0, max_value = 10)
 *       Critical Band: [-2.0, 12.0] (critical_min_value = -2, critical_max_value = 12)
 *   - Normal operating values stay healthy inside [1.0, 9.0]
 *   - Excursions:
 *       Crossing 10.0 (or 0.0) -> WARNING event/alert (peaks between 10.4 and 11.6 / -0.4 and -1.6)
 *       Crossing 12.0 (or -2.0) -> CRITICAL event/alert (peaks between 12.5 and 14.5 / -2.5 and -4.5)
 *   - Raw signal values -> hypertable signal_data
 *   - 3-4 events per week (mix of WARNING and CRITICAL)
 *   - 12 alerts (mix of WARNING and CRITICAL; resolved, acknowledged, and open)
 *
 * Every event / alert is ALSO injected into the raw data, so querying signal_data
 * shows the same crossing that is recorded in the events / alerts tables.
 *
 * Usage:
 *   npm install
 *   node seed.js --dry-run     # plan + generate in memory, no DB (sanity check)
 *   node seed.js --reset       # wipe tables and seed
 *
 * Env (optional): PGHOST PGPORT PGUSER PGPASSWORD PGDATABASE MONTHS SAMPLE_MINUTES SEED
 */
const { Client } = require('pg');
const { from: copyFrom } = require('pg-copy-streams');
const { pipeline } = require('stream/promises');
const { Readable } = require('stream');

// ------------------------------------------------------------------ config
const CFG = {
  pg: {
    host: process.env.PGHOST || 'localhost',
    port: +(process.env.PGPORT || 5432),
    user: process.env.PGUSER || 'iam_user',
    password: process.env.PGPASSWORD || 'iam_pass',
    database: process.env.PGDATABASE || 'iam_db',
  },
  months: +(process.env.MONTHS || 6),
  sampleMinutes: +(process.env.SAMPLE_MINUTES || 5),
  signalsPerAsset: 120,
  eventsPerWeek: [3, 4],
  alertCount: 12,
  seed: +(process.env.SEED || 42),
};
const RESET = process.argv.includes('--reset');
const DRY_RUN = process.argv.includes('--dry-run');

// Standardized Dual-Tier Thresholds
const WARN_MIN = 0.0;
const WARN_MAX = 10.0;
const CRIT_MIN = -2.0;
const CRIT_MAX = 12.0;

const ASSETS = [
  { name: 'Boiler Feed Pump Motor', location: 'Boiler House' },
  { name: 'Air Compressor Motor', location: 'Compressor Room' },
  { name: 'Main Conveyor Drive Motor', location: 'Material Handling Area' },
  { name: 'Cooling Tower Fan Motor', location: 'Utility Yard' },
  { name: 'Primary Crusher Motor', location: 'Crushing Plant' },
];

// ------------------------------------------------------------------ helpers
function mulberry32(a) {
  return function () {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const rand = mulberry32(CFG.seed);
const rr = (a, b) => a + (b - a) * rand();
const ri = (a, b) => Math.floor(rr(a, b + 1));
const TWO_PI = Math.PI * 2;
const DAY_MS = 86400000;

// ------------------------------------------------------------------ signal templates (120 per motor)
function buildTemplates() {
  const T = [];
  const add = (name, unit, o = {}) =>
    T.push({
      name,
      unit,
      lo: WARN_MIN,
      hi: WARN_MAX,
      critLo: CRIT_MIN,
      critHi: CRIT_MAX,
      frac: [0.4, 0.6], // normal resting baseline sits between 4.0 and 6.0
      dir: 'high',
      ...o
    });

  const P = ['U', 'V', 'W'], L = ['L1', 'L2', 'L3'], POS = ['DE', 'NDE'], AX = ['H', 'V', 'A'];

  // temperatures (11)
  P.forEach((p) => add(`Winding_Temp_${p}`, '°C'));
  POS.forEach((p) => add(`Bearing_Temp_${p}`, '°C'));
  add('Ambient_Temp', '°C');
  add('Housing_Temp', '°C');
  add('Coolant_Temp', '°C');
  add('Stator_Core_Temp', '°C');
  add('Rotor_Temp', '°C');
  add('Cooling_Fan_Temp', '°C');

  // vibration (18)
  POS.forEach((p) => AX.forEach((a) => add(`Vib_Vel_${p}_${a}`, 'mm/s')));
  POS.forEach((p) => AX.forEach((a) => add(`Vib_Acc_${p}_${a}`, 'g')));
  POS.forEach((p) => AX.forEach((a) => add(`Vib_Env_${p}_${a}`, 'gE')));

  // current (6)
  L.forEach((l) => add(`Current_${l}`, 'A'));
  add('Neutral_Current', 'A');
  add('Current_Imbalance', '%');
  add('Inrush_Current_Peak', 'A');

  // voltage (7)
  ['L12', 'L23', 'L31'].forEach((l) => add(`Voltage_${l}`, 'V', { dir: 'both' }));
  L.forEach((l) => add(`Voltage_${l}N`, 'V', { dir: 'both' }));
  add('Voltage_Imbalance', '%');

  // power (4)
  add('Active_Power', 'kW');
  add('Reactive_Power', 'kVAr');
  add('Apparent_Power', 'kVA');
  add('Power_Factor', '', { dir: 'low' });

  // speed / load (6)
  add('Speed_RPM', 'rpm', { dir: 'both' });
  add('Frequency', 'Hz', { dir: 'both' });
  add('Slip', '%');
  add('Torque', 'Nm');
  add('Load', '%');
  add('Shaft_Power', 'kW');

  // power quality (18)
  L.forEach((l) => add(`Current_THD_${l}`, '%'));
  L.forEach((l) => add(`Voltage_THD_${l}`, '%'));
  [3, 5, 7, 11].forEach((h) => L.forEach((l) => add(`Current_H${h}_${l}`, '%')));

  // insulation / electrical health (6)
  add('Insulation_Resistance', 'MΩ', { dir: 'low' });
  P.forEach((p) => add(`Winding_Resistance_${p}`, 'mΩ', { dir: 'both' }));
  add('Leakage_Current', 'mA');
  add('Partial_Discharge', 'pC');

  // lubrication (4)
  add('Oil_Pressure', 'bar', { dir: 'both' });
  add('Oil_Temp', '°C');
  add('Oil_Level', '%', { dir: 'low' });
  add('Oil_Flow', 'L/min', { dir: 'both' });

  // cooling (4)
  add('Fan_Speed', 'rpm', { dir: 'both' });
  add('Air_Flow', 'm3/h', { dir: 'both' });
  add('Coolant_Flow', 'L/min', { dir: 'both' });
  add('Coolant_Pressure', 'bar', { dir: 'both' });

  // shaft (4)
  add('Shaft_Disp_X', 'um');
  add('Shaft_Disp_Y', 'um');
  add('Axial_Position', 'um', { dir: 'both' });
  add('Shaft_Eccentricity', 'um');

  // embedded RTDs (6)
  for (let n = 1; n <= 6; n++) add(`Winding_RTD_${n}`, '°C');

  // pad up to 120 with auxiliary channels
  for (let n = 1; T.length < CFG.signalsPerAsset; n++) {
    const id = String(n).padStart(2, '0');
    add(`Aux_Temp_${id}`, '°C');
    if (T.length < CFG.signalsPerAsset) add(`Aux_Vib_${id}`, 'mm/s');
  }
  return T.slice(0, CFG.signalsPerAsset);
}

// ------------------------------------------------------------------ timeline
const STEP_MS = CFG.sampleMinutes * 60000;
const endMs = Math.floor(Date.now() / STEP_MS) * STEP_MS;
const startRaw = new Date(endMs);
startRaw.setUTCMonth(startRaw.getUTCMonth() - CFG.months);
const startMs = Math.ceil(startRaw.getTime() / STEP_MS) * STEP_MS;
const N = Math.floor((endMs - startMs) / STEP_MS) + 1; // number of timestamps
const tsAt = (i) => startMs + i * STEP_MS;
const iso = (ms) => new Date(ms).toISOString();
const minToSamples = (m) => Math.max(2, Math.ceil(m / CFG.sampleMinutes));

// ------------------------------------------------------------------ build signal instances
const TPL = buildTemplates();
const sigs = [];
ASSETS.forEach((_, ai) => {
  TPL.forEach((t) => {
    const R = t.hi - t.lo; // 10.0
    const base = t.lo + rr(t.frac[0], t.frac[1]) * R; // around 4.0 to 6.0
    sigs.push({
      ai, t, R, base,
      amp: rr(0.4, 0.9),  // gentle daily swing
      sd: rr(0.08, 0.18), // noise
      phase: rr(0, TWO_PI),
      cLo: 1.0,           // normal data is strictly clamped inside [1.0, 9.0]
      cHi: 9.0,
    });
  });
});
const S = sigs.length;

// ------------------------------------------------------------------ plan excursions (events + alerts)
const windows = sigs.map(() => []); // per-signal list of excursion windows
const GAP = 12;                     // min samples between two windows on the same signal
const overlaps = (si, s, e) => windows[si].some((w) => s <= w.e + GAP && e >= w.s - GAP);

function excursionValue(w, i) {
  const p = (i - w.s) / Math.max(1, w.e - w.s);
  const k = 0.2 + 0.8 * Math.sin(Math.PI * p); // peaks at mid-window
  return w.dir === 'high' ? w.baseThreshold + w.delta * k : w.baseThreshold - w.delta * k;
}

function tryPlace(sMin, sMax, dur, ongoing, forceSeverity = null) {
  if (!ongoing && sMax < sMin) return null;
  for (let tries = 0; tries < 300; tries++) {
    const si = ri(0, S - 1);
    const s = ongoing ? N - dur : ri(sMin, sMax);
    const e = ongoing ? N - 1 : s + dur - 1;
    if (s < 0 || e > (ongoing ? N - 1 : N - 2)) continue;
    if (overlaps(si, s, e)) continue;
    const t = sigs[si].t;
    const dir = t.dir === 'both' ? (rand() < 0.5 ? 'high' : 'low') : t.dir;

    // Severity: CRITICAL vs WARNING
    const isCritical = forceSeverity ? (forceSeverity === 'CRITICAL') : (rand() < 0.4);
    const severity = isCritical ? 'CRITICAL' : 'WARNING';

    let baseThreshold, threshold, delta;
    if (severity === 'CRITICAL') {
      // Surges past Warning (10/0) AND crosses Critical limit (12/-2)
      if (dir === 'high') {
        baseThreshold = t.hi;      // 10.0
        threshold = t.critHi;      // 12.0
        delta = rr(2.4, 4.5);      // peaks at 12.4 .. 14.5 (well above 12.0)
      } else {
        baseThreshold = t.lo;      // 0.0
        threshold = t.critLo;      // -2.0
        delta = rr(2.4, 4.5);      // drops to -2.4 .. -4.5 (well below -2.0)
      }
    } else {
      // WARNING: Crosses Warning (10/0) but strictly stays below Critical limit (12/-2)
      if (dir === 'high') {
        baseThreshold = t.hi;      // 10.0
        threshold = t.hi;          // 10.0
        delta = rr(0.4, 1.6);      // peaks at 10.4 .. 11.6 (between 10.0 and 12.0)
      } else {
        baseThreshold = t.lo;      // 0.0
        threshold = t.lo;          // 0.0
        delta = rr(0.4, 1.6);      // drops to -0.4 .. -1.6 (between 0.0 and -2.0)
      }
    }

    const w = {
      si, s, e, ongoing, dir, severity,
      baseThreshold, threshold, delta
    };

    let peak = excursionValue(w, s);
    for (let i = s; i <= e; i++) {
      const v = excursionValue(w, i);
      peak = dir === 'high' ? Math.max(peak, v) : Math.min(peak, v);
    }
    w.peak = peak;
    windows[si].push(w);
    return w;
  }
  return null;
}

const alertPlan = [];
const eventPlan = [];

// Alerts: 12 alerts across timeline; last 3 are ongoing at "now" (2 OPEN, 1 ACKNOWLEDGED)
for (let i = 0; i < CFG.alertCount; i++) {
  const ongoing = i >= CFG.alertCount - 3;
  const status = ongoing ? (i === CFG.alertCount - 3 ? 'ACKNOWLEDGED' : 'OPEN') : 'RESOLVED';
  const dur = minToSamples(rr(60, 240));
  const segLen = Math.floor(N / (CFG.alertCount - 3));
  const sMin = i * segLen + 2;
  const sMax = Math.min((i + 1) * segLen - dur - 3, N - dur - 3);
  // Realistic distribution of WARNING vs CRITICAL alerts
  const forceSeverity = (i % 2 === 0) ? 'WARNING' : 'CRITICAL';
  const w = tryPlace(sMin, sMax, dur, ongoing, forceSeverity);
  if (w) alertPlan.push({ w, status });
}

// Events: 3-4 per week (mix of WARNING and CRITICAL excursions)
const SPW = Math.round((7 * 24 * 60) / CFG.sampleMinutes);
const weeks = Math.ceil(N / SPW);
const eventsPerWeekCount = [];
for (let wk = 0; wk < weeks; wk++) {
  const want = ri(CFG.eventsPerWeek[0], CFG.eventsPerWeek[1]);
  let placed = 0;
  for (let k = 0; k < want; k++) {
    const dur = minToSamples(rr(30, 360));
    const sMin = wk * SPW + 1;
    const sMax = Math.min(wk * SPW + SPW - 1, N - dur - 3);
    const w = tryPlace(sMin, sMax, dur, false);
    if (w) { eventPlan.push({ w }); placed++; }
  }
  eventsPerWeekCount.push(placed);
}
windows.forEach((list) => list.sort((a, b) => a.s - b.s));

// ------------------------------------------------------------------ data generator (time-ordered, streamed)
const ids = new Int32Array(S);       // signal_id per signal (set after DB insert; dry-run uses index+1)
const winPtr = new Int32Array(S);
const cosPh = sigs.map((x) => Math.cos(x.phase));
const sinPh = sigs.map((x) => Math.sin(x.phase));
let rowsGenerated = 0;

function* rowChunks() {
  const STEPS_PER_CHUNK = 20;
  const logEvery = Math.max(1, Math.floor(N / 20));
  let buf = [];
  for (let i = 0; i < N; i++) {
    const t = tsAt(i);
    const ts = iso(t);
    const ang = (TWO_PI * (t % DAY_MS)) / DAY_MS;
    const sa = Math.sin(ang), ca = Math.cos(ang);
    for (let s = 0; s < S; s++) {
      const g = sigs[s];
      let v;
      const list = windows[s];
      let p = winPtr[s];
      while (p < list.length && list[p].e < i) p++;
      winPtr[s] = p;
      if (p < list.length && list[p].s <= i) {
        v = excursionValue(list[p], i);
      } else {
        v = g.base + g.amp * (sa * cosPh[s] + ca * sinPh[s]) + (rand() + rand() + rand() - 1.5) * 2 * g.sd;
        if (v < g.cLo) v = g.cLo; else if (v > g.cHi) v = g.cHi;
      }
      buf.push(ts + '\t' + ids[s] + '\t' + v.toFixed(3) + '\n');
    }
    rowsGenerated += S;
    if ((i + 1) % STEPS_PER_CHUNK === 0) { yield buf.join(''); buf = []; }
    if (i % logEvery === 0) console.log(`  ${Math.round((i / N) * 100)}%  (${rowsGenerated.toLocaleString()} rows)`);
  }
  if (buf.length) yield buf.join('');
}

// ------------------------------------------------------------------ db helpers
async function insertMany(client, table, cols, rows, chunk = 500, returning = '') {
  const out = [];
  for (let i = 0; i < rows.length; i += chunk) {
    const part = rows.slice(i, i + chunk);
    const params = [];
    const values = part.map((r, ri_) => {
      const ph = r.map((v, ci) => { params.push(v); return `$${ri_ * cols.length + ci + 1}`; });
      return `(${ph.join(',')})`;
    });
    const res = await client.query(`INSERT INTO ${table} (${cols.join(',')}) VALUES ${values.join(',')} ${returning}`, params);
    out.push(...res.rows);
  }
  return out;
}

function summary() {
  const critAlerts = alertPlan.filter((a) => a.w.severity === 'CRITICAL').length;
  const warnAlerts = alertPlan.filter((a) => a.w.severity === 'WARNING').length;
  const critEvents = eventPlan.filter((e) => e.w.severity === 'CRITICAL').length;
  const warnEvents = eventPlan.filter((e) => e.w.severity === 'WARNING').length;
  console.log(`Range      : ${iso(startMs)} -> ${iso(endMs)}`);
  console.log(`Samples    : ${N.toLocaleString()} timestamps every ${CFG.sampleMinutes} min`);
  console.log(`Assets     : ${ASSETS.length}   Signals: ${S} (${TPL.length} per asset)`);
  console.log(`Limits     : Warning [${WARN_MIN} .. ${WARN_MAX}], Critical [${CRIT_MIN} .. ${CRIT_MAX}]`);
  console.log(`Rows       : ${(N * S).toLocaleString()} in signal_data`);
  console.log(`Events     : ${eventPlan.length} (Warning: ${warnEvents}, Critical: ${critEvents})`);
  console.log(`Alerts     : ${alertPlan.length} (Warning: ${warnAlerts}, Critical: ${critAlerts})`);
}

// ------------------------------------------------------------------ main
async function connectWithRetry(retries = 30) {
  for (let i = 1; i <= retries; i++) {
    const c = new Client(CFG.pg);
    try {
      await c.connect();
      return c;
    } catch (e) {
      await c.end().catch(() => {});
      if (i === retries) throw e;
      console.log(`Waiting for database (${i}/${retries})...`);
      await new Promise((r) => setTimeout(r, 2000));
    }
  }
}

async function main() {
  summary();

  if (DRY_RUN) {
    for (let s = 0; s < S; s++) ids[s] = s + 1;
    console.log('\nDRY RUN: generating rows in memory (no DB)...');
    let bytes = 0;
    for (const chunk of rowChunks()) bytes += chunk.length;
    console.log(`Done. ${rowsGenerated.toLocaleString()} rows, ~${(bytes / 1048576).toFixed(0)} MB of COPY text.`);
    return;
  }

  const client = await connectWithRetry();
  try {
    const { rows: [{ count }] } = await client.query('SELECT count(*)::int AS count FROM assets');
    if (count > 0 && !RESET) {
      console.log('\nData already present in the database. Skipping seed. (Use --reset to wipe and reseed.)');
      return;
    }

    // everything below runs in one transaction: a failed run leaves the DB empty, never half-seeded
    await client.query('BEGIN');
    if (count > 0) {
      console.log('\nTruncating existing data...');
      await client.query('TRUNCATE alerts, events, signal_data, signals, assets RESTART IDENTITY CASCADE');
    }

    // assets
    const assetRows = await insertMany(client, 'assets', ['name', 'asset_type', 'location'],
      ASSETS.map((a) => [a.name, 'Motor', a.location]), 500, 'RETURNING asset_id, name');
    const assetId = Object.fromEntries(assetRows.map((r) => [r.name, r.asset_id]));

    // signals (120 per asset with dual thresholds: warning [0..10], critical [-2..12])
    const sigRows = await insertMany(client, 'signals',
      ['asset_id', 'name', 'unit', 'min_value', 'max_value', 'critical_min_value', 'critical_max_value'],
      sigs.map((g) => [
        assetId[ASSETS[g.ai].name],
        g.t.name,
        g.t.unit,
        g.t.lo,
        g.t.hi,
        g.t.critLo,
        g.t.critHi
      ]),
      500, 'RETURNING signal_id, asset_id, name');
    const sigKey = new Map(sigRows.map((r) => [`${r.asset_id}:${r.name}`, r.signal_id]));
    sigs.forEach((g, s) => {
      g.assetId = assetId[ASSETS[g.ai].name];
      g.signalId = sigKey.get(`${g.assetId}:${g.t.name}`);
      ids[s] = g.signalId;
    });
    console.log(`Inserted ${assetRows.length} assets, ${sigRows.length} signals.`);

    // raw time-series via COPY
    console.log('\nLoading signal_data (COPY)...');
    const t0 = Date.now();
    const copyStream = client.query(copyFrom('COPY signal_data (time, signal_id, value) FROM STDIN'));
    await pipeline(Readable.from(rowChunks(), { objectMode: false }), copyStream);
    console.log(`Loaded ${rowsGenerated.toLocaleString()} rows in ${((Date.now() - t0) / 1000).toFixed(0)}s.`);

    // events (with warning vs critical severity and threshold crossed)
    const evRows = eventPlan.map(({ w }) => {
      const g = sigs[w.si];
      const evType = (w.severity === 'CRITICAL' ? 'CRITICAL_' : 'WARNING_') + (w.dir === 'high' ? 'HIGH_EXCURSION' : 'LOW_EXCURSION');
      return [
        g.assetId,
        g.signalId,
        evType,
        iso(tsAt(w.s)),
        iso(tsAt(w.e + 1)),
        +w.peak.toFixed(3),
        w.threshold,
        w.severity
      ];
    });
    await insertMany(client, 'events',
      ['asset_id', 'signal_id', 'event_type', 'start_time', 'end_time', 'peak_value', 'threshold', 'severity'],
      evRows);

    // alerts (with severity WARNING vs CRITICAL and status OPEN / ACKNOWLEDGED / RESOLVED)
    const alRows = alertPlan.map(({ w, status }) => {
      const g = sigs[w.si];
      const trig = tsAt(w.s);
      const ackMs = trig + Math.min(rr(3, 20) * 60000, ((w.e - w.s) * STEP_MS) / 2);
      return [
        g.assetId,
        g.signalId,
        iso(trig),
        +excursionValue(w, w.s).toFixed(3),
        w.threshold,
        w.severity,
        status,
        status === 'OPEN' ? null : iso(ackMs),
        status === 'RESOLVED' ? iso(tsAt(w.e + 1)) : null
      ];
    });
    await insertMany(client, 'alerts',
      ['asset_id', 'signal_id', 'triggered_at', 'trigger_value', 'threshold_value', 'severity', 'status', 'acknowledged_at', 'resolved_at'],
      alRows);
    console.log(`Inserted ${evRows.length} events, ${alRows.length} alerts.`);

    await client.query('COMMIT');
    await client.query('ANALYZE signal_data');
    const { rows: [chk] } = await client.query(
      `SELECT (SELECT count(*) FROM signal_data) AS data_rows,
              (SELECT count(*) FROM signals) AS signals,
              (SELECT count(*) FROM events) AS events,
              (SELECT count(*) FROM alerts) AS alerts`);
    console.log('\nVerification:', chk);
    console.log('Done.');
  } catch (err) {
    await client.query('ROLLBACK').catch(() => {});
    throw err;
  } finally {
    await client.end();
  }
}

main().catch((err) => { console.error(err); process.exit(1); });