/// A deployment's configuration (SUM-DATALOC-001, SUM0-003, SUM0-009,
/// SUM0-033): where its data lives, how people sign in, and which
/// organizations it serves. Every value comes from the deployment's
/// configuration document; Summa hard-codes no repository, owner, branch,
/// base path, sign-in host or client id.
///
/// ```json
/// {"environment":"production","environmentName":"production",
///  "location":{"owner":"acme","repository":"summa-data","branch":"main","basePath":"deployments/prod"},
///  "identity":{"exchange":"https://fides.acme.example","application":"summa-production",
///              "provider":"github","clientId":"Iv23li...","redirectUri":"https://summa.acme.example/"},
///  "organizations":[
///    {"id":"org_acme","displayName":"Acme Consulting","slug":"acme","defaultCurrency":"USD",
///     "timeZone":"America/New_York","administrators":["583231"]},
///    {"id":"org_eu","displayName":"Acme Europe","slug":"acme-eu","defaultCurrency":"EUR",
///     "timeZone":"Europe/Berlin","administrators":["583231"],
///     "location":{"owner":"acme-eu","repository":"summa-eu","branch":"main","basePath":""}}]}
/// ```
///
/// `location`, `identity` and `organizations` are optional together: a local
/// deployment configures neither storage nor sign-in. A deployment that
/// stores data on GitHub needs sign-in and names the organizations it
/// serves, the default first; an organization may keep its data in its own
/// repository (SUM0-009). The document is closed and every value validated.
/// Nothing in it is secret.
///
/// Pure.
module Summa.Storage.Deployment

open System
open Arca
open Summa.Storage.Diagnostics

/// One configured data location, as the configuration states it.
type LocationConfig =
    { Owner: string
      Repository: string
      Branch: string
      /// The folder application namespaces live under; empty for the repository root.
      BasePath: string }

/// How people sign in (SUM0-003): Fides' exchange and this deployment's
/// registration with it.
type IdentityConfig =
    { /// The exchange's origin, for example `https://fides.acme.example`.
      Exchange: string
      /// The application's id as registered with the exchange.
      Application: string
      /// The identity provider: `github`.
      Provider: string
      /// The GitHub App's public client id.
      ClientId: string
      /// The exact redirect URI registered for this deployment.
      RedirectUri: string }

/// An organization the deployment serves (SUM0-010).
type OrganizationConfig =
    { /// Immutable; it names the organization's folder.
      Id: string
      DisplayName: string
      Slug: string
      /// ISO 4217 code of the organization's books.
      DefaultCurrency: string
      /// The organization's business time zone (IANA id).
      TimeZone: string
      /// Where this organization's data lives when not at the deployment's
      /// location, for example its own repository (SUM0-009).
      Location: LocationConfig option
      /// GitHub numeric account ids allowed to set the organization up and be
      /// its first administrators. No one becomes one by opening it.
      Administrators: string list }

/// Where a deployment keeps binary artifacts such as PDFs (set 0 §0.35,
/// §0.38): never in the financial records, and never shared between
/// environments.
type ArtifactsConfig =
    /// IndexedDB in the browser, in a database named for the environment
    /// (`summa-artifacts-<environment>`, optionally with a suffix).
    | BrowserDatabase of name: string

type DeploymentConfig =
    { Environment: EnvironmentKind
      EnvironmentName: string
      Location: LocationConfig option
      Identity: IdentityConfig option
      Artifacts: ArtifactsConfig
      /// The default first.
      Organizations: OrganizationConfig list }

/// The identity providers Summa signs in with.
let providers = [ "github" ]

/// A location's Arca form.
let dataLocation (config: LocationConfig) =
    DataLocation.create config.Owner config.Repository config.Branch config.BasePath

/// A configured organization, by id. Organizations are discovered from the
/// configuration only, never by scanning the repository (SUM0-033).
let organization (config: DeploymentConfig) (organizationId: string) =
    config.Organizations |> List.tryFind (fun found -> found.Id = organizationId)

/// Whether a session's actor (`github:<numeric id>`) is one of the
/// organization's bootstrap administrators.
let isBootstrapAdministrator (organization: OrganizationConfig) (actorId: string) =
    match actorId.Split(':', 2) with
    | [| "github"; id |] -> List.contains id organization.Administrators
    | _ -> false

let private invalid detail = Error(InvalidDeploymentConfig detail)

