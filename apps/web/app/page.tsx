import { AlertBanner, Badge, PageHeader, StatCard } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { apiGet, type AuditEvent, type BankAccount, type Department, type LegalEntity, type MonthlyCashFlow } from "@/lib/api";
import { formatDate, formatDateTime, formatMoney, formatNumber, type MessageKey } from "@/lib/i18n";
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

  const [entities, departments, audit, accounts, cashFlow] = await Promise.all([
    apiGet<LegalEntity[]>("/api/tenant/legal-entities", tenant),
    apiGet<Department[]>("/api/tenant/departments", tenant),
    apiGet<AuditEvent[]>("/api/tenant/audit?limit=8", tenant),
    apiGet<BankAccount[]>("/api/tenant/bank-accounts", tenant),
    apiGet<MonthlyCashFlow[]>("/api/tenant/cash-flow/monthly?months=2", tenant),
  ]);
  const p = profile.data;

  // Cash by currency: balances are summed only within a currency, never converted.
  const byCurrency = new Map<string, { total: number; count: number; asOf: string | null }>();
  for (const a of accounts.ok ? accounts.data : []) {
    const c = byCurrency.get(a.currency) ?? { total: 0, count: 0, asOf: null };
    c.total += a.balance;
    c.count += 1;
    if (a.lastBookedOn && (!c.asOf || a.lastBookedOn > c.asOf)) c.asOf = a.lastBookedOn;
    byCurrency.set(a.currency, c);
  }
  const lastMonth = cashFlow.ok ? cashFlow.data.at(-1)?.month : undefined;
  const monthFlow = (currency: string) =>
    cashFlow.ok ? cashFlow.data.find((m) => m.month === lastMonth && m.currency === currency) : undefined;
  const monthLabel = lastMonth
    ? new Intl.DateTimeFormat(locale, { month: "long", year: "numeric", timeZone: "UTC" }).format(new Date(`${lastMonth}-01T00:00:00Z`))
    : "";

  return (
    <AppShell active="dashboard">
      <div className="stack">
        <PageHeader
          title={p.displayName}
          eyebrow={t("dashboard.eyebrow", { currency: p.baseCurrency, timeZone: p.timeZone })}
        />

        {byCurrency.size > 0 && (
          <div className="grid">
            {[...byCurrency.entries()].sort(([x], [y]) => (x === "USD" ? -1 : y === "USD" ? 1 : x.localeCompare(y))).map(([currency, c]) => {
              const flow = monthFlow(currency);
              return (
                <StatCard
                  key={currency}
                  label={t("cash.title", { currency })}
                  value={formatMoney(locale, c.total, currency)}
                  note={
                    flow
                      ? t("cash.month", { month: monthLabel, income: formatMoney(locale, flow.income, currency), expense: formatMoney(locale, flow.expense, currency) })
                      : t("cash.accounts", { count: c.count, date: c.asOf ? formatDate(locale, c.asOf) : "—" })
                  }
                />
              );
            })}
          </div>
        )}

        <div className="grid">
          <StatCard label={t("dashboard.legalEntities")} value={formatNumber(locale, p.counts.legalEntities)} />
          <StatCard label={t("dashboard.departments")} value={formatNumber(locale, p.counts.departments)} />
          <StatCard label={t("dashboard.users")} value={formatNumber(locale, p.counts.users)} />
        </div>

        <AlertBanner tone="info" kicker={t("dashboard.phaseKicker")} title={t("dashboard.phaseTitle")}>
          {t("dashboard.phaseBody")}
        </AlertBanner>

        <div className="grid">
          {accounts.ok && accounts.data.length > 0 && (
            <section className="card">
              <h2>{t("banks.title")}</h2>
              <ul className="list">
                {accounts.data.map((a) => (
                  <li key={a.id} className="account-row">
                    <span className="account-row__name">
                      {a.accountName}
                      <span className="meta">
                        {a.bankName} ••{a.accountLast4}
                        {a.feed.lastSyncedAt && ` · ${t("banks.synced", { time: formatDateTime(locale, a.feed.lastSyncedAt, p.timeZone) })}`}
                      </span>
                    </span>
                    <span className="account-row__value">
                      {formatMoney(locale, a.balance, a.currency)}
                      <Badge tone={a.feed.status === "connected" ? "green" : a.feed.status === "needs_attention" ? "orange" : "neutral"}>
                        {t(`feed.${a.feed.status}` as MessageKey)}
                      </Badge>
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          )}

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
