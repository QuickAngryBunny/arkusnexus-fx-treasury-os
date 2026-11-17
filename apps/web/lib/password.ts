/**
 * Live password checks for the forms. Mirrors FxOs.Platform/Identity/PasswordPolicy.cs, which stays the authority:
 * the API checks again and answers with the same rule codes.
 */
export const PASSWORD_MIN = 12;
export const PASSWORD_MAX = 128;

export type PasswordRuleCode = "min_length" | "number" | "symbol" | "personal_info" | "max_length";

/** The rules shown in the checklist, in order. max_length shows only when broken. */
export const SHOWN_RULES: PasswordRuleCode[] = ["min_length", "number", "symbol", "personal_info"];

const segmenter = typeof Intl !== "undefined" && "Segmenter" in Intl ? new Intl.Segmenter(undefined, { granularity: "grapheme" }) : null;

/** Characters as a person sees them (an emoji or an accented letter is one). */
function visibleLength(text: string): number {
  return segmenter ? Array.from(segmenter.segment(text)).length : Array.from(text).length;
}

/** Lower case without accents, so "Peña" and "pena" match. */
function fold(value: string): string {
  return value.normalize("NFD").replace(/\p{Mn}/gu, "").toLowerCase();
}

function personalParts(email?: string, name?: string): string[] {
  const parts: string[] = [];
  if (email) {
    const local = email.split("@")[0] ?? "";
    parts.push(local, ...local.split(/[._+-]/));
  }
  if (name) parts.push(...name.split(/\s+/));
  return parts.map(fold).filter((p) => p.length >= 3);
}

/** Rule codes the password breaks; empty when acceptable. */
export function brokenRules(password: string, email?: string, name?: string): PasswordRuleCode[] {
  const text = password.normalize("NFC");
  const length = visibleLength(text);
  const broken: PasswordRuleCode[] = [];
  if (length < PASSWORD_MIN) broken.push("min_length");
  if (length > PASSWORD_MAX) broken.push("max_length");
  if (!/\p{Nd}/u.test(text)) broken.push("number");
  // Anything that isn't a letter, digit, space or control character counts as a symbol.
  if (!/[^\p{L}\p{Nd}\p{Zs}\s\p{Cc}]/u.test(text)) broken.push("symbol");
  const folded = fold(text);
  if (personalParts(email, name).some((p) => folded.includes(p))) broken.push("personal_info");
  return broken;
}
