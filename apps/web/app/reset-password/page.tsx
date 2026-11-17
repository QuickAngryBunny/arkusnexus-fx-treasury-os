import type { Metadata } from "next";
import Link from "next/link";
import { AuthHeading, Button, StatusTile } from "@fxos/ui";
import { AuthPage } from "@/components/auth/AuthPage";
import { ResetForm } from "@/components/auth/ResetForm";
import { authCall } from "@/lib/api";
import type { Translate } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Set a new password · FX Intelligence", referrer: "no-referrer" };
export const dynamic = "force-dynamic";

type LinkInfo = { state: "valid" | "expired" | "used" | "revoked" | "not_found"; email: string | null; name: string | null };

function Expired({ t, used }: { t: Translate; used: boolean }) {
  return (
    <>
      <StatusTile kind="clock" />
      <AuthHeading title={t(used ? "auth.used.title" : "auth.expired.title")}>{t("auth.expired.body")}</AuthHeading>
      <Button href="/forgot-password" linkAs={Link} size="lg" full>{t("auth.expired.newLink")}</Button>
      <Link href="/sign-in" className="vi-auth__link" style={{ textAlign: "center" }}>{t("auth.expired.signIn")}</Link>
    </>
  );
}

/** T-10 Set a new password, or T-S1 (reset variant) when the link expired, was used or is unknown. */
export default async function ResetPasswordPage({ searchParams }: { searchParams: Promise<{ token?: string }> }) {
  const { locale, t } = await getSession();
  const { token = "" } = await searchParams;
  const { data } = await authCall<LinkInfo>("GET", `/api/auth/password/reset?token=${encodeURIComponent(token)}`);
  const valid = data?.state === "valid" && data.email;

  if (!valid) {
    return (
      <AuthPage t={t} locale={locale} panel={{ title: "auth.expired.panelTitle", body: "auth.expired.panelBody" }}>
        <Expired t={t} used={data?.state === "used"} />
      </AuthPage>
    );
  }

  return (
    <AuthPage t={t} locale={locale} panel={{ title: "auth.reset.panelTitle", body: "auth.reset.panelBody" }}>
      <AuthHeading title={t("auth.reset.title")}>{t("auth.reset.for", { email: data.email! })}</AuthHeading>
      <ResetForm
        token={token}
        email={data.email!}
        name={data.name ?? ""}
        expired={{ expired: <Expired t={t} used={false} />, used: <Expired t={t} used /> }}
        labels={{
          newPassword: t("auth.newPassword"),
          confirm: t("auth.confirmPassword"),
          show: t("auth.show"),
          hide: t("auth.hide"),
          rulesLabel: t("auth.rules.label"),
          rules: {
            min_length: t("auth.rules.min_length"),
            number: t("auth.rules.number"),
            symbol: t("auth.rules.symbol"),
            personal_info: t("auth.rules.personal_info"),
            max_length: t("auth.rules.max_length"),
          },
          matchEmpty: t("auth.match.empty"),
          matchYes: t("auth.match.yes"),
          matchNo: t("auth.match.no"),
          submit: t("auth.reset.submit"),
          pending: t("auth.reset.pending"),
          weak: t("auth.reset.weak"),
          unavailable: t("auth.error.unavailable"),
        }}
      />
    </AuthPage>
  );
}
