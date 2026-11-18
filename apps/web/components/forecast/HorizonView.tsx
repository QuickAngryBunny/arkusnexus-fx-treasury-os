"use client";

import { createContext, useContext, useMemo, useRef, useState, type PointerEvent, type ReactNode } from "react";
import { HeroCard, Segmented } from "@fxos/ui";

/** One forecast horizon, with every display string already formatted on the server (locale, 2 decimals). */
export type HorizonData = {
  key: string; // "7D" … "180D"
  pillLabel: string;
  days: number; // horizon length in calendar days; the chart shows the same number of days of history
  kicker: string; // "Pronóstico · 10 dic"
  base: string;
  note: string;
  chartTitle: string;
  chartAria: string;
  targetLabel: string;
  // Chart numbers (raw): target date as a day number and the forecast at that date.
  targetDay: number;
  point: number;
  lower80: number;
  upper80: number;
  lower95: number;
  upper95: number;
};

export type ChartLabels = {
  today: string;
  forecast: string;
  horizon: string;
  fix: string; // legend + tooltip: daily FIX
  expected: string; // legend + tooltip: expected value (dashed line)
  range80: string;
  range95: string;
  min: string;
  max: string;
  high: string; // hover: top of the 80% range
  low: string; // hover: bottom of the 80% range
  bigMove: string; // legend: flagged move marker
  modelPoints: string; // legend: dots = values published by the model
  pastForecast: string; // legend: orange line = forecast made {days} days before each past FIX
  madeOn: string; // tooltip: "Forecast made {date}"
  miss: string; // tooltip: FIX − forecast
  pickHint: string; // "Tap a marker, or Today, …"
  todayButton: string; // "Today · 11 Nov"
};

/** One selectable event for the News card: today, or a flagged move. Strings are formatted on the server. */
export type NewsEvent = {
  key: string; // "today" | "move:YYYY-MM-DD"
  kicker: string;
  rate: string;
  change: string;
  changeTone: "up" | "down" | "flat";
  title: string;
  why: string;
  badge: string;
  badgeTone: "big" | "normal";
  articles: { title: string; source: string; time: string; url: string }[]; // the most relevant first
  newsLabel: string; // "Most relevant headlines"
  note: string; // where the headlines come from, or why there are none
  historyHref: string;
  historyLabel: string;
  readLabel: string;
};

export type ChartData = {
  locale: string;
  /** Number formatting (the profile's number format); dates use `locale`. */
  numberLocale: string;
  quote: string; // "MXN"
  history: { day: number; rate: number }[]; // day = days since 1970-01-01, oldest first
  /** Every published point of the headline model (7, 15, 30, 60, 90, 180 days), oldest first. */
  fan: { horizonDays: number; day: number; point: number; lower80: number; upper80: number; lower95: number; upper95: number }[];
  /**
   * Past forecasts: for each past FIX, the headline model's forecast published `horizonDays` before it (only forecasts
   * the app actually made that day). The selected horizon's series is the orange line over the past.
   */
  past: { horizonDays: number; day: number; point: number; madeOn: number }[];
  /** Flagged moves (beyond ±2σ), drawn as clickable markers when inside the visible window. */
  moves: { key: string; day: number; rate: number; aria: string }[];
  labels: ChartLabels;
};

type Ctx = {
  horizon: HorizonData;
  horizons: HorizonData[];
  select: (key: string) => void;
  event: string;
  selectEvent: (key: string) => void;
};
const Ctx = createContext<Ctx | null>(null);

export function HorizonProvider({ horizons, initial, children }: { horizons: HorizonData[]; initial: string; children: ReactNode }) {
  const [key, setKey] = useState(initial);
  const [event, setEvent] = useState("today");
  const horizon = horizons.find((h) => h.key === key) ?? horizons[0]!;
  return <Ctx.Provider value={{ horizon, horizons, select: setKey, event, selectEvent: setEvent }}>{children}</Ctx.Provider>;
}

function useHorizon() {
  const value = useContext(Ctx);
  if (!value) throw new Error("HorizonProvider missing");
  return value;
}

/** The plum card in the "today" row: the selected horizon's base and 80% range. */
export function ForecastCard({ label, link }: { label: string; link?: ReactNode }) {
  const { horizon } = useHorizon();
  return (
    <HeroCard tone="plum" label={label} kicker={horizon.kicker} value={horizon.base} note={horizon.note}>
      {link}
    </HeroCard>
  );
}

