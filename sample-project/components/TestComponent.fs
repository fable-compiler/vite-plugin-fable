module Components.Test

open Feliz
open Fable.Core

// for editor highlight: alfonsogarciacaro.vscode-template-fsharp-highlight
[<Erase>]
let inline css s = s

[<ReactComponent>]
let El (props: {| name: string |}) =
    let spinCss = css "@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }"
    React.fragment [
        Html.div [
            prop.style [
                style.backgroundColor "yellow"
                style.animationName "spin"
                style.animationDuration 3
                style.animationIterationCount.initial
                style.animationTimingFunction.linear
            ]
            prop.children [
                Html.h1 $"My name is: {props.name}!"
            ]
        ]
        Html.style spinCss 
    ]
