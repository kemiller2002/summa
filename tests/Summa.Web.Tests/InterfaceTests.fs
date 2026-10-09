/// The rest of the v0.4 interface (WI-0044): the editor's unsaved form
/// survives a refresh, tax is entered on the screens and never worked out
/// (INV-ADJ-005), and an invoice proposal, an agent's among them, is
/// inspected and then approved or abandoned by a person (SUM4-042, SUM4-043,
/// INV-AGENT-004).
module Summa.Web.Tests.InterfaceTests

open System
open Xunit
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting
module Routes = Summa.Web.Engine.Routes

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private address (hash: string) : Limen.Routing.PageLocation =
    { Origin = "https://summa.example"
      Path = "/app/"
      Query = ""
      Hash = hash }

let private localConfig = """{"environment":"local","environmentName":"local development"}"""

let private started () =
    run [ Started(address ""); ConfigurationRead(Ok localConfig); Loaded None ] initial |> fst

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | Some(_, Value(Number n)) -> string n
    | _ -> failwith $"no scalar view value {name}"

let private items (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Items rows) -> rows |> List.map Map.ofList
    | _ -> failwith $"no list view value {name}"

let private withCustomer (extra: Msg list) (model: Model) =
    run ([ LocationChanged(address "#/customers"); CustomerNameChanged "ABC Corp"; CustomerAddressChanged "1 Main Street"; CustomerEmailChanged "ap@abc.example" ] @ extra @ [ CustomerAdded ]) model
    |> fst

let private typed (model: Model) =
    let opened = run [ LocationChanged(address "#/invoices/new"); DraftCustomerChanged "CUST-0001" ] model |> fst
    let key = opened.Draft.Lines.Head.Key
    run [ LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "10"); LineRateChanged(key, "100.00") ] opened

/// The last form the engine asked the tab to keep.
let private kept (effects: AppEffect list) =
    effects |> List.choose (function KeepEditor form -> Some form | _ -> None) |> List.last

/// The books as this browser saved them last.
let private snapshot (effects: AppEffect list) =
    effects |> List.choose (function SaveBooks s -> Some s | _ -> None) |> List.last

// ---- The editor's unsaved form survives a refresh -------------------------------------------

[<Fact>]
let ``the page asks the tab for a kept editor form as it starts`` () =
    let _, effects = update ctx (Started(address "")) initial
    Assert.Contains(ReadEditor, effects)

[<Fact>]
let ``typing in the editor keeps the form in the tab, and a refresh restores it`` () =
    let books, saving =
        run [ Started(address ""); ConfigurationRead(Ok localConfig); Loaded None; LocationChanged(address "#/customers"); CustomerNameChanged "ABC Corp"; CustomerAddressChanged "1 Main"; CustomerEmailChanged "a@b.example"; CustomerAdded ] initial

    let typedModel, effects = typed books
    let form = kept effects
    Assert.True(form.IsSome)

    // The page is refreshed: the books come back from the browser, the form from the tab.
    let refreshed =
        run [ Started(address "#/invoices/new"); ConfigurationRead(Ok localConfig); EditorRead form; Loaded(Some(snapshot saving)) ] initial |> fst

    Assert.Equal<LineForm list>(typedModel.Draft.Lines, refreshed.Draft.Lines)
    Assert.Equal("CUST-0001", refreshed.Draft.CustomerId)
    Assert.Equal("Your unsaved changes were restored.", value "notice" refreshed)
    Assert.Equal("1,000.00 USD", value "draftTotal" refreshed)
    // Later rows never take a restored row's key.
    let more = run [ LineAdded ] refreshed |> fst
    Assert.Equal(more.Draft.Lines.Length, more.Draft.Lines |> List.map _.Key |> List.distinct |> List.length)

[<Fact>]
let ``a kept form also comes back when it is read after the books`` () =
    let books = started () |> withCustomer []
    let _, effects = typed books
    let reopened = run [ LocationChanged(address "#/"); LocationChanged(address "#/invoices/new") ] { books with KeptEditor = None } |> fst
    Assert.Equal("", reopened.Draft.CustomerId)
    let restored = run [ EditorRead(kept effects) ] reopened |> fst
    Assert.Equal("CUST-0001", restored.Draft.CustomerId)
    Assert.Equal("Assessment", restored.Draft.Lines.Head.Description)

