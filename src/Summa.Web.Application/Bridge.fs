/// Asynchronous clients inside the Limen request/reply loop (DF-SUMMA-2026-0013).
///
/// Fides' sign-in client is asynchronous: it asks its host for browser
/// services and continues with the answers. Here every such service is a
/// kernel request. A call parks its continuation under a fresh correlation
/// id; the request goes out with the engine's next reply (`Drain`); when the
/// kernel answers, `Answer` resumes the continuation, which runs until the
/// client needs the browser again or finishes. A finished operation yields
/// engine messages.
///
/// This is the application's one piece of mutable machinery, kept at the
/// edge: the engine stays a pure function of its messages. The browser
/// runtime has one thread, so all of this runs synchronously inside one
/// dispatch; nothing here touches the browser itself.
module Summa.Web.Application.Bridge

open System.Collections.Generic
open Summa.Web.Engine.Accounting

/// What a client asked of the browser.
type KernelCall =
    /// A POST of a JSON body, its response read as text.
    | Post of url: string * body: string * timeoutMs: int
    | DeviceGet of key: string
    | DeviceSet of key: string * value: string
    | DeviceRemove of key: string
    | TabGet of key: string
    | TabSet of key: string * value: string
    | TabRemove of key: string
    /// Leave the page for the identity provider's sign-in page.
    | Leave of url: string
    | ReplaceAddress of url: string
    | Announce of message: string

/// How an Http request ended, as the kernel reported it.
type HttpAnswer =
    | Responded of status: int * body: string
    | Unreachable of reason: string
    /// It may have reached the server; nothing is known of its effect.
    | Unknown of reason: string

/// What the kernel answered.
type KernelAnswer =
    | Http of HttpAnswer
    | Read of value: string option
    | Done
    /// The kernel offers no such service, or did not run the request.
    | Missing

/// One page's in-flight work.
[<Sealed>]
type Bridge() =
    let waiting = Dictionary<string, KernelAnswer -> unit>()
    let outbox = List<string * KernelCall>()
    let finished = List<Msg>()
    let failures = List<exn>()
    let mutable sequence = 0

    /// Asks the browser for something and continues with its answer.
    member _.Call(request: KernelCall) : Async<KernelAnswer> =
        Async.FromContinuations(fun (resume, _, _) ->
            sequence <- sequence + 1
            let id = $"bridge-{sequence}"
            waiting[id] <- resume
            outbox.Add(id, request))

    /// Runs an operation to its first browser call (or its end), recording
    /// the messages it finishes with, or the exception it failed with.
    member _.Start(work: Async<Msg list>) =
        Async.StartImmediate(
            async {
                match! Async.Catch work with
                | Choice1Of2 messages -> finished.AddRange messages
                | Choice2Of2 error -> failures.Add error
            }
        )

    /// Resumes the operation waiting on this correlation id; false when none is.
    member _.Answer (id: string) (answer: KernelAnswer) =
        match waiting.TryGetValue id with
        | true, resume ->
            waiting.Remove id |> ignore
            resume answer
            true
        | _ -> false

    /// Whether an operation waits on this correlation id.
    member _.Waits(id: string) = waiting.ContainsKey id

    /// The browser calls made and the engine messages produced since the last
    /// drain. An operation that failed unexpectedly is an exception.
    member _.Drain() : Result<(string * KernelCall) list * Msg list, exn> =
        let calls = List.ofSeq outbox
        let messages = List.ofSeq finished
        let failed = List.ofSeq failures
        outbox.Clear()
        finished.Clear()
        failures.Clear()

        match failed with
        | error :: _ -> Error error
        | [] -> Ok(calls, messages)
