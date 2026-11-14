import Link from "next/link";
import type { ReactNode } from "react";
import { AuthShell } from "@fxos/ui";
import type { MessageKey, Translate } from "@/lib/i18n";
import { LocaleSwitch } from "@/components/LocaleSwitch";
import { LOCALES, type Locale } from "@/lib/i18n";

/** A signed-out page in the AuthShell, with the panel copy for this screen and a language switch. */
export function AuthPage({ t, locale, panel, children }: {
  t: Translate; locale: Locale; panel: { title: MessageKey; body: MessageKey }; children: ReactNode;
}) {
  return (
    <AuthShell eyebrow={t("auth.panelEyebrow")} title={t(panel.title)} body={t(panel.body)} footer={t("auth.panelFooter")}
      productName={t("product")} productTagline={t("productSubtitle")}>
      {children}
      <div className="auth-locale">
        <LocaleSwitch label={t("user.language")} current={locale}
          options={LOCALES.map((l) => ({ value: l, label: t(`locale.${l}`) }))} />
      </div>
    </AuthShell>
  );
}

/** "← Back to sign in" (T-07). */
export function BackToSignIn({ t }: { t: Translate }) {
  return <Link href="/sign-in" className="vi-auth__link">{t("auth.backToSignIn")}</Link>;
}