[<Fact>]
let ``a kept form for a saved draft returns only to that draft`` () =
    let books = started () |> withCustomer []
    let saved, _ = typed books |> fst |> run [ DraftSubmitted ]
    Assert.Equal(Routes.Draft "D-0001", saved.Place)
    let key = saved.Draft.Lines.Head.Key
    let edited, effects = run [ LineDescriptionChanged(key, "Assessment, revised") ] saved
    let form = kept effects
    Assert.Equal("True", value "editorChanged" edited)

    let elsewhere = run [ LocationChanged(address "#/invoices/new"); EditorRead form ] saved |> fst
    Assert.Equal("", elsewhere.Draft.Lines.Head.Description)
    let back = run [ LocationChanged(address "#/drafts/D-0001") ] elsewhere |> fst
    Assert.Equal("Assessment, revised", back.Draft.Lines.Head.Description)

[<Fact>]
let ``saving or issuing the draft forgets the kept form`` () =
    let books = started () |> withCustomer []
    let model, _ = typed books
    let submitted, effects = run [ DraftSubmitted ] model
    Assert.Contains(KeepEditor None, effects)
    Assert.Equal("False", value "editorChanged" submitted)
    let _, issuing = run [ DraftIssued ] submitted
    Assert.Contains(KeepEditor None, issuing)

[<Fact>]
let ``discarding gives the saved draft back and forgets the kept form`` () =
    let books = started () |> withCustomer []
    let saved = typed books |> fst |> run [ DraftSubmitted ] |> fst
    let key = saved.Draft.Lines.Head.Key
    let edited = run [ LineRateChanged(key, "999.00") ] saved |> fst
    let discarded, effects = run [ EditorDiscarded ] edited
    Assert.Contains(KeepEditor None, effects)
    Assert.Equal("100.00", discarded.Draft.Lines.Head.Rate)
    Assert.Equal("False", value "editorChanged" discarded)

[<Fact>]
let ``a kept form that cannot be read is forgotten, not guessed at`` () =
    let books = started ()
    let after, effects = run [ EditorRead(Some "{not json") ] books
    Assert.Equal<AppEffect list>([ KeepEditor None ], effects)
    Assert.Equal(None, after.KeptEditor)
    Assert.Equal(None, decodeEditor """{"counter":1}""")

[<Fact>]
let ``the kept form reads back as it was written`` () =
    let form =
        { DraftId = Some "D-0007"
          CustomerId = "CUST-0002"
          Lines = [ { Key = "line-4"; Description = "Audit \"phase 1\""; Hours = "2.5"; Rate = "180"; Taxable = true } ]
          PurchaseOrder = "PO-9"
          Notes = "Thanks"
          Tax = { noTax with Code = "NY-8.875"; Amount = "39.94"; Inclusive = true; Rate = "8.875" } }

    Assert.Equal(Some(12, form), decodeEditor (encodeEditor 12 form))

// ---- Tax is entered on the screens (INV-ADJ-005) ---------------------------------------------

let private taxed (inclusive: bool) (model: Model) =
    let opened, _ = typed model
    let key = opened.Draft.Lines.Head.Key

    run
        ([ LineTaxableChanged(key, true)
           DraftTaxCodeChanged "NY-8.875"
           DraftTaxAmountChanged "88.75"
           DraftTaxRateChanged "8.875"
           DraftTaxJurisdictionChanged "New York"
           DraftTaxRateSourceChanged "NY DTF table, 2026-06"
           DraftTaxEvidenceChanged "ST-100 2026Q3" ]
         @ (if inclusive then [ DraftTaxInclusiveChanged true ] else []))
        opened
    |> fst

[<Fact>]
let ``a tax the person enters is saved on the draft, added to the total and posted to its liability`` () =
    let model = started () |> withCustomer [ CustomerTaxStatusChosen "taxable"; CustomerTaxJurisdictionChanged "New York" ] |> taxed false
    Assert.Equal("1,088.75 USD", value "draftTotal" model)
    Assert.Equal("Subject to tax in New York", value "draftCustomerTax" model)
    let reviewed = run [ DraftSubmitted ] model |> fst
    Assert.Equal(None, reviewed.Error)
    let draft = reviewed.Books.Value.Books.Drafts["D-0001"]
    let salesTax = reviewed.Books.Value.Books.Ledger.Accounts |> Map.findKey (fun _ a -> a.Code = "2300")

    match draft.Adjustments with
    | [ { Kind = Tax tax; Amount = amount } ] ->
        Assert.Equal("NY-8.875", tax.Code)
        Assert.Equal(salesTax, tax.AccountId)
        Assert.Equal(Some 88750, tax.RateHundredthBasisPoints)
        Assert.Equal(Some "ST-100 2026Q3", tax.Evidence)
        Assert.Equal(TaxExclusive, tax.Pricing)
        Assert.Equal(8875L, amount.Minor)
    | other -> failwith $"%A{other}"

    Assert.Equal(Taxable "NY-8.875", draft.Lines.Head.Tax)
    Assert.Contains("Tax NY-8.875", items "previewAdjustments" reviewed |> List.map (fun r -> r["label"]) |> List.map string |> String.concat " ")
    let issued = run [ DraftIssued ] reviewed |> fst
    Assert.Equal("1,088.75 USD", value "docTotal" issued)
    Assert.Equal<Scalar list>([ Text "Tax NY-8.875" ], items "docAdjustments" issued |> List.map (fun r -> r["label"]))
    // The tax reached its liability account.
    Assert.Equal("False", value "hasError" issued)

