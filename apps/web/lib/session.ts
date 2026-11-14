import { cache } from "react";
import { cookies } from "next/headers";
import { apiGet, currentTenant, devSwitcherEnabled, type DevTenant, type TenantProfile, type TenantUser } from "@/lib/api";
import { DEFAULT_LOCALE, LOCALE_COOKIE, isLocale, translator, type Locale } from "@/lib/i18n";

/**
 * Per-request context shared by the layout, the app shell and pages (React cache dedupes the API calls).
 * Phase 0 has no sign-in: the viewer is the tenant's first dummy admin, and the developer switcher picks the tenant.
 */
export const getSession = cache(async () => {
  const switcher = devSwitcherEnabled();
  const tenant = await currentTenant();

  const [tenants, profile, users] = await Promise.all([
    switcher ? apiGet<DevTenant[]>("/api/dev/tenants") : Promise.resolve(null),
    tenant ? apiGet<TenantProfile>("/api/tenant/profile", tenant) : Promise.resolve(null),
    tenant ? apiGet<TenantUser[]>("/api/tenant/users", tenant) : Promise.resolve(null),
  ]);

  // The user's choice wins; otherwise the tenant's default; otherwise Spanish.
  const chosen = (await cookies()).get(LOCALE_COOKIE)?.value;
  const locale: Locale = isLocale(chosen) ? chosen : profile?.ok ? profile.data.locale : DEFAULT_LOCALE;

  // Dev switcher only: roles live in the control plane now (tenant_membership); sign-in (Sprint 2) replaces this.
  const viewer = users?.ok ? users.data[0] : undefined;

  return { switcher, tenant, tenants, profile, viewer, locale, t: translator(locale) };
});
