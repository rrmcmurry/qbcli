# Project instructions

This is an independent McMurry Software QuickBooks Desktop CLI. Keep the CLI useful for humans and scripts without AI. Read README.md and docs/design.md before changing the architecture.

- The user approved broad SDK implementation and add/modify/delete/merge testing in QBData/Test.QBW on 2026-10-05. Writes require explicit CLI apply intent and an explicit company path; automated live mutation tests must enforce the designated Test.QBW boundary. Other companies are not authorized for agent-run mutations.
- Use supported Intuit SDK/COM/QBXML interfaces only. Never directly read/modify the company-file database, reverse engineer it, or scrape the QuickBooks GUI.
- Keep CLI parsing/output, domain DTOs/services and SDK/COM/XML code separate.
- Never expose arbitrary QBXML execution in the CLI. Schema-validated JSON for allowlisted documented SDK operations is authorized; reject unknown operations/fields and construct XML internally. Do not build MCP during this milestone.
- Keep structured JSON success and error contracts stable. Logs go to stderr and omit company values, XML, session tickets and sensitive data.
- Keep COM calls and release on one STA thread. Do not release a preferences RCW that shares the processor's COM identity until final processor cleanup.
- Test non-live logic with `dotnet test McMurry.QbCli.sln`. Live testing requires a specifically designated test company and normal administrator authorization.
- Company files/sidecars, logs, generated distribution files and accounting exports must stay out of Git. Do not redistribute Intuit request-processor DLLs.
- Public product/command name: `qbcli` by McMurry Software; executable: `qbcli.exe`. Keep McMurry Software ownership and lack of Intuit development, endorsement, sponsorship or affiliation explicit in public help/documentation. Preserve the existing SDK authorization identity unless changing it is intentional.
