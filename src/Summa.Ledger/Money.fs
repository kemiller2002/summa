/// Fixed-decimal money (v0.1 §1.2, v0.2 §26): an integer count of minor
/// units (cents) in one currency. Never floating point; arithmetic is exact
/// and currencies never mix silently.
module Summa.Ledger.Money

open System
open System.Globalization

[<Struct; CustomEquality; CustomComparison>]
type Money =
    { Currency: string
      /// Minor units (cents for USD).
      Minor: int64 }

    override this.Equals other =
        match other with
        | :? Money as o -> o.Currency = this.Currency && o.Minor = this.Minor
        | _ -> false

    override this.GetHashCode() = HashCode.Combine(this.Currency, this.Minor)

    interface IComparable with
        member this.CompareTo other =
            match other with
            | :? Money as o when o.Currency = this.Currency -> compare this.Minor o.Minor
            | :? Money as o -> invalidArg "other" $"cannot compare {this.Currency} with {o.Currency}"
            | _ -> invalidArg "other" "not money"

    override this.ToString() =
        let sign = if this.Minor < 0L then "-" else ""
        let absolute = abs this.Minor
        $"{sign}{absolute / 100L}.{absolute % 100L:D2} {this.Currency}"

let usd (minor: int64) = { Currency = "USD"; Minor = minor }

let zero (currency: string) = { Currency = currency; Minor = 0L }

/// Parses "6050", "6050.5" or "6050.50" exactly; more than two decimal
/// places, exponents or anything else is refused rather than rounded.
let tryParse (currency: string) (text: string) : Money option =
    match Decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) with
    | true, value when Decimal.Round(value, 2) = value -> Some { Currency = currency; Minor = int64 (value * 100m) }
    | _ -> None

let private same (a: Money) (b: Money) =
    if a.Currency <> b.Currency then invalidOp $"currency mismatch: {a.Currency} and {b.Currency}"

let add (a: Money) (b: Money) =
    same a b
    { a with Minor = a.Minor + b.Minor }

let subtract (a: Money) (b: Money) =
    same a b
    { a with Minor = a.Minor - b.Minor }

let sum (currency: string) (amounts: Money seq) = amounts |> Seq.fold add (zero currency)

/// Quantity (in thousandths, so 7.5 hours is 7500) times a unit price,
/// rounded half away from zero to the minor unit, exactly.
let extend (quantityThousandths: int64) (unitPrice: Money) =
    let product = decimal quantityThousandths * decimal unitPrice.Minor / 1000m
    { unitPrice with Minor = int64 (Decimal.Round(product, 0, MidpointRounding.AwayFromZero)) }

/// An amount in words for a record of what changed: `175.00 USD`.
let text (m: Money) =
    let sign = if m.Minor < 0L then "-" else ""
    let a = abs m.Minor
    $"{sign}{a / 100L}.{a % 100L:D2} {m.Currency}"

let isPositive (m: Money) = m.Minor > 0L
let isNegative (m: Money) = m.Minor < 0L
