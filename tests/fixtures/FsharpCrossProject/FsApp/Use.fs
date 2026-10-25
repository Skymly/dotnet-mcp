module FsApp.Use

let doubled (n: int) = FsLib.Core.doubleThenBump n

let viaParam (f: int -> int) (n: int) = FsLib.Core.doubleThenBump (f n)

let greeting (name: string) = FsLib.Core.Greeter.Greet name

type Coin (r: float) =
    interface FsLib.Core.IShape with
        member _.Area = System.Math.PI * r * r

let own (n: int) = n * 3
