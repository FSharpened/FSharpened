module FSharpened.Interop.Rust.Tests.InteropTests

// Calls the reference Rust library (native/rust/fsh-interop-example) from F#, through the pillar's own build,
// resolver and ABI helpers. The library is built once per test run with the real cargo.
//
// Without cargo these tests pass vacuously, because a contributor who never touches Rust must still be able to run
// the suite. Set FSH_REQUIRE_RUST=1 (the CI runner does, with --require-rust) to make a missing toolchain a failure.

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open Xunit
open FSharpened.Platform
open FSharpened.Interop.Rust

/// The extern block is the entire binding surface: plain DllImport, no reflection, no generated code (ADR-0019).
module private Native =
    [<Literal>]
    let Library = "fsh_interop_example"

    [<DllImport(Library)>]
    extern uint32 fsh_abi_version()

    [<DllImport(Library)>]
    extern int fsh_add(int a, int b)

    [<DllImport(Library)>]
    extern int fsh_sum(int[] values, unativeint length, int64& total)

    [<DllImport(Library)>]
    extern int fsh_divide(int a, int b, int& quotient)

    [<DllImport(Library)>]
    extern int fsh_greet(nativeint name, nativeint& greeting)

    [<DllImport(Library)>]
    extern int fsh_trigger_panic()

    [<DllImport(Library)>]
    extern nativeint fsh_last_error()

    [<DllImport(Library)>]
    extern void fsh_free_string(nativeint text)

let private freeString (p: nativeint) = Native.fsh_free_string p

let private lastError () =
    Utf8.takeOwned freeString (Native.fsh_last_error ())

let private requireRust =
    Environment.GetEnvironmentVariable "FSH_REQUIRE_RUST" = "1"

let rec private findRepoRoot (dir: DirectoryInfo) =
    if isNull dir then
        failwith "FSharpened.slnx not found above the test assembly"
    elif File.Exists(Path.Combine(dir.FullName, "FSharpened.slnx")) then
        dir.FullName
    else
        findRepoRoot dir.Parent

let private manifest =
    Path.Combine(
        findRepoRoot (DirectoryInfo AppContext.BaseDirectory),
        "native",
        "rust",
        "fsh-interop-example",
        "Cargo.toml"
    )

/// Builds the crate for this machine and installs the resolver for this assembly. Done once.
let private loaded: Lazy<Result<BuiltLibrary, string>> =
    lazy
        (let identity = RuntimeIdentity.detect ()

         let options =
             { Manifest = manifest
               Rid = identity.Rid
               Profile = Debug
               Locked = true
               TargetDir = Some(Path.Combine(AppContext.BaseDirectory, "rust-target")) }

         Cargo.build (Command.run 600000) File.Exists options None
         |> Result.bind (fun built ->
             match NativeResolver.planFor identity [ Path.GetDirectoryName built.Path ] built.Stem with
             | None -> Error("no native library plan for RID " + identity.Rid)
             | Some plan ->
                 NativeResolver.install (Assembly.GetExecutingAssembly()) (Map [ built.Stem, plan ]) true
                 Ok built))

/// Runs the body against the loaded library. No Rust installed: pass, unless FSH_REQUIRE_RUST=1.
let private withRust (body: BuiltLibrary -> unit) : unit =
    match Toolchain.probe (Command.run 60000) with
    | Error message ->
        if requireRust then
            failwith ("FSH_REQUIRE_RUST=1 but " + message)
    | Ok _ ->
        match loaded.Force() with
        | Ok built -> body built
        | Error message -> failwith message

[<Fact>]
let ``the example crate builds and its library is where cargo metadata says`` () =
    withRust (fun built ->
        Assert.True(File.Exists built.Path)
        Assert.Equal("fsh-interop-example", built.Package)
        Assert.Equal("fsh_interop_example", built.Stem)
        Assert.Equal(Native.Library, built.Stem)
        Assert.EndsWith(Path.GetFileName built.Path, built.Path))

[<Fact>]
let ``the library reports the ABI version the conventions document`` () =
    withRust (fun _ -> Assert.Equal(1u, Native.fsh_abi_version ()))

[<Fact>]
let ``a plain function crosses the boundary and wraps instead of trapping`` () =
    withRust (fun _ ->
        Assert.Equal(5, Native.fsh_add (2, 3))
        Assert.Equal(Int32.MinValue, Native.fsh_add (Int32.MaxValue, 1)))

