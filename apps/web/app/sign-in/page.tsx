import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { AuthHeading } from "@fxos/ui";
import { AuthPage } from "@/components/auth/AuthPage";
import { SignInForm } from "@/components/auth/SignInForm";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Sign in · FX Intelligence" };
export const dynamic = "force-dynamic";

/** T-06 Sign in (T-S2 errors come back from the action). */
export default async function SignInPage({ searchParams }: { searchParams: Promise<{ next?: string }> }) {
  const { session, sessionEnded, locale, t } = await getSession();
  const { next = "/" } = await searchParams;
  if (session) redirect(next.startsWith("/") && !next.startsWith("//") ? next : "/");

  return (
    <AuthPage t={t} locale={locale} panel={{ title: "auth.signIn.panelTitle", body: "auth.signIn.panelBody" }}>
      <AuthHeading title={t("auth.signIn.title")}>{t("auth.signIn.subtitle")}</AuthHeading>
      <SignInForm
        locale={locale}
        next={next}
        notice={sessionEnded ? t("auth.error.sessionEnded") : undefined}
        labels={{
          email: t("auth.email"),
          password: t("auth.password"),
          forgot: t("auth.forgotLink"),
          remember: t("auth.remember"),
          submit: t("auth.signIn.submit"),
          pending: t("auth.signIn.pending"),
          show: t("auth.show"),
          hide: t("auth.hide"),
          invalidTitle: t("auth.error.invalidTitle"),
          invalidBody: t("auth.error.invalidBody"),
          lockedTitle: t("auth.error.lockedTitle"),
          lockedBody: t("auth.error.lockedBody"),
          noAccessTitle: t("auth.error.noAccessTitle"),
          noAccessBody: t("auth.error.noAccessBody"),
          unavailable: t("auth.error.unavailable"),
        }}
      />
      <p className="vi-auth__fine">{t("auth.signIn.noAccount")}</p>
    </AuthPage>
  );
}