[<Fact>]
let ``a tax already inside the prices adds nothing to the total and says so on the document`` () =
    let model = started () |> withCustomer [] |> taxed true
    Assert.Equal("1,000.00 USD", value "draftTotal" model)
    let issued = run [ DraftSubmitted; DraftIssued ] model |> fst
    Assert.Equal("1,000.00 USD", value "docTotal" issued)
    Assert.Equal<Scalar list>([ Text "Tax NY-8.875 (included in the prices above)" ], items "docAdjustments" issued |> List.map (fun r -> r["label"]))

[<Fact>]
let ``the editor's tax round-trips through a saved draft`` () =
    let model = started () |> withCustomer [] |> taxed false
    let saved = run [ DraftSubmitted ] model |> fst
    let reopened = run [ LocationChanged(address "#/invoices"); LocationChanged(address "#/drafts/D-0001") ] { saved with Draft = { saved.Draft with DraftId = None } } |> fst
    Assert.Equal(model.Draft.Tax, reopened.Draft.Tax)
    Assert.True(reopened.Draft.Lines.Head.Taxable)

[<Fact>]
let ``tax entered wrongly is refused with a reason; Summa never fills it in`` () =
    let model = started () |> withCustomer []
    let opened, _ = typed model
    let noCode = run [ DraftTaxAmountChanged "10.00"; DraftSubmitted ] opened |> fst
    Assert.Equal("A tax needs its code, such as NY-8.875 or VAT-STD.", value "error" noCode)
    let noAmount = run [ DraftTaxAmountChanged ""; DraftTaxCodeChanged "VAT"; DraftSubmitted ] opened |> fst
    Assert.Equal("Tax VAT needs an amount such as 12.50. Summa does not work tax out.", value "error" noAmount)
    let badRate = run [ DraftTaxCodeChanged "VAT"; DraftTaxAmountChanged "5"; DraftTaxRateChanged "eight"; DraftSubmitted ] opened |> fst
    Assert.Equal("Tax VAT's rate is a percentage such as 8.875.", value "error" badRate)
    let noAccount = run [ DraftTaxCodeChanged "VAT"; DraftTaxAmountChanged "5"; DraftTaxAccountChosen "9999"; DraftSubmitted ] opened |> fst
    Assert.Equal("Tax VAT needs a liability account; account 9999 is not in these books.", value "error" noAccount)
    // A tax with no line marked taxable: Summa does not decide which lines are.
    let noLine = run [ DraftTaxCodeChanged "VAT"; DraftTaxAmountChanged "5"; DraftSubmitted ] opened |> fst
    // It is saved as a draft, and the blocker says why it cannot be issued.
    let said = (items "blockers" noLine |> List.map (fun r -> string r["explanation"] + " " + string r["resolution"])) @ [ value "error" noLine ] |> String.concat " "
    Assert.Contains("marked taxable", said)
    // Only liability accounts are offered.
    Assert.Equal<Scalar list>([ Text "2100"; Text "2200"; Text "2300" ], items "taxAccountOptions" opened |> List.map (fun r -> r["value"]))

[<Fact>]
let ``a customer's tax status is what the person records; an exemption needs its evidence`` () =
    let model = started ()
    let noEvidence = run [ LocationChanged(address "#/customers"); CustomerNameChanged "Exempt Co"; CustomerTaxStatusChosen "exempt"; CustomerAdded ] model |> fst
    Assert.Equal("An exempt customer needs the certificate or reference that shows it.", value "error" noEvidence)
    Assert.Equal("True", value "customerTaxExempt" noEvidence)
    let exempt = run [ CustomerTaxEvidenceChanged "ST-119 #4471"; CustomerTaxJurisdictionChanged "NY"; CustomerAdded ] noEvidence |> fst
    Assert.Equal(TaxExempt("ST-119 #4471", Some "NY"), exempt.Books.Value.Books.Customers["CUST-0001"].Tax)
    let plain = started () |> withCustomer []
    Assert.Equal(TaxNotAssessed, plain.Books.Value.Books.Customers["CUST-0001"].Tax)
    // An unknown status is not taken.
    let unknown = run [ CustomerTaxStatusChosen "maybe" ] model |> fst
    Assert.Equal("not-assessed", unknown.Customer.TaxStatus)

