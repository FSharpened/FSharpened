/// Starting programs by name: the PATH and PATHEXT lookup, and the quoting that lets a Windows `.cmd` or `.bat` shim (what
/// npm installs for a language server) start without its arguments being able to add commands.
module LaunchTests

open System
open System.IO
open Xunit
open FSharpened.Ide.Host

/// A machine described in the test: which files exist, what PATH and PATHEXT say.
let private machine (windows: bool) (path: string) (files: string list) : LaunchEnv =
    let present =
        Collections.Generic.HashSet<string>(files, StringComparer.OrdinalIgnoreCase)

    { IsWindows = windows
      Path = path
      PathExt = ".COM;.EXE;.BAT;.CMD"
      Exists = present.Contains
      Cmd = @"C:\Windows\System32\cmd.exe" }

let private win files =
    machine true @"C:\tools;C:\Users\me\AppData\Roaming\npm" files

// ---- finding the program -------------------------------------------------------------------------

[<Fact>]
let ``a name is found along the PATH with the PATHEXT extensions, in PATHEXT order`` () =
    let env =
        win
            [ @"C:\tools\tsc.cmd"
              @"C:\tools\tsc.exe"
              @"C:\Users\me\AppData\Roaming\npm\gopls.cmd" ]

    Assert.Equal(Some @"C:\tools\tsc.exe", Launch.resolve env "tsc")
    Assert.Equal(Some @"C:\Users\me\AppData\Roaming\npm\gopls.cmd", Launch.resolve env "gopls")
    Assert.Equal(None, Launch.resolve env "nothing")

[<Fact>]
let ``an extensionless file next to its shim is not the program on Windows`` () =
    // npm installs `typescript-language-server` (a script for a Unix shell) beside `typescript-language-server.cmd`
    let env =
        win
            [ @"C:\Users\me\AppData\Roaming\npm\typescript-language-server"
              @"C:\Users\me\AppData\Roaming\npm\typescript-language-server.cmd" ]

    Assert.Equal(
        Some @"C:\Users\me\AppData\Roaming\npm\typescript-language-server.cmd",
        Launch.resolve env "typescript-language-server"
    )

[<Fact>]
let ``a name that already has an extension is tried as it is, in any case`` () =
    let env = win [ @"C:\tools\Server.CMD" ]
    Assert.Equal(Some @"C:\tools\Server.CMD", Launch.resolve env "Server.CMD")
    // the file system is not case sensitive, so a lower-case name finds it too
    Assert.Equal(Some @"C:\tools\server.cmd", Launch.resolve env "server.cmd")

[<Fact>]
let ``a path is not looked up along the PATH and the current folder is never searched`` () =
    let env = win [ @"D:\here\x.exe"; @"C:\tools\x.exe"; @"x.exe" ]
    Assert.Equal(Some @"D:\here\x.exe", Launch.resolve env @"D:\here\x")
    Assert.Equal(Some @"C:\tools\x.exe", Launch.resolve env "x")
    Assert.Equal(None, Launch.resolve (win [ @"x.exe"; @".\x.exe" ]) "x")

[<Fact>]
let ``quoted PATH entries and empty ones are handled`` () =
    let env =
        machine true "\"C:\\Program Files\\Go\\bin\";;C:\\tools" [ @"C:\Program Files\Go\bin\gopls.exe" ]

    Assert.Equal(Some @"C:\Program Files\Go\bin\gopls.exe", Launch.resolve env "gopls")

[<Fact>]
let ``on other systems the name is the first executable file on the PATH`` () =
    let env =
        machine false "/usr/local/bin:/usr/bin" [ "/usr/bin/gopls"; "/usr/local/bin/other" ]

    Assert.Equal(Some "/usr/bin/gopls", Launch.resolve env "gopls")
    Assert.Equal(Some "/usr/local/bin/other", Launch.resolve env "other")

// ---- the plan ------------------------------------------------------------------------------------

[<Fact>]
let ``an exe is started directly with its arguments one by one`` () =
    let env = win [ @"C:\tools\gopls.exe" ]

    match Launch.plan env "gopls" [ "serve"; "-rpc.trace"; "a b" ] with
    | Ok(Direct(file, args)) ->
        Assert.Equal(@"C:\tools\gopls.exe", file)
        Assert.Equal<string list>([ "serve"; "-rpc.trace"; "a b" ], args)
    | other -> failwithf "expected a direct start, got %A" other

