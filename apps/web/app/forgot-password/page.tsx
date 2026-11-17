import type { Metadata } from "next";
import { AuthPage, BackToSignIn } from "@/components/auth/AuthPage";
import { ForgotForm } from "@/components/auth/ForgotForm";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Forgot password · FX Intelligence" };
export const dynamic = "force-dynamic";

/** T-07 Forgot password → T-08 Check your email. */
export default async function ForgotPasswordPage() {
  const { locale, t } = await getSession();
  return (
    <AuthPage t={t} locale={locale} panel={{ title: "auth.forgot.panelTitle", body: "auth.forgot.panelBody" }}>
      <BackToSignIn t={t} />
      <ForgotForm labels={{
        title: t("auth.forgot.title"),
        subtitle: t("auth.forgot.subtitle"),
        email: t("auth.email"),
        submit: t("auth.forgot.submit"),
        pending: t("auth.forgot.pending"),
        sentTitle: t("auth.sent.title"),
        sentBody: t("auth.sent.body"),
        resendIn: t("auth.sent.resendIn"),
        resend: t("auth.sent.resend"),
        otherEmail: t("auth.sent.otherEmail"),
        help: t("auth.sent.help"),
        unavailable: t("auth.error.unavailable"),
      }} />
    </AuthPage>
  );
}
