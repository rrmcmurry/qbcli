namespace QbCli.Core;

public enum CompanyFileMode { DoNotCare, SingleUser, MultiUser }

public static class CompanyFileModes
{
    public static string Name(CompanyFileMode mode) => mode switch
    {
        CompanyFileMode.DoNotCare => "any", CompanyFileMode.SingleUser => "single-user",
        CompanyFileMode.MultiUser => "multi-user", _ => throw new QbException("invalid_arguments", "Unknown company file mode.")
    };
    public static CompanyFileMode Parse(string name) => name switch
    {
        "any" => CompanyFileMode.DoNotCare, "single-user" => CompanyFileMode.SingleUser,
        "multi-user" => CompanyFileMode.MultiUser, _ => throw new QbException("invalid_arguments", "--file-mode must be any, single-user or multi-user.")
    };
}
