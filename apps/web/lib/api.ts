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
