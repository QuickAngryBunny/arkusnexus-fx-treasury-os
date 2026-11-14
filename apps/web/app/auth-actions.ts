"use server";

import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { authCall, SESSION_COOKIE } from "@/lib/api";
import { getSession } from "@/lib/session";
import type { PasswordRuleCode } from "@/lib/password";

export type SignInState = {
  email: string;
  error?: "invalid" | "locked" | "no_access" | "unavailable";
  lockedUntil?: string;
};

/** Only same-site paths, so ?next= can't send someone to another site after sign-in. */
function safeNext(value: FormDataEntryValue | null): string {
  const next = typeof value === "string" ? value : "";
  return next.startsWith("/") && !next.startsWith("//") && !next.startsWith("/\\") ? next : "/";
}

export async function signIn(_prev: SignInState, form: FormData): Promise<SignInState> {
  const email = String(form.get("email") ?? "").trim();
  const password = String(form.get("password") ?? "");
  const remember = form.get("remember") === "on";

  const { status, data } = await authCall<{ token: string; expiresAt: string; lockedUntil?: string }>(
    "POST", "/api/auth/sign-in", { email, password, remember });

  if (status === 200 && data?.token) {
    (await cookies()).set(SESSION_COOKIE, data.token, {
      httpOnly: true,
      sameSite: "lax",
      secure: process.env.NODE_ENV === "production",
      path: "/",
      // Unticked: a browser-session cookie (the API also ends the session after 12 hours).
      ...(remember ? { expires: new Date(data.expiresAt) } : {}),
    });
    redirect(safeNext(form.get("next")));
  }

  if (status === 423) return { email, error: "locked", lockedUntil: data?.lockedUntil };
  if (status === 403) return { email, error: "no_access" };
  if (status === 401) return { email, error: "invalid" };
  return { email, error: "unavailable" };
}

export async function signOut(): Promise<void> {
  await authCall("POST", "/api/auth/sign-out");
  (await cookies()).delete(SESSION_COOKIE);
  redirect("/sign-in");
}

export type ForgotState = { sent: boolean; email: string; error?: "unavailable" };

export async function requestReset(_prev: ForgotState, form: FormData): Promise<ForgotState> {
  const email = String(form.get("email") ?? "").trim();
  const { locale } = await getSession();
  const { status } = await authCall("POST", "/api/auth/password/forgot", { email, locale });
  // The page says the same thing whether or not the account exists.
  return status === 202 ? { sent: true, email } : { sent: false, email, error: "unavailable" };
}

export type ResetState = {
  error?: "weak" | "invalid_link" | "unavailable";
  rules?: PasswordRuleCode[];
  linkState?: "expired" | "used" | "revoked" | "not_found";
};

export async function resetPassword(_prev: ResetState, form: FormData): Promise<ResetState> {
  const token = String(form.get("token") ?? "");
  const password = String(form.get("password") ?? "");
  const { locale } = await getSession();

  const { status, data } = await authCall<{ rules?: PasswordRuleCode[]; state?: ResetState["linkState"] }>(
    "POST", "/api/auth/password/reset", { token, password, locale });

  if (status === 200) {
    // The reset ended every session, this browser's too.
    (await cookies()).delete(SESSION_COOKIE);
    redirect("/reset-password/done");
  }

  if (status === 400) return { error: "weak", rules: data?.rules ?? [] };
  if (status === 410) return { error: "invalid_link", linkState: data?.state ?? "not_found" };
  return { error: "unavailable" };
}
