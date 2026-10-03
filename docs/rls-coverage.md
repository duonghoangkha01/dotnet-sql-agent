# Row-level security coverage

Which AdventureWorks2022 tables carry territory, salesperson, customer or order data, and how each one is
protected for the `sales_rep` role. Finance and admin see everything by design.

The inventory came from a query over `sys.columns` for the key columns `TerritoryID`, `SalesPersonID`,
`CustomerID`, `SalesOrderID` and `StoreID` on every base table, plus a second query for tables holding
pre-aggregated sales, cost or quota columns. It was run on 2026-09-26 against the restored database.

Why this document exists: territory data is not only in the obvious tables. `Sales.SalesTerritory` and
`Sales.SalesPerson` store per-territory and per-person revenue totals, so a question like "total sales last
year by territory" can be answered from them without touching any row-level-secured table.

## Classification

| Table | Why it matters | Protection for `sales_rep` | Enforced by |
|---|---|---|---|
| `Sales.SalesOrderHeader` | Orders, with `TerritoryID` | Only rows of the session's territory | RLS filter predicate `fn_can_see_territory` |
| `Sales.Customer` | Customers, with `TerritoryID` | Only rows of the session's territory | RLS filter predicate `fn_can_see_territory` |
| `Sales.SalesOrderDetail` | Order lines (`SalesOrderID` only): company-wide `SUM(LineTotal)` otherwise | Only lines of orders in the session's territory | RLS filter predicate `fn_can_see_order` |
| `Sales.SalesOrderHeaderSalesReason` | Order-to-reason link (`SalesOrderID` only) | Only rows of orders in the session's territory | RLS filter predicate `fn_can_see_order` |
| `Sales.Store` | Stores reach a territory through their salesperson | Only stores whose salesperson is in the session's territory. Stores with no salesperson are hidden | RLS filter predicate `fn_can_see_salesperson_territory` |
| `Sales.SalesTerritory` | Holds `SalesYTD`, `SalesLastYear`, `CostYTD`, `CostLastYear` for every territory | Identifying columns only: `TerritoryID`, `Name`, `CountryRegionCode`, `Group` | Column-level DENY (DB), and the guardrail allowlist |
| `Sales.SalesPerson` | Holds `SalesYTD`, `SalesLastYear`, `Bonus`, `SalesQuota`, `CommissionPct` | The non-monetary columns only (`BusinessEntityID`, `TerritoryID`, ...); no row filter, so a rep can see which salesperson sits in which territory | Column-level DENY (DB), and the guardrail allowlist |
| `Sales.SalesTerritoryHistory` | Salesperson-to-territory history | Excluded | No GRANT (DB), and the guardrail allowlist |
| `Sales.SalesPersonQuotaHistory` | Quotas per salesperson | Excluded | No GRANT (DB), and the guardrail allowlist |
| `Purchasing.PurchaseOrderHeader`, `Purchasing.PurchaseOrderDetail` | Company-wide purchasing totals (not territory data) | Excluded: financial data outside a sales rep's remit | No GRANT (DB), and the guardrail allowlist |
| `Person.StateProvince` | Maps states to territories (reference data) | Readable, not restricted. Reveals no revenue | In `sales_rep`'s tables in `semantic.yaml` |
| `Person.Person` | Names of every person: customers of all territories, salespeople and employees. Not territory-keyed, so **no row filter** | Readable for names only: `Demographics` and `AdditionalContactInfo` are denied. **Knowingly open:** a rep can list the names of people outside their territory | Column-level DENY (DB), and the guardrail allowlist |

Not territory data, and therefore outside this inventory: `Production.*`, `HumanResources.*` and the rest
of `Person.*`. Their restrictions are role-based and are defined with the allowlist in `semantic.yaml`;
`sales_rep` gets no `HumanResources.*` table, and no role can read `Person.Password` or the columns in
`global_deny_columns` (`Sales.CreditCard.CardNumber`, `HumanResources.Employee.NationalIDNumber`).

## Who bypasses the policy

The policy exempts the database users `dbo`, `sqlagent_finance` and `sqlagent_admin`. The exemption is decided
by `USER_NAME()`, never by a value in `SESSION_CONTEXT`, so a query cannot grant itself a role. `dbo` is
exempt so that the bootstrap scripts and `sa` keep working; the API never connects as `dbo`.

For `sqlagent_sales_rep` the policy compares each row's territory with `SESSION_CONTEXT('territory_id')`.
With no territory set the comparison is NULL and the user sees no rows. The API sets the territory
read-only immediately after opening the connection, so later statements cannot change it.

## Ownership chaining

The predicate functions read `Sales.SalesOrderHeader` and `Sales.SalesPerson`. Those reads work whatever
the rep's own column grants are (a rep has only some `Sales.SalesPerson` columns): the functions and the
tables share an owner (`dbo`), so permission checks inside the function are skipped. The same mechanism means a view or
table-valued function owned by `dbo` can expose a column that is column-level denied on its base table.
The allowlist therefore contains base tables only, and the guardrail rejects views and functions.

## Views

