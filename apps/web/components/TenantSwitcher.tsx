"use client";

import { useRef } from "react";
import { switchTenant } from "@/app/actions";
import type { DevTenant } from "@/lib/api";

type Props = { tenants: DevTenant[]; current?: string; label: string; placeholder: string };

/** Developer-only stand-in for sign-in. Removed in Phase 3. */
export function TenantSwitcher({ tenants, current, label, placeholder }: Props) {
  const form = useRef<HTMLFormElement>(null);

  return (
    <form ref={form} action={switchTenant} className="switcher">
      <label htmlFor="tenant">{label}</label>
      <select
        id="tenant"
        name="tenant"
        defaultValue={current ?? ""}
        onChange={() => form.current?.requestSubmit()}
      >
        <option value="">{placeholder}</option>
        {tenants
          .filter((t) => t.status === "active")
          .map((t) => (
            <option key={t.slug} value={t.slug}>
              {t.displayName} ({t.slug})
            </option>
          ))}
      </select>
    </form>
  );
}
