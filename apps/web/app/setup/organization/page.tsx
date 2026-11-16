import type { Metadata } from "next";
import { AuthHeading, Field, Segmented, SelectField, StepPills } from "@fxos/ui";
import { saveOrganization } from "@/app/settings/actions";
import { ActionForm } from "@/components/ActionForm";
import { SetupShell, setupPills } from "@/components/onboarding/SetupShell";
import { TIME_ZONES } from "@/components/settings/timeZones";
import { apiGet, type CompanyInfo } from "@/lib/api";
import { requireOnboardingAdmin } from "@/lib/session";

export const metadata: Metadata = { title: "Your organization · FX Intelligence" };
export const dynamic = "force-dynamic";

/** O-03 Your organization (step 2 of 3). Pre-filled with what the platform knows. */
export default async function OrganizationStep() {
  const { session, locale, t } = await requireOnboardingAdmin();
  const result = await apiGet<CompanyInfo>("/api/tenant/company");
  const c = result.ok ? result.data : null;
  const company = c?.displayName ?? session.tenant.displayName;

  return (
    <SetupShell t={t} company={company}>
      <StepPills label={t("onb.stepsLabel")} steps={setupPills(t, 1)} />
      <AuthHeading title={t("onb.org.title")}>{t("onb.org.subtitle")}</AuthHeading>
      <ActionForm action={saveOrganization} submit={t("onb.continue")} pending={t("onb.saving")} full>
        <Field label={t("company.name")} name="displayName" required maxLength={200} defaultValue={company} hint={t("company.nameHint")} />
        <Field label={<>{t("company.legalName")} <span className="optional">{t("company.optional")}</span></>} name="legalName"
          maxLength={200} defaultValue={c?.legalName ?? ""} />
        <div className="form-grid">
          <SelectField label={t("company.country")} name="country" defaultValue={c?.country ?? "MX"}>
            <option value="MX">{t("country.MX")}</option>
            <option value="US">{t("country.US")}</option>
          </SelectField>
          <SelectField label={t("company.timeZone")} name="timeZone" defaultValue={c?.timeZone ?? "America/Mexico_City"}>
            {TIME_ZONES.map((z) => <option key={z.id} value={z.id}>{t(z.label)}</option>)}
          </SelectField>
        </div>
        <div className="field-block">
          <span className="field-label">{t("company.direction")}</span>
          <Segmented label={t("company.direction")} name="conversionDirection" defaultValue={c?.conversionDirection ?? "usd_to_mxn"}
            options={[
              { value: "usd_to_mxn", label: "USD → MXN" },
              { value: "mxn_to_usd", label: "MXN → USD" },
              { value: "both", label: t("company.direction.both") },
            ]} />
        </div>
        <div className="field-block">
          <span className="field-label">{t("company.language")}</span>
          <Segmented label={t("company.language")} name="locale" defaultValue={c?.locale ?? locale}
            options={[{ value: "en-US", label: "English" }, { value: "es-MX", label: "Español" }]} />
        </div>
        <input type="hidden" name="baseCurrency" value={c?.baseCurrency ?? "USD"} />
        <input type="hidden" name="reportingCurrency" value={c?.reportingCurrency ?? "MXN"} />
      </ActionForm>
    </SetupShell>
  );
}
