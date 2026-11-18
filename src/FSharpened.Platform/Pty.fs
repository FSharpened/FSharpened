namespace FSharpened.Platform

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Microsoft.Win32.SafeHandles

/// What to start on a pseudo-terminal. `Argv` is the program and its arguments, never a shell command line (nothing is
/// parsed, so nothing can be injected). `Env` is added to the environment of this process; a null value removes the
/// variable from what the program sees.
type PtySpec =
    { Argv: string list
      Cwd: string
      Env: (string * string) list
      Cols: int
      Rows: int }

/// A program running on a pseudo-terminal: bytes written here are what a person typed, and what the program prints
/// arrives through the `onData` callback given to `Pty.start`.
type IPty =
    inherit IDisposable
    abstract IsRunning: bool
    /// Sends keystrokes (already encoded) to the program.
    abstract Write: data: byte[] -> unit
    /// Tells the program the window changed size.
    abstract Resize: cols: int * rows: int -> unit
    /// Ends the program and the processes it started.
    abstract Kill: unit -> unit

// ---- Windows: ConPTY -------------------------------------------------------------------------------------------
module internal WinPty =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type StartupInfoEx =
        val mutable cb: int
        val mutable reserved: nativeint
        val mutable desktop: nativeint
        val mutable title: nativeint
        val mutable x: int
        val mutable y: int
        val mutable xSize: int
        val mutable ySize: int
        val mutable xChars: int
        val mutable yChars: int
        val mutable fill: int
        val mutable flags: int
        val mutable showWindow: int16
        val mutable cbReserved2: int16
        val mutable reserved2: nativeint
        val mutable stdIn: nativeint
        val mutable stdOut: nativeint
        val mutable stdErr: nativeint
        val mutable attributeList: nativeint

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ProcessInformation =
        val mutable procHandle: nativeint
        val mutable thread: nativeint
        val mutable processId: int
        val mutable threadId: int

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CreatePipe(nativeint& readPipe, nativeint& writePipe, nativeint attributes, uint32 size)

    [<DllImport("kernel32.dll")>]
    extern int CreatePseudoConsole(int size, nativeint input, nativeint output, uint32 flags, nativeint& console)

    [<DllImport("kernel32.dll")>]
    extern int ResizePseudoConsole(nativeint console, int size)

    [<DllImport("kernel32.dll")>]
    extern void ClosePseudoConsole(nativeint console)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool InitializeProcThreadAttributeList(nativeint list, int count, int flags, nativeint& size)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool UpdateProcThreadAttribute(
        nativeint list,
        uint32 flags,
        nativeint attribute,
        nativeint value,
        nativeint size,
        nativeint previous,
        nativeint returned
    )

    [<DllImport("kernel32.dll")>]
    extern void DeleteProcThreadAttributeList(nativeint list)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CreateProcessW(
        string application,
        StringBuilder commandLine,
        nativeint processAttributes,
        nativeint threadAttributes,
        bool inheritHandles,
        uint32 flags,
        nativeint environment,
        string directory,
        StartupInfoEx& startup,
        ProcessInformation& info
    )

    [<DllImport("kernel32.dll")>]
    extern bool SetConsoleCtrlHandler(nativeint handler, bool add)

    [<DllImport("kernel32.dll")>]
    extern uint32 WaitForSingleObject(nativeint handle, uint32 milliseconds)

    [<DllImport("kernel32.dll")>]
    extern bool GetExitCodeProcess(nativeint handle, uint32& code)

    [<DllImport("kernel32.dll")>]
    extern bool CloseHandle(nativeint handle)

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode)>]
    extern nativeint CreateJobObjectW(nativeint attributes, string name)

    [<DllImport("kernel32.dll")>]
    extern bool SetInformationJobObject(nativeint job, int infoClass, byte[] info, uint32 length)

    [<DllImport("kernel32.dll")>]
    extern bool AssignProcessToJobObject(nativeint job, nativeint procHandle)

    [<DllImport("kernel32.dll")>]
    extern bool TerminateJobObject(nativeint job, uint32 code)

    let private extendedStartupInfoPresent = 0x00080000u
    let private createUnicodeEnvironment = 0x00000400u
    let private pseudoConsoleAttribute = nativeint 0x00020016

    let private size (cols: int) (rows: int) = (rows <<< 16) ||| (cols &&& 0xffff)

    /// The environment block (UTF-16, sorted, double-terminated) for the child, or null when nothing is added.
    let private environmentBlock (extra: (string * string) list) : nativeint =
        if extra.IsEmpty then
            0n
        else
            let merged = SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)

            for e in
                Environment.GetEnvironmentVariables()
                |> Seq.cast<System.Collections.DictionaryEntry> do
                merged[string e.Key] <- string e.Value

            for (k, v) in extra do
                if isNull v then
                    merged.Remove k |> ignore
                else
                    merged[k] <- v

            let sb = StringBuilder()

            for kv in merged do
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\000') |> ignore

            sb.Append('\000') |> ignore
            Marshal.StringToHGlobalUni(sb.ToString())

    /// Quotes one argument the way `CommandLineToArgvW` reads it back.
    let quoteArg (a: string) : string =
        if a <> "" && a.IndexOfAny [| ' '; '\t'; '"'; '\n'; '\v' |] < 0 then
            a
        else
            let sb = StringBuilder("\"")
            let mutable slashes = 0

            for c in a do
                if c = '\\' then
                    slashes <- slashes + 1
                else
                    if c = '"' then
                        sb.Append('\\', slashes * 2 + 1) |> ignore
                    else
                        sb.Append('\\', slashes) |> ignore

                    sb.Append c |> ignore
                    slashes <- 0

            sb.Append('\\', slashes * 2).Append('"') |> ignore
            sb.ToString()

    type Session
        (
            console: nativeint,
            processHandle: nativeint,
            job: nativeint,
            input: FileStream,
            output: FileStream,
            onData: byte[] -> unit,
            onExit: int -> unit
        ) =
        let writeLock = obj ()
        let mutable running = true
        let mutable disposed = false

        let reader =
            Thread(
                (fun () ->
                    let buf = Array.zeroCreate<byte> 8192
                    let mutable fin = false

                    while not fin do
                        let n =
                            try
                                output.Read(buf, 0, buf.Length)
                            with _ ->
                                0

                        if n <= 0 then fin <- true else onData (Array.sub buf 0 n)),
                IsBackground = true,
                Name = "fsh-conpty-read"
            )

        let waiter =
            Thread(
                (fun () ->
                    WaitForSingleObject(processHandle, 0xFFFFFFFFu) |> ignore
                    let mutable code = 0u
                    GetExitCodeProcess(processHandle, &code) |> ignore
                    running <- false
                    // closing the console ends the output pipe once the reader has drained it
                    ClosePseudoConsole console
                    reader.Join 2000 |> ignore
                    CloseHandle processHandle |> ignore

                    try
                        input.Dispose()
                        output.Dispose()
                    with _ ->
                        ()

                    onExit (int code)),
                IsBackground = true,
                Name = "fsh-conpty-wait"
            )

        member _.Begin() =
            reader.Start()
            waiter.Start()

        interface IPty with
            member _.IsRunning = running

            member _.Write(data: byte[]) =
                if running then
                    try
                        lock writeLock (fun () ->
                            input.Write(data, 0, data.Length)
                            input.Flush())
                    with _ ->
                        ()

            member _.Resize(cols: int, rows: int) =
                if running then
                    ResizePseudoConsole(console, size (max 1 cols) (max 1 rows)) |> ignore

            member _.Kill() =
                if running && job <> 0n then
                    TerminateJobObject(job, 1u) |> ignore

            member this.Dispose() =
                if not disposed then
                    disposed <- true
                    (this :> IPty).Kill()

                    if job <> 0n then
                        CloseHandle job |> ignore

    let start (spec: PtySpec) (onData: byte[] -> unit) (onExit: int -> unit) : Result<IPty, string> =
        let mutable inRead = 0n
        let mutable inWrite = 0n
        let mutable outRead = 0n
        let mutable outWrite = 0n

        if
            not (CreatePipe(&inRead, &inWrite, 0n, 0u))
            || not (CreatePipe(&outRead, &outWrite, 0n, 0u))
        then
            Error(sprintf "could not create the pipes for a pseudo-terminal (error %d)" (Marshal.GetLastWin32Error()))
        else
            let mutable console = 0n

            let hr =
                CreatePseudoConsole(size spec.Cols spec.Rows, inRead, outWrite, 0u, &console)

            // the console holds its own copies of these two ends
            CloseHandle inRead |> ignore
            CloseHandle outWrite |> ignore

            if hr <> 0 then
                CloseHandle inWrite |> ignore
                CloseHandle outRead |> ignore
                Error(sprintf "this Windows has no pseudo-console (CreatePseudoConsole returned 0x%08x)" hr)
            else
                let mutable listSize = 0n
                InitializeProcThreadAttributeList(0n, 1, 0, &listSize) |> ignore
                let list = Marshal.AllocHGlobal listSize

                let fail (msg: string) =
                    ClosePseudoConsole console
                    CloseHandle inWrite |> ignore
                    CloseHandle outRead |> ignore
                    Error msg

                if not (InitializeProcThreadAttributeList(list, 1, 0, &listSize)) then
                    Marshal.FreeHGlobal list
                    fail "could not prepare the process attributes"
                elif
                    not (
                        UpdateProcThreadAttribute(
                            list,
                            0u,
                            pseudoConsoleAttribute,
                            console,
                            nativeint IntPtr.Size,
                            0n,
                            0n
                        )
                    )
                then
                    DeleteProcThreadAttributeList list
                    Marshal.FreeHGlobal list
                    fail "could not attach the pseudo-console to the process"
                else
                    let mutable si = StartupInfoEx()
                    si.cb <- Marshal.SizeOf<StartupInfoEx>()
                    si.attributeList <- list
                    // without these the program inherits this process's own standard handles (a redirected output
                    // would then bypass the pseudo-console); explicit invalid handles make it use the console
                    si.flags <- 0x100 // STARTF_USESTDHANDLES
                    si.stdIn <- nativeint -1
                    si.stdOut <- nativeint -1
                    si.stdErr <- nativeint -1
                    let mutable pi = ProcessInformation()
                    let cmd = StringBuilder(spec.Argv |> List.map quoteArg |> String.concat " ")
                    let env = environmentBlock spec.Env
                    // a program started by something that ignores Ctrl+C (a build agent, a service) would pass the
                    // setting on to the shell, and Ctrl+C typed in the terminal would do nothing: restore the default
                    SetConsoleCtrlHandler(0n, false) |> ignore

                    let ok =
                        CreateProcessW(
                            null,
                            cmd,
                            0n,
                            0n,
                            false,
                            extendedStartupInfoPresent ||| createUnicodeEnvironment,
                            env,
                            (if String.IsNullOrEmpty spec.Cwd then null else spec.Cwd),
                            &si,
                            &pi
                        )

                    let lastError = Marshal.GetLastWin32Error()
                    DeleteProcThreadAttributeList list
                    Marshal.FreeHGlobal list

                    if env <> 0n then
                        Marshal.FreeHGlobal env

                    if not ok then
                        fail (sprintf "could not start “%s” (error %d)" (List.head spec.Argv) lastError)
                    else
                        CloseHandle pi.thread |> ignore

                        // every process the shell starts joins one job, so ending the terminal ends them all
                        let job = CreateJobObjectW(0n, null)

                        if job <> 0n then
                            let info = Array.zeroCreate<byte> 144
                            BitConverter.GetBytes(0x2000).CopyTo(info, 16) // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                            SetInformationJobObject(job, 9, info, uint32 info.Length) |> ignore
                            AssignProcessToJobObject(job, pi.procHandle) |> ignore

                        let input =
                            new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write, 1, false)

                        let output =
                            new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 1, false)

                        let s = new Session(console, pi.procHandle, job, input, output, onData, onExit)
                        s.Begin()
                        Ok(s :> IPty)

