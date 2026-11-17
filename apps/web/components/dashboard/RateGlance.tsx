/** Mini chart for the dashboard rate card: last 30 days of FIX, then the model's 30-day fan (white on violet). */
export function RateGlance({
  history, fan, leftLabel, rightLabel,
}: {
  history: { day: number; rate: number }[];
  fan: { day: number; point: number; lower80: number; upper80: number }[];
  leftLabel: string;
  rightLabel: string;
}) {
  if (history.length < 2) return null;
  const W = 300, H = 70, SPLIT = 200;
  const last = history[history.length - 1]!;
  const first = history[0]!;
  const end = fan.length ? fan[fan.length - 1]!.day : last.day + 30;
  const vals = [...history.map((p) => p.rate), ...fan.flatMap((p) => [p.lower80, p.upper80])];
  const lo = Math.min(...vals), hi = Math.max(...vals);
  const y = (v: number) => H - 4 - ((v - lo) / (hi - lo || 1)) * (H - 8);
  const xh = (d: number) => ((d - first.day) / (last.day - first.day || 1)) * SPLIT;
  const xf = (d: number) => SPLIT + ((d - last.day) / (end - last.day || 1)) * (W - SPLIT);
  const line = history.map((p, i) => `${i ? "L" : "M"}${xh(p.day).toFixed(1)},${y(p.rate).toFixed(1)}`).join(" ");
  const path = [{ day: last.day, point: last.rate, lower80: last.rate, upper80: last.rate }, ...fan];
  const upper = path.map((p) => `${xf(p.day).toFixed(1)},${y(p.upper80).toFixed(1)}`);
  const lower = path.map((p) => `${xf(p.day).toFixed(1)},${y(p.lower80).toFixed(1)}`).reverse();
  const base = path.map((p) => `${xf(p.day).toFixed(1)},${y(p.point).toFixed(1)}`).join(" L");
  // Fixed height, stretched to the card's width; strokes keep their thickness, labels and the "today" dot are HTML.
  return (
    <span className="dash-rate__chart" aria-hidden="true">
      <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" width="100%" height="100%">
        <path d={`M${[...upper, ...lower].join(" L")} Z`} fill="#FFFFFF" fillOpacity={0.2} />
        <path d={line} fill="none" stroke="#FFFFFF" strokeWidth={2.5} strokeLinejoin="round" vectorEffect="non-scaling-stroke" />
        <path d={`M${base}`} stroke="#FFFFFF" strokeWidth={2.5} strokeDasharray="5 4" fill="none" vectorEffect="non-scaling-stroke" />
        <path d={`M${SPLIT} 0V${H}`} stroke="#FFFFFF" strokeOpacity={0.5} strokeDasharray="2 3" vectorEffect="non-scaling-stroke" />
      </svg>
      <span className="dash-rate__dot" style={{ left: `${(SPLIT / W) * 100}%`, top: `${(y(last.rate) / H) * 100}%` }} />
      <span className="dash-rate__labels"><span>{leftLabel}</span><span>{rightLabel}</span></span>
    </span>
  );
}
