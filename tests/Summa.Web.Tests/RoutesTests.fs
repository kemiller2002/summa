/// Summa's deep links (WI-0041, SUM-LINK-001..012): the route table, the
/// typed codec over `Routes.Place`, the canonical form, the outcomes for bad
/// locations, sign-in return, sharing, and the committed route inventory.
module Summa.Web.Tests.RoutesTests

open System
open System.IO
open System.Text.Json
open Xunit
open Limen.Routing
open Summa.Web.Engine.Routes
open Summa.Web.Tests.Support

let private everyone =
    Member(set [ "ViewFinancials"; "CreateDraftInvoice"; "ManageSettings"; "AllocatePayment"; "ExportData" ])

let private parsed location = parse everyone location

let private formatted place =
    match format place with
    | Ok location -> location
    | Error problem -> failwith $"%A{place} did not format: %A{problem}"

// ---- The table -----------------------------------------------------------------------------

[<Fact>]
let ``the route table is valid`` () =
    match definition with
    | Ok _ -> ()
    | Error problems -> failwith $"%A{problems}"

/// A sample of every place, with filters set and unset.
let private samples =
    [ Home
      SignIn None
      SignIn(Some "/invoices/INV-0001?tab=history")
      NotFound
      Customers allCustomers
      Customers { Search = Some "Acme & Sons"; Inactive = true; Sort = ByBalance }
      Customer("CUST-0001", CustomerInvoices)
      Customer("CUST-0001", CustomerCredits)
      Invoices allInvoices
      Invoices
          { Search = Some "late fee"
            Status = set [ "unpaid"; "partly-paid" ]
            Customer = Some "CUST-0002"
            From = Some(DateOnly(2026, 1, 1))
            To = Some(DateOnly(2026, 3, 31))
            Overdue = true
            Sort = DueFirst }
      NewInvoice None
      NewInvoice(Some "CUST-0003")
      Draft "D-0001"
      Invoice("INV-0001", Document)
      Invoice("INV-0001", History)
      Invoice("INV-0001", InvoicePayments)
      Payments allPayments
      Payments
          { Search = Some "chk 1042"
            Customer = Some "CUST-0001"
            From = Some(DateOnly(2026, 9, 1))
            To = None
            Unapplied = true
            Sort = OldestPayment }
      Payment "PAY-0001"
      CreditMemos None
      CreditMemos(Some "CUST-0001")
      CreditMemo "CM-0001"
      Engagements None
      Engagement "ENG-0001"
      Proposals
      Proposal "P-1"
      Receivables(None, None)
      Receivables(Some(DateOnly(2026, 6, 30)), Some "CUST-0001")
      Periods None
      Periods(Some 2026)
      Period(2026, 9)
      Ledger(None, None, None)
      Ledger(Some "acct-1200", Some(DateOnly(2026, 1, 1)), Some(DateOnly(2026, 12, 31)))
      JournalEntry "JE-INV-0001"
      Reports
      TrialBalance None
      TrialBalance(Some(DateOnly(2026, 9, 30)))
      IncomeStatement(None, None, Accrual)
      IncomeStatement(Some(DateOnly(2026, 1, 1)), Some(DateOnly(2026, 9, 30)), Cash)
      BalanceSheet(Some(DateOnly(2026, 9, 30)))
      Settings
      Work
      FollowUp OverdueInvoices
      FollowUp DisputedInvoices
      Inbox None
      Inbox(Some "CUST-0001")
      Cpa None
      Cpa(Some 2026) ]

[<Fact>]
let ``every place has a route`` () =
    let routeNames = RouteTable.destinations table |> List.map fst |> Set.ofList
    let used = samples |> List.map (fun place -> (toTarget place).Route) |> Set.ofList
    Assert.Equal<Set<string>>(routeNames, used)

[<Fact>]
let ``every sample place survives format then parse`` () =
    // The not-found place is an outcome, not a destination: it parses as
    // RouteError.NotFound (see ``an unknown place is not found``).
    for place in samples |> List.filter ((<>) NotFound) do
        Assert.True((parsed (formatted place) = Ok place), $"%A{place} -> {formatted place} -> %A{parsed (formatted place)}")

// ---- Canonical form (SUM-LINK-003) ------------------------------------------------------------

