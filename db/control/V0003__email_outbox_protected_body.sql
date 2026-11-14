-- Email bodies carry raw invitation, reset and confirmation links. The rule is "store only token hashes, never raw
-- tokens", so the outbox keeps the body encrypted (ASP.NET Data Protection) and clears it once the email is handed to
-- Firebase or given up. The outbox has not been used yet, so the plain-text columns are dropped.

alter table email_outbox drop column html;
alter table email_outbox drop column text_body;

-- Data Protection payload of {"html": ..., "text": ...}. Null once sent or finally failed.
alter table email_outbox add column body_protected text;

alter table email_outbox add constraint email_outbox_body_while_pending
    check (status not in ('pending', 'sending') or body_protected is not null);
