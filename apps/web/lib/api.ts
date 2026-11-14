import { cookies, headers } from "next/headers";

/** Server-side only. The browser never talks to the API directly. */
const API_BASE_URL = process.env.FX_API_BASE_URL ?? "http://localhost:5080";

/** HTTP-only cookie with the session token; the API keeps only its hash. Sent to the API as a Bearer token. */
export const SESSION_COOKIE = "fx_session";

export type ApiResult<T> = { ok: true; data: T } | { ok: false; status: number; message: string };

async function sessionHeaders(): Promise<Record<string, string>> {
  const token = (await cookies()).get(SESSION_COOKIE)?.value;
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function send<T>(path: string, init: RequestInit): Promise<ApiResult<T>> {
  try {
    const res = await fetch(`${API_BASE_URL}${path}`, { ...init, cache: "no-store" });
    const body = (await res.json().catch(() => null)) as (T & { detail?: string; title?: string; code?: string }) | null;
    if (!res.ok) return { ok: false, status: res.status, message: body?.detail ?? body?.title ?? body?.code ?? res.statusText };
    return { ok: true, data: body as T };
  } catch {
    return { ok: false, status: 0, message: `API unreachable at ${API_BASE_URL}` };
  }
}

/** GET as the signed-in user (the API takes the company from the session). */
export async function apiGet<T>(path: string): Promise<ApiResult<T>> {
  return send<T>(path, { headers: { Accept: "application/json", ...(await sessionHeaders()) } });
}

/** POST as the signed-in user. */
export async function apiPost<T>(path: string, body: unknown = {}): Promise<ApiResult<T>> {
  return send<T>(path, {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...(await sessionHeaders()) },
    body: JSON.stringify(body),
  });
}

/** Raw call for the sign-in and password endpoints: status and JSON body, plus the browser's address and agent. */
export async function authCall<T>(
  method: "GET" | "POST", path: string, body?: unknown,
): Promise<{ status: number; data: T | null }> {
  const h = await headers();
  const forwarded = h.get("x-forwarded-for")?.split(",")[0]?.trim() || h.get("x-real-ip") || "";
  try {
    const res = await fetch(`${API_BASE_URL}${path}`, {
      method,
      cache: "no-store",
      headers: {
        Accept: "application/json",
        ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
        ...(forwarded ? { "X-Client-IP": forwarded } : {}),
        "X-Client-User-Agent": h.get("user-agent") ?? "",
        ...(await sessionHeaders()),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    return { status: res.status, data: (await res.json().catch(() => null)) as T | null };
  } catch {
    return { status: 0, data: null };
  }
}

// Response shapes (mirror src/FxOs.Api/Endpoints).
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

export type Role = "admin" | "approver" | "standard";

/** GET /api/auth/session: who is signed in and which company the session works in. */
export type SessionInfo = {
  user: { id: string; email: string; displayName: string };
  tenant: { id: string; slug: string; displayName: string; isDummy: boolean };
  role: Role;
  expiresAt: string;
};

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