[<Theory>]
[<InlineData("home", "/")>]
[<InlineData("invoice", "/invoices/INV-0001")>]
[<InlineData("invoice-history", "/invoices/INV-0001?tab=history")>]
[<InlineData("invoices", "/invoices")>]
[<InlineData("invoices-filtered", "/invoices?q=late%20fee&status=partly-paid,unpaid&customer=CUST-0002&from=2026-01-01&to=2026-03-31&overdue=true&sort=due")>]
[<InlineData("period", "/periods/2026-09")>]
[<InlineData("income", "/reports/income-statement?from=2026-01-01&to=2026-09-30&basis=cash")>]
[<InlineData("customers", "/customers?q=Acme%20%26%20Sons&inactive=true&sort=balance")>]
let ``places format to their canonical location`` (name: string, expected: string) =
    let place =
        match name with
        | "home" -> Home
        | "invoice" -> Invoice("INV-0001", Document)
        | "invoice-history" -> Invoice("INV-0001", History)
        | "invoices" -> Invoices allInvoices
        | "invoices-filtered" -> samples[9]
        | "period" -> Period(2026, 9)
        | "income" -> IncomeStatement(Some(DateOnly(2026, 1, 1)), Some(DateOnly(2026, 9, 30)), Cash)
        | _ -> samples[5]

    Assert.Equal(expected, formatted place)

[<Fact>]
let ``a set is sorted and de-duplicated, and defaults are omitted`` () =
    let place = Invoices { allInvoices with Status = set [ "unpaid"; "paid" ]; Sort = Newest; Overdue = false }
    Assert.Equal("/invoices?status=paid,unpaid", formatted place)
    Assert.Equal(Ok place, parsed "/invoices?status=unpaid,paid,unpaid&sort=newest&overdue=false")

[<Fact>]
let ``adopting a non-canonical location replaces it with the canonical one`` () =
    let state, resolution, effect =
        RouteCodec.adopt codec (guard everyone) Navigation.initial "/invoices?sort=newest&q=late+fee&tab=x&utm=1"

    Assert.Equal(Ok(Invoices { allInvoices with Search = Some "late fee" }), resolution)
    Assert.Equal(Some(NavigationEffect.Replace "/invoices?q=late%20fee"), effect)
    Assert.Equal(Some "/invoices?q=late%20fee", state.Current)

[<Fact>]
let ``navigating pushes a new place, refining replaces, and the current place does nothing`` () =
    let state = { Current = Some "/invoices" }
    let filtered = Invoices { allInvoices with Overdue = true }

    match RouteCodec.navigate codec state (Invoice("INV-0001", Document)), RouteCodec.refine codec state filtered with
    | Ok(_, pushed), Ok(_, replaced) ->
        Assert.Equal(Some(NavigationEffect.Push "/invoices/INV-0001"), pushed)
        Assert.Equal(Some(NavigationEffect.Replace "/invoices?overdue=true"), replaced)
    | other -> failwith $"%A{other}"

    Assert.Equal(Ok(state, None), RouteCodec.navigate codec state (Invoices allInvoices))

// ---- Generated round trips (SUM-LINK-002) ---------------------------------------------------------

