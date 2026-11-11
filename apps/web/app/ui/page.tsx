import Link from "next/link";
import { notFound } from "next/navigation";
import { AlertBanner, Badge, Breadcrumb, Button, Field, PageHeader, SelectField, StatCard } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import { UiDemoControls } from "@/components/UiDemoControls";
import { getSession } from "@/lib/session";

export const dynamic = "force-dynamic";

/** Internal reference page for the Violeta library. Only with the developer switcher (internal environments). */
export default async function ComponentsPage() {
  const { switcher, t } = await getSession();
  if (!switcher) notFound();

  return (
    <AppShell>
      <div className="stack">
        <PageHeader
          breadcrumb={
            <Breadcrumb
              label={t("breadcrumb.label")}
              linkAs={Link}
              items={[{ label: t("nav.dashboard"), href: "/" }, { label: t("ui.title") }]}
            />
          }
          eyebrow={t("ui.eyebrow")}
          title={t("ui.title")}
          actions={<Button variant="outline" href="/" linkAs={Link}>{t("nav.dashboard")}</Button>}
        />

        <section className="card stack-sm">
          <h2>{t("ui.buttons")}</h2>
          <div className="row">
            <Button>primary</Button>
            <Button variant="secondary">secondary</Button>
            <Button variant="outline">outline</Button>
            <Button variant="dark">dark</Button>
            <Button variant="ghost">ghost</Button>
            <Button variant="danger">danger</Button>
            <Button disabled>disabled</Button>
          </div>
          <div className="row">
            <Button size="sm">sm · 40px</Button>
            <Button size="md">md · 44px</Button>
            <Button size="lg">lg · 52px</Button>
          </div>
        </section>

        <section className="card stack-sm">
          <h2>{t("ui.badges")}</h2>
          <div className="row">
            <Badge tone="violet">violet</Badge>
            <Badge tone="green">green</Badge>
            <Badge tone="orange">orange</Badge>
            <Badge tone="neutral">neutral</Badge>
            <Badge tone="plum">plum</Badge>
            <Badge tone="neutral" dot={false}>USD</Badge>
            <Badge tone="neutral" dot={false}>MXN</Badge>
          </div>
        </section>

        <section className="card stack-sm">
          <h2>{t("ui.inputs")}</h2>
          <div className="grid">
            <Field label={t("ui.rate")} hint={t("ui.rateHint")} defaultValue="18.4215" inputMode="decimal" />
            <Field label={t("ui.rate")} emphasis defaultValue="18.4215" inputMode="decimal" />
            <SelectField label={t("ui.entity")} defaultValue="mx">
              <option value="mx">Demo Norte S.A. de C.V.</option>
              <option value="us">Demo Norte Inc.</option>
            </SelectField>
          </div>
          <UiDemoControls
            rangeLabel={t("ui.range")}
            switchLabel={t("ui.switchLabel")}
            switchNote={t("ui.switchNote")}
          />
        </section>

        <section className="stack-sm">
          <h2 className="section-title">{t("ui.cards")}</h2>
          <div className="grid">
            <StatCard label={t("ui.statLabel")} value="USD 1,707,450" note={t("ui.statNote")} tone="positive" />
            <StatCard label="USD/MXN" value="18.4215" note="−0.8%" tone="negative" />
            <StatCard label={t("hist.observations")} value="8,796" />
          </div>
          <AlertBanner tone="info" kicker={t("ui.bannerKicker")} title={t("ui.bannerTitle")}
            action={{ label: t("ui.bannerAction"), href: "#" }}>
            {t("ui.bannerBody")}
          </AlertBanner>
          <AlertBanner tone="danger" kicker={t("ui.bannerKicker")} title={t("ui.bannerTitle")} headingLevel={3}>
            {t("ui.bannerBody")}
          </AlertBanner>
        </section>
      </div>
    </AppShell>
  );
}
