"use server";

import { cookies } from "next/headers";
import { revalidatePath } from "next/cache";
import { LOCALE_COOKIE, isLocale } from "@/lib/i18n";

const ONE_YEAR = 60 * 60 * 24 * 365;

export async function setLocale(locale: string): Promise<void> {
  if (!isLocale(locale)) return;
  (await cookies()).set(LOCALE_COOKIE, locale, { httpOnly: true, sameSite: "lax", path: "/", maxAge: ONE_YEAR });
  revalidatePath("/", "layout");
}
