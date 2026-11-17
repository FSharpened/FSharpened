namespace FSharpened.Interop.Rust

open System.Runtime.InteropServices

/// The status codes every FSharpened-conventional Rust export returns. Zero is success; the table is part of the ABI
/// and only grows (docs/packages/Interop.Rust.md).
type AbiStatus =
    | Success
    | NullPointer
    | InvalidUtf8
    | InvalidArgument
    | Panicked
    | Failed
    /// A code this table does not know: a newer library than the caller expects.
    | Unknown of int

type AbiError = { Status: AbiStatus; Message: string }

module AbiStatus =
    let ofCode (code: int) : AbiStatus =
        match code with
        | 0 -> Success
        | 1 -> NullPointer
        | 2 -> InvalidUtf8
        | 3 -> InvalidArgument
        | 4 -> Panicked
        | 5 -> Failed
        | other -> Unknown other

    let code (status: AbiStatus) : int =
        match status with
        | Success -> 0
        | NullPointer -> 1
        | InvalidUtf8 -> 2
        | InvalidArgument -> 3
        | Panicked -> 4
        | Failed -> 5
        | Unknown other -> other

    let name (status: AbiStatus) : string =
        match status with
        | Success -> "ok"
        | NullPointer -> "null pointer"
        | InvalidUtf8 -> "invalid UTF-8"
        | InvalidArgument -> "invalid argument"
        | Panicked -> "panic"
        | Failed -> "failed"
        | Unknown other -> "unknown status " + string other

/// UTF-8 strings across the boundary. A string the library returns is owned by the library and must be handed
/// back to its free function; a string the caller passes in is only borrowed for the duration of the call.
module Utf8 =
    /// Copies a NUL-terminated UTF-8 string out of native memory, then gives the pointer back to its owner.
    /// A null pointer is None and is never passed to the free function.
    let takeOwned (free: nativeint -> unit) (pointer: nativeint) : string option =
        if pointer = 0n then
            None
        else
            try
                Some(Marshal.PtrToStringUTF8 pointer)
            finally
                free pointer

    /// Lends a NUL-terminated UTF-8 copy of the string to the callback and frees it afterwards.
    let withBorrowed (text: string) (callback: nativeint -> 'a) : 'a =
        let pointer = Marshal.StringToCoTaskMemUTF8 text

        try
            callback pointer
        finally
            Marshal.FreeCoTaskMem pointer

module Abi =
    /// Turns a status code into a result. The message is only fetched when the call failed, so the happy path
    /// never touches the library's last-error slot.
    let check (lastError: unit -> string option) (code: int) : Result<unit, AbiError> =
        match AbiStatus.ofCode code with
        | Success -> Ok()
        | failure ->
            let message =
                match lastError () with
                | Some text -> text
                | None -> AbiStatus.name failure

            Error { Status = failure; Message = message }
