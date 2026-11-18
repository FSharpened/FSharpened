/// Accessibility metadata: what a screen reader says about a control, and the help text behind it. Every control a
/// person can operate gets a name here (a test walks the window and fails on one without), and the landmarks and roles
/// Avalonia supports are set so a screen reader can jump between the explorer, the editor, the panels and the status bar.
namespace FSharpened.Ide.Desktop

open System
open System.Text
open Avalonia.Automation
open Avalonia.Automation.Peers
open Avalonia.Controls

module Access =
    /// A stable id for tests and tools: the name in lower case, letters and digits joined by dashes.
    let slug (name: string) =
        let sb = StringBuilder()
        let mutable dash = false

        for c in name do
            if Char.IsLetterOrDigit c then
                if dash && sb.Length > 0 then
                    sb.Append '-' |> ignore

                sb.Append(Char.ToLowerInvariant c) |> ignore
                dash <- false
            else
                dash <- true

        sb.ToString()

    /// "Run this file (Ctrl+F5)" is named "Run this file": the shortcut belongs to the help text, not the name.
    let plainName (text: string) =
        let t = text.Trim()
        let i = t.LastIndexOf " ("

        if i > 0 && t.EndsWith ")" then t.Substring(0, i) else t

    /// The name a menu item or button label gives: access-key underscores and the ellipsis are not part of it.
    let labelName (header: string) =
        header.Replace("_", "").Replace("…", "").Replace("...", "").Trim()

    let private asControl (c: 'T :> Control) : Control = c :> Control

    /// Names a control and says what it does. `help` may be empty.
    let describe (name: string) (help: string) (c: 'T :> Control) : 'T =
        AutomationProperties.SetName(asControl c, name)

        if help <> "" then
            AutomationProperties.SetHelpText(asControl c, help)

        AutomationProperties.SetAutomationId(asControl c, slug name)
        c

    /// Like `describe`, without an automation id: for items that repeat by name (menu entries).
    let nameOnly (name: string) (help: string) (c: 'T :> Control) : 'T =
        AutomationProperties.SetName(asControl c, name)

        if help <> "" then
            AutomationProperties.SetHelpText(asControl c, help)

        c

    /// The role a screen reader announces, when the control's own is not the right one (a bordered row used as a tab).
    let role (t: AutomationControlType) (c: 'T :> Control) : 'T =
        AutomationProperties.SetControlTypeOverride(asControl c, Nullable t)
        c

    /// Marks a region a screen reader can jump to.
    let landmark (t: AutomationLandmarkType) (c: 'T :> Control) : 'T =
        AutomationProperties.SetLandmarkType(asControl c, Nullable t)
        c

    /// Text that changes by itself (the status message) is announced politely.
    let live (c: 'T :> Control) : 'T =
        AutomationProperties.SetLiveSetting(asControl c, AutomationLiveSetting.Polite)
        c

    /// The shortcut a control answers to, for the screen reader's "keyboard shortcut" field.
    let shortcut (keys: string) (c: 'T :> Control) : 'T =
        if keys <> "" then
            AutomationProperties.SetAcceleratorKey(asControl c, keys)

        c

    /// Whether a name says something: not empty, and not only symbols (a glyph such as "✕" is not a name).
    let isMeaningful (name: string) =
        not (String.IsNullOrWhiteSpace name) && (name |> Seq.exists Char.IsLetter)

    /// A ring round a region while the focus came into it by key (Tab or the arrows), not by a click. The border holds
    /// a thickness of 0 otherwise, so the layout does not move.
    let focusRing (host: Border) =
        // the focus event comes from the control inside (the editor's text area, the terminal) and some of those
        // mark it handled, so the ring listens for handled events too
        host.AddHandler(
            Avalonia.Input.InputElement.GotFocusEvent,
            EventHandler<Avalonia.Input.FocusChangedEventArgs>(fun _ e ->
                if e.NavigationMethod <> Avalonia.Input.NavigationMethod.Pointer then
                    host.BorderThickness <- Avalonia.Thickness 2.0),
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            true
        )

        host.AddHandler(
            Avalonia.Input.InputElement.LostFocusEvent,
            EventHandler<Avalonia.Interactivity.RoutedEventArgs>(fun _ _ ->
                host.BorderThickness <- Avalonia.Thickness 0.0),
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            true
        )

        host
