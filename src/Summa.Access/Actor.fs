/// The actor every financial command carries (SUM0-023, SUM3-012, SUM3-014,
/// INV-PROV-001, INV-PROV-002): who acted, of what kind, and the identifiers
/// that let the audit trace it. Anonymous mutation is impossible because a
/// command cannot be built without one (SUM3-012).
///
/// Pure.
module Summa.Access.Actor

open System
open Summa.Access.Access

/// An agent's self-reported identity (Praxis-compatible: provider, model and
/// runtime, literal `unknown` when not known). Provenance, never authority
/// (INV-PROV-006).
type AgentIdentity =
    { Provider: string
      Model: string
      Runtime: string }

type Actor =
    { Kind: PrincipalKind
      /// Stable id: `github:<numeric id>` for a person signed in with GitHub,
      /// the configured id for an agent, service or integration.
      ActorId: string
      /// The GitHub login, for display only; never the identity.
      GitHubLogin: string option
      Agent: AgentIdentity option
      /// Opaque, stored as supplied, never parsed or dereferenced (INV-PROV-002).
      ExecutionId: string option
      SourceSystem: string option
      CorrelationId: string }

/// Why an actor cannot be accepted.
type ActorProblem =
    | MissingActorId
    | MissingCorrelation
    /// An agent must say so: a person signed in with GitHub is `github:`;
    /// anything else claiming a person's form is refused (SUM3-014).
    | AgentAsPerson
    | AgentWithoutIdentity
    | CredentialInActor of field: string

let private credentialShaped (value: string) =
    let trimmed = value.TrimStart()

    [ "ghp_"; "gho_"; "ghu_"; "ghs_"; "ghr_"; "github_pat_" ]
    |> List.exists (fun prefix -> trimmed.StartsWith(prefix, StringComparison.Ordinal))
    || trimmed.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)
    || value.Contains("-----BEGIN", StringComparison.Ordinal)

/// Every problem with an actor; empty when a command may carry it. Tokens
/// never travel in an actor (SUM0-004, INV-PROV-008).
let problems (actor: Actor) =
    let blank = String.IsNullOrWhiteSpace

    let texts =
        [ "actorId", Some actor.ActorId
          "gitHubLogin", actor.GitHubLogin
          "executionId", actor.ExecutionId
          "sourceSystem", actor.SourceSystem
          "correlationId", Some actor.CorrelationId ]
        @ (actor.Agent
           |> Option.map (fun a -> [ "agent.provider", Some a.Provider; "agent.model", Some a.Model; "agent.runtime", Some a.Runtime ])
           |> Option.defaultValue [])

    [ if blank actor.ActorId then MissingActorId
      if blank actor.CorrelationId then MissingCorrelation
      if actor.Kind <> Human && actor.ActorId.StartsWith("github:", StringComparison.Ordinal) then AgentAsPerson
      if actor.Kind = Human && actor.Agent.IsSome then AgentAsPerson
      if actor.Kind = Agent && actor.Agent.IsNone then AgentWithoutIdentity
      yield!
          texts
          |> List.choose (fun (field, value) ->
              match value with
              | Some text when credentialShaped text -> Some(CredentialInActor field)
              | _ -> None) ]

/// A person signed in with GitHub: the numeric account id is the identity,
/// the login only a display name.
let person (gitHubId: string) (login: string) (correlationId: string) =
    { Kind = Human
      ActorId = $"github:{gitHubId}"
      GitHubLogin = Some login
      Agent = None
      ExecutionId = None
      SourceSystem = None
      CorrelationId = correlationId }

/// An agent: never a person, always with its identity and execution.
let agent (agentId: string) (identity: AgentIdentity) (executionId: string option) (correlationId: string) =
    { Kind = Agent
      ActorId = agentId
      GitHubLogin = None
      Agent = Some identity
      ExecutionId = executionId
      SourceSystem = None
      CorrelationId = correlationId }
