/// The customer-facing invoice document (v0.1 §7, SUM0-037, SUM3-030,
/// INV-DOC).
///
/// An issued invoice is an immutable snapshot of what it said:
/// - its lines and totals;
/// - the issuer and customer as they were at issue;
/// - its terms and details;
/// - the template it is printed with.
///
/// The document is derived from that snapshot alone, so later changes to
/// customers, company details or defaults never change it (INV-DOC-006).
///
/// Summa produces semantic HTML and leaves pagination, repeated headers,
/// footers and page numbers to Folio's print primitives (INV-DOC-002,
/// INV-DOC-003). Templates are versioned: a template version, once
/// released, renders the same bytes for the same snapshot, which the
/// recorded SHA-256 proves (INV-DOC-007, INV-DOC-008).
///
/// Pure.
module Summa.Ledger.Documents

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Summa.Ledger.Money
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing

/// The template new invoices are issued with: Folio's portable browser profile.
let currentTemplate =
    { Id = "summa.invoice"
      Version = "1.0.0"
      Profile = "P0" }

/// The machine-readable document's name and version (INV-DOC-012).
[<Literal>]
let JsonSchema = "summa.invoice-document"

[<Literal>]
let JsonVersion = 1

type DocumentLine =
    { Description: string
      QuantityThousandths: int64
      UnitPrice: Money
      Discount: Money
      Amount: Money
      Project: string option
      Source: LineSource }

/// The canonical customer-facing invoice (INV-DOC-001), from the issued
/// snapshot only.
type InvoiceDocument =
    { InvoiceId: string
      Number: string
      Currency: string
      IssueDate: DateOnly
      DueDate: DateOnly
      Terms: PaymentTerms
      Issuer: IssuerSnapshot
      Customer: CustomerSnapshot
      Details: InvoiceDetails
      EngagementId: string option
      Corrects: string option
      Lines: DocumentLine list
      Subtotal: Money
      Discounts: (string * Money) list
      Adjustments: Money list
      Total: Money
      /// What is due at issue: the total. Credits and payments applied
      /// later are receivables, not part of the issued document.
      AmountDue: Money
      Template: TemplateRef }

let ofInvoice (invoice: IssuedInvoice) : InvoiceDocument =
    { InvoiceId = invoice.InvoiceId
      Number = invoice.Number
      Currency = invoice.Currency
      IssueDate = invoice.IssueDate
      DueDate = invoice.DueDate
      Terms = invoice.Terms
      Issuer = invoice.Issuer
      Customer = invoice.Customer
      Details = invoice.Details
      EngagementId = invoice.EngagementId
      Corrects = invoice.Corrects
      Lines =
        invoice.Lines
        |> List.map (fun l ->
            { Description = l.Description
              QuantityThousandths = l.QuantityThousandths
              UnitPrice = l.UnitPrice
              Discount = lineDiscount l
              Amount = lineAmount l
              Project = l.Project
              Source = l.Source })
      Subtotal = invoice.Subtotal
      Discounts = invoice.Discounts |> List.map (fun d -> d.Label, discountOn invoice.Subtotal d.Rule)
      Adjustments = invoice.Adjustments
      Total = invoice.Total
      AmountDue = invoice.Total
      Template = invoice.Template }

// ---- Formatting: invariant, never the browser's locale (INV-INTL-002) -----------

/// `1,234.50`: grouped, two decimals, minus sign first.
let amountText (m: Money) =
    let sign = if m.Minor < 0L then "-" else ""
    let absolute = abs m.Minor
    let whole = (absolute / 100L).ToString("#,0", CultureInfo.InvariantCulture)
    $"{sign}{whole}.{absolute % 100L:D2}"

let moneyText (m: Money) = $"{amountText m} {m.Currency}"

/// Thousandths without trailing zeros: 34500 is `34.5`, 1000 is `1`.
let quantityText (thousandths: int64) =
    (decimal thousandths / 1000m).ToString("0.###", CultureInfo.InvariantCulture)

