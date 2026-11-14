import Link from "next/link";
import type { ReactNode } from "react";
import { AppNav, Badge, MenuDivider, MenuItem, MenuSection, UserMenu, type AppNavItem } from "@fxos/ui";
import { signOut } from "@/app/auth-actions";
import { LocaleSwitch } from "@/components/LocaleSwitch";
import { LOCALES } from "@/lib/i18n";
import { requireSession } from "@/lib/session";

export type Section = "dashboard" | "forecast";

/** App frame for every signed-in screen: sidebar (AppNav + user menu), main content. Signed out → /sign-in. */
export async function AppShell({ active, children }: { active?: Section; children: ReactNode }) {
  const { session, viewer, locale, t } = await requireSession();
  const showDevTools = process.env.NODE_ENV !== "production";

  const items: AppNavItem[] = [
    { key: "dashboard", label: t("nav.dashboard"), shortLabel: t("nav.short.dashboard"), href: "/", icon: "dashboard" },
    { key: "forecast", label: t("nav.forecast"), shortLabel: t("nav.short.forecast"), href: "/forecast", icon: "forecast" },
  ];

  const name = viewer.displayName;

  // Dummy companies say so wherever their data shows.
  const devBlock = session.tenant.isDummy ? <Badge tone="orange">{t("dev.dummyNotice")}</Badge> : null;

  return (
    <div className="app">
      <a href="#main" className="skip-link">
        {t("skipToContent")}
      </a>
      <div className="app__nav">
        <AppNav
          label={t("nav.label")}
          items={items}
          active={active}
          linkAs={Link}
          brand={{ title: t("product"), subtitle: t("productSubtitle"), href: "/", label: t("homeLabel") }}
          user={
            <>
            {/* Desktop: above the user card. Phones: inside the user menu (nav-dev--menu below). */}
            {devBlock && <div className="nav-dev nav-dev--sidebar">{devBlock}</div>}
            <UserMenu name={name} email={viewer.email} triggerLabel={t("user.menuLabel", { name })}>
              {devBlock && <div className="nav-dev nav-dev--menu">{devBlock}</div>}
              <MenuSection>{t("user.language")}</MenuSection>
              <LocaleSwitch
                label={t("user.language")}
                current={locale}
                options={LOCALES.map((l) => ({ value: l, label: t(`locale.${l}`) }))}
              />
              <MenuDivider />
              <MenuItem disabled>{t("user.profile")}</MenuItem>
              {showDevTools && (
                <>
                  <MenuDivider />
                  <MenuSection>{t("user.developer")}</MenuSection>
                  <MenuItem href="/ui">{t("user.components")}</MenuItem>
                </>
              )}
              <MenuDivider />
              <form action={signOut}>
                <MenuItem type="submit">{t("user.signOut")}</MenuItem>
              </form>
            </UserMenu>
            </>
          }
        />
      </div>

      <div className="app__body">
        <main id="main" tabIndex={-1}>
          {children}
        </main>
      </div>
    </div>
  );
}
