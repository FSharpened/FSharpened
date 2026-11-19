module PtyTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open FSharpened.Platform
open FSharpened.Ide.Host

/// Output of a terminal, drawn on a screen the way the desktop IDE does it.
type private Capture(cols: int, rows: int) =
    let gate = obj ()
    let screen = Screen(cols, rows, ignore)
    let mutable exitCode: int option = None

    member _.Screen = screen

    member this.Feed(text: string) =
        lock gate (fun () -> this.Screen.Feed text)

    member this.Exit(code: int) =
        lock gate (fun () -> exitCode <- Some code)

    member _.ExitCode = lock gate (fun () -> exitCode)

    member this.Text() =
        lock gate (fun () ->
            let sb = StringBuilder()

            for i in 0 .. this.Screen.ScrollbackCount - 1 do
                sb.AppendLine(this.Screen.ScrollbackText i) |> ignore

            sb.Append(this.Screen.Text()).ToString())

    member this.WaitFor(text: string, seconds: float) =
        let deadline = DateTime.UtcNow.AddSeconds seconds

        while not ((this.Text()).Contains text) && DateTime.UtcNow < deadline do
            Thread.Sleep 50

        (this.Text()).Contains text

let private isWindows = (RuntimeIdentity.detect ()).Os = Windows

let private openOrSkip (cwd: string) (cap: Capture) =
    let shell = Terminal.defaultInteractiveShell ()
    let session = PtySession(shell, cwd, 100, 30, cap.Feed, cap.Exit)
    // the build scripts set NO_COLOR for their own logs; the shells honour it, and a test of colour must not
    session.Env <- [ "NO_COLOR", null ]

    match session.Start() with
    | Ok() -> session
    | Error e -> failwithf "a pseudo-terminal should start here: %s" e

[<Fact>]
let ``windows get quoted the way CommandLineToArgvW reads them back`` () =
    Assert.Equal("a b", Pty.windowsCommandLine [ "a"; "b" ])
    Assert.Equal("\"a b\" c", Pty.windowsCommandLine [ "a b"; "c" ])
    Assert.Equal("\"say \\\"hi\\\"\"", Pty.windowsCommandLine [ "say \"hi\"" ])
    Assert.Equal("\"C:\\dir with space\\\\\"", Pty.windowsCommandLine [ "C:\\dir with space\\" ])
    Assert.Equal("\"\"", Pty.windowsCommandLine [ "" ])

[<Fact>]
let ``on Linux and macOS the program is started through a wrapper that changes folder, so no thread changes folder``
    ()
    =
    let w = Pty.posixWrap "/work dir" [ "bash"; "-i" ]
    Assert.Equal<string list>([ "/bin/sh"; "-c"; "cd \"$0\" && exec \"$@\""; "/work dir"; "bash"; "-i" ], w)

[<Fact>]
let ``a program on a pseudo-terminal sees a terminal`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)

        let argv =
            if isWindows then
                [ "powershell.exe"
                  "-NoLogo"
                  "-NoProfile"
                  "-Command"
                  "if (-not [Console]::IsOutputRedirected -and -not [Console]::IsInputRedirected) { 'tty-' + 'yes' } else { 'tty-' + 'no' }" ]
            else
                [ "sh"
                  "-c"
                  "if test -t 0 && test -t 1; then echo tty-yes; else echo tty-no; fi" ]

        let shell = { Name = "probe"; Argv = argv }
        let session = PtySession(shell, dir, 100, 30, cap.Feed, cap.Exit)

        match session.Start() with
        | Error e -> failwith e
        | Ok() ->
            try
                Assert.True(cap.WaitFor("tty-yes", 40.0), "output was: " + cap.Text())
            finally
                session.Stop())

[<Fact>]
let ``an interactive shell on a pseudo-terminal runs commands, in the folder, with a screen that shows them`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)
        let session = openOrSkip dir cap

        try
            Assert.True session.IsRunning
            session.Write "echo fsh-pty-ok\r"
            Assert.True(cap.WaitFor("fsh-pty-ok", 40.0), "output was: " + cap.Text())
            session.Write(if isWindows then "(Get-Location).Path\r" else "pwd\r")
            Assert.True(cap.WaitFor(Path.GetFileName dir, 20.0), "output was: " + cap.Text())
        finally
            session.Stop())

[<Fact>]
let ``Ctrl+C interrupts a running command and the shell stays usable`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)
        let session = openOrSkip dir cap

        try
            session.Write "echo ready-fsh\r"
            Assert.True(cap.WaitFor("ready-fsh", 40.0), "output was: " + cap.Text())
            session.Write(if isWindows then "Start-Sleep 60\r" else "sleep 60\r")
            Thread.Sleep 1500
            session.Write "\u0003"
            Thread.Sleep 1000
            session.Write "echo after-fsh\r"
            Assert.True(cap.WaitFor("after-fsh", 20.0), "output was: " + cap.Text())
            Assert.True session.IsRunning
        finally
            session.Stop())

[<Fact>]
let ``resizing the terminal tells the program`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)
        let session = openOrSkip dir cap

        try
            session.Write "echo up-fsh\r"
            Assert.True(cap.WaitFor("up-fsh", 40.0), "output was: " + cap.Text())
            session.Resize(77, 25)
            Thread.Sleep 500

            session.Write(
                if isWindows then
                    "echo size-$([Console]::WindowWidth)x$([Console]::WindowHeight)\r"
                else
                    "echo size-$(stty size)\r"
            )

            let expected = if isWindows then "size-77x25" else "size-25 77"
            Assert.True(cap.WaitFor(expected, 20.0), "output was: " + cap.Text())
        finally
            session.Stop())

