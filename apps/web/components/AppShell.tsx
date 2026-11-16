import Link from "next/link";
import { redirect } from "next/navigation";
import type { ReactNode } from "react";
import { AppNav, Badge, MenuDivider, MenuItem, MenuSection, UserMenu, type AppNavItem } from "@fxos/ui";
import { signOut } from "@/app/auth-actions";
import { LocaleSwitch } from "@/components/LocaleSwitch";
import { TenantList, TenantMenu, type TenantMenuLabels } from "@/components/TenantMenu";
import { apiGet, type Company } from "@/lib/api";
import { LOCALES } from "@/lib/i18n";
import { requireSession } from "@/lib/session";

export type Section = "dashboard" | "forecast";

/** App frame for every signed-in screen: sidebar (AppNav + user menu), main content. Signed out → /sign-in. */
export async function AppShell({ active, children }: { active?: Section; children: ReactNode }) {
  const { session, profile, viewer, locale, t } = await requireSession();
  // O-03 / O-04: a company admin finishes onboarding before using the app. Other roles never see the setup steps.
  if (session.role === "admin" && profile?.ok && !profile.data.onboarding.completed) {
    redirect(profile.data.onboarding.companyConfirmed ? "/setup/banks" : "/setup/organization");
  }
  const showDevTools = process.env.NODE_ENV !== "production";
  const companiesResult = await apiGet<Company[]>("/api/auth/companies");
  const companies = companiesResult.ok ? companiesResult.data : [];
  const tenantLabels: TenantMenuLabels = {
    title: t("tenantMenu.title"),
    switchTo: t("tenantMenu.switchTo"),
    real: t("admin.data.real"),
    dummy: t("admin.data.dummy"),
    manage: t("tenantMenu.manage"),
  };
  const adminHref = session.isPlatformAdmin ? "/admin" : undefined;
  // One company and no portal: nothing to switch, so no menu.
  const showTenants = companies.length > 1 || Boolean(adminHref);

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
            {/* A-05: tenant menu above the user card on desktop; A-06: inside the user menu on phones. */}
            {showTenants && (
              <div className="nav-dev nav-dev--sidebar">
                <TenantMenu companies={companies} labels={tenantLabels} adminHref={adminHref} />
              </div>
            )}
            <UserMenu name={name} email={viewer.email} triggerLabel={t("user.menuLabel", { name })}>
              {devBlock && <div className="nav-dev nav-dev--menu">{devBlock}</div>}
              {showTenants && (
                <div className="nav-dev nav-dev--menu">
                  <TenantList companies={companies} labels={tenantLabels} adminHref={adminHref} />
                </div>
              )}
              <MenuSection>{t("user.language")}</MenuSection>
              <LocaleSwitch
                label={t("user.language")}
                current={locale}
                options={LOCALES.map((l) => ({ value: l, label: t(`locale.${l}`) }))}
              />
              <MenuDivider />
              <MenuItem href="/settings/profile">{t("user.profile")}</MenuItem>
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