[<Fact>]
let ``a shim is started through cmd with /d /v:off /s /c and every part quoted`` () =
    let env = win [ @"C:\Users\me\AppData\Roaming\npm\typescript-language-server.cmd" ]

    match Launch.plan env "typescript-language-server" [ "--stdio"; "a b" ] with
    | Ok(ViaCmd(cmd, line)) ->
        Assert.Equal(@"C:\Windows\System32\cmd.exe", cmd)

        Assert.Equal(
            "/d /v:off /s /c \"\"C:\\Users\\me\\AppData\\Roaming\\npm\\typescript-language-server.cmd\" \"--stdio\" \"a b\"\"",
            line
        )
    | other -> failwithf "expected a start through cmd, got %A" other

[<Fact>]
let ``a .bat is run the same way and an unresolved name with a script extension too`` () =
    match Launch.plan (win []) @"C:\x\run.bat" [ "1" ] with
    | Ok(ViaCmd(_, line)) -> Assert.Equal("/d /v:off /s /c \"\"C:\\x\\run.bat\" \"1\"\"", line)
    | other -> failwithf "%A" other

[<Fact>]
let ``on a system without cmd a .cmd file is just a file`` () =
    match Launch.plan (machine false "/bin" [ "/bin/x.cmd" ]) "/bin/x.cmd" [ "a&b" ] with
    | Ok(Direct("/bin/x.cmd", [ "a&b" ])) -> ()
    | other -> failwithf "%A" other

// ---- hostile arguments ---------------------------------------------------------------------------

let private shim = @"C:\npm\ls.cmd"

let private lineOf (args: string list) =
    match Launch.plan (machine true @"C:\npm" [ shim ]) "ls" args with
    | Ok(ViaCmd(_, line)) -> Ok line
    | Ok other -> Error(sprintf "not via cmd: %A" other)
    | Error e -> Error e

/// A model of what cmd.exe does with the text after `/s /c "…"`: the outer quotes come off, then a quote toggles
/// quoting, and outside quotes `& | < > ^ ( ) %` and a line break are operators. Returns the characters that would act
/// as an operator.
let private operatorsOutsideQuotes (line: string) =
    let body = line.Substring("/d /v:off /s /c ".Length)
    let inner = body.Substring(1, body.Length - 2)
    let mutable inQuote = false
    let found = Collections.Generic.List<char>()

    for c in inner do
        if c = '"' then
            inQuote <- not inQuote
        elif not inQuote && "&|<>^()%\r\n".IndexOf c >= 0 then
            found.Add c

    List.ofSeq found, inQuote

[<Theory>]
[<InlineData("a&b")>]
[<InlineData("& calc.exe")>]
[<InlineData("a|b")>]
[<InlineData("a^b")>]
[<InlineData("^")>]
[<InlineData("a<b>c")>]
[<InlineData("(x) (y)")>]
[<InlineData("a && b || c")>]
[<InlineData("--flag=a&b")>]
[<InlineData("!VAR!")>]
[<InlineData("a;b,c=d")>]
[<InlineData("日本語 ünï")>]
[<InlineData("")>]
let ``arguments with cmd's operators stay inside their quotes`` (hostile: string) =
    match lineOf [ "--stdio"; hostile; "tail" ] with
    | Ok line ->
        let ops, open' = operatorsOutsideQuotes line
        Assert.Empty ops
        Assert.False open'
        Assert.Contains("\"" + hostile + "\"", line)
    | Error e -> failwithf "%s should be quoted, not refused: %s" hostile e

[<Theory>]
[<InlineData("a\"b")>]
[<InlineData("\" & calc.exe & \"")>]
[<InlineData("%PATH%")>]
[<InlineData("100%")>]
[<InlineData("%~dp0")>]
[<InlineData("a\nb")>]
[<InlineData("a\r\nb")>]
[<InlineData("a\000b")>]
let ``arguments that quotes cannot hold are refused, not escaped`` (hostile: string) =
    match lineOf [ "--stdio"; hostile ] with
    | Error e ->
        Assert.Contains("argument 2", e)
        Assert.Contains("cmd.exe", e)
        // the message names the problem, not the value: a value with a line break must not break the message
        Assert.DoesNotContain(hostile, e)
    | Ok line -> failwithf "%A should have been refused, got %s" hostile line

