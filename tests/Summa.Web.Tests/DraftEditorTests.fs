/// The draft editor (WI-0029 slice 23): saving from the editor keeps what
/// the editor does not show, a save made on an older version of the draft is
/// refused, and line details and typed client references are edited there
/// (INV-DATA-008, INV-DATA-014, INV-UI-005, INV-UI-007).
module Summa.Web.Tests.DraftEditorTests

open System
open Xunit
open Summa.Ledger.Invoicing
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private address (hash: string) : Limen.Routing.PageLocation =
    { Origin = "https://summa.example"
      Path = "/app/"
      Query = ""
      Hash = hash }

let private started () =
    run [ Started(address ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"local development"}"""); Loaded None ] initial |> fst

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | _ -> failwith $"no scalar view value {name}"

let private items (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Items rows) -> rows |> List.map Map.ofList
    | _ -> failwith $"no list view value {name}"

/// A saved draft D-0001 for ABC Corp with one line.
let private saved () =
    let withCustomer =
        run
            [ LocationChanged(address "#/customers")
              CustomerNameChanged "ABC Corp"
              CustomerAddressChanged "1 Main Street"
              CustomerEmailChanged "ap@abc.example"
              CustomerTermsChanged "30"
              CustomerAdded
              LocationChanged(address "#/invoices/new")
              DraftCustomerChanged "CUST-0001" ]
            (started ())
        |> fst

    let key = withCustomer.Draft.Lines.Head.Key
    run [ LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "10"); LineRateChanged(key, "150.00"); DraftSubmitted ] withCustomer |> fst

let private someone: Summa.Ledger.Ledger.Context = { Who = "agent-7"; When = ctx.Now; Source = "summa"; CorrelationId = None; Provenance = None }

let private detailed =
    { DetailedDescription = Some "Security assessment of the billing service"
      Unit = Some "hours"
      ServicePeriod = Some(DateOnly(2026, 9, 1), DateOnly(2026, 9, 30))
      Category = Some "Consulting"
      CustomerCode = Some "ABC-4410" }

/// Someone else (an agent, a proposal) sets on the stored draft what the editor does not show.
let private enriched (model: Model) =
    let books = model.Books.Value
    let draft = books.Books.Drafts["D-0001"]

    let richer =
        { draft with
            Lines = [ { draft.Lines.Head with Project = Some "PRJ-9"; WorkItem = Some "WI-3"; Details = Some detailed } ]
            Terms = Some(Net 15)
            DueDate = Some(DateOnly(2026, 11, 1))
            Assumptions = [ "rate from the engagement" ]
            Recipients = Some { To = [ "ap@abc.example" ]; Cc = [ "cfo@abc.example" ]; ReplyTo = None }
            Details =
                { draft.Details with
                    ServicePeriod = Some(DateOnly(2026, 9, 1), DateOnly(2026, 9, 30))
                    InternalNotes = Some "renewal pending"
                    ClientReference = Some "REF-1"
                    References = [ ContractNumber, "C-100"; CostCenter, "CC-7"; CostCenter, "CC-8" ] } }

    let stored = saveDraft someone richer books.Books |> Result.defaultWith (fun e -> failwith $"%A{e}")
    { model with Books = Some { books with Books = stored } }

/// Opens D-0001 in the editor afresh.
let private opened (model: Model) =
    run [ LocationChanged(address "#/invoices"); LocationChanged(address "#/drafts/D-0001") ] { model with Draft = { model.Draft with DraftId = None } } |> fst

[<Fact>]
let ``INV-DATA-008, INV-DATA-014: saving from the editor keeps everything on the draft it does not show`` () =
    let model = saved () |> enriched |> opened
    let key = model.Draft.Lines.Head.Key
    let after = run [ LineDescriptionChanged(key, "Assessment, phase 1"); DraftPurchaseOrderChanged "PO-55"; DraftSubmitted ] model |> fst
    Assert.Equal(None, after.Error)
    let draft = after.Books.Value.Books.Drafts["D-0001"]
    let line = draft.Lines.Head
    Assert.Equal("Assessment, phase 1", line.Description)
    Assert.Equal(Some detailed, line.Details)
    Assert.Equal(Some "PRJ-9", line.Project)
    Assert.Equal(Some "WI-3", line.WorkItem)
    Assert.Equal(Some(Net 15), draft.Terms)
    Assert.Equal(Some(DateOnly(2026, 11, 1)), draft.DueDate)
    Assert.Equal<string list>([ "rate from the engagement" ], draft.Assumptions)
    Assert.Equal(Some [ "cfo@abc.example" ], draft.Recipients |> Option.map _.Cc)
    Assert.Equal(Some "PO-55", draft.Details.PurchaseOrder)
    Assert.Equal(Some "renewal pending", draft.Details.InternalNotes)
    Assert.Equal(Some "REF-1", draft.Details.ClientReference)
    Assert.Equal(Some(DateOnly(2026, 9, 1), DateOnly(2026, 9, 30)), draft.Details.ServicePeriod)
    Assert.Equal<(ClientReferenceKind * string) list>([ ContractNumber, "C-100"; CostCenter, "CC-7"; CostCenter, "CC-8" ], draft.Details.References)

[<Fact>]
let ``INV-UI-007: a save made on an older version of the draft is refused, and the newer draft is kept`` () =
    let model = saved () |> opened
    // Someone else saves the draft after it was opened here.
    let theirs = enriched model
    let key = theirs.Draft.Lines.Head.Key
    let refused = run [ LineDescriptionChanged(key, "Mine"); DraftSubmitted ] theirs |> fst
    Assert.Equal("Someone else changed this draft after you opened it. Discard your changes to see theirs, then make yours again.", value "error" refused)
    let kept = refused.Books.Value.Books.Drafts["D-0001"]
    Assert.Equal("Assessment", kept.Lines.Head.Description)
    Assert.Equal(Some detailed, kept.Lines.Head.Details)

[<Fact>]
let ``INV-DATA-008, INV-DATA-014: line details and typed client references are entered in the editor, saved and shown again`` () =
    let model = saved () |> opened
    let key = model.Draft.Lines.Head.Key

    let typed =
        run
            [ LineDetailsOpened key
              LineDetailedDescriptionChanged "Security assessment of the billing service"
              LineUnitChanged "hours"
              LineServiceFromChanged "2026-09-01"
              LineServiceToChanged "2026-09-30"
              LineCategoryChanged "Consulting"
              LineCustomerCodeChanged "ABC-4410"
              ReferenceAdded
              ReferenceAdded ]
            model
        |> fst

    let first, second = typed.Draft.References[0].Key, typed.Draft.References[1].Key
    let entered = run [ ReferenceKindChosen(first, "matter"); ReferenceValueChanged(first, "M-12"); ReferenceKindChosen(second, "vendor"); ReferenceValueChanged(second, "V-3") ] typed |> fst
    Assert.Equal("True", value "editorChanged" entered)
    let after = run [ DraftSubmitted ] entered |> fst
    Assert.Equal(None, after.Error)
    let draft = after.Books.Value.Books.Drafts["D-0001"]
    Assert.Equal(Some detailed, draft.Lines.Head.Details)
    Assert.Equal<(ClientReferenceKind * string) list>([ MatterNumber, "M-12"; VendorReference, "V-3" ], draft.Details.References)
    // Shown again as saved: the editor holds the stored draft, with nothing unsaved.
    Assert.Equal("False", value "editorChanged" after)
    let row = items "draftLines" after |> List.head
    Assert.Equal(Text " (given)", row["detailsNote"])
    let shown = run [ LineDetailsOpened after.Draft.Lines.Head.Key ] after |> fst
    Assert.Equal("True", value "hasDetailsLine" shown)
    Assert.Equal("1", value "detailsLinePosition" shown)
    Assert.Equal("2026-09-01", value "lineServiceFrom" shown)
    Assert.Equal("False", value "hasDetailsLine" (run [ LineDetailsClosed ] shown |> fst))
    Assert.Equal<Scalar list>([ Text "matter"; Text "vendor" ], items "draftReferences" after |> List.map (fun r -> r["kind"]))
    // Removing a reference removes it; an empty row is not saved.
    let removed = run [ ReferenceRemoved after.Draft.References[0].Key; ReferenceAdded; DraftSubmitted ] after |> fst
    Assert.Equal<(ClientReferenceKind * string) list>([ VendorReference, "V-3" ], removed.Books.Value.Books.Drafts["D-0001"].Details.References)

[<Fact>]
let ``line details and references that cannot be used are refused with a reason, and nothing is saved`` () =
    let model = saved () |> opened
    let key = model.Draft.Lines.Head.Key
    let version = model.Books.Value.Books.Drafts["D-0001"].Version
    let refusedWith msgs = run (msgs @ [ DraftSubmitted ]) model |> fst |> fun m -> value "error" m, m.Books.Value.Books.Drafts["D-0001"].Version
    Assert.Equal(("'Assessment' needs both the first and the last day of its service period, or neither.", version), refusedWith [ LineDetailsOpened key; LineServiceFromChanged "2026-09-01" ])
    Assert.Equal(("'Assessment' has a service period that ends before it starts.", version), refusedWith [ LineDetailsOpened key; LineServiceFromChanged "2026-09-30"; LineServiceToChanged "2026-09-01" ])
    Assert.Equal(("'Assessment' needs its service period as dates such as 2026-09-01.", version), refusedWith [ LineDetailsOpened key; LineServiceFromChanged "September"; LineServiceToChanged "2026-09-30" ])

    let withRow = run [ ReferenceAdded ] model |> fst
    let row = withRow.Draft.References.Head.Key
    Assert.Equal(("Choose what kind of reference 'X-1' is.", version), run [ ReferenceKindChosen(row, "invented"); ReferenceValueChanged(row, "X-1"); DraftSubmitted ] withRow |> fst |> fun m -> value "error" m, m.Books.Value.Books.Drafts["D-0001"].Version)

[<Fact>]
let ``a form kept before line details and references existed is still restored`` () =
    let older =
        """{"counter":3,"customerId":"CUST-0001","draftId":"D-0001","lines":[{"description":"Assessment","hours":"10","key":"line-2","rate":"150.00","taxable":false}],"notes":"","purchaseOrder":"PO-1","tax":{"accountCode":"2300","amount":"","code":"","evidence":"","inclusive":false,"jurisdiction":"","rate":"","rateSource":""}}"""

    match decodeEditor older with
    | Some(3, form) ->
        Assert.Equal(noLineDetailsForm, form.Lines.Head.Details)
        Assert.Equal(None, form.Lines.Head.Origin)
        Assert.Empty form.References
        Assert.Equal(None, form.BaseVersion)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``INV-DATA-008, 013, 014: the printed invoice shows line details and references, only those given`` () =
    let model = saved () |> enriched |> opened
    let issued = run [ DraftSubmitted; DraftIssued ] model |> fst
    Assert.Equal(None, issued.Error)
    let line = items "docLines" issued |> List.head
    Assert.Equal(Text "Security assessment of the billing service\nUnit: hours\nService period: September 1, 2026 to September 30, 2026\nCategory: Consulting\nYour code: ABC-4410", line["details"])

    Assert.Equal<(Scalar * Scalar) list>(
        [ Text "Your reference", Text "REF-1"
          Text "Service period", Text "September 1, 2026 to September 30, 2026"
          Text "Contract number", Text "C-100"
          Text "Cost center", Text "CC-7"
          Text "Cost center", Text "CC-8" ],
        items "docReferences" issued |> List.map (fun r -> r["label"], r["value"])
    )

    // An invoice without them shows none.
    let plain = run [ DraftSubmitted; DraftIssued ] (saved ()) |> fst
    Assert.Equal(Text "", (items "docLines" plain |> List.head)["details"])
    Assert.Empty(items "docReferences" plain)

[<Fact>]
let ``a detail typed with no line shown changes nothing, and removing the shown line closes its details`` () =
    let model = saved () |> opened
    Assert.Equal(model.Draft, run [ LineUnitChanged "hours" ] model |> fst |> _.Draft)
    let key = model.Draft.Lines.Head.Key
    let removed = run [ LineDetailsOpened key; LineRemoved key ] model |> fst
    Assert.Equal(None, removed.DetailsLine)
    Assert.Equal("False", value "hasDetailsLine" removed)
