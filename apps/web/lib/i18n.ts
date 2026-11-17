import esMX from "@/messages/es-MX.json";
import enUS from "@/messages/en-US.json";

/** Spanish and English from the first screen. Catalogs live in apps/web/messages/<locale>.json. */
export const LOCALES = ["es-MX", "en-US"] as const;
export type Locale = (typeof LOCALES)[number];
export const DEFAULT_LOCALE: Locale = "es-MX";
export const LOCALE_COOKIE = "fx_locale";

export type MessageKey = keyof typeof esMX;

// Both catalogs must have exactly the same keys; a missing or extra key fails the typecheck.
const catalogs: Record<Locale, Record<MessageKey, string>> = {
  "es-MX": esMX,
  "en-US": enUS satisfies Record<MessageKey, string>,
};
const _sameKeys: Record<keyof typeof enUS, string> = esMX;
void _sameKeys;

export function isMessageKey(value: string): value is MessageKey {
  return Object.prototype.hasOwnProperty.call(esMX, value);
}

export function isLocale(value: unknown): value is Locale {
  return typeof value === "string" && (LOCALES as readonly string[]).includes(value);
}

export type Translate = (key: MessageKey, vars?: Record<string, string | number>) => string;

/** Returns t(key, vars) for a locale. Placeholders look like {name}. */
export function translator(locale: Locale): Translate {
  const messages = catalogs[locale];
  return (key, vars) => {
    const text = messages[key];
    return vars ? text.replace(/\{(\w+)\}/g, (m, name: string) => (name in vars ? String(vars[name]) : m)) : text;
  };
}

export function formatDateTime(locale: Locale, iso: string, timeZone: string): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short", timeZone }).format(new Date(iso));
}

/**
 * The locale numbers are formatted with: the user's number format (U-01) when they chose one, else the language's.
 * Dates and words keep the language; only digit grouping and the decimal mark follow this.
 */
export type NumberLocale = string;
export type NumberFormatPref = "period_decimal" | "comma_decimal";

export function numberLocale(locale: Locale, format?: NumberFormatPref | null): NumberLocale {
  if (format === "comma_decimal") return "de-DE"; // 1.234,56
  if (format === "period_decimal") return "en-US"; // 1,234.56
  return locale;
}

export function formatNumber(locale: NumberLocale, value: number): string {
  return new Intl.NumberFormat(locale).format(value);
}

/** Currency values (rates, amounts, errors in currency) are shown with 2 decimals; stored values keep full precision. */
export const CURRENCY_DIGITS = 2;

export function formatRate(locale: NumberLocale, value: number, digits = CURRENCY_DIGITS): string {
  return new Intl.NumberFormat(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value);
}

export function formatSigned(locale: NumberLocale, value: number, digits: number, suffix = ""): string {
  const text = new Intl.NumberFormat(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits, signDisplay: "exceptZero" }).format(value);
  return text + suffix;
}

/** A calendar date (YYYY-MM-DD) shown as written, without shifting it through a time zone. */
export function formatDate(locale: Locale, isoDate: string, style: "medium" | "long" = "medium"): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: style, timeZone: "UTC" }).format(new Date(`${isoDate}T00:00:00Z`));
}

/** An amount always with its currency code (house rule), e.g. "USD 1,707,450.00". */
export function formatMoney(locale: NumberLocale, amount: number, currency: string, digits = CURRENCY_DIGITS): string {
  const n = new Intl.NumberFormat(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(amount);
  return `${currency} ${n}`;
}
