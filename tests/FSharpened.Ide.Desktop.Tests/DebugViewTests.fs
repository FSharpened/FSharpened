module DebugViewTests

open System.IO
open Xunit
open FSharpened.Ide.Host
open FSharpened.Ide.Desktop

let private root = Path.Combine(Path.GetTempPath(), "fsh-proj")

let private frame (path: string) (line: int) (name: string) : DapFrame =
    { FrameId = 1
      FrameName = name
      FramePath = path
      FrameLine = line
      FrameColumn = 1 }

[<Fact>]
let ``the buttons follow the state of the session`` () =
    let open' = DebugView.Phase.Idle
    Assert.True(DebugView.canStart open')
    Assert.False(DebugView.canStep open')
    Assert.False(DebugView.canStop open')
    Assert.True(DebugView.canStart DebugView.Paused)
    Assert.True(DebugView.canStep DebugView.Paused)
    Assert.True(DebugView.canStop DebugView.Paused)
    Assert.False(DebugView.canStep DebugView.Running)
    Assert.True(DebugView.canPause DebugView.Running)
    Assert.False(DebugView.canPause DebugView.Paused)
    Assert.True(DebugView.canStop DebugView.Starting)
    Assert.False(DebugView.canStart DebugView.Running)

[<Fact>]
let ``a call stack row names the function and where it is, relative to the folder`` () =
    let inside = Path.Combine(root, "src", "util.rs")
    Assert.Equal("compute  src/util.rs:12", DebugView.frameLabel root (frame inside 12 "compute"))
    // a file outside the folder shows its name only; a frame without a source shows just the function
    let outside = Path.Combine(Path.GetTempPath(), "elsewhere", "lib.c")
    Assert.Equal("memcpy  lib.c:3", DebugView.frameLabel root (frame outside 3 "memcpy"))
    Assert.Equal("[Native Frame]", DebugView.frameLabel root (frame "" 0 "[Native Frame]"))

[<Fact>]
let ``only frames inside the folder can be shown in the editor`` () =
    Assert.Equal(Some "src/util.rs", DebugView.relOfFrame root (frame (Path.Combine(root, "src", "util.rs")) 1 "f"))
    Assert.Equal(None, DebugView.relOfFrame root (frame (Path.Combine(Path.GetTempPath(), "x.c")) 1 "f"))
    Assert.Equal(None, DebugView.relOfFrame root (frame "" 0 "f"))

[<Fact>]
let ``variables read as name = value with the type, and a long value is cut`` () =
    let v: DapVariable =
        { VarName = "x"
          Value = "1"
          VarType = "int"
          VarRef = 0 }

    Assert.Equal("x = 1   [int]", DebugView.variableLabel v)

    Assert.Equal(
        "y = 2",
        DebugView.variableLabel
            { v with
                VarName = "y"
                Value = "2"
                VarType = "" }
    )

    let long =
        DebugView.variableLabel
            { v with
                Value = String.replicate 500 "a\nb" }

    Assert.DoesNotContain("\n", long)
    Assert.True(long.Length < 220)

[<Fact>]
let ``the status says what the debugger is doing and why it stopped`` () =
    Assert.Equal("", DebugView.statusText DebugView.Idle "")
    Assert.Equal("Debugger running", DebugView.statusText DebugView.Running "")
    Assert.Equal("Paused: breakpoint", DebugView.statusText DebugView.Paused "breakpoint")
    Assert.Equal("breakpoint", DebugView.stopDetail "breakpoint" "")
    Assert.Equal("exception (Division by zero)", DebugView.stopDetail "exception" "Division by zero")
    Assert.Equal("", DebugView.stopDetail "" "")

[<Fact>]
let ``the console keeps its end when it grows and hides telemetry`` () =
    Assert.Equal("cdef", DebugView.capConsole 4 "abc" "def")
    Assert.Equal("abcdef", DebugView.capConsole 100 "abc" "def")
    Assert.False(DebugView.showsOutput "telemetry")
    Assert.True(DebugView.showsOutput "stdout")
    Assert.True(DebugView.showsOutput "console")
