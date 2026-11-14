import { cache } from "react";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
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
  const profile = signedIn ? await apiGet<TenantProfile>("/api/tenant/profile") : null;

  // The user's choice wins; otherwise the company's default; otherwise Spanish.
  const chosen = store.get(LOCALE_COOKIE)?.value;
  const locale: Locale = isLocale(chosen) ? chosen : profile?.ok ? profile.data.locale : DEFAULT_LOCALE;

  return {
    session: signedIn,
    /** The signed-in company's slug, or undefined when signed out. */
    tenant: signedIn?.tenant.slug,
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

/** For signed-in pages: the session, or a redirect to sign-in that returns here afterwards. */
export async function requireSession(returnTo = "/") {
  const ctx = await getSession();
  if (!ctx.session) {
    redirect(`/sign-in${returnTo !== "/" ? `?next=${encodeURIComponent(returnTo)}` : ""}`);
  }
  return ctx as typeof ctx & { session: SessionInfo; tenant: string; viewer: SessionInfo["user"] };
}
