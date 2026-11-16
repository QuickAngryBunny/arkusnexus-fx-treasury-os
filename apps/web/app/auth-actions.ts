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
    await setSessionCookie(data.token, data.expiresAt, remember);
    redirect(safeNext(form.get("next")));
  }

  if (status === 423) return { email, error: "locked", lockedUntil: data?.lockedUntil };
  if (status === 403) return { email, error: "no_access" };
  if (status === 401) return { email, error: "invalid" };
  return { email, error: "unavailable" };
}

/** The HTTP-only session cookie. Unticked "keep me signed in": a browser-session cookie (the API ends it after 12 h). */
async function setSessionCookie(token: string, expiresAt: string, remember: boolean): Promise<void> {
  (await cookies()).set(SESSION_COOKIE, token, {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    path: "/",
    ...(remember ? { expires: new Date(expiresAt) } : {}),
  });
}

export type AcceptState = {
  error?: "weak" | "invalid_link" | "unavailable" | "tenant_unavailable";
  rules?: PasswordRuleCode[];
  linkState?: string;
};

/** O-02 / T-05: accept the link, sign in, then onboarding (setup) or the dashboard (join). */
export async function acceptInvitation(_prev: AcceptState, form: FormData): Promise<AcceptState> {
  const { locale } = await getSession();
  const token = String(form.get("token") ?? "");
  const { status, data } = await authCall<{ token?: string; expiresAt?: string; kind?: string; rules?: PasswordRuleCode[]; state?: string }>(
    "POST", "/api/auth/invitation/accept", {
      token,
      displayName: String(form.get("displayName") ?? ""),
      password: String(form.get("password") ?? ""),
      remember: true,
      locale,
    });
  if (status === 200 && data?.token) {
    await setSessionCookie(data.token, data.expiresAt!, true);
    redirect(data.kind === "tenant_setup" ? "/setup/organization" : "/?joined=1");
  }
  if (status === 202) redirect("/sign-in?joined=1");
  if (status === 400) return { error: "weak", rules: data?.rules ?? [] };
  if (status === 410) return { error: "invalid_link", linkState: data?.state };
  if (status === 409) return { error: "tenant_unavailable" };
  return { error: "unavailable" };
}

export type RenewState = { sent?: boolean };

/** O-S1 / T-S1: email a fresh link to the same address. */
export async function renewInvitation(_prev: RenewState, form: FormData): Promise<RenewState> {
  const { locale } = await getSession();
  const { data } = await authCall<{ sent: boolean }>("POST", "/api/auth/invitation/renew", { token: String(form.get("token") ?? ""), locale });
  return { sent: data?.sent ?? false };
}

export type ConfirmEmailState = { error?: "invalid_link" | "email_taken" | "unavailable"; email?: string };

export async function confirmEmail(_prev: ConfirmEmailState, form: FormData): Promise<ConfirmEmailState> {
  const { status, data } = await authCall<{ email?: string; code?: string }>("POST", "/api/auth/email/confirm", { token: String(form.get("token") ?? "") });
  if (status === 200) return { email: data?.email };
  if (status === 409) return { error: "email_taken" };
  if (status === 410) return { error: "invalid_link" };
  return { error: "unavailable" };
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
