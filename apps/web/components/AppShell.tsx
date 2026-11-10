import Link from "next/link";
import type { ReactNode } from "react";
import { AppNav, Badge, MenuDivider, MenuItem, MenuSection, UserMenu, type AppNavItem } from "@fxos/ui";
import { LocaleSwitch } from "@/components/LocaleSwitch";
import { TenantSwitcher } from "@/components/TenantSwitcher";
import { LOCALES } from "@/lib/i18n";
import { getSession } from "@/lib/session";

export type Section = "dashboard" | "forecast" | "treasury" | "exposure" | "simulator";

/** App frame for every signed-in screen: sidebar (AppNav + user menu), developer bar, main content. */
export async function AppShell({ active, children }: { active?: Section; children: ReactNode }) {
  const { switcher, tenant, tenants, viewer, locale, t } = await getSession();

  const items: AppNavItem[] = [
    { key: "dashboard", label: t("nav.dashboard"), href: "/", icon: "dashboard" },
    { key: "forecast", label: t("nav.forecast"), href: "/forecast", icon: "forecast" },
    { key: "treasury", label: t("nav.treasury"), href: "/treasury", icon: "treasury" },
    { key: "exposure", label: t("nav.exposure"), href: "/exposure", icon: "exposure" },
    { key: "simulator", label: t("nav.simulator"), href: "/simulator", icon: "simulator" },
  ];

  const name = viewer?.displayName ?? t("user.devViewer");

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
            <div className="nav-dev">
              <Badge tone="orange">{t("dev.dummyNotice")}</Badge>
              {switcher && tenants?.ok && (
                <TenantSwitcher
                  tenants={tenants.data}
                  current={tenant}
                  label={t("dev.switcher")}
                  placeholder={t("dev.chooseTenant")}
                />
              )}
            </div>
            <UserMenu name={name} email={viewer?.email} triggerLabel={t("user.menuLabel", { name })}>
              <MenuSection>{t("user.language")}</MenuSection>
              <LocaleSwitch
                label={t("user.language")}
                current={locale}
                options={LOCALES.map((l) => ({ value: l, label: t(`locale.${l}`) }))}
              />
              <MenuDivider />
              <MenuItem disabled>{t("user.profile")}</MenuItem>
              {switcher && (
                <>
                  <MenuDivider />
                  <MenuSection>{t("user.developer")}</MenuSection>
                  <MenuItem href="/ui">{t("user.components")}</MenuItem>
                </>
              )}
              <MenuDivider />
              <MenuItem disabled>{t("user.signOut")}</MenuItem>
            </UserMenu>
            </>
          }
        />
      </div>

      <div className="app__body">
        {!switcher && <p className="subtitle">{t("signInLater")}</p>}
        {tenants && !tenants.ok && (
          <p className="error" role="alert">
            {t("apiDown")}: {tenants.message}
          </p>
        )}
        <main id="main" tabIndex={-1}>
          {children}
        </main>
      </div>
    </div>
  );
}
