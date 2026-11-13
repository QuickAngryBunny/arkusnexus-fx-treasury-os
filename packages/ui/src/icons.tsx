/** Stroke icon paths (24×24 viewBox) used by AppNav. Taken from the design canvas. */
export const navIcons = {
  dashboard: "M4 4h7v9H4zM13 4h7v5h-7zM13 11h7v9h-7zM4 15h7v5H4z",
  forecast: "M3 20h18M4 16l4-5 4 3 4-5M18 3l.9 2.1L21 6l-2.1.9L18 9l-.9-2.1L15 6l2.1-.9z",
} as const;

export type NavIcon = keyof typeof navIcons;

export function StrokeIcon({ d, size = 20 }: { d: string; size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={d} />
    </svg>
  );
}

/** The FX Intelligence mark: two overlapping circles with a plum lens. */
export function BrandMark({ label }: { label?: string }) {
  return (
    <svg width="40" height="30" viewBox="0 0 76 56" role={label ? "img" : undefined} aria-label={label} aria-hidden={label ? undefined : true}>
      <circle cx="28" cy="28" r="26" fill="#6C10E0" />
      <circle cx="48" cy="28" r="26" fill="#B06BEA" />
      <path d="M38 6.3 A26 26 0 0 1 38 49.7 A26 26 0 0 1 38 6.3 Z" fill="#3E1F55" />
    </svg>
  );
}
