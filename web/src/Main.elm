module Main exposing (main)

import App exposing (Msg)
import Browser


{-| Elm entry point. The runtime calls `App.init` to obtain the initial
model and effect, then dispatches `requestProduct` to perform the effect.
-}
main : Program () App.Model Msg
main =
    Browser.element
        { init = \_ -> App.init () |> Tuple.mapSecond App.requestProduct
        , update = \msg model -> App.update msg model |> Tuple.mapSecond App.requestProduct
        , subscriptions = \_ -> Sub.none
        , view = App.view
        }
