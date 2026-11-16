import type { MessageKey } from "@/lib/i18n";

/** Time zones offered in onboarding (O-03) and the profile (U-01): Mexico's zones plus the main US ones. */
export const TIME_ZONES: { id: string; label: MessageKey }[] = [
  { id: "America/Tijuana", label: "tz.tijuana" },
  { id: "America/Hermosillo", label: "tz.hermosillo" },
  { id: "America/Mazatlan", label: "tz.mazatlan" },
  { id: "America/Mexico_City", label: "tz.mexicoCity" },
  { id: "America/Monterrey", label: "tz.monterrey" },
  { id: "America/Cancun", label: "tz.cancun" },
  { id: "America/Los_Angeles", label: "tz.losAngeles" },
  { id: "America/Chicago", label: "tz.chicago" },
  { id: "America/New_York", label: "tz.newYork" },
];
