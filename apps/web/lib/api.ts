import { cookies } from "next/headers";

/** Server-side only. The browser never talks to the API directly. */
const API_BASE_URL = process.env.FX_API_BASE_URL ?? "http://localhost:5080";

export const TENANT_COOKIE = "fx_dev_tenant";
export const devSwitcherEnabled = () => process.env.FX_DEV_TENANT_SWITCHER === "true";

export type ApiResult<T> = { ok: true; data: T } | { ok: false; status: number; message: string };

export async function currentTenant(): Promise<string | undefined> {
  if (!devSwitcherEnabled()) return undefined;
  return (await cookies()).get(TENANT_COOKIE)?.value;
}

export async function apiGet<T>(path: string, tenant?: string): Promise<ApiResult<T>> {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (tenant) headers["X-Tenant"] = tenant;

  try {
    const res = await fetch(`${API_BASE_URL}${path}`, { headers, cache: "no-store" });
    if (!res.ok) {
      const body = (await res.json().catch(() => null)) as { detail?: string; title?: string } | null;
      return { ok: false, status: res.status, message: body?.detail ?? body?.title ?? res.statusText };
    }
    return { ok: true, data: (await res.json()) as T };
  } catch {
    return { ok: false, status: 0, message: `API unreachable at ${API_BASE_URL}` };
  }
}

export async function apiPost<T>(path: string, tenant: string, body: unknown = {}): Promise<ApiResult<T>> {
  try {
    const res = await fetch(`${API_BASE_URL}${path}`, {
      method: "POST",
      headers: { Accept: "application/json", "Content-Type": "application/json", "X-Tenant": tenant },
      body: JSON.stringify(body),
      cache: "no-store",
    });
    const data = (await res.json().catch(() => null)) as (T & { detail?: string; title?: string }) | null;
    if (!res.ok) return { ok: false, status: res.status, message: data?.detail ?? data?.title ?? res.statusText };
    return { ok: true, data: data as T };
  } catch {
    return { ok: false, status: 0, message: `API unreachable at ${API_BASE_URL}` };
  }
}

// Response shapes (mirror src/FxOs.Api/Endpoints).
export type DevTenant = { slug: string; displayName: string; status: string; isDummy: boolean; schemaVersion: number | null };

export type TenantProfile = {
  slug: string;
  tenantId: string;
  displayName: string;
  baseCurrency: string;
  locale: "es-MX" | "en-US";
  timeZone: string;
  counts: { users: number };
};

export type AuditEvent = { id: number; occurredAt: string; actor: string; action: string; entityType: string | null; entityId: string | null };
export type TenantUser = { id: string; email: string; displayName: string };

export type RateSource = {
  code: string;
  name: string;
  provider: string;
  seriesId: string;
  baseCurrency: string;
  quoteCurrency: string;
  isEnabled: boolean;
  firstDate: string | null;
  lastDate: string | null;
  points: number;
  lastLoad: { status: "succeeded" | "failed"; at: string; error: string | null } | null;
};

export type LatestRate = {
  source: string;
  baseCurrency: string;
  quoteCurrency: string;
  date: string;
  rate: number;
  revision: number;
  loadedAt: string;
  previous: { date: string; rate: number } | null;
  change: number | null;
  changePct: number | null;
};

export type RateHistory = {
  source: string;
  baseCurrency: string;
  quoteCurrency: string;
  from: string;
  to: string;
  points: { date: string; rate: number }[];
};

export type RateAnalytics = {
  source: string;
  date: string;
  rate: number;
  changes: { days: number; from: string; fromRate: number; change: number; changePct: number }[];
  volatility: { days30: number | null; days90: number | null };
  high52w: { date: string; rate: number };
  low52w: { date: string; rate: number };
  sma50: number | null;
  sma200: number | null;
  trend: "up" | "down" | "flat" | "unknown";
  previous: { date: string; rate: number } | null;
  streakWeeks: number;
  volatilityDetail: { level: "low" | "normal" | "high"; typicalDailyMove: number; peak12m: { month: string; vol: number } | null } | null;
  rateGap: { mxnSeries: string; mxnRatePct: number; usdSeries: string; usdRatePct: number; gapPts: number; correlation90d: number | null } | null;
};

export type ForecastModel = {
  code: string;
  version: string;
  name: string;
  description: string;
  isBenchmark: boolean;
  asOf: string;
  lastRate: number;
  publishedAt: string;
  points: { horizonDays: number; targetDate: string; point: number; lower80: number; upper80: number; lower95: number; upper95: number }[];
  backtest: { horizonDays: number; n: number; originsFrom: string; originsTo: string; mae: number; mape: number; coverage80: number; coverage95: number; maeVsNaive: number }[];
};

export type Forecasts = { source: string; primary: string; models: ForecastModel[] };

export type SyncRequest = { id: number; status: "pending" | "running" | "done" | "failed"; requestedAt: string; finishedAt: string | null; result: string | null };

export type HistorySummary = {
  source: string;
  firstDate: string | null;
  lastDate: string | null;
  observations: number;
  openGaps: number;
  duplicates: number;
  corrections: number;
  holidaysMarked: number;
  lastLoad: { status: "succeeded" | "failed"; at: string; error: string | null } | null;
  lastRequest: SyncRequest | null;
};

export type HistoryYear = {
  year: number;
  months: { month: string; days: number; average: number | null; low: number | null; high: number | null; revised: number; gaps: number }[];
};

export type DayStatus = "ok" | "revised" | "gap" | "holiday" | "holiday_marked" | "weekend";

export type HistoryMonth = {
  month: string;
  days: { date: string; rate: number | null; revision: number; loadedAt: string | null; status: DayStatus }[];
};

export type HistoryDay = {
  source: string;
  provider: string;
  seriesId: string;
  baseCurrency: string;
  quoteCurrency: string;
  date: string;
  status: DayStatus;
  rate: number | null;
  revision: number | null;
  loadedAt: string | null;
  rawRef: string | null;
  previous: { date: string; rate: number } | null;
  change: number | null;
  changePct: number | null;
  resolution: { resolution: string; note: string | null; resolvedBy: string; resolvedAt: string } | null;
  versions: { at: string; actor: string; oldRate: string | null; newRate: string | null; rawRef: string | null }[];
};

export type NewsFeed = {
  latest: { date: string; rate: number; previousDate: string; changePct: number; sigma: number; vol30Pct: number } | null;
  moves: { date: string; rate: number; changePct: number; sigma: number; vol30Pct: number }[];
  windows: {
    key: string;
    kind: "today" | "move";
    rateDate: string;
    from: string;
    to: string;
    fetchedAt: string;
    articles: { rank: number; lang: string; title: string; description: string | null; url: string; sourceName: string; sourceUrl: string | null; publishedAt: string }[];
  }[];
};
