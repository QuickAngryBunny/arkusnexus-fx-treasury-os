"use server";

import { cookies } from "next/headers";
import { revalidatePath } from "next/cache";
import { TENANT_COOKIE, devSwitcherEnabled } from "@/lib/api";
import { LOCALE_COOKIE, isLocale } from "@/lib/i18n";

const SLUG = /^[a-z][a-z0-9_]{2,30}$/;
const ONE_YEAR = 60 * 60 * 24 * 365;

export async function switchTenant(formData: FormData): Promise<void> {
  if (!devSwitcherEnabled()) return;
  const slug = String(formData.get("tenant") ?? "");
  const store = await cookies();
  if (SLUG.test(slug)) {
    store.set(TENANT_COOKIE, slug, { httpOnly: true, sameSite: "lax", path: "/" });
  } else {
    store.delete(TENANT_COOKIE);
  }
  revalidatePath("/", "layout");
}

export async function setLocale(locale: string): Promise<void> {
  if (!isLocale(locale)) return;
  (await cookies()).set(LOCALE_COOKIE, locale, { httpOnly: true, sameSite: "lax", path: "/", maxAge: ONE_YEAR });
  revalidatePath("/", "layout");
}
