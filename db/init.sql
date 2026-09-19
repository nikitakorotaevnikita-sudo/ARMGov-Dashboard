\getenv harness_test_password HARNESS_TEST_PASSWORD

create table public.harness_items
(
    id bigint generated always as identity primary key,
    name text not null
);

insert into public.harness_items(name)
select 'fixture-' || n::text
from generate_series(1, 250) as n;

-- Полностью вымышленные RX-подобные фикстуры. Большие ID намеренно не совпадают
-- с идентификаторами сотрудников какого-либо стенда.
create table public.sungero_core_recipient
(
    id bigint primary key,
    name text,
    department_company_sungero bigint,
    status text
);

create table public.sungero_wf_task
(
    id bigint primary key,
    maintask bigint not null default 0,
    discriminator uuid not null,
    created timestamptz not null
);

create table public.sungero_wf_assignment
(
    id bigint primary key,
    performer bigint,
    task bigint,
    discriminator uuid
);

insert into public.sungero_core_recipient
    (id, name, department_company_sungero, status)
values
    (900000001, 'Иванов Алексей Тестович', 900000100, 'Active'),
    (900000002, 'Иванов Борис Тестович', 900000100, 'Closed'),
    (900000003, 'Босов Виктор Тестович', 900000100, 'Active'),
    (900000100, 'Ивановский испытательный отдел', null, 'Active');

insert into public.sungero_wf_assignment(id, performer, task, discriminator)
values
    (910000001, 900000001, null, null),
    (910000002, 900000002, null, null),
    (910000003, 900000003, null, null);

-- Root dedup fixture: one poruchenie (root), two assignments to the same performer → count=1.
insert into public.sungero_wf_task(id, maintask, discriminator, created)
values (920000001, 0, 'c290b098-12c7-487d-bb38-73e2c98f9789', '2026-06-15T12:00:00+03:00');

insert into public.sungero_wf_assignment(id, performer, task, discriminator)
values
    (910010001, 900000001, 920000001, '11111111-1111-1111-1111-111111111101'),
    (910010002, 900000001, 920000001, '11111111-1111-1111-1111-111111111102');

insert into public.sungero_core_recipient
    (id, name, department_company_sungero, status)
select
    900001000 + n,
    'Переполнение Кандидат ' || n::text,
    900000100,
    'Active'
from generate_series(1, 21) as n;

insert into public.sungero_wf_assignment(id, performer, task, discriminator)
select 910001000 + n, 900001000 + n, null, null
from generate_series(1, 21) as n;

revoke all on schema public from public;
revoke all on all tables in schema public from public;

create role harness_reader login password :'harness_test_password';
grant connect on database harness_test to harness_reader;
grant usage on schema public to harness_reader;
grant select on public.harness_items,
    public.sungero_core_recipient,
    public.sungero_wf_assignment,
    public.sungero_wf_task
to harness_reader;
