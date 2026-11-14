import type { BadgeTone } from "@fxos/ui";
import type { AdminTenant } from "@/lib/api";
import type { Translate } from "@/lib/i18n";
import type { NewTenantLabels } from "@/components/admin/NewTenant";

/** One status word for a tenant, from its status and its setup job. */
export function tenantState(t: Translate, x: AdminTenant): { label: string; tone: BadgeTone } {
  if (x.setup?.status === "failed") return { label: t("admin.state.failed"), tone: "orange" };
  if (x.status === "archived") return { label: t("admin.state.archived"), tone: "neutral" };
  if (x.status === "active") return { label: t("admin.state.ready"), tone: "green" };
  return { label: t("admin.state.settingUp"), tone: "violet" };
}

export function newTenantLabels(t: Translate): NewTenantLabels {
  return {
    open: t("admin.new.open"),
    kicker: t("admin.new.kicker"),
    title: t("admin.new.title"),
    close: t("admin.close"),
    intro: t("admin.new.intro"),
    name: t("admin.new.name"),
    nameHint: t("admin.new.nameHint"),
    email: t("admin.new.email"),
    emailHint: t("admin.new.emailHint"),
    realData: t("admin.new.realData"),
    realDataHint: t("admin.new.realDataHint"),
    nextTitle: t("admin.new.nextTitle"),
    next1: t("admin.new.next1"),
    next2: t("admin.new.next2"),
    next3: t("admin.new.next3"),
    cancel: t("admin.cancel"),
    submit: t("admin.new.submit"),
    pending: t("admin.new.pending"),
  };
}
