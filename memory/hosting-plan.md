---
name: hosting-plan
description: "Local development now; hosting target is Firebase/Google Cloud, with Railway documented as an alternative — don't provision cloud resources"
metadata:
  type: project
  modified: 2026-11-17
---

Development runs locally (`./scripts/dev.sh`). On 2026-11-16 the hosting target changed from Azure to Firebase on Google
Cloud (App Hosting for the web app, Cloud Run for API/Migrator/workers, Cloud SQL, Cloud Storage, Secret Manager); the
repo has the Dockerfiles and `apps/web/apphosting.yaml` ready. On 2026-11-17 the README added step-by-step Railway
deployment (Postgres, api, workers, web) as an alternative: it runs as `ASPNETCORE_ENVIRONMENT=Internal` and sends
email through SMTP (`Email__Transport=smtp`, e.g. Gmail with an app password).

**Why:** the user wants to keep building locally and choose where to host later; a fresh install must start with no
tenants and no dummy data.

**How to apply:** don't provision cloud resources (projects, networks, buckets, deploy workflows) unless asked. Keep
the seams that hosting needs (`ISecretStore`, `Email:Transport`, raw store by URL or directory) and test against local
services. Locally, email goes through the real Firebase project with test addresses only; the app keeps its own
email-and-password sign-in (Firebase never authenticates users). No service-account key files (org policy). Related:
[[dev-stack-ports-belong-to-user]].
