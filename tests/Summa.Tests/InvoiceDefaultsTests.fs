/// What choosing a customer proposes for a new invoice (WI-0044, v0.4 §5).
module Summa.Tests.InvoiceDefaultsTests

open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Billing
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

let private engagement id terms : Engagement =
    { Id = id
      CustomerId = abc.Id
      Name = $"Engagement {id}"
      Currency = "USD"
      FixedFee = None
      Milestones = []
      Terms = terms }

let private withRates (rates: (RateScope * Money) list) (b: Books) = { b with Rates = { b.Rates with Rates = rates } }
let private withEngagements (es: Engagement list) (b: Books) = { b with Engagements = es |> List.map (fun e -> e.Id, e) |> Map.ofList }

[<Fact>]
let ``SUM4-005: a customer proposes its billing details, its terms and the rate the card resolves for it`` () =
    let b = books |> withRates [ ForCustomer abc.Id, usd 17500L; Everyone, usd 15000L ]
    let d = invoiceDefaults b (Net 30) abc.Id None |> Option.get
    Assert.Equal(abc.BillingName, d.BillingName)
    Assert.Equal(abc.BillingAddress, d.BillingAddress)
    Assert.Equal(abc.Email, d.Email)
    Assert.Equal((abc.DefaultTerms.Value, CustomerTerms), d.Terms)
    Assert.Equal(Some(usd 17500L, CustomerDefault), d.Rate |> Option.map (fun (m, p) -> m, p.RateSource))
    Assert.Empty d.Engagements
    Assert.Equal(None, d.Engagement)
    // An unknown customer proposes nothing.
    Assert.Equal(None, invoiceDefaults b (Net 30) "CUST-NONE" None)

[<Fact>]
let ``SUM4-005: the only engagement is proposed with its terms and rate; with several, none is chosen until asked`` () =
    let one = engagement "ENG-1" (Some(Net 10))
    let b = books |> withEngagements [ one ] |> withRates [ ForEngagement "ENG-1", usd 20000L; ForCustomer abc.Id, usd 17500L ]
    let d = invoiceDefaults b (Net 30) abc.Id None |> Option.get
    Assert.Equal(Some "ENG-1", d.Engagement |> Option.map _.Id)
    Assert.Equal((Net 10, EngagementTerms), d.Terms)
    Assert.Equal(Some(usd 20000L, EngagementAgreement), d.Rate |> Option.map (fun (m, p) -> m, p.RateSource))

    let several = b |> withEngagements [ one; engagement "ENG-2" None ]
    let undecided = invoiceDefaults several (Net 30) abc.Id None |> Option.get
    Assert.Equal(None, undecided.Engagement)
    Assert.Equal(2, undecided.Engagements.Length)
    Assert.Equal(Some(usd 17500L), undecided.Rate |> Option.map fst)
    Assert.Equal(Some "ENG-2", invoiceDefaults several (Net 30) abc.Id (Some "ENG-2") |> Option.bind _.Engagement |> Option.map _.Id)

[<Fact>]
let ``SUM4-005: with no rate that applies, none is invented; with no customer terms, the organization's apply`` () =
    let b = { books with Customers = books.Customers.Add(abc.Id, { abc with DefaultTerms = None }) }
    let d = invoiceDefaults b DueOnReceipt abc.Id None |> Option.get
    Assert.Equal(None, d.Rate)
    Assert.Equal((DueOnReceipt, SystemTerms), d.Terms)
