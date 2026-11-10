import { AlertBanner, PageHeader, StatCard } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { apiGet, type LatestRate, type RateHistory, type RateSource } from "@/lib/api";
import { formatDate, formatDateTime, formatNumber, formatRate, formatSigned } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";

const SOURCE = "banxico_fix";

/** Forecast Intelligence, Phase 1 slice: today's FIX and recent history from the tenant's own rate copy. */
export default async function ForecastPage() {
  const { tenant, profile, locale, t } = await getSession();
  const header = <PageHeader title={t("nav.forecast")} eyebrow={t("fx.eyebrow")} />;

  if (!tenant || !profile?.ok) {
    return (
      <AppShell active="forecast">
        <div className="stack">
          {header}
          <AlertBanner tone="info" title={t("dev.pickTenantTitle")}>{t("dev.pickTenantBody")}</AlertBanner>
        </div>
      </AppShell>
    );
  }

  const [latest, sources] = await Promise.all([
    apiGet<LatestRate>(`/api/tenant/rates/${SOURCE}/latest`, tenant),
    apiGet<RateSource[]>("/api/tenant/rates/sources", tenant),
  ]);
  const source = sources.ok ? sources.data.find((s) => s.code === SOURCE) : undefined;
  const failedLoad = source?.lastLoad?.status === "failed" ? source.lastLoad : null;

  if (!latest.ok) {
    return (
      <AppShell active="forecast">
        <div className="stack">
          {header}
          {latest.status === 404 ? (
            <AlertBanner tone="brand" title={t("fx.emptyTitle")}>{t("fx.emptyBody")}</AlertBanner>
          ) : (
            <p className="error" role="alert">{latest.message}</p>
          )}
        </div>
      </AppShell>
    );
  }

  const r = latest.data;
  const from = new Date(Date.parse(`${r.date}T00:00:00Z`) - 30 * 86_400_000).toISOString().slice(0, 10);
  const history = await apiGet<RateHistory>(`/api/tenant/rates/${SOURCE}?from=${from}&to=${r.date}`, tenant);
  const pair = { base: r.baseCurrency, quote: r.quoteCurrency };

  return (
    <AppShell active="forecast">
      <div className="stack">
        {header}

        {failedLoad && (
          <AlertBanner tone="danger" title={t("fx.lastLoadFailed", { error: failedLoad.error ?? "?" })} />
        )}

        <div className="grid">
          <StatCard
            label={`${t("fx.today")} · ${t("fx.perUnit", pair)}`}
            value={`${formatRate(locale, r.rate)} ${r.quoteCurrency}`}
            note={
              r.previous && r.change !== null && r.changePct !== null
                ? t("fx.vsPrevious", {
                    change: formatSigned(locale, r.change, 4),
                    pct: formatSigned(locale, r.changePct, 2, "%"),
                    date: formatDate(locale, r.previous.date),
                  })
                : undefined
            }
            // A stronger peso (fewer MXN per USD) is shown neutral: whether it is good depends on the exposure.
            tone="neutral"
          />
          <StatCard label={t("fx.fixDate")} value={formatDate(locale, r.date, "long")} />
          {source && source.firstDate && (
            <StatCard
              label={t("fx.history")}
              value={t("fx.historyValue", { count: formatNumber(locale, source.points) })}
              note={t("fx.historySince", { date: formatDate(locale, source.firstDate) })}
            />
          )}
        </div>

        <section className="card">
          <h2>{t("fx.recent")}</h2>
          {history.ok ? (
            <table className="table">
              <thead>
                <tr>
                  <th scope="col">{t("fx.date")}</th>
                  <th scope="col" className="num">{t("fx.rate", pair)}</th>
                </tr>
              </thead>
              <tbody>
                {[...history.data.points].reverse().map((p) => (
                  <tr key={p.date}>
                    <td>{formatDate(locale, p.date)}</td>
                    <td className="num">{formatRate(locale, p.rate)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <p className="error" role="alert">{history.message}</p>
          )}
        </section>

        <p className="caption">
          {t("fx.sourceNote", {
            series: source?.seriesId ?? "SF43718",
            loadedAt: formatDateTime(locale, r.loadedAt, profile.data.timeZone),
          })}
        </p>
      </div>
    </AppShell>
  );
}
