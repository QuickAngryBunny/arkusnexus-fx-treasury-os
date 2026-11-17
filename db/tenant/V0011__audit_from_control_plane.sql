-- The company's own copy of its sign-ins, invitations and role changes (Phase 3 brief, "Data to add"). They are written
-- to the control plane's control_audit_log in the same transaction as the change; ControlAuditMirror copies them here
-- afterwards. control_audit_id is that row's id: unique, so a copy is never made twice.
alter table audit_log add column control_audit_id bigint unique;
