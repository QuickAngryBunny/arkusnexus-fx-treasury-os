import type { Metadata } from "next";
import Link from "next/link";
import { AuthHeading, Button, StatusTile } from "@fxos/ui";
import { AuthPage } from "@/components/auth/AuthPage";
import { getSession } from "@/lib/session";

export const metadata: Metadata = { title: "Password changed · FX Intelligence" };

/** T-11 Password changed. */
export default async function PasswordChangedPage() {
  const { locale, t } = await getSession();
  return (
    <AuthPage t={t} locale={locale} panel={{ title: "auth.done.panelTitle", body: "auth.done.panelBody" }}>
      <StatusTile kind="check" />
      <AuthHeading title={t("auth.done.title")}>{t("auth.done.body")}</AuthHeading>
      <Button href="/sign-in" linkAs={Link} size="lg" full>{t("auth.signIn.submit")}</Button>
    </AuthPage>
  );
}
