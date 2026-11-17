"use client";

import Link from "next/link";
import { useActionState, useEffect, useMemo, useRef, useState, useTransition, type ReactNode } from "react";
import { Badge, Button, Drawer, Field, Notice, Segmented, Switch } from "@fxos/ui";
import { deleteAlert, saveAlert, turnAlertOn, type AlertFormState } from "@/app/forecast/alert-actions";
import { chanceWithin, isPast, LOOK_AHEADS, parseRate, type AlertDirection, type LookAhead } from "@/lib/alerts";
import type { RateAlert, RateAlerts } from "@/lib/api";

/** Every string the drawer shows; "{x}" placeholders are filled here (no functions cross the server boundary). */
export type AlertDrawerLabels = {
  open: string;
  kicker: string;
  titleNew: string;
  titleEdit: string;
  titleList: string;
  close: string;
  done: string;
  // List (shown once you have at least one alert)
  listIntro: string;
  above: string; // "Rises to or above {rate}"
  below: string;
  chance30: string; // "{chance}% chance within 30 days"
  earlyOn: string; // "Early warning · {days} days"
  earlyOff: string;
  keepOnShort: string;
  keepOffShort: string;
  statusOn: string;
  statusOff: string;
  statusReached: string; // "Off · reached {date}"
  edit: string;
  turnOn: string;
  remove: string;
  confirmRemove: string;
  confirmYes: string;
  confirmNo: string;
  addAnother: string;
  maxReached: string; // "{max}"
  // Form
  today: string; // "Today {rate}"
  forecast30: string; // "30D forecast {rate}"
  chartAria: string; // "{rate}"
  emailWhen: string;
  dirAbove: string;
  dirBelow: string;
  rate: string;
  rateHint: string;
  oddsEmpty: string; // "{today}"
  oddsAbove: string; // "{chance}", "{rate}"
  oddsBelow: string;
  oddsPast: string; // "{rate}"
  oddsNoForecast: string;
  trendTitle: string;
  trendSubAbove: string; // "{needed}", "{window}", "{rate}"
  trendSubBelow: string;
  lookAhead: string;
  days: string; // "{days} days"
  trendNow: string; // "{days}", "{chance}"
  trendOnce: string;
  keepTitle: string;
  keepOn: string; // "{rate}"
  keepOff: string;
  footnote: string; // "{email}", "{stop}"
  stopOn: string;
  stopOff: string;
  emailsOff: string;
  emailsOffBody: string;
  earlyEmailsOff: string;
  settingsLink: string;
  cancel: string;
  back: string;
  create: string;
  save: string;
  saving: string;
  saved: string;
};

type Props = {
  data: RateAlerts;
  labels: AlertDrawerLabels;
  /** Number locale from the profile (U-01): rates show 2 decimals in it. */
  numbers: string;
  /** Language, for dates. */
  locale: string;
  email: string;
  icon?: ReactNode;
  initialOpen?: boolean;
};

const fill = (s: string, values: Record<string, string | number>) =>
  s.replace(/\{(\w+)\}/g, (m, k: string) => (k in values ? String(values[k]) : m));

