import type { Metadata } from "next";
import { Badge, Switch } from "@fxos/ui";
import { saveNotifications } from "@/app/settings/actions";
import { ActionForm } from "@/components/ActionForm";
import { SettingsShell } from "@/components/settings/SettingsShell";
import { apiGet, type NotificationSetting } from "@/lib/api";
import type { MessageKey } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Notifications · Settings" };
export const dynamic = "force-dynamic";

// Rate alerts (Phase 4) send these two; the plan emails start with conversion plans (Phase 5).
const ALERTS = ["rate_target_reached", "rate_early_warning"];
const PLANS = ["conversion_reminder", "final_rate_needed", "plan_status_change", "approval_request", "approval_decision", "followed_plan"];
const FORECAST = ["forecast_move", "weekly_forecast_summary"];

/** U-03 Notifications: an email switch per item. Rate alerts send theirs now; plan emails start in Phase 5. */
export default async function NotificationsPage() {
  const { t } = await getSession();
  const result = await apiGet<NotificationSetting[]>("/api/tenant/me/notifications");
  const items = result.ok ? result.data : [];
  const section = (title: MessageKey, codes: string[]) => (
    <div className="stack-sm">
      <span className="kicker">{t(title)}</span>
      {items.filter((n) => codes.includes(n.code)).map((n) => (
        <div key={n.code} className="notif-row">
          <Switch name={`n_${n.code}`} defaultChecked={n.enabled}
            label={<>{t(`notif.${n.code}.title` as MessageKey)}{n.approversOnly && <> <Badge tone="violet" dot={false}>{t("notif.approvers")}</Badge></>}</>}
            note={t(`notif.${n.code}.note` as MessageKey)} />
        </div>
      ))}
    </div>
  );

  return (
    <SettingsShell page="notifications" eyebrow={t("notif.eyebrow")}>
      <ActionForm action={saveNotifications} submit={t("settings.save")} pending={t("onb.saving")} saved={t("settings.saved")} className="card stack">
        <input type="hidden" name="codes" value={items.map((n) => n.code).join(",")} />
        {section("notif.alerts", ALERTS)}
        {section("notif.plans", PLANS)}
        {section("notif.forecast", FORECAST)}
        <p className="subtitle">{t("notif.later")}</p>
      </ActionForm>
    </SettingsShell>
  );
}
