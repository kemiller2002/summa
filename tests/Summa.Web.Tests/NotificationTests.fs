/// Notification philosophy (WI-0044, v0.4 §32, SUM4-032): the work queue is
/// the one place Summa raises anything, only when action is required, a
/// deadline approaches, a financial operation failed or a meaningful
/// condition changed; items are derived, so they go when resolved and
/// never repeat.
module Summa.Web.Tests.NotificationTests

open System
open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting

let private at (day: DateTimeOffset) = { Now = day; Actor = "local-person" }
let private october = at (DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero))
let private on (y, m, d) = at (DateTimeOffset(y, m, d, 9, 0, 0, TimeSpan.Zero))

let private address hash : Limen.Routing.PageLocation =
    { Origin = "https://summa.example"; Path = "/app/"; Query = ""; Hash = hash }

let private go hash = LocationChanged(address hash)

let private runAt ctx (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private work ctx (model: Model) =
    match view (runAt ctx [ go "#/work" ] model |> fst) |> List.tryFind (fun (n, _) -> n = "workItems") with
    | Some(_, Items rows) -> rows |> List.map (List.map (fun (k, v) -> k, (match v with Text t -> t | Flag f -> string f | other -> $"%A{other}")) >> Map.ofList)
    | _ -> failwith "no work items"

let private kindsOf rows = rows |> List.map (fun (r: Map<string, string>) -> r["kind"], r["trigger"])

/// Acme (net 30) owing INV-0001, 250.00 USD, issued on 2026-10-07 and so
/// overdue from 2026-11-07.
let private books () =
    let started =
        runAt october [ Started(address ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}"""); Loaded None ] initial |> fst

    let opened =
        runAt october [ go "#/customers"; CustomerNameChanged "Acme"; CustomerAddressChanged "1 Main"; CustomerTermsChanged "30"; CustomerAdded; go "#/invoices/new?customer=CUST-0001" ] started
        |> fst

    let key = opened.Draft.Lines.Head.Key
    runAt october [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, "250"); DraftSubmitted; DraftIssued ] opened |> fst

let private triggers = set [ "Action required"; "Deadline approaching"; "Failed"; "Changed" ]

[<Fact>]
let ``SUM4-032: every item in the work queue says which of the four reasons raised it`` () =
    let rows = work (on (2026, 12, 15)) (books ())
    Assert.NotEmpty rows
    Assert.All(rows, fun r -> Assert.Contains(r["trigger"], triggers))
    Assert.Contains(("Overdue", "Action required"), kindsOf rows)
    // The dashboard counts the same queue.
    let home = runAt (on (2026, 12, 15)) [ go "#/" ] (books ()) |> fst
    let count = view home |> List.pick (fun (n, v) -> match n, v with "workCount", Value(Text t) -> Some t | _ -> None)
    Assert.Equal(string rows.Length, count)
    // Nothing is raised twice: each item is one condition.
    Assert.Equal(rows.Length, rows |> List.distinctBy (fun r -> r["kind"], r["text"]) |> List.length)

[<Fact>]
let ``SUM4-032: a promised date is raised as it approaches, not before; once missed it is a changed condition`` () =
    let promised =
        runAt (on (2026, 12, 10)) [ go "#/invoices/INV-0001?tab=payments"; PromiseAmountChanged "250.00"; PromiseByChanged "2026-12-20"; PromiseRecorded ] (books ()) |> fst

    let promiseItems ctx =
        work ctx promised |> kindsOf |> List.filter (fun (kind, _) -> kind.StartsWith "Payment promise")

    // Ten days out: nothing yet.
    Assert.Empty(promiseItems (on (2026, 12, 10)))
    // Three days out: a deadline approaching.
    Assert.Equal<(string * string) list>([ "Payment promise due", "Deadline approaching" ], promiseItems (on (2026, 12, 17)))
    // The date passed unpaid: the condition changed, and the approaching item is gone.
    Assert.Equal<(string * string) list>([ "Payment promise missed", "Changed" ], promiseItems (on (2026, 12, 21)))

[<Fact>]
let ``SUM4-032: items are derived from the books, so they go when resolved and a second look raises nothing new`` () =
    let model = books ()
    let december = on (2026, 12, 15)
    let first = work december model
    // Looking again changes nothing: there is nothing to acknowledge and nothing piles up.
    Assert.Equal<(string * string) list>(kindsOf first, kindsOf (work december (runAt december [ go "#/"; go "#/invoices" ] model |> fst)))
    // Paid, the overdue item is gone.
    let paid = runAt december [ go "#/invoices/INV-0001?tab=payments"; PaymentAmountChanged "250"; PaymentRecorded ] model |> fst
    Assert.DoesNotContain(("Overdue", "Action required"), work december paid |> kindsOf)

[<Fact>]
let ``SUM4-032: failures are raised as failures, changes as changes`` () =
    Assert.Equal("Failed", triggerText OperationFailed)
    Assert.Equal("Changed", triggerText ConditionChanged)
    Assert.Equal("Deadline approaching", triggerText DeadlineApproaching)
    Assert.Equal("Action required", triggerText ActionRequired)
