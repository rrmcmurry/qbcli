using System.Runtime.InteropServices;
using QbCli.Core;
using QbCli.QuickBooks;

namespace QbCli;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => CliApplication.Run(args, CreateReader, Console.Out, Console.Error, Diagnostics, CreateSubscriptionProcessor);
    private static IRequestProcessor CreateSubscriptionProcessor()
    {
        if (!OperatingSystem.IsWindows()) throw new QbException("unsupported_platform", "QuickBooks Desktop integration requires Windows.");
        return new ComRequestProcessor();
    }
    private static IBooksReader CreateReader(Options options, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) throw new QbException("unsupported_platform", "QuickBooks Desktop integration requires Windows.");
        if (options.CompanyFile.Length > 0 && !File.Exists(options.CompanyFile))
            throw new QbException("company_file_not_found", "The specified company file does not exist or is inaccessible.");
        log($"Process architecture: {RuntimeInformation.ProcessArchitecture}; apartment: {Thread.CurrentThread.GetApartmentState()}.");
        return new QuickBooksReader(new ComRequestProcessor(), options.CompanyFile, log, allowWrites: options.Apply, fileMode: options.FileMode);
    }
    // Non-connecting environment check: never opens QuickBooks or a company file.
    private static object Diagnostics()
    {
        bool registered = OperatingSystem.IsWindows() && Type.GetTypeFromProgID("QBXMLRP2.RequestProcessor") is not null;
        if (registered && OperatingSystem.IsWindows())
        {
            using var processor = new ComRequestProcessor();
            processor.ConfigureAccess();
        }
        return new
        {
            success = true, schemaVersion = 1, windows = OperatingSystem.IsWindows(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            apartment = Thread.CurrentThread.GetApartmentState().ToString(),
            requestProcessorRegistered = registered,
            comActivationTested = registered, readOnlyPreferencesConfigured = false,
            requestedAccess = new { readWrite = true, personalData = "required", unattended = "required" },
            fullAccessPreferencesConfigured = registered,
            liveConnectionTested = false
        };
    }
}

