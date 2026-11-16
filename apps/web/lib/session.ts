import { cache } from "react";
import { cookies } from "next/headers";
import { notFound, redirect } from "next/navigation";
import { apiGet, SESSION_COOKIE, type SessionInfo, type TenantProfile } from "@/lib/api";
import { DEFAULT_LOCALE, LOCALE_COOKIE, isLocale, translator, type Locale } from "@/lib/i18n";

/**
 * Per-request context shared by the layout, the app shell and pages (React cache dedupes the API calls).
 * The signed-in user and their company come from the session cookie (GET /api/auth/session); signed-out
 * requests get a null session and the app shell sends them to /sign-in.
 */
export const getSession = cache(async () => {
  const store = await cookies();
  const hasCookie = Boolean(store.get(SESSION_COOKIE)?.value);

  const session = hasCookie ? await apiGet<SessionInfo>("/api/auth/session") : null;
  const signedIn = session?.ok ? session.data : null;
  const profile = signedIn?.tenant ? await apiGet<TenantProfile>("/api/tenant/profile") : null;

  // The language switch (cookie) wins; then the user's profile language (U-01); then the company's default; then Spanish.
  const chosen = store.get(LOCALE_COOKIE)?.value;
  const locale: Locale = isLocale(chosen)
    ? chosen
    : profile?.ok ? (profile.data.me?.language ?? profile.data.locale) : DEFAULT_LOCALE;

  return {
    session: signedIn,
    /** The signed-in company's slug, or undefined when signed out or in the portal only. */
    tenant: signedIn?.tenant?.slug,
    isPlatformAdmin: signedIn?.isPlatformAdmin ?? false,
    profile,
    viewer: signedIn?.user,
    role: signedIn?.role,
    /** The API answered, but not with a session (expired, revoked, deactivated). */
    sessionEnded: hasCookie && !signedIn && session !== null && !session.ok && session.status === 401,
    apiDown: session !== null && !session.ok && session.status === 0,
    locale,
    t: translator(locale),
  };
});

type Company = NonNullable<SessionInfo["tenant"]>;

/**
 * For FX app pages: a session with a company, or a redirect. Signed out → sign-in (returning here afterwards);
 * a platform admin signed in to the portal only → /admin.
 */
export async function requireSession(returnTo = "/") {
  const ctx = await getSession();
  if (!ctx.session) {
    redirect(`/sign-in${returnTo !== "/" ? `?next=${encodeURIComponent(returnTo)}` : ""}`);
  }
  if (!ctx.session.tenant) {
    redirect(ctx.session.isPlatformAdmin ? "/admin" : "/sign-in");
  }
  return ctx as typeof ctx & { session: SessionInfo & { tenant: Company }; tenant: string; viewer: SessionInfo["user"] };
}

/** For the tenant admin portal: a platform admin's session; anyone else gets a 404 (the portal isn't advertised). */
export async function requirePlatformAdmin() {
  const ctx = await getSession();
  if (!ctx.session) redirect("/sign-in?next=%2Fadmin");
  if (!ctx.session.isPlatformAdmin) notFound();
  return ctx as typeof ctx & { session: SessionInfo; viewer: SessionInfo["user"] };
}

/** Onboarding steps 2 and 3: the company's admins only; a finished company goes to its dashboard. */
export async function requireOnboardingAdmin() {
  const ctx = await requireSession("/setup/organization");
  if (ctx.session.role !== "admin") redirect("/");
  if (ctx.profile?.ok && ctx.profile.data.onboarding.completed) redirect("/");
  return ctx;
}
