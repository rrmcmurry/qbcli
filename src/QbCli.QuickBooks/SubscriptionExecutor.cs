using QbCli.Core;

namespace QbCli.QuickBooks;

public static class SubscriptionExecutor
{
    public static SdkExecutionResult Execute(IRequestProcessor processor, PreparedSdkPlan prepared, string? journal, bool apply)
    {
        using (processor)
        {
            if (!SdkBatch.IsSubscription(prepared.Plan) || prepared.Plan.Requests.Any(r => SdkReference.Get(r.Operation).Transport != "subscription"))
                throw new QbException("invalid_arguments", "Subscription transport accepts subscription operations only.");
            if (prepared.RequiresApply && (!apply || journal is null))
                throw new QbException("write_not_enabled", "Subscription changes require apply intent and a durable journal.");
            if (processor is not ISubscriptionProcessor subscriptions) throw new QbException("unsupported_capability", "Request processor has no subscription interface.");
            bool opened = false;
            try
            {
                processor.Open(); opened = true;
                processor.ConfigureAccess();
                // ProcessSubscription is installation-scoped. Never BeginSession
                // or attach a company-file identity to these requests.
                return SdkBatch.Execute(prepared, "", journal, subscriptions.ProcessSubscription);
            }
            finally { if (opened) processor.Close(); }
        }
    }
}
