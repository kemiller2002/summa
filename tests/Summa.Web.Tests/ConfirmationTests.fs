/// High-impact actions ask first and say what will happen (WI-0044, v0.4 §38):
/// the sentence each confirmation shows, from the books as they stand.
module Summa.Web.Tests.ConfirmationTests

open System
open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private go (hash: string) =
    LocationChanged
        { Origin = "https://summa.example"
          Path = "/app/"
          Query = ""
          Hash = hash }

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | _ -> failwith $"no text view value {name}"

/// ABC Corp with a reviewed draft for 1,500.00, open in the editor.
let private drafted () =
    let started =
        run
            [ Started(match go "" with LocationChanged p -> p | _ -> failwith "page")
              ConfigurationRead(Ok """{"environment":"local","environmentName":"local development"}""")
              Loaded None
              go "#/customers"
              CustomerNameChanged "ABC Corp"
              CustomerAddressChanged "1 Main Street"
              CustomerAdded
              go "#/invoices/new?customer=CUST-0001" ]
            initial
        |> fst

    let key = started.Draft.Lines.Head.Key
    run [ LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "10"); LineRateChanged(key, "150.00"); DraftSubmitted ] started |> fst

[<Fact>]
let ``SUM4-038: issuing says who it goes to, for how much, and that the number never changes`` () =
    Assert.Equal(
        "Issue this invoice to ABC Corp for 1,500.00 USD? It gets the next invoice number, which never changes, and is recorded in the books. To correct it afterwards, issue a credit memo.",
        value "confirmIssueText" (drafted ())
    )

[<Fact>]
let ``SUM4-038: a credit memo says how much it credits, against which invoice, and that it cannot be withdrawn`` () =
    let issued = run [ DraftIssued ] (drafted ()) |> fst
    let onInvoice = run [ go "#/invoices/INV-0001?tab=payments" ] issued |> fst
    Assert.Equal("Issue a credit memo against INV-2026-0001? Enter the amount to credit first.", value "confirmCreditText" onInvoice)
    let typed = run [ CreditAmountChanged "200.00" ] onInvoice |> fst
    Assert.Equal(
        "Issue a credit memo for 200.00 USD against INV-2026-0001? It reduces what ABC Corp owes, is recorded in the books and is sent as its own document. It cannot be withdrawn.",
        value "confirmCreditText" typed
    )

[<Fact>]
let ``SUM4-038: closing, locking and reopening a month each say what they change`` () =
    let month = run [ go "#/periods/2026-09" ] (drafted ()) |> fst
    Assert.Equal("Close September 2026? Nothing more can be posted to September 2026, so its reports stay as they are, until an accountant reopens it.", value "confirmCloseText" month)
    Assert.Equal("Lock September 2026? It stays closed, and the lock says its numbers are final, for example once they are filed or handed to the accountant.", value "confirmLockText" month)
    Assert.Equal("Reopen September 2026? Entries can be posted to it again, so reports already given for September 2026 may no longer match the books.", value "confirmReopenText" month)
