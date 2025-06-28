module Components.RootComponent

open Feliz
open Fable.Core

JsInterop.importSideEffects "./app.css"


[<ReactComponent>]
let El () =
    let count, setCount = React.useState 0
    React.fragment [
        Test.El({| name = "Test" |})
        Html.h1 "Vite fable plugin rocks!"
        Html.button [
            prop.onClick (fun _ -> setCount (count + 1))
            prop.text $"Current state {count}"
        ]
    ]