// ---- Linux and macOS: a pseudo-terminal master, and the program started on the slave ------------------------------
module internal PosixPty =
    let private installResolver () =
        let candidates =
            if RuntimeInformation.IsOSPlatform OSPlatform.OSX then
                [ "libSystem.dylib"; "/usr/lib/libSystem.B.dylib" ]
            else
                [ "libc.so.6"; "libc"; "libc.so" ]

        let load (name: string) =
            if name = "fsh_libc" then
                candidates
                |> List.tryPick (fun c ->
                    match NativeLibrary.TryLoad c with
                    | true, h -> Some h
                    | _ -> None)
                |> Option.defaultValue IntPtr.Zero
            else
                IntPtr.Zero

        NativeLibrary.SetDllImportResolver(typeof<IPty>.Assembly, DllImportResolver(fun name _ _ -> load name))

    let private resolver = lazy (installResolver ())

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern int posix_openpt(int flags)

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern int grantpt(int fd)

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern int unlockpt(int fd)

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern nativeint ptsname(int fd)

    [<DllImport("fsh_libc", SetLastError = true)>]
    // ioctl is variadic. Apple's arm64 ABI passes variadic arguments on the stack, not in registers, so a plain
    // three-argument call hands the kernel garbage there. Eight register slots are filled and the ninth (the first
    // stack slot) carries the same pointer, which reaches ioctl wherever each platform looks for it.
    extern int ioctl(
        int fd,
        uint64 request,
        byte[] a2,
        byte[] a3,
        byte[] a4,
        byte[] a5,
        byte[] a6,
        byte[] a7,
        byte[] onStack
    )

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern nativeint read(int fd, byte[] buffer, nativeint count)

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern nativeint write(int fd, byte[] buffer, nativeint count)

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern int poll(byte[] fds, uint64 count, int timeout)

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern int close(int fd)

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern int kill(int pid, int signal)

    [<DllImport("fsh_libc", SetLastError = true)>]
    extern int waitpid(int pid, int& status, int options)

    [<DllImport("fsh_libc")>]
    extern int posix_spawn(
        int& pid,
        string path,
        nativeint fileActions,
        nativeint attributes,
        nativeint[] argv,
        nativeint[] envp
    )

    [<DllImport("fsh_libc")>]
    extern int posix_spawn_file_actions_init(nativeint actions)

    [<DllImport("fsh_libc")>]
    extern int posix_spawn_file_actions_destroy(nativeint actions)

    [<DllImport("fsh_libc")>]
    extern int posix_spawn_file_actions_addopen(nativeint actions, int fd, string path, int flags, int mode)

    [<DllImport("fsh_libc")>]
    extern int posix_spawn_file_actions_adddup2(nativeint actions, int fd, int newFd)

    [<DllImport("fsh_libc")>]
    extern int posix_spawn_file_actions_addclose(nativeint actions, int fd)

    [<DllImport("fsh_libc")>]
    extern int posix_spawnattr_init(nativeint attributes)

    [<DllImport("fsh_libc")>]
    extern int posix_spawnattr_destroy(nativeint attributes)

    [<DllImport("fsh_libc")>]
    extern int posix_spawnattr_setflags(nativeint attributes, int16 flags)

    [<DllImport("fsh_libc")>]
    extern int posix_spawnattr_setsigdefault(nativeint attributes, nativeint set)

    [<DllImport("fsh_libc")>]
    extern int posix_spawnattr_setsigmask(nativeint attributes, nativeint set)

    [<DllImport("fsh_libc")>]
    extern int sigemptyset(nativeint set)

    [<DllImport("fsh_libc")>]
    extern int sigaddset(nativeint set, int signal)

    let private isMac = RuntimeInformation.IsOSPlatform OSPlatform.OSX
    let private oRdwr = 2
    let private oNoctty = if isMac then 0x20000 else 0x100
    // the request that sets the window size of a terminal: struct winsize { rows; cols; xpixel; ypixel } of four shorts
    let private tiocswinsz = if isMac then 0x80087467UL else 0x5414UL
    let private spawnSetSigdef = 0x04s
    let private spawnSetSigmask = 0x08s
    let private spawnSetSid = if isMac then 0x0400s else 0x80s

    let private winsize (cols: int) (rows: int) : byte[] =
        let b = Array.zeroCreate<byte> 8
        BitConverter.GetBytes(int16 rows).CopyTo(b, 0)
        BitConverter.GetBytes(int16 cols).CopyTo(b, 2)
        b

    let private setWindowSize (fd: int) (size: byte[]) =
        ioctl (fd, tiocswinsz, size, size, size, size, size, size, size) |> ignore

    let private utf8Z (s: string) : nativeint = Marshal.StringToCoTaskMemUTF8 s

    let private ptsLock = obj ()

    type Session(master: int, pid: int, onData: byte[] -> unit, onExit: int -> unit) =
        let writeLock = obj ()
        let mutable running = true
        let mutable stop = false
        let mutable disposed = false

        let reader =
            Thread(
                (fun () ->
                    let buf = Array.zeroCreate<byte> 8192
                    let pfd = Array.zeroCreate<byte> 8
                    BitConverter.GetBytes(master).CopyTo(pfd, 0)
                    BitConverter.GetBytes(1s).CopyTo(pfd, 4) // POLLIN
                    let mutable fin = false

                    while not fin && not stop do
                        pfd[6] <- 0uy
                        pfd[7] <- 0uy
                        let r = poll (pfd, 1UL, 100)

                        if r < 0 then
                            if Marshal.GetLastPInvokeError() <> 4 then // not EINTR
                                fin <- true
                        elif r > 0 then
                            let n = int (read (master, buf, nativeint buf.Length))

                            if n > 0 then
                                onData (Array.sub buf 0 n)
                            elif n = 0 || Marshal.GetLastPInvokeError() <> 4 then
                                fin <- true),
                IsBackground = true,
                Name = "fsh-pty-read"
            )

        let waiter =
            Thread(
                (fun () ->
                    let mutable status = 0
                    let mutable fin = false

                    while not fin do
                        let r = waitpid (pid, &status, 0)

                        if r = pid || (r < 0 && Marshal.GetLastPInvokeError() <> 4) then
                            fin <- true

                    running <- false
                    reader.Join 500 |> ignore
                    stop <- true
                    reader.Join 500 |> ignore
                    close master |> ignore

                    let code =
                        if status &&& 0x7f = 0 then
                            (status >>> 8) &&& 0xff
                        else
                            128 + (status &&& 0x7f)

                    onExit code),
                IsBackground = true,
                Name = "fsh-pty-wait"
            )

        member _.Begin() =
            reader.Start()
            waiter.Start()

        interface IPty with
            member _.IsRunning = running

            member _.Write(data: byte[]) =
                if running then
                    lock writeLock (fun () ->
                        let mutable off = 0
                        let mutable bad = false

                        while off < data.Length && not bad do
                            let chunk =
                                if off = 0 then
                                    data
                                else
                                    Array.sub data off (data.Length - off)

                            let n = int (write (master, chunk, nativeint chunk.Length))

                            if n > 0 then
                                off <- off + n
                            elif Marshal.GetLastPInvokeError() <> 4 then
                                bad <- true)

            member _.Resize(cols: int, rows: int) =
                if running then
                    setWindowSize master (winsize (max 1 cols) (max 1 rows))

            member _.Kill() =
                if running then
                    // the program leads its own session and group: hang up on all of it, then insist
                    kill (-pid, 1) |> ignore

                    Thread(
                        (fun () ->
                            Thread.Sleep 400

                            if running then
                                kill (-pid, 9) |> ignore),
                        IsBackground = true
                    )
                        .Start()

            member this.Dispose() =
                if not disposed then
                    disposed <- true
                    (this :> IPty).Kill()

    /// The program that starts the real one: it changes to the folder first, so the start itself needs no thread-wide
    /// `chdir`, then replaces itself with the program (searched on the PATH).
    let wrap (cwd: string) (argv: string list) : string list =
        [ "/bin/sh"
          "-c"
          "cd \"$0\" && exec \"$@\""
          (if String.IsNullOrEmpty cwd then "." else cwd) ]
        @ argv

    let start (spec: PtySpec) (onData: byte[] -> unit) (onExit: int -> unit) : Result<IPty, string> =
        resolver.Force()

        try
            let master = posix_openpt (oRdwr ||| oNoctty)

            if master < 0 then
                Error(sprintf "could not open a pseudo-terminal (error %d)" (Marshal.GetLastPInvokeError()))
            elif grantpt master <> 0 || unlockpt master <> 0 then
                close master |> ignore
                Error "could not unlock the pseudo-terminal"
            else
                let slave =
                    lock ptsLock (fun () ->
                        let p = ptsname master
                        if p = 0n then null else Marshal.PtrToStringUTF8 p)

                if isNull slave then
                    close master |> ignore
                    Error "could not find the pseudo-terminal's device"
                else
                    setWindowSize master (winsize spec.Cols spec.Rows)

                    let argv = wrap spec.Cwd spec.Argv |> List.map utf8Z |> Array.ofList
                    let argvz = Array.append argv [| 0n |]

                    let env =
                        let merged = Dictionary<string, string>(StringComparer.Ordinal)

                        for e in
                            Environment.GetEnvironmentVariables()
                            |> Seq.cast<System.Collections.DictionaryEntry> do
                            merged[string e.Key] <- string e.Value

                        for (k, v) in spec.Env do
                            if isNull v then
                                merged.Remove k |> ignore
                            else
                                merged[k] <- v

                        merged |> Seq.map (fun kv -> utf8Z (kv.Key + "=" + kv.Value)) |> Array.ofSeq

                    let envz = Array.append env [| 0n |]
                    let actions = Marshal.AllocHGlobal 512
                    let attrs = Marshal.AllocHGlobal 512
                    let sigs = Marshal.AllocHGlobal 512
                    let empty = Marshal.AllocHGlobal 512
                    posix_spawn_file_actions_init actions |> ignore
                    posix_spawnattr_init attrs |> ignore
                    sigemptyset sigs |> ignore
                    sigemptyset empty |> ignore

                    for s in [ 1; 2; 3; 13; 15 ] do // HUP INT QUIT PIPE TERM back to their defaults
                        sigaddset (sigs, s) |> ignore

                    posix_spawnattr_setsigdefault (attrs, sigs) |> ignore
                    posix_spawnattr_setsigmask (attrs, empty) |> ignore

                    posix_spawnattr_setflags (attrs, spawnSetSigdef ||| spawnSetSigmask ||| spawnSetSid)
                    |> ignore
                    // the new session has no terminal yet: opening the slave without O_NOCTTY makes it the controlling one
                    posix_spawn_file_actions_addopen (actions, 0, slave, oRdwr, 0) |> ignore
                    posix_spawn_file_actions_adddup2 (actions, 0, 1) |> ignore
                    posix_spawn_file_actions_adddup2 (actions, 0, 2) |> ignore
                    posix_spawn_file_actions_addclose (actions, master) |> ignore

                    let mutable pid = 0
                    let rc = posix_spawn (&pid, "/bin/sh", actions, attrs, argvz, envz)
                    posix_spawn_file_actions_destroy actions |> ignore
                    posix_spawnattr_destroy attrs |> ignore

                    for p in [ actions; attrs; sigs; empty ] do
                        Marshal.FreeHGlobal p

                    for p in Array.append argv env do
                        Marshal.FreeCoTaskMem p

                    if rc <> 0 then
                        close master |> ignore
                        Error(sprintf "could not start “%s” (error %d)" (List.head spec.Argv) rc)
                    else
                        let s = new Session(master, pid, onData, onExit)
                        s.Begin()
                        Ok(s :> IPty)
        with
        | :? DllNotFoundException as e -> Error("no C library to talk to: " + e.Message)
        | :? EntryPointNotFoundException as e -> Error("the C library lacks a function: " + e.Message)

