import type { Metadata } from "next";
import { AuthHeading } from "@fxos/ui";
import { AuthPage } from "@/components/auth/AuthPage";
import { ConfirmEmailForm } from "@/components/settings/ConfirmEmailForm";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Confirm your email · FX Intelligence", referrer: "no-referrer" };
export const dynamic = "force-dynamic";

/** The link from the email-change confirmation (U-01): one click confirms; nothing changes before it. */
export default async function ConfirmEmailPage({ searchParams }: { searchParams: Promise<{ token?: string }> }) {
  const { locale, t } = await getSession();
  const { token = "" } = await searchParams;
  return (
    <AuthPage t={t} locale={locale} panel={{ title: "email.panelTitle", body: "email.panelBody" }}>
      <AuthHeading title={t("email.title")}>{t("email.subtitle")}</AuthHeading>
      <ConfirmEmailForm token={token} labels={{
        submit: t("email.submit"), pending: t("onb.saving"), done: t("email.done"), signIn: t("auth.signIn.submit"),
        invalid: t("email.invalid"), taken: t("email.taken"), unavailable: t("auth.error.unavailable"),
      }} />
    </AuthPage>
  );
}