export function RateAlertDrawer({ data, labels, numbers, locale, email, icon, initialOpen = false }: Props) {
  const hasAlerts = data.alerts.length > 0;
  const [open, setOpen] = useState(initialOpen);
  const [view, setView] = useState<"list" | "form">(hasAlerts ? "list" : "form");
  const [editing, setEditing] = useState<RateAlert | null>(null);
  const rate2 = (v: number) => new Intl.NumberFormat(numbers, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  const pct = (c: number) => Math.round(c * 100);

  // The list is only for people with alerts; deleting the last one goes straight to a new alert.
  useEffect(() => {
    if (view === "list" && !hasAlerts) setView("form");
  }, [view, hasAlerts]);

  const openDrawer = () => {
    setEditing(null);
    setView(hasAlerts ? "list" : "form");
    setOpen(true);
  };
  const toForm = (alert: RateAlert | null) => {
    setEditing(alert);
    setView("form");
  };
  const backOrClose = () => (hasAlerts ? (setEditing(null), setView("list")) : setOpen(false));

  return (
    <>
      <Button variant="secondary" onClick={openDrawer}>
        {icon} {labels.open}
      </Button>
      {view === "list" ? (
        <Drawer open={open} onClose={() => setOpen(false)} kicker={labels.kicker} title={labels.titleList} closeLabel={labels.close}
          footer={
            <>
              <Button variant="outline" size="lg" onClick={() => setOpen(false)}>{labels.done}</Button>
              <Button size="lg" onClick={() => toForm(null)} disabled={data.alerts.length >= data.maxAlerts}
                title={data.alerts.length >= data.maxAlerts ? fill(labels.maxReached, { max: data.maxAlerts }) : undefined}>
                + {labels.addAnother}
              </Button>
            </>
          }>
          <AlertList data={data} labels={labels} rate2={rate2} pct={pct} locale={locale} onEdit={toForm} />
        </Drawer>
      ) : (
        <AlertForm key={editing?.id ?? "new"} data={data} labels={labels} rate2={rate2} pct={pct} email={email}
          editing={editing} open={open} onClose={() => setOpen(false)} onBack={backOrClose} backLabel={hasAlerts ? labels.back : labels.cancel}
          onSaved={() => { setEditing(null); setView("list"); }} />
      )}
    </>
  );
}

function AlertList({ data, labels, rate2, pct, locale, onEdit }: {
  data: RateAlerts; labels: AlertDrawerLabels; rate2: (v: number) => string; pct: (c: number) => number; locale: string;
  onEdit: (a: RateAlert) => void;
}) {
  const [confirming, setConfirming] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, start] = useTransition();
  const date = (iso: string) => new Intl.DateTimeFormat(locale, { day: "numeric", month: "short" }).format(new Date(iso));

  const run = (work: () => Promise<{ ok: boolean; error?: string }>) =>
    start(async () => {
      const r = await work();
      setError(r.ok ? null : r.error ?? "?");
      setConfirming(null);
    });

  return (
    <div className="stack-sm">
      {!data.emails.reached && (
        <Notice tone="warning" title={labels.emailsOff}>
          {labels.emailsOffBody} <Link href="/settings/notifications">{labels.settingsLink}</Link>
        </Notice>
      )}
      <p className="vi-alerts__intro">{labels.listIntro}</p>
      {error && <Notice tone="danger" title={error} />}
      <ul className="vi-alerts" aria-busy={pending}>
        {data.alerts.map((a) => (
          <li key={a.id} className={a.isOn ? "vi-alerts__item" : "vi-alerts__item vi-alerts__item--off"}>
            <div className="vi-alerts__top">
              <strong>{fill(a.direction === "above" ? labels.above : labels.below, { rate: rate2(a.rate) })}</strong>
              {a.isOn
                ? <Badge tone="green">{labels.statusOn}</Badge>
                : <Badge tone="neutral">{a.reachedAt ? fill(labels.statusReached, { date: date(a.reachedAt) }) : labels.statusOff}</Badge>}
            </div>
            <span className="vi-alerts__meta">
              {[
                a.chance30 !== null && a.isOn ? fill(labels.chance30, { chance: pct(a.chance30) }) : null,
                a.earlyWarning ? fill(labels.earlyOn, { days: a.lookAheadDays }) : labels.earlyOff,
                a.keepOn ? labels.keepOnShort : labels.keepOffShort,
              ].filter(Boolean).join(" · ")}
            </span>
            {confirming === a.id ? (
              <div className="vi-alerts__actions" role="group" aria-label={labels.confirmRemove}>
                <span className="vi-alerts__confirm">{labels.confirmRemove}</span>
                <Button size="sm" variant="danger" disabled={pending} onClick={() => run(() => deleteAlert(a.id))}>{labels.confirmYes}</Button>
                <Button size="sm" variant="outline" disabled={pending} onClick={() => setConfirming(null)}>{labels.confirmNo}</Button>
              </div>
            ) : (
              <div className="vi-alerts__actions">
                <Button size="sm" variant="outline" onClick={() => onEdit(a)}>{labels.edit}</Button>
                {!a.isOn && (
                  <Button size="sm" variant="secondary" disabled={pending} onClick={() => run(() => turnAlertOn(a))}>{labels.turnOn}</Button>
                )}
                <Button size="sm" variant="ghost" onClick={() => setConfirming(a.id)}>{labels.remove}</Button>
              </div>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

function AlertForm({ data, labels, rate2, pct, email, editing, open, onClose, onBack, backLabel, onSaved }: {
  data: RateAlerts; labels: AlertDrawerLabels; rate2: (v: number) => string; pct: (c: number) => number; email: string;
  editing: RateAlert | null; open: boolean; onClose: () => void; onBack: () => void; backLabel: string; onSaved: () => void;
}) {
  const [state, action, pending] = useActionState<AlertFormState, FormData>(saveAlert, {});
  const [direction, setDirection] = useState<AlertDirection>(editing?.direction ?? "above");
  const [rateText, setRateText] = useState(editing ? rate2(editing.rate) : "");
  const [early, setEarly] = useState(editing?.earlyWarning ?? true);
  const [lookAhead, setLookAhead] = useState<LookAhead>((editing?.lookAheadDays as LookAhead) ?? 7);
  const [keepOn, setKeepOn] = useState(editing?.keepOn ?? false);
  const handled = useRef<number | undefined>(undefined);

  useEffect(() => {
    if (state.ok && state.savedAt && handled.current !== state.savedAt) {
      handled.current = state.savedAt;
      onSaved();
    }
  }, [state, onSaved]);

  const today = data.today?.rate ?? null;
  const rate = parseRate(rateText);
  const shown = rate ?? today ?? 0;
  const f = data.forecast;
  const chance = (days: number) => (rate !== null && f ? chanceWithin(direction, rate, f.lastRate, f.horizons, days) : null);
  const rule = data.trendRules.find((r) => r.lookAheadDays === lookAhead) ?? { needed: 3, window: 5 };
  const h30 = f?.horizons.find((h) => h.days === 30);

  let odds: string;
  if (rate === null) odds = fill(labels.oddsEmpty, { today: today !== null ? rate2(today) : "—" });
  else if (today !== null && isPast(direction, rate, today)) odds = fill(labels.oddsPast, { rate: rate2(rate) });
  else if (!f) odds = labels.oddsNoForecast;
  else odds = fill(direction === "above" ? labels.oddsAbove : labels.oddsBelow, { chance: pct(chance(30) ?? 0), rate: rate2(rate) });

  const nowChance = chance(lookAhead);
  const rateShown = rate2(shown);

  return (
    <Drawer open={open} onClose={onClose} kicker={labels.kicker} title={editing ? labels.titleEdit : labels.titleNew} closeLabel={labels.close}
      footer={
        <>
          <Button variant="outline" size="lg" onClick={onBack}>{backLabel}</Button>
          <Button type="submit" size="lg" form="rate-alert-form" disabled={pending}>
            {pending ? labels.saving : editing ? labels.save : labels.create}
          </Button>
        </>
      }>
      <form id="rate-alert-form" action={action} className="stack-sm">
        {editing && <input type="hidden" name="id" value={editing.id} />}
        {state.error && !state.rateError && <Notice tone="danger" title={state.error} />}
        {!data.emails.reached && (
          <Notice tone="warning" title={labels.emailsOff}>
            {labels.emailsOffBody} <Link href="/settings/notifications">{labels.settingsLink}</Link>
          </Notice>
        )}

        <div className="vi-alert-preview">
          <div className="vi-alert-preview__head">
            <span>{fill(labels.today, { rate: today !== null ? rate2(today) : "—" })}</span>
            {h30 && <span>{fill(labels.forecast30, { rate: rate2(h30.point) })}</span>}
          </div>
          <AlertChart data={data} rate={rate} label={fill(labels.chartAria, { rate: rateShown })} tag={rate !== null ? rateShown : null} />
          <span className="vi-alert-preview__odds" aria-live="polite">{odds}</span>
        </div>

        <div className="field-block">
          <span className="field-label">{labels.emailWhen}</span>
          <Segmented label={labels.emailWhen} name="direction" value={direction} onValueChange={(v) => setDirection(v)} onWhite
            options={[{ value: "above", label: labels.dirAbove }, { value: "below", label: labels.dirBelow }]} />
        </div>

        <Field label={labels.rate} name="rate" inputMode="decimal" autoComplete="off" emphasis
          placeholder={today !== null ? rate2(today) : undefined}
          value={rateText} onChange={(e) => setRateText(e.target.value)}
          hint={labels.rateHint} error={state.rateError ? state.error : undefined} />

        <section className={early ? "vi-alert-option vi-alert-option--on" : "vi-alert-option"} aria-label={labels.trendTitle}>
          <Switch label={labels.trendTitle} name="earlyWarning" checked={early} onCheckedChange={setEarly}
            note={fill(direction === "above" ? labels.trendSubAbove : labels.trendSubBelow, { needed: rule.needed, window: rule.window, rate: rateShown })} />
          {early && (
            <div className="vi-alert-option__more">
              <span className="field-label">{labels.lookAhead}</span>
              <Segmented label={labels.lookAhead} name="lookAheadDays" value={String(lookAhead) as `${LookAhead}`}
                onValueChange={(v) => setLookAhead(Number(v) as LookAhead)} onWhite
                options={LOOK_AHEADS.map((d) => ({ value: String(d) as `${LookAhead}`, label: fill(labels.days, { days: d }) }))} />
              {nowChance !== null && <span className="field-hint">{fill(labels.trendNow, { days: lookAhead, chance: pct(nowChance) })}</span>}
              <span className="field-hint">{labels.trendOnce}{!data.emails.earlyWarning ? ` ${labels.earlyEmailsOff}` : ""}</span>
            </div>
          )}
          {!early && <input type="hidden" name="lookAheadDays" value={lookAhead} />}
        </section>

        <Switch label={labels.keepTitle} name="keepOn" checked={keepOn} onCheckedChange={setKeepOn}
          note={fill(keepOn ? labels.keepOn : labels.keepOff, { rate: rateShown })} />

        <p className="field-hint">{fill(labels.footnote, { email, stop: keepOn ? labels.stopOn : labels.stopOff })}</p>
      </form>
    </Drawer>
  );
}

/** The last 12 months of FIX, the 30-day forecast with its 8-in-10 range, and the alert's rate as a line. */
function AlertChart({ data, rate, label, tag }: { data: RateAlerts; rate: number | null; label: string; tag: string | null }) {
  const W = 720, H = 240, split = 600;
  const geometry = useMemo(() => {
    const hist = data.history;
    if (hist.length < 2) return null;
    const step = Math.max(1, Math.floor(hist.length / 120));
    const pts = hist.filter((_, i) => i % step === 0 || i === hist.length - 1);
    const f = data.forecast;
    const horizons = (f?.horizons ?? []).filter((h) => h.days <= 30).sort((a, b) => a.days - b.days);
    const values = [...pts.map((p) => p.rate), ...horizons.flatMap((h) => [h.lower80, h.upper80])];
    return { pts, horizons, last: hist[hist.length - 1].rate, min: Math.min(...values), max: Math.max(...values) };
  }, [data]);
  if (!geometry) return null;

  const lo = Math.min(geometry.min, rate ?? geometry.min), hi = Math.max(geometry.max, rate ?? geometry.max);
  const pad = (hi - lo) * 0.12 || 0.5;
  const y = (v: number) => H - ((v - (lo - pad)) / (hi - lo + 2 * pad)) * H;
  const x = (i: number) => (i / (geometry.pts.length - 1)) * split;
  const line = geometry.pts.map((p, i) => `${i ? "L" : "M"}${x(i).toFixed(1)},${y(p.rate).toFixed(1)}`).join(" ");
  const fx = (days: number) => split + (days / 30) * (W - split);
  const hz = geometry.horizons;
  const band = hz.length
    ? `M${split},${y(geometry.last)} ` + hz.map((h) => `L${fx(h.days)},${y(h.upper80)}`).join(" ") + " "
      + [...hz].reverse().map((h) => `L${fx(h.days)},${y(h.lower80)}`).join(" ") + " Z"
    : "";
  const center = hz.length ? `M${split},${y(geometry.last)} ` + hz.map((h) => `L${fx(h.days)},${y(h.point)}`).join(" ") : "";
  const ly = rate !== null ? Math.max(14, Math.min(H - 14, y(rate))) : null;

  return (
    <svg viewBox={`0 0 ${W} ${H}`} width="100%" role="img" aria-label={label} className="vi-alert-chart">
      {band && <path d={band} className="vi-alert-chart__band" />}
      <path d={line} className="vi-alert-chart__line" />
      {center && <path d={center} className="vi-alert-chart__center" />}
      {ly !== null && (
        <>
          <path d={`M0 ${ly.toFixed(0)}H${W}`} className="vi-alert-chart__target" />
          {tag && (
            <>
              <rect x={W - 124} y={ly - 20} width={120} height={40} rx={20} className="vi-alert-chart__tag" />
              <text x={W - 64} y={ly + 8} textAnchor="middle" className="vi-alert-chart__tagText">{tag}</text>
            </>
          )}
        </>
      )}
    </svg>
  );
}
