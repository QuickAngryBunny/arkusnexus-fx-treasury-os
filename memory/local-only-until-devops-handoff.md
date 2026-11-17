---
name: local-only-until-devops-handoff
description: "Project runs locally through the build plan; Azure hosting is handed to the DevOps developer at the end — don't plan infra work"
metadata:
  node_type: memory
  type: project
  originSessionId: 73487db6-f2aa-4705-b8c0-42a62df5f5a8
  modified: 2026-11-13T19:57:36.616Z
---

As of 2026-11-13 the user keeps FX Intelligence running locally, probably until the last phase of the build plan, then hands Azure deployment to the company's DevOps developer. Don't plan or build Azure infrastructure (VNet, private endpoints, Key Vault provisioning, Bicep, deploy workflows) in Phase 3 sprints. The Phase 3 brief's "real-data gate" infra items move to that hand-off.

**Why:** the user said they shouldn't worry about hosting; DevOps owns it.

**How to apply:** keep seams DevOps can fill (e.g. `ISecretStore` so Key Vault can be added later), but implement and test against local services. What the user does want working locally is the Firebase email sending (Trigger Email extension). Confirmed 2026-11-13: the app keeps its own sign-in (Firebase is never the identity provider; "authentication with Firebase" meant the API's service account), and local runs send through the REAL Firebase project (not emulators), so use test addresses only and keep the service-account key outside the repo. Related: [[dev-stack-ports-belong-to-user]].
