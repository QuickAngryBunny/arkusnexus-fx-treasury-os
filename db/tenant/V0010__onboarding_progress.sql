-- Phase 3 Sprint 4: onboarding (O-02 to O-05). Step 2 (organization) is confirmed separately from step 3 (banks), so a
-- first admin who leaves after confirming the company resumes at step 3.
alter table tenant_settings add column company_confirmed_at timestamptz;

-- Onboarding can only complete after the organization step.
alter table tenant_settings add constraint tenant_settings_onboarding_after_company
    check (onboarding_completed_at is null or company_confirmed_at is not null);