[<Fact>]
let ``an array is read in place and the total comes back through an out parameter`` () =
    withRust (fun _ ->
        let mutable total = 0L
        let code = Native.fsh_sum ([| 1; 2; 3; Int32.MaxValue |], 4un, &total)
        Assert.Equal(Ok(), Abi.check lastError code)
        Assert.Equal(6L + int64 Int32.MaxValue, total)

        let code = Native.fsh_sum ([||], 0un, &total)
        Assert.Equal(Ok(), Abi.check lastError code)
        Assert.Equal(0L, total))

[<Fact>]
let ``a null pointer is a status and a message, not a crash`` () =
    withRust (fun _ ->
        let mutable total = 0L
        let code = Native.fsh_sum (Unchecked.defaultof<int[]>, 3un, &total)

        match Abi.check lastError code with
        | Error e ->
            Assert.Equal(NullPointer, e.Status)
            Assert.Equal("values must not be null", e.Message)
        | Ok _ -> failwith "expected a failure")

[<Fact>]
let ``division by zero is reported with the arguments in the message`` () =
    withRust (fun _ ->
        let mutable quotient = 0
        Assert.Equal(Ok(), Abi.check lastError (Native.fsh_divide (7, 2, &quotient)))
        Assert.Equal(3, quotient)

        match Abi.check lastError (Native.fsh_divide (1, 0, &quotient)) with
        | Error e ->
            Assert.Equal(InvalidArgument, e.Status)
            Assert.Contains("division by zero", e.Message)
            Assert.Contains("cannot divide 1 by 0", e.Message)
        | Ok _ -> failwith "expected a failure")

[<Fact>]
let ``the last error is cleared by the next successful call`` () =
    withRust (fun _ ->
        let mutable quotient = 0
        Native.fsh_divide (1, 0, &quotient) |> ignore
        Assert.True((lastError ()).IsSome)
        Native.fsh_divide (4, 2, &quotient) |> ignore
        Assert.Equal(None, lastError ()))

[<Fact>]
let ``strings cross as UTF-8 and the library's allocation is freed by the library`` () =
    withRust (fun _ ->
        let greeting =
            Utf8.withBorrowed "Ünïcode 🦀 日本語" (fun name ->
                let mutable greeting = 0n
                let code = Native.fsh_greet (name, &greeting)
                Assert.Equal(Ok(), Abi.check lastError code)
                Utf8.takeOwned freeString greeting)

        Assert.Equal(Some "Hello, Ünïcode 🦀 日本語!", greeting))

[<Fact>]
let ``a null name is rejected with a null pointer status`` () =
    withRust (fun _ ->
        let mutable greeting = 0n

        match Abi.check lastError (Native.fsh_greet (0n, &greeting)) with
        | Error e -> Assert.Equal(NullPointer, e.Status)
        | Ok _ -> failwith "expected a failure")

[<Fact>]
let ``invalid UTF-8 from the caller is rejected, not misread`` () =
    withRust (fun _ ->
        let bytes = [| 0xFFuy; 0xFEuy; 0uy |]
        let p = Marshal.AllocCoTaskMem bytes.Length

        try
            Marshal.Copy(bytes, 0, p, bytes.Length)
            let mutable greeting = 0n

            match Abi.check lastError (Native.fsh_greet (p, &greeting)) with
            | Error e -> Assert.Equal(InvalidUtf8, e.Status)
            | Ok _ -> failwith "expected a failure"
        finally
            Marshal.FreeCoTaskMem p)

[<Fact>]
let ``a Rust panic comes back as a status and this process keeps running`` () =
    withRust (fun _ ->
        match Abi.check lastError (Native.fsh_trigger_panic ()) with
        | Error e ->
            Assert.Equal(Panicked, e.Status)
            Assert.StartsWith("panic: deliberate panic", e.Message)
        | Ok _ -> failwith "expected a failure"

        // The runtime is still alive and the library still works after the panic.
        Assert.Equal(7, Native.fsh_add (3, 4)))

[<Fact>]
let ``calls from many threads each see their own last error`` () =
    withRust (fun _ ->
        let work (i: int) =
            let mutable quotient = 0
            let code = Native.fsh_divide (i, 0, &quotient)
            let message = lastError ()
            code, message

        let results = [| 1..16 |] |> Array.Parallel.map work

        for (code, message) in results do
            Assert.Equal(InvalidArgument, AbiStatus.ofCode code)
            Assert.Contains("division by zero", message.Value))
