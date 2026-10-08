/// Summa's books as Arca records (SUM0-012..014, SUM0-027, SUM0-044):
/// one object or event per file, deterministic paths, canonical content,
/// posted records immutable, derived values recomputed.
module Summa.Storage.Tests.FinancialRecordsTests

open Xunit
open Arca
open Summa.Ledger.Ledger
open Summa.Ledger.Payments
open Summa.Ledger.Reports
open Summa.Storage
open Summa.Storage.FinancialRecords
open Summa.Ledger.Money
open Summa.Storage.Tests.Books

let private stored (r: Receivables) =
    toRecords r
    |> Result.bind contents
    |> ok
    |> Map.toList
    |> List.mapi (fun i (p, (_, content)) ->
        ({ Path = RelativePath.parse p |> Result.defaultWith (failwithf "%A")
           Content = content
           Revision = Revision $"r{i}" }: StoredObject))

[<Fact>]
let ``every financial object and event is its own file at a deterministic path`` () =
    let paths = stored (full ()) |> List.map (fun o -> RelativePath.render o.Path) |> Set.ofList

    for expected in
        [ "records/summa.account/cash.json"
          "records/summa.period/2026-09.json"
          "records/summa.entry/2026/10/JE-000001.json"
          "records/summa.entry/2026/10/JE-000004.json"
          "records/summa.customer/CUST-ABC.json"
          "records/summa.invoice/2026/INV-001.json"
          "records/summa.delivery/INV-001.json"
          "records/summa.obligation/OBL-001.json"
          "records/summa.payment/2026/PAY-1.json"
          "records/summa.allocation/AL-1.json" ] do
        Assert.Contains(expected, paths)

    // The issued draft is gone; audit events are one file each.
    Assert.DoesNotContain("records/summa.draft/D-1.json", paths)
    let audits = paths |> Set.filter (fun p -> p.StartsWith "records/summa.audit/2026/10/")
    Assert.Equal((full ()).Books.Ledger.Audit.Length, audits.Count)
    // The same books always give the same files and bytes.
    Assert.True((stored (full ()) = stored (full ())))

[<Fact>]
let ``loading what was stored gives back the same books, with derived values recomputed`` () =
    let original = full ()
    let loaded = load (stored original)
    Assert.Empty loaded.Problems
    let r = loaded.State
    Assert.Equal<Map<string, Account>>(original.Books.Ledger.Accounts, r.Books.Ledger.Accounts)
    Assert.Equal<Map<string, PostedEntry>>(original.Books.Ledger.Entries, r.Books.Ledger.Entries)
    Assert.Equal<Map<string, string>>(original.Books.Ledger.Keys, r.Books.Ledger.Keys)
    Assert.True((original.Books.Invoices = r.Books.Invoices))
    Assert.True((original.Books.Obligations = r.Books.Obligations))
    Assert.True((original.Books.IssuedFrom = r.Books.IssuedFrom))
    Assert.True((original.Payments = r.Payments))
    Assert.Equal<Allocation list>(original.Allocations, r.Allocations)
    Assert.True((Set.ofList original.Books.Ledger.Audit = Set.ofList r.Books.Ledger.Audit))
    Assert.Equal(Closed, r.Books.Ledger.Periods[(2026, 9)])
    // "Reversed" is derived from the reversal, not stored on the original.
    Assert.Equal(Reversed "JE-000004", r.Books.Ledger.Entries["JE-000003"].State)
    // The books still balance and the invoice is still Paid.
    let tb = trialBalance "USD" (System.DateOnly(2026, 10, 31)) r.Books.Ledger
    Assert.Equal(tb.TotalDebits, tb.TotalCredits)
    Assert.Equal(Paid, status r r.Books.Invoices["INV-001"])

[<Fact>]
let ``posted records are immutable envelopes; mutable ones say so`` () =
    let byPath = stored (full ()) |> List.map (fun o -> RelativePath.render o.Path, o.Content) |> Map.ofList
    Assert.Contains("\"mutability\":\"immutable\"", byPath["records/summa.entry/2026/10/JE-000001.json"])
    Assert.Contains("\"mutability\":\"immutable\"", byPath["records/summa.invoice/2026/INV-001.json"])
    Assert.Contains("\"mutability\":\"immutable\"", byPath["records/summa.payment/2026/PAY-1.json"])
    Assert.Contains("\"mutability\":\"mutable\"", byPath["records/summa.customer/CUST-ABC.json"])
    // The reversed original's file is byte-for-byte what was first written.
    Assert.DoesNotContain("Reversed", byPath["records/summa.entry/2026/10/JE-000003.json"])

[<Fact>]
let ``stored content is untrusted: tampering, misplacement and broken books are reported`` () =
    let objects = stored (full ())
    let at path = objects |> List.findIndex (fun o -> RelativePath.render o.Path = path)

    let replace path (f: string -> string) =
        objects |> List.mapi (fun i o -> if i = at path then { o with Content = f o.Content } else o)

    // An entry that no longer balances.
    let unbalanced = replace "records/summa.entry/2026/10/JE-000001.json" (fun c -> let i = c.IndexOf "\"minor\":605000" in c.Substring(0, i) + "\"minor\":605001" + c.Substring(i + 15))
    Assert.NotEmpty (load unbalanced).Problems

    // An invoice whose content names another id than its file.
    let misnamed = replace "records/summa.invoice/2026/INV-001.json" (fun c -> c.Replace("\"invoiceId\":\"INV-001\"", "\"invoiceId\":\"INV-999\""))
    Assert.NotEmpty (load misnamed).Problems

    // An allocation whose payment is missing.
    let orphan = objects |> List.filter (fun o -> RelativePath.render o.Path <> "records/summa.payment/2026/PAY-1.json")
    Assert.Contains((load orphan).Problems, fun p -> (Diagnostics.describe p).Contains "payment PAY-1 is missing")

    // An unknown field (for example a token someone added) is refused.
    let extra = replace "records/summa.customer/CUST-ABC.json" (fun c -> c.Replace("\"active\":true", "\"active\":true,\"token\":\"x\""))
    Assert.NotEmpty (load extra).Problems

[<Fact>]
let ``every v0.2 receivables record round-trips and the books stay sound`` () =
    let original = withCredits ()
    Assert.Empty(Invariants.check original)
    let loaded = load (stored original)
    Assert.Empty loaded.Problems
    let r = loaded.State
    Assert.True((original.Credits = r.Credits))
    Assert.True((original.Deposits = r.Deposits))
    Assert.True((original.CreditMemos = r.CreditMemos))
    Assert.True((original.Refunds = r.Refunds))
    Assert.True((original.Reversals = r.Reversals))
    Assert.True((original.WriteOffs = r.WriteOffs))
    Assert.True((Set.ofList original.Applications = Set.ofList r.Applications))
    Assert.Equal(Some(usd 6000L), Summa.Ledger.Credits.remaining r (FromCredit "CR-1"))
    let paths = stored original |> List.map (fun o -> RelativePath.render o.Path) |> Set.ofList

    for expected in
        [ "records/summa.credit/CR-1.json"
          "records/summa.deposit/2026/DEP-1.json"
          "records/summa.credit-memo/2026/CM-1.json"
          "records/summa.refund/2026/RF-1.json"
          "records/summa.payment-reversal/PAY-3.json" ] do
        Assert.Contains(expected, paths)
