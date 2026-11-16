"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { apiPost, apiPut, type Bank } from "@/lib/api";
import type { PasswordRuleCode } from "@/lib/password";
import { getSession } from "@/lib/session";

export type FormState = { ok?: boolean; error?: string; rules?: PasswordRuleCode[]; emailSent?: string };

const str = (form: FormData, name: string) => String(form.get(name) ?? "").trim();

function companyBody(form: FormData) {
  return {
    displayName: str(form, "displayName"),
    legalName: str(form, "legalName") || null,
    country: str(form, "country"),
    timeZone: str(form, "timeZone"),
    conversionDirection: str(form, "conversionDirection"),
    locale: str(form, "locale"),
    baseCurrency: str(form, "baseCurrency") || "USD",
    reportingCurrency: str(form, "reportingCurrency") || "MXN",
  };
}

/** O-03 → O-04. */
export async function saveOrganization(_prev: FormState, form: FormData): Promise<FormState> {
  const result = await apiPut("/api/tenant/company", { ...companyBody(form), confirmOnboardingStep: true });
  if (!result.ok) return { error: result.message };
  redirect("/setup/banks");
}

/** Banks as JSON from the bank editor (O-04, C-01). */
function banksFrom(form: FormData): Pick<Bank, "name" | "currency">[] & { id?: string }[] {
  try {
    return JSON.parse(String(form.get("banks") ?? "[]"));
  } catch {
    return [];
  }
}

/** O-04 → O-05: save the banks, then finish onboarding (O-S2 when a currency is missing). */
export async function finishOnboarding(_prev: FormState, form: FormData): Promise<FormState> {
  const { locale } = await getSession();
  const saved = await apiPut("/api/tenant/banks", { banks: banksFrom(form) });
  if (!saved.ok) return { error: saved.message };
  const done = await apiPost("/api/tenant/onboarding/complete", { locale });
  if (!done.ok) return { error: done.message };
  revalidatePath("/", "layout");
  redirect("/?welcome=1");
}

/** C-01: company info and banks together. */
export async function saveCompany(_prev: FormState, form: FormData): Promise<FormState> {
  const info = await apiPut("/api/tenant/company", { ...companyBody(form), confirmOnboardingStep: false });
  if (!info.ok) return { error: info.message };
  const banks = await apiPut("/api/tenant/banks", { banks: banksFrom(form) });
  if (!banks.ok) return { error: banks.message };
  revalidatePath("/", "layout");
  return { ok: true };
}

/** U-01: profile and preferences; a new email only gets a confirmation link. */
export async function saveProfile(_prev: FormState, form: FormData): Promise<FormState> {
  const { locale } = await getSession();
  const language = str(form, "language");
  const result = await apiPut("/api/tenant/me", {
    displayName: str(form, "displayName"), language, numberFormat: str(form, "numberFormat"), timeZone: str(form, "timeZone"),
  });
  if (!result.ok) return { error: result.message };

  const email = str(form, "email").toLowerCase();
  if (email && email !== str(form, "currentEmail").toLowerCase()) {
    const change = await apiPost("/api/auth/email/change", { email, locale });
    if (!change.ok) return { error: change.message };
    revalidatePath("/", "layout");
    return { ok: true, emailSent: email };
  }

  revalidatePath("/", "layout");
  return { ok: true };
}

/** U-02. */
export async function changePassword(_prev: FormState, form: FormData): Promise<FormState> {
  const { locale } = await getSession();
  if (String(form.get("password") ?? "") !== String(form.get("confirm") ?? "")) return { error: "mismatch" };
  const result = await apiPost<{ code?: string; rules?: PasswordRuleCode[] }>("/api/auth/password/change", {
    current: String(form.get("current") ?? ""), password: String(form.get("password") ?? ""), locale,
  });
  return result.ok ? { ok: true } : { error: result.message };
}

/** U-03. */
export async function saveNotifications(_prev: FormState, form: FormData): Promise<FormState> {
  const codes = String(form.get("codes") ?? "").split(",").filter(Boolean);
  const items = codes.map((code) => ({ code, enabled: form.get(`n_${code}`) === "on" }));
  const result = await apiPut("/api/tenant/me/notifications", { items });
  return result.ok ? { ok: true } : { error: result.message };
}

// ── Team (T-01 to T-03) ─────────────────────────────────────

export type InviteState = FormState & { sentTo?: string; alreadyMember?: boolean; values?: { displayName: string; email: string; role: string; note: string } };

export async function inviteTeammate(_prev: InviteState, form: FormData): Promise<InviteState> {
  const { locale } = await getSession();
  const values = { displayName: str(form, "displayName"), email: str(form, "email"), role: str(form, "role") || "standard", note: str(form, "note") };
  const result = await apiPost("/api/tenant/team/invitations", { ...values, locale });
  if (!result.ok) return { error: result.message, alreadyMember: result.status === 409, values };
  revalidatePath("/settings/team");
  return { ok: true, sentTo: values.email };
}

async function teamAction(path: string, body: unknown, done: string): Promise<never> {
  const result = await apiPost(path, body);
  revalidatePath("/settings/team");
  redirect(`/settings/team?${result.ok ? `done=${done}` : `error=${encodeURIComponent(result.message)}`}`);
}

const ID = /^[0-9a-f-]{36}$/i;
const idOf = (form: FormData, name = "id") => {
  const id = str(form, name);
  if (!ID.test(id)) throw new Error("Invalid id.");
  return id;
};

export async function resendInvitation(form: FormData) {
  const { locale } = await getSession();
  await teamAction(`/api/tenant/team/invitations/${idOf(form)}/resend`, { locale }, "resent");
}

export async function cancelInvitation(form: FormData) {
  await teamAction(`/api/tenant/team/invitations/${idOf(form)}/cancel`, {}, "cancelled");
}

export async function changeRole(form: FormData) {
  const { locale } = await getSession();
  await teamAction(`/api/tenant/team/members/${idOf(form)}/role`, { role: str(form, "role"), locale }, "role");
}

export async function deactivateMember(form: FormData) {
  const { locale } = await getSession();
  await teamAction(`/api/tenant/team/members/${idOf(form)}/deactivate`, { locale }, "deactivated");
}

export async function reactivateMember(form: FormData) {
  await teamAction(`/api/tenant/team/members/${idOf(form)}/reactivate`, {}, "reactivated");
}

export async function sendReset(form: FormData) {
  const { locale } = await getSession();
  await teamAction(`/api/tenant/team/members/${idOf(form)}/password-reset`, { locale }, "reset");
}
