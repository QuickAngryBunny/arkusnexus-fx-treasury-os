"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { apiPost } from "@/lib/api";

const ID = /^[0-9a-f-]{36}$/i;

function idOf(form: FormData): string {
  const id = String(form.get("id") ?? "");
  if (!ID.test(id)) throw new Error("Invalid tenant id.");
  return id;
}

export type CreateTenantState = { error?: string; name: string; email: string; realData: boolean };

/** A-02 → A-03: create, then watch setup on the tenant page. */
export async function createTenant(_prev: CreateTenantState, form: FormData): Promise<CreateTenantState> {
  const name = String(form.get("name") ?? "").trim();
  const email = String(form.get("email") ?? "").trim();
  const realData = form.get("realData") === "on";
  const result = await apiPost<{ id: string }>("/api/admin/tenants", { name, firstAdminEmail: email, realData });
  if (!result.ok) return { error: result.message, name, email, realData };
  revalidatePath("/admin");
  redirect(`/admin/tenants/${result.data.id}`);
}

/** Runs a portal action and comes back to the tenant page with a one-word outcome for the status line. */
async function act(form: FormData, path: string, body: unknown, done: string): Promise<never> {
  const id = idOf(form);
  const result = await apiPost(`/api/admin/tenants/${id}/${path}`, body);
  revalidatePath("/admin");
  redirect(`/admin/tenants/${id}?${result.ok ? `done=${done}` : `error=${encodeURIComponent(result.message)}`}`);
}

export async function retrySetup(form: FormData) {
  await act(form, "retry", {}, "retried");
}

export async function renameTenant(form: FormData) {
  await act(form, "rename", { name: String(form.get("name") ?? "") }, "renamed");
}

export async function archiveTenant(form: FormData) {
  await act(form, "archive", {}, "archived");
}

export async function restoreTenant(form: FormData) {
  await act(form, "restore", {}, "restored");
}

export async function requestDummyData(form: FormData) {
  const load = form.get("load") === "true";
  await act(form, "dummy-data", { load }, load ? "dummy_loading" : "dummy_clearing");
}

export type DeleteState = { error?: string };

/** A-S3: the API checks the typed name and refuses real-data tenants. */
export async function deleteTenant(_prev: DeleteState, form: FormData): Promise<DeleteState> {
  const id = idOf(form);
  const result = await apiPost(`/api/admin/tenants/${id}/delete`, { confirmName: String(form.get("confirmName") ?? "") });
  if (!result.ok) return { error: result.message };
  revalidatePath("/admin");
  redirect("/admin?done=deleted");
}

/** "Open in FX app" and the tenant menu: move the session to that company, then open its dashboard. */
export async function openCompany(form: FormData) {
  const id = idOf(form);
  const result = await apiPost("/api/auth/session/company", { tenantId: id });
  revalidatePath("/", "layout");
  redirect(result.ok ? "/" : "/admin?error=not_allowed");
}
