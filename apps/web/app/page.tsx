import Link from "next/link";
import { AlertBanner, PageHeader, StatCard } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { FiltersDrawer } from "@/components/dashboard/FiltersDrawer";
import { RateGlance } from "@/components/dashboard/RateGlance";
import { apiGet, type Dashboard, type Forecasts, type LatestRate, type RateAnalytics, type RateHistory } from "@/lib/api";
import { formatMoney, formatRate, formatSigned, type MessageKey } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";

const SOURCE = "banxico_fix";
const DAY_MS = 86_400_000;
const toDay = (iso: string) => Math.round(Date.parse(`${iso}T00:00:00Z`) / DAY_MS);

type Params = { period?: string; cur?: string | string[]; customer?: string; entity?: string };

/**
 * Executive dashboard (P8-01 "Glance" + P8-02 "Focus with filters"): the same real FIX and model forecast as
 * Exchange Forecast, next to the tenant's treasury figures (dummy data in Phases 0–2).
 */
export default async function Dashboard({ searchParams }: { searchParams: Promise<Params> }) {
  const params = await searchParams;
  const { switcher, tenant, tenants, profile, viewer, locale, t } = await getSession();

  if (!tenant || !profile) {
    return (
      <AppShell active="dashboard">
        {switcher && tenants?.ok && tenants.data.length === 0 ? (
          <p className="subtitle">{t("dev.noTenants")}</p>
        ) : (
          <AlertBanner tone="info" title={t("dev.pickTenantTitle")}>{t("dev.pickTenantBody")}</AlertBanner>
        )}
      </AppShell>
    );
  }
  if (!profile.ok) {
    return (
      <AppShell active="dashboard">
        <p className="error" role="alert">{profile.message}</p>
      </AppShell>
    );
  }

  // ── Filters from the URL ─────────────────────────────────
  const period = params.period === "quarter" || params.period === "12m" ? params.period : "month";
  const curList = (Array.isArray(params.cur) ? params.cur : params.cur ? [params.cur] : []).filter((c) => /^[A-Z]{3}$/.test(c));
  const customer = params.customer?.trim() || null;
  const entity = /^[0-9a-f-]{36}$/i.test(params.entity ?? "") ? params.entity! : null;
  const query = new URLSearchParams({ period });
  if (curList.length) query.set("currencies", curList.join(","));
  if (customer) query.set("customer", customer);
  if (entity) query.set("entity", entity);

  const [dash, latest, forecasts, analytics] = await Promise.all([
    apiGet<Dashboard>(`/api/tenant/dashboard?${query}`, tenant),
    apiGet<LatestRate>(`/api/tenant/rates/${SOURCE}/latest`, tenant),
    apiGet<Forecasts>(`/api/tenant/forecasts/${SOURCE}`, tenant),
    apiGet<RateAnalytics>(`/api/tenant/rates/${SOURCE}/analytics`, tenant),
  ]);
  if (!dash.ok || !latest.ok) {
    const failed = !dash.ok ? dash : !latest.ok ? latest : null;
    return (
      <AppShell active="dashboard">
        {failed?.status === 404
          ? <AlertBanner tone="brand" title={t("fx.emptyTitle")}>{t("fx.emptyBody")}</AlertBanner>
          : <p className="error" role="alert">{failed?.message}</p>}
      </AppShell>
    );
  }

  const d = dash.data;
  const r = latest.data;
  const tz = profile.data.timeZone;
  const rate = (v: number) => formatRate(locale, v);
  const money = (v: number, c: string) => formatMoney(locale, v, c);
  const pct = (v: number) => `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(v)}%`;
  const fmtDate = (iso: string, opts: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat(locale, { ...opts, timeZone: "UTC" }).format(new Date(`${iso}T00:00:00Z`));
  const periodLabel = t(`dash.period.${period}` as MessageKey);

  // ── Header: greeting, or what the view is filtered to ────
  const now = new Date();
  const hour = Number(new Intl.DateTimeFormat("en-US", { hour: "numeric", hourCycle: "h23", timeZone: tz }).format(now));
  const firstName = (viewer?.displayName ?? t("user.devViewer")).split(" ")[0]!;
  const entityName = entity ? d.options.entities.find((e) => e.id === entity)?.name : null;
  const appliedCount = [period !== "month", curList.length > 0 && curList.length < d.options.currencies.length, !!customer, !!entity].filter(Boolean).length;
  const filtered = appliedCount > 0;
  const focus = customer ?? entityName ?? t("dash.allEntities");
  const eyebrow = filtered
    ? t("dash.filtered", { what: [customer, entityName, periodLabel].filter(Boolean).join(" · ") })
    : new Intl.DateTimeFormat(locale, { weekday: "long", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", timeZone: tz }).format(now);
  const title = filtered
    ? `${focus} · ${periodLabel}`
    : t(hour < 12 ? "dash.greeting.morning" : hour < 19 ? "dash.greeting.afternoon" : "dash.greeting.evening", { name: firstName });

  // ── Rate card: same FIX and forward model as Exchange Forecast ──
  const primary = forecasts.ok ? forecasts.data.models.find((m) => m.code === forecasts.data.primary) : undefined;
  const p30 = primary?.points.find((p) => p.horizonDays === 30);
  const history = await apiGet<RateHistory>(
    `/api/tenant/rates/${SOURCE}?from=${new Date(Date.parse(`${r.date}T00:00:00Z`) - 30 * DAY_MS).toISOString().slice(0, 10)}&to=${r.date}`,
    tenant,
  );
  // ── Forecast summary: trend now, how far the forecast can be trusted, and the rate to budget with ──
  const a = analytics.ok ? analytics.data : null;
  const bt30 = primary?.backtest.find((b) => b.horizonDays === 30);
  let signal: { trend: RateAnalytics["trend"]; title: string; confidence: "high" | "medium" | "low"; lines: string[] } | null = null;
  if (p30 && a) {
    // Confidence in the 30-day range (0–3 points): is the range historically reliable, is the market calm, is it narrow?
    const calibrated = !!bt30 && bt30.coverage80 >= 0.75 && bt30.coverage80 <= 0.9;
    const level = a.volatilityDetail?.level ?? "normal";
    const widthPct = ((p30.upper80 - p30.lower80) / p30.point) * 100;
    const score = (calibrated ? 1 : 0) + (level === "low" ? 1 : level === "normal" ? 0.5 : 0) + (widthPct < 5 ? 1 : widthPct < 9 ? 0.5 : 0);
    const confidence = score >= 2.5 ? "high" : score >= 1.5 ? "medium" : "low";

    const n = Math.abs(a.streakWeeks);
    const trendText =
      a.trend === "unknown" ? t("dash.sig.trend.unknown")
      : n >= 2 && Math.sign(a.streakWeeks) === (a.trend === "up" ? 1 : -1) ? t(`dash.sig.trend.${a.trend}N` as MessageKey, { n })
      : t(`dash.sig.trend.${a.trend}` as MessageKey);
    const vsSma = a.sma50 ? ((a.rate - a.sma50) / a.sma50) * 100 : null;
    const vol30 = a.volatility.days30;

    signal = {
      trend: a.trend,
      title: trendText,
      confidence,
      lines: [
        [
          vsSma !== null ? t(vsSma >= 0 ? "dash.sig.aboveSma" : "dash.sig.belowSma", { pct: formatSigned(locale, Math.abs(vsSma), 1, "%").replace(/^[+-]/, "") }) : null,
          vol30 !== null ? t(`dash.sig.vol.${level}` as MessageKey, { vol: `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(vol30)}%` }) : null,
        ].filter(Boolean).join(" · "),
        t("dash.sig.range", {
          lo: rate(p30.lower80),
          hi: rate(p30.upper80),
          held: bt30 ? `${Math.round(bt30.coverage80 * 100)}%` : "—",
        }),
        // A USD-long company converts dollars into pesos: the cautious budget rate is the low end of the range.
        d.exposure.usdLong > 0
          ? t("dash.sig.budgetSell", { rate: rate(p30.lower80) })
          : t("dash.sig.budgetBuy", { rate: rate(p30.upper80) }),
      ],
    };
  }

  // ── KPIs ─────────────────────────────────────────────────
  const fixRate = rate(d.fix.rate);
  const prevMonth = fmtDate(d.cash.prevMonthEnd, { month: "short" });
  const rec = d.recommendation;

  // ── Peso projection bars ─────────────────────────────────
  const proj = d.pesoProjection;
  const maxBal = proj ? Math.max(...proj.steps.map((s) => s.balance), proj.reserveMxn) * 1.08 : 1;
  const millions = (v: number) => `${new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v / 1_000_000)}M`;

  return (
    <AppShell active="dashboard">
      <div className="stack-sm">
        <PageHeader
          eyebrow={eyebrow}
          title={title}
          actions={
            <>
              <FiltersDrawer
                buttonLabel={filtered ? t("dash.filtersN", { n: appliedCount }) : t("dash.filters")}
                title={t("dash.drawer.title")}
                closeLabel={t("dash.drawer.close")}
                values={{ period, currencies: curList.length ? curList : d.options.currencies, customer, entity }}
                options={d.options}
                text={{
                  dateRange: t("dash.drawer.dateRange"),
                  periods: (["month", "quarter", "12m"] as const).map((v) => ({ value: v, label: t(`dash.period.${v}` as MessageKey) })),
                  currency: t("dash.drawer.currency"),
                  customer: t("dash.drawer.customer"),
                  allCustomers: t("dash.allCustomers"),
                  entity: t("dash.drawer.entity"),
                  allEntities: t("dash.allEntities"),
                  cancel: t("dash.drawer.cancel"),
                  apply: t("dash.drawer.apply"),
                  clear: t("dash.drawer.clear"),
                }}
              />
              <button type="button" className="dash-ai" disabled title={t("dash.askAiSoon")}>
                <span aria-hidden="true" />{t("dash.askAi")}
              </button>
            </>
          }
        />

        <section className="dash-top">
          <Link href="/forecast" className="dash-rate" aria-label={t("dash.rate.aria", { rate: rate(r.rate) })}>
            <span className="dash-rate__blob dash-rate__blob--a" aria-hidden="true" />
            <span className="dash-rate__blob dash-rate__blob--b" aria-hidden="true" />
            <span className="dash-rate__body">
              <span className="dash-rate__head">
                <span>{t("dash.rate.kicker")}</span>
                <span className="dash-rate__badge">{t("dash.rate.badge")}</span>
              </span>
              <span className="dash-rate__value">
                <strong>{rate(r.rate)}</strong>
                {r.changePct !== null && <span>{formatSigned(locale, r.changePct, 2, "%")}</span>}
              </span>
              {history.ok && (
                <RateGlance
                  history={history.data.points.map((p) => ({ day: toDay(p.date), rate: p.rate }))}
                  fan={(primary?.points ?? []).filter((p) => p.horizonDays <= 30).map((p) => ({ day: toDay(p.targetDate), point: p.point, lower80: p.lower80, upper80: p.upper80 }))}
                  leftLabel={t("dash.rate.left")}
                  rightLabel={p30 ? t("dash.rate.right", { date: fmtDate(p30.targetDate, { day: "numeric", month: "short" }), rate: rate(p30.point) }) : ""}
                />
              )}
              {signal && (
                <span className="dash-rate__signal">
                  <span className="dash-rate__clock" aria-hidden="true">
                    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                      <path d={signal.trend === "up" ? "M3 17l6-6 4 4 8-8M15 7h6v6" : signal.trend === "down" ? "M3 7l6 6 4-4 8 8M15 17h6v-6" : "M3 12h18M15 6l6 6-6 6"} />
                    </svg>
                  </span>
                  <span className="dash-rate__signal-text">
                    <span className="dash-rate__signal-title">
                      <strong>{signal.title}</strong>
                      <span className={`dash-conf dash-conf--${signal.confidence}`}>{t(`dash.sig.conf.${signal.confidence}` as MessageKey)}</span>
                    </span>
                    {signal.lines.map((l) => <span key={l}>{l}</span>)}
                  </span>
                </span>
              )}
            </span>
          </Link>

          <div className="dash-kpis">
            <StatCard
              label={t("dash.cash.label")}
              value={money(d.cash.totalUsd, "USD")}
              note={d.cash.changePct !== null
                ? t("dash.cash.note", { change: formatSigned(locale, d.cash.changePct, 1, "%"), month: prevMonth, rate: fixRate })
                : t("dash.cash.noteNoChange", { rate: fixRate, date: fmtDate(d.fix.date, { dateStyle: "medium" }) })}
              tone={d.cash.changePct === null ? "neutral" : d.cash.changePct >= 0 ? "positive" : "negative"}
            />
            <StatCard
              label={t("dash.exposure.label")}
              value={money(d.exposure.usdLong, "USD")}
              note={d.exposure.usdLong > 0 ? t("dash.exposure.note") : t("dash.exposure.none")}
              tone={d.exposure.usdLong > 0 ? "negative" : "neutral"}
            />
            {customer ? (
              <StatCard
                label={t("dash.flows.customerLabel", { customer, period: periodLabel })}
                value={money(d.flows.revenueUsd, "USD")}
                note={t("dash.flows.customerNote", { share: pct(d.flows.customerSharePct ?? 0), rate: rate(d.flows.fix) })}
              />
            ) : (
              <StatCard
                label={t("dash.flows.label", { period: periodLabel })}
                value={money(d.flows.revenueUsd, "USD")}
                note={t("dash.flows.note", {
                  expenses: money(d.flows.expensesUsd ?? 0, "USD"),
                  margin: d.flows.marginPct !== null ? pct(d.flows.marginPct) : "—",
                  rate: rate(d.flows.fix),
                })}
              />
            )}
            <StatCard label={t("dash.ar.label")} value="—" note={t("dash.ar.note")} />
          </div>
        </section>

        <section className="dash-bottom">
          <article className="card dash-peso" aria-label={t("dash.peso.title")}>
            <div className="dash-peso__head">
              <strong>{t("dash.peso.title")}</strong>
              <ul className="dash-peso__legend">
                <li><i className="is-actual" />{t("dash.peso.actual")}</li>
                <li><i className="is-estimated" />{t("dash.peso.estimated")}</li>
                <li><i className="is-below" />{t("dash.peso.below")}</li>
              </ul>
            </div>
            {proj ? (
              <>
                <div
                  className="dash-peso__plot"
                  role="img"
                  aria-label={t("dash.peso.aria", { values: proj.steps.map((s) => `${fmtDate(s.date, { day: "numeric", month: "short" })} ${money(s.balance, "MXN")}`).join(", ") })}
                >
                  <span className="dash-peso__reserve" style={{ bottom: `${(proj.reserveMxn / maxBal) * 100}%` }}>
                    <span>{t("dash.peso.reserve", { amount: money(proj.reserveMxn, "MXN") })}</span>
                  </span>
                  {proj.steps.map((s) => (
                    <div key={s.date} className="dash-peso__col">
                      <span className={`dash-peso__val${s.belowReserve ? " is-below" : s.kind === "estimated" ? " is-estimated" : ""}`}>{millions(s.balance)}</span>
                      <div
                        className={`dash-peso__bar${s.belowReserve ? " is-below" : s.kind === "estimated" ? " is-estimated" : ""}`}
                        style={{ height: `${Math.max(2, (Math.max(s.balance, 0) / maxBal) * 100)}%` }}
                      />
                    </div>
                  ))}
                </div>
                <div className="dash-peso__dates" aria-hidden="true">
                  {proj.steps.map((s) => (
                    <span key={s.date} className={s.belowReserve ? "is-below" : undefined}>{fmtDate(s.date, { day: "numeric", month: "short" })}</span>
                  ))}
                </div>
                <p className="caption">{t("dash.peso.note")}</p>
              </>
            ) : (
              <p className="subtitle">{t("dash.peso.none")}</p>
            )}
          </article>

          <Link href="/forecast" className="dash-rec">
            {rec.kind === "liquidity" ? (
              <>
                <span className="dash-rec__kicker">{t("dash.rec.liquidityKicker")}</span>
                <strong>{t("dash.rec.liquidityTitle", { usd: money(rec.usdToConvert, "USD"), date: fmtDate(rec.date, { day: "numeric", month: "short" }) })}</strong>
                <span>{t("dash.rec.liquidityBody", { shortfall: money(rec.shortfallMxn, "MXN"), source: rec.rateSource, rate: rate(rec.rate) })}</span>
              </>
            ) : rec.kind === "exposure" ? (
              <>
                <span className="dash-rec__kicker">{t("dash.rec.exposureKicker")}</span>
                <strong>{t("dash.rec.exposureTitle", { usd: money(rec.exposureUsd, "USD") })}</strong>
                <span>
                  {t("dash.rec.exposureBody", {
                    mxn: money(rec.mxnAtFix, "MXN"),
                    days: rec.horizon ?? 90,
                    lo: rec.mxnLow80 !== null ? money(rec.mxnLow80, "MXN") : "—",
                    hi: rec.mxnHigh80 !== null ? money(rec.mxnHigh80, "MXN") : "—",
                  })}
                </span>
              </>
            ) : (
              <>
                <span className="dash-rec__kicker">{t("dash.rec.noneKicker")}</span>
                <strong>{t("dash.rec.noneTitle")}</strong>
              </>
            )}
            <span className="dash-rec__link">{t("dash.rec.link")}</span>
          </Link>
        </section>
      </div>
    </AppShell>
  );
}