let dateText (d: DateOnly) = d.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)

let termsText =
    function
    | DueOnReceipt -> "Due on receipt"
    | Net days -> $"Net {days}"
    | CustomDate d -> $"Due {dateText d}"

// ---- HTML, template summa.invoice 1.0.0 -----------------------------------------------

let private escape (text: string) =
    text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;")

let private multiline (text: string) =
    text.Replace("\r\n", "\n").Split('\n') |> Array.map escape |> String.concat "<br>"

let private row (label: string) (value: string) =
    $"<div><dt>{escape label}</dt><dd>{escape value}</dd></div>"

let private renderV1 (doc: InvoiceDocument) =
    let details =
        [ Some(row "Invoice number" doc.Number)
          Some(row "Issue date" (dateText doc.IssueDate))
          Some(row "Due date" (dateText doc.DueDate))
          Some(row "Payment terms" (termsText doc.Terms))
          doc.Details.PurchaseOrder |> Option.map (row "Purchase order")
          doc.Details.ClientReference |> Option.map (row "Your reference")
          doc.Details.ServicePeriod |> Option.map (fun (a, b) -> row "Service period" $"{dateText a} to {dateText b}")
          doc.EngagementId |> Option.map (row "Engagement")
          doc.Corrects |> Option.map (row "Corrects invoice") ]
        |> List.choose id
        |> String.concat ""

    let lines =
        doc.Lines
        |> List.map (fun l ->
            let discount = if l.Discount.Minor = 0L then "" else amountText l.Discount

            $"<tr><td>{escape l.Description}</td><td class=\"summa-number\">{quantityText l.QuantityThousandths}</td><td class=\"summa-number\">{amountText l.UnitPrice}</td><td class=\"summa-number\">{discount}</td><td class=\"summa-number\">{amountText l.Amount}</td></tr>")
        |> String.concat ""

    let totals =
        [ yield $"<tr><th scope=\"row\">Subtotal</th><td class=\"summa-number\">{amountText doc.Subtotal}</td></tr>"
          for label, amount in doc.Discounts do
              yield $"<tr><th scope=\"row\">Discount: {escape label}</th><td class=\"summa-number\">-{amountText amount}</td></tr>"
          for amount in doc.Adjustments do
              yield $"<tr><th scope=\"row\">Adjustment</th><td class=\"summa-number\">{amountText amount}</td></tr>"
          yield $"<tr><th scope=\"row\">Total</th><td class=\"summa-number\">{moneyText doc.Total}</td></tr>"
          yield $"<tr class=\"summa-due\"><th scope=\"row\">Amount due</th><td class=\"summa-number\">{moneyText doc.AmountDue}</td></tr>" ]
        |> String.concat ""

    let issuerContact =
        [ Some(multiline doc.Issuer.Address)
          doc.Issuer.TaxId |> Option.map (fun t -> "Tax ID: " + escape t)
          (if String.IsNullOrWhiteSpace doc.Issuer.Email then None else Some(escape doc.Issuer.Email)) ]
        |> List.choose id
        |> String.concat "<br>"

    let payment =
        if String.IsNullOrWhiteSpace doc.Issuer.PaymentInstructions then
            ""
        else
            $"<section aria-labelledby=\"summa-payment\"><h2 id=\"summa-payment\">Payment instructions</h2><p>{multiline doc.Issuer.PaymentInstructions}</p></section>"

    let notes =
        match doc.Details.CustomerNotes with
        | Some text when not (String.IsNullOrWhiteSpace text) ->
            $"<section aria-labelledby=\"summa-notes\"><h2 id=\"summa-notes\">Notes</h2><p>{multiline text}</p></section>"
        | _ -> ""

    String.concat
        ""
        [ "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n"
          $"<title>Invoice {escape doc.Number}</title>\n"
          $"<meta name=\"summa-template\" content=\"{escape doc.Template.Id} {escape doc.Template.Version} {escape doc.Template.Profile}\">\n"
          "<link rel=\"stylesheet\" href=\"folio/print.css\">\n<script type=\"module\" src=\"folio/register.js\"></script>\n"
          "</head>\n<body>\n<ef-print-document>\n"
          $"<ef-print-header repeat=\"page\"><span slot=\"left\">{escape doc.Issuer.LegalName}</span><span slot=\"right\">Invoice {escape doc.Number}</span></ef-print-header>\n"
          "<main>\n"
          $"<header><h1>Invoice {escape doc.Number}</h1><p><strong>{escape doc.Issuer.LegalName}</strong><br>{issuerContact}</p></header>\n"
          $"<dl class=\"summa-details\">{details}</dl>\n"
          $"<section aria-labelledby=\"summa-bill-to\"><h2 id=\"summa-bill-to\">Bill to</h2><p>{escape doc.Customer.BillingName}<br>{multiline doc.Customer.BillingAddress}</p></section>\n"
          "<ef-print-table><table><caption>Invoice lines</caption><thead><tr><th scope=\"col\">Description</th><th scope=\"col\">Quantity</th>"
          $"<th scope=\"col\">Rate ({escape doc.Currency})</th><th scope=\"col\">Discount</th><th scope=\"col\">Amount ({escape doc.Currency})</th></tr></thead>"
          $"<tbody>{lines}</tbody></table></ef-print-table>\n"
          $"<ef-print-keep><table class=\"summa-totals\"><caption>Totals</caption><tbody>{totals}</tbody></table></ef-print-keep>\n"
          payment
          notes
          "\n</main>\n"
          "<ef-print-footer repeat=\"page\"><span slot=\"left\">"
          $"{escape doc.Issuer.LegalName} - Invoice {escape doc.Number}</span><span slot=\"right\"><ef-print-page-number format=\"page-of-pages\"></ef-print-page-number></span></ef-print-footer>\n"
          "</ef-print-document>\n</body>\n</html>\n" ]

