import type { Metadata } from "next";
import Link from "next/link";
import { PasswordField } from "@fxos/ui";
import { changePassword } from "@/app/settings/actions";
import { ActionForm } from "@/components/ActionForm";
import { SettingsShell } from "@/components/settings/SettingsShell";
import { apiGet, type Me } from "@/lib/api";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Security · Settings" };
export const dynamic = "force-dynamic";

/** U-02 Security: change the password with the current one, a new one and a confirmation. Nothing else here. */
export default async function SecurityPage() {
  const { locale, t } = await getSession();
  const me = await apiGet<Me>("/api/tenant/me");
  const changed = me.ok && me.data.passwordChangedAt
    ? new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(me.data.passwordChangedAt))
    : null;
  const show = t("auth.show");
  const hide = t("auth.hide");

  return (
    <SettingsShell page="security">
      <ActionForm action={changePassword} submit={t("security.submit")} pending={t("onb.saving")} className="card stack-sm"
        saved={t("security.done")}
        messages={{ wrong_password: t("security.wrong"), weak_password: t("security.weak"), mismatch: t("auth.match.no") }}>
        <span className="kicker">{t("security.kicker")}</span>
        {changed && <p className="subtitle">{t("security.lastChanged", { date: changed })}</p>}
        <PasswordField id="current-password" label={t("security.current")} name="current" autoComplete="current-password" required
          showLabel={show} hideLabel={hide} />
        <PasswordField id="new-password" label={t("auth.newPassword")} name="password" autoComplete="new-password" required
          showLabel={show} hideLabel={hide} hint={t("security.rules")} />
        <PasswordField id="confirm-password" label={t("security.confirm")} name="confirm" autoComplete="new-password" required
          showLabel={show} hideLabel={hide} />
        <p className="subtitle">
          {t("security.forgot")} <Link href="/forgot-password">{t("auth.forgotLink")}</Link>
        </p>
      </ActionForm>
    </SettingsShell>
  );
}
