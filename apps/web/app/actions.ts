"use server";

import { cookies } from "next/headers";
import { revalidatePath } from "next/cache";
import { TENANT_COOKIE, devSwitcherEnabled } from "@/lib/api";

const SLUG = /^[a-z][a-z0-9_]{2,30}$/;

export async function switchTenant(formData: FormData): Promise<void> {
  if (!devSwitcherEnabled()) return;
  const slug = String(formData.get("tenant") ?? "");
  const store = await cookies();
  if (SLUG.test(slug)) {
    store.set(TENANT_COOKIE, slug, { httpOnly: true, sameSite: "lax", path: "/" });
  } else {
    store.delete(TENANT_COOKIE);
  }
  revalidatePath("/");
}
