/// The terminal panel: the shell on a pseudo-terminal (or pipes), its trust question and restart.
namespace FSharpened.Ide.Desktop

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Documents
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Input.Platform
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Styling
open Avalonia.Threading
open AvaloniaEdit.CodeCompletion
open AvaloniaEdit
open AvaloniaEdit.Document
open AvaloniaEdit.Rendering
open AvaloniaEdit.Search
open AvaloniaEdit.TextMate
open TextMateSharp.Grammars
open FSharpened.Ide
open FSharpened.Ide.Host

type TerminalPanel(ui: Ui) =
    let env = ui.Env
    let termView = ui.TermView
    let window = ui.Window

    // ---- terminal ---------------------------------------------------------------------------------
    /// Shows a message in the terminal (our own lines, not the shell's).
    let termAppend (text: string) =
        termView.Feed(text.Replace("\r\n", "\n").Replace("\n", "\r\n"))

    let stopTerminal () =
        match ui.Term with
        | Some t ->
            t.Stop()
            ui.Term <- None
        | None -> ()

    /// Starts the shell in the opened folder, after the same trust question Run asks: a terminal runs whatever is typed.
    let startTerminal () =
        match ui.Root, ui.Term with
        | None, _ -> termAppend "Open a folder first: the terminal starts in it.\n"
        | Some _, Some t when t.IsRunning() -> ()
        | Some r, _ ->
            task {
                let! allowed =
                    task {
                        if env.Trust.IsTrusted r then
                            return true
                        else
                            let! yes =
                                Dialogs.confirm
                                    window
                                    ui.Roles
                                    "Trust this folder?"
                                    (sprintf
                                        "A terminal starts your shell on this computer, with your permissions, in:\n\n%s\n\nAnything typed into it runs. Only trust folders whose code you would run anyway. You can revoke this from the command palette."
                                        r)
                                    "Trust and open terminal"

                            if yes then
                                env.Trust.Trust r

                            return yes
                    }

                if allowed then
                    let ptyShell = Terminal.defaultInteractiveShell ()
                    let pipeShell = Terminal.defaultShell ()
                    let mutable started: TermBackend option = None

                    let opened =
                        TermBackends.openShell
                            true
                            ptyShell
                            pipeShell
                            r
                            (max 20 termView.Cols)
                            (max 5 termView.Rows)
                            termView.Feed
                            (fun code ->
                                termAppend (
                                    sprintf
                                        "\n[%s ended with code %d]\n"
                                        (match started with
                                         | Some b -> b.Shell.Name
                                         | None -> ptyShell.Name)
                                        code
                                )

                                Dispatcher.UIThread.Post(fun () -> ui.Term <- None))

                    match opened with
                    | Ok(backend, note) ->
                        started <- Some backend
                        ui.Term <- Some backend
                        note |> Option.iter (fun n -> termAppend (n + "\n"))
                        termView.Focus() |> ignore
                    | Error e -> termAppend (e + "\n")
            }
            |> ignore

    let restartTerminal () =
        stopTerminal ()
        termView.Clear()
        startTerminal ()

    /// Sends a line to the shell, as if typed and followed by Enter.
    let sendTerminalLine (line: string) =
        match ui.Term with
        | Some t when t.IsRunning() -> t.Write(line + "\r")
        | _ -> termAppend "The shell is not running. Use Terminal: Restart.\n"

    do
        ui.TerminalShownFn <-
            (fun () ->
                if ui.Term.IsNone then
                    startTerminal ())

        // what is typed in the terminal goes to the shell; a window resize tells it the new size
        termView.Input.Add(fun text ->
            match ui.Term with
            | Some t when t.IsRunning() -> t.Write text
            | _ ->
                if text.Contains '\r' then
                    termAppend "The shell is not running. Use Terminal: Restart.\n")

        termView.Resized.Add(fun (c, r) ->
            match ui.Term with
            | Some t -> t.Resize(c, r)
            | None -> ())


    member _.RestartTerminal = restartTerminal
    member _.SendTerminalLine = sendTerminalLine
    member _.StopTerminal = stopTerminal