No view is granted to any role, and the allowlist contains base tables only. Views that carry territory or
salesperson keys exist in the database (for example `Sales.vSalesPerson`,
`Sales.vSalesPersonSalesByFiscalYears`, `Sales.vStoreWithDemographics`, `Sales.vIndividualCustomer`,
`Person.vStateProvinceCountryRegion`), so keeping them ungranted is a security control, not an omission.
`scripts/verify-infra.sh` checks the denial for `Sales.vSalesPerson` as the representative.

## Known residual exposure

- Any login that can connect reaches `master`, `tempdb` and `msdb` through the `guest` user. As a sales rep
  we could read `msdb.dbo.backupset`, which includes the host user name and machine name, and
  `sys.partitions` row counts (unfiltered, so they show whole-table sizes). Nothing in AdventureWorks
  business data is reachable this way. The barrier is the guardrail, which allows only two-part names
  from the allowlist and no cross-database references; its adversarial corpus holds `msdb.dbo.backupset`,
  `master.sys.databases` and `sys.partitions`. Revoking `guest` in `msdb` was not done: Microsoft
  documents that some features rely on it.
- `MAXDOP = 1` is only the database default. `OPTION (MAXDOP n)` overrides it, and the guardrail rejects
  every `OPTION` clause. `deploy/sql/45-resource-governor.sql` makes the cap enforceable in the database: the
  three reader users are classified (by `ORIGINAL_LOGIN()`) into a workload group with `MAX_DOP = 1`, which
  wins over a hint, a 10 s CPU-time limit (enforced through trace flag 2422, which the script re-enables on
  every boot) and a 10% memory-grant cap. Tests: `ResourceGovernorTests`.
- `sp_set_session_context` is executable by any user. Territory scoping is safe only because the API sets the
  territory read-only right after opening the connection and the guardrail rejects `EXEC`.
- Filter predicates are not a defence against side channels. Microsoft documents that a crafted `WHERE`
  (for example one that divides by zero only for certain hidden values) can reveal whether rows exist that
  the policy filters out, through error messages or timing. The agent writes arbitrary `WHERE` clauses, so
  this is reachable by a prompt-injected question. The barrier is again the guardrail, plus the evaluation
  suite; the database cannot close it.
- Denied columns are listed by name. A column added to a table later is covered by that table's `GRANT` (and shown
  by the catalog) until it is added to `deny_columns`, except for pattern entries such as `Person.*: ["Password*"]`,
  which are re-expanded on every boot. AdventureWorks is static; a schema that changes should list sensitive tables'
  denied columns by pattern, or move to column-level `GRANT` lists.
- Text in `semantic.yaml` (table descriptions, join hints) is shown to the agent as written. Only its author keeps
  it free of denied column names; nothing checks it.
- Through `guest` a reader can create temporary tables (`#t`) in `tempdb`, but not permanent ones:
  `CREATE TABLE tempdb.dbo.x` is denied (checked by `scripts/verify-infra.sh`), so nothing written there can
  outlive the session or pass between territories.

## Tests

`scripts/verify-infra.sh` checks, against the running database, and computing expected counts as `dbo`
(which the policy exempts), so nothing is hard-coded:

- every row-level-secured table above (rows for territory 1, none without a territory, other territory,
  finance and admin see everything, the territory cannot be changed once set),
- `SalesTerritory` and `SalesPerson` column restrictions, and denial of `SalesTerritoryHistory`,
  `SalesPersonQuotaHistory`, `Purchasing.*`, `HumanResources.Employee`, `HumanResources.EmployeePayHistory`,
  `Person.Password` and `Sales.vSalesPerson` for the sales rep; the global denied columns for admin,
- INSERT, UPDATE, DELETE (each inside a rolled-back transaction), `xp_cmdshell`, `SELECT ... INTO`, permanent
  tables in `tempdb`, `EXECUTE AS`, and reading the `SqlAgent` database, all denied for the three reader users,
- `sqlagent_app`: can append to and read the audit log, cannot update or delete it, cannot read business data,
  can read schema metadata,
- with `--restart-check`: permission drift added by hand (role membership, schema grant) is removed by the next boot.

`tests/SqlAgent.IntegrationTests` repeats the row-level-security checks through the real executor, on a throwaway
container that replays `deploy/sql` (the same scripts the stack boots with), and runs in CI:

- one test per row-level-secured table above: territory 1 sees a non-empty strict subset, finance and admin see all,
- a rep with no territory sees nothing, and user SQL cannot change the territory the executor set (read-only context),
- 20 parallel queries alternating between two territories over pooled connections, four rounds, each seeing only its own,
- the column and table denials, sent straight to the database with the guardrail bypassed,
- the guardrail and the server read names the same way: SQL Server resolves a padded identifier such as
  `[SalesYTD ]` to the column, so the guardrail compares names without trailing spaces (checked on both sides).

Finance and admin are checked on every row-level-secured table, not just the order header, and every
territory-1 expectation must be a non-empty strict subset of the table, so a comparison of two empty or equal
results cannot pass.

`Person.StateProvince` is reference data and is not tested. During development the policy was switched off
by hand once to confirm these checks fail without it; that mutation is not part of the script.
