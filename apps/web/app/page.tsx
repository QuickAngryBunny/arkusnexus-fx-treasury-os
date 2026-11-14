import Link from "next/link";
import { AlertBanner, PageHeader } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { RateGlance } from "@/components/dashboard/RateGlance";
import { apiGet, type Forecasts, type LatestRate, type RateAnalytics, type RateHistory } from "@/lib/api";
import { formatRate, formatSigned, type MessageKey } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";

const SOURCE = "banxico_fix";
const DAY_MS = 86_400_000;
const toDay = (iso: string) => Math.round(Date.parse(`${iso}T00:00:00Z`) / DAY_MS);

/**
 * Dashboard: today's rate card with the same real FIX and model forecast as Exchange Forecast.
 * Phase 3 (O-05) adds the welcome, news, first conversion plan and team cards; Phase 4 adds ongoing conversions.
 */
export default async function Dashboard() {
  const { tenant, profile, viewer, locale, t } = await getSession();

  if (!tenant || !profile) {
    return (
      <AppShell active="dashboard">
        <AlertBanner tone="info" title={t("session.signInTitle")}>{t("session.signInBody")}</AlertBanner>
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

  const [latest, forecasts, analytics] = await Promise.all([
    apiGet<LatestRate>(`/api/tenant/rates/${SOURCE}/latest`),
    apiGet<Forecasts>(`/api/tenant/forecasts/${SOURCE}`),
    apiGet<RateAnalytics>(`/api/tenant/rates/${SOURCE}/analytics`),
  ]);
  if (!latest.ok) {
    return (
      <AppShell active="dashboard">
        {latest.status === 404
          ? <AlertBanner tone="brand" title={t("fx.emptyTitle")}>{t("fx.emptyBody")}</AlertBanner>
          : <p className="error" role="alert">{latest.message}</p>}
      </AppShell>
    );
  }

  const r = latest.data;
  const tz = profile.data.timeZone;
  const rate = (v: number) => formatRate(locale, v);
  const fmtDate = (iso: string, opts: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat(locale, { ...opts, timeZone: "UTC" }).format(new Date(`${iso}T00:00:00Z`));

  // ── Header: greeting in the tenant's time zone ──────────
  const now = new Date();
  const hour = Number(new Intl.DateTimeFormat("en-US", { hour: "numeric", hourCycle: "h23", timeZone: tz }).format(now));
  const firstName = (viewer?.displayName ?? "").split(" ")[0]!;
  const eyebrow = new Intl.DateTimeFormat(locale, { weekday: "long", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", timeZone: tz }).format(now);
  const title = t(hour < 12 ? "dash.greeting.morning" : hour < 19 ? "dash.greeting.afternoon" : "dash.greeting.evening", { name: firstName });

  // ── Rate card: same FIX and forward model as Exchange Forecast ──
  const primary = forecasts.ok ? forecasts.data.models.find((m) => m.code === forecasts.data.primary) : undefined;
  const p30 = primary?.points.find((p) => p.horizonDays === 30);
  const history = await apiGet<RateHistory>(
    `/api/tenant/rates/${SOURCE}?from=${new Date(Date.parse(`${r.date}T00:00:00Z`) - 30 * DAY_MS).toISOString().slice(0, 10)}&to=${r.date}`,
  );
  // ── Forecast summary: trend now and how far the 30-day range can be trusted ──
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
      ],
    };
  }

  return (
    <AppShell active="dashboard">
      <div className="stack-sm">
        <PageHeader
          eyebrow={eyebrow}
          title={title}
          actions={
            <button type="button" className="dash-ai" disabled title={t("dash.askAiSoon")}>
              <span aria-hidden="true" />{t("dash.askAi")}
            </button>
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
        </section>
      </div>
    </AppShell>
  );
}
