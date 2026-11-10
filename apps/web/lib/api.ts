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

// Response shapes (mirror src/FxOs.Api/Endpoints).
export type DevTenant = { slug: string; displayName: string; status: string; isDummy: boolean; schemaVersion: number | null };

export type TenantProfile = {
  slug: string;
  tenantId: string;
  displayName: string;
  baseCurrency: string;
  locale: "es-MX" | "en-US";
  timeZone: string;
  counts: { legalEntities: number; departments: number; users: number };
};

export type LegalEntity = { id: string; name: string; taxId: string | null; country: string; functionalCurrency: string };
export type Department = { id: string; name: string; legalEntityId: string | null; legalEntityName: string | null };
export type AuditEvent = { id: number; occurredAt: string; actor: string; action: string; entityType: string | null; entityId: string | null };
export type TenantUser = { id: string; email: string; displayName: string; role: "standard" | "admin"; departmentName: string | null };

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

export type BankAccount = {
  id: string;
  legalEntityId: string;
  legalEntityName: string;
  bankName: string;
  accountName: string;
  accountLast4: string;
  currency: string;
  kind: "operating" | "payroll" | "savings";
  feed: { provider: string | null; status: "connected" | "needs_attention" | "disconnected"; lastSyncedAt: string | null };
  balance: number;
  lastBookedOn: string | null;
};

export type MonthlyCashFlow = {
  month: string;
  currency: string;
  income: number;
  expense: number;
  net: number;
  transfersIn: number;
  transfersOut: number;
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
