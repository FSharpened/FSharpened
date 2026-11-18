/// Says hello.
let greet (name: string) = "hello " + name

let doubled = List.map (fun x -> x * 2) [ 1; 2; 3 ]

printfn "%s %A" (greet "world") doubled