/// Every released template version. A version is never changed once
/// released; a new look is a new version, and old invoices keep theirs.
let private templates: Map<string * string, InvoiceDocument -> string> =
    Map.ofList [ ("summa.invoice", "1.0.0"), renderV1 ]

/// Renders the document with the template it names. An unknown template
/// is a render failure (INV-DOC-014).
let render (doc: InvoiceDocument) : Result<string, string> =
    match templates.TryFind(doc.Template.Id, doc.Template.Version) with
    | Some template -> Ok(template doc)
    | None -> Error $"template {doc.Template.Id} {doc.Template.Version} is not available"

// ---- JSON, summa.invoice-document version 1 ---------------------------------------------

let private writeMoney (w: Utf8JsonWriter) (name: string) (m: Money) =
    w.WriteStartObject name
    w.WriteString("currency", m.Currency)
    w.WriteNumber("minor", m.Minor)
    w.WriteEndObject()

let private writeOptional (w: Utf8JsonWriter) (name: string) (value: string option) =
    match value with
    | Some text -> w.WriteString(name, text)
    | None -> w.WriteNull name

let private writeSource (w: Utf8JsonWriter) (source: LineSource) =
    w.WriteStartObject "source"

    match source with
    | ManualLine -> w.WriteString("kind", "manual")
    | TimeSource refs ->
        w.WriteString("kind", "time")
        w.WriteStartArray "entries"

        for t in refs do
            w.WriteStartObject()
            w.WriteString("publicationId", t.PublicationId)
            w.WriteString("activityId", t.ActivityId)
            w.WriteNumber("revision", t.Revision)
            w.WriteNumber("minutes", t.Minutes)
            w.WriteEndObject()

        w.WriteEndArray()
    | FixedFeeSource e ->
        w.WriteString("kind", "fixed-fee")
        w.WriteString("engagementId", e)
    | MilestoneSource(e, m) ->
        w.WriteString("kind", "milestone")
        w.WriteString("engagementId", e)
        w.WriteString("milestoneId", m)
    | ExpenseSource x ->
        w.WriteString("kind", "expense")
        w.WriteString("expenseId", x)

    w.WriteEndObject()

