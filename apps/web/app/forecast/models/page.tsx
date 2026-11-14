import Link from "next/link";
import { AlertBanner, Badge, Breadcrumb, PageHeader } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { HorizonLinks } from "@/components/forecast/HorizonLinks";
import { apiGet, type Forecasts } from "@/lib/api";
import { formatNumber, formatRate, isMessageKey, type MessageKey } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";

const SOURCE = "banxico_fix";

/** Model leaderboard (P4-04): every published model's walk-forward track record at one horizon, vs the naive benchmark. */
export default async function ModelsPage({ searchParams }: { searchParams: Promise<{ h?: string }> }) {
  const { tenant, profile, locale, t } = await getSession();
  const crumbs = (
    <Breadcrumb
      label={t("breadcrumb.label")}
      linkAs={Link}
      items={[{ label: t("nav.forecast"), href: "/forecast" }, { label: t("models.crumb") }]}
    />
  );

  const forecasts = tenant && profile?.ok ? await apiGet<Forecasts>(`/api/tenant/forecasts/${SOURCE}`) : null;
  if (!forecasts?.ok) {
    return (
      <AppShell active="forecast">
        <div className="stack-sm">
          <PageHeader breadcrumb={crumbs} title={t("models.crumb")} />
          {!tenant
            ? <AlertBanner tone="info" title={t("session.signInTitle")}>{t("session.signInBody")}</AlertBanner>
            : <p className="subtitle">{t("fx.noForecast")}</p>}
        </div>
      </AppShell>
    );
  }

  const { models, primary } = forecasts.data;
  const horizons = [...new Set(models.flatMap((m) => m.backtest.map((b) => b.horizonDays)))].sort((x, y) => x - y);
  const requested = Number((await searchParams).h);
  const h = horizons.includes(requested) ? requested : horizons.includes(30) ? 30 : horizons[0]!;
  const rows = models
    .map((m) => ({ m, s: m.backtest.find((b) => b.horizonDays === h) }))
    .filter((x): x is { m: (typeof models)[number]; s: NonNullable<typeof x.s> } => !!x.s);
  const worst = Math.max(...rows.map((x) => x.s.mae));
  const anyEdge = rows.some((x) => !x.m.isBenchmark && x.s.maeVsNaive < 1);
  const word = t(`fx.horizonWord.${h}` as MessageKey);
  const name = (code: string, fallback: string) => {
    const key = `fx.model.${code}.name`;
    return isMessageKey(key) ? t(key) : fallback;
  };
  const pct = (v: number) => `${new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(v * 100)}%`;
  const vsNaive = (v: number) => {
    const d = Math.round((v - 1) * 100);
    return `${d > 0 ? "+" : d < 0 ? "−" : "±"}${Math.abs(d)}%`;
  };
  const first = rows[0]?.s;

  return (
    <AppShell active="forecast">
      <div className="stack-sm">
        <PageHeader
          breadcrumb={crumbs}
          eyebrow={first ? t("models.eyebrow", { from: new Intl.DateTimeFormat(locale, { month: "short", year: "numeric", timeZone: "UTC" }).format(new Date(`${first.originsFrom}T00:00:00Z`)) }) : undefined}
          title={t("models.title", { horizon: word })}
          actions={<HorizonLinks label={t("fx.horizonLabel")} value={String(h)} options={horizons.map((d) => ({ value: String(d), label: `${d}D` }))} />}
        />

        <section className="card leaderboard">
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th scope="col">{t("models.model")}</th>
                  <th scope="col">{t("models.mae", { quote: "MXN" })}</th>
                  <th scope="col" className="num">{t("models.mape")}</th>
                  <th scope="col" className="num">{t("models.held80")}</th>
                  <th scope="col" className="num">{t("models.held95")}</th>
                  <th scope="col" className="num">{t("models.vsNaive")}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map(({ m, s }) => (
                  <tr key={m.code} className={m.code === primary ? "is-primary" : m.isBenchmark ? "is-benchmark" : undefined}>
                    <td>
                      <span className="leaderboard__name">
                        {name(m.code, m.name)} <span className="muted">v{m.version}</span>
                        {m.code === primary && <Badge tone="violet">{t("models.inUse")}</Badge>}
                      </span>
                    </td>
                    <td>
                      <span className="leaderboard__mae">
                        <span className="leaderboard__bar" aria-hidden="true">
                          <span style={{ width: `${(s.mae / worst) * 100}%` }} className={m.isBenchmark ? "is-benchmark" : undefined} />
                        </span>
                        {formatRate(locale, s.mae)}
                      </span>
                    </td>
                    <td className="num">{formatRate(locale, s.mape)}%</td>
                    <td className="num">{pct(s.coverage80)}</td>
                    <td className="num">{pct(s.coverage95)}</td>
                    <td className={`num ${m.isBenchmark ? "muted" : s.maeVsNaive < 1 ? "fx-better" : s.maeVsNaive > 1.005 ? "fx-worse" : "muted"}`}>
                      {m.isBenchmark ? t("models.benchmark") : vsNaive(s.maeVsNaive)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
        <p className="caption">
          {first && `${t("models.n", { n: formatNumber(locale, first.n) })}. `}
          {anyEdge ? t("models.noteEdge") : t("models.note")}
        </p>
      </div>
    </AppShell>
  );
}
