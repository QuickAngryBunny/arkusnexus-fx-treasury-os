"use server";

import { redirect } from "next/navigation";
import { apiPost, currentTenant } from "@/lib/api";

const SOURCE = "banxico_fix";
const DATE = /^\d{4}-\d{2}-\d{2}$/;

/** Return to the page the action came from, with a one-word outcome the page turns into a status message. */
function back(formData: FormData, outcome: string): never {
  const raw = String(formData.get("back") ?? "/forecast/history");
  const url = new URL(raw.startsWith("/forecast/history") ? raw : "/forecast/history", "http://x");
  url.searchParams.set("done", outcome);
  redirect(`${url.pathname}${url.search}`);
}

export async function markHoliday(formData: FormData): Promise<void> {
  const tenant = await currentTenant();
  const date = String(formData.get("date") ?? "");
  if (!tenant || !DATE.test(date)) back(formData, "error");
  const result = await apiPost(`/api/tenant/rates/${SOURCE}/gaps/${date}/holiday`, tenant!, {});
  back(formData, result.ok ? `holiday:${date}` : "error");
}

export async function requestSync(formData: FormData): Promise<void> {
  const tenant = await currentTenant();
  const from = String(formData.get("from") ?? "");
  const to = String(formData.get("to") ?? "");
  if (!tenant) back(formData, "error");
  const body = DATE.test(from) && DATE.test(to) ? { from, to } : {};
  const result = await apiPost(`/api/tenant/rates/${SOURCE}/sync-requests`, tenant!, body);
  back(formData, result.ok ? (DATE.test(from) ? `refetch:${from}` : "sync") : "error");
}
