namespace FSharpened.Ide

open System.Text
open FSharpened.Platform

/// What the user is working on. Immutable: every edit or restore produces a new value.
type Workspace =
    {
        Active: Language
        /// Language id -> buffer text.
        Buffers: Map<string, string>
        Stdin: string
    }

module Workspace =
    let initial =
        { Active = FSharp
          Buffers =
            Language.all
            |> List.map (fun l -> Language.id l, (Templates.defaultFor l).Code)
            |> Map.ofList
          Stdin = "" }

    let text (lang: Language) (w: Workspace) =
        match Map.tryFind (Language.id lang) w.Buffers with
        | Some t -> t
        | None -> (Templates.defaultFor lang).Code

    let withText (lang: Language) (code: string) (w: Workspace) =
        { w with
            Buffers = Map.add (Language.id lang) code w.Buffers }

    let activate (lang: Language) (w: Workspace) = { w with Active = lang }
    let withStdin (s: string) (w: Workspace) = { w with Stdin = s }

    // ---- storage in a statepoint overlay (keys are stable so snapshots share structure) ----
    let private enc (s: string) = Encoding.UTF8.GetBytes s
    let private dec (b: byte[]) = Encoding.UTF8.GetString b

    let write (store: IOverlayStore) (w: Workspace) =
        for l in Language.all do
            store.Put("buffer/" + Language.id l, enc (text l w))

        store.Put("meta/active", enc (Language.id w.Active))
        store.Put("meta/stdin", enc w.Stdin)

    let read (store: IOverlayStore) : Workspace =
        let get key fallback =
            match store.Get key with
            | Some b -> dec b
            | None -> fallback

        let buffers =
            Language.all
            |> List.map (fun l -> Language.id l, get ("buffer/" + Language.id l) (Templates.defaultFor l).Code)
            |> Map.ofList

        { Active = get "meta/active" "fsharp" |> Language.tryOfId |> Option.defaultValue FSharp
          Buffers = buffers
          Stdin = get "meta/stdin" "" }
