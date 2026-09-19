\getenv harness_test_password HARNESS_TEST_PASSWORD

create table public.harness_items
(
    id bigint generated always as identity primary key,
    name text not null
);

insert into public.harness_items(name)
select 'fixture-' || n::text
from generate_series(1, 250) as n;

revoke all on schema public from public;
revoke all on all tables in schema public from public;

create role harness_reader login password :'harness_test_password';
grant connect on database harness_test to harness_reader;
grant usage on schema public to harness_reader;
grant select on public.harness_items to harness_reader;
