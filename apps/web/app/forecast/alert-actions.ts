"use server";

import { revalidatePath } from "next/cache";
import { apiDelete, apiPost, apiPut } from "@/lib/api";
import { parseRate } from "@/lib/alerts";
import { getSession } from "@/lib/session";

export type AlertFormState = {
  ok?: boolean;
  /** Bumps on every successful save so the drawer can go back to the list. */
  savedAt?: number;
  error?: string;
  rateError?: boolean;
};

const str = (form: FormData, name: string) => String(form.get(name) ?? "").trim();

/** Create (no id) or edit an alert from the drawer form (P2-04). Editing also turns an alert back on. */
export async function saveAlert(_prev: AlertFormState, form: FormData): Promise<AlertFormState> {
  const { t } = await getSession();
  const rate = parseRate(str(form, "rate"));
  if (rate === null) return { error: t("alert.rateRequired"), rateError: true };
  const body = {
    direction: str(form, "direction") === "below" ? "below" : "above",
    rate: Math.round(rate * 10_000) / 10_000,
    earlyWarning: str(form, "earlyWarning") === "on",
    lookAheadDays: Number(str(form, "lookAheadDays")) || 7,
    keepOn: str(form, "keepOn") === "on",
    isOn: true,
  };
  const id = str(form, "id");
  const result = id ? await apiPut(`/api/tenant/alerts/${encodeURIComponent(id)}`, body) : await apiPost("/api/tenant/alerts", body);
  if (!result.ok) {
    return result.status === 409 ? { error: t("alert.tooMany") } : { error: result.message, rateError: result.status === 400 };
  }
  revalidatePath("/forecast");
  return { ok: true, savedAt: Date.now() };
}

/** Delete from the list (after the inline confirmation). */
export async function deleteAlert(id: number): Promise<{ ok: boolean; error?: string }> {
  const result = await apiDelete(`/api/tenant/alerts/${encodeURIComponent(String(id))}`);
  revalidatePath("/forecast");
  return result.ok ? { ok: true } : { ok: false, error: result.message };
}

/** "Turn back on" in the list: same settings, started afresh. */
export async function turnAlertOn(alert: {
  id: number; direction: string; rate: number; earlyWarning: boolean; lookAheadDays: number; keepOn: boolean;
}): Promise<{ ok: boolean; error?: string }> {
  const { id, ...settings } = alert;
  const result = await apiPut(`/api/tenant/alerts/${encodeURIComponent(String(id))}`, { ...settings, isOn: true });
  revalidatePath("/forecast");
  return result.ok ? { ok: true } : { ok: false, error: result.message };
}
