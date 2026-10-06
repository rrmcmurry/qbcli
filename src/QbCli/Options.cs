using System.Globalization;
using QbCli.Core;

namespace QbCli;

public sealed record Options(string Command, bool Json, bool Verbose, string CompanyFile,
    bool Live = false, string? Value = null, string? Id = null, string? Name = null, int PageSize = 500,
    string? InputFile = null, bool Apply = false, string? Journal = null, string Protocol = "13.0", string Country = "US", int BatchSize = 50, bool All = false,
    CompanyFileMode FileMode = CompanyFileMode.DoNotCare, bool OpenOnly = false, ReportSelection? Report = null, TransactionSelection? Transactions = null)
{
    public static Options Parse(string[] args)
    {
        var words = new List<string>();
        bool json = false, verbose = false, live = false;
        string file = "";
        string? id = null, name = null;
        int pageSize = 500;
        string? input = null, journal = null;
        string protocol = "13.0", country = "US";
        bool apply = false, all = false;
        int batchSize = 50;
        var fileMode = CompanyFileMode.DoNotCare;
        bool openOnly = false;
        string? reportFrom = null, reportTo = null, reportAsOf = null;
        string reportBasis = "accrual";
        string? since = null, until = null, memo = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string ReadValue()
            {
                if (!seen.Add(arg) || ++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]))
                    throw Usage($"{arg} requires one nonempty value and cannot be repeated.");
                return args[i];
            }
            switch (arg)
            {
                case "--json": json = true; break;
                case "--verbose": verbose = true; break;
                case "--live": live = true; break;
                case "--apply": apply = true; break;
                case "--all": all = true; break;
                case "--open":
                    if (!seen.Add(arg)) throw Usage("--open cannot be repeated.");
                    openOnly = true; break;
                case "--input": input = ReadValue(); break;
                case "--journal": journal = ReadValue(); break;
                case "--protocol": protocol = ReadValue(); break;
                case "--country": country = ReadValue(); break;
                case "--file-mode": fileMode = CompanyFileModes.Parse(ReadValue()); break;
                case "--from": reportFrom = ReadValue(); break;
                case "--to": reportTo = ReadValue(); break;
                case "--as-of": reportAsOf = ReadValue(); break;
                case "--basis": reportBasis = ReadValue(); break;
                case "--since": since = ReadValue(); break;
                case "--until": until = ReadValue(); break;
                case "--memo": memo = ReadValue(); break;
                case "--batch-size":
                    if (!int.TryParse(ReadValue(), NumberStyles.None, CultureInfo.InvariantCulture, out batchSize) || batchSize is < 1 or > 1000)
                        throw Usage("--batch-size must be 1..1000.");
                    break;
                case "--company-file":
                    file = ReadValue();
                    if (!Path.IsPathFullyQualified(file) || !string.Equals(Path.GetExtension(file), ".qbw", StringComparison.OrdinalIgnoreCase))
                        throw Usage("--company-file requires an absolute .QBW path.");
                    break;
                case "--id": id = ReadValue(); break;
                case "--name": name = ReadValue(); break;
                case "--page-size":
                    if (!int.TryParse(ReadValue(), NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > 1000)
                        throw Usage("--page-size must be an integer from 1 to 1000.");
                    break;
                case "--help" or "-h": words.Add("help"); break;
                default:
                    if (arg.StartsWith('-')) throw Usage($"Unknown option: {arg}");
                    words.Add(arg); break;
            }
        }
        string? value = null;
        if (words.Count == 3 && words[0] == "sdk" && words[1] is "run" or "validate" or "schema" or "subscription")
        { value = words[2]; words.RemoveAt(2); }
        if (words.Count == 3 && words[0] is "customers" or "vendors" && words[1] == "find")
        {
            value = words[2]; words.RemoveAt(2);
            if (string.IsNullOrWhiteSpace(value)) throw Usage("find requires nonempty search text.");
        }
        var command = string.Join(' ', words);
        if (command == "") command = "help";
        if (!CommandCatalog.CommandNames.Contains(command, StringComparer.Ordinal))
            throw Usage("Unsupported command. Run qbcli capabilities for the complete command contract.");
        bool directory = command.StartsWith("customers ", StringComparison.Ordinal) || command.StartsWith("vendors ", StringComparison.Ordinal);
        bool documents = command is "invoices list" or "bills list";
        bool transactionHistory = command is "transactions list" or "transactions search";
        TransactionSelection? transactions = null;
        if (transactionHistory)
        {
            if (command == "transactions search" && memo is null || command == "transactions list" && memo is not null)
                throw Usage("transactions search requires --memo; transactions list does not accept it.");
            transactions = new(since, until, memo);
            transactions.Validate();
        }
        else if (seen.Overlaps(["--since", "--until", "--memo"])) throw Usage("Transaction date/memo options require transactions list/search.");
        ReportSelection? report = command switch
        {
            "reports trial-balance" => new(FinancialReportKind.TrialBalance, reportFrom, reportTo, reportAsOf, reportBasis),
            "reports profit-loss" => new(FinancialReportKind.ProfitLoss, reportFrom, reportTo, reportAsOf, reportBasis),
            "reports balance-sheet" => new(FinancialReportKind.BalanceSheet, reportFrom, reportTo, reportAsOf, reportBasis),
            _ => null
        };
        if (report is not null) report.Validate();
        else if (seen.Overlaps(["--from", "--to", "--as-of", "--basis"])) throw Usage("Report date/basis options require a financial report command.");
        if (openOnly && !documents) throw Usage("--open is only supported by invoices list and bills list.");
        bool bulk = command == "bulk reclassify";
        bool events = command is "events listen" or "events cleanup" or "events register";
        bool sdk = command.StartsWith("sdk ", StringComparison.Ordinal);
        if (seen.Contains("--file-mode") && (events || command is "help" or "diagnostics" or "sdk schema" or "sdk validate" or "sdk subscription" or "sdk subscription-batch" || command == "capabilities" && !live))
            throw Usage("--file-mode applies only to commands using a company SDK session.");
        if (!sdk && !bulk && !events && (input is not null || apply || all || journal is not null || seen.Contains("--protocol") || seen.Contains("--country") || seen.Contains("--batch-size")))
            throw Usage("SDK request options require an sdk command.");
        if (sdk)
        {
            bool subscription = command is "sdk subscription" or "sdk subscription-batch";
            bool batch = command is "sdk batch" or "sdk subscription-batch" or "sdk resume";
            if (subscription && file.Length > 0) throw Usage("Subscriptions are local-installation scoped and do not accept --company-file.");
            if (all && (command != "sdk run" || apply)) throw Usage("--all is only supported by sdk run iterator queries without --apply.");
            if (!batch && value is null) throw Usage("SDK command requires a documented operation name.");
            if (batch && input is null) throw Usage("SDK batch requires --input with a batch plan file.");
            if (command is "sdk schema" or "sdk validate" && (apply || journal is not null)) throw Usage("Offline schema/validation cannot apply or journal.");
            if (command == "sdk schema" && input is not null) throw Usage("sdk schema does not accept --input.");
            if (apply && !subscription && file.Length == 0) throw Usage("--apply requires an explicit --company-file path.");
            if (journal is not null && !apply) throw Usage("--journal requires --apply.");
            if (command == "sdk resume" && (file.Length == 0 || seen.Contains("--protocol") || seen.Contains("--country")))
                throw Usage("sdk resume requires the original explicit company path and uses the journal's country/protocol.");
        }
        if (bulk)
        {
            if (input is null || file.Length == 0) throw Usage("bulk reclassify requires --input selection.json and an explicit --company-file.");
            if (all || live) throw Usage("bulk reclassify manages its own complete queries.");
            if (journal is not null && !apply) throw Usage("--journal requires --apply.");
        }
        if (events)
        {
            if(input is null || command=="events listen" && journal is null || file.Length==0) throw Usage("Event commands require --input and --company-file; listen also requires a new --journal file.");
            if(command!="events listen" && journal is not null) throw Usage("events register/cleanup do not accept --journal; server journalDirectory is an explicit setting.");
            if(all || live || seen.Contains("--protocol") || seen.Contains("--country") || seen.Contains("--batch-size")) throw Usage("events listen does not execute SDK requests or create subscriptions.");
        }
        if (live && command != "capabilities") throw Usage("--live is only supported by capabilities.");
        if (command == "capabilities" && file.Length > 0 && !live) throw Usage("--company-file on capabilities requires --live.");
        if (command is "help" or "diagnostics" && file.Length > 0) throw Usage("This command does not accept --company-file.");
        if (seen.Contains("--page-size") && (!directory || command.EndsWith(" get", StringComparison.Ordinal)) && !(command == "sdk run" && all) && !bulk && !documents && !transactionHistory)
            throw Usage("--page-size requires a paginated list/find, bulk reclassification or sdk run --all.");
        if (command.EndsWith(" get", StringComparison.Ordinal))
        {
            if ((id is null) == (name is null)) throw Usage("get requires exactly one of --id or --name (exact full name).");
        }
        else if (id is not null || name is not null) throw Usage("--id and --name are only supported by get.");
        if (command.EndsWith(" find", StringComparison.Ordinal) && value is null) throw Usage("find requires one positional search text.");
        return new(command, json, verbose, file, live, value, id, name, pageSize, input, apply, journal, protocol, country, batchSize, all, fileMode, openOnly, report, transactions);
    }
    public PartySelection Selection => Id is not null ? new(PartySelectionKind.Id, Id)
        : Name is not null ? new(PartySelectionKind.FullName, Name)
        : Value is not null ? new(PartySelectionKind.Contains, Value) : new();
    private static QbException Usage(string message) => new("invalid_arguments", message);
}
