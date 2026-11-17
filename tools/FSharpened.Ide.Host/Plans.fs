namespace FSharpened.Ide.Host

open System
open System.IO
open System.Text.RegularExpressions

/// Where a plan runs and what it is about.
type PlanContext =
    {
        /// The opened folder (the working directory of every step).
        Root: string
        /// The file to run, relative to the root, forward slashes.
        Entry: string
        /// A fresh folder for build output.
        OutDir: string
        /// Compiler flags the person chose, already validated.
        Flags: string list
        /// The suffix of an executable on this system (".exe" or "").
        ExeSuffix: string
    }

module Plans =
    let private flagRe = Regex("^[-A-Za-z0-9_=+.,:/]{1,80}$", RegexOptions.Compiled)

    /// Flags are passed as separate arguments, never through a shell; this only refuses obvious nonsense.
    let parseFlags (text: string) : Result<string list, string> =
        let parts =
            text.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        match parts |> List.tryFind (fun p -> not (flagRe.IsMatch p)) with
        | Some bad -> Error(sprintf "the flag “%s” is not accepted (letters, digits and - _ = + . , : / only)" bad)
        | None when parts.Length > 24 -> Error "too many flags"
        | None -> Ok parts

    let private matches (root: string) (pattern: string) =
        try
            Directory.EnumerateFiles(root, pattern) |> Seq.isEmpty |> not
        with _ ->
            false

    /// The first plan whose conditions hold: its file patterns all match in the folder, and the entry file has one of
    /// its extensions (when it names any).
    let choose (root: string) (entry: string) (plans: Plan list) : Plan option =
        let ext = Path.GetExtension(entry).ToLowerInvariant()

        plans
        |> List.tryFind (fun p ->
            p.Files |> List.forall (matches root)
            && (p.EntryExtensions.IsEmpty || List.contains ext p.EntryExtensions))

    let outPath (ctx: PlanContext) =
        Path.Combine(ctx.OutDir, "fsh-out" + ctx.ExeSuffix)

    let private expandOne (ctx: PlanContext) (a: string) =
        a
            .Replace("{entry}", ctx.Entry)
            .Replace("{out}", outPath ctx)
            .Replace("{dir}", ctx.OutDir)
            .Replace("{root}", ctx.Root)
            .Replace("{name}", Path.GetFileNameWithoutExtension ctx.Entry)

    /// An argument that is exactly `{flags}` becomes the chosen flags (zero or more arguments).
    let expandArgs (ctx: PlanContext) (args: string list) : string list =
        args
        |> List.collect (fun a -> if a = "{flags}" then ctx.Flags else [ expandOne ctx a ])