let private generated (seed: int) =
    let random = Random seed
    let pick (items: 'a list) = items[random.Next items.Length]
    let maybe (make: unit -> 'a) = if random.Next 2 = 0 then None else Some(make ())

    let text () =
        let alphabet = "abcXYZ019 -_.~!*'();:@&=+$,/?#[]%\"<>\\^`{|}éü中文😀"
        let length = 1 + random.Next 12

        // Characters are taken as text elements, so a surrogate pair stays whole.
        let elements =
            Globalization.StringInfo.GetTextElementEnumerator alphabet
            |> Seq.unfold (fun e -> if e.MoveNext() then Some(string e.Current, e) else None)
            |> List.ofSeq

        String.concat "" [ for _ in 1..length -> pick elements ]

    let id () = pick [ "INV"; "CUST"; "PAY"; "D"; "CM"; "ENG"; "JE-INV" ] + "-" + string (random.Next 10000)
    let date () = DateOnly.FromDayNumber(random.Next(DateOnly(1, 1, 1).DayNumber, DateOnly(9999, 12, 31).DayNumber))

    [ for _ in 1..1000 ->
          match random.Next 12 with
          | 0 ->
              Invoices
                  { Search = maybe text
                    Status = invoiceStatuses |> List.filter (fun _ -> random.Next 2 = 0) |> Set.ofList
                    Customer = maybe id
                    From = maybe date
                    To = maybe date
                    Overdue = random.Next 2 = 0
                    Sort = pick [ Newest; Oldest; DueFirst; ByNumber; ByCustomer ] }
          | 1 -> Customers { Search = maybe text; Inactive = random.Next 2 = 0; Sort = pick [ ByName; ByBalance ] }
          | 2 ->
              Payments
                  { Search = maybe text
                    Customer = maybe id
                    From = maybe date
                    To = maybe date
                    Unapplied = random.Next 2 = 0
                    Sort = pick [ NewestPayment; OldestPayment ] }
          | 3 -> Invoice(text (), pick [ Document; InvoicePayments; History ])
          | 4 -> Customer(text (), pick [ CustomerInvoices; CustomerPayments; CustomerCredits ])
          | 5 -> Period(1 + random.Next 9999, 1 + random.Next 12)
          | 6 -> Ledger(maybe id, maybe date, maybe date)
          | 7 -> IncomeStatement(maybe date, maybe date, pick [ Accrual; Cash ])
          | 8 -> Receivables(maybe date, maybe id)
          | 9 -> Periods(maybe (fun () -> 1 + random.Next 9999))
          | 10 -> NewInvoice(maybe id)
          | _ -> JournalEntry(text ()) ]

[<Fact>]
let ``generated places survive format then parse, and their locations are canonical`` () =
    for place in generated 20261008 do
        let location = formatted place
        Assert.Equal(Ok place, parsed location)

        // Adopting a canonical location changes nothing.
        let _, _, effect = RouteCodec.adopt codec (guard everyone) Navigation.initial location
        Assert.Equal(None, effect)

// ---- Bad locations (SUM-LINK-007) -------------------------------------------------------------------

[<Theory>]
[<InlineData("/no-such-place")>]
[<InlineData("/invoices/INV-0001/extra")>]
[<InlineData("/not-found")>]
let ``an unknown place is not found`` (location: string) =
    Assert.Equal(Error RouteError.NotFound, parsed location)

[<Fact>]
let ``a parameter of the wrong type is invalid, naming the parameter`` () =
    match parsed "/periods/2026-13", parsed "/invoices?status=open", parsed "/reports/trial-balance?asOf=2026-02-30" with
    | Error(RouteError.Invalid(_, "period", "2026-13", _)), Error(RouteError.Invalid(_, "status", "open", _)), Error(RouteError.Invalid(_, "asOf", _, _)) -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``malformed and oversized locations are refused before decoding`` () =
    Assert.Equal(Error(RouteError.Malformed "path"), parsed "/invoices/%E0%A4")
    Assert.Equal(Error(RouteError.Malformed "query"), parsed "/invoices?q=%ZZ")
    Assert.Equal(Error(RouteError.Malformed "length"), parsed ("/invoices?q=" + String('a', 9000)))

[<Fact>]
let ``a member without a place's capability is not permitted`` () =
    let reader = Member(set [ "ViewFinancials" ])
    Assert.Equal(Error(RouteError.NotPermitted "settings"), parse reader "/settings")
    Assert.Equal(Error(RouteError.NotPermitted "invoice-new"), parse reader "/invoices/new")
    Assert.Equal(Ok(Invoice("INV-0001", Document)), parse reader "/invoices/INV-0001")

// ---- Sign-in (SUM-LINK-008) ---------------------------------------------------------------------------

[<Fact>]
let ``a deep link opened signed out goes to sign-in carrying its canonical place`` () =
    Assert.Equal(
        Ok(SignIn(Some "/invoices/INV-0001?tab=history")),
        parse Anonymous "/invoices/INV-0001?tab=history&sort=x"
    )

    Assert.Equal("/sign-in?returnTo=%2Finvoices%2FINV-0001%3Ftab%3Dhistory", formatted (SignIn(Some "/invoices/INV-0001?tab=history")))

[<Fact>]
let ``after sign-in the person returns to the deep link, re-checked against their capabilities`` () =
    Assert.Equal("/invoices/INV-0001?tab=history", resume everyone (Some "/invoices/INV-0001?tab=history"))
    Assert.Equal("/", resume (Member(set [ "ViewFinancials" ])) (Some "/settings"))

[<Theory>]
[<InlineData("//evil.example/invoices")>]
[<InlineData("https://evil.example/")>]
[<InlineData("/\\evil.example")>]
[<InlineData("javascript:alert(1)")>]
[<InlineData("/sign-in?returnTo=%2Finvoices")>]
[<InlineData("/no-such-place")>]
let ``a return target that is not one of Summa's own places goes home`` (target: string) =
    Assert.Equal("/", resume everyone (Some target))

// ---- Links and sharing (SUM-LINK-005, SUM-LINK-009) --------------------------------------------------

[<Fact>]
let ``links are relative hash links, and a shared link keeps the page's own address`` () =
    Assert.Equal("#/invoices/INV-0001?tab=history", href (Invoice("INV-0001", History)))

    let page =
        { Origin = "https://summa.echelonfoundry.com"
          Path = "/app/"
          Query = ""
          Hash = "#/customers" }

    Assert.Equal("/customers", locationOf page)
    Assert.Equal("/", locationOf { page with Hash = "" })
    Assert.Equal(Ok "https://summa.echelonfoundry.com/app/#/invoices/INV-0001", share page (Invoice("INV-0001", Document)))

// ---- What never goes in a URL (SUM-LINK-004) -------------------------------------------------------

let private inventoryDocument () = JsonDocument.Parse(inventory ())

[<Fact>]
let ``no parameter can carry an amount, a credential or an account number`` () =
    use document = inventoryDocument ()

    let parameters =
        document.RootElement.GetProperty("routes").EnumerateArray()
        |> Seq.collect (fun route -> route.GetProperty("params").EnumerateArray() |> Seq.map (fun p -> p.GetProperty("name").GetString(), p.GetProperty("type").GetString()))
        |> List.ofSeq

    let forbidden = [ "amount"; "total"; "balance"; "price"; "rate"; "paid"; "owed"; "token"; "secret"; "password"; "iban"; "routing"; "card"; "ssn"; "tax" ]

    for name, kind in parameters do
        Assert.False(RouteTable.isReserved name, name)
        Assert.False(forbidden |> List.exists (fun word -> name.ToLowerInvariant().Contains word), name)
        // Free text is only a search or an opaque id: never a value the page shows as money.
        Assert.Contains(kind, [ "string"; "int"; "bool"; "date"; "month"; "enum"; "set" ])

        if kind = "string" then
            Assert.Contains(name, [ "id"; "q"; "customer"; "account"; "returnTo" ])

// ---- The inventory (SUM-LINK-011) -------------------------------------------------------------------

[<Fact>]
let ``the committed route inventory is the table's own`` () =
    let path = repoFile ".echelon/routes.json"
    let expected = inventory ()

    if Environment.GetEnvironmentVariable "SUMMA_WRITE_ROUTES" = "1" then File.WriteAllText(path, expected)

    Assert.True(File.Exists path, "Run the tests with SUMMA_WRITE_ROUTES=1 to write .echelon/routes.json.")
    Assert.Equal(expected, File.ReadAllText path)

[<Fact>]
let ``the inventory is echelon.routes v1 in hash mode with sign-in and not-found roles`` () =
    use document = inventoryDocument ()
    let root = document.RootElement
    Assert.Equal("echelon.routes/v1", root.GetProperty("schema").GetString())
    Assert.Equal("hash", root.GetProperty("mode").GetString())
    Assert.Equal("home", root.GetProperty("home").GetString())
    Assert.Equal("sign-in", root.GetProperty("signIn").GetString())
    Assert.Equal("not-found", root.GetProperty("notFound").GetString())
    Assert.EndsWith("}\n", inventory ())

// ---- The inventory's schema (Limen 0.9.0 contract/routes.schema.json) --------------------------------

/// Built once: the schema's $id is registered globally, and may not be registered twice.
let private routesSchema =
    lazy (Json.Schema.JsonSchema.FromText(readRepoFile "node_modules/@echelon-foundry/limen/contract/routes.schema.json"))

let private validates (document: string) =
    use parsed = JsonDocument.Parse document
    routesSchema.Value.Evaluate(parsed.RootElement, Json.Schema.EvaluationOptions(OutputFormat = Json.Schema.OutputFormat.List)).IsValid

[<Fact>]
let ``the committed route inventory is valid against Limen's echelon.routes v1 schema`` () =
    Assert.True(validates (readRepoFile ".echelon/routes.json"))
    // The schema is not vacuous: a route with no pattern, or another schema, fails it.
    Assert.False(validates ((readRepoFile ".echelon/routes.json").Replace("\"pattern\": \"/invoices/{id:string}\",", "")))
    Assert.False(validates ((readRepoFile ".echelon/routes.json").Replace("echelon.routes/v1", "echelon.routes/v2")))