let private read (decoded: Codec.Decoded<'a>) = decoded |> Result.mapError InvalidDeploymentConfig

let private closed names value = Codec.closed names value |> read

let private text name value = Codec.text name value |> read

let private locationOf value =
    closed [ "basePath"; "branch"; "owner"; "repository" ] value
    |> Result.bind (fun () ->
        match text "owner" value, text "repository" value, text "branch" value, text "basePath" value with
        | Ok owner, Ok repository, Ok branch, Ok basePath ->
            let config =
                { Owner = owner
                  Repository = repository
                  Branch = branch
                  BasePath = basePath }

            dataLocation config
            |> Result.mapError (LocationError.describe >> InvalidDataLocation)
            |> Result.map (fun _ -> config)
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

let private optionalLocation value =
    match Json.field "location" value with
    | None -> Ok None
    | Some found -> locationOf found |> Result.map Some

let private environmentOf =
    function
    | "local" -> Ok EnvironmentKind.Local
    | "test" -> Ok EnvironmentKind.Test
    | "staging" -> Ok EnvironmentKind.Staging
    | "production" -> Ok EnvironmentKind.Production
    | other -> invalid $"'{other}' is not local, test, staging or production"

let private loopback (uri: Uri) =
    uri.IsLoopback && (uri.Host = "localhost" || uri.Host = "127.0.0.1" || uri.Host = "[::1]")

/// An absolute https address (http only on this machine, for development),
/// with no user information; `originOnly` also refuses a path, query or fragment.
let private address (name: string) (originOnly: bool) (value: string) =
    let parsed =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, found -> Option.ofObj found
        | _ -> None

    match parsed with
    | Some uri ->
        let secure = uri.Scheme = Uri.UriSchemeHttps || (uri.Scheme = Uri.UriSchemeHttp && loopback uri)

        if not secure then
            invalid $"'{name}' must be an https address"
        elif uri.UserInfo <> "" then
            invalid $"'{name}' must not hold user information"
        elif originOnly && (uri.AbsolutePath <> "/" || uri.Query <> "" || uri.Fragment <> "" || value.EndsWith "/") then
            invalid $"'{name}' must be an origin, with no path"
        elif not originOnly && (uri.Query <> "" || uri.Fragment <> "") then
            invalid $"'{name}' must not have a query or fragment"
        else
            Ok value
    | None -> invalid $"'{name}' is not an absolute address"

let private identifier (name: string) (value: string) =
    if String.IsNullOrWhiteSpace value || value.Length > 200 || value |> Seq.exists (fun c -> Char.IsWhiteSpace c || Char.IsControl c) then
        invalid $"'{name}' is not an identifier"
    else
        Ok value

let private identityOf value =
    match Json.field "identity" value with
    | None -> Ok None
    | Some identity ->
        closed [ "application"; "clientId"; "exchange"; "provider"; "redirectUri" ] identity
        |> Result.bind (fun () ->
            match
                text "exchange" identity |> Result.bind (address "exchange" true),
                text "application" identity |> Result.bind (identifier "application"),
                text "provider" identity,
                text "clientId" identity |> Result.bind (identifier "clientId"),
                text "redirectUri" identity |> Result.bind (address "redirectUri" false)
            with
            | Ok exchange, Ok application, Ok provider, Ok clientId, Ok redirectUri ->
                if List.contains provider providers then
                    Ok(
                        Some
                            { Exchange = exchange
                              Application = application
                              Provider = provider
                              ClientId = clientId
                              RedirectUri = redirectUri }
                    )
                else
                    invalid $"'{provider}' is not an identity provider Summa signs in with"
            | Error e, _, _, _, _
            | _, Error e, _, _, _
            | _, _, Error e, _, _
            | _, _, _, Error e, _
            | _, _, _, _, Error e -> Error e)

/// GitHub numeric account ids: digits only, each once.
let private administratorsOf (value: Json) =
    match Json.field "administrators" value with
    | None -> Ok []
    | Some(Json.Array items) ->
        let ids =
            items
            |> List.map (function
                | Json.String id when id <> "" && id.Length <= 20 && id |> Seq.forall Char.IsAsciiDigit -> Ok id
                | _ -> invalid "'administrators' holds something other than a GitHub account number")

        match ids |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some error -> Error error
        | None ->
            let found = ids |> List.choose Result.toOption

            if (List.distinct found).Length <> found.Length then
                invalid "'administrators' names an account twice"
            else
                Ok found
    | Some _ -> invalid "'administrators' is not a list"

/// A validated organization from its configuration entry.
let private organizationOf (value: Json) =
    closed [ "administrators"; "defaultCurrency"; "displayName"; "id"; "location"; "slug"; "timeZone" ] value
    |> Result.bind (fun () ->
        match text "id" value, text "displayName" value, text "slug" value, text "defaultCurrency" value, text "timeZone" value with
        | Ok id, Ok displayName, Ok slug, Ok currency, Ok zone ->
            Organization.validateIdentity id displayName slug currency zone
            |> Result.bind (fun () -> optionalLocation value)
            |> Result.bind (fun location ->
                administratorsOf value
                |> Result.map (fun administrators ->
                    { Id = id
                      DisplayName = displayName
                      Slug = slug
                      DefaultCurrency = currency
                      TimeZone = zone
                      Location = location
                      Administrators = administrators }))
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e)

let private organizationsOf value =
    match Json.field "organizations" value with
    | None -> Ok []
    | Some(Json.Array items) ->
        items
        |> List.fold (fun state item -> state |> Result.bind (fun found -> organizationOf item |> Result.map (fun next -> found @ [ next ]))) (Ok [])
        |> Result.bind (fun organizations ->
            match organizations |> List.countBy _.Id |> List.tryFind (fun (_, count) -> count > 1) with
            | Some(id, _) -> invalid $"'{id}' is configured twice"
            | None -> Ok organizations)
    | Some _ -> invalid "'organizations' is not a list"

let private environmentText =
    function
    | EnvironmentKind.Local -> "local"
    | EnvironmentKind.Test -> "test"
    | EnvironmentKind.Staging -> "staging"
    | EnvironmentKind.Production -> "production"

/// The artifact store a deployment names, or its environment's own. A
/// browser database's name starts with `summa-artifacts-<environment>`, so
/// environments served from one origin never share one.
let private artifactsOf (environment: EnvironmentKind) value =
    let own = $"summa-artifacts-{environmentText environment}"

    match Json.field "artifacts" value with
    | None -> Ok(BrowserDatabase own)
    | Some artifacts ->
        closed [ "database"; "store" ] artifacts
        |> Result.bind (fun () ->
            match text "store" artifacts, text "database" artifacts with
            | Ok "browser", Ok database ->
                let suffix = if database.StartsWith own then database.Substring own.Length else "?"

                if suffix = "" || (suffix.StartsWith "-" && suffix.Length > 1 && suffix.Length <= 40 && suffix |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-')) then
                    Ok(BrowserDatabase database)
                else
                    invalid $"'database' must be '{own}' or start with '{own}-', so no other environment can share it"
            | Ok other, Ok _ -> invalid $"'{other}' is not an artifact store this build can use: 'browser'"
            | Error e, _
            | _, Error e -> Error e)

/// A deployment's configuration from its JSON text, every value validated.
let parse (document: string) : Result<DeploymentConfig, Diagnostic> =
    match Json.parse document with
    | Error error -> invalid (JsonError.describe error)
    | Ok value ->
        closed [ "artifacts"; "environment"; "environmentName"; "identity"; "location"; "organizations" ] value
        |> Result.bind (fun () ->
            match text "environment" value |> Result.bind environmentOf, text "environmentName" value with
            | Ok _, Ok name when String.IsNullOrWhiteSpace name -> Error(MissingField "environmentName")
            | Ok environment, Ok environmentName ->
                match optionalLocation value, organizationsOf value, identityOf value, artifactsOf environment value with
                | Ok location, Ok organizations, Ok identity, Ok artifacts ->
                    if location.IsNone && not organizations.IsEmpty then
                        invalid "'organizations' needs a 'location'"
                    elif location.IsSome && (identity.IsNone || organizations.IsEmpty) then
                        // Data on GitHub needs someone signed in to write it,
                        // and an organization to keep it under.
                        invalid "a 'location' needs 'identity' and 'organizations'"
                    else
                        Ok
                            { Environment = environment
                              EnvironmentName = environmentName
                              Location = location
                              Identity = identity
                              Artifacts = artifacts
                              Organizations = organizations }
                | Error e, _, _, _
                | _, Error e, _, _
                | _, _, Error e, _
                | _, _, _, Error e -> Error e
            | Error e, _
            | _, Error e -> Error e)
