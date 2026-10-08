/// The accounting application's screens for payments, customers, credit
/// memos and engagements (WI-0030 slice 2), each opened from its own link
/// (WI-0043), and the credit memo issued from an invoice through Summa's
/// domain commands.
module Summa.Web.Tests.ScreensTests

open System
open Xunit
open Limen.Routing
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting
module Routes = Summa.Web.Engine.Routes

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private address hash : PageLocation =
    { Origin = "https://summa.example"
      Path = "/app/"
      Query = ""
      Hash = hash }

let private go hash = LocationChanged(address hash)

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | Some(_, Value(Number n)) -> string n
    | _ -> failwith $"no scalar view value {name}"

let private items (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Items rows) -> rows
    | _ -> failwith $"no list view value {name}"

let private field name (row: (string * Scalar) list) =
    match row |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Text t) -> t
    | Some(_, Flag f) -> string f
    | other -> failwith $"%A{other}"

/// Acme with one 1,000.00 invoice, 400.00 paid by check 1042; Beta with none.
let private books () =
    let customer name =
        [ go "#/customers"; CustomerNameChanged name; CustomerAddressChanged "1 Main Street"; CustomerAdded ]

    let opened =
        run [ Started(address ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}"""); Loaded None ] initial
        |> fst
        |> run (customer "Acme" @ customer "Beta" @ [ go "#/invoices/new?customer=CUST-0001" ])
        |> fst

    let key = opened.Draft.Lines.Head.Key

    run
        [ LineDescriptionChanged(key, "Assessment")
          LineRateChanged(key, "1000")
          DraftSubmitted
          DraftIssued
          InvoiceTabChosen "payments"
          PaymentAmountChanged "400"
          PaymentReferenceChanged "CHK-1042"
          PaymentMethodChanged "check"
          PaymentRecorded ]
        opened
    |> fst

// ---- Credit memos ---------------------------------------------------------------------------

[<Fact>]
let ``a credit memo issued from an invoice is applied to it and has its own document`` () =
    let model = books ()
    let credited, effects = run [ CreditAmountChanged "150"; CreditReasonChanged "Workshop shortened"; CreditMemoIssued ] model
    Assert.Equal("Credit memo CM-0001 issued and applied.", value "notice" credited)
    Assert.Equal("450.00 USD", value "detailOutstanding" credited)
    Assert.Equal<string list>([ "Credit memo CM-0001" ], items "detailCredits" credited |> List.map (field "source"))
    Assert.Equal("", value "creditAmount" credited)
    Assert.Contains(effects, function SaveBooks _ -> true | _ -> false)

    let memo = run [ go "#/credit-memos/CM-0001" ] credited |> fst
    Assert.Equal("True", value "onCreditMemo" memo)
    Assert.Equal("True", value "cmHasDocument" memo)
    Assert.Equal("Workshop shortened", value "cmReason" memo)
    Assert.Equal("150.00 USD", value "cmAmount" memo)
    Assert.Equal("0.00 USD", value "cmRemaining" memo)
    Assert.Equal("#/invoices/INV-0001", value "cmInvoiceHref" memo)
    Assert.Equal(value "docIssuer" (run [ go "#/invoices/INV-0001" ] credited |> fst), value "cmIssuer" memo)

[<Fact>]
let ``a credit memo is refused without a reason, an amount, or for more than is owed`` () =
    let model = books ()
    Assert.Equal("A credit memo needs a reason the customer will read.", value "error" (run [ CreditAmountChanged "10"; CreditMemoIssued ] model |> fst))
    Assert.Equal("Enter the amount to credit, such as 150.00.", value "error" (run [ CreditReasonChanged "x"; CreditMemoIssued ] model |> fst))
    let tooMuch, effects = run [ CreditAmountChanged "601"; CreditReasonChanged "x"; CreditMemoIssued ] model
    Assert.Equal("That is more than the 600.00 USD still owed.", value "error" tooMuch)
    Assert.Empty effects
    // Nothing was issued: the memo and its application are one transition.
    Assert.Empty tooMuch.Books.Value.CreditMemos

[<Fact>]
let ``the credit memo list filters by customer in the address`` () =
    let credited = run [ CreditAmountChanged "150"; CreditReasonChanged "Goodwill"; CreditMemoIssued; go "#/credit-memos" ] (books ()) |> fst
    Assert.Equal<string list>([ "CM-0001" ], items "creditMemos" credited |> List.map (field "id"))
    let beta, effects = run [ CreditMemosCustomerChosen "CUST-0002" ] credited
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/credit-memos?customer=CUST-0002") ], effects)
    Assert.Empty(items "creditMemos" beta)

// ---- Payments ---------------------------------------------------------------------------------

[<Fact>]
let ``payments are listed, filtered in the address and open to what they paid`` () =
    let model = run [ go "#/payments" ] (books ()) |> fst
    Assert.Equal<string list>([ "PAY-0001" ], items "payments" model |> List.map (field "id"))
    Assert.Equal("Check", items "payments" model |> List.head |> field "method")
    let searched, effects = run [ PaymentSearchChanged "1042" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/payments?q=1042") ], effects)
    Assert.Single(items "payments" searched) |> ignore
    Assert.Empty(items "payments" (run [ PaymentUnappliedToggled ] model |> fst))
    Assert.Equal("1 of 1 payments", value "paymentCountText" model)

    let payment = run [ go "#/payments/PAY-0001" ] model |> fst
    Assert.Equal("400.00 USD", value "payAmount" payment)
    Assert.Equal("0.00 USD", value "payUnapplied" payment)
    Assert.Equal<string list>([ "#/invoices/INV-0001" ], items "payAllocations" payment |> List.map (field "href"))
    Assert.Equal("True", value "isNotFound" (run [ go "#/payments/PAY-9999" ] model |> fst))

// ---- One customer -----------------------------------------------------------------------------

[<Fact>]
let ``a customer opens from its link with tabs for invoices, payments and credits`` () =
    let model = run [ go "#/customers/CUST-0001" ] (books ()) |> fst
    Assert.Equal("Acme", value "custName" model)
    Assert.Equal("600.00 USD", value "custBalance" model)
    Assert.Equal("#/invoices/new?customer=CUST-0001", value "custNewInvoiceHref" model)
    Assert.Equal<string list>([ "INV-0001" ], items "custInvoices" model |> List.map (field "id"))
    let payments, effects = run [ CustomerTabChosen "payments" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/customers/CUST-0001?tab=payments") ], effects)
    Assert.Equal("True", value "onCustPaymentsTab" payments)
    Assert.Equal<string list>([ "PAY-0001" ], items "custPayments" payments |> List.map (field "id"))
    Assert.Equal("True", value "isNotFound" (run [ go "#/customers/CUST-9999" ] model |> fst))
    Assert.Equal("#/customers/CUST-0001", items "customers" (run [ go "#/customers" ] model |> fst) |> List.head |> field "href")

// ---- Engagements ------------------------------------------------------------------------------

[<Fact>]
let ``an engagement is added through Billing and opened at its own address`` () =
    let model = run [ go "#/engagements" ] (books ()) |> fst
    Assert.Equal("Choose the customer the engagement is with.", value "error" (run [ EngagementAdded ] model |> fst))
    Assert.Equal("A fixed fee is an amount such as 5000.00, or empty for hourly work.", value "error" (run [ EngagementCustomerChanged "CUST-0001"; EngagementNameChanged "Retainer"; EngagementFeeChanged "lots"; EngagementAdded ] model |> fst))

    let added, effects = run [ EngagementCustomerChanged "CUST-0001"; EngagementNameChanged "Retainer"; EngagementFeeChanged "5000"; EngagementAdded ] model
    Assert.Contains(Navigate(NavigationEffect.Push "/engagements/ENG-0001"), effects)
    Assert.Equal("True", value "onEngagement" added)
    Assert.Equal("Retainer", value "engName" added)
    Assert.Equal("5,000.00 USD", value "engFee" added)
    Assert.Equal("#/customers/CUST-0001", value "engCustomerHref" added)
    let listed = run [ go "#/engagements?customer=CUST-0002" ] added |> fst
    Assert.Empty(items "engagements" listed)
    Assert.Single(items "engagements" (run [ EngagementsCustomerChosen "" ] listed |> fst)) |> ignore
