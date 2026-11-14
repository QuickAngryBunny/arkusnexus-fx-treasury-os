import Link from "next/link";
import type { ReactNode } from "react";
import { AppNav, MenuDivider, MenuItem, MenuSection, UserMenu, type AppNavItem } from "@fxos/ui";
import { signOut } from "@/app/auth-actions";
import { LocaleSwitch } from "@/components/LocaleSwitch";
import { LOCALES } from "@/lib/i18n";
import { requirePlatformAdmin } from "@/lib/session";

/** Frame of the tenant admin portal (AdminNav from the prototype). Platform admins only; others get a 404. */
export async function AdminShell({ children }: { children: ReactNode }) {
  const { session, viewer, locale, t } = await requirePlatformAdmin();

  const items: AppNavItem[] = [
    { key: "tenants", label: t("admin.nav.tenants"), href: "/admin", icon: "tenants" },
    { key: "sync", label: t("admin.nav.rateSync"), href: "/admin", icon: "sync", soon: t("admin.nav.later") },
    { key: "audit", label: t("admin.nav.audit"), href: "/admin", icon: "audit", soon: t("admin.nav.later") },
  ];

  return (
    <div className="app">
      <a href="#main" className="skip-link">{t("skipToContent")}</a>
      <div className="app__nav">
        <AppNav
          label={t("admin.nav.label")}
          items={items}
          active="tenants"
          linkAs={Link}
          brand={{ title: t("product"), subtitle: t("admin.nav.subtitle"), href: "/admin", label: t("admin.nav.home") }}
          user={
            <UserMenu name={viewer.displayName} email={viewer.email} triggerLabel={t("user.menuLabel", { name: viewer.displayName })}>
              <MenuSection>{t("user.language")}</MenuSection>
              <LocaleSwitch label={t("user.language")} current={locale}
                options={LOCALES.map((l) => ({ value: l, label: t(`locale.${l}`) }))} />
              <MenuDivider />
              {session.tenant && <MenuItem href="/">{t("admin.openApp")}</MenuItem>}
              <form action={signOut}>
                <MenuItem type="submit">{t("user.signOut")}</MenuItem>
              </form>
            </UserMenu>
          }
        />
      </div>
      <div className="app__body">
        <main id="main" tabIndex={-1}>{children}</main>
      </div>
    </div>
  );
}
