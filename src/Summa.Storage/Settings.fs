/// Configuration scopes (SUM0-041, SUM0-042): application, environment,
/// organization and principal. Business configuration lives in data (the
/// organization manifest, the deployment document, a principal's
/// preferences), so changing it needs no deployment; accounting rules stay
/// in code. Each setting declares the scopes it may be set at, and the most
/// specific value wins.
///
/// Pure.
module Summa.Storage.Settings

type Scope =
    | ApplicationScope
    | EnvironmentScope
    | OrganizationScope of organizationId: string
    | PrincipalScope of organizationId: string * principalId: string

let private rank =
    function
    | ApplicationScope -> 0
    | EnvironmentScope -> 1
    | OrganizationScope _ -> 2
    | PrincipalScope _ -> 3

/// A setting: its name, the scopes it may be set at, and its default.
type Setting =
    { Name: string
      AllowedAt: Set<int>
      Default: string }

let private scopes (allowed: Scope list) = allowed |> List.map rank |> Set.ofList

/// Settings Summa knows. Accounting defaults are organization settings,
/// never per person; display preferences may be personal.
let known =
    [ { Name = "invoice.numberPrefix"; AllowedAt = scopes [ OrganizationScope "" ]; Default = "INV" }
      { Name = "invoice.defaultTermsDays"; AllowedAt = scopes [ OrganizationScope "" ]; Default = "30" }
      { Name = "fiscal.yearStartMonth"; AllowedAt = scopes [ OrganizationScope "" ]; Default = "1" }
      { Name = "display.dateFormat"; AllowedAt = scopes [ ApplicationScope; OrganizationScope ""; PrincipalScope("", "") ]; Default = "yyyy-MM-dd" }
      { Name = "display.pageSize"; AllowedAt = scopes [ ApplicationScope; PrincipalScope("", "") ]; Default = "50" }
      { Name = "storage.indexRebuildMinutes"; AllowedAt = scopes [ ApplicationScope; EnvironmentScope ]; Default = "60" } ]

type Value = { Setting: string; Scope: Scope; Value: string }

type SettingProblem =
    | UnknownSetting of string
    | NotSettableAt of setting: string * scope: Scope

/// Every value that may not be stored where it was set.
let problems (values: Value list) =
    values
    |> List.choose (fun v ->
        match known |> List.tryFind (fun s -> s.Name = v.Setting) with
        | None -> Some(UnknownSetting v.Setting)
        | Some s when not (s.AllowedAt.Contains(rank v.Scope)) -> Some(NotSettableAt(v.Setting, v.Scope))
        | Some _ -> None)

let private applies (organizationId: string) (principalId: string) =
    function
    | ApplicationScope
    | EnvironmentScope -> true
    | OrganizationScope o -> o = organizationId
    | PrincipalScope(o, p) -> o = organizationId && p = principalId

/// The effective value of a setting for a principal in an organization: the
/// most specific valid value that applies, else the default.
let resolve (values: Value list) (organizationId: string) (principalId: string) (name: string) : string option =
    known
    |> List.tryFind (fun s -> s.Name = name)
    |> Option.map (fun setting ->
        values
        |> List.filter (fun v -> v.Setting = name && setting.AllowedAt.Contains(rank v.Scope) && applies organizationId principalId v.Scope)
        |> List.sortByDescending (fun v -> rank v.Scope)
        |> List.tryHead
        |> Option.map _.Value
        |> Option.defaultValue setting.Default)
