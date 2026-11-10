import path from "node:path";
import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  // packages/ui ships TypeScript source; Next compiles it with the app.
  transpilePackages: ["@fxos/ui"],
  // The monorepo root; stops Next guessing from unrelated lockfiles higher up the disk.
  outputFileTracingRoot: path.join(__dirname, "..", ".."),
};

export default nextConfig;
