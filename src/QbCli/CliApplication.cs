using System.Runtime.InteropServices;
using QbCli.Core;
using QbCli.QuickBooks;

namespace QbCli;

public static class CliApplication
{
    public const string Help = """
        qbcli by McMurry Software — Keep QuickBooks. Make it scriptable.
        Independent software. Not developed, sponsored, or endorsed by Intuit.
        McMurry Software is not affiliated with Intuit.
        Usage:
          qbcli company info [--company-file C:\path\test.QBW] [--json] [--verbose]
          qbcli accounts list [--company-file C:\path\test.QBW] [--json] [--verbose]
          qbcli invoices list [--open] [--page-size 500] [--company-file PATH] [--json]
          qbcli bills list [--open] [--page-size 500] [--company-file PATH] [--json]
          qbcli reports trial-balance --as-of YYYY-MM-DD [--basis accrual|cash] [--json]
          qbcli reports profit-loss --from YYYY-MM-DD --to YYYY-MM-DD [--basis accrual|cash] [--json]
          qbcli reports balance-sheet --as-of YYYY-MM-DD [--basis accrual|cash] [--json]
          qbcli transactions list [--since YYYY-MM-DD] [--until YYYY-MM-DD] [--page-size 500] [--json]
          qbcli transactions search --memo TEXT [--since YYYY-MM-DD] [--until YYYY-MM-DD] [--json]
          qbcli diagnostics [--json]
          qbcli capabilities [--live] [--company-file C:\path\test.QBW]
          qbcli customers list|find "TEXT"|get --id ID|--name "FULL NAME" [--json]
          qbcli vendors list|find "TEXT"|get --id ID|--name "FULL NAME" [--json]
          qbcli sdk schema OPERATION
          qbcli sdk validate OPERATION [--input fields.json]
          qbcli sdk run OPERATION [--input fields.json] [--apply --company-file PATH]
          qbcli sdk batch --input plan.json [--apply --company-file PATH]
          qbcli sdk subscription OPERATION [--input fields.json] [--apply]
          qbcli sdk subscription-batch --input plan.json [--apply]
          qbcli bulk reclassify --input selection.json --company-file PATH [--apply]
          qbcli sdk resume --input prior-run.jsonl --company-file PATH [--apply]
          qbcli events listen --input settings.json --company-file PATH --journal NEWFILE
          qbcli events cleanup --input identity.json --company-file PATH [--apply]
          qbcli events register --input server-settings.json --company-file PATH [--apply]
        Run capabilities for every command, option, result contract and SDK catalog.
        Without --company-file, use the company already open in QuickBooks.
        Company sessions accept --file-mode any|single-user|multi-user (default any); SDK mode conflicts may fail.
        Writes require --apply and an explicit company path. Initial access requires administrator authorization.
        JSON results and errors go to stdout; diagnostics go to stderr.
        Exit codes: 0 success, 2 usage, 3 platform/runtime, 4 QuickBooks, 5 internal.
        """;

