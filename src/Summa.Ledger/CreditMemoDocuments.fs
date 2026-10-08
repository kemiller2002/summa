/// The credit memo's customer document (INV-COR-008), rendered through
/// Folio like the invoice (DF-SUMMA-2026-0007).
///
/// A credit memo raised against an invoice takes its parties from that
/// invoice's issued snapshot, and its credited lines from the invoice's own
/// lines, so the document is reproduced from the records alone and never
/// changes once issued. Pure.
module Summa.Ledger.CreditMemoDocuments

open System
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Documents

/// The template credit memos are issued with: Folio's portable profile.
let template =
    { Id = "summa.credit-memo"
      Version = "1.0.0"
      Profile = "P0" }

type CreditMemoDocument =
    { MemoId: string
      Currency: string
      IssueDate: DateOnly
      Reason: string
      Issuer: IssuerSnapshot
      Customer: CustomerSnapshot
      /// The invoice credited: its number and issue date.
      Credits: string * DateOnly
      /// The credited lines, when the memo names them; otherwise an amount.
      Lines: DocumentLine list
      Amount: Money
      Template: TemplateRef }

type DocumentProblem =
    | UnknownMemo of string
    /// A memo with no invoice has no issued snapshot of its parties.
    | NoInvoice of memoId: string
    | UnknownInvoice of string

/// The document of a credit memo in these receivables.
let ofCreditMemo (r: Receivables) (memoId: string) : Result<CreditMemoDocument, DocumentProblem> =
    match r.CreditMemos.TryFind memoId with
    | None -> Error(UnknownMemo memoId)
    | Some memo ->
        match memo.InvoiceId with
        | None -> Error(NoInvoice memoId)
        | Some invoiceId ->
            match r.Books.Invoices.TryFind invoiceId with
            | None -> Error(UnknownInvoice invoiceId)
            | Some invoice ->
                let invoiceLines = (ofInvoice invoice).Lines

                Ok
                    { MemoId = memo.Id
                      Currency = memo.Amount.Currency
                      IssueDate = memo.IssueDate
                      Reason = memo.Reason
                      Issuer = invoice.Issuer
                      Customer = invoice.Customer
                      Credits = invoice.Number, invoice.IssueDate
                      Lines = memo.Lines |> List.sort |> List.choose (fun index -> List.tryItem index invoiceLines)
                      Amount = memo.Amount
                      Template = template }

let private renderV1 (doc: CreditMemoDocument) =
    let number, issued = doc.Credits

    let lines =
        match doc.Lines with
        | [] -> ""
        | credited ->
            let rows =
                credited
                |> List.map (fun l ->
                    $"<tr><td>{escape l.Description}</td><td class=\"summa-number\">{quantityText l.QuantityThousandths}</td><td class=\"summa-number\">{amountText l.Amount}</td></tr>")
                |> String.concat ""

            "<ef-print-table><table><caption>Credited lines</caption><thead><tr><th scope=\"col\">Description</th><th scope=\"col\">Quantity</th>"
            + $"<th scope=\"col\">Charged ({escape doc.Currency})</th></tr></thead><tbody>{rows}</tbody></table></ef-print-table>\n"

    String.concat
        ""
        [ "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n"
          $"<title>Credit memo {escape doc.MemoId}</title>\n"
          $"<meta name=\"summa-template\" content=\"{escape doc.Template.Id} {escape doc.Template.Version} {escape doc.Template.Profile}\">\n"
          "<link rel=\"stylesheet\" href=\"folio/print.css\">\n<script type=\"module\" src=\"folio/register.js\"></script>\n"
          "</head>\n<body>\n<ef-print-document>\n"
          $"<ef-print-header repeat=\"page\"><span slot=\"left\">{escape doc.Issuer.LegalName}</span><span slot=\"right\">Credit memo {escape doc.MemoId}</span></ef-print-header>\n"
          "<main>\n"
          $"<header><h1>Credit memo {escape doc.MemoId}</h1><p><strong>{escape doc.Issuer.LegalName}</strong><br>{multiline doc.Issuer.Address}</p></header>\n"
          "<dl class=\"summa-details\">"
          $"<div><dt>Credit memo</dt><dd>{escape doc.MemoId}</dd></div>"
          $"<div><dt>Issue date</dt><dd>{dateText doc.IssueDate}</dd></div>"
          $"<div><dt>Credits invoice</dt><dd>{escape number}, issued {dateText issued}</dd></div>"
          "</dl>\n"
          $"<section aria-labelledby=\"summa-credit-to\"><h2 id=\"summa-credit-to\">Credit to</h2><p>{escape doc.Customer.BillingName}<br>{multiline doc.Customer.BillingAddress}</p></section>\n"
          $"<section aria-labelledby=\"summa-reason\"><h2 id=\"summa-reason\">Reason</h2><p>{multiline doc.Reason}</p></section>\n"
          lines
          $"<ef-print-keep><table class=\"summa-totals\"><caption>Credit</caption><tbody><tr class=\"summa-due\"><th scope=\"row\">Amount credited</th><td class=\"summa-number\">{moneyText doc.Amount}</td></tr></tbody></table></ef-print-keep>\n"
          "</main>\n"
          $"<ef-print-footer repeat=\"page\"><span slot=\"left\">{escape doc.Issuer.LegalName} - Credit memo {escape doc.MemoId}</span><span slot=\"right\"><ef-print-page-number format=\"page-of-pages\"></ef-print-page-number></span></ef-print-footer>\n"
          "</ef-print-document>\n</body>\n</html>\n" ]

/// Every released template version; never changed once released.
let private templates: Map<string * string, CreditMemoDocument -> string> =
    Map.ofList [ ("summa.credit-memo", "1.0.0"), renderV1 ]

/// Renders the document with the template it names; an unknown template
/// is a render failure.
let render (doc: CreditMemoDocument) : Result<string, string> =
    match templates.TryFind(doc.Template.Id, doc.Template.Version) with
    | Some render -> Ok(render doc)
    | None -> Error $"template {doc.Template.Id} {doc.Template.Version} is not available"
