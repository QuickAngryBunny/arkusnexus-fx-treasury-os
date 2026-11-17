/**
 * Live odds in the rate alert drawer (P2-04). Mirrors src/FxOs.Platform/Alerts/RateAlertMath.cs so the number you see
 * while typing matches what the API stores and emails; the API's value is the one that counts.
 */
export type AlertDirection = "above" | "below";
export type Horizon = { days: number; point: number; lower80: number; upper80: number };

const Z80 = 1.2815515655446004;

export const LOOK_AHEADS = [7, 15, 30] as const;
export type LookAhead = (typeof LOOK_AHEADS)[number];

export function isPast(direction: AlertDirection, rate: number, fix: number): boolean {
  return direction === "above" ? fix >= rate : fix <= rate;
}

export function normalCdf(z: number): number {
  const t = 1 / (1 + 0.2316419 * Math.abs(z));
  const d = 0.3989422804014327 * Math.exp((-z * z) / 2);
  const p = d * t * (0.31938153 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
  return z > 0 ? 1 - p : p;
}

/** Chance (0–1) of being at or past `rate` on some day within `days` calendar days, read from the stored horizons. */
export function chanceWithin(direction: AlertDirection, rate: number, lastRate: number, horizons: Horizon[], days: number): number {
  if (isPast(direction, rate, lastRate)) return 1;
  const points = horizons.filter((h) => h.days > 0).sort((a, b) => a.days - b.days);
  if (points.length === 0 || days <= 0) return 0;
  const above = direction === "above";
  const side = (h: Horizon) => Math.max(1e-6, (above ? h.upper80 - h.point : h.point - h.lower80) / Z80);

  let best = 0;
  for (let k = 1; k <= days; k++) {
    let prevDay = 0, prevMean = lastRate, prevVar = 0;
    let mean = NaN, sd = NaN;
    for (const h of points) {
      const s = side(h);
      if (k <= h.days) {
        const f = (k - prevDay) / (h.days - prevDay);
        mean = prevMean + (h.point - prevMean) * f;
        sd = Math.sqrt(Math.max(prevVar + (s * s - prevVar) * f, 1e-12));
        break;
      }
      prevDay = h.days;
      prevMean = h.point;
      prevVar = s * s;
    }
    if (Number.isNaN(mean)) {
      mean = prevMean;
      sd = Math.sqrt(Math.max((prevVar * k) / points[points.length - 1].days, 1e-12));
    }
    const z = (rate - mean) / sd;
    best = Math.max(best, above ? 1 - normalCdf(z) : normalCdf(z));
  }
  return Math.min(1, Math.max(0, best));
}

/** Reads a typed rate in either number format ("19.25", "19,25"); null when it isn't a positive number. */
export function parseRate(text: string): number | null {
  const s = text.trim().replace(/\s/g, "");
  if (!s) return null;
  const normalized = s.includes(",") && !s.includes(".") ? s.replace(",", ".") : s.replace(/,/g, "");
  if (!/^\d+(\.\d+)?$/.test(normalized)) return null;
  const n = Number(normalized);
  return n > 0 ? n : null;
}
