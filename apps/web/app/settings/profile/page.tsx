import type { Metadata } from "next";
import { Field, PasswordField, Segmented, SelectField } from "@fxos/ui";
import { saveProfile } from "@/app/settings/actions";
import { ActionForm } from "@/components/ActionForm";
import { SettingsShell } from "@/components/settings/SettingsShell";
import { TIME_ZONES } from "@/components/settings/timeZones";
import { apiGet, type Me } from "@/lib/api";
import type { MessageKey } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Profile · Settings" };
export const dynamic = "force-dynamic";

/** U-01 Profile: name, work email (changed only after a confirmation link), language, number format, email time zone. */
export default async function ProfilePage() {
  const { t } = await getSession();
  const me = await apiGet<Me>("/api/tenant/me");
  const m = me.ok ? me.data : null;

  return (
    <SettingsShell page="profile" eyebrow={m ? `${m.displayName} · ${m.role ? t(`role.${m.role}` as MessageKey) : ""}` : undefined}>
      {m && (
        <ActionForm action={saveProfile} submit={t("settings.save")} pending={t("onb.saving")} className="card stack-sm"
          saved={t("settings.saved")} savedEmail={t("profile.emailSent", { email: "{email}" })}
          messages={{ password_needed: t("profile.passwordNeeded"), wrong_password: t("security.wrong"), email_taken: t("email.taken"), invalid_email: t("profile.invalidEmail"), same_email: t("profile.invalidEmail") }}>
          <span className="kicker">{t("profile.kicker")}</span>
          <Field label={t("onb.name")} name="displayName" required maxLength={200} defaultValue={m.displayName} autoComplete="name" />
          <input type="hidden" name="currentEmail" value={m.email} />
          <Field label={t("auth.email")} name="email" type="email" required defaultValue={m.email} autoComplete="email"
            hint={t("profile.emailHint")} />
          <PasswordField id="email-password" label={t("security.current")} name="currentPassword" autoComplete="current-password"
            showLabel={t("auth.show")} hideLabel={t("auth.hide")} hint={t("profile.passwordHint")} />
          <span className="kicker">{t("profile.prefs")}</span>
          <div className="field-block">
            <span className="field-label">{t("company.language")}</span>
            <Segmented label={t("company.language")} name="language" defaultValue={m.language} onWhite
              options={[{ value: "en-US", label: "English" }, { value: "es-MX", label: "Español" }]} />
          </div>
          <div className="field-block">
            <span className="field-label">{t("profile.numberFormat")}</span>
            <Segmented label={t("profile.numberFormat")} name="numberFormat" defaultValue={m.numberFormat} onWhite
              options={[{ value: "period_decimal", label: "1,234.56" }, { value: "comma_decimal", label: "1.234,56" }]} />
          </div>
          <SelectField label={t("company.timeZone")} name="timeZone" defaultValue={m.timeZone} hint={t("profile.timeZoneHint")}>
            {TIME_ZONES.map((z) => <option key={z.id} value={z.id}>{t(z.label)}</option>)}
          </SelectField>
        </ActionForm>
      )}
    </SettingsShell>
  );
}
