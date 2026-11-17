module TerminalTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open FSharpened.Platform
open FSharpened.Ide.Host

[<Fact>]
let ``colours, cursor moves, titles and carriage returns are removed from what a shell prints`` () =
    let clean, held =
        Terminal.strip "\u001b[31mred\u001b[0m and \u001b]0;title\u0007plain\r\nnext\r"

    Assert.Equal("red and plain\nnext", clean)
    Assert.Equal("", held)

[<Fact>]
let ``a sequence split across two reads is held back and finished by the next one`` () =
    let first, held = Terminal.strip "abc\u001b[3"
    Assert.Equal("abc", first)
    Assert.Equal("\u001b[3", held)
    let second, rest = Terminal.strip (held + "1mdef")
    Assert.Equal("def", second)
    Assert.Equal("", rest)

[<Fact>]
let ``the shell is chosen by system, preferring what is installed, and never through a guess`` () =
    let all (_: string) = true
    let none (_: string) = false
    let only (names: string list) (n: string) = List.contains n names

    Assert.Equal("PowerShell", (Terminal.chooseShell Windows all None).Name)
    Assert.Equal("Windows PowerShell", (Terminal.chooseShell Windows (only [ "powershell.exe" ]) None).Name)
    Assert.Equal("Command Prompt", (Terminal.chooseShell Windows none None).Name)
    Assert.Equal("zsh", (Terminal.chooseShell MacOs all (Some "/bin/zsh")).Name)
    Assert.Equal("bash", (Terminal.chooseShell Linux (only [ "bash" ]) (Some "/no/such/shell")).Name)
    Assert.Equal("sh", (Terminal.chooseShell Linux none None).Name)

    // every shell is started from an argument list: the program is the first item, with no quoting
    for os in [ Windows; Linux; MacOs ] do
        let s = Terminal.chooseShell os all None
        Assert.False(s.Argv.IsEmpty)
        Assert.DoesNotContain(" ", s.Argv.Head)

let private waitFor (sb: StringBuilder) (text: string) =
    let deadline = DateTime.UtcNow.AddSeconds 30.0

    while not (lock sb (fun () -> sb.ToString().Contains text))
          && DateTime.UtcNow < deadline do
        Thread.Sleep 50

[<Fact>]
let ``a real shell runs in the folder, answers what is typed, and ends when asked`` () =
    DiskTests.withFolder (fun dir ->
        let shell = Terminal.defaultShell ()
        let seen = StringBuilder()
        let mutable code = None

        let session =
            TermSession(
                shell,
                dir,
                (fun s -> lock seen (fun () -> seen.Append s |> ignore)),
                (fun c -> code <- Some c)
            )

        match session.Start() with
        | Error e -> failwith e
        | Ok() ->
            try
                Assert.True session.IsRunning
                session.Send "echo fsh-terminal-ok"
                waitFor seen "fsh-terminal-ok"
                Assert.Contains("fsh-terminal-ok", lock seen (fun () -> seen.ToString()))

                // it starts in the folder it was given
                session.Send(if shell.Name = "Command Prompt" then "cd" else "pwd")
                waitFor seen (Path.GetFileName dir)
                Assert.Contains(Path.GetFileName dir, lock seen (fun () -> seen.ToString()))

                session.Send "exit"
                let deadline = DateTime.UtcNow.AddSeconds 20.0

                while code.IsNone && DateTime.UtcNow < deadline do
                    Thread.Sleep 50

                Assert.True(code.IsSome, "the shell should have ended after `exit`")
            finally
                session.Stop())

[<Fact>]
let ``a shell that is not there is an error, not an exception`` () =
    let session =
        TermSession(
            { Name = "none"
              Argv = [ "fsh-no-such-shell-" + Guid.NewGuid().ToString "N" ] },
            Path.GetTempPath(),
            ignore,
            ignore
        )

    Assert.True((session.Start()).IsError)
