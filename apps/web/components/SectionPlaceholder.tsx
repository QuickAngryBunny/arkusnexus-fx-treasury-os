import { AlertBanner, PageHeader } from "@fxos/ui";
import { AppShell, type Section } from "@/components/AppShell";
import type { MessageKey } from "@/lib/i18n";
import { getSession } from "@/lib/session";

/** Top-level section that is not built yet: real title, a short note on what it will hold. */
export async function SectionPlaceholder({ section }: { section: Exclude<Section, "dashboard" | "forecast"> }) {
  const { t } = await getSession();
  return (
    <AppShell active={section}>
      <div className="stack">
        <PageHeader title={t(`nav.${section}` as MessageKey)} />
        <AlertBanner tone="brand" kicker={t("section.soonKicker")} title={t("section.soonTitle")}>
          {t(`section.${section}.body` as MessageKey)}
        </AlertBanner>
      </div>
    </AppShell>
  );
}
