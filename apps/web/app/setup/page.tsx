import type { Metadata } from "next";
import Link from "next/link";
import { redirect } from "next/navigation";
import { AuthHeading, StatusTile, StepPills } from "@fxos/ui";
import { AcceptForm } from "@/components/onboarding/AcceptForm";
import { RenewLink } from "@/components/onboarding/RenewLink";
import { acceptLabels } from "@/components/onboarding/labels";
import { SetupShell, setupPills } from "@/components/onboarding/SetupShell";
import { authCall, type InvitationInfo } from "@/lib/api";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Set up your company · FX Intelligence", referrer: "no-referrer" };
export const dynamic = "force-dynamic";

/** O-02 Your account (step 1 of 3), or O-S1 when the setup link expired or was used. */
export default async function SetupPage({ searchParams }: { searchParams: Promise<{ token?: string }> }) {
  const { t } = await getSession();
  const { token = "" } = await searchParams;
  const { data } = await authCall<InvitationInfo>("GET", `/api/auth/invitation?token=${encodeURIComponent(token)}`);
  if (data?.kind === "teammate") redirect(`/join?token=${encodeURIComponent(token)}`);
  const company = data?.company ?? t("product");

  const expired = (
    <>
      <StatusTile kind="clock" />
      <AuthHeading title={t(data?.state === "used" ? "onb.used.title" : data?.state === "expired" ? "onb.expired.title" : "onb.invalid.title")}>
        {data?.state === "used" ? t("onb.used.body") : data?.state === "expired" ? t("onb.expired.body", { company }) : t("onb.invalid.body")}
      </AuthHeading>
      {data?.state === "expired" && (
        <RenewLink token={token} labels={{ submit: t("onb.expired.renew"), pending: t("onb.saving"), sent: t("onb.expired.sent"), notSent: t("onb.expired.notSent") }} />
      )}
      <Link href="/sign-in" className="vi-auth__link" style={{ textAlign: "center" }}>{t("onb.expired.signIn")}</Link>
    </>
  );

  if (data?.state !== "valid" || !data.email) {
    return <SetupShell t={t} company={company}>{expired}</SetupShell>;
  }

  return (
    <SetupShell t={t} company={company}>
      <StepPills label={t("onb.stepsLabel")} steps={setupPills(t, 0)} />
      <AuthHeading title={t("onb.account.title")}>{t("onb.account.subtitle")}</AuthHeading>
      <AcceptForm token={token} email={data.email} name={data.displayName ?? ""} editableName labels={acceptLabels(t, t("onb.continue"))}
        expired={expired} />
    </SetupShell>
  );
}
