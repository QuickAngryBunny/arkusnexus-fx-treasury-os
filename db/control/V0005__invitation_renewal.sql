-- An expired invitation can be renewed once (O-S1 / T-S1): the new link records the one it replaced, and a renewed
-- link can't be renewed again. Stops a never-cancelled invitation from being renewed forever.
alter table invitation add column renewed_from uuid references invitation (id);