[<Fact>]
let ``backslashes before the closing quote are doubled so the program behind the script reads them as they are`` () =
    match lineOf [ @"C:\dir\"; @"C:\dir\\"; @"a\b" ] with
    | Ok line ->
        Assert.Equal("/d /v:off /s /c \"\"C:\\npm\\ls.cmd\" \"C:\\dir\\\\\" \"C:\\dir\\\\\\\\\" \"a\\b\"\"", line)
    | Error e -> failwith e

[<Fact>]
let ``a script path with an operator in it is quoted and one with a percent sign is refused`` () =
    match Launch.plan (win [ @"C:\a&b\x.cmd" ]) @"C:\a&b\x.cmd" [] with
    | Ok(ViaCmd(_, line)) ->
        Assert.Equal("/d /v:off /s /c \"\"C:\\a&b\\x.cmd\"\"", line)
        Assert.Empty(fst (operatorsOutsideQuotes line))
    | other -> failwithf "%A" other

    match Launch.plan (win [ @"C:\100%\x.cmd" ]) @"C:\100%\x.cmd" [] with
    | Error e -> Assert.Contains("script's path", e)
    | other -> failwithf "%A" other

[<Fact>]
let ``no mix of the awkward characters gets an operator out of the quotes`` () =
    let alphabet =
        [| 'a'
           ' '
           '&'
           '|'
           '^'
           '<'
           '>'
           '('
           ')'
           '\\'
           '!'
           ','
           ';'
           '='
           '"'
           '%'
           '\n' |]

    let rng = Random 20241118

    for _ in 1..2000 do
        let arg =
            String(Array.init (rng.Next(0, 12)) (fun _ -> alphabet[rng.Next alphabet.Length]))

        match lineOf [ arg ] with
        | Ok line ->
            let ops, open' = operatorsOutsideQuotes line
            Assert.True(ops.IsEmpty && not open', sprintf "argument %A gave %s" arg line)
        | Error _ -> Assert.True(arg.IndexOfAny [| '"'; '%'; '\n' |] >= 0, sprintf "argument %A refused" arg)

// ---- through a real process ----------------------------------------------------------------------

let private tempDir () =
    let d =
        Path.Combine(Path.GetTempPath(), "fsh-launch-" + Guid.NewGuid().ToString("N").Substring(0, 8))

    Directory.CreateDirectory d |> ignore
    d

/// A script that prints each of its first four arguments in quotes, one per line, as the shims npm writes pass them on.
let private echoShim (dir: string) (name: string) =
    let path = Path.Combine(dir, name + ".cmd")

    File.WriteAllText(
        path,
        "@echo off\r\necho START\r\necho \"%~1\"\r\necho \"%~2\"\r\necho \"%~3\"\r\necho \"%~4\"\r\necho END\r\n"
    )

    path

/// These need cmd.exe; on Linux and macOS (no such thing as a `.cmd` shim) they have nothing to start, so the plan's
/// tests above carry that part and these return at once.
let private onWindows (body: unit -> unit) =
    if OperatingSystem.IsWindows() then
        body ()

[<Fact>]
let ``(Windows) a real .cmd that echoes its arguments gets them back unchanged, found by its bare name`` () =
    onWindows (fun () ->
        let dir = tempDir ()

        try
            let name = "fsh-echo-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            echoShim dir name |> ignore
            let before = Environment.GetEnvironmentVariable "PATH"
            Environment.SetEnvironmentVariable("PATH", dir + ";" + before)

            try
                let hostile =
                    [ "hello world"; "a&b|c^d"; "x<y>z (p) & echo INJECTED"; "--flag=!x!" ]

                match Proc.run (name :: hostile) dir None (TimeSpan.FromSeconds 20.0) with
                | Error e -> failwith e
                | Ok r ->
                    Assert.Equal(Some 0, r.ExitCode)
                    let lines = r.Stdout.Split('\n') |> Array.map (fun l -> l.TrimEnd '\r')

                    Assert.Equal<string[]>(
                        [| "START"
                           "\"hello world\""
                           "\"a&b|c^d\""
                           "\"x<y>z (p) & echo INJECTED\""
                           "\"--flag=!x!\""
                           "END"
                           "" |],
                        lines
                    )

            finally
                Environment.SetEnvironmentVariable("PATH", before)
        finally
            try
                Directory.Delete(dir, true)
            with _ ->
                ())

[<Fact>]
let ``(Windows) a .bat given by path runs, and a hostile argument stops the start instead of running`` () =
    onWindows (fun () ->
        let dir = tempDir ()

        try
            let path = Path.Combine(dir, "run.bat")
            File.WriteAllText(path, "@echo off\r\necho \"%~1\"\r\n")

            match Proc.run [ path; "one two" ] dir None (TimeSpan.FromSeconds 20.0) with
            | Ok r -> Assert.Equal("\"one two\"", r.Stdout.Trim())
            | Error e -> failwith e

            let marker = Path.Combine(dir, "pwned.txt")

            for hostile in [ "x\" & echo pwned > \"" + marker; "%COMSPEC%"; "a\nb" ] do
                match Proc.run [ path; hostile ] dir None (TimeSpan.FromSeconds 20.0) with
                | Error e -> Assert.Contains("cmd.exe", e)
                | Ok _ -> failwithf "%A should not start" hostile

            Assert.False(File.Exists marker)
        finally
            try
                Directory.Delete(dir, true)
            with _ ->
                ())

[<Fact>]
let ``(Windows) a language server installed as a .cmd shim starts and answers`` () =
    onWindows (fun () ->
        let py =
            [ "python"; "python3" ]
            |> List.tryFind (fun p ->
                match Proc.run [ p; "-c"; "print(1)" ] (Path.GetTempPath()) None (TimeSpan.FromSeconds 10.0) with
                | Ok r -> r.ExitCode = Some 0
                | _ -> false)

        match py with
        | None -> () // no Python here, so the scripted server cannot run (as in the rename tests)
        | Some python ->
            let dir = tempDir ()

            try
                let fake = Path.Combine(AppContext.BaseDirectory, "fake_lsp.py")
                let shimPath = Path.Combine(dir, "fsh-fake-ls.cmd")
                File.WriteAllText(shimPath, sprintf "@echo off\r\n%s \"%s\" %%*\r\n" python fake)
                let before = Environment.GetEnvironmentVariable "PATH"
                Environment.SetEnvironmentVariable("PATH", dir + ";" + before)

                try
                    let spec: ServerSpec =
                        { Command = "fsh-fake-ls"
                          Args = []
                          Install = "" }

                    match Lsp.start spec (fun _ _ -> ()) with
                    | Error e -> failwith e
                    | Ok session ->
                        try
                            Assert.True((session.Client.Initialize dir).Result)
                        finally
                            session.Stop()
                finally
                    Environment.SetEnvironmentVariable("PATH", before)
            finally
                try
                    Directory.Delete(dir, true)
                with _ ->
                    ())

[<Fact>]
let ``a server that cannot be passed its arguments safely is an error that says how to get it`` () =
    // the refusal only applies where a .cmd shim is started; elsewhere the same arguments are passed as they are
    let spec: ServerSpec =
        { Command = "fsh-no-such-server-" + Guid.NewGuid().ToString "N"
          Args = [ "100%" ]
          Install = "https://example.invalid/ls" }

    match Lsp.start spec (fun _ _ -> ()) with
    | Ok _ -> failwith "should not start"
    | Error e -> Assert.Contains("https://example.invalid/ls", e)

[<Fact>]
let ``on every system a program on the PATH is found by its bare name and gets awkward arguments as they are`` () =
    // Linux and macOS: a script on the PATH, started by its bare name with arguments a shell would take apart
    if not (OperatingSystem.IsWindows()) then
        let dir = tempDir ()

        try
            let name = "fsh-echo-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let path = Path.Combine(dir, name)
            File.WriteAllText(path, "#!/bin/sh\nfor a in \"$@\"; do printf '[%s]\\n' \"$a\"; done\n")
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            let before = Environment.GetEnvironmentVariable "PATH"
            Environment.SetEnvironmentVariable("PATH", dir + ":" + before)

            try
                let args = [ "a&b|c^d"; "x\"y"; "100%"; "line1\nline2"; "(z)" ]

                match Proc.run (name :: args) dir None (TimeSpan.FromSeconds 20.0) with
                | Error e -> failwith e
                | Ok r ->
                    Assert.Equal(Some 0, r.ExitCode)
                    Assert.Equal("[a&b|c^d]\n[x\"y]\n[100%]\n[line1\nline2]\n[(z)]\n", r.Stdout)
            finally
                Environment.SetEnvironmentVariable("PATH", before)
        finally
            try
                Directory.Delete(dir, true)
            with _ ->
                ()
