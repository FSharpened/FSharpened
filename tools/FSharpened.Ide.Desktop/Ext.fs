[<AutoOpen>]
module FSharpened.Ide.Desktop.Ext

open Avalonia.Controls

type ItemCollection with
    /// `Add` returns the new index; the window never needs it.
    member c.Push(x: obj) = c.Add x |> ignore