// ---- Proposals: inspect, then approve or abandon (SUM4-042, SUM4-043) -----------------------

let private agent: Context =
    { Who = "summa-agent"
      When = DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero)
      Source = "agent"
      CorrelationId = None
      Provenance =
        Some
            { ActorKind = "agent"
              Agent = Some { Provider = "anthropic"; Model = "claude"; Runtime = "claude-code" }
              ExecutionId = Some "EXE-summa.42"
              SourceSystem = None
              SourceId = None
              Reason = None } }

/// Books with a customer and a proposal an agent prepared for them.
let private proposed () =
    let model = started () |> withCustomer []
    let books = model.Books.Value
    let revenue = books.Books.Ledger.Accounts |> Map.findKey (fun _ a -> a.Code = "4000")

    let request: Summa.Ledger.Billing.ProposalRequest =
        { ProposalId = "P-1"
          CustomerId = "CUST-0001"
          EngagementId = None
          Currency = "USD"
          Time = []
          Grouping = []
          FixedFee = false
          Milestones = []
          Expenses = []
          Manual =
            [ { Description = "Architecture review"
                QuantityThousandths = 4000L
                UnitPrice = { Currency = "USD"; Minor = 20000L }
                RevenueAccountId = revenue
                Project = None
                WorkItem = None
                Discount = None
                Source = ManualLine
                Rate = None
                Tax = NotAssessed } ]
          Accounts = { TimeRevenue = revenue; FeeRevenue = revenue; ReimbursedExpenses = revenue } }

    match Summa.Ledger.Billing.propose agent request books with
    | Ok withProposal -> { model with Books = Some withProposal }
    | Error problems -> failwith $"%A{problems}"

[<Fact>]
let ``proposals are listed with who prepared them`` () =
    let listed = run [ LocationChanged(address "#/proposals") ] (proposed ()) |> fst
    Assert.Equal("True", value "onProposals" listed)

    match items "proposals" listed with
    | [ row ] ->
        Assert.Equal(Text "P-1", row["id"])
        Assert.Equal(Text "#/proposals/P-1", row["href"])
        Assert.Equal(Text "Agent summa-agent (anthropic claude), run EXE-summa.42", row["preparedBy"])
        Assert.Equal(Text "800.00 USD", row["total"])
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a proposal shows its lines, how it was made and who contributed, before anyone decides`` () =
    let shown = run [ LocationChanged(address "#/proposals/P-1") ] (proposed ()) |> fst
    Assert.Equal("True", value "onProposal" shown)
    Assert.Equal("True", value "propByAgent" shown)
    Assert.Equal("Proposed", value "propState" shown)
    Assert.Equal<Scalar list>([ Text "Architecture review" ], items "propLines" shown |> List.map (fun r -> r["description"]))
    Assert.Equal<Scalar list>([ Text "Architecture review: typed by hand" ], items "propSelected" shown |> List.map (fun r -> r["text"]))
    Assert.Equal("time is not grouped: one line per entry", value "propGrouping" shown)
    Assert.Contains("tax: nothing is recorded for the customer", items "propAssumptions" shown |> List.map (fun r -> string r["text"]) |> String.concat " ")
    Assert.Equal<Scalar list>([ Text "Agent summa-agent (anthropic claude), run EXE-summa.42" ], items "propContributions" shown |> List.map (fun r -> r["who"]))
    Assert.Equal<Scalar list>([ Text "P-1" ], items "propActions" shown |> List.map (fun r -> r["id"]))

[<Fact>]
let ``a person approves the proposal and it is issued as one invoice, the person's contribution appended`` () =
    let shown = run [ LocationChanged(address "#/proposals/P-1") ] (proposed ()) |> fst
    let approved, effects = run [ ProposalApproved "P-1" ] shown
    Assert.Equal(None, approved.Error)
    Assert.Contains(Navigate(Limen.Routing.NavigationEffect.Push "/invoices/INV-0001"), effects)
    Assert.Equal("800.00 USD", value "docTotal" approved)
    let proposal = approved.Books.Value.Books.Proposals["P-1"]
    Assert.Equal(Accepted "INV-0001", proposal.State)
    Assert.Equal<string list>([ "summa-agent"; "local-person" ], proposal.Contributions |> List.map _.Who)
    Assert.Equal(Some "human", proposal.Contributions |> List.last |> _.Provenance |> Option.map _.ActorKind)
    // Decided: the proposal offers no actions, and points at its invoice.
    let after = run [ LocationChanged(address "#/proposals/P-1") ] approved |> fst
    Assert.Empty(items "propActions" after)
    Assert.Equal("#/invoices/INV-0001", value "propIssuedHref" after)

