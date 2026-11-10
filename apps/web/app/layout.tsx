import type { Metadata } from "next";
import { Manrope } from "next/font/google";
import "@fxos/ui/styles.css";
import "./globals.css";
import { getSession } from "@/lib/session";

const manrope = Manrope({ subsets: ["latin"], variable: "--font-sans", display: "swap" });

export const metadata: Metadata = {
  title: "FX Intelligence & Treasury OS",
  description: "Internal build · Phase 0",
  robots: { index: false, follow: false },
};

export default async function RootLayout({ children }: { children: React.ReactNode }) {
  const { locale } = await getSession();
  return (
    <html lang={locale.slice(0, 2)} className={manrope.variable}>
      <body>{children}</body>
    </html>
  );
}
