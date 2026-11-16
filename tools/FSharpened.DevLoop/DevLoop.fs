namespace FSharpened.DevLoop

/// How the hot-reload session applies an edit. See ADR-0008.
type ReloadMechanism =
    | HarmonyPatch
    | FcsHotReloadSession

module Pillar =
    [<Literal>]
    let Id = "DevLoop"

    [<Literal>]
    let Status = "Skeleton"
