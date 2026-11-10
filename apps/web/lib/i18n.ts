/** Spanish and English from the first screen. Sprint 2 moves this to a proper message catalog. */
export type Locale = "es-MX" | "en-US";

const messages = {
  "es-MX": {
    product: "FX Intelligence",
    devSwitcher: "Tenant de desarrollo",
    chooseTenant: "Elige un tenant",
    noTenants: "No hay tenants. Crea uno con el Migrator: provision <slug> \"<nombre>\".",
    apiDown: "No se pudo contactar la API",
    overview: "Resumen del tenant",
    baseCurrency: "Moneda base",
    timeZone: "Zona horaria",
    legalEntities: "Entidades legales",
    departments: "Departamentos",
    users: "Usuarios",
    recentActivity: "Actividad reciente",
    dummyNotice: "Datos ficticios · entorno interno",
    signInLater: "El inicio de sesión llega en la Fase 3.",
  },
  "en-US": {
    product: "FX Intelligence",
    devSwitcher: "Developer tenant",
    chooseTenant: "Choose a tenant",
    noTenants: "No tenants yet. Create one with the Migrator: provision <slug> \"<name>\".",
    apiDown: "Could not reach the API",
    overview: "Tenant overview",
    baseCurrency: "Base currency",
    timeZone: "Time zone",
    legalEntities: "Legal entities",
    departments: "Departments",
    users: "Users",
    recentActivity: "Recent activity",
    dummyNotice: "Dummy data · internal environment",
    signInLater: "Sign-in arrives in Phase 3.",
  },
} as const;

export type MessageKey = keyof (typeof messages)["es-MX"];

export function t(locale: Locale, key: MessageKey): string {
  return messages[locale][key];
}

export function formatDateTime(locale: Locale, iso: string, timeZone: string): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short", timeZone }).format(new Date(iso));
}
