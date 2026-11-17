---
name: dev-stack-ports-belong-to-user
description: "Processes on ports 3000/5080 are usually the user's own ./scripts/dev.sh — don't kill them"
metadata:
  node_type: memory
  type: feedback
  originSessionId: c40dd45e-87ee-48e9-a024-f32a0f01fac6
  modified: 2026-11-10T20:46:20.372Z
---

The user runs `./scripts/dev.sh` (API on 5080, Next web on 3000). Killing its web process fires the script's exit trap, which also stops the API.

**Why:** On 2026-11-10 I killed a next-server on 3000 assuming it was my leftover preview; it was dev.sh's, and the API went down with it.

**How to apply:** Before killing anything on 3000/5080, check its parent (`ps -o ppid`) and whether a preview I started actually owns it. If the user's stack is up, reuse it for browser checks; for `next build`, ask first or build in a way that doesn't touch the running dev server's `.next`.
