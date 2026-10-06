using System.Diagnostics;
using QbCli.Core;

namespace QbCli.QuickBooks;

public sealed class QuickBooksReader : IBooksReader, ISdkExecutor, IDocumentReader, IFinancialReportReader, ITransactionReader
{
    private readonly IRequestProcessor processor;
    private readonly Action<string> log;
    private string? ticket;
    private bool opened;
    private bool disposed;
    private readonly bool allowsWrites;
    private readonly CompanyFileMode fileMode;

    public QuickBooksReader(IRequestProcessor processor, string companyFile, Action<string>? log = null, bool allowWrites = false,
        CompanyFileMode fileMode = CompanyFileMode.DoNotCare)
    {
        this.processor = processor;
        this.log = log ?? (_ => { });
        allowsWrites = allowWrites;
        this.fileMode = fileMode;
        try
        {
            this.log("Opening local QuickBooks connection.");
            processor.Open();
            opened = true;
            processor.ConfigureAccess();
            processor.ConfigureFileMode(fileMode);
            this.log("Beginning company session; QuickBooks may require administrator authorization.");
            ticket = processor.Begin(companyFile);
            if (string.IsNullOrEmpty(ticket)) throw new QbException("session_failed", "QuickBooks did not return a session ticket.");
            this.log("Company session established.");
        }
        catch { Dispose(); throw; }
    }
    public CompanyInfo GetCompany() => QbXml.Company(Query("CompanyQuery", QbXml.CompanyRequest()));
    public TransactionsResult ReadTransactions(TransactionSelection selection, int pageSize, string expectedCompanyFile)
    {
        selection.Validate();
        var connection = GetConnectionInfo();
        if (connection.Host.Country is null) throw new QbException("unsupported_capability", "The connected host did not advertise a query country.");
        var query = QuerySdkAll(TransactionQueries.Plan(selection, connection.Host.Country), expectedCompanyFile, pageSize);
        var records = query.Data.Select(TransactionQueries.Normalize).ToArray();
        if (records.Any(r => selection.Since is not null && string.CompareOrdinal(r.Date, selection.Since) < 0 || selection.Until is not null && string.CompareOrdinal(r.Date, selection.Until) > 0))
            throw new QbException("invalid_response", "The SDK returned a transaction outside the requested date range.");
        return new(true, 1, connection.Company.Name, query.CompanyFile, selection, query.Complete, query.Pages, records.Length,
            TransactionQueries.Coverage, TransactionQueries.Select(records, selection));
    }
    public FinancialReportResult ReadReport(ReportSelection selection, string expectedCompanyFile)
    {
        selection.Validate();
        var connection = GetConnectionInfo();
        if (connection.Host.Country is null) throw new QbException("unsupported_capability", "The connected host did not advertise a report country.");
        var result = ExecuteSdk(FinancialReports.Plan(selection, connection.Host.Country), expectedCompanyFile, null);
        var outcome = result.Results.Single();
        if (!result.Success || outcome.Outcome != "succeeded") throw new QbException("quickbooks_status", "The SDK did not return a successful financial report.", outcome.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (outcome.Data.Count != 1) throw new QbException("invalid_response", "The SDK did not return exactly one financial report.");
        return FinancialReports.Normalize(outcome.Data[0], selection, connection.Company.Name, result.CompanyFile);
    }
    public DocumentsResult ReadDocuments(DocumentKind kind, bool openOnly, int pageSize, string expectedCompanyFile)
    {
        var connection = GetConnectionInfo();
        if (connection.Host.Country is null) throw new QbException("unsupported_capability", "The connected host did not advertise a query country.");
        var query = QuerySdkAll(DocumentQueries.Plan(kind, openOnly, connection.Host.Country), expectedCompanyFile, pageSize);
        IReadOnlyDictionary<string,decimal> balances=new Dictionary<string,decimal>();
        if(kind==DocumentKind.Bill)
        {
            var billPlan=DocumentQueries.BillBalancesPlan(query.Data.Select(r=>DocumentQueries.Normalize(r,kind,openOnly)),connection.Host.Country);
            if(billPlan.Requests.Count>0)balances=DocumentQueries.BillBalances(ExecuteSdk(billPlan,expectedCompanyFile,null));
        }
        var records = query.Data.Select(r => DocumentQueries.Normalize(r, kind, openOnly,
                kind==DocumentKind.Bill && balances.TryGetValue(r.Children.Single(c=>c.Name=="TxnID").Value!,out var balance)?balance:null))
            .OrderBy(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.Number, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        return new(true, 1, connection.Company.Name, query.CompanyFile, DocumentQueries.Name(kind).ToLowerInvariant(),
            openOnly, query.Complete, query.Pages, records);
    }
    public IReadOnlyList<Account> GetAccounts() => QbXml.Accounts(Query("AccountQuery", QbXml.AccountsRequest()));
    public SdkExecutionResult ExecuteSdk(SdkPlan plan, string expectedCompanyFile, string? journalFile)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var prepared = SdkBatch.Prepare(plan);
        if (plan.Requests.Any(r => SdkReference.Get(r.Operation).Transport != "company"))
            throw new QbException("invalid_arguments", "Subscription requests require the separate subscription transport.");
        if (prepared.RequiresApply && (!allowsWrites || string.IsNullOrWhiteSpace(expectedCompanyFile) || journalFile is null))
            throw new QbException("write_not_enabled", "Mutations require apply intent, an explicit company path and a new durable journal.");
        var connection = GetConnectionInfo();
        if (expectedCompanyFile.Length > 0 && !string.Equals(Path.GetFullPath(connection.CompanyFile), Path.GetFullPath(expectedCompanyFile), StringComparison.OrdinalIgnoreCase))
            throw new QbException("company_mismatch", "The SDK session company differs from the explicitly selected file. No requests were submitted.");
        if (!connection.Host.SupportedProtocolVersions.Contains(plan.ProtocolVersion, StringComparer.Ordinal) || connection.Host.Country != plan.Country)
            throw new QbException("unsupported_capability", "The connected host does not advertise the plan's country/protocol. No requests were submitted.");
        return SdkBatch.Execute(prepared, connection.CompanyFile, journalFile, request => Query("SDK batch", request), CompanyFileModes.Name(fileMode));
    }
    public SdkQueryResult QuerySdkAll(SdkPlan plan, string expectedCompanyFile, int pageSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (plan.Requests.Count != 1 || pageSize is < 1 or > 1000) throw new QbException("invalid_arguments", "--all requires one iterator query and page size 1..1000.");
        if (plan.MessageSetAttributes?.GetValueOrDefault("responseData") == "includeNone" || plan.MessageSetAttributes?.Keys.Any(k => k is "newMessageSetID" or "oldMessageSetID") == true)
            throw new QbException("invalid_arguments", "Iterator completion requires records and does not accept recovery IDs across multiple messages.");
        var call = plan.Requests[0];
        var definition = SdkReference.Get(call.Operation);
        if (definition.RequiresApply || !definition.Request.Attributes.Any(a => a.Name == "iterator"))
            throw new QbException("invalid_arguments", "This operation has no documented query iterator. Use an unbounded query or its specific completeness rules.");
        SdkBatch.Prepare(plan);
        var fields = (System.Text.Json.Nodes.JsonObject)call.Fields.DeepClone();
        var hiddenIdentityFields = new HashSet<string>(StringComparer.Ordinal);
        if (call.Operation == "TransactionQuery" && fields["IncludeRetElement"] is { } requested)
        {
            var selected = requested is System.Text.Json.Nodes.JsonArray array
                ? array.Select(n => n?.GetValue<string>()).ToList() : [requested.GetValue<string>()];
            // A summary and a posting line can share a TxnID. Fetch composite
            // identities internally even when the caller projects other fields,
            // then remove only the fields added here from the public result.
            if (selected.Count > 0)
            {
                foreach (var name in new[] { "TxnType", "TxnID", "TxnLineID", "AccountRef", "Amount" })
                    if (!selected.Contains(name, StringComparer.Ordinal)) { selected.Add(name); hiddenIdentityFields.Add(name); }
                fields["IncludeRetElement"] = new System.Text.Json.Nodes.JsonArray(selected.Select(n => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(n)).ToArray());
            }
        }
        var attributes = fields["$attributes"] as System.Text.Json.Nodes.JsonObject ?? new();
        if (attributes.ContainsKey("iterator") || attributes.ContainsKey("iteratorID") || attributes["metaData"]?.ToString() == "MetaDataOnly")
            throw new QbException("invalid_arguments", "--all manages the iterator and requires records rather than MetaDataOnly.");
        attributes["iterator"] = "Start";
        fields["$attributes"] = attributes;
        fields["MaxReturned"] = pageSize;
        var pageCall = call with { Fields = fields };
        var pagePlan = plan with { Requests = new[] { pageCall } };
        SdkBatch.Prepare(pagePlan); // Reject unsupported direct lookup/filter combinations before querying.
        var connection = GetConnectionInfo();
        if (expectedCompanyFile.Length > 0 && !string.Equals(Path.GetFullPath(connection.CompanyFile), Path.GetFullPath(expectedCompanyFile), StringComparison.OrdinalIgnoreCase))
            throw new QbException("company_mismatch", "The SDK session company differs from the explicitly selected file.");
        if (!connection.Host.SupportedProtocolVersions.Contains(plan.ProtocolVersion, StringComparer.Ordinal) || connection.Host.Country != plan.Country)
            throw new QbException("unsupported_capability", "The connected host does not advertise the query country/protocol.");
        var data = new List<SdkElement>();
        var ids = new HashSet<(string Id, string? TxnType, string? LineId, string? AccountId, decimal? Amount)>();
        int previousRemaining = int.MaxValue, pages = 0;
        string? iterator = null;
        while (true)
        {
            var prepared = SdkBatch.Prepare(pagePlan);
            var result = SdkBatch.Responses(Query("SDK iterator page", SdkBatch.Message(pagePlan, prepared.Requests)), [pageCall]).Single();
            if (result.Outcome is not ("succeeded" or "no_match")) throw new QbException("quickbooks_status", "SDK query did not return a complete successful page.", result.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            int remaining;
            if (result.Outcome == "no_match" && result.Data.Count == 0 && result.RemainingCount is null) remaining = 0;
            else if (!int.TryParse(result.RemainingCount, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out remaining) || remaining < 0)
                throw new QbException("incomplete_response", "SDK iterator response has invalid remaining-count metadata.");
            if (remaining >= previousRemaining || result.Data.Count > pageSize || remaining > 0 && (result.Data.Count == 0 || string.IsNullOrWhiteSpace(result.IteratorId) || iterator is not null && result.IteratorId != iterator))
                throw new QbException("incomplete_response", "SDK iterator did not make valid progress.");
            foreach (var record in result.Data)
            {
                var id = record.Children.FirstOrDefault(c => c.Name is "ListID" or "TxnID")?.Value;
                string? txnType = null, lineId = null, accountId = null;
                decimal? postingAmount=null;
                if (call.Operation == "TransactionQuery")
                {
                    txnType = record.Children.FirstOrDefault(c => c.Name == "TxnType")?.Value;
                    lineId = record.Children.FirstOrDefault(c => c.Name == "TxnLineID")?.Value;
                    if (record.Name != "TransactionRet" || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(txnType))
                        throw new QbException("incomplete_response", "Transaction iterator lacks required composite identity fields.");
                    if (string.IsNullOrWhiteSpace(lineId)) lineId = null;
                    // Inventory leaves have income, inventory and COGS postings
                    // sharing the same line ID. Summaries still use transaction
                    // identity; account-less group/nonposting rows retain null.
                    if(lineId is not null)
                    {
                        // Inventory purchase credits can return distinct rows
                        // sharing even the account ID, with different amounts.
                        // Summaries must never use amount to mask duplicate IDs.
                        var amounts=record.Children.Where(c=>c.Name=="Amount").ToArray();
                        if(amounts.Length>1 || amounts.Any(c=>c.Children.Count>0)) throw new QbException("incomplete_response","Posting iterator has malformed amount identity.");
                        if(amounts.SingleOrDefault() is { } amount)
                        {
                            if(!decimal.TryParse(amount.Value,System.Globalization.NumberStyles.AllowLeadingSign|System.Globalization.NumberStyles.AllowDecimalPoint,
                                System.Globalization.CultureInfo.InvariantCulture,out var parsedAmount)) throw new QbException("incomplete_response","Posting iterator has invalid amount identity.");
                            postingAmount=parsedAmount;
                        }
                        var accounts=record.Children.Where(c=>c.Name=="AccountRef").ToArray();
                        if(accounts.Length>1) throw new QbException("incomplete_response","Posting iterator has duplicate account references.");
                        if(accounts.SingleOrDefault() is { } account)
                        {
                            var accountIds=account.Children.Where(c=>c.Name=="ListID").ToArray();
                            if(account.Value is not null || accountIds.Length>1 || accountIds.Any(c=>c.Children.Count>0))
                                throw new QbException("incomplete_response","Posting iterator has a malformed account identity.");
                            accountId=accountIds.SingleOrDefault()?.Value;
                            if(string.IsNullOrWhiteSpace(accountId))
                            {
                                if(account.Children.Any(c=>!string.IsNullOrWhiteSpace(c.Value)))
                                    throw new QbException("incomplete_response","Posting account reference lacks its ListID.");
                                accountId=null;
                            }
                        }
                    }
                }
                if (id is not null && !ids.Add((id, txnType, lineId, accountId,postingAmount))) throw new QbException("incomplete_response", "SDK iterator returned a duplicate record identity.");
                data.Add(hiddenIdentityFields.Count == 0 ? record : record with
                    { Children = record.Children.Where(c => !hiddenIdentityFields.Contains(c.Name)).ToArray() });
            }
            if (data.Count > 1_000_000) throw new QbException("result_limit", "Query exceeds the one-million-record output limit.");
            pages++;
            if (remaining == 0) return new(true, 1, connection.CompanyFile, call.Operation, plan.ProtocolVersion, true, pages, data, CompanyFileModes.Name(fileMode));
            iterator = result.IteratorId;
            previousRemaining = remaining;
            attributes["iterator"] = "Continue"; attributes["iteratorID"] = iterator;
        }
    }
    public ConnectionInfo GetConnectionInfo()
    {
        var result = QbXml.Connection(Query("Host and company discovery", QbXml.ConnectionRequest()));
        return new(result.Company, result.Host, processor.CurrentCompanyFile(ticket!), QbXml.ProtocolVersion, CompanyFileModes.Name(fileMode));
    }
    public IReadOnlyList<Party> GetParties(PartyKind kind, PartySelection selection, int pageSize)
    {
        bool paged = selection.Kind is PartySelectionKind.All or PartySelectionKind.Contains;
        var records = new List<Party>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? iterator = null;
        int previousRemaining = int.MaxValue;
        do
        {
            var page = QbXml.Parties(Query(kind + "Query", QbXml.PartiesRequest(kind, selection, pageSize, iterator)), kind, paged);
            if (page.Records.Count > pageSize && paged || page.Remaining >= previousRemaining && paged)
                throw new QbException("incomplete_response", "QuickBooks query pagination did not make valid progress.");
            if (iterator is not null && page.Remaining > 0 && page.IteratorId != iterator)
                throw new QbException("incomplete_response", "QuickBooks changed the query iterator before completion.");
            foreach (var record in page.Records)
            {
                if (!ids.Add(record.Id)) throw new QbException("incomplete_response", "QuickBooks returned duplicate record IDs across query pages.");
                records.Add(record);
            }
            if (records.Count > 1_000_000) throw new QbException("result_limit", "Query exceeds the current one-million-record output limit.");
            previousRemaining = page.Remaining;
            iterator = page.Remaining > 0 ? page.IteratorId : null;
        } while (iterator is not null);
        return selection.Kind switch
        {
            PartySelectionKind.Contains => records.Where(p => p.FullName.Contains(selection.Value!, StringComparison.OrdinalIgnoreCase)).ToArray(),
            PartySelectionKind.Id => records.Where(p => p.Id == selection.Value).ToArray(),
            PartySelectionKind.FullName => records.Where(p => string.Equals(p.FullName, selection.Value, StringComparison.OrdinalIgnoreCase)).ToArray(),
            _ => records
        };
    }
    private string Query(string name, string request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var clock = Stopwatch.StartNew();
        log($"Sending {name}.");
        var response = processor.Process(ticket!, request);
        log($"{name} response received in {clock.ElapsedMilliseconds} ms.");
        return response;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Cleanup failures cannot prevent the remaining cleanup or replace the original query error.
        if (ticket is not null) Cleanup(() => processor.End(ticket), "EndSession");
        if (opened) Cleanup(processor.Close, "CloseConnection");
        Cleanup(processor.Dispose, "COM release");
    }
    private void Cleanup(Action action, string stage)
    {
        try { action(); log($"{stage} completed."); }
        catch (Exception ex) { log($"{stage} failed ({ex.GetType().Name}, 0x{ex.HResult:X8})."); }
    }
}