let private isoDate (d: DateOnly) = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

/// The versioned machine-readable document: fixed member order, no
/// whitespace, so the same document is always the same bytes.
let toJson (doc: InvoiceDocument) =
    use stream = new MemoryStream()

    do
        use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
        w.WriteStartObject()
        w.WriteString("schema", JsonSchema)
        w.WriteNumber("version", JsonVersion)
        w.WriteString("invoiceId", doc.InvoiceId)
        w.WriteString("number", doc.Number)
        w.WriteString("currency", doc.Currency)
        w.WriteString("issueDate", isoDate doc.IssueDate)
        w.WriteString("dueDate", isoDate doc.DueDate)
        w.WriteString("terms", termsText doc.Terms)
        w.WriteStartObject "issuer"
        w.WriteString("legalName", doc.Issuer.LegalName)
        w.WriteString("address", doc.Issuer.Address)
        writeOptional w "taxId" doc.Issuer.TaxId
        w.WriteString("email", doc.Issuer.Email)
        w.WriteString("paymentInstructions", doc.Issuer.PaymentInstructions)
        w.WriteEndObject()
        w.WriteStartObject "customer"
        w.WriteString("customerId", doc.Customer.CustomerId)
        w.WriteString("name", doc.Customer.Name)
        w.WriteString("billingName", doc.Customer.BillingName)
        w.WriteString("billingAddress", doc.Customer.BillingAddress)
        w.WriteString("email", doc.Customer.Email)
        w.WriteEndObject()
        writeOptional w "purchaseOrder" doc.Details.PurchaseOrder
        writeOptional w "clientReference" doc.Details.ClientReference

        match doc.Details.ServicePeriod with
        | Some(a, b) ->
            w.WriteStartObject "servicePeriod"
            w.WriteString("from", isoDate a)
            w.WriteString("to", isoDate b)
            w.WriteEndObject()
        | None -> w.WriteNull "servicePeriod"

        writeOptional w "customerNotes" doc.Details.CustomerNotes
        writeOptional w "engagementId" doc.EngagementId
        writeOptional w "corrects" doc.Corrects
        w.WriteStartArray "lines"

        for l in doc.Lines do
            w.WriteStartObject()
            w.WriteString("description", l.Description)
            w.WriteNumber("quantityThousandths", l.QuantityThousandths)
            writeMoney w "unitPrice" l.UnitPrice
            writeMoney w "discount" l.Discount
            writeMoney w "amount" l.Amount
            writeOptional w "project" l.Project
            writeSource w l.Source
            w.WriteEndObject()

        w.WriteEndArray()
        writeMoney w "subtotal" doc.Subtotal
        w.WriteStartArray "discounts"

        for label, amount in doc.Discounts do
            w.WriteStartObject()
            w.WriteString("label", label)
            writeMoney w "amount" amount
            w.WriteEndObject()

        w.WriteEndArray()
        w.WriteStartArray "adjustments"

        for amount in doc.Adjustments do
            w.WriteStartObject()
            writeMoney w "amount" amount
            w.WriteEndObject()

        w.WriteEndArray()
        writeMoney w "total" doc.Total
        writeMoney w "amountDue" doc.AmountDue
        w.WriteStartObject "template"
        w.WriteString("id", doc.Template.Id)
        w.WriteString("version", doc.Template.Version)
        w.WriteString("profile", doc.Template.Profile)
        w.WriteEndObject()
        w.WriteEndObject()

    Encoding.UTF8.GetString(stream.ToArray())

/// Lower-case hex SHA-256 of the UTF-8 bytes, and their size.
let digest (text: string) =
    let bytes = Encoding.UTF8.GetBytes text
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant(), int64 bytes.Length
