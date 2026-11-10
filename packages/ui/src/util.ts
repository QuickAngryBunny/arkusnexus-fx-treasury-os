import type { AnchorHTMLAttributes, ComponentType } from "react";

export function cx(...parts: Array<string | false | null | undefined>): string {
  return parts.filter(Boolean).join(" ");
}

/**
 * Components that render links accept `linkAs`, so an app can pass its router link (for example next/link)
 * instead of a plain <a>. It receives href, className, aria-current and children.
 */
export type LinkComponent = ComponentType<AnchorHTMLAttributes<HTMLAnchorElement> & { href: string }> | "a";

/** Up to two initials for an avatar: first and last word. */
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const first = parts[0]?.[0] ?? "";
  const last = parts.length > 1 ? (parts[parts.length - 1]?.[0] ?? "") : "";
  return (first + last).toUpperCase() || "?";
}
