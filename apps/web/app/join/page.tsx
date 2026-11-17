import type { Metadata } from "next";
import Link from "next/link";
import { redirect } from "next/navigation";
import { AuthHeading, AuthShell, Badge, StatusTile, initials } from "@fxos/ui";
import { AcceptForm } from "@/components/onboarding/AcceptForm";
import { RenewLink } from "@/components/onboarding/RenewLink";
import { acceptLabels } from "@/components/onboarding/labels";
import { authCall, type InvitationInfo } from "@/lib/api";
import type { MessageKey } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Join your team · FX Intelligence", referrer: "no-referrer" };
export const dynamic = "force-dynamic";

/** T-05 Join (set a password), or T-S1 when the invitation expired, was used or cancelled. */
export default async function JoinPage({ searchParams }: { searchParams: Promise<{ token?: string }> }) {
  const { t } = await getSession();
  const { token = "" } = await searchParams;
  const { data } = await authCall<InvitationInfo>("GET", `/api/auth/invitation?token=${encodeURIComponent(token)}`);
  if (data?.kind === "tenant_setup") redirect(`/setup?token=${encodeURIComponent(token)}`);

  const company = data?.company ?? t("product");
  const inviter = data?.inviterName ?? company;
  const shell = (children: React.ReactNode) => (
    <AuthShell eyebrow={t("join.eyebrow", { inviter })} title={t("join.panelTitle", { company })} body={t("join.panelBody")}
      footer={t("auth.panelFooter")} productName={t("product")} productTagline={t("productSubtitle")}>
      {children}
    </AuthShell>
  );

  const expired = (
    <>
      <StatusTile kind="clock" />
      <AuthHeading title={t(data?.state === "used" ? "join.used.title" : data?.state === "expired" ? "join.expired.title" : "join.revoked.title")}>
        {data?.state === "used" ? t("join.used.body") : data?.state === "expired" ? t("join.expired.body", { inviter }) : t("join.invalid.body")}
      </AuthHeading>
      {data?.state === "expired" && (
        <RenewLink token={token} labels={{ submit: t("join.expired.renew"), pending: t("onb.saving"), sent: t("onb.expired.sent"), notSent: t("onb.expired.notSent") }} />
      )}
      <Link href="/sign-in" className="vi-auth__link" style={{ textAlign: "center" }}>{t("join.signIn")}</Link>
    </>
  );

  if (data?.state !== "valid" || !data.email) return shell(expired);

  const person = (
    <div className="join-person">
      <span className="join-person__avatar" aria-hidden="true">{initials(data.displayName ?? "")}</span>
      <span className="join-person__text"><strong>{data.displayName}</strong><span>{data.email}</span></span>
      {data.role && <Badge tone="violet" dot={false}>{t(`role.${data.role}` as MessageKey)}</Badge>}
    </div>
  );

  return shell(
    <>
      <AuthHeading title={t("join.title")}>{t("join.subtitle")}</AuthHeading>
      <AcceptForm token={token} email={data.email} name={data.displayName ?? ""} editableName={false} person={person}
        labels={acceptLabels(t, t("join.submit", { company }))} expired={expired} />
      <p className="vi-auth__fine">{t("join.wrongPerson", { inviter })}</p>
    </>,
  );
}
