import { NextResponse, type NextRequest } from "next/server";

const SESSION_COOKIE = "fx_session";

/** Pages that work signed out. Everything else needs a session. */
const PUBLIC = ["/sign-in", "/forgot-password", "/reset-password"];

/**
 * Fast path: no session cookie → straight to sign-in, remembering where the person was going.
 * A cookie that the API no longer accepts is caught by AppShell (requireSession), which redirects the same way.
 */
export function middleware(request: NextRequest) {
  const { pathname, search } = request.nextUrl;
  if (PUBLIC.some((p) => pathname === p || pathname.startsWith(`${p}/`))) return NextResponse.next();
  if (request.cookies.get(SESSION_COOKIE)?.value) return NextResponse.next();

  const url = request.nextUrl.clone();
  url.pathname = "/sign-in";
  url.search = pathname === "/" ? "" : `?next=${encodeURIComponent(pathname + search)}`;
  return NextResponse.redirect(url);
}

export const config = {
  // Skip Next's own files and static assets.
  matcher: ["/((?!_next/|favicon\\.ico|.*\\.(?:png|jpg|jpeg|gif|svg|webp|ico|woff2?)$).*)"],
};
