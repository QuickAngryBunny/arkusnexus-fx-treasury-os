import { TenantSwitcher } from "@/components/TenantSwitcher";
import {
  apiGet,
  currentTenant,
  devSwitcherEnabled,
  type AuditEvent,
  type Department,
  type DevTenant,
  type LegalEntity,
  type TenantProfile,
} from "@/lib/api";
import { formatDateTime, t, type Locale } from "@/lib/i18n";

export const dynamic = "force-dynamic";

export default async function Home() {
  const switcher = devSwitcherEnabled();
  const tenant = await currentTenant();
  const tenants = switcher ? await apiGet<DevTenant[]>("/api/dev/tenants") : null;

  const [profile, entities, departments, audit] = tenant
    ? await Promise.all([
        apiGet<TenantProfile>("/api/tenant/profile", tenant),
        apiGet<LegalEntity[]>("/api/tenant/legal-entities", tenant),
        apiGet<Department[]>("/api/tenant/departments", tenant),
        apiGet<AuditEvent[]>("/api/tenant/audit?limit=8", tenant),
      ])
    : [null, null, null, null];

  const locale: Locale = profile?.ok ? profile.data.locale : "es-MX";

  return (
    <div className="shell">
      <header className="topbar">
        <div className="brand">
          {t(locale, "product")}
          <small>Treasury OS · Phase 0</small>
        </div>
        {switcher && tenants?.ok && (
          <TenantSwitcher
            tenants={tenants.data}
            current={tenant}
            label={t(locale, "devSwitcher")}
            placeholder={t(locale, "chooseTenant")}
          />
        )}
      </header>

      <main>
        <span className="notice">{t(locale, "dummyNotice")}</span>

        {!switcher && <p className="subtitle">{t(locale, "signInLater")}</p>}
        {tenants && !tenants.ok && (
          <p className="error">
            {t(locale, "apiDown")}: {tenants.message}
          </p>
        )}
        {tenants?.ok && tenants.data.length === 0 && <p className="subtitle">{t(locale, "noTenants")}</p>}

        {profile && !profile.ok && <p className="error">{profile.message}</p>}

        {profile?.ok && (
          <>
            <h1>{profile.data.displayName}</h1>
            <p className="subtitle">
              {t(locale, "overview")} · {t(locale, "baseCurrency")} {profile.data.baseCurrency} · {t(locale, "timeZone")}{" "}
              {profile.data.timeZone}
            </p>

            <div className="grid">
              <Stat label={t(locale, "legalEntities")} value={profile.data.counts.legalEntities} locale={locale} />
              <Stat label={t(locale, "departments")} value={profile.data.counts.departments} locale={locale} />
              <Stat label={t(locale, "users")} value={profile.data.counts.users} locale={locale} />
            </div>

            <div className="grid section">
              <section className="card">
                <h2>{t(locale, "legalEntities")}</h2>
                <ul className="list">
                  {entities?.ok &&
                    entities.data.map((e) => (
                      <li key={e.id}>
                        <span>{e.name}</span>
                        <span className="meta">
                          {e.country} · {e.functionalCurrency}
                        </span>
                      </li>
                    ))}
                </ul>
              </section>

              <section className="card">
                <h2>{t(locale, "departments")}</h2>
                <ul className="list">
                  {departments?.ok &&
                    departments.data.map((d) => (
                      <li key={d.id}>
                        <span>{d.name}</span>
                        <span className="meta">{d.legalEntityName ?? "—"}</span>
                      </li>
                    ))}
                </ul>
              </section>

              <section className="card">
                <h2>{t(locale, "recentActivity")}</h2>
                <ul className="list">
                  {audit?.ok &&
                    audit.data.map((a) => (
                      <li key={a.id}>
                        <span>{a.action}</span>
                        <span className="meta">{formatDateTime(locale, a.occurredAt, profile.data.timeZone)}</span>
                      </li>
                    ))}
                </ul>
              </section>
            </div>
          </>
        )}
      </main>
    </div>
  );
}

function Stat({ label, value, locale }: { label: string; value: number; locale: Locale }) {
  return (
    <section className="card">
      <h2>{label}</h2>
      <div className="stat">{new Intl.NumberFormat(locale).format(value)}</div>
    </section>
  );
}
