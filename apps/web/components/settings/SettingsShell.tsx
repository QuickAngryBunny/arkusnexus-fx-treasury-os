import Link from "next/link";
import type { ReactNode } from "react";
import { Breadcrumb, PageHeader, SettingsNav } from "@fxos/ui";
import { AppShell } from "@/components/AppShell";
import type { MessageKey } from "@/lib/i18n";
import { requireSession } from "@/lib/session";

export type SettingsPage = "profile" | "security" | "notifications" | "company" | "team";

/** Settings frame: breadcrumb, title and the settings menu. Team only for admins; no Subscription item. */
export async function SettingsShell({ page, eyebrow, actions, children }: {
  page: SettingsPage; eyebrow?: ReactNode; actions?: ReactNode; children: ReactNode;
}) {
  const { session, t } = await requireSession(`/settings/${page}`);
  const admin = session.role === "admin";
  const item = (key: SettingsPage) => ({ key, label: t(`settings.nav.${key}` as MessageKey), href: `/settings/${key}` });

  return (
    <AppShell>
      <div className="stack">
        <PageHeader
          breadcrumb={<Breadcrumb label={t("breadcrumb.label")} linkAs={Link}
            items={[{ label: t("settings.title"), href: "/settings/profile" }, { label: t(`settings.nav.${page}` as MessageKey) }]} />}
          eyebrow={eyebrow}
          title={t(`settings.title.${page}` as MessageKey)}
          actions={actions}
        />
        <div className="settings-layout">
          <SettingsNav label={t("settings.navLabel")} active={page} linkAs={Link} groups={[
            { title: t("settings.group.account"), items: [item("profile"), item("security"), item("notifications")] },
            { title: t("settings.group.company"), items: [item("company"), ...(admin ? [item("team")] : [])] },
          ]} />
          <div className="settings-main">{children}</div>
        </div>
      </div>
    </AppShell>
  );
}
