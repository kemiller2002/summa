/// Room for tax, without calculating it (WI-0040, INV-ADJ-005): taxable and
/// non-taxable lines, tax category or code, the customer's exempt status,
/// jurisdiction, where the rate came from, the tax amount, tax-inclusive or
/// tax-exclusive pricing, and the evidence. Summa never infers a tax: it
/// keeps what the person said and refuses what contradicts itself.
module Summa.Tests.TaxTests

open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Issuance
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

let private salesTax =
    { Id = "salestax"; Code = "2300"; Name = "Sales Tax Payable"; Type = Liability; Active = true }

let private taxBooks (customer: Customer) =
    { books with Ledger = addAccount context salesTax books.Ledger |> ok } |> saveCustomer context customer

let private blockersOf (draft: DraftInvoice) (b: Books) =
    blockers (start (saveDraft context draft b |> ok)) request |> List.map _.Code

let private issue' (draft: DraftInvoice) (b: Books) =
    start (saveDraft context draft b |> ok) |> submitForReview context request |> ok |> issueInvoice context request |> ok

let private nyTax =
    { Code = "NY-8.875"
      AccountId = "salestax"
      Jurisdiction = Some "New York, NY"
      RateSource = Some "NYS Pub 718, 2026-09"
      RateHundredthBasisPoints = Some 88750
      Evidence = Some "ST-100 filing 2026-Q4"
      Pricing = TaxExclusive }

let private taxable = { consulting 10000L 10000L with Tax = Taxable "services" }

[<Fact>]
let ``nothing is inferred: lines and customers are not assessed until someone says, and no tax is ever added`` () =
    Assert.Equal(NotAssessed, (consulting 10000L 10000L).Tax)
    Assert.Equal(TaxNotAssessed, abc.Tax)

    // Taxable lines with no tax: Summa issues exactly what was entered.
    let r, invoice = issue' (draftFor [ taxable ]) (taxBooks abc)
    Assert.Equal(usd 100000L, invoice.Total)
    Assert.DoesNotContain(r.Books.Ledger.Entries[invoice.JournalEntryId].Lines, fun l -> l.AccountId = "salestax")

[<Fact>]
let ``a tax says where it applies, at what rate from where, and on what evidence; and it needs a taxable line`` () =
    let withTax lines = { draftFor lines with Adjustments = [ { Kind = Tax nyTax; Label = "Sales tax"; Amount = usd 8875L } ] }

    // A tax with no line marked taxable contradicts itself.
    Assert.Equal<string list>([ "invalid-adjustment" ], blockersOf (withTax [ consulting 10000L 10000L ]) (taxBooks abc))
    Assert.Equal<string list>([ "invalid-adjustment" ], blockersOf (withTax [ { consulting 10000L 10000L with Tax = NonTaxable "exported service" } ]) (taxBooks abc))

    let r, invoice = issue' (withTax [ taxable ]) (taxBooks abc)
    Assert.Equal(usd 108875L, invoice.Total)

    match invoice.Adjustments with
    | [ { Kind = Tax kept } ] -> Assert.Equal(nyTax, kept)
    | other -> failwith $"%A{other}"

    Assert.Contains(r.Books.Ledger.Entries[invoice.JournalEntryId].Lines, fun l -> l.AccountId = "salestax" && l.Side = Credit(usd 8875L))

[<Fact>]
let ``an exempt customer is not charged tax, and the reason is the evidence recorded`` () =
    let exempt = { abc with Tax = TaxExempt("NY ST-119.1 #4471", Some "New York") }
    let draft = { draftFor [ taxable ] with Adjustments = [ { Kind = Tax nyTax; Label = "Sales tax"; Amount = usd 8875L } ] }
    Assert.Equal<string list>([ "invalid-adjustment" ], blockersOf draft (taxBooks exempt))

    let explained =
        blockers (start (saveDraft context draft (taxBooks exempt) |> ok)) request |> List.map _.Explanation |> String.concat " "

    Assert.Contains("ST-119.1 #4471", explained)

    // Without a tax the exempt customer's invoice issues as entered.
    Assert.Empty(blockersOf (draftFor [ taxable ]) (taxBooks exempt))

[<Fact>]
let ``a tax inside the prices leaves the total alone and moves from revenue to its liability`` () =
    let inclusive = { nyTax with Pricing = TaxInclusive }
    let draft = { draftFor [ taxable ] with Adjustments = [ { Kind = Tax inclusive; Label = "Sales tax included"; Amount = usd 8153L } ] }
    let r, invoice = issue' draft (taxBooks abc)
    Assert.Equal(usd 100000L, invoice.Total)

    let entry = r.Books.Ledger.Entries[invoice.JournalEntryId]
    Assert.Contains(entry.Lines, fun l -> l.AccountId = "revenue" && l.Side = Credit(usd 100000L))
    Assert.Contains(entry.Lines, fun l -> l.AccountId = "revenue" && l.Side = Debit(usd 8153L))
    Assert.Contains(entry.Lines, fun l -> l.AccountId = "salestax" && l.Side = Credit(usd 8153L))

    // The document says it is included, so no reader adds it again.
    let html = Documents.render (Documents.ofInvoice invoice) |> ok
    Assert.Contains("Sales tax included (included in the prices above)", html)

    // More tax inside the prices than the taxable lines charge cannot be.
    let tooMuch = { draft with Adjustments = [ { Kind = Tax inclusive; Label = "Sales tax included"; Amount = usd 100001L } ] }
    Assert.Equal<string list>([ "invalid-adjustment" ], blockersOf tooMuch (taxBooks abc))