const W = 720;
const H = 240;
const DAY_MS = 86_400_000;
const COLORS = { fix: "#6C10E0", expected: "#3E1F55", band80: "#D9C4F2", band95: "#EFE6FB", marker: "#E8582E", past: "#E8582E" };

type PastForecast = { point: number; madeOn: number };
type Hover =
  | { kind: "past"; x: number; day: number; rate: number; forecast: PastForecast | null }
  | { kind: "future"; x: number; day: number; horizonDays: number; expected: number; lo80: number; hi80: number; lo95: number; hi95: number };

/**
 * The same number of days on both sides of today: the last N days of FIX, then the N-day forecast fan
 * (80% dark band, 95% light band, dashed expected value). Hover shows the values under the pointer.
 */
export function RateChart({ data }: { data: ChartData }) {
  const { horizon, horizons, select, event, selectEvent } = useHorizon();
  const [hover, setHover] = useState<Hover | null>(null);
  const svgRef = useRef<SVGSVGElement>(null);
  const L = data.labels;

  const nf = useMemo(() => new Intl.NumberFormat(data.numberLocale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), [data.numberLocale]);
  const df = useMemo(() => new Intl.DateTimeFormat(data.locale, { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" }), [data.locale]);
  const tickFmt = useMemo(
    () => new Intl.DateTimeFormat(data.locale, horizon.days > 60 ? { month: "short", year: "2-digit", timeZone: "UTC" } : { day: "numeric", month: "short", timeZone: "UTC" }),
    [data.locale, horizon.days],
  );
  const dateOf = (day: number) => new Date(day * DAY_MS);

  const geo = useMemo(() => {
    const last = data.history[data.history.length - 1]!;
    const start = last.day - horizon.days;
    const end = last.day + horizon.days;
    // History inside the window, plus the last point before it so the line starts at the left edge.
    const firstIn = data.history.findIndex((p) => p.day >= start);
    const hist = data.history.slice(Math.max(0, firstIn - 1));
    const x = (day: number) => ((Math.max(day, start) - start) / (end - start)) * W;
    const split = x(last.day);

    // The forecast made `horizon.days` before each FIX in the window (the orange line).
    const past = (data.past ?? []).filter((p) => p.horizonDays === horizon.days && p.day >= start && p.day <= last.day);
    const pastByDay = new Map(past.map((p) => [p.day, p]));
    const values = [
      ...hist.map((p) => p.rate), ...past.map((p) => p.point),
      ...data.fan.filter((p) => p.horizonDays <= horizon.days).flatMap((p) => [p.lower95, p.upper95]),
    ];
    const min = Math.min(...values);
    const max = Math.max(...values);
    const step = niceStep((max - min) / 4);
    const lo = Math.floor(min / step) * step;
    const hi = Math.ceil(max / step) * step;
    const y = (v: number) => H - ((v - lo) / (hi - lo)) * H;
    const yLast = y(last.rate);

    const line = hist.map((p, i) => `${i ? "L" : "M"}${x(p.day).toFixed(1)},${y(p.rate).toFixed(1)}`).join(" ");
    const pastLine = past.map((p, i) => `${i ? "L" : "M"}${x(p.day).toFixed(1)},${y(p.point).toFixed(1)}`).join(" ");
    // The fan follows the model's published horizons (not a straight wedge), so every width shown is a model value.
    const pts = data.fan.filter((p) => p.horizonDays <= horizon.days);
    const today = { day: last.day, point: last.rate, lower80: last.rate, upper80: last.rate, lower95: last.rate, upper95: last.rate };
    const path = [today, ...pts];
    const edge = (key: "point" | "lower80" | "upper80" | "lower95" | "upper95") =>
      path.map((p) => `${x(p.day).toFixed(1)},${y(p[key]).toFixed(1)}`);
    const band = (lo: "lower80" | "lower95", hi: "upper80" | "upper95") =>
      `M${[...edge(hi), ...edge(lo).reverse()].join(" L")} Z`;
    const grid: { y: number; label: string }[] = [];
    for (let v = lo; v <= hi + step / 2; v += step) grid.push({ y: y(v), label: nf.format(v) });

    // Evenly spaced date labels on each side (fewer for short horizons), never repeating a label.
    const perSide = horizon.days <= 7 ? 2 : horizon.days <= 15 ? 3 : 4;
    const ticks: { left: number; label: string }[] = [];
    const seen = new Set<string>();
    for (const [from, k0] of [[start, 0], [last.day, 1]] as const) {
      for (let k = k0; k < perSide; k++) {
        const day = Math.round(from + (horizon.days * k) / perSide);
        const label = tickFmt.format(dateOf(day));
        if (!seen.has(label)) {
          seen.add(label);
          ticks.push({ left: (x(day) / W) * 100, label });
        }
      }
    }

    const markers = data.moves.filter((m) => m.day >= start).map((m) => ({ ...m, left: (x(m.day) / W) * 100, top: (y(m.rate) / H) * 100 }));
    return { hist, start, end, last, x, y, split, yLast, line, pastLine, pastByDay, grid, ticks, markers, pts,
      band95: band("lower95", "upper95"), band80: band("lower80", "upper80"),
      base: `M${edge("point").join(" L")}` };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data, horizon, nf, tickFmt]);

  const onMove = (e: PointerEvent<SVGRectElement>) => {
    const box = svgRef.current?.getBoundingClientRect();
    if (!box) return;
    const px = ((e.clientX - box.left) / box.width) * W;
    const day = geo.start + (px / W) * (geo.end - geo.start);
    if (day <= geo.last.day) {
      // Nearest stored FIX.
      let best = geo.hist[0]!;
      for (const p of geo.hist) if (Math.abs(p.day - day) < Math.abs(best.day - day)) best = p;
      const f = geo.pastByDay.get(best.day);
      setHover({ kind: "past", x: geo.x(best.day), day: best.day, rate: best.rate, forecast: f ? { point: f.point, madeOn: f.madeOn } : null });
    } else {
      // Snap to the nearest published model point: every value shown comes straight from the model.
      let best = geo.pts[0]!;
      for (const p of geo.pts) if (Math.abs(p.day - day) < Math.abs(best.day - day)) best = p;
      setHover({
        kind: "future", x: geo.x(best.day), day: best.day, horizonDays: best.horizonDays,
        expected: best.point, lo80: best.lower80, hi80: best.upper80, lo95: best.lower95, hi95: best.upper95,
      });
    }
  };

  const fmt = (v: number) => `${nf.format(v)} ${data.quote}`;
  const tipLeft = hover ? Math.min(Math.max((hover.x / W) * 100, 12), 88) : 0;

  return (
    <section className="card fx-chart" aria-label={horizon.chartTitle}>
      <div className="fx-chart__head">
        <h2>{horizon.chartTitle}</h2>
        <Segmented
          label={L.horizon}
          options={horizons.map((h) => ({ value: h.key, label: h.pillLabel }))}
          value={horizon.key}
          onValueChange={(k) => {
            setHover(null);
            select(k);
          }}
          onWhite
        />
      </div>

      <div className="fx-chart__plot">
        <svg ref={svgRef} viewBox={`0 0 ${W} ${H}`} width="100%" role="img" aria-label={horizon.chartAria} className="fx-chart__svg">
          <rect x={geo.split} y={0} width={W - geo.split} height={H} fill="#FBF8FE" />
          {geo.grid.map((g) => (
            <path key={g.label} d={`M0 ${g.y.toFixed(1)}H${W}`} stroke="#EFE6FB" />
          ))}
          <path d={geo.band95} fill={COLORS.band95} />
          <path d={geo.band80} fill={COLORS.band80} />
          {geo.pastLine && <path d={geo.pastLine} fill="none" stroke={COLORS.past} strokeWidth={2.5} strokeLinejoin="round" />}
          <path d={geo.line} fill="none" stroke={COLORS.fix} strokeWidth={2.5} strokeLinejoin="round" />
          <path d={geo.base} stroke={COLORS.expected} strokeWidth={2.5} strokeDasharray="6 5" fill="none" />
          {geo.pts.map((p) => (
            <circle key={p.horizonDays} cx={geo.x(p.day)} cy={geo.y(p.point)} r={3.5} fill={COLORS.expected} />
          ))}
          <path d={`M${geo.split} 0V${H}`} stroke={COLORS.expected} strokeDasharray="2 4" />
          <circle cx={geo.split} cy={geo.yLast} r={6} fill="#FFFFFF" stroke={COLORS.fix} strokeWidth={3} />
          <g fontFamily="inherit" fontSize={11} fill="#6B6378">
            {geo.grid.map((g) => (
              <text key={g.label} x={4} y={g.y - 4}>{g.label}</text>
            ))}
            <text x={geo.split + 8} y={16} fontWeight={700} fill="#4B1499">{L.forecast}</text>
          </g>

          {hover && (
            <g pointerEvents="none">
              <path d={`M${hover.x.toFixed(1)} 0V${H}`} stroke="#3E1F55" strokeOpacity={0.35} />
              {hover.kind === "past" ? (
                <>
                  {hover.forecast && <circle cx={hover.x} cy={geo.y(hover.forecast.point)} r={5} fill={COLORS.past} stroke="#FFFFFF" strokeWidth={2} />}
                  <circle cx={hover.x} cy={geo.y(hover.rate)} r={5} fill={COLORS.fix} stroke="#FFFFFF" strokeWidth={2} />
                </>
              ) : (
                <>
                  <circle cx={hover.x} cy={geo.y(hover.hi80)} r={4} fill="#FFFFFF" stroke={COLORS.expected} strokeWidth={2} />
                  <circle cx={hover.x} cy={geo.y(hover.expected)} r={5} fill={COLORS.expected} stroke="#FFFFFF" strokeWidth={2} />
                  <circle cx={hover.x} cy={geo.y(hover.lo80)} r={4} fill="#FFFFFF" stroke={COLORS.expected} strokeWidth={2} />
                </>
              )}
            </g>
          )}
          <rect x={0} y={0} width={W} height={H} fill="transparent" onPointerMove={onMove} onPointerDown={onMove} onPointerLeave={() => setHover(null)} />
        </svg>

        {geo.markers.map((m) => (
          <button
            key={m.key}
            type="button"
            className={`fx-marker${event === m.key ? " is-on" : ""}`}
            style={{ left: `${m.left}%`, top: `${m.top}%` }}
            aria-label={m.aria}
            aria-pressed={event === m.key}
            onClick={() => selectEvent(m.key)}
          />
        ))}

        {hover?.kind === "past" && (
          <div className="fx-tip" style={{ left: `${tipLeft}%` }} role="status">
            <strong>{df.format(dateOf(hover.day))}</strong>
            <dl>
              <dt><i style={{ background: COLORS.fix }} />{L.fix}</dt><dd>{fmt(hover.rate)}</dd>
              {hover.forecast && (
                <>
                  <dt><i style={{ background: COLORS.past }} />{L.madeOn.replace("{date}", df.format(dateOf(hover.forecast.madeOn)))}</dt>
                  <dd>{fmt(hover.forecast.point)}</dd>
                  <dt>{L.miss}</dt>
                  <dd>{new Intl.NumberFormat(data.numberLocale, { minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: "exceptZero" }).format(hover.rate - hover.forecast.point)}</dd>
                </>
              )}
            </dl>
          </div>
        )}

        {hover?.kind === "future" && (() => {
          // Labels sit at the dots: high above, expected on the dashed line, low below (80% range).
          const left = Math.min(Math.max((hover.x / W) * 100, 9), 91);
          const pct = (v: number) => (geo.y(v) / H) * 100;
          const mid = pct(hover.expected);
          const top = Math.min(pct(hover.hi80), mid - 13);
          const bottom = Math.max(pct(hover.lo80), mid + 13);
          return (
            <div className="fx-fan-tip" role="status" aria-live="polite">
              <span className="fx-fan-tip__date" style={{ left: `${left}%` }}>{hover.horizonDays}D · {df.format(dateOf(hover.day))}</span>
              <span className="fx-fan-tip__pill is-range is-high" style={{ left: `${left}%`, top: `${top}%` }}>
                {L.high} <b>{nf.format(hover.hi80)}</b>
              </span>
              <span className="fx-fan-tip__pill is-expected" style={{ left: `${left}%`, top: `${mid}%` }}>
                {L.expected} <b>{fmt(hover.expected)}</b>
              </span>
              <span className="fx-fan-tip__pill is-range is-low" style={{ left: `${left}%`, top: `${bottom}%` }}>
                {L.low} <b>{nf.format(hover.lo80)}</b>
              </span>
            </div>
          );
        })()}
      </div>

      <div className="fx-chart__ticks" aria-hidden="true">
        {geo.ticks
          .filter((t) => Math.abs(t.left - (geo.split / W) * 100) > 7 && t.left < 93)
          .map((t, i) => (
            <span key={t.label + t.left} className={i > 0 ? "is-mid" : undefined} style={{ left: `${t.left}%`, ...(i === 0 ? { transform: "none" } : {}) }}>
              {t.label}
            </span>
          ))}
        <span className="fx-chart__today" style={{ left: `${(geo.split / W) * 100}%` }}>{L.today}</span>
        <span className="fx-chart__target">{horizon.targetLabel}</span>
      </div>

      <div className="fx-pick">
        <span>{L.pickHint}</span>
        <button type="button" className={`fx-today-btn${event === "today" ? " is-on" : ""}`} aria-pressed={event === "today"} onClick={() => selectEvent("today")}>
          {L.todayButton}
        </button>
      </div>

      <ul className="fx-legend">
        <li><i style={{ background: COLORS.fix }} />{L.fix}</li>
        {geo.pastLine && <li><i style={{ background: COLORS.past }} />{L.pastForecast.replace("{days}", String(horizon.days))}</li>}
        <li><i className="is-dash" style={{ borderColor: COLORS.expected }} />{L.expected}</li>
        <li><i className="is-dot" style={{ background: COLORS.expected }} />{L.modelPoints}</li>
        <li><i className="is-band" style={{ background: COLORS.band80 }} />{L.range80}</li>
        <li><i className="is-band" style={{ background: COLORS.band95 }} />{L.range95}</li>
        {geo.markers.length > 0 && <li><i className="is-ring" style={{ borderColor: COLORS.marker }} />{L.bigMove}</li>}
      </ul>
    </section>
  );
}

/** News widget: today's headlines, or those of the selected marker's date (stored by the worker, never live). */
export function NewsCard({ events }: { events: NewsEvent[] }) {
  const { event } = useHorizon();
  const ev = events.find((e) => e.key === event) ?? events[0];
  if (!ev) return null;
  const top = ev.articles[0];
  return (
    <article className="card fx-why" aria-live="polite">
      <span className="fx-kicker fx-kicker--brand">{ev.kicker}</span>
      <div className="fx-why__value">
        <span className="fx-why__rate">{ev.rate}</span>
        <span className={ev.changeTone === "up" ? "fx-up" : ev.changeTone === "down" ? "fx-down" : "muted"}>{ev.change}</span>
      </div>
      <strong className="fx-why__title">{ev.title}</strong>
      <p className="fx-why__body">{ev.why}</p>
      {top && (
        <a className="fx-news-top" href={top.url} target="_blank" rel="noopener noreferrer">
          <span className="fx-news-top__text">
            <span className="fx-news-top__label">{ev.newsLabel}</span>
            <strong>{top.title}</strong>
            <span className="fx-news-meta">
              {top.source} · {top.time} · <span className={`fx-sigma fx-sigma--${ev.badgeTone}`}>{ev.badge}</span>
            </span>
          </span>
          <span className="fx-news-read">{ev.readLabel} ↗</span>
        </a>
      )}
      <span className="caption">{ev.note}</span>
      <a href={ev.historyHref} className="fx-why__link">{ev.historyLabel}</a>
    </article>
  );
}

/** One big move in the mobile history card (layout B). Text is formatted on the server. */
export type MobileMove = {
  key: string; // "move:YYYY-MM-DD" (shared with the News card selection)
  day: number;
  rate: number;
  n: number; // marker number, chronological
  title: string;
  date: string;
  rateLabel: string;
  change: string;
  up: boolean;
  why: string;
  headline: { title: string; source: string; url: string } | null;
};

export type MobileText = {
  kicker: string; // "Where it could go"
  by: Record<string, string>; // horizon key → "by 10 Dec"
  todayLabel: string; // "Today 18.62"
  chance: Record<string, string>; // horizon key → "8 in 10 chance between … and …"
  fanAria: Record<string, string>;
  historyTitle: string; // "Last 12 months"
  lowHigh: string; // "low 16.87 · high 18.81"
  firstMonth: string;
  midMonth: string;
  today: string;
  historyAria: string;
  moreMoves: string | null; // link text when more moves exist than are listed
  moreHref: string;
  noMoves: string;
  readLabel: string;
};

const MOBILE_HORIZONS = ["7D", "30D", "90D", "180D"];

/**
 * Phone layout "B · Forecast and history as two cards": a plum forecast card (horizon pills, value by date, fan with
 * high / expected / low at its edge) and a white 12-month card (sparkline with numbered big moves and a list of them).
 */
export function MobileForecast({ data, moves, text }: { data: ChartData; moves: MobileMove[]; text: MobileText }) {
  const { horizon, horizons, select, event, selectEvent } = useHorizon();
  const [open, setOpen] = useState<string | null>(null);
  const nf = useMemo(() => new Intl.NumberFormat(data.locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), [data.locale]);
  const pills = horizons.filter((h) => MOBILE_HORIZONS.includes(h.key));
  const active = pills.some((h) => h.key === horizon.key) ? horizon : (pills.find((h) => h.key === "30D") ?? horizon);

  const fan = useMemo(() => {
    const W = 330, H = 150, X0 = 8, X1 = 272;
    const last = data.history[data.history.length - 1]!;
    const pts = data.fan.filter((p) => p.horizonDays <= active.days);
    const end = pts.length ? pts[pts.length - 1]! : null;
    if (!end) return null;
    const lo = Math.min(last.rate, ...pts.map((p) => p.lower95)), hi = Math.max(last.rate, ...pts.map((p) => p.upper95));
    const pad = (hi - lo) * 0.06;
    const y = (v: number) => H - 10 - ((v - (lo - pad)) / (hi - lo + 2 * pad)) * (H - 20);
    const x = (day: number) => X0 + ((day - last.day) / (end.day - last.day || 1)) * (X1 - X0);
    const path = [{ day: last.day, point: last.rate, lower80: last.rate, upper80: last.rate, lower95: last.rate, upper95: last.rate }, ...pts];
    const edge = (k: "point" | "lower80" | "upper80" | "lower95" | "upper95") => path.map((p) => `${x(p.day).toFixed(1)},${y(p[k]).toFixed(1)}`);
    const band = (a: "lower80" | "lower95", b: "upper80" | "upper95") => `M${[...edge(b), ...edge(a).reverse()].join(" L")} Z`;
    // Edge labels: keep at least 14px apart so they never overlap.
    let yHi = y(end.upper80), yMid = y(end.point), yLo = y(end.lower80);
    yHi = Math.min(yHi, yMid - 14);
    yLo = Math.max(yLo, yMid + 14);
    return { W, H, X0, y0: y(last.rate), band95: band("lower95", "upper95"), band80: band("lower80", "upper80"),
      line: `M${edge("point").join(" L")}`, yHi, yMid, yLo, end };
  }, [data, active]);

  const spark = useMemo(() => {
    const W = 330, H = 110;
    const pts = data.history;
    if (pts.length < 2) return null;
    const first = pts[0]!.day, lastDay = pts[pts.length - 1]!.day;
    const lo = Math.min(...pts.map((p) => p.rate)), hi = Math.max(...pts.map((p) => p.rate));
    const x = (d: number) => ((d - first) / (lastDay - first || 1)) * W;
    const y = (v: number) => H - 12 - ((v - lo) / (hi - lo || 1)) * (H - 24);
    return { W, H, x, y, line: pts.map((p, i) => `${i ? "L" : "M"}${x(p.day).toFixed(1)},${y(p.rate).toFixed(1)}`).join(" ") };
  }, [data]);

  return (
    <div className="fx-mobile">
      {fan && (
        <section className="fx-m-forecast" aria-label={text.kicker}>
          <span className="fx-m-kicker">{text.kicker}</span>
          <div role="radiogroup" aria-label={data.labels.horizon} className="fx-m-pills">
            {pills.map((h) => (
              <button key={h.key} type="button" role="radio" aria-checked={h.key === active.key} onClick={() => select(h.key)}>
                {h.pillLabel}
              </button>
            ))}
          </div>
          <div className="fx-m-value">
            <strong>{active.base}</strong>
            <span>{text.by[active.key]}</span>
          </div>
          <svg viewBox={`0 0 ${fan.W} ${fan.H}`} width="100%" role="img" aria-label={text.fanAria[active.key]} className="fx-m-fan">
            <path d={fan.band95} fill="#FFFFFF" fillOpacity={0.12} />
            <path d={fan.band80} fill="#FFFFFF" fillOpacity={0.22} />
            <path d={fan.line} stroke="#FFFFFF" strokeWidth={2.5} strokeDasharray="6 5" fill="none" />
            <circle cx={fan.X0} cy={fan.y0} r={6} fill="#3E1F55" stroke="#FFFFFF" strokeWidth={3} />
            <g fontFamily="inherit" fontSize={12} fontWeight={700} fill="#FFFFFF" textAnchor="end">
              <text x={326} y={fan.yHi + 4}>{nf.format(fan.end.upper80)}</text>
              <text x={326} y={fan.yMid + 4}>{nf.format(fan.end.point)}</text>
              <text x={326} y={fan.yLo + 4}>{nf.format(fan.end.lower80)}</text>
            </g>
            <text x={16} y={Math.min(fan.y0 + 22, fan.H - 2)} fontFamily="inherit" fontSize={12} fontWeight={700} fill="#D9C4F2">
              {text.todayLabel}
            </text>
          </svg>
          <span className="fx-m-note">{text.chance[active.key]}</span>
        </section>
      )}

      {spark && (
        <section className="fx-m-history" aria-label={text.historyTitle}>
          <div className="fx-m-history__head">
            <strong>{text.historyTitle}</strong>
            <span>{text.lowHigh}</span>
          </div>
          <svg viewBox={`0 0 ${spark.W} ${spark.H}`} width="100%" role="img" aria-label={text.historyAria} className="fx-m-spark">
            <path d={spark.line} fill="none" stroke="#6C10E0" strokeWidth={2.5} strokeLinejoin="round" />
            {moves.map((m) => {
              const on = event === m.key;
              return (
                <g key={m.key}>
                  <circle cx={spark.x(m.day)} cy={spark.y(m.rate)} r={10} fill={on ? "#E8582E" : "#FFFFFF"} stroke="#E8582E" strokeWidth={2.5} />
                  <text x={spark.x(m.day)} y={spark.y(m.rate) + 4} textAnchor="middle" fontFamily="inherit" fontSize={11} fontWeight={800} fill={on ? "#FFFFFF" : "#A33A16"}>
                    {m.n}
                  </text>
                </g>
              );
            })}
          </svg>
          <div className="fx-m-axis" aria-hidden="true"><span>{text.firstMonth}</span><span>{text.midMonth}</span><strong>{text.today}</strong></div>
          {moves.length === 0 ? (
            <p className="fx-m-empty">{text.noMoves}</p>
          ) : (
            <div className="fx-m-moves">
              {moves.map((m) => {
                const isOpen = open === m.key;
                return (
                  <div key={m.key} className={`fx-m-move${isOpen ? " is-open" : ""}`}>
                    <button
                      type="button"
                      aria-expanded={isOpen}
                      onClick={() => {
                        setOpen(isOpen ? null : m.key);
                        selectEvent(isOpen ? "today" : m.key);
                      }}
                    >
                      <span className="fx-m-move__n" aria-hidden="true">{m.n}</span>
                      <span className="fx-m-move__text"><strong>{m.title}</strong><span>{m.date} · {m.rateLabel}</span></span>
                      <span className={m.up ? "fx-up" : "fx-down"}>{m.change}</span>
                    </button>
                    {isOpen && (
                      <div className="fx-m-move__body">
                        <p>{m.why}</p>
                        {m.headline && (
                          <a href={m.headline.url} target="_blank" rel="noopener noreferrer">
                            {m.headline.title} <span>· {m.headline.source} · {text.readLabel} ↗</span>
                          </a>
                        )}
                      </div>
                    )}
                  </div>
                );
              })}
              {text.moreMoves && <a href={text.moreHref} className="fx-why__link">{text.moreMoves}</a>}
            </div>
          )}
        </section>
      )}
    </div>
  );
}

function niceStep(raw: number): number {
  const pow = 10 ** Math.floor(Math.log10(raw || 1));
  const n = raw / pow;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * pow;
}
