module FsApp.Use

let doubled (n: int) = FsLib.Core.doubleThenBump n

let greeting (name: string) = FsLib.Core.Greeter.Greet name

let own (n: int) = n * 3
