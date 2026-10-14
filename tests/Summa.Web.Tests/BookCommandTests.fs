/// Every message is either a change to the books, with the capability it
/// needs and the summary its commit carries (`bookCommand`), or one that
/// changes no books. A message on neither list fails here, so a new message
/// that changes the books cannot skip the capability check or the commit
/// to books on GitHub (WI-0029; the defect that #98 shipped and this
/// test now prevents).
module Summa.Web.Tests.BookCommandTests

open Xunit
open Microsoft.FSharp.Reflection
open Summa.Web.Engine.Accounting

/// Messages that change no books: navigation, typing into forms, reading
/// and storage plumbing, exports and previews. Books themselves are loaded,
/// saved, founded, migrated and reset through their own effects.
let private changesNoBooks =
    set
        [ "Started"; "LocationChanged"; "ConfigurationRead"; "IdentityChanged"; "SignInRequested"; "SignOutRequested"; "KeepSignInToggled"
          "ReturnTargetRead"; "BooksOpened"; "BooksNotOpened"; "FoundRequested"; "ConfirmRequested"; "MigrateRequested"; "BooksCommitted"
          "ManifestCommitted"; "StoreRefused"; "ChangeKept"; "BooksSynchronized"; "UnsentChanged"; "SendUnsentRequested"; "CheckRequested"
          "BooksChecked"; "AbandonUnsentRequested"; "ForeignUnsentSendRequested"; "ForeignUnsentDiscardRequested"; "Loaded"; "Saved"
          "LinkCopyRequested"; "LinkCopied"; "InvoiceSearchChanged"; "GlobalSearchChanged"; "InvoiceStatusToggled"; "InvoiceCustomerChosen"; "InvoiceFromChanged"
          "InvoiceToChanged"; "InvoiceOverdueToggled"; "InvoiceSortChosen"; "InvoiceFiltersCleared"; "CustomerSearchChanged"; "CustomerSortChosen"
          "CustomerInactiveToggled"; "ReceivablesAsOfChanged"; "ReceivablesCustomerChosen"; "InvoiceTabChosen"; "CustomerNameChanged"
          "CustomerBillingNameChanged"; "CustomerAddressChanged"; "CustomerEmailChanged"; "CustomerTermsChanged"; "DraftCustomerChanged"
          "LineDescriptionChanged"; "LineHoursChanged"; "LineRateChanged"; "LineAdded"; "LineRemoved"; "LineDetailsOpened"; "LineDetailsClosed"
          "LineDetailedDescriptionChanged"; "LineUnitChanged"; "LineServiceFromChanged"; "LineServiceToChanged"; "LineCategoryChanged"
          "LineCustomerCodeChanged"; "ReferenceAdded"; "ReferenceRemoved"; "ReferenceKindChosen"; "ReferenceValueChanged"; "DraftPurchaseOrderChanged"
          "DraftNotesChanged"; "PrintRequested"; "PaymentAmountChanged"; "PaymentDateChanged"; "PaymentMethodChanged"; "PaymentReferenceChanged"
          "CompanyLegalNameChanged"; "CompanyAddressChanged"; "CompanyEmailChanged"; "CompanyPaymentChanged"; "ImportTextChanged"; "ImportPreviewed"
          "ImportDiscarded"; "LegalKindChosen"; "LegalJurisdictionChanged"; "LegalValueChanged"; "ResetConfirmed"; "CreditAmountChanged"
          "CreditReasonChanged"; "PaymentSearchChanged"; "PaymentCustomerChosen"; "PaymentFromChanged"; "PaymentToChanged"; "PaymentUnappliedToggled"
          "PaymentSortChosen"; "PaymentFiltersCleared"; "CustomerTabChosen"; "CreditMemosCustomerChosen"; "EngagementsCustomerChosen"
          "EngagementCustomerChanged"; "EngagementNameChanged"; "EngagementFeeChanged"; "LedgerAccountChosen"; "LedgerFromChanged"; "LedgerToChanged"
          "ReportAsOfChanged"; "IncomeFromChanged"; "IncomeToChanged"; "IncomeBasisChosen"; "IncomeRangeChosen"; "PacksNegotiated"
          "ArtifactStoreOpened"; "PdfPicked"; "PdfChunkRead"; "PdfDownloadRequested"; "PdfFetched"; "FollowUpShowChosen"; "FollowUpNoteChanged"
          "InboxCustomerChosen"; "ReceiptCustomerChanged"; "ReceiptAmountChanged"; "ReceiptDateChanged"; "ReceiptMethodChanged"
          "ReceiptReferenceChanged"; "CpaExportRequested"; "InvoiceExportRequested"; "LineTaxableChanged"; "DraftTaxCodeChanged"
          "DraftTaxAmountChanged"; "DraftTaxAccountChosen"; "DraftTaxJurisdictionChanged"; "DraftTaxRateChanged"; "DraftTaxRateSourceChanged"
          "DraftTaxEvidenceChanged"; "DraftTaxInclusiveChanged"; "CustomerTaxStatusChosen"; "CustomerTaxJurisdictionChanged"
          "CustomerTaxEvidenceChanged"; "CustomerRequiresPurchaseOrderChanged"; "EditorRead"; "EditorDiscarded"; "ProposalLineChosen"
          "ProposalRateChanged"; "ProposalReasonChanged"; "PromiseAmountChanged"; "PromiseByChanged"; "PromiseNoteChanged"
          "PaymentApplyInvoiceChosen"; "DraftEngagementChosen" ]

/// One value of each message case; `bookCommand` looks only at the case.
let private everyMessage () =
    [ for case in FSharpType.GetUnionCases typeof<Msg> do
          let args =
              case.GetFields()
              |> Array.map (fun f ->
                  if f.PropertyType = typeof<string> then box ""
                  elif f.PropertyType.IsValueType then System.Activator.CreateInstance f.PropertyType
                  else null)

          case.Name, FSharpValue.MakeUnion(case, args) :?> Msg ]

[<Fact>]
let ``every message is either a change to the books with its capability, or listed as changing none`` () =
    for name, msg in everyMessage () do
        match bookCommand msg, changesNoBooks.Contains name with
        | Some _, true -> failwith $"{name} is a book command and is also listed as changing no books"
        | None, false -> failwith $"{name} is not a book command: list it in changesNoBooks if it changes no books, or give it a capability in bookCommand"
        | _ -> ()

[<Fact>]
let ``INV-OPS-006: marking a customer as needing a purchase order is a change to the books, made only by a role that manages billing`` () =
    Assert.Equal(Some(Summa.Access.Access.ManageBilling, "change whether a customer needs a purchase order"), bookCommand (CustomerPurchaseOrderRequirementSet true))
