module Library.Tests.Index

open Library
open Fable.Pyxpecto

describe("index", fun () -> 
    it ("add", fun () ->
        let actual = Library.add(1,1)
        let expected = 2
        Expect.equal actual expected "1 + 1 = 2"
    )
)

