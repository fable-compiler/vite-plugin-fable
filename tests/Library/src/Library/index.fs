namespace Library

open Fable.Core

[<Erase; Mangle(false)>]
type Library =
    static member add(x: int, y: int) = x + y

    static member substract(x: int, y: int) = x - y