    public static int Run(string[] args, Func<Options, Action<string>, IBooksReader> factory,
        TextWriter stdout, TextWriter stderr, Func<object>? diagnostics = null, Func<IRequestProcessor>? subscriptionFactory = null,
        Func<string>? callbackServerExecutable = null)
    {
        bool json = args.Contains("--json", StringComparer.Ordinal) || args.Contains("capabilities", StringComparer.Ordinal) || args.Contains("diagnostics", StringComparer.Ordinal) || args.Contains("sdk", StringComparer.Ordinal) || args.Contains("bulk", StringComparer.Ordinal) || args.Contains("events",StringComparer.Ordinal);
        try
        {
            var options = Options.Parse(args);
            if (options.Command == "help")
            {
                if (json) stdout.WriteLine(Output.Json(new { success = true, schemaVersion = 1, help = Help }));
                else stdout.WriteLine(Help);
                return 0;
            }
            if (options.Command == "diagnostics")
            {
                stdout.WriteLine(Output.Json(diagnostics?.Invoke() ?? new { success = true, schemaVersion = 1 }));
                return 0;
            }
            Action<string> log = options.Verbose ? message => stderr.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}") : _ => { };
            if (options.Command == "events register")
            {
                var settings = CallbackServerInput.Parse(SdkInput.Read(options.InputFile));
                if (!OperatingSystem.IsWindows()) throw new QbException("unsupported_platform", "Callback server registration requires Windows.");
                string executable = callbackServerExecutable?.Invoke() ?? Path.Combine(AppContext.BaseDirectory, "qbcli-callback.exe");
                stdout.WriteLine(Output.Json(PersistentCallbackRegistration.Register(settings, options.CompanyFile, executable, options.Apply)));
                return 0;
            }
            if(options.Command=="events cleanup")
            {
                var identity=SdkInput.Read(options.InputFile);
                if(identity.Count!=1 || identity["callbackClsid"] is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<string>(out var text)
                    || !Guid.TryParseExact(text,"B",out var clsid) || clsid==Guid.Empty) throw new QbException("invalid_arguments","Cleanup input requires only callbackClsid in braced GUID form.");
                if(!OperatingSystem.IsWindows()) throw new QbException("unsupported_platform","Callback registration cleanup requires Windows.");
                stdout.WriteLine(Output.Json(TemporaryCallbackRegistration.Cleanup(clsid,options.CompanyFile,options.Apply)));
                return 0;
            }
            if(options.Command=="events listen")
            {
                var settings=SdkInput.Read(options.InputFile);
                if(settings.Any(p=>p.Key is not ("callbackClsid" or "durationSeconds" or "registerCallback")) || settings["callbackClsid"] is not System.Text.Json.Nodes.JsonValue clsidValue || !clsidValue.TryGetValue<string>(out var clsidText)
                    || !Guid.TryParseExact(clsidText,"B",out var clsid) || clsid==Guid.Empty || settings["durationSeconds"] is not System.Text.Json.Nodes.JsonValue durationValue || !durationValue.TryGetValue<int>(out var seconds) || seconds is <1 or >43200)
                    throw new QbException("invalid_arguments","Listener settings require callbackClsid in braced GUID form and durationSeconds 1..43200.");
                bool register=false;
                if(settings.ContainsKey("registerCallback") && (settings["registerCallback"] is not System.Text.Json.Nodes.JsonValue registerValue || !registerValue.TryGetValue<bool>(out register)))
                    throw new QbException("invalid_arguments","registerCallback must be boolean.");
                if(options.Apply && !register) throw new QbException("invalid_arguments","--apply on a listener requires registerCallback:true.");
                if(register && !options.Apply)
                {
                    stdout.WriteLine(Output.Json(new {success=true,schemaVersion=1,mode="preview",submitted=false,callbackClsid=clsid.ToString("B"),scope="current Windows user",temporaryRegistration=true,
                        guidance="Apply creates a fresh per-user CLSID registration for the running listener and removes it on normal exit. Use DeliverOnlyIfRunning. Do not terminate the process forcibly; abnormal exit may leave the owned key."}));
                    return 0;
                }
                if(!OperatingSystem.IsWindows()) throw new QbException("unsupported_platform","Event callback hosting requires Windows.");
                var callbackResult=EventCallbackHost.Run(clsid,options.CompanyFile,NewJournal(options.Journal),seconds,register);
                stdout.WriteLine(Output.Json(callbackResult));
                return callbackResult.Success?0:4;
            }
            if (options.Command == "bulk reclassify")
            {
                var selection = ReclassificationPlanner.Selection(SdkInput.Read(options.InputFile));
                SdkXmlBuilder.ValidateProtocol(options.Protocol, options.Country);
                using var bulkReader = factory(options, log);
                if (bulkReader is not ISdkExecutor bulkExecutor) throw new InvalidOperationException("Reader has no SDK executor.");
                var plan = ReclassificationPlanner.Read(bulkExecutor, selection, options.CompanyFile, options.Protocol, options.Country, options.PageSize, options.BatchSize);
                string? digest = plan.Plan.Requests.Count == 0 ? null : SdkBatch.Prepare(plan.Plan).Digest;
                string? journal = null;
                SdkExecutionResult? applied = null;
                ErrorInfo? verificationError = null;
                IReadOnlyList<ReclassificationVerification> verified = [];
                if (options.Apply && plan.Plan.Requests.Count > 0)
                {
                    journal = NewJournal(options.Journal);
                    applied = bulkExecutor.ExecuteSdk(plan.Plan, options.CompanyFile, journal);
                    try { verified = ReclassificationPlanner.Verify(bulkExecutor, plan, selection, applied, options.CompanyFile); }
                    catch (Exception ex) { verificationError = MapError(ex); }
                    using var stream = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                    using var writer = new StreamWriter(stream);
                    writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "reclassification_verification", verified, verificationError }));
                    writer.Flush(); stream.Flush(true);
                }
                bool success = applied is null || verificationError is null && applied.Success && verified.Count == plan.Changes.Count && verified.All(v => v.ClassVerified && v.OtherFieldsPreserved);
                stdout.WriteLine(Output.Json(new { success, schemaVersion = 1, mode = options.Apply ? "apply" : "preview", companyFile = options.CompanyFile,
                    selection, scope = "Expense, journal debit/credit and deposit source lines plus confirmed service/non-inventory/other-charge single-posting purchase items in Check/Bill/CreditCardCharge/CreditCardCredit/VendorCredit/ItemReceipt, combining expense/item classes in one modifier. Explicit Invoice/SalesReceipt/CreditMemo also support single-posting items and three-posting inventory revenue lines. Actual posting accounts and ordered flat/group leaves are retained. Inventory sales revenue uses confirmed Income type and balanced asset/COGS amounts. Default types remain the original eight. Exact account ListID; no subaccounts, purchase inventory, inventory COGS/asset class selection, assemblies, nonposting leaves, deposit cash-back or header postings. Billable customer fields on flat purchase item/expense lines have scoped preservation evidence. Purchase PO links, already-billed allocations and billable group children remain unverified.",
                    coverageCompleteForAllAccountPostings = false, planDigest = digest, plan = plan.Plan,
                    counts = new { plan.ScannedTransactions, transactionsToChange = plan.Changes.Count, linesToChange = plan.Changes.Sum(c => c.LineIds.Count), plan.AlreadyClassifiedLines, plan.OtherAccountLines, plan.ItemLinesOutsideScope },
                    changes = plan.Changes.Select(c => new { c.TransactionType, c.TxnId, c.EditSequence, c.LineIds, original = c.Original, originalPostings = c.OriginalPostings }), journalFile = journal, execution = applied, verification = verified, verificationError }));
                return success ? 0 : 4;
            }
            if (options.Command == "capabilities" && !options.Live)
            {
                stdout.WriteLine(CommandCatalog.Discover().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (options.Command.StartsWith("sdk ", StringComparison.Ordinal))
            {
                if (options.Command == "sdk resume")
                {
                    var resume = SdkResume.Read(options.InputFile!,options.BatchSize);
                    if (!string.Equals(Path.GetFullPath(resume.CompanyFile),Path.GetFullPath(options.CompanyFile),StringComparison.OrdinalIgnoreCase))
                        throw new QbException("company_mismatch","Resume company path differs from the original journal.");
                    SdkExecutionResult? execution = null;
                    if (options.Apply)
                    {
                        if (resume.SourceProcessState == "running") throw new QbException("source_process_running","The original journal process is still alive. A finished request journal does not prove session cleanup completed. No requests were submitted.");
                        if (resume.SourceProcessState == "unknown") throw new QbException("source_process_unavailable","The original process lifetime could not be observed. An observation failure does not establish exit. No requests were submitted.");
                        if (!resume.CanApply) throw new QbException("reconciliation_required","Submitted unknown or warning outcomes must be reconciled before resume. No requests were submitted.");
                        if (resume.PendingPlan.Requests.Count > 0)
                        {
                            string resumeJournal = NewJournal(options.Journal);
                            using var resumeReader = factory(options,log);
                            if (resumeReader is not ISdkExecutor resumeExecutor) throw new InvalidOperationException("Reader has no SDK executor.");
                            execution = resumeExecutor.ExecuteSdk(resume.PendingPlan,options.CompanyFile,resumeJournal);
                        }
                    }
                    bool success = execution?.Success ?? true;
                    stdout.WriteLine(Output.Json(new { success,schemaVersion = 1,mode = options.Apply ? "apply" : "preview",sourceJournal = Path.GetFullPath(options.InputFile!),resumption = resume,execution,
                        guidance = "Confirmed completions are skipped. Failed requests are excluded and require a separate corrected plan. Only never-submitted requests and SDK-acknowledged 3231 requests are eligible. Unknown/warning submissions block apply. New journals record the originating PID and UTC start time; a matching live process or failed lifetime observation blocks apply even after finished. Legacy journals have no process-exit evidence; the SDK session mutex still applies. Source journal is retained; applied residual work receives a new journal. Macro and recovery-ID plans require deliberate reconciliation." }));
                    return success ? 0 : 4;
                }
                if (options.Command == "sdk schema")
                {
                    stdout.WriteLine(Output.Json(new { success = true, schemaVersion = 1, operation = SdkReference.Get(options.Value!), referenceProtocolMaximum = SdkReference.MaximumProtocol }));
                    return 0;
                }
                var prepared = SdkBatch.Prepare(SdkInput.Plan(options));
                bool subscription = options.Command is "sdk subscription" or "sdk subscription-batch";
                if (options.Command != "sdk validate" && SdkBatch.IsSubscription(prepared.Plan) != subscription)
                    throw new QbException("invalid_arguments", "Use sdk subscription/subscription-batch for subscription operations and sdk run/batch for company operations.");
                if (options.All && prepared.RequiresApply) throw new QbException("invalid_arguments", "--all cannot run a mutation.");
                if (options.All && prepared.Plan.MessageSetAttributes?.GetValueOrDefault("responseData") == "includeNone")
                    throw new QbException("invalid_arguments", "--all requires response data; includeNone cannot establish complete query records.");
                if (options.Command == "sdk validate" || prepared.RequiresApply && !options.Apply)
                {
                    stdout.WriteLine(Output.Json(new { success = true, schemaVersion = 1, mode = "preview", submitted = false,
                        planDigest = prepared.Digest, requiresApply = prepared.RequiresApply, companyFile = options.CompanyFile,
                        requestedFileMode = CompanyFileModes.Name(options.FileMode),
                        prepared.MutatesCompany, prepared.MutatesSubscriptions, prepared.MutatesRecoveryState, prepared.AffectsUserInterface,
                        executionScope = SdkBatch.IsSubscription(prepared.Plan) ? "local_quickbooks_installation" : "company",
                        referenceProtocolMaximum = SdkReference.MaximumProtocol, plan = prepared.Plan, batchCount = SdkBatch.Messages(prepared).Count }));
                    return 0;
                }
                string? journal = null;
                if (prepared.RequiresApply || options.Journal is not null)
                {
                    if (options.Journal is null)
                    {
                        var directory = Path.GetFullPath(Path.Combine("artifacts", "runs"));
                        Directory.CreateDirectory(directory);
                        journal = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jsonl");
                    }
                    else journal = Path.GetFullPath(options.Journal);
                    if (File.Exists(journal) || !Directory.Exists(Path.GetDirectoryName(journal)))
                        throw new QbException("invalid_arguments", "Journal must be a new file in an existing directory.");
                }
                if (subscription)
                {
                    if (subscriptionFactory is null) throw new QbException("unsupported_capability", "Subscription transport is unavailable in this host.");
                    var subscriptionResult = SubscriptionExecutor.Execute(subscriptionFactory(), prepared, journal, options.Apply);
                    stdout.WriteLine(Output.Json(subscriptionResult));
                    return subscriptionResult.Success ? 0 : 4;
                }
                using var sdkReader = factory(options with { Apply = options.Apply && prepared.RequiresApply }, log);
                if (sdkReader is not ISdkExecutor executor) throw new InvalidOperationException("Reader has no SDK executor.");
                if (options.All)
                {
                    stdout.WriteLine(Output.Json(executor.QuerySdkAll(prepared.Plan, options.CompanyFile, options.PageSize)));
                    return 0;
                }
                var sdkResult = executor.ExecuteSdk(prepared.Plan, options.CompanyFile, journal);
                stdout.WriteLine(Output.Json(sdkResult));
                return sdkResult.Success ? 0 : 4;
            }
            object result;
            using (var reader = factory(options, log))
            {
                var service = new ReadService(reader);
                result = options.Command switch
                {
                    "capabilities" => DiscoverLive(reader,options.CompanyFile),
                    "company info" => service.Company(),
                    "accounts list" => service.Accounts(),
                    "transactions list" or "transactions search" => reader is ITransactionReader transactions
                        ? transactions.ReadTransactions(options.Transactions!, options.PageSize, options.CompanyFile)
                        : throw new QbException("unsupported_capability", "This reader does not support transaction summaries."),
                    "reports trial-balance" or "reports profit-loss" or "reports balance-sheet" => reader is IFinancialReportReader reports
                        ? reports.ReadReport(options.Report!, options.CompanyFile)
                        : throw new QbException("unsupported_capability", "This reader does not support financial reports."),
                    "invoices list" or "bills list" => reader is IDocumentReader documents
                        ? documents.ReadDocuments(options.Command == "invoices list" ? DocumentKind.Invoice : DocumentKind.Bill, options.OpenOnly, options.PageSize, options.CompanyFile)
                        : throw new QbException("unsupported_capability", "This reader does not support document summaries."),
                    "customers list" or "customers find" or "customers get" => PartyService.Read(reader, PartyKind.Customer, options.Selection, options.PageSize),
                    "vendors list" or "vendors find" or "vendors get" => PartyService.Read(reader, PartyKind.Vendor, options.Selection, options.PageSize),
                    _ => throw new InvalidOperationException("Command catalog has no handler.")
                };
            }
            Output.Write(stdout, result, json);
            return 0;
        }
        catch (Exception ex)
        {
            var error = MapError(ex);
            var result = new ErrorResult(false, 1, error);
            Output.Write(json ? stdout : stderr, result, json);
            if (args.Contains("--verbose", StringComparer.Ordinal))
                stderr.WriteLine($"Failure: {ex.GetType().Name}; HRESULT 0x{ex.HResult:X8}.");
            return error.Code switch
            {
                "invalid_arguments" => 2,
                "unsupported_platform" or "sdk_not_registered" => 3,
                "internal_error" => 5,
                _ => 4
            };
        }
    }
    private static object DiscoverLive(IBooksReader reader,string expectedCompanyFile)
    {
        var connection=reader.GetConnectionInfo();
        if(expectedCompanyFile.Length>0 && !string.Equals(Path.GetFullPath(expectedCompanyFile),Path.GetFullPath(connection.CompanyFile),StringComparison.OrdinalIgnoreCase))
            throw new QbException("company_mismatch","The SDK session company differs from the explicitly selected file.");
        var features=reader is ISdkExecutor executor ? CompanyFeatureDiscovery.Read(executor,connection)
            : CompanyFeatureDiscovery.NotQueried("executor_unavailable");
        return CommandCatalog.Discover(connection,features);
    }
    private static string NewJournal(string? requested)
    {
        string path;
        if (requested is null)
        {
            var directory = Path.GetFullPath(Path.Combine("artifacts", "runs")); Directory.CreateDirectory(directory);
            path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jsonl");
        }
        else path = Path.GetFullPath(requested);
        if (File.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path))) throw new QbException("invalid_arguments", "Journal must be a new file in an existing directory.");
        return path;
    }
    private static ErrorInfo MapError(Exception ex) => ex switch
    {
        QbException qb => new(qb.Code, qb.Message, qb.DiagnosticCode),
        COMException { HResult: unchecked((int)0x80040154) } => new("sdk_not_registered", "QuickBooks request processor is not registered for this process architecture. Install or repair QuickBooks and use the matching x64/x86 executable.", "0x80040154"),
        COMException { HResult: unchecked((int)0x80040414) } => new("sdk_busy", "QuickBooks cannot accept this connection while another session or dialog is active. Close the dialog or let the existing SDK batch finish.", "0x80040414"),
        COMException com => new("quickbooks_connection", "QuickBooks communication failed. Open the test company as administrator, authorize McMurry Software QuickBooks CLI, and check the QuickBooks SDK log.", $"0x{com.HResult:X8}"),
        _ => new("internal_error", "An unexpected application error occurred. Run again with --verbose for diagnostics.")
    };
}
