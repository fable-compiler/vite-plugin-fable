module Components.Component

open Fable.Core
open React
open type React.DSL.DOMProps
open type React.React

JsInterop.importSideEffects "./app.css"

[<ExportDefault>]
let Component () : JSX.Element =
    let count, setCount = useStateByFunction (0)

    fragment [] [
        h1 [] [ str "React rocks!" ]
        button [ OnClick (fun _ -> setCount ((+) 1)) ] [ str $"Current state {count}" ]
    ]
