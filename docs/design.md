> Historical milestones below. Current SDK commands and permission profile are documented in [sdk-commands.md](sdk-commands.md); earlier read-only restrictions have been superseded by the explicitly authorized SDK phase.

# Technical design — first read-only milestone

McMurry Software owns this independent project. Its boundary is a deterministic CLI: **Keep QuickBooks. Make it scriptable.**

## Smallest connection proof

On a Windows PC with QuickBooks installed, create the official request processor on the executable's STA main thread, open a local connection, set read-only authorization preferences, begin one company session, send CompanyQuery and/or AccountQuery, normalize results, then end the session and close/release COM resources. Administrator approval occurs in QuickBooks on initial company access. Diagnostic probing only activates COM and sets process-local preferences; it opens no connection.

## Structure

| Project | Responsibility |
| --- | --- |
| `src/QbCli` | Parsing, validation of command options, composition, JSON/table output, exit codes |
| `src/QbCli.Core` | Company/account DTOs, `IBooksReader`, `ReadService` |
| `src/QbCli.QuickBooks` | Documented COM interface projections, session lifecycle, allowlisted queries, XML normalization |
| `tests/QbCli.Tests` | Domain, CLI contract, adapter parsing and lifecycle tests without QuickBooks |

One `IBooksReader` is sufficient for two reads. Separate per-entity interfaces can be added when implementations justify them. `IRequestProcessor` is an adapter test seam, not an application-service API. Its XML values never escape to consumers. There is no arbitrary QBXML CLI command.

## Decisions

- .NET 10; publish a self-contained Windows x64 executable first. Retain x86 publishing as an unvalidated legacy target.
- Use the installed QBXML request processor directly. QBFC is an alternative wrapper over the same messaging path, but adds a redistributable dependency with no benefit for two queries.
- Bind a narrow explicit IDispatch projection of SDK `IRequestProcessor4` and `IAuthPreferences`. Late-bound access failed locally for authorization preferences; explicit interface binding passed.
- All COM work, including cleanup, remains synchronous on the STA main thread. No async continuations or concurrent requests. A future hosted service must marshal work to a dedicated STA worker.
- Request QBXML 6.0 for this minimal field set. Initial validation target is installed QuickBooks 2024 R21, not all historical SDK-compatible releases. Negotiate host capabilities before expanding version-specific features.
- AccountQuery requests all active/inactive accounts without MaxReturned. Its documented schema has no iterator. Before larger entity queries, implement supported pagination and verify completeness.
- Request only public DTO fields with IncludeRetElement. Unknown optional fields stay null; XML structure/type failures and SDK warnings fail visibly. Known account no-match status returns an empty list.
- Emit one JSON envelope after the complete read; sort accounts by ordinal full name then ID. Logs go to stderr and contain operation names/timings/HRESULTs, not raw XML, session tickets, company paths or accounting values.
- Read-only enforcement comes from allowlisted query builders and absence of mutation services, with SDK read-only preferences as additional protection. The adapter never opens company-file bytes.

## Acceptance and stop condition

Offline tests and COM probing cannot establish connection reliability. Milestone acceptance requires real company/COA reads in JSON and console output, repeated sessions without resource leaks, permission refusal/revocation behavior, and matching expected test data. Then test attended multi-user access and separately approved unattended access. See the test-PC checklist.

The authorized next read-only milestone adds capability discovery and customer/vendor reads after the successful company/account proof. Invoices, bills, history and reports remain future work. Writes require separate services and explicit preview/approval design; MCP should eventually wrap the same domain services.

## Discovery and directory milestone

An embedded command catalog is the parser's command allowlist and the source of the comprehensive `capabilities` dump. Offline discovery never activates COM. Live discovery batches HostQuery and CompanyQuery in one message set and correlates responses by requestID rather than position; it reports the actual company path from the supported request-processor interface. Host-supported protocol versions do not establish operation-specific permissions or edition compatibility.

Customer and vendor list/find read all pages with session-scoped SDK iterators using qbXML 6.0, page size 1..1000. Requested return fields are repeated on continuation. Duplicate IDs, missing iterator metadata, non-progress, changed iterators, warnings and over-limit results fail without a partial-success envelope. Find filters full names locally after complete retrieval, including jobs. Exact get uses ListID or FullName without iterator pagination. Status 500/Warn with no records on an exact lookup is not_found; broad query warnings still fail. Results sort ordinal full name then ID. EndSession releases any unfinished iterator after failure.

The SDK catalog is a dated navigation snapshot with documentation links, optional sidebar version annotations and implemented command mappings. It is not a field-level schema or a promise that every operation works against every company. Bulk mutation is designed in docs/bulk-changes.md and remains unimplemented.

## Product identity

The executable and command name are `qbcli.exe` / `qbcli`. Public help and executable metadata identify McMurry Software as the developer and explicitly state that the software is not developed, sponsored, or endorsed by Intuit, and that McMurry Software is not affiliated with Intuit. JSON query envelopes stay free of banners so script consumers retain the same contract. QuickBooks is a trademark of Intuit Inc.; references describe compatibility.

The SDK authorization identity remains “McMurry Software QuickBooks CLI,” the developer-qualified name already approved in the test company. Treat that identity as stable because changing it can affect company authorization.
