using System.Runtime.InteropServices.JavaScript;

// Entry point required by the WebAssembly SDK. The module is driven entirely
// through the [JSExport] methods below; nothing runs here.
return;

// Limen kernel side: owns WASM and browser interop.
//
// A pure marshalling shim. Each method forwards one Limen message, as JSON,
// to its page's engine in Summa.Web.Application and returns the engine's
// reply. It must never contain a decision: every rule lives in F#.
public partial class SummaWasm
{
    /// <summary>One message for the work-backlog page (web/).</summary>
    [JSExport]
    internal static string DispatchBacklog(string messageJson) =>
        Summa.Web.Application.Runtime.dispatchBacklog(messageJson);

    /// <summary>One message for the project-administration hub page (web-hub/).</summary>
    [JSExport]
    internal static string DispatchHub(string messageJson) =>
        Summa.Web.Application.Runtime.dispatchHub(messageJson);

    /// <summary>One message for the accounting application (app/).</summary>
    [JSExport]
    internal static string DispatchAccounting(string messageJson) =>
        Summa.Web.Application.Runtime.dispatchAccounting(messageJson);
}
