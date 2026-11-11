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
  biggestMove: string;
  modelPoints: string; // legend: dots = values published by the model
};

export type ChartData = {
  locale: string;
  quote: string; // "MXN"
  history: { day: number; rate: number }[]; // day = days since 1970-01-01, oldest first
  /** Every published point of the headline model (7, 15, 30, 60, 90, 180 days), oldest first. */
  fan: { horizonDays: number; day: number; point: number; lower80: number; upper80: number; lower95: number; upper95: number }[];
  marker: { day: number; rate: number; label: string } | null; // biggest one-day move of the year
  labels: ChartLabels;
};

const Ctx = createContext<{ horizon: HorizonData; horizons: HorizonData[]; select: (key: string) => void } | null>(null);

export function HorizonProvider({ horizons, initial, children }: { horizons: HorizonData[]; initial: string; children: ReactNode }) {
  const [key, setKey] = useState(initial);
  const horizon = horizons.find((h) => h.key === key) ?? horizons[0]!;
  return <Ctx.Provider value={{ horizon, horizons, select: setKey }}>{children}</Ctx.Provider>;
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
const COLORS = { fix: "#6C10E0", expected: "#3E1F55", band80: "#D9C4F2", band95: "#EFE6FB", marker: "#E8582E" };

type Hover =
  | { kind: "past"; x: number; day: number; rate: number }
  | { kind: "future"; x: number; day: number; horizonDays: number; expected: number; lo80: number; hi80: number; lo95: number; hi95: number };

/**
 * The same number of days on both sides of today: the last N days of FIX, then the N-day forecast fan
 * (80% dark band, 95% light band, dashed expected value). Hover shows the values under the pointer.
 */
export function RateChart({ data }: { data: ChartData }) {
  const { horizon, horizons, select } = useHorizon();
  const [hover, setHover] = useState<Hover | null>(null);
  const svgRef = useRef<SVGSVGElement>(null);
  const L = data.labels;

  const nf = useMemo(() => new Intl.NumberFormat(data.locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), [data.locale]);
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

    const values = [...hist.map((p) => p.rate), ...data.fan.filter((p) => p.horizonDays <= horizon.days).flatMap((p) => [p.lower95, p.upper95])];
    const min = Math.min(...values);
    const max = Math.max(...values);
    const step = niceStep((max - min) / 4);
    const lo = Math.floor(min / step) * step;
    const hi = Math.ceil(max / step) * step;
    const y = (v: number) => H - ((v - lo) / (hi - lo)) * H;
    const yLast = y(last.rate);

    const line = hist.map((p, i) => `${i ? "L" : "M"}${x(p.day).toFixed(1)},${y(p.rate).toFixed(1)}`).join(" ");
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

    const marker = data.marker && data.marker.day >= start ? { x: x(data.marker.day), y: y(data.marker.rate), label: data.marker.label } : null;
    return { hist, start, end, last, x, y, split, yLast, line, grid, ticks, marker, pts,
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
      setHover({ kind: "past", x: geo.x(best.day), day: best.day, rate: best.rate });
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
          <path d={geo.line} fill="none" stroke={COLORS.fix} strokeWidth={2.5} strokeLinejoin="round" />
          <path d={geo.base} stroke={COLORS.expected} strokeWidth={2.5} strokeDasharray="6 5" fill="none" />
          {geo.pts.map((p) => (
            <circle key={p.horizonDays} cx={geo.x(p.day)} cy={geo.y(p.point)} r={3.5} fill={COLORS.expected} />
          ))}
          <path d={`M${geo.split} 0V${H}`} stroke={COLORS.expected} strokeDasharray="2 4" />
          <circle cx={geo.split} cy={geo.yLast} r={6} fill="#FFFFFF" stroke={COLORS.fix} strokeWidth={3} />
          {geo.marker && <circle cx={geo.marker.x} cy={geo.marker.y} r={9} fill="#FFFFFF" stroke={COLORS.marker} strokeWidth={3} />}
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
                <circle cx={hover.x} cy={geo.y(hover.rate)} r={5} fill={COLORS.fix} stroke="#FFFFFF" strokeWidth={2} />
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

        {hover?.kind === "past" && (
          <div className="fx-tip" style={{ left: `${tipLeft}%` }} role="status">
            <strong>{df.format(dateOf(hover.day))}</strong>
            <dl>
              <dt><i style={{ background: COLORS.fix }} />{L.fix}</dt><dd>{fmt(hover.rate)}</dd>
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

      <ul className="fx-legend">
        <li><i style={{ background: COLORS.fix }} />{L.fix}</li>
        <li><i className="is-dash" style={{ borderColor: COLORS.expected }} />{L.expected}</li>
        <li><i className="is-dot" style={{ background: COLORS.expected }} />{L.modelPoints}</li>
        <li><i className="is-band" style={{ background: COLORS.band80 }} />{L.range80}</li>
        <li><i className="is-band" style={{ background: COLORS.band95 }} />{L.range95}</li>
        {geo.marker && <li><i className="is-ring" style={{ borderColor: COLORS.marker }} />{L.biggestMove}</li>}
      </ul>
    </section>
  );
}

function niceStep(raw: number): number {
  const pow = 10 ** Math.floor(Math.log10(raw || 1));
  const n = raw / pow;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * pow;
}