[<Fact>]
let ``abandoning a proposal frees its sources, and an abandoned proposal cannot be approved`` () =
    let shown = run [ LocationChanged(address "#/proposals/P-1") ] (proposed ()) |> fst
    let abandoned = run [ ProposalAbandoned "P-1" ] shown |> fst
    Assert.Equal(Abandoned, abandoned.Books.Value.Books.Proposals["P-1"].State)
    Assert.Equal("Abandoned", value "propState" abandoned)
    let refused = run [ ProposalApproved "P-1" ] abandoned |> fst
    Assert.StartsWith("Proposal P-1 cannot be approved", value "error" refused)
    Assert.True(refused.Books.Value.Books.Invoices.IsEmpty)

[<Fact>]
let ``approving needs the right to issue; abandoning the right to propose`` () =
    Assert.Equal(Some Summa.Access.Access.IssueInvoice, bookCommand (ProposalApproved "P-1") |> Option.map fst)
    Assert.Equal(Some Summa.Access.Access.ProposeInvoice, bookCommand (ProposalAbandoned "P-1") |> Option.map fst)

[<Fact>]
let ``a proposal the books do not hold is not found`` () =
    let missing = run [ LocationChanged(address "#/proposals/P-404") ] (proposed ()) |> fst
    Assert.Equal("True", value "isNotFound" missing)


[<Fact>]
let ``a checkbox in the editor reports whether it is checked, so the form's submit cannot flip it`` () =
    let decoded checkedState =
        match Summa.Web.Application.AccountingWire.decode $"""{{"kind":"Event","event":{{"name":"lineTaxableChanged","key":"line-1","value":"on","checked":{checkedState}}}}}""" with
        | Summa.Web.Application.AccountingWire.Event(_, key, value) -> key, value
        | _ -> failwith "not an event"

    Assert.Equal((Some "line-1", Some "true"), decoded "true")
    Assert.Equal((Some "line-1", Some "false"), decoded "false")
    // Firing twice with the same state leaves it as it is.
    let model = started () |> withCustomer [] |> typed |> fst
    let key = model.Draft.Lines.Head.Key
    let twice = run [ LineTaxableChanged(key, true); LineTaxableChanged(key, true) ] model |> fst
    Assert.True(twice.Draft.Lines.Head.Taxable)

[<Fact>]
let ``a person changes a proposed rate with a reason, and the change is appended to the agent's work`` () =
    let shown = run [ LocationChanged(address "#/proposals/P-1") ] (proposed ()) |> fst
    let noReason = run [ ProposalLineChosen "0"; ProposalRateChanged "250.00"; ProposalRateOverridden "P-1" ] shown |> fst
    Assert.Equal("Say why the rate changes; the reason is kept with the proposal.", value "error" noReason)
    let changed = run [ ProposalReasonChanged "agreed senior rate"; ProposalRateOverridden "P-1" ] noReason |> fst
    Assert.Equal(None, changed.Error)
    Assert.Equal("1,000.00 USD", value "propTotal" changed)
    Assert.Equal<Scalar list>([ Text "proposed"; Text "rate overridden" ], items "propContributions" changed |> List.map (fun r -> r["what"]))
    Assert.Contains("changed by local-person: agreed senior rate", items "propRates" changed |> List.map (fun r -> string r["text"]) |> String.concat " ")
    Assert.Equal(Some Summa.Access.Access.OverrideRate, bookCommand (ProposalRateOverridden "P-1") |> Option.map fst)
    // Then approved, at the changed rate.
    let approved = run [ ProposalApproved "P-1" ] changed |> fst
    Assert.Equal("1,000.00 USD", value "docTotal" approved)

[<Fact>]
let ``leaving a new invoice and coming back keeps what was typed`` () =
    let model = started () |> withCustomer [] |> typed |> fst
    let back = run [ LocationChanged(address "#/customers"); LocationChanged(address "#/invoices/new") ] model |> fst
    Assert.Equal("Assessment", back.Draft.Lines.Head.Description)
    // A new invoice for another customer starts empty.
    let other = run [ LocationChanged(address "#/customers"); LocationChanged(address "#/invoices/new?customer=CUST-0009") ] model |> fst
    Assert.Equal("", other.Draft.Lines.Head.Description)
