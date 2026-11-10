import { AlertBanner, Badge, PageHeader, StatCard } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { apiGet, type AuditEvent, type Department, type LegalEntity } from "@/lib/api";
import { formatDateTime, formatNumber } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";

export default async function Dashboard() {
  const { switcher, tenant, tenants, profile, locale, t } = await getSession();

  if (!tenant || !profile) {
    return (
      <AppShell active="dashboard">
        {switcher && tenants?.ok && tenants.data.length === 0 ? (
          <p className="subtitle">{t("dev.noTenants")}</p>
        ) : (
          <AlertBanner tone="info" title={t("dev.pickTenantTitle")}>
            {t("dev.pickTenantBody")}
          </AlertBanner>
        )}
      </AppShell>
    );
  }

  if (!profile.ok) {
    return (
      <AppShell active="dashboard">
        <p className="error" role="alert">
          {profile.message}
        </p>
      </AppShell>
    );
  }

  const [entities, departments, audit] = await Promise.all([
    apiGet<LegalEntity[]>("/api/tenant/legal-entities", tenant),
    apiGet<Department[]>("/api/tenant/departments", tenant),
    apiGet<AuditEvent[]>("/api/tenant/audit?limit=8", tenant),
  ]);
  const p = profile.data;

  return (
    <AppShell active="dashboard">
      <div className="stack">
        <PageHeader
          title={p.displayName}
          eyebrow={t("dashboard.eyebrow", { currency: p.baseCurrency, timeZone: p.timeZone })}
        />

        <div className="grid">
          <StatCard label={t("dashboard.legalEntities")} value={formatNumber(locale, p.counts.legalEntities)} />
          <StatCard label={t("dashboard.departments")} value={formatNumber(locale, p.counts.departments)} />
          <StatCard label={t("dashboard.users")} value={formatNumber(locale, p.counts.users)} />
        </div>

        <AlertBanner tone="info" kicker={t("dashboard.phaseKicker")} title={t("dashboard.phaseTitle")}>
          {t("dashboard.phaseBody")}
        </AlertBanner>

        <div className="grid">
          <section className="card">
            <h2>{t("dashboard.legalEntities")}</h2>
            <ul className="list">
              {entities.ok &&
                entities.data.map((e) => (
                  <li key={e.id}>
                    <span>{e.name}</span>
                    <span className="meta">
                      {e.country} · <Badge tone="neutral" dot={false} title={t("dashboard.functionalCurrency")}>{e.functionalCurrency}</Badge>
                    </span>
                  </li>
                ))}
            </ul>
          </section>

          <section className="card">
            <h2>{t("dashboard.departments")}</h2>
            <ul className="list">
              {departments.ok &&
                departments.data.map((d) => (
                  <li key={d.id}>
                    <span>{d.name}</span>
                    <span className="meta">{d.legalEntityName ?? t("dashboard.noEntity")}</span>
                  </li>
                ))}
            </ul>
          </section>

          <section className="card">
            <h2>{t("dashboard.recentActivity")}</h2>
            <ul className="list">
              {audit.ok && audit.data.length === 0 && <li className="meta">{t("dashboard.noActivity")}</li>}
              {audit.ok &&
                audit.data.map((a) => (
                  <li key={a.id}>
                    <span>{a.action}</span>
                    <span className="meta">{formatDateTime(locale, a.occurredAt, p.timeZone)}</span>
                  </li>
                ))}
            </ul>
          </section>
        </div>
      </div>
    </AppShell>
  );
}
