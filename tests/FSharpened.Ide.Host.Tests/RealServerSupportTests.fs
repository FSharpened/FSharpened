/// Tests of the pieces the opt-in real-server checks stand on, and of the bugs those checks found. These run in the
/// normal test run; the checks against real servers themselves are in RealServers.fs and are skipped unless asked for.
module RealServerSupportTests

open System
open System.IO
open Xunit
open FSharpened.Ide.Host

[<Fact>]
let ``a lower-case Windows drive letter from a server is made upper case`` () =
    Assert.Equal(@"C:\work\a.go", LspWire.normalizeDrive @"c:\work\a.go")
    Assert.Equal("C:/work/a.go", LspWire.normalizeDrive "c:/work/a.go")
    Assert.Equal(@"C:\work", LspWire.normalizeDrive @"C:\work")
    // not a drive: a relative name, a POSIX path that happens to contain a colon, a single letter
    Assert.Equal("c.txt", LspWire.normalizeDrive "c.txt")
    Assert.Equal("/c:/work", LspWire.normalizeDrive "/c:/work")
    Assert.Equal("c", LspWire.normalizeDrive "c")

[<Fact>]
let ``a file URI with a lower-case drive compares equal to the path the editor opened`` () =
    if OperatingSystem.IsWindows() then
        // rust-analyzer writes file:///c:/... for a file the editor opened as C:\...
        Assert.Equal(@"C:\work\a.rs", LspWire.ofUri "file:///c:/work/a.rs")
        Assert.Equal(@"C:\work\a.rs", LspWire.ofUri "file:///c%3A/work/a.rs")
        Assert.Equal(@"C:\work\a.rs", LspWire.ofUri "file:///C:/work/a.rs")
    else
        Assert.Equal("/work/a.rs", LspWire.ofUri "file:///work/a.rs")

[<Fact>]
let ``a fixture position marks the cursor at the bar in the first match`` () =
    let text = "package main\n\nfunc main() {\n\tfmt.Println(Greet(\"x\"))\n}\n"

    let at line character =
        Some { Line = line; Character = character }

    Assert.Equal(at 3 9, RealServers.positionOf text "fmt.Prin|tln")
    Assert.Equal(at 3 12, RealServers.positionOf text "fmt.Println|(")
    Assert.Equal(at 0 0, RealServers.positionOf text "|package")
    Assert.Equal(None, RealServers.positionOf text "missing|")

[<Fact>]
let ``every fixture describes checks that point at real files and positions`` () =
    for dir in Directory.GetDirectories RealServers.fixturesDir do
        let json = File.ReadAllText(Path.Combine(dir, "fixture.json"))
        use doc = Text.Json.JsonDocument.Parse json
        let fx = doc.RootElement
        Assert.Equal(Path.GetFileName dir, fx.GetProperty("id").GetString())

        if fx.GetProperty("server").ValueKind = Text.Json.JsonValueKind.String then
            for f in fx.GetProperty("files").EnumerateArray() do
                Assert.True(File.Exists(Path.Combine(dir, f.GetString())), dir + ": " + f.GetString())

            for section in [ "completion"; "definition"; "hover" ] do
                let s = fx.GetProperty section
                let file = Path.Combine(dir, s.GetProperty("file").GetString())

                Assert.True(
                    (RealServers.positionOf (File.ReadAllText file) (s.GetProperty("at").GetString())).IsSome,
                    dir + ": " + section
                )

            Assert.True(File.Exists(Path.Combine(dir, fx.GetProperty("diagnostic").GetProperty("file").GetString())))

            // the server named by the fixture is the one the language pack starts
            let packFile =
                Path.Combine(RealServers.repoRoot, "languages", fx.GetProperty("pack").GetString() + ".json")

            match LanguagePack.parse packFile (File.ReadAllText packFile) with
            | Ok p -> Assert.Equal(fx.GetProperty("server").GetString(), p.Server.Value.Command)
            | Error e -> failwith e
