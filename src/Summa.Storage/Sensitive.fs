/// What Summa never stores (SUM3-022, SUM3-023, INV-SEC-003, INV-SEC-004):
/// full card numbers, full bank account numbers and credentials. Summa
/// does not process payments, so it has no reason to hold them; a payment
/// reference or a note keeps only what identifies the payment, such as the
/// last four digits. Every record a command writes is scanned before it is
/// stored, and one that holds such a value is refused.
///
/// Pure.
module Summa.Storage.Sensitive

open System
open System.Text.RegularExpressions

/// What a text holds that must not be stored, in words.
type Finding =
    | CardNumber
    | BankAccountNumber
    | Credential of kind: string

let describe =
    function
    | CardNumber -> "a full payment card number"
    | BankAccountNumber -> "a full bank account number (IBAN)"
    | Credential kind -> $"a credential ({kind})"

let private options = RegexOptions.CultureInvariant

/// The Luhn checksum every payment card number passes.
let private luhn (digits: string) =
    let total =
        digits
        |> Seq.rev
        |> Seq.mapi (fun i c ->
            let d = int c - int '0'
            if i % 2 = 1 then (if d * 2 > 9 then d * 2 - 9 else d * 2) else d)
        |> Seq.sum

    total % 10 = 0

/// 13 to 19 digits, grouped by spaces or hyphens or not at all, standing
/// alone (not inside an id or a hash), passing Luhn.
let private cardPattern = Regex(@"(?<![\w])(?:\d[ -]?){12,18}\d(?![\w])", options)

/// An IBAN-shaped value: country, check digits and account groups.
let private ibanPattern = Regex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}\b", options)

/// The text with every IBAN-shaped value blanked out. The account part of
/// an IBAN is a long digit run that can pass Luhn by chance; it is judged
/// by the IBAN rule alone, never read as a card number.
let private withoutIbanShapes (text: string) = ibanPattern.Replace(text, " ")

let private hasCard (text: string) =
    cardPattern.Matches(withoutIbanShapes text)
    |> Seq.exists (fun m ->
        let digits = String(m.Value |> Seq.filter Char.IsDigit |> Array.ofSeq)
        digits.Length >= 13 && digits.Length <= 19 && digits |> Seq.distinct |> Seq.length > 1 && luhn digits)

/// An IBAN passes the mod-97 check over its check digits and account.
let private validIban (candidate: string) =
    let compact = candidate.Replace(" ", "")
    let rearranged = compact.Substring 4 + compact.Substring(0, 4)

    let numeric =
        rearranged
        |> Seq.map (fun c -> if Char.IsDigit c then string c else string (int c - int 'A' + 10))
        |> String.concat ""

    numeric |> Seq.fold (fun (r: int) c -> (r * 10 + (int c - int '0')) % 97) 0 = 1

let private hasIban (text: string) =
    ibanPattern.Matches text |> Seq.exists (fun m -> validIban m.Value)

let private credentials =
    [ "GitHub token", Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b", options)
      "private key", Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", options)
      "AWS access key", Regex(@"\bAKIA[0-9A-Z]{16}\b", options)
      "Slack token", Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", options)
      "bearer token", Regex(@"\bBearer\s+[A-Za-z0-9\-._~+/]{20,}=*", options) ]

/// Everything a text holds that Summa must not store.
let scan (text: string) : Finding list =
    [ if hasCard text then CardNumber
      if hasIban text then BankAccountNumber
      for kind, pattern in credentials do
          if pattern.IsMatch text then Credential kind ]
