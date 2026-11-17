namespace FSharpened.Ide

/// Interop with real VS Code (ADR-0022, layer 3). The IDE cannot host VS Code extensions or GitHub Copilot, but a project
/// can be exported so that VS Code, vscode.dev or a Codespace opens it ready for them: the language extensions are
/// recommended, Copilot is recommended, and the editor settings carry over.
module VsCode =
    /// GitHub Copilot and Copilot Chat. They run in VS Code, not in this page, and need the person's own GitHub sign-in
    /// and Copilot entitlement.
    let copilot = [ "GitHub.copilot"; "GitHub.copilot-chat" ]

    /// The extension a language needs in VS Code for completion, diagnostics and debugging, by its Marketplace and Open
    /// VSX id. JavaScript and TypeScript are built into VS Code, so they have none.
    let extensionFor (lang: Language) : string option =
        match lang with
        | FSharp -> Some "Ionide.Ionide-fsharp"
        | CSharp -> Some "ms-dotnettools.csharp"
        | VisualBasic -> None
        | JavaScript
        | TypeScript -> None
        | Python -> Some "ms-python.python"
        | Rust -> Some "rust-lang.rust-analyzer"
        | Dart -> Some "Dart-Code.dart-code"
        | C
        | Cpp -> Some "ms-vscode.cpptools"
        | Go -> Some "golang.go"
        | Java -> Some "redhat.java"
        | Ruby -> Some "Shopify.ruby-lsp"
        | Lua -> Some "sumneko.lua"
        | Php -> Some "bmewburn.vscode-intelephense-client"
        | R -> Some "REditorSupport.r"
        | Haskell -> Some "haskell.haskell"
        | OCaml -> Some "ocamllabs.ocaml-platform"

    /// What `.vscode/extensions.json` recommends for a project: one extension per language in use, then Copilot.
    let recommendations (p: Project) : string list =
        let langs = Project.languages p |> List.choose extensionFor |> List.distinct

        langs @ copilot

    let private slug (name: string) =
        let s =
            name
            |> Seq.map (fun c ->
                if
                    (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c = '-'
                    || c = '_'
                then
                    c
                else
                    '-')
            |> Seq.toArray
            |> System.String

        let trimmed = s.Split('-') |> Array.filter (fun x -> x <> "") |> String.concat "-"
        if trimmed = "" then "project" else trimmed

    /// The name of the `.code-workspace` file for a project.
    let workspaceName (p: Project) = slug p.Name + ".code-workspace"

    /// The files that make a project open well in VS Code: `.vscode/extensions.json`, `.vscode/settings.json` and a
    /// `.code-workspace` file. A file the project already has is left alone and not listed.
    let files (p: Project) (s: EditorSettings) : (string * string) list =
        let recs = recommendations p |> List.map JStr
        let settings = Settings.toVsCode s

        [ ".vscode/extensions.json", Json.render (JObj [ "recommendations", JArr recs ])
          ".vscode/settings.json", Json.render (JObj settings)
          workspaceName p,
          Json.render (
              JObj
                  [ "folders", JArr [ JObj [ "path", JStr "." ] ]
                    "settings", JObj settings
                    "extensions", JObj [ "recommendations", JArr recs ] ]
          ) ]
        |> List.filter (fun (path, _) -> not (Project.exists path p))
