module FsLib.Core

let private bump (n: int) = n + 1

let doubleThenBump (n: int) = bump (n + n)

type Greeter =
    static member Greet (name: string) = "hi " + name