[<Fact>]
let ``colours that a program prints reach the screen as styles`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)
        let session = openOrSkip dir cap

        try
            session.Write(
                if isWindows then
                    "Write-Host (\"colour-\" + \"fsh\") -ForegroundColor Red\r"
                else
                    "printf '\\033[31mcolour-fsh\\033[0m\\n'\r"
            )

            Assert.True(cap.WaitFor("colour-fsh", 40.0), "output was: " + cap.Text())

            // the text can arrive a moment before the colour it was printed in is applied, so look until it shows
            let styled () =
                lock cap (fun () ->
                    [ for r in 0 .. cap.Screen.Rows - 1 do
                          let row = cap.Screen.Row r
                          let text = cap.Screen.RowText r
                          let i = text.IndexOf "colour-fsh"

                          if
                              i >= 0
                              && not (text.Contains "echo" || text.Contains "printf" || text.Contains "Write-Host")
                          then
                              yield row[i].Style.Fg ])

            let deadline = DateTime.UtcNow.AddSeconds 20.0

            while not (styled () |> List.exists (fun fg -> fg <> Default))
                  && DateTime.UtcNow < deadline do
                Thread.Sleep 100

            Assert.True(
                styled () |> List.exists (fun fg -> fg <> Default),
                sprintf "styles were %A; output was: %s" (styled ()) (cap.Text())
            )
        finally
            session.Stop())

[<Fact>]
let ``the shell ending reports its exit code once`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)
        let session = openOrSkip dir cap

        try
            session.Write "echo bye-fsh\r"
            Assert.True(cap.WaitFor("bye-fsh", 40.0))
            session.Write "exit 3\r"
            let deadline = DateTime.UtcNow.AddSeconds 20.0

            while cap.ExitCode.IsNone && DateTime.UtcNow < deadline do
                Thread.Sleep 50

            Assert.Equal(Some 3, cap.ExitCode)
        finally
            session.Stop())

[<Fact>]
let ``stopping ends the shell and what it started`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)
        let session = openOrSkip dir cap
        session.Write "echo run-fsh\r"
        Assert.True(cap.WaitFor("run-fsh", 40.0))
        session.Stop()
        Thread.Sleep 1500
        Assert.False session.IsRunning)

[<Fact>]
let ``a shell that is not there is an error, never an exception`` () =
    let missing = "fsh-no-such-shell-" + Guid.NewGuid().ToString "N"

    let started =
        Pty.start
            { Argv = [ missing ]
              Cwd = Path.GetTempPath()
              Env = []
              Cols = 80
              Rows = 24 }
            ignore
            ignore

    // Windows refuses at once; Linux and macOS start `sh`, which then fails to find it and ends with 127
    match started with
    | Error _ -> ()
    | Ok p ->
        Thread.Sleep 1500
        Assert.False p.IsRunning
        p.Dispose()

[<Fact>]
let ``without a pseudo-terminal the pipe session still serves, line by line`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)
        let shell = Terminal.defaultShell ()

        match TermBackends.openShell false shell shell dir 100 30 cap.Feed cap.Exit with
        | Error e -> failwith e
        | Ok(backend, note) ->
            try
                Assert.False backend.IsPty
                Assert.True note.IsSome
                backend.Write "echo fsh-pipe-ok\r"
                Assert.True(cap.WaitFor("fsh-pipe-ok", 40.0), "output was: " + cap.Text())
            finally
                backend.Stop())

[<Fact>]
let ``the usual way opens a pseudo-terminal`` () =
    DiskTests.withFolder (fun dir ->
        let cap = Capture(100, 30)

        match
            TermBackends.openShell
                true
                (Terminal.defaultInteractiveShell ())
                (Terminal.defaultShell ())
                dir
                100
                30
                cap.Feed
                cap.Exit
        with
        | Error e -> failwith e
        | Ok(backend, note) ->
            try
                Assert.True(backend.IsPty, sprintf "fell back: %A" note)
                Assert.True note.IsNone
            finally
                backend.Stop())

[<Fact>]
let ``the line editor echoes, edits, keeps a history and ends lines`` () =
    let echoed = StringBuilder()
    let lines = ResizeArray<string>()
    let ed = LineEditor((fun s -> echoed.Append s |> ignore), lines.Add)
    ed.Feed "ls -x\u007f\u007fla\r"
    Assert.Equal<string list>([ "ls la" ], List.ofSeq lines)
    Assert.Contains("ls -x", echoed.ToString())
    ed.Feed "pwd\r"
    ed.Feed "\u001b[A\r"
    Assert.Equal<string list>([ "ls la"; "pwd"; "pwd" ], List.ofSeq lines)
    ed.Feed "\u001b[A\u001b[A\r"
    Assert.Equal("ls la", lines[lines.Count - 1])
    ed.Feed "junk\u0003"
    Assert.Equal("", ed.Current)
    ed.Feed "\u001b[1;5Ck\r"
    Assert.Equal("k", lines[lines.Count - 1])

[<Fact>]
let ``the interactive shell is chosen without any flag that would stop it being interactive`` () =
    let all (_: string) = true
    let none (_: string) = false
    Assert.Equal<string list>([ "pwsh.exe"; "-NoLogo" ], (Terminal.chooseInteractive Windows all None).Argv)
    Assert.Equal<string list>([ "cmd.exe" ], (Terminal.chooseInteractive Windows none None).Argv)
    Assert.Equal("zsh", (Terminal.chooseInteractive MacOs all (Some "/bin/zsh")).Name)

    Assert.Equal<string list>(
        [ "bash"; "--noprofile"; "--norc" ],
        (Terminal.chooseInteractive Linux (fun n -> n = "bash") None).Argv
    )
