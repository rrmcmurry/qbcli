using QbCli.Core;

namespace QbCli.QuickBooks;

public static class SdkFileModes
{
    // Official qbFileMode values used by IRequestProcessor.BeginSession.
    public static int NativeValue(CompanyFileMode mode) => mode switch
    {
        CompanyFileMode.SingleUser => 0, CompanyFileMode.MultiUser => 1,
        CompanyFileMode.DoNotCare => 2, _ => throw new QbException("invalid_arguments", "Unknown company file mode.")
    };
}
