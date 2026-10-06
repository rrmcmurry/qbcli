# SDK event callbacks

`events listen` supplies an independent McMurry Software COM callback receiver. It implements the documented IQBEventCallback interface and writes notifications to a new JSONL journal before returning success. It does not invoke the QuickBooks SDK from inside the callback. The installed official sdkevent.dll type library supplied the interface ABI facts; no Intuit DLL or sample implementation is distributed.

Listener settings:

```json
{"callbackClsid":"{GENERATE-A-FRESH-GUID}","durationSeconds":300,"registerCallback":true}
```

Use a real fresh braced GUID. `durationSeconds` is an integer1..43200. A listener requires an explicit absolute company path and a new journal. For example:

```powershell
qbcli events listen --input listener.json --company-file 'C:\Test\Test.QBW' --journal events.jsonl
qbcli events listen --input listener.json --company-file 'C:\Test\Test.QBW' --journal events.jsonl --apply
```

With `registerCallback:true`, the first command previews registration without starting a listener. The second creates a fresh CLSID under the current user's Windows class registrations and starts the receiver. It refuses existing COM identities. The registration carries qbcli ownership and company scope and is removed on normal exit. A per-identity mutex excludes concurrent listeners. Without registration, omit `registerCallback` or set it false; that mode is useful for a direct COM probe, but QuickBooks rejected a subscription without the registry entry on the test PC.

Listening creates no SDK subscription. In a separate process, use `sdk subscription DataEventSubscriptionAdd`, UIEventSubscriptionAdd or UIExtensionSubscriptionAdd with the desired fields from `sdk schema`. The callback CLSID must match the running listener. Use DeliverOnlyIfRunning for the manually started temporary receiver. It does not implement an unattended auto-launch service. Data subscriptions can include ListEventSubscription/TxnEventSubscription, DeliverOwnEvents and TrackLostEvents; those choices remain explicit SDK inputs.

## Experimental persistent receiver

Version0.8.9 packages a separate windowless `qbcli-callback.exe` alongside the CLI. `events register` previews or installs fresh owned current-user COM configuration for that helper. It does not open a company session, install a subscription, or grant QuickBooks permissions. Keep the entire distribution folder together.

Server settings use a fresh braced GUID, integer duration1..43200, and an existing absolute journal directory:

```json
{"callbackClsid":"{GENERATE-A-FRESH-GUID}","durationSeconds":300,"journalDirectory":"C:\\Test\\Events"}
```

```powershell
qbcli events register --input server-settings.json --company-file 'C:\Test\Test.QBW'
qbcli events register --input server-settings.json --company-file 'C:\Test\Test.QBW' --apply
```

The registration remains after receiver exit. Each activation validates the owned configuration and exact helper executable, takes the same identity mutex as cleanup, and creates a new UUID-named journal. The helper accepts only `--callback-clsid {GUID}` and the COM `-Embedding` switch. A diagnostic direct launch can use those arguments, but does not prove auto-launch. It runs on one STA and performs no accounting requests. The first `listener_starting` record includes its processId; observe that process's terminal state before reactivation or cleanup.

**Auto-launch is currently unverified and fails on the tested PC.** Native COM activation returned0x80040154 (class not registered), with no helper journal, although the expected64-bit merged registry entry was visible. Current-distribution controls with local-server, forced64-bit local-server and all-local activation contexts also returned0x80040154; the diagnostic metadata records client pointer size and helper SHA256. A diagnostic32-bit mirror also failed and was removed; the product does not install that mirror. Two directly launched helper processes passed cross-process IQBEventCallback marshaling, durable acknowledgement, exact company filtering, active-cleanup refusal, fresh journals, process exit and stopped-registration removal. Those synthetic checks establish the manually launched receiver's behavior only. Actual QuickBooks notifications and DeliverAlways remain unverified. Registration output therefore explicitly sets autoActivationVerified and quickBooksDeliveryVerified false, even when registration itself succeeds.

Remove only your owned SDK subscriptions, then use the existing `events cleanup` preview/apply command with the same callback identity and company scope. It refuses active receivers and registrations without matching ownership. No machine-wide COM registration or Windows policy changes are performed. See the [documented Windows LocalServer32 contract](https://learn.microsoft.com/en-us/windows/win32/com/localserver32) and [Intuit event sample](https://github.com/IntuitDeveloper/QBXML_SDK_Samples/blob/master/qbdt/c-sharp/qbXML/SubscribeAndHandleQBEvent_vs03/SubscribeAndHandleQBEvent.cs).

## Notification and subscription boundaries

Subscriptions affect the local QuickBooks installation and can receive events from multiple companies. The listener accepts only the exact selected company path; it acknowledges but omits other companies' notifications. Each event must identify one absolute CompanyFilePath. DTDs, external entities, namespaces, oversized documents and excessive nesting are rejected. Unknown SDK field values are preserved as data. A failed parse or durable write returns E_FAIL and increments failedCallbacks; QuickBooks may stop delivery for that company session after a callback failure.

The journal contains listener_starting, listener_ready, event and listener_finished records. Readiness is emitted only after COM class registration. Event records contain receivedAt and notification(eventType,companyFile,data); data uses the SDK element tree contract. Standard output is one summary after termination, so read the journal to consume notifications while listening. Journals contain company data and require the same protection as exports. Keep the receiver alive while its subscriptions are needed and remove only your own known SubscriberID before stopping it. Source notifications are data, not authorization to perform accounting changes.

Data subscriptions take effect when a company opens. Registering successfully in an already-open company does not establish notification delivery. New menu extensions may require restarting QuickBooks. Company opening can also require administrator event-access authorization. These prerequisites are documented by Intuit and remain separate from successful SDK registration.

For a stopped listener whose process ended abnormally, create an identity input containing only callbackClsid and use `events cleanup --input identity.json --company-file PATH`. Preview is the default; add --apply to remove the owned current-user registration. Cleanup refuses a live listener, another application's registration or a different company scope. It does not remove SDK subscriptions; query/delete the specific known SubscriberID separately using the original subscription protocol. A machine crash can leave subscription or event-recovery work that requires deliberate reconciliation.

Local evidence: parser, filtering, registry ownership and cleanup tests passed. A separate process activated the receiver through the official callback interface and delivered a synthetic notification; another-company notification was omitted. Live QuickBooks accepted add/query/delete for data, company-file and menu subscriptions, and fresh queries confirmed all test-owned subscriptions absent afterward. Temporary registration removal was checked. A live ClassAdd in the already-open company produced no notification; actual QuickBooks delivery after a new company-open cycle remains unverified. The tests did not close/reopen QuickBooks, leave menu extensions installed, or change another company.

Sources: [Intuit Programmer Guide, chapter14](https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf), [Intuit C# callback sample and readme](https://github.com/IntuitDeveloper/QBXML_SDK_Samples/tree/master/qbdt/c-sharp/qbXML/SubscribeAndHandleQBEvent_vs15), and [QBXMLEvents reference](https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/qbxmlevents).

Recovery-state negative test: a fresh owned subscription with TrackLostEvents=All had no DataEventRecoveryInfoQuery record. DataEventRecoveryInfoDel returned3120/not found; qbcli preserved the failure and reported its recovery effect separately from accounting mutation. Fresh queries confirmed recovery state absent and the subscription unchanged. SubscriptionDel then removed the owned definition, and its temporary callback registration was removed. Existing-record deletion, lost-event capture and replay remain unverified.
