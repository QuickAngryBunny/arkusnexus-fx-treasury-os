import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { AuthHeading, StepPills } from "@fxos/ui";
import { BanksStep } from "@/components/onboarding/BanksStep";
import { SetupShell, setupPills } from "@/components/onboarding/SetupShell";
import { bankLabels } from "@/components/onboarding/labels";
import { apiGet, type CompanyInfo } from "@/lib/api";
import { requireOnboardingAdmin } from "@/lib/session";

export const metadata: Metadata = { title: "Your banks · FX Intelligence" };
export const dynamic = "force-dynamic";

/** O-04 Your banks (step 3 of 3), O-S2 when a currency is missing. Resumes here if the admin left after step 2. */
export default async function BanksStepPage() {
  const { session, t } = await requireOnboardingAdmin();
  const result = await apiGet<CompanyInfo>("/api/tenant/company");
  if (result.ok && !result.data.onboarding.companyConfirmed) redirect("/setup/organization");
  const company = result.ok ? result.data.displayName : session.tenant.displayName;

  return (
    <SetupShell t={t} company={company} body={t("onb.banks.panelBody")}>
      <StepPills label={t("onb.stepsLabel")} steps={setupPills(t, 2)} />
      <AuthHeading title={t("onb.banks.title")}>{t("onb.banks.subtitle")}</AuthHeading>
      <BanksStep initial={result.ok ? result.data.banks : []} labels={bankLabels(t)} submit={t("onb.banks.finish")}
        pending={t("onb.saving")} note={t("onb.banks.later")} />
    </SetupShell>
  );
}
