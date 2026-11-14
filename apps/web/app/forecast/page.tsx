import Link from "next/link";
import { AlertBanner, Button, HeroCard, PageHeader, StatCard } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { ForecastCard, HorizonProvider, NewsCard, RateChart, type ChartData, type HorizonData, type NewsEvent } from "@/components/forecast/HorizonView";
import { apiGet, type Forecasts, type LatestRate, type NewsFeed, type RateAnalytics, type RateHistory, type RateSource } from "@/lib/api";
import { CURRENCY_DIGITS, formatDateTime, formatRate, formatSigned, type Locale, type MessageKey, type Translate } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";

const SOURCE = "banxico_fix";
const DAY_MS = 86_400_000;
const toDay = (iso: string) => Math.round(Date.parse(`${iso}T00:00:00Z`) / DAY_MS);

/** Exchange Forecast (R-01): today, trend, volatility and forecast up front; 12 months → forecast chart; drivers. */
export default async function ForecastPage() {
  const { tenant, profile, locale, t } = await getSession();
  const fmtDate = (iso: string, opts: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat(locale, { ...opts, timeZone: "UTC" }).format(new Date(`${iso}T00:00:00Z`));

  const shell = (body: React.ReactNode, eyebrow = t("fx.eyebrowNoSync")) => (
    <AppShell active="forecast">
      <div className="stack-sm">
        <PageHeader title={t("nav.forecast")} eyebrow={eyebrow} actions={<HeaderActions t={t} />} />
        {body}
      </div>
    </AppShell>
  );

  if (!tenant || !profile?.ok) {
    return shell(<AlertBanner tone="info" title={t("session.signInTitle")}>{t("session.signInBody")}</AlertBanner>);
  }

  const [latest, sources, analytics, forecasts] = await Promise.all([
    apiGet<LatestRate>(`/api/tenant/rates/${SOURCE}/latest`),
    apiGet<RateSource[]>("/api/tenant/rates/sources"),
    apiGet<RateAnalytics>(`/api/tenant/rates/${SOURCE}/analytics`),
    apiGet<Forecasts>(`/api/tenant/forecasts/${SOURCE}`),
  ]);
  if (!latest.ok) {
    return shell(
      latest.status === 404
        ? <AlertBanner tone="brand" title={t("fx.emptyTitle")}>{t("fx.emptyBody")}</AlertBanner>
        : <p className="error" role="alert">{latest.message}</p>,
    );
  }

  const r = latest.data;
  const from = new Date(Date.parse(`${r.date}T00:00:00Z`) - 365 * DAY_MS).toISOString().slice(0, 10);
  const [history, news] = await Promise.all([
    apiGet<RateHistory>(`/api/tenant/rates/${SOURCE}?from=${from}&to=${r.date}`),
    apiGet<NewsFeed>(`/api/tenant/news?from=${from}`),
  ]);
  const source = sources.ok ? sources.data.find((s) => s.code === SOURCE) : undefined;
  const a = analytics.ok ? analytics.data : null;
  const rate = (v: number) => formatRate(locale, v);
  const eyebrow = source?.lastLoad
    ? t("fx.eyebrow", { synced: formatDateTime(locale, source.lastLoad.at, profile.data.timeZone) })
    : t("fx.eyebrowNoSync");

  // ── Today row ─────────────────────────────────────────────
  const today = (
    <HeroCard
      label={t("fx.todayLabel")}
      kicker={t("fx.today", { date: fmtDate(r.date, { day: "numeric", month: "short" }) })}
      value={rate(r.rate)}
      note={r.previous && r.change !== null && r.changePct !== null
        ? t("fx.vsPrevious", {
            change: formatSigned(locale, r.change, CURRENCY_DIGITS),
            pct: formatSigned(locale, r.changePct, 2, "%"),
            day: fmtDate(r.previous.date, { weekday: "long" }),
          })
        : undefined}
    />
  );

  const yearChange = a?.changes.find((c) => c.days === 365);
  const trendCard = a && (
    <StatCard
      kicker
      label={t("fx.trendKicker")}
      icon={<TrendIcon trend={a.trend} />}
      value={t(`fx.trend.${a.trend}` as MessageKey)}
      valueTone={a.trend === "up" ? "positive" : a.trend === "down" ? "negative" : "default"}
      note={[
        streakText(t, a.streakWeeks),
        a.sma50 !== null && t(a.rate >= a.sma50 ? "fx.vsSma50Above" : "fx.vsSma50Below", { sma: rate(a.sma50) }),
        yearChange && t("fx.oneYear", { pct: formatSigned(locale, yearChange.changePct, 1, "%") }),
      ].filter(Boolean).join(" · ")}
    />
  );

  const vd = a?.volatilityDetail;
  const vol30 = a?.volatility.days30 ?? null;
  const volCard = vd && vol30 !== null && (
    <StatCard
      kicker
      label={t("fx.volKicker")}
      value={
        <>
          {formatRate(locale, vol30, 1)}% <span className="fx-level">{t(`fx.vol.${vd.level}` as MessageKey)}</span>
        </>
      }
      note={[
        t("fx.volTypical", { move: rate(vd.typicalDailyMove) }),
        vd.peak12m && (vd.peak12m.vol > vol30 + 0.05
          ? t("fx.volCalmer", { month: monthName(locale, vd.peak12m.month), vol: `${formatRate(locale, vd.peak12m.vol, 1)}%` })
          : t("fx.volPeak")),
      ].filter(Boolean).join(" ")}
    >
      <span className="fx-levels" aria-hidden="true">
        {(["low", "normal", "high"] as const).map((l) => (
          <span key={l} className={`fx-levels__seg fx-levels__seg--${l}${l === vd.level ? " is-on" : ""}`} />
        ))}
      </span>
    </StatCard>
  );

  // ── Forecast horizons (headline model) ────────────────────
  const primary = forecasts.ok ? forecasts.data.models.find((m) => m.code === forecasts.data.primary) : undefined;
  const horizons: HorizonData[] = (primary?.points ?? []).map((p) => {
    const word = t(`fx.horizonWord.${p.horizonDays}` as MessageKey);
    const target = fmtDate(p.targetDate, { day: "numeric", month: "short" });
    return {
      key: `${p.horizonDays}D`,
      pillLabel: `${p.horizonDays}D`,
      days: p.horizonDays,
      kicker: t("fx.forecastKicker", { date: target }),
      base: rate(p.point),
      note: t("fx.forecastNote", { lo: rate(p.lower80), hi: rate(p.upper80) }),
      chartTitle: t("fx.chartTitle", { horizon: word }),
      chartAria: t("fx.chartAria", { horizon: word, base: rate(p.point), lo: rate(p.lower80), hi: rate(p.upper80) }),
      targetLabel: target,
      targetDay: toDay(p.targetDate),
      point: p.point,
      lower80: p.lower80,
      upper80: p.upper80,
      lower95: p.lower95,
      upper95: p.upper95,
    };
  });

  // ── Chart data, flagged moves and the news for each (stored by the worker; never a live GNews call) ──
  const points = history.ok ? history.data.points : [];
  const feed = news.ok ? news.data : null;
  const tz = profile.data.timeZone;
  const dayMonthYear = (iso: string) => fmtDate(iso, { day: "numeric", month: "short", year: "numeric" });
  const timeOf = (iso: string) =>
    new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", timeZone: tz }).format(new Date(iso));
  const times = (sigma: number) => formatRate(locale, Math.abs(sigma), 1);
  const toArticles = (key: string) =>
    // The widget shows the single most relevant headline.
    (feed?.windows.find((w) => w.key === key)?.articles ?? []).slice(0, 1).map((x) => ({
      title: x.title, source: x.sourceName, time: timeOf(x.publishedAt), url: x.url,
    }));
  const common = {
    newsLabel: t("news.label"),
    readLabel: t("news.read"),
    historyLabel: t("fx.whyHistory"),
  };

  const events: NewsEvent[] = [];
  const scored = feed?.latest;
  const todayWindow = feed?.windows.find((w) => w.kind === "today");
  if (scored) {
    const big = Math.abs(scored.sigma) >= 2;
    const tw = todayWindow;
    events.push({
      ...common,
      key: "today",
      kicker: t("news.todayKicker", { date: dayMonthYear(scored.date) }),
      rate: rate(scored.rate),
      change: t("news.vsPrevious", { pct: formatSigned(locale, scored.changePct, 2, "%"), day: fmtDate(scored.previousDate, { weekday: "long" }) }),
      changeTone: scored.changePct > 0 ? "up" : scored.changePct < 0 ? "down" : "flat",
      title: t(big ? "news.todayBig" : Math.abs(scored.sigma) >= 1 ? "news.todayBusy" : "news.todayNormal"),
      why: t("news.todayWhy", { n: times(scored.sigma), vol: `${formatRate(locale, scored.vol30Pct, 1)}%` }),
      badge: t(big ? "news.badgeBig" : "news.badgeNormal", { s: formatRate(locale, Math.abs(scored.sigma), 1) }),
      badgeTone: big ? "big" : "normal",
      articles: tw ? toArticles(tw.key) : [],
      note: tw
        ? t("news.todayNote", { from: timeOf(tw.from), to: timeOf(tw.to) })
        : t("news.todayNone"),
      historyHref: `/forecast/history?view=day&d=${scored.date}`,
    });
  }
  for (const m of [...(feed?.moves ?? [])].reverse()) {
    const key = `move:${m.date}`;
    const w = feed?.windows.find((x) => x.key === key);
    events.push({
      ...common,
      key,
      kicker: t("news.moveKicker", { date: dayMonthYear(m.date) }),
      rate: rate(m.rate),
      change: t("news.inADay", { pct: formatSigned(locale, m.changePct, 2, "%") }),
      changeTone: m.changePct > 0 ? "up" : "down",
      title: t(m.changePct > 0 ? "news.moveUp" : "news.moveDown", { s: formatRate(locale, Math.abs(m.sigma), 1) }),
      why: t(m.changePct > 0 ? "news.moveWhyUp" : "news.moveWhyDown", {
        pct: formatRate(locale, Math.abs(m.changePct), 2), n: times(m.sigma), vol: `${formatRate(locale, m.vol30Pct, 1)}%`,
      }),
      badge: t("news.badgeBig", { s: formatRate(locale, Math.abs(m.sigma), 1) }),
      badgeTone: "big",
      articles: w ? toArticles(key) : [],
      note: w ? (w.articles.length ? t("news.moveNote") : t("news.moveEmpty")) : t("news.moveNone"),
      historyHref: `/forecast/history?view=day&d=${m.date}`,
    });
  }

  const chart: ChartData = {
    locale,
    quote: r.quoteCurrency,
    history: points.map((p) => ({ day: toDay(p.date), rate: p.rate })),
    fan: (primary?.points ?? []).map((p) => ({
      horizonDays: p.horizonDays, day: toDay(p.targetDate), point: p.point,
      lower80: p.lower80, upper80: p.upper80, lower95: p.lower95, upper95: p.upper95,
    })),
    moves: (feed?.moves ?? []).map((m) => ({
      key: `move:${m.date}`, day: toDay(m.date), rate: m.rate,
      aria: t("news.markerAria", { date: dayMonthYear(m.date), pct: formatSigned(locale, m.changePct, 2, "%") }),
    })),
    labels: {
      today: t("fx.chartToday"),
      forecast: t("fx.chartForecast"),
      horizon: t("fx.horizonLabel"),
      fix: t("fx.legend.fix"),
      expected: t("fx.legend.expected"),
      range80: t("fx.legend.range80"),
      range95: t("fx.legend.range95"),
      min: t("fx.legend.min"),
      max: t("fx.legend.max"),
      high: t("fx.legend.high"),
      low: t("fx.legend.low"),
      bigMove: t("news.legend"),
      modelPoints: t("fx.legend.modelPoints"),
      pickHint: t("news.pickHint"),
      todayButton: t("news.todayButton", { date: fmtDate(r.date, { day: "numeric", month: "short" }) }),
    },
  };

  const failedLoad = source?.lastLoad?.status === "failed" ? source.lastLoad : null;
  const gap = a?.rateGap;
  const corr = gap?.correlation90d ?? null;
  const corrText = corr === null
    ? "—"
    : t(Math.abs(corr) < 0.2 ? "fx.driverWeak" : corr > 0 ? "fx.driverWith" : "fx.driverOpposite", { r: formatSigned(locale, corr, 2) });
  const corrClass = corr === null || Math.abs(corr) < 0.2 ? "muted" : corr > 0 ? "fx-with" : "fx-opposite";

  const driversCard = (
        <article className="card fx-drivers">
          <span className="fx-kicker">{t("fx.driversKicker")}</span>
          {gap ? (
            <>
              <div className="fx-driver">
                <span>{t("fx.driverGap", { gap: formatRate(locale, gap.gapPts) })}</span>
                <strong className={corrClass}>{corrText}</strong>
              </div>
              <div className="fx-driver muted"><span>{t("fx.driverDxy")}</span><span>{t("fx.driverSoon")}</span></div>
              <div className="fx-driver muted"><span>{t("fx.driverInflation")}</span><span>{t("fx.driverSoon")}</span></div>
              <p className="fx-drivers__note">
                {t("fx.driversNote", { mxn: formatRate(locale, gap.mxnRatePct), usd: formatRate(locale, gap.usdRatePct) })}
              </p>
            </>
          ) : (
            <p className="fx-drivers__note">{t("fx.driversNone")}</p>
          )}
        </article>
  );

  const todayRow = (withForecast: boolean) => (
    <section aria-label={t("fx.todayLabel")} className="fx-today">
      {today}
      {trendCard}
      {volCard}
      {withForecast && (
        <ForecastCard label={t("fx.forecastKicker", { date: "" })} link={<Link href="/forecast/models">{t("fx.forecastModels")}</Link>} />
      )}
    </section>
  );

  const body = (
    <>
      {failedLoad && <AlertBanner tone="danger" title={t("fx.lastLoadFailed", { error: failedLoad.error ?? "?" })} />}

      {horizons.length > 0 && points.length > 1 ? (
        <HorizonProvider horizons={horizons} initial={horizons.find((h) => h.key === "30D")?.key ?? horizons[0]!.key}>
          {todayRow(true)}
          <RateChart data={chart} />
          <section className="fx-bottom">
            <NewsCard events={events} />
            {driversCard}
          </section>
        </HorizonProvider>
      ) : (
        <>
          {todayRow(false)}
          <p className="subtitle">{t("fx.noForecast")}</p>
          <section className="fx-bottom">{driversCard}</section>
        </>
      )}

    </>
  );

  return shell(body, eyebrow);
}

function HeaderActions({ t }: { t: Translate }) {
  // Rate alerts (P2-04) are the next screen to build.
  return (
    <>
      <Button variant="secondary" disabled title={t("fx.comingSoon")}>
        <BellIcon /> {t("fx.alertMe")}
      </Button>
      <Button variant="secondary" href="/forecast/history" linkAs={Link}>
        <HistoryIcon /> {t("fx.fullHistory")}
      </Button>
    </>
  );
}

function streakText(t: Translate, n: number): string | false {
  if (n >= 2) return t("fx.streakUp", { n });
  if (n === 1) return t("fx.streakUp1");
  if (n <= -2) return t("fx.streakDown", { n: -n });
  if (n === -1) return t("fx.streakDown1");
  return false;
}

function monthName(locale: Locale, ym: string): string {
  return new Intl.DateTimeFormat(locale, { month: "long", timeZone: "UTC" }).format(new Date(`${ym}-01T00:00:00Z`));
}

function TrendIcon({ trend }: { trend: RateAnalytics["trend"] }) {
  const d = trend === "up" ? "M3 17l6-6 4 4 8-8M15 7h6v6" : trend === "down" ? "M3 7l6 6 4-4 8 8M15 17h6v-6" : "M3 12h18M15 6l6 6-6 6";
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={d} />
    </svg>
  );
}

function BellIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10 21h4" />
    </svg>
  );
}

function HistoryIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l3 3" />
    </svg>
  );
}
