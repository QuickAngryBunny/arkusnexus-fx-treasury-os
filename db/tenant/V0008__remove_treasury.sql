-- Phase 3 scope change: treasury, legal entities and departments are out of the product
-- (see docs/phase-3-brief.md). Every tenant holds dummy data only at this point, so nothing real is dropped.
-- Banks come back in Phase 3 as a plain name + currency list, without accounts or transactions.

drop table financial_transaction;
drop function financial_transaction_check_currency();
drop table bank_account;

alter table user_profile drop column department_id;
drop table department;
drop table legal_entity;