/// Starts programs on a pseudo-terminal, whichever the system: ConPTY on Windows, a pty master on Linux and macOS.
module Pty =
    /// How an argument list becomes the one string `CreateProcess` takes (exposed for tests).
    let windowsCommandLine (argv: string list) : string =
        argv |> List.map WinPty.quoteArg |> String.concat " "

    /// What is really started on Linux and macOS to reach `argv` in `cwd` (exposed for tests).
    let posixWrap (cwd: string) (argv: string list) : string list = PosixPty.wrap cwd argv

    /// Starts `spec`. `onData` is called on a pool thread with each piece of output, `onExit` once with the exit code.
    /// An `Error` means no pseudo-terminal could be made, so the caller can fall back to plain pipes.
    let start (spec: PtySpec) (onData: byte[] -> unit) (onExit: int -> unit) : Result<IPty, string> =
        if spec.Argv.IsEmpty then
            Error "there is no program to start"
        else
            let os = (RuntimeIdentity.detect ()).Os

            let spec =
                { spec with
                    Cols = max 1 spec.Cols
                    Rows = max 1 spec.Rows
                    Env =
                        // Windows programs do not read TERM; the others need to be told what the terminal can draw
                        if os = Windows then
                            spec.Env
                        else
                            ("TERM", "xterm-256color")
                            :: ("COLORTERM", "truecolor")
                            :: (spec.Env |> List.filter (fun (k, _) -> k <> "TERM" && k <> "COLORTERM")) }

            match os with
            | Windows -> WinPty.start spec onData onExit
            | Linux
            | MacOs -> PosixPty.start spec onData onExit
            | _ -> Error "this system has no pseudo-terminal support"
