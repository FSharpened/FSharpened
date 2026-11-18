module Sample

let greet () =
    printfn "hello"
    printfn "%d items" 3

#if WINDOWS
let sep = '\\'
#else
let sep = '/'
#endif

// printfn "ignored in a comment"
let quote = "a \"quoted\" word"
