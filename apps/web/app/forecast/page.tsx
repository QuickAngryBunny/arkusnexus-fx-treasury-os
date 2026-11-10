import { AlertBanner, PageHeader, StatCard } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { apiGet, type Forecasts, type LatestRate, type RateAnalytics, type RateHistory, type RateSource } from "@/lib/api";
import { CURRENCY_DIGITS, formatDate, formatDateTime, formatNumber, formatRate, formatSigned, isMessageKey, type MessageKey } from "@/lib/i18n";
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
  const [history, analytics, forecasts] = await Promise.all([
    apiGet<RateHistory>(`/api/tenant/rates/${SOURCE}?from=${from}&to=${r.date}`, tenant),
    apiGet<RateAnalytics>(`/api/tenant/rates/${SOURCE}/analytics`, tenant),
    apiGet<Forecasts>(`/api/tenant/forecasts/${SOURCE}`, tenant),
  ]);
  const pair = { base: r.baseCurrency, quote: r.quoteCurrency };
  const a = analytics.ok ? analytics.data : null;
  const primary = forecasts.ok ? forecasts.data.models.find((m) => m.code === forecasts.data.primary) : undefined;
  const naive = forecasts.ok ? forecasts.data.models.find((m) => m.isBenchmark) : undefined;
  const scored = primary?.backtest ?? [];
  const noEdge = scored.length > 0 && scored.every((s) => s.maeVsNaive >= 0.995);
  // Model names and descriptions come from the catalogs when known; the registry text is the fallback.
  const modelText = (code: string, field: "name" | "description", fallback: string) => {
    const key = `fx.model.${code}.${field}`;
    return isMessageKey(key) ? t(key) : fallback;
  };
  // The point column is shown only when it differs from today's rate (e.g. a forward); otherwise it is said once.
  const pointVaries = primary ? primary.points.some((p) => Math.abs(p.point - primary.lastRate) >= 0.00005) : false;
  const pointLabel = primary && isMessageKey(`fx.point.${primary.code}`) ? t(`fx.point.${primary.code}` as MessageKey) : t("fx.point");
  const pct = (v: number) => `${new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(v * 100)}%`;

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
                    change: formatSigned(locale, r.change, CURRENCY_DIGITS),
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

        {a && (
          <div className="grid">
            <StatCard
              label={t("fx.trend")}
              value={t(`fx.trend.${a.trend}` as MessageKey)}
              note={a.sma50 !== null && a.sma200 !== null
                ? t("fx.trendNote", { sma50: formatRate(locale, a.sma50), sma200: formatRate(locale, a.sma200) })
                : undefined}
            />
            <StatCard
              label={t("fx.volatility")}
              value={a.volatility.days30 !== null ? `${formatRate(locale, a.volatility.days30, 1)}%` : "—"}
              note={a.volatility.days90 !== null ? t("fx.volatilityNote", { vol90: `${formatRate(locale, a.volatility.days90, 1)}%` }) : undefined}
            />
            <StatCard
              label={t("fx.range52w")}
              value={`${formatRate(locale, a.low52w.rate)} – ${formatRate(locale, a.high52w.rate)} ${r.quoteCurrency}`}
              note={t("fx.range52wNote", { lowDate: formatDate(locale, a.low52w.date), highDate: formatDate(locale, a.high52w.date) })}
            />
          </div>
        )}

        {a && a.changes.length > 0 && (
          <p className="caption">
            {a.changes.map((c) => t("fx.changes", { days: c.days, change: formatSigned(locale, c.change, CURRENCY_DIGITS), pct: formatSigned(locale, c.changePct, 2, "%") })).join(" · ")}
          </p>
        )}

        <section className="card">
          {primary ? (
            <>
              <h2>{t("fx.forecastTitle", { model: modelText(primary.code, "name", primary.name) })}</h2>
              <p className="caption card-lead">
                {t("fx.forecastLead", { date: formatDate(locale, primary.asOf), rate: formatRate(locale, primary.lastRate), quote: r.quoteCurrency })}
                {!pointVaries && ` ${t("fx.pointFlat")}`}
              </p>
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th scope="col">{t("fx.horizon")}</th>
                      <th scope="col">{t("fx.targetDate")}</th>
                      {pointVaries && <th scope="col" className="num">{pointLabel}</th>}
                      {pointVaries && <th scope="col" className="num">{t("fx.forwardPoints")}</th>}
                      <th scope="col" className="num">{t("fx.range80")}</th>
                      <th scope="col" className="num">{t("fx.range95")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {primary.points.map((p) => (
                      <tr key={p.horizonDays}>
                        <td>{t("fx.horizonDays", { days: p.horizonDays })}</td>
                        <td>{formatDate(locale, p.targetDate)}</td>
                        {pointVaries && <td className="num">{formatRate(locale, p.point)}</td>}
                        {pointVaries && <td className="num muted">{formatSigned(locale, p.point - primary.lastRate, CURRENCY_DIGITS)}</td>}
                        <td className="num">{formatRate(locale, p.lower80)} – {formatRate(locale, p.upper80)}</td>
                        <td className="num muted">{formatRate(locale, p.lower95)} – {formatRate(locale, p.upper95)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {primary.code === "forward" && <p className="caption">{t("fx.forwardNote")}</p>}
              <p className="caption">{t("fx.modelNote", { code: primary.code, version: primary.version, description: modelText(primary.code, "description", primary.description) })}</p>
            </>
          ) : (
            <p className="subtitle">{t("fx.noForecast")}</p>
          )}
        </section>

        {primary && naive && scored.length > 0 && (
          <section className="card">
            <h2>{t("fx.backtestTitle", { model: modelText(primary.code, "name", primary.name) })}</h2>
            <p className="caption card-lead">
              {t("fx.backtestLead", { n: formatNumber(locale, Math.min(...scored.map((s) => s.n))), from: formatDate(locale, scored[0].originsFrom) })}
            </p>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th scope="col">{t("fx.horizon")}</th>
                    <th scope="col" className="num">{t("fx.mae", pair)}</th>
                    <th scope="col" className="num">{t("fx.mape")}</th>
                    <th scope="col" className="num">{t("fx.coverage80")}</th>
                    <th scope="col" className="num">{t("fx.coverage95")}</th>
                    <th scope="col" className="num">{t("fx.vsNaive")}</th>
                  </tr>
                </thead>
                <tbody>
                  {scored.map((s) => {
                    const bench = naive.backtest.find((b) => b.horizonDays === s.horizonDays);
                    return (
                      <tr key={s.horizonDays}>
                        <td>{t("fx.horizonDays", { days: s.horizonDays })}</td>
                        <td className="num">{formatRate(locale, s.mae)}</td>
                        <td className="num">{formatRate(locale, s.mape, 2)}%</td>
                        <td className="num">{pct(s.coverage80)}{bench && <span className="muted"> ({pct(bench.coverage80)})</span>}</td>
                        <td className="num">{pct(s.coverage95)}{bench && <span className="muted"> ({pct(bench.coverage95)})</span>}</td>
                        <td className="num">{formatRate(locale, s.maeVsNaive, 2)}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <p className="caption">
              {t("fx.backtestBenchmark")} {noEdge && t("fx.backtestNoEdge")}
            </p>
          </section>
        )}

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
